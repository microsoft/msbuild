// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Shared;
using System.Text.RegularExpressions;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Internal
{
    internal static class EngineFileUtilities
    {
        // Regexes for wildcard filespecs that should not get expanded
        // By default all wildcards are expanded.
        private static List<Regex> s_lazyWildCardExpansionRegexes;

        static EngineFileUtilities()
        {
            if (Traits.Instance.UseLazyWildCardEvaluation)
            {
                CaptureLazyWildcardRegexes();
            }
        }

        // used by test to reset regexes
        internal static void CaptureLazyWildcardRegexes()
        {
            s_lazyWildCardExpansionRegexes = PopulateRegexFromEnvironment();
        }

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions. If path is invalid, just returns filespec verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <returns>Array of file paths, unescaped.</returns>
        internal static string[] GetFileListUnescaped
            (
            string directoryEscaped,
            string filespecEscaped
            )

        {
            return GetFileList(directoryEscaped, filespecEscaped, returnEscaped: false, forceEvaluateWildCards: false, excludeSpecsEscaped: null, fileMatcher: FileMatcher.Default);
        }

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions. If path is invalid, just returns filespec verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <param name="excludeSpecsEscaped">Filespecs to exclude, escaped.</param>
        /// <param name="forceEvaluate">Whether to force file glob expansion when eager expansion is turned off</param>
        /// <param name="fileMatcher"></param>
        /// <returns>Array of file paths, escaped.</returns>
        internal static string[] GetFileListEscaped
            (
            string directoryEscaped,
            string filespecEscaped,
            IEnumerable<string> excludeSpecsEscaped = null,
            bool forceEvaluate = false,
            FileMatcher fileMatcher = null
            )
        {
            return GetFileList(directoryEscaped, filespecEscaped, returnEscaped: true, forceEvaluate, excludeSpecsEscaped, fileMatcher ?? FileMatcher.Default);
        }

        internal static bool FilespecHasWildcards(string filespecEscaped)
        {
            if (!FileMatcher.HasWildcards(filespecEscaped))
            {
                return false;
            }

            // If the item's Include has both escaped wildcards and real wildcards, then it's
            // not clear what they are asking us to do.  Go to the file system and find
            // files that literally have '*' in their filename?  Well, that's not going to
            // happen because '*' is an illegal character to have in a filename.
            return !EscapingUtilities.ContainsEscapedWildcards(filespecEscaped);
        }

        /// <summary>
        /// Used for the purposes of evaluating an item specification. Given a filespec that may include wildcard characters * and
        /// ?, we translate it into an actual list of files. If the input filespec doesn't contain any wildcard characters, and it
        /// doesn't appear to point to an actual file on disk, then we just give back the input string as an array of length one,
        /// assuming that it wasn't really intended to be a filename (as items are not required to necessarily represent files).
        /// Any wildcards passed in that are unescaped will be treated as real wildcards.
        /// The "include" of items passed back from the filesystem will be returned canonically escaped.
        /// The ordering of the list returned is deterministic (it is sorted).
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="directoryEscaped">The directory to evaluate, escaped.</param>
        /// <param name="filespecEscaped">The filespec to evaluate, escaped.</param>
        /// <param name="returnEscaped"><code>true</code> to return escaped specs.</param>
        /// <param name="forceEvaluateWildCards">Whether to force file glob expansion when eager expansion is turned off</param>
        /// <param name="excludeSpecsEscaped">The exclude specification, escaped.</param>
        /// <param name="fileMatcher"></param>
        /// <returns>Array of file paths.</returns>
        private static string[] GetFileList
            (
            string directoryEscaped,
            string filespecEscaped,
            bool returnEscaped,
            bool forceEvaluateWildCards,
            IEnumerable<string> excludeSpecsEscaped,
            FileMatcher fileMatcher
            )
        {
            ErrorUtilities.VerifyThrowInternalLength(filespecEscaped, nameof(filespecEscaped));

            string[] fileList;

            if (!FilespecHasWildcards(filespecEscaped) ||
                FilespecMatchesLazyWildcard(filespecEscaped, forceEvaluateWildCards))
            {
                // Just return the original string.
                fileList = new string[] { returnEscaped ? filespecEscaped : EscapingUtilities.UnescapeAll(filespecEscaped) };
            }
            else
            {
                if (Traits.Instance.LogExpandedWildcards)
                {
                    ErrorUtilities.DebugTraceMessage("Expanding wildcard for file spec {0}", filespecEscaped);
                }

                // Unescape before handing it to the filesystem.
                var directoryUnescaped = EscapingUtilities.UnescapeAll(directoryEscaped);
                var filespecUnescaped = EscapingUtilities.UnescapeAll(filespecEscaped);
                var excludeSpecsUnescaped = excludeSpecsEscaped?.Where(IsValidExclude).Select(i => EscapingUtilities.UnescapeAll(i)).ToList();

                // Get the list of actual files which match the filespec.  Put
                // the list into a string array.  If the filespec started out
                // as a relative path, we will get back a bunch of relative paths.
                // If the filespec started out as an absolute path, we will get
                // back a bunch of absolute paths.
                fileList = fileMatcher.GetFiles(directoryUnescaped, filespecUnescaped, excludeSpecsUnescaped);

                ErrorUtilities.VerifyThrow(fileList != null, "We must have a list of files here, even if it's empty.");

                // Before actually returning the file list, we sort them alphabetically.  This
                // provides a certain amount of extra determinism and reproducability.  That is,
                // we're sure that the build will behave in exactly the same way every time,
                // and on every machine.
                Array.Sort(fileList, StringComparer.OrdinalIgnoreCase);

                if (returnEscaped)
                {
                    // We must now go back and make sure all special characters are escaped because we always
                    // store data in the engine in escaped form so it doesn't interfere with our parsing.
                    // Note that this means that characters that were not escaped in the original filespec
                    // may now be escaped, but that's not easy to avoid.
                    for (int i = 0; i < fileList.Length; i++)
                    {
                        fileList[i] = EscapingUtilities.Escape(fileList[i]);
                    }
                }
            }

            return fileList;
        }

        private static bool FilespecMatchesLazyWildcard(string filespecEscaped, bool forceEvaluateWildCards)
        {
            return Traits.Instance.UseLazyWildCardEvaluation && !forceEvaluateWildCards && MatchesLazyWildcard(filespecEscaped);
        }

        private static bool IsValidExclude(string exclude)
        {
            // TODO: assumption on legal path characters: https://github.com/Microsoft/msbuild/issues/781
            // Excludes that have both wildcards and non escaped wildcards will never be matched on Windows, because
            // wildcard characters are invalid in Windows paths.
            // Filtering these excludes early keeps the glob expander simpler. Otherwise unescaping logic would reach all the way down to
            // filespec parsing (parse escaped string (to correctly ignore escaped wildcards) and then
            // unescape the path fragments to unfold potentially escaped wildcard chars)
            var hasBothWildcardsAndEscapedWildcards = FileMatcher.HasWildcards(exclude) && EscapingUtilities.ContainsEscapedWildcards(exclude);
            return !hasBothWildcardsAndEscapedWildcards;
        }

        private static List<Regex> PopulateRegexFromEnvironment()
        {
            string wildCards = Environment.GetEnvironmentVariable("MsBuildSkipEagerWildCardEvaluationRegexes");
            if (string.IsNullOrEmpty(wildCards))
            {
                return new List<Regex>(0);
            }
            else
            {
                List<Regex> regexes = new List<Regex>();
                foreach (string regex in wildCards.Split(MSBuildConstants.SemicolonChar))
                {
                    Regex item = new Regex(regex, RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    // trigger a match first?
                    item.IsMatch("foo");
                    regexes.Add(item);
                }

                return regexes;
            }
        }

        // TODO: assumption on file system case sensitivity: https://github.com/Microsoft/msbuild/issues/781
        private static readonly Lazy<ConcurrentDictionary<string, bool>> _regexMatchCache = new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

        private static bool MatchesLazyWildcard(string fileSpec)
        {
            return _regexMatchCache.Value.GetOrAdd(fileSpec, file => s_lazyWildCardExpansionRegexes.Any(regex => regex.IsMatch(fileSpec)));
        }

        /// Returns a Func that will return true IFF its argument matches any of the specified filespecs
        /// Assumes filespec may be escaped, so it unescapes it
        /// The returned function makes no escaping assumptions or escaping operations. Its callers should control escaping.
        internal static Func<string, bool> GetFileSpecMatchTester(IList<string> filespecsEscaped, string currentDirectory)
        {
            var matchers = filespecsEscaped
                .Select(fs => new Lazy<FileSpecMatcherTester>(() => FileSpecMatcherTester.Parse(currentDirectory, fs)))
                .ToList();

            return file => matchers.Any(m => m.Value.IsMatch(file));
        }

        internal class IOCache
        {
            private readonly Lazy<ConcurrentDictionary<string, bool>> existenceCache = new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>(), true);

            public virtual bool DirectoryExists(string directory)
            {
                return existenceCache.Value.GetOrAdd(directory, Directory.Exists);
            }

            public virtual bool FileExists(string file)
            {
                return existenceCache.Value.GetOrAdd(file, File.Exists);
            }
        }
    }
}
