// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.BackEnd.Components.Logging;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Internal
{
    internal static class EngineFileUtilities
    {
        private const string DriveEnumeratingWildcardMessageResourceName = "WildcardResultsInDriveEnumeration";

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

        /// <summary>
        /// Test only: repopulate lazy wildcard regexes from the environment.
        /// </summary>
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
        /// <param name="loggingMechanism">Accepted loggers for drive enumeration: TargetLoggingContext, ILoggingService,
        /// and EvaluationLoggingContext.</param>
        /// <param name="excludeLocation">Location of Exclude element in file, used after drive enumeration detection.</param>
        /// <returns>Array of file paths, unescaped.</returns>
        internal static string[] GetFileListUnescaped(
            string directoryEscaped,
            string filespecEscaped,
            object loggingMechanism = null,
            IElementLocation excludeLocation = null)
        {
            return GetFileList(
                directoryEscaped,
                filespecEscaped,
                returnEscaped: false,
                forceEvaluateWildCards: false,
                excludeSpecsEscaped: null,
                fileMatcher: FileMatcher.Default,
                loggingMechanism: loggingMechanism,
                excludeLocation: excludeLocation);
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
        /// <param name="forceEvaluate">Whether to force file glob expansion when eager expansion is turned off.</param>
        /// <param name="fileMatcher">Class that contains functions for matching filenames with patterns.</param>
        /// <param name="loggingMechanism">Accepted loggers for drive enumeration: TargetLoggingContext, ILoggingService,
        /// and EvaluationLoggingContext.</param>
        /// <param name="includeLocation">Location of Include element in file, used after drive enumeration detection.</param>
        /// <param name="excludeLocation">Location of Exclude element in file, used after drive enumeration detection.</param>
        /// <param name="importLocation">Location of Import element in file, used after drive enumeration detection.</param>
        /// <param name="buildEventContext">Context to log a warning, used after drive enumeration detection.</param>
        /// <param name="buildEventFileInfoFullPath">Full path to project file to create BuildEventFileInfo,
        /// used after drive enumeration detection.</param>
        /// <param name="disableExcludeDriveEnumerationWarning">Flag used to detect when to properly log a warning
        /// for the Exclude attribute after detecting a drive enumerating wildcard.</param>
        /// <returns>Array of file paths, escaped.</returns>
        internal static string[] GetFileListEscaped(
            string directoryEscaped,
            string filespecEscaped,
            IEnumerable<string> excludeSpecsEscaped = null,
            bool forceEvaluate = false,
            FileMatcher fileMatcher = null,
            object loggingMechanism = null,
            IElementLocation includeLocation = null,
            IElementLocation excludeLocation = null,
            IElementLocation importLocation = null,
            BuildEventContext buildEventContext = null,
            string buildEventFileInfoFullPath = null,
            bool disableExcludeDriveEnumerationWarning = false)
        {
            return GetFileList(
                directoryEscaped,
                filespecEscaped,
                returnEscaped: true,
                forceEvaluate,
                excludeSpecsEscaped,
                fileMatcher ?? FileMatcher.Default,
                loggingMechanism: loggingMechanism,
                includeLocation: includeLocation,
                excludeLocation: excludeLocation,
                importLocation: importLocation,
                buildEventFileInfoFullPath: buildEventFileInfoFullPath,
                buildEventContext: buildEventContext,
                disableExcludeDriveEnumerationWarning: disableExcludeDriveEnumerationWarning);
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
        /// <param name="forceEvaluateWildCards">Whether to force file glob expansion when eager expansion is turned off.</param>
        /// <param name="excludeSpecsEscaped">The exclude specification, escaped.</param>
        /// <param name="fileMatcher">Class that contains functions for matching filenames with patterns.</param>
        /// <param name="loggingMechanism">Accepted loggers for drive enumeration: TargetLoggingContext, ILoggingService,
        /// and EvaluationLoggingContext.</param>
        /// <param name="includeLocation">Location of Include element in file, used after drive enumeration detection.</param>
        /// <param name="excludeLocation">Location of Exclude element in file, used after drive enumeration detection.</param>
        /// <param name="importLocation">Location of Import element in file, used after drive enumeration detection.</param>
        /// <param name="buildEventContext">Context to log a warning, used after drive enumeration detection.</param>
        /// <param name="buildEventFileInfoFullPath">Full path to project file to create BuildEventFileInfo,
        /// used after drive enumeration detection.</param>
        /// <param name="disableExcludeDriveEnumerationWarning">Flag used to detect when to properly log a warning
        /// for the Exclude attribute after detecting a drive enumerating wildcard.</param>
        /// <returns>Array of file paths.</returns>
        private static string[] GetFileList(
            string directoryEscaped,
            string filespecEscaped,
            bool returnEscaped,
            bool forceEvaluateWildCards,
            IEnumerable<string> excludeSpecsEscaped,
            FileMatcher fileMatcher,
            object loggingMechanism = null,
            IElementLocation includeLocation = null,
            IElementLocation excludeLocation = null,
            IElementLocation importLocation = null,
            BuildEventContext buildEventContext = null,
            string buildEventFileInfoFullPath = null,
            bool disableExcludeDriveEnumerationWarning = false)
        {
            ErrorUtilities.VerifyThrowInternalLength(filespecEscaped, nameof(filespecEscaped));

            string[] fileList = [];

            // Used to properly detect and log drive enumerating wildcards when applicable.
            string excludeFileSpec = string.Empty;

            var filespecHasNoWildCards = !FilespecHasWildcards(filespecEscaped);
            var filespecMatchesLazyWildcard = FilespecMatchesLazyWildcard(filespecEscaped, forceEvaluateWildCards);
            var excludeSpecsAreEmpty = excludeSpecsEscaped?.Any() != true;

            // Return original value if:
            //      FileSpec matches lazyloading regex or
            //      file has no wildcard and excludeSpecs are empty
            if (filespecMatchesLazyWildcard || (filespecHasNoWildCards && excludeSpecsAreEmpty))
            {
                // Just return the original string.
                fileList = [returnEscaped ? filespecEscaped : EscapingUtilities.UnescapeAll(filespecEscaped)];
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

                // Extract file spec information
                FileMatcher.Default.GetFileSpecInfo(filespecUnescaped, out string directoryPart, out string wildcardPart, out string filenamePart, out bool needsRecursion, out bool isLegalFileSpec);

                // Check if the file spec contains a drive-enumerating wildcard
                bool logDriveEnumeratingWildcard = FileMatcher.IsDriveEnumeratingWildcardPattern(directoryPart, wildcardPart);

                // Process exclude specs (if provided) and check if any of them contain a drive-enumerating wildcard
                if (excludeSpecsUnescaped != null)
                {
                    foreach (string excludeSpec in excludeSpecsUnescaped)
                    {
                        FileMatcher.Default.GetFileSpecInfo(excludeSpec, out directoryPart, out wildcardPart, out filenamePart, out needsRecursion, out isLegalFileSpec);
                        bool logDriveEnumeratingWildcardFromExludeSpec = FileMatcher.IsDriveEnumeratingWildcardPattern(directoryPart, wildcardPart);
                        if (logDriveEnumeratingWildcardFromExludeSpec)
                        {
                            excludeFileSpec = excludeSpec;
                        }

                        logDriveEnumeratingWildcard |= logDriveEnumeratingWildcardFromExludeSpec;
                    }
                }

                // Determines whether Exclude filespec or passed in file spec should be
                // used in drive enumeration warning or exception.
                bool excludeFileSpecIsEmpty = string.IsNullOrWhiteSpace(excludeFileSpec);
                string fileSpec = excludeFileSpecIsEmpty ? filespecUnescaped : excludeFileSpec;

                if (logDriveEnumeratingWildcard)
                {
                    switch (loggingMechanism)
                    {
                        // Logging mechanism received from ItemGroupIntrinsicTask.
                        case TargetLoggingContext targetLoggingContext:
                            LogDriveEnumerationWarningWithTargetLoggingContext(
                                targetLoggingContext,
                                includeLocation,
                                excludeLocation,
                                excludeFileSpecIsEmpty,
                                disableExcludeDriveEnumerationWarning,
                                fileSpec);

                            break;

                        // Logging mechanism received from Evaluator.
                        case ILoggingService loggingService:
                            LogDriveEnumerationWarningWithLoggingService(
                                loggingService,
                                includeLocation,
                                buildEventContext,
                                buildEventFileInfoFullPath,
                                filespecUnescaped);

                            break;

                        // Logging mechanism received from Evaluator and LazyItemEvaluator.IncludeOperation.
                        case EvaluationLoggingContext evaluationLoggingContext:
                            LogDriveEnumerationWarningWithEvaluationLoggingContext(
                                evaluationLoggingContext,
                                importLocation,
                                includeLocation,
                                excludeLocation,
                                excludeFileSpecIsEmpty,
                                filespecUnescaped,
                                fileSpec);

                            break;

                        default:
                            throw new InternalErrorException(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                "UnknownLoggingType",
                                loggingMechanism.GetType(),
                                nameof(GetFileList)));
                    }
                }

                if (logDriveEnumeratingWildcard && Traits.Instance.ThrowOnDriveEnumeratingWildcard)
                {
                    switch (loggingMechanism)
                    {
                        // Logging mechanism received from ItemGroupIntrinsicTask.
                        case TargetLoggingContext targetLoggingContext:
                            ThrowDriveEnumerationExceptionWithTargetLoggingContext(
                                includeLocation,
                                excludeLocation,
                                excludeFileSpecIsEmpty,
                                filespecUnescaped,
                                fileSpec);

                            break;

                        // Logging mechanism received from Evaluator.
                        case ILoggingService loggingService:
                            ThrowDriveEnumerationExceptionWithLoggingService(includeLocation, filespecUnescaped);

                            break;

                        // Logging mechanism received from Evaluator and LazyItemEvaluator.IncludeOperation.
                        case EvaluationLoggingContext evaluationLoggingContext:
                            ThrowDriveEnumerationExceptionWithEvaluationLoggingContext(
                                importLocation,
                                includeLocation,
                                excludeLocation,
                                filespecUnescaped,
                                fileSpec,
                                excludeFileSpecIsEmpty);

                            break;

                        default:
                            throw new InternalErrorException(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                "UnknownLoggingType",
                                loggingMechanism.GetType(),
                                nameof(GetFileList)));
                    }
                }
                else
                {
                    // Get the list of actual files which match the filespec.  Put
                    // the list into a string array.  If the filespec started out
                    // as a relative path, we will get back a bunch of relative paths.
                    // If the filespec started out as an absolute path, we will get
                    // back a bunch of absolute paths
                    (fileList, _, _, string globFailure) = fileMatcher.GetFiles(directoryUnescaped, filespecUnescaped, excludeSpecsUnescaped);

                    // log globing failure with the present logging mechanism
                    if (globFailure != null)
                    {
                        switch (loggingMechanism)
                        {
                            case TargetLoggingContext targetLoggingContext:
                                targetLoggingContext.LogCommentFromText(MessageImportance.Low, globFailure);
                                break;
                            case ILoggingService loggingService:
                                loggingService.LogCommentFromText(buildEventContext, MessageImportance.Low, globFailure);
                                break;
                            case EvaluationLoggingContext evaluationLoggingContext:
                                evaluationLoggingContext.LogCommentFromText(MessageImportance.Low, globFailure);
                                break;
                            default:
                                throw new InternalErrorException(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                    "UnknownLoggingType",
                                    loggingMechanism.GetType(),
                                    nameof(GetFileList)));
                        }
                    }


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
            }

            return fileList;
        }

        private static void LogDriveEnumerationWarningWithTargetLoggingContext(TargetLoggingContext targetLoggingContext, IElementLocation includeLocation, IElementLocation excludeLocation, bool excludeFileSpecIsEmpty, bool disableExcludeDriveEnumerationWarning, string fileSpec)
        {
            // Both condition lines are necessary to skip for the first GetFileListEscaped call
            // and reach for the GetFileListUnescaped call when the wildcarded Exclude attribute results
            // in a drive enumeration. Since we only want to check for the Exclude
            // attribute here, we want to ensure that includeLocation is null - otherwise,
            // Include wildcard attributes for the GetFileListEscaped calls would falsely appear
            // with the Exclude attribute in the logged warning.
            if (((!excludeFileSpecIsEmpty) && (!disableExcludeDriveEnumerationWarning)) ||
                ((includeLocation == null) && (excludeLocation != null)))
            {
                targetLoggingContext.LogWarning(
                        DriveEnumeratingWildcardMessageResourceName,
                        fileSpec,
                        XMakeAttributes.exclude,
                        XMakeElements.itemGroup,
                        excludeLocation.LocationString);
            }

            // Both conditions are necessary to reach for both GetFileListEscaped calls
            // and skip for the GetFileListUnescaped call when the wildcarded Include attribute
            // results in drive enumeration.
            else if (excludeFileSpecIsEmpty && (includeLocation != null))
            {
                targetLoggingContext.LogWarning(
                    DriveEnumeratingWildcardMessageResourceName,
                    fileSpec,
                    XMakeAttributes.include,
                    XMakeElements.itemGroup,
                    includeLocation.LocationString);
            }
        }

        private static void LogDriveEnumerationWarningWithLoggingService(ILoggingService loggingService, IElementLocation includeLocation, BuildEventContext buildEventContext, string buildEventFileInfoFullPath, string filespecUnescaped)
        {
            if (buildEventContext != null && includeLocation != null)
            {
                loggingService.LogWarning(
                    buildEventContext,
                    string.Empty,
                    new BuildEventFileInfo(buildEventFileInfoFullPath),
                    DriveEnumeratingWildcardMessageResourceName,
                    filespecUnescaped,
                    XMakeAttributes.include,
                    XMakeElements.itemGroup,
                    includeLocation.LocationString);
            }
        }

        private static void LogDriveEnumerationWarningWithEvaluationLoggingContext(EvaluationLoggingContext evaluationLoggingContext, IElementLocation importLocation, IElementLocation includeLocation, IElementLocation excludeLocation, bool excludeFileSpecIsEmpty, string filespecUnescaped, string fileSpec)
        {
            if (importLocation != null)
            {
                evaluationLoggingContext.LogWarning(
                    DriveEnumeratingWildcardMessageResourceName,
                    filespecUnescaped,
                    XMakeAttributes.project,
                    XMakeElements.import,
                    importLocation.LocationString);
            }
            else if (excludeFileSpecIsEmpty && includeLocation != null)
            {
                evaluationLoggingContext.LogWarning(
                    DriveEnumeratingWildcardMessageResourceName,
                    fileSpec,
                    XMakeAttributes.include,
                    XMakeElements.itemGroup,
                    includeLocation.LocationString);
            }
            else if (excludeLocation != null)
            {
                evaluationLoggingContext.LogWarning(
                    DriveEnumeratingWildcardMessageResourceName,
                    fileSpec,
                    XMakeAttributes.exclude,
                    XMakeElements.itemGroup,
                    excludeLocation.LocationString);
            }
        }

        private static void ThrowDriveEnumerationExceptionWithTargetLoggingContext(IElementLocation includeLocation, IElementLocation excludeLocation, bool excludeFileSpecIsEmpty, string filespecUnescaped, string fileSpec)
        {
            // The first condition is necessary to reach for both GetFileListEscaped calls
            // whenever the wildcarded Include attribute results in drive enumeration, and
            // the second condition is necessary to skip for the GetFileListUnescaped call
            // whenever the wildcarded Exclude attribute results in drive enumeration.
            if (excludeFileSpecIsEmpty && (includeLocation != null))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    includeLocation,
                    DriveEnumeratingWildcardMessageResourceName,
                    filespecUnescaped,
                    XMakeAttributes.include,
                    XMakeElements.itemGroup,
                    includeLocation.LocationString);
            }

            // The first condition is necessary to reach for both GetFileListEscaped calls
            // whenever the wildcarded Exclude attribute results in drive enumeration, and
            // the second condition is necessary to reach for the GetFileListUnescaped call
            // (also when the wildcarded Exclude attribute results in drive enumeration).
            else if (((!excludeFileSpecIsEmpty) || (includeLocation == null)) && (excludeLocation != null))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                        excludeLocation,
                        DriveEnumeratingWildcardMessageResourceName,
                        fileSpec,
                        XMakeAttributes.exclude,
                        XMakeElements.itemGroup,
                        excludeLocation.LocationString);
            }
        }

        private static void ThrowDriveEnumerationExceptionWithLoggingService(IElementLocation includeLocation, string filespecUnescaped)
        {
            ProjectErrorUtilities.ThrowInvalidProject(
                includeLocation,
                DriveEnumeratingWildcardMessageResourceName,
                filespecUnescaped,
                XMakeAttributes.include,
                XMakeElements.itemGroup,
                includeLocation.LocationString);
        }

        private static void ThrowDriveEnumerationExceptionWithEvaluationLoggingContext(IElementLocation importLocation, IElementLocation includeLocation, IElementLocation excludeLocation, string filespecUnescaped, string fileSpec, bool excludeFileSpecIsEmpty)
        {
            if (importLocation != null)
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    importLocation,
                    DriveEnumeratingWildcardMessageResourceName,
                    filespecUnescaped,
                    XMakeAttributes.project,
                    XMakeElements.import,
                    importLocation.LocationString);
            }
            else if (excludeFileSpecIsEmpty && includeLocation != null)
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    includeLocation,
                    DriveEnumeratingWildcardMessageResourceName,
                    fileSpec,
                    XMakeAttributes.include,
                    XMakeElements.itemGroup,
                    includeLocation.LocationString);
            }
            else if (excludeLocation != null)
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    excludeLocation,
                    DriveEnumeratingWildcardMessageResourceName,
                    fileSpec,
                    XMakeAttributes.exclude,
                    XMakeElements.itemGroup,
                    excludeLocation.LocationString);
            }
        }

        private static bool FilespecMatchesLazyWildcard(string filespecEscaped, bool forceEvaluateWildCards)
        {
            return Traits.Instance.UseLazyWildCardEvaluation && !forceEvaluateWildCards && MatchesLazyWildcard(filespecEscaped);
        }

        private static bool IsValidExclude(string exclude)
        {
            // TODO: assumption on legal path characters: https://github.com/dotnet/msbuild/issues/781
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

        // TODO: assumption on file system case sensitivity: https://github.com/dotnet/msbuild/issues/781
        private static readonly Lazy<ConcurrentDictionary<string, bool>> _regexMatchCache = new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

        private static bool MatchesLazyWildcard(string fileSpec)
        {
            return _regexMatchCache.Value.GetOrAdd(fileSpec, file => s_lazyWildCardExpansionRegexes.Any(regex => regex.IsMatch(fileSpec)));
        }

        /// <summary>
        /// Returns a Func that will return true IFF its argument matches any of the specified filespecs.
        /// Assumes filespec may be escaped, so it unescapes it.
        /// The returned function makes no escaping assumptions or escaping operations. Its callers should control escaping.
        /// </summary>
        /// <param name="filespecsEscaped"></param>
        /// <param name="currentDirectory"></param>
        /// <returns>A Func that will return true IFF its argument matches any of the specified filespecs.</returns>
        internal static Func<string, bool> GetFileSpecMatchTester(IList<string> filespecsEscaped, string currentDirectory)
        {
            var matchers = filespecsEscaped
                .Select(fs => new Lazy<FileSpecMatcherTester>(() => FileSpecMatcherTester.Parse(currentDirectory, fs)))
                .ToList();

            return file => matchers.Any(m => m.Value.IsMatch(file));
        }

        internal sealed class IOCache
        {
            private readonly Lazy<ConcurrentDictionary<string, bool>> existenceCache = new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>(), true);

            public bool DirectoryExists(string directory)
            {
                return existenceCache.Value.GetOrAdd(directory, directory => Directory.Exists(directory));
            }
        }
    }
}
