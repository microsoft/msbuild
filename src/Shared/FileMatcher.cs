// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Functions for matching file names with patterns. 
    /// </summary>
    internal class FileMatcher
    {
        private readonly IFileSystem _fileSystem;
        private const string recursiveDirectoryMatch = "**";

        private static readonly string s_directorySeparator = new string(Path.DirectorySeparatorChar, 1);

        private static readonly string s_thisDirectory = "." + s_directorySeparator;

        private static readonly char[] s_wildcardCharacters = { '*', '?' };
        private static readonly char[] s_wildcardAndSemicolonCharacters = { '*', '?', ';' };

        private static readonly string[] s_propertyAndItemReferences = { "$(", "@(" };

        // on OSX both System.IO.Path separators are '/', so we have to use the literals
        internal static readonly char[] directorySeparatorCharacters = FileUtilities.Slashes;

        // until Cloudbuild switches to EvaluationContext, we need to keep their dependence on global glob caching via an environment variable
        private static readonly Lazy<ConcurrentDictionary<string, IReadOnlyList<string>>> s_cachedGlobExpansions = new Lazy<ConcurrentDictionary<string, IReadOnlyList<string>>>(() => new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        private static readonly Lazy<ConcurrentDictionary<string, object>> s_cachedGlobExpansionsLock = new Lazy<ConcurrentDictionary<string, object>>(() => new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cachedGlobExpansions;
        private readonly Lazy<ConcurrentDictionary<string, object>> _cachedGlobExpansionsLock = new Lazy<ConcurrentDictionary<string, object>>(() => new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Cache of the list of invalid path characters, because this method returns a clone (for security reasons)
        /// which can cause significant transient allocations
        /// </summary>
        private static readonly char[] s_invalidPathChars = Path.GetInvalidPathChars();

        public const RegexOptions DefaultRegexOptions = RegexOptions.IgnoreCase;

        private readonly GetFileSystemEntries _getFileSystemEntries;

        private static class FileSpecRegexParts
        {
            internal const string BeginningOfLine = "^";
            internal const string WildcardGroupStart = "(?<WILDCARDDIR>";
            internal const string FilenameGroupStart = "(?<FILENAME>";
            internal const string GroupEnd = ")";
            internal const string EndOfLine = "$";

            internal const string AnyNonSeparator = @"[^/\\]*";
            internal const string AnySingleCharacterButDot = @"[^\.].";
            internal const string AnythingButDot = @"[^\.]*";
            internal const string DirSeparator = @"[/\\]+";
            internal const string LeftDirs = @"((.*/)|(.*\\)|())";
            internal const string MiddleDirs = @"((/)|(\\)|(/.*/)|(/.*\\)|(\\.*\\)|(\\.*/))";
            internal const string SingleCharacter = ".";
            internal const string UncSlashSlash = @"\\\\";
        }

        /*
         * FileSpecRegexParts.BeginningOfLine.Length + FileSpecRegexParts.WildcardGroupStart.Length + FileSpecRegexParts.GroupEnd.Length
            + FileSpecRegexParts.FilenameGroupStart.Length + FileSpecRegexParts.GroupEnd.Length + FileSpecRegexParts.EndOfLine.Length;
         */
        private const int FileSpecRegexMinLength = 31;

        /// <summary>
        /// The Default FileMatcher does not cache directory enumeration.
        /// </summary>
        public static FileMatcher Default = new FileMatcher(FileSystems.Default, null);

        public FileMatcher(IFileSystem fileSystem, ConcurrentDictionary<string, IReadOnlyList<string>> fileEntryExpansionCache = null) : this(
            fileSystem,
            (entityType, path, pattern, projectDirectory, stripProjectDirectory) => GetAccessibleFileSystemEntries(
                fileSystem,
                entityType,
                path,
                pattern,
                projectDirectory,
                stripProjectDirectory).ToArray(),
            fileEntryExpansionCache)
        {
        }

        internal FileMatcher(IFileSystem fileSystem, GetFileSystemEntries getFileSystemEntries, ConcurrentDictionary<string, IReadOnlyList<string>> getFileSystemDirectoryEntriesCache = null)
        {
            if (Traits.Instance.MSBuildCacheFileEnumerations)
            {
                _cachedGlobExpansions = s_cachedGlobExpansions.Value;
                _cachedGlobExpansionsLock = s_cachedGlobExpansionsLock;
            }
            else
            {
                _cachedGlobExpansions = getFileSystemDirectoryEntriesCache;
            }

            _fileSystem = fileSystem;

            _getFileSystemEntries = getFileSystemDirectoryEntriesCache == null
                ? getFileSystemEntries
                : (type, path, pattern, directory, stripProjectDirectory) =>
                {
                    if (ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave16_10))
                    {
                        // New behavior:
                        // Always hit the filesystem with "*" pattern, cache the results, and do the filtering here.
                        string cacheKey = type switch
                        {
                            FileSystemEntity.Files => "F",
                            FileSystemEntity.Directories => "D",
                            FileSystemEntity.FilesAndDirectories => "A",
                            _ => throw new NotImplementedException()
                        } + ";" + path;
                        IReadOnlyList<string> allEntriesForPath = getFileSystemDirectoryEntriesCache.GetOrAdd(
                                cacheKey,
                                s => getFileSystemEntries(
                                    type,
                                    path,
                                    "*",
                                    directory,
                                    false));
                        IEnumerable<string> filteredEntriesForPath = (pattern != null && !IsAllFilesWildcard(pattern))
                            ? allEntriesForPath.Where(o => IsMatch(Path.GetFileName(o), pattern))
                            : allEntriesForPath;
                        return stripProjectDirectory
                            ? RemoveProjectDirectory(filteredEntriesForPath, directory).ToArray()
                            : filteredEntriesForPath.ToArray();
                    }
                    else
                    {
                        // Legacy behavior:
                        // Cache only directories, for files we won't hit the cache because the file name patterns tend to be unique
                        if (type == FileSystemEntity.Directories)
                        {
                            return getFileSystemDirectoryEntriesCache.GetOrAdd(
                                $"D;{path};{pattern ?? "*"}",
                                s => getFileSystemEntries(
                                    type,
                                    path,
                                    pattern,
                                    directory,
                                    stripProjectDirectory).ToArray());
                        }
                    }
                    return getFileSystemEntries(type, path, pattern, directory, stripProjectDirectory);
                };
        }

        /// <summary>
        /// The type of entity that GetFileSystemEntries should return.
        /// </summary>
        internal enum FileSystemEntity
        {
            Files,
            Directories,
            FilesAndDirectories
        };

        /// <summary>
        /// Delegate defines the GetFileSystemEntries signature that GetLongPathName uses
        /// to enumerate directories on the file system.
        /// </summary>
        /// <param name="entityType">Files, Directories, or Files and Directories</param>
        /// <param name="path">The path to search.</param>
        /// <param name="pattern">The file pattern.</param>
        /// <param name="projectDirectory"></param>
        /// <param name="stripProjectDirectory"></param>
        /// <returns>An enumerable of filesystem entries.</returns>
        internal delegate IReadOnlyList<string> GetFileSystemEntries(FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory);

        internal static void ClearFileEnumerationsCache()
        {
            if (s_cachedGlobExpansions.IsValueCreated)
            {
                s_cachedGlobExpansions.Value.Clear();
            }

            if (s_cachedGlobExpansionsLock.IsValueCreated)
            {
                s_cachedGlobExpansionsLock.Value.Clear();
            }
        }

        /// <summary>
        /// Determines whether the given path has any wild card characters.
        /// </summary>
        internal static bool HasWildcards(string filespec)
        {
            // Perf Note: Doing a [Last]IndexOfAny(...) is much faster than compiling a
            // regular expression that does the same thing, regardless of whether
            // filespec contains one of the characters.
            // Choose LastIndexOfAny instead of IndexOfAny because it seems more likely
            // that wildcards will tend to be towards the right side.

            return -1 != filespec.LastIndexOfAny(s_wildcardCharacters);
        }

        /// <summary>
        /// Determines whether the given path has any wild card characters, any semicolons or any property references.
        /// </summary>
        internal static bool HasWildcardsSemicolonItemOrPropertyReferences(string filespec)
        {
            return

                (-1 != filespec.IndexOfAny(s_wildcardAndSemicolonCharacters)) ||
                HasPropertyOrItemReferences(filespec)
                ;
        }

        /// <summary>
        /// Determines whether the given path has any property references.
        /// </summary>
        internal static bool HasPropertyOrItemReferences(string filespec)
        {
            return s_propertyAndItemReferences.Any(filespec.Contains);
        }

        /// <summary>
        /// Get the files and\or folders specified by the given path and pattern.
        /// </summary>
        /// <param name="entityType">Whether Files, Directories or both.</param>
        /// <param name="path">The path to search.</param>
        /// <param name="pattern">The pattern to search.</param>
        /// <param name="projectDirectory">The directory for the project within which the call is made</param>
        /// <param name="stripProjectDirectory">If true the project directory should be stripped</param>
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// <returns></returns>
        private static IReadOnlyList<string> GetAccessibleFileSystemEntries(IFileSystem fileSystem, FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory)
        {
            path = FileUtilities.FixFilePath(path);
            switch (entityType)
            {
                case FileSystemEntity.Files: return GetAccessibleFiles(fileSystem, path, pattern, projectDirectory, stripProjectDirectory);
                case FileSystemEntity.Directories: return GetAccessibleDirectories(fileSystem, path, pattern);
                case FileSystemEntity.FilesAndDirectories: return GetAccessibleFilesAndDirectories(fileSystem,path, pattern);
                default:
                    ErrorUtilities.VerifyThrow(false, "Unexpected filesystem entity type.");
                    break;
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Returns an enumerable of file system entries matching the specified search criteria. Inaccessible or non-existent file
        /// system entries are skipped.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="pattern"></param>
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// <returns>An enumerable of matching file system entries (can be empty).</returns>
        private static IReadOnlyList<string> GetAccessibleFilesAndDirectories(IFileSystem fileSystem, string path, string pattern)
        {
            if (fileSystem.DirectoryExists(path))
            {
                try
                {
                    return (ShouldEnforceMatching(pattern)
                        ? fileSystem.EnumerateFileSystemEntries(path, pattern)
                            .Where(o => IsMatch(Path.GetFileName(o), pattern))
                        : fileSystem.EnumerateFileSystemEntries(path, pattern)
                        ).ToArray();
                }
                // for OS security
                catch (UnauthorizedAccessException)
                {
                    // do nothing
                }
                // for code access security
                catch (System.Security.SecurityException)
                {
                    // do nothing
                }
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Determine if the given search pattern will match loosely on Windows
        /// </summary>
        /// <param name="searchPattern">The search pattern to check</param>
        /// <returns></returns>
        private static bool ShouldEnforceMatching(string searchPattern)
        {
            if (searchPattern == null)
            {
                return false;
            }
            // https://github.com/Microsoft/msbuild/issues/3060
            // NOTE: Corefx matches loosely in three cases (in the absence of the * wildcard in the extension):
            // 1) if the extension ends with the ? wildcard, it matches files with shorter extensions also e.g. "file.tx?" would
            //    match both "file.txt" and "file.tx"
            // 2) if the extension is three characters, and the filename contains the * wildcard, it matches files with longer
            //    extensions that start with the same three characters e.g. "*.htm" would match both "file.htm" and "file.html"
            // 3) if the ? wildcard is to the left of a period, it matches files with shorter name e.g. ???.txt would match
            //    foo.txt, fo.txt and also f.txt
            return searchPattern.IndexOf("?.", StringComparison.Ordinal) != -1 ||
                   (
                       Path.GetExtension(searchPattern).Length == (3 + 1 /* +1 for the period */) &&
                       searchPattern.IndexOf('*') != -1
                   ) ||
                   searchPattern.EndsWith("?", StringComparison.Ordinal);
        }

        /// <summary>
        /// Same as Directory.EnumerateFiles(...) except that files that
        /// aren't accessible are skipped instead of throwing an exception.
        /// 
        /// Other exceptions are passed through.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="filespec">The pattern.</param>
        /// <param name="projectDirectory">The project directory</param>
        /// <param name="stripProjectDirectory"></param>
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// <returns>Files that can be accessed.</returns>
        private static IReadOnlyList<string> GetAccessibleFiles
        (
            IFileSystem fileSystem,
            string path,
            string filespec,     // can be null
            string projectDirectory,
            bool stripProjectDirectory
        )
        {
            try
            {
                // look in current directory if no path specified
                string dir = ((path.Length == 0) ? s_thisDirectory : path);

                // get all files in specified directory, unless a file-spec has been provided
                IEnumerable<string> files;
                if (filespec == null)
                {
                    files = fileSystem.EnumerateFiles(dir);
                }
                else
                {
                    files = fileSystem.EnumerateFiles(dir, filespec);
                    if (ShouldEnforceMatching(filespec))
                    {
                        files = files.Where(o => IsMatch(Path.GetFileName(o), filespec));
                    }
                }
                // If the Item is based on a relative path we need to strip
                // the current directory from the front
                if (stripProjectDirectory)
                {
                    files = RemoveProjectDirectory(files, projectDirectory);
                }
                // Files in the current directory are coming back with a ".\"
                // prepended to them.  We need to remove this; it breaks the
                // IDE, which expects just the filename if it is in the current
                // directory.  But only do this if the original path requested
                // didn't itself contain a ".\".
                else if (!path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    files = RemoveInitialDotSlash(files);
                }

                return files.ToArray();
            }
            catch (System.Security.SecurityException)
            {
                // For code access security.
                return Array.Empty<string>();
            }
            catch (System.UnauthorizedAccessException)
            {
                // For OS security.
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Same as Directory.EnumerateDirectories(...) except that files that
        /// aren't accessible are skipped instead of throwing an exception.
        /// 
        /// Other exceptions are passed through.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="pattern">Pattern to match</param>
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// <returns>Accessible directories.</returns>
        private static IReadOnlyList<string> GetAccessibleDirectories
        (
            IFileSystem fileSystem,
            string path,
            string pattern
        )
        {
            try
            {
                IEnumerable<string> directories = null;

                if (pattern == null)
                {
                    directories = fileSystem.EnumerateDirectories((path.Length == 0) ? s_thisDirectory : path);
                }
                else
                {
                    directories = fileSystem.EnumerateDirectories((path.Length == 0) ? s_thisDirectory : path, pattern);
                    if (ShouldEnforceMatching(pattern))
                    {
                        directories = directories.Where(o => IsMatch(Path.GetFileName(o), pattern));
                    }
                }

                // Subdirectories in the current directory are coming back with a ".\"
                // prepended to them.  We need to remove this; it breaks the
                // IDE, which expects just the filename if it is in the current
                // directory.  But only do this if the original path requested
                // didn't itself contain a ".\".
                if (!path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    directories = RemoveInitialDotSlash(directories);
                }

                return directories.ToArray();
            }
            catch (System.Security.SecurityException)
            {
                // For code access security.
                return Array.Empty<string>();
            }
            catch (System.UnauthorizedAccessException)
            {
                // For OS security.
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Given a path name, get its long version.
        /// </summary>
        /// <param name="path">The short path.</param>
        /// <returns>The long path.</returns>
        internal string GetLongPathName
        (
            string path
        )
        {
            return GetLongPathName(path, _getFileSystemEntries);
        }

        /// <summary>
        /// Given a path name, get its long version.
        /// </summary>
        /// <param name="path">The short path.</param>
        /// <param name="getFileSystemEntries">Delegate.</param>
        /// <returns>The long path.</returns>
        internal static string GetLongPathName
        (
            string path,
            GetFileSystemEntries getFileSystemEntries
        )
        {
            if (path.IndexOf("~", StringComparison.Ordinal) == -1)
            {
                // A path with no '~' must not be a short name.
                return path;
            }

            ErrorUtilities.VerifyThrow(!HasWildcards(path),
                "GetLongPathName does not handle wildcards and was passed '{0}'.", path);

            string[] parts = path.Split(directorySeparatorCharacters);
            string pathRoot;
            bool isUnc = path.StartsWith(s_directorySeparator + s_directorySeparator, StringComparison.Ordinal);
            int startingElement;
            if (isUnc)
            {
                pathRoot = s_directorySeparator + s_directorySeparator;
                pathRoot += parts[2];
                pathRoot += s_directorySeparator;
                pathRoot += parts[3];
                pathRoot += s_directorySeparator;
                startingElement = 4;
            }
            else
            {
                // Is it relative?
                if (path.Length > 2 && path[1] == ':')
                {
                    // Not relative
                    pathRoot = parts[0] + s_directorySeparator;
                    startingElement = 1;
                }
                else
                {
                    // Relative
                    pathRoot = string.Empty;
                    startingElement = 0;
                }
            }

            // Build up an array of parts. These elements may be "" if there are
            // extra slashes.
            string[] longParts = new string[parts.Length - startingElement];

            string longPath = pathRoot;
            for (int i = startingElement; i < parts.Length; ++i)
            {
                // If there is a zero-length part, then that means there was an extra slash.
                if (parts[i].Length == 0)
                {
                    longParts[i - startingElement] = string.Empty;
                }
                else
                {
                    if (parts[i].IndexOf("~", StringComparison.Ordinal) == -1)
                    {
                        // If there's no ~, don't hit the disk.
                        longParts[i - startingElement] = parts[i];
                        longPath = Path.Combine(longPath, parts[i]);
                    }
                    else
                    {
                        // getFileSystemEntries(...) returns an empty enumerable if longPath doesn't exist.
                        IReadOnlyList<string> entries = getFileSystemEntries(FileSystemEntity.FilesAndDirectories, longPath, parts[i], null, false);

                        if (0 == entries.Count)
                        {
                            // The next part doesn't exist. Therefore, no more of the path will exist.
                            // Just return the rest.
                            for (int j = i; j < parts.Length; ++j)
                            {
                                longParts[j - startingElement] = parts[j];
                            }
                            break;
                        }

                        // Since we know there are no wild cards, this should be length one, i.e. MoveNext should return false.
                        ErrorUtilities.VerifyThrow(entries.Count == 1,
                            "Unexpected number of entries ({3}) found when enumerating '{0}' under '{1}'. Original path was '{2}'",
                            parts[i], longPath, path, entries.Count);

                        // Entries[0] contains the full path.
                        longPath = entries[0];

                        // We just want the trailing node.
                        longParts[i - startingElement] = Path.GetFileName(longPath);
                    }
                }
            }

            return pathRoot + string.Join(s_directorySeparator, longParts);
        }

        /// <summary>
        /// Given a filespec, split it into left-most 'fixed' dir part, middle 'wildcard' dir part, and filename part.
        /// The filename part may have wildcard characters in it.
        /// </summary>
        /// <param name="filespec">The filespec to be decomposed.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        internal void SplitFileSpec(
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart)
        {
            PreprocessFileSpecForSplitting
            (
                filespec,
                out fixedDirectoryPart,
                out wildcardDirectoryPart,
                out filenamePart
            );

            /* 
             * Handle the special case in which filenamePart is '**'.
             * In this case, filenamePart becomes '*.*' and the '**' is appended
             * to the end of the wildcardDirectory part.
             * This is so that later regular expression matching can accurately
             * pull out the different parts (fixed, wildcard, filename) of given
             * file specs.
             */
            if (recursiveDirectoryMatch == filenamePart)
            {
                wildcardDirectoryPart += recursiveDirectoryMatch;
                wildcardDirectoryPart += s_directorySeparator;
                filenamePart = "*.*";
            }

            fixedDirectoryPart = FileMatcher.GetLongPathName(fixedDirectoryPart, _getFileSystemEntries);
        }

        /// <summary>
        /// Do most of the grunt work of splitting the filespec into parts.
        /// Does not handle post-processing common to the different matching
        /// paths.
        /// </summary>
        /// <param name="filespec">The filespec to be decomposed.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        private static void PreprocessFileSpecForSplitting
        (
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart
        )
        {
            filespec = FileUtilities.FixFilePath(filespec);
            int indexOfLastDirectorySeparator = filespec.LastIndexOfAny(directorySeparatorCharacters);
            if (-1 == indexOfLastDirectorySeparator)
            {
                /*
                 * No dir separator found. This is either this form,
                 * 
                 *      Source.cs
                 *      *.cs
                 * 
                 *  or this form,
                 * 
                 *     **
                 */
                fixedDirectoryPart = string.Empty;
                wildcardDirectoryPart = string.Empty;
                filenamePart = filespec;
                return;
            }

            int indexOfFirstWildcard = filespec.IndexOfAny(s_wildcardCharacters);
            if
            (
                -1 == indexOfFirstWildcard
                || indexOfFirstWildcard > indexOfLastDirectorySeparator
            )
            {
                /*
                 * There is at least one dir separator, but either there is no wild card or the
                 * wildcard is after the dir separator.
                 *
                 * The form is one of these:
                 * 
                 *      dir1\Source.cs
                 *      dir1\*.cs
                 * 
                 * Where the trailing spec is meant to be a filename. Or,
                 * 
                 *      dir1\**
                 * 
                 * Where the trailing spec is meant to be any file recursively.
                 */

                // We know the fixed director part now.
                fixedDirectoryPart = filespec.Substring(0, indexOfLastDirectorySeparator + 1);
                wildcardDirectoryPart = string.Empty;
                filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
                return;
            }

            /*
             * Find the separator right before the first wildcard.
             */
            string filespecLeftOfWildcard = filespec.Substring(0, indexOfFirstWildcard);
            int indexOfSeparatorBeforeWildCard = filespecLeftOfWildcard.LastIndexOfAny(directorySeparatorCharacters);
            if (-1 == indexOfSeparatorBeforeWildCard)
            {
                /*
                 * There is no separator before the wildcard, so the form is like this:
                 * 
                 *      dir?\Source.cs
                 * 
                 * or this,
                 * 
                 *      dir?\**
                 */
                fixedDirectoryPart = string.Empty;
                wildcardDirectoryPart = filespec.Substring(0, indexOfLastDirectorySeparator + 1);
                filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
                return;
            }

            /*
             * There is at least one wildcard and one dir separator, split parts out.
             */
            fixedDirectoryPart = filespec.Substring(0, indexOfSeparatorBeforeWildCard + 1);
            wildcardDirectoryPart = filespec.Substring(indexOfSeparatorBeforeWildCard + 1, indexOfLastDirectorySeparator - indexOfSeparatorBeforeWildCard);
            filenamePart = filespec.Substring(indexOfLastDirectorySeparator + 1);
        }

        /// <summary>
        /// Removes the leading ".\" from all of the paths in the array. 
        /// </summary>
        /// <param name="paths">Paths to remove .\ from.</param>
        private static IEnumerable<string> RemoveInitialDotSlash
        (
            IEnumerable<string> paths
        )
        {
            foreach (string path in paths)
            {
                if (path.StartsWith(s_thisDirectory, StringComparison.Ordinal))
                {
                    yield return path.Substring(2);
                }
                else
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// Checks if the char is a DirectorySeparatorChar or a AltDirectorySeparatorChar
        /// </summary>
        /// <param name="c"></param>
        /// <returns></returns>
        internal static bool IsDirectorySeparator(char c)
        {
            return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        }
        /// <summary>
        /// Removes the current directory converting the file back to relative path 
        /// </summary>
        /// <param name="paths">Paths to remove current directory from.</param>
        /// <param name="projectDirectory"></param>
        internal static IEnumerable<string> RemoveProjectDirectory
        (
            IEnumerable<string> paths,
            string projectDirectory
        )
        {
            bool directoryLastCharIsSeparator = IsDirectorySeparator(projectDirectory[projectDirectory.Length - 1]);
            foreach (string path in paths)
            {
                if (path.StartsWith(projectDirectory, StringComparison.Ordinal))
                {
                    // If the project directory did not end in a slash we need to check to see if the next char in the path is a slash
                    if (!directoryLastCharIsSeparator)
                    {
                        //If the next char after the project directory is not a slash, skip this path
                        if (path.Length <= projectDirectory.Length || !IsDirectorySeparator(path[projectDirectory.Length]))
                        {
                            yield return path;
                            continue;
                        }
                        yield return path.Substring(projectDirectory.Length + 1);
                    }
                    else
                    {
                        yield return path.Substring(projectDirectory.Length);
                    }
                }
                else
                {
                    yield return path;
                }
            }
        }

        struct RecursiveStepResult
        {
            public string RemainingWildcardDirectory;
            public bool ConsiderFiles;
            public bool NeedsToProcessEachFile;
            public string DirectoryPattern;
            public bool NeedsDirectoryRecursion;
        }

        class FilesSearchData
        {
            public FilesSearchData(
                string filespec,                // can be null
                string directoryPattern,        // can be null
                Regex regexFileMatch,           // can be null
                bool needsRecursion
                )
            {
                Filespec = filespec;
                DirectoryPattern = directoryPattern;
                RegexFileMatch = regexFileMatch;
                NeedsRecursion = needsRecursion;
            }

            /// <summary>
            /// The filespec.
            /// </summary>
            public string Filespec { get; }
            /// <summary>
            /// Holds the directory pattern for globs like **/{pattern}/**, i.e. when we're looking for a matching directory name
            /// regardless of where on the path it is. This field is used only if the wildcard directory part has this shape. In
            /// other cases such as **/{pattern1}/**/{pattern2}/**, we don't use this optimization and instead rely on
            /// <see cref="RegexFileMatch"/> to test if a file path matches the glob or not.
            /// </summary>
            public string DirectoryPattern { get; }
            /// <summary>
            /// Wild-card matching.
            /// </summary>
            public Regex RegexFileMatch { get; }
            /// <summary>
            /// If true, then recursion is required.
            /// </summary>
            public bool NeedsRecursion { get; }
        }

        struct RecursionState
        {
            /// <summary>
            /// The directory to search in
            /// </summary>
            public string BaseDirectory;
            /// <summary>
            /// The remaining, wildcard part of the directory.
            /// </summary>
            public string RemainingWildcardDirectory;
            /// <summary>
            /// True if SearchData.DirectoryPattern is non-null and we have descended into a directory that matches the pattern.
            /// </summary>
            public bool IsInsideMatchingDirectory;
            /// <summary>
            /// Data about a search that does not change as the search recursively traverses directories
            /// </summary>
            public FilesSearchData SearchData;

            /// <summary>
            /// True if a SearchData.DirectoryPattern is specified but we have not descended into a matching directory.
            /// </summary>
            public bool IsLookingForMatchingDirectory => (SearchData.DirectoryPattern != null && !IsInsideMatchingDirectory);
        }

        /// <summary>
        /// Get all files that match either the file-spec or the regular expression. 
        /// </summary>
        /// <param name="listOfFiles">List of files that gets populated.</param>
        /// <param name="recursionState">Information about the search</param>
        /// <param name="projectDirectory"></param>
        /// <param name="stripProjectDirectory"></param>
        /// <param name="searchesToExclude">Patterns to exclude from the results</param>
        /// <param name="searchesToExcludeInSubdirs">exclude patterns that might activate farther down the directory tree. Keys assume paths are normalized with forward slashes and no trailing slashes</param>
        /// <param name="taskOptions">Options for tuning the parallelization of subdirectories</param>
        private void GetFilesRecursive(
            ConcurrentStack<List<string>> listOfFiles,
            RecursionState recursionState,
            string projectDirectory,
            bool stripProjectDirectory,
            IList<RecursionState> searchesToExclude,
            Dictionary<string, List<RecursionState>> searchesToExcludeInSubdirs,
            TaskOptions taskOptions)
        {
            ErrorUtilities.VerifyThrow((recursionState.SearchData.Filespec== null) || (recursionState.SearchData.RegexFileMatch == null),
                "File-spec overrides the regular expression -- pass null for file-spec if you want to use the regular expression.");

            ErrorUtilities.VerifyThrow((recursionState.SearchData.Filespec != null) || (recursionState.SearchData.RegexFileMatch != null),
                "Need either a file-spec or a regular expression to match files.");

            ErrorUtilities.VerifyThrow(recursionState.RemainingWildcardDirectory != null, "Expected non-null remaning wildcard directory.");

            RecursiveStepResult[] excludeNextSteps = null;
            //  Determine if any of searchesToExclude is necessarily a superset of the results that will be returned.
            //  This means all results will be excluded and we should bail out now.
            if (searchesToExclude != null)
            {
                excludeNextSteps = new RecursiveStepResult[searchesToExclude.Count];
                for (int i = 0; i < searchesToExclude.Count; i++)
                {
                    RecursionState searchToExclude = searchesToExclude[i];
                    //  The BaseDirectory of all the exclude searches should be the same as the include one
                    Debug.Assert(FileUtilities.PathsEqual(searchToExclude.BaseDirectory, recursionState.BaseDirectory), "Expected exclude search base directory to match include search base directory");

                    excludeNextSteps[i] = GetFilesRecursiveStep(searchesToExclude[i]);

                    //  We can exclude all results in this folder if:
                    if (
                        //  We are not looking for a directory matching the pattern given in SearchData.DirectoryPattern
                        !searchToExclude.IsLookingForMatchingDirectory &&
                        //  We are matching files based on a filespec and not a regular expression
                        searchToExclude.SearchData.Filespec != null &&
                        //  The wildcard path portion of the excluded search matches the include search
                        searchToExclude.RemainingWildcardDirectory == recursionState.RemainingWildcardDirectory &&
                        //  The exclude search will match ALL filenames OR
                        (IsAllFilesWildcard(searchToExclude.SearchData.Filespec) ||
                            //  The exclude search filename pattern matches the include search's pattern
                            searchToExclude.SearchData.Filespec == recursionState.SearchData.Filespec))
                    {
                        //  We won't get any results from this search that we would end up keeping
                        return;
                    }
                }
            }

            RecursiveStepResult nextStep = GetFilesRecursiveStep(recursionState);

            List<string> files = null;
            foreach (string file in GetFilesForStep(nextStep, recursionState, projectDirectory,
                stripProjectDirectory))
            {
                if (excludeNextSteps != null)
                {
                    bool exclude = false;
                    for (int i = 0; i < excludeNextSteps.Length; i++)
                    {
                        RecursiveStepResult excludeNextStep = excludeNextSteps[i];
                        if (excludeNextStep.ConsiderFiles && MatchFileRecursionStep(searchesToExclude[i], file))
                        {
                            exclude = true;
                            break;
                        }
                    }
                    if (exclude)
                    {
                        continue;
                    }
                }
                files ??= new List<string>();
                files.Add(file);
            }
            // Add all matched files at once to reduce thread contention
            if (files?.Count > 0)
            {
                listOfFiles.Push(files);
            }

            if (!nextStep.NeedsDirectoryRecursion)
            {
                return;
            }

            Action<string> processSubdirectory = subdir =>
            {
                //  RecursionState is a struct so this copies it
                var newRecursionState = recursionState;

                newRecursionState.BaseDirectory = subdir;
                newRecursionState.RemainingWildcardDirectory = nextStep.RemainingWildcardDirectory;

                if (newRecursionState.IsLookingForMatchingDirectory &&
                    DirectoryEndsWithPattern(subdir, recursionState.SearchData.DirectoryPattern))
                {
                    newRecursionState.IsInsideMatchingDirectory = true;
                }

                List<RecursionState> newSearchesToExclude = null;

                if (excludeNextSteps != null)
                {
                    newSearchesToExclude = new List<RecursionState>();

                    for (int i = 0; i < excludeNextSteps.Length; i++)
                    {
                        if (excludeNextSteps[i].NeedsDirectoryRecursion &&
                            (excludeNextSteps[i].DirectoryPattern == null || IsMatch(Path.GetFileName(subdir), excludeNextSteps[i].DirectoryPattern)))
                        {
                            RecursionState thisExcludeStep = searchesToExclude[i];
                            thisExcludeStep.BaseDirectory = subdir;
                            thisExcludeStep.RemainingWildcardDirectory = excludeNextSteps[i].RemainingWildcardDirectory;
                            if (thisExcludeStep.IsLookingForMatchingDirectory &&
                                DirectoryEndsWithPattern(subdir, thisExcludeStep.SearchData.DirectoryPattern))
                            {
                                thisExcludeStep.IsInsideMatchingDirectory = true;
                            }
                            newSearchesToExclude.Add(thisExcludeStep);
                        }
                    }
                }

                if (searchesToExcludeInSubdirs != null)
                {
                    List<RecursionState> searchesForSubdir;

                    if (searchesToExcludeInSubdirs.TryGetValue(subdir, out searchesForSubdir))
                    {
                        //  We've found the base directory that these exclusions apply to.  So now add them as normal searches
                        if (newSearchesToExclude == null)
                        {
                            newSearchesToExclude = new List<RecursionState>();
                        }
                        newSearchesToExclude.AddRange(searchesForSubdir);
                    }
                }

                // We never want to strip the project directory from the leaves, because the current 
                // process directory maybe different
                GetFilesRecursive(
                    listOfFiles,
                    newRecursionState,
                    projectDirectory,
                    stripProjectDirectory,
                    newSearchesToExclude,
                    searchesToExcludeInSubdirs,
                    taskOptions);
            };

            // Calcuate the MaxDegreeOfParallelism value in order to prevent too much tasks being running concurrently.
            int dop = 0;
            // Lock only when we may be dealing with multiple threads
            if (taskOptions.MaxTasks > 1 && taskOptions.MaxTasksPerIteration > 1)
            {
                // We don't need to lock when there will be only one Parallel.ForEach running
                // If the condition is true, means that we are going to iterate though the project root folder
                // by using only one Parallel.ForEach
                if (taskOptions.MaxTasks == taskOptions.MaxTasksPerIteration)
                {
                    dop = taskOptions.AvailableTasks;
                    taskOptions.AvailableTasks = 0;
                }
                else
                {
                    lock (taskOptions)
                    {
                        dop = Math.Min(taskOptions.MaxTasksPerIteration, taskOptions.AvailableTasks);
                        taskOptions.AvailableTasks -= dop;
                    }
                }
            }
            // Use a foreach to reduce the overhead of Parallel.ForEach when we are not running in parallel
            if (dop < 2)
            {
                foreach (var subdir in _getFileSystemEntries(FileSystemEntity.Directories, recursionState.BaseDirectory, nextStep.DirectoryPattern, null, false))
                {
                    processSubdirectory(subdir);
                }
            }
            else
            {
                Parallel.ForEach(
                    _getFileSystemEntries(FileSystemEntity.Directories, recursionState.BaseDirectory, nextStep.DirectoryPattern, null, false),
                    new ParallelOptions {MaxDegreeOfParallelism = dop},
                    processSubdirectory);
            }
            if (dop <= 0)
            {
                return;
            }
            // We don't need to lock if there was only one Parallel.ForEach running
            // If the condition is true, means that we finished the iteration though the project root folder and
            // all its subdirectories
            if (taskOptions.MaxTasks == taskOptions.MaxTasksPerIteration)
            {
                taskOptions.AvailableTasks = taskOptions.MaxTasks;
                return;
            }
            lock (taskOptions)
            {
                taskOptions.AvailableTasks += dop;
            }
        }

        private IEnumerable<string> GetFilesForStep(
            RecursiveStepResult stepResult,
            RecursionState recursionState,
            string projectDirectory,
            bool stripProjectDirectory)
        {
            if (!stepResult.ConsiderFiles)
            {
                return Enumerable.Empty<string>();
            }

            // Back-compat hack: We don't use case-insensitive file enumeration I/O on Linux so the behavior is different depending
            // on the NeedsToProcessEachFile flag. If the flag is false and matching is done within the _getFileSystemEntries call,
            // it is case sensitive. If the flag is true and matching is handled with MatchFileRecursionStep, it is case-insensitive.
            // TODO: Can we fix this by using case-insensitive file I/O on Linux?
            string filespec;
            if (NativeMethodsShared.IsLinux && recursionState.SearchData.DirectoryPattern != null)
            {
                filespec = "*.*";
                stepResult.NeedsToProcessEachFile = true;
            }
            else
            {
                filespec = recursionState.SearchData.Filespec;
            }

            IEnumerable<string> files = _getFileSystemEntries(FileSystemEntity.Files, recursionState.BaseDirectory,
                filespec, projectDirectory, stripProjectDirectory);

            if (!stepResult.NeedsToProcessEachFile)
            {
                return files;
            }
            return files.Where(o => MatchFileRecursionStep(recursionState, o));
        }

        private static bool MatchFileRecursionStep(RecursionState recursionState, string file)
        {
            if (IsAllFilesWildcard(recursionState.SearchData.Filespec))
            {
                return true;
            }
            else if (recursionState.SearchData.Filespec != null)
            {
                return IsMatch(Path.GetFileName(file), recursionState.SearchData.Filespec);
            }

            // if no file-spec provided, match the file to the regular expression
            // PERF NOTE: Regex.IsMatch() is an expensive operation, so we avoid it whenever possible
            return recursionState.SearchData.RegexFileMatch.IsMatch(file);
        }

        private static RecursiveStepResult GetFilesRecursiveStep
        (
            RecursionState recursionState
        )
        {
            RecursiveStepResult ret = new RecursiveStepResult();

            /*
             * Get the matching files.
             */
            bool considerFiles = false;

            // Only consider files if...
            if (recursionState.SearchData.DirectoryPattern != null)
            {
                // We are looking for a directory pattern and have descended into a matching directory,
                considerFiles = recursionState.IsInsideMatchingDirectory;
            }
            else if (recursionState.RemainingWildcardDirectory.Length == 0)
            {
                // or we've reached the end of the wildcard directory elements,
                considerFiles = true;
            }
            else if (recursionState.RemainingWildcardDirectory.IndexOf(recursiveDirectoryMatch, StringComparison.Ordinal) == 0)
            {
                // or, we've reached a "**" so everything else is matched recursively.
                considerFiles = true;
            }
            ret.ConsiderFiles = considerFiles;
            if (considerFiles)
            {
                ret.NeedsToProcessEachFile = recursionState.SearchData.Filespec == null;
            }

            /*
             * Recurse into subdirectories.
             */
            if (recursionState.SearchData.NeedsRecursion && recursionState.RemainingWildcardDirectory.Length > 0)
            {
                // Find the next directory piece.
                string pattern = null;

                if (!IsRecursiveDirectoryMatch(recursionState.RemainingWildcardDirectory))
                {
                    int indexOfNextSlash = recursionState.RemainingWildcardDirectory.IndexOfAny(directorySeparatorCharacters);

                    pattern = indexOfNextSlash != -1 ? recursionState.RemainingWildcardDirectory.Substring(0, indexOfNextSlash) : recursionState.RemainingWildcardDirectory;

                    if (pattern == recursiveDirectoryMatch)
                    {
                        // If pattern turned into **, then there's no choice but to enumerate everything.
                        pattern = null;
                        recursionState.RemainingWildcardDirectory = recursiveDirectoryMatch;
                    }
                    else
                    {
                        // Peel off the leftmost directory piece. So for example, if remainingWildcardDirectory
                        // contains:
                        //
                        //        ?emp\foo\**\bar
                        //
                        // then put '?emp' into pattern. Then put the remaining part,
                        //
                        //        foo\**\bar
                        //
                        // back into remainingWildcardDirectory.
                        // This is a performance optimization. We don't want to enumerate everything if we 
                        // don't have to.
                        recursionState.RemainingWildcardDirectory = indexOfNextSlash != -1 ? recursionState.RemainingWildcardDirectory.Substring(indexOfNextSlash + 1) : string.Empty;
                    }
                }

                ret.NeedsDirectoryRecursion = true;
                ret.RemainingWildcardDirectory = recursionState.RemainingWildcardDirectory;
                ret.DirectoryPattern = pattern;
            }

            return ret;
        }

        /// <summary>
        /// Given a split file spec consisting of a directory without wildcard characters,
        /// a sub-directory containing wildcard characters,
        /// and a filename which may contain wildcard characters,
        /// create a regular expression that will match that file spec.
        /// 
        /// PERF WARNING: this method is called in performance-critical
        /// scenarios, so keep it fast and cheap
        /// </summary>
        /// <param name="fixedDirectoryPart">The fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">The wildcard directory part.</param>
        /// <param name="filenamePart">The filename part.</param>
        /// <returns>The regular expression string.</returns>
        internal static string RegularExpressionFromFileSpec
        (
            string fixedDirectoryPart,
            string wildcardDirectoryPart,
            string filenamePart
        )
        {
#if DEBUG
            ErrorUtilities.VerifyThrow(
                FileSpecRegexMinLength == FileSpecRegexParts.BeginningOfLine.Length
                + FileSpecRegexParts.WildcardGroupStart.Length
                + FileSpecRegexParts.FilenameGroupStart.Length
                + (FileSpecRegexParts.GroupEnd.Length * 2)
                + FileSpecRegexParts.EndOfLine.Length,
                "Checked-in length of known regex components differs from computed length. Update checked-in constant."
            );
#endif
            using (var matchFileExpression = new ReuseableStringBuilder(FileSpecRegexMinLength + NativeMethodsShared.MAX_PATH))
            {
                AppendRegularExpressionFromFixedDirectory(matchFileExpression, fixedDirectoryPart);
                AppendRegularExpressionFromWildcardDirectory(matchFileExpression, wildcardDirectoryPart);
                AppendRegularExpressionFromFilename(matchFileExpression, filenamePart);

                return matchFileExpression.ToString();
            }
        }

        /// <summary>
        /// Determine if the filespec is legal according to the following conditions:
        /// 
        /// (1) It is not legal for there to be a ".." after a wildcard.
        /// 
        /// (2) By definition, "**" must appear alone between directory slashes.If there is any remaining "**" then this is not
        ///     a valid filespec.
        /// </summary>
        /// <returns>True if both parts meet all conditions for a legal filespec.</returns>
        private static bool IsLegalFileSpec(string wildcardDirectoryPart, string filenamePart) =>
            !HasDotDot(wildcardDirectoryPart)
            && !HasMisplacedRecursiveOperator(wildcardDirectoryPart)
            && !HasMisplacedRecursiveOperator(filenamePart);

        private static bool HasDotDot(string str)
        {
            for (int i = 0; i < str.Length - 1; i++)
            {
                if (str[i] == '.' && str[i + 1] == '.')
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasMisplacedRecursiveOperator(string str)
        {
            for (int i = 0; i < str.Length - 1; i++)
            {
                bool isRecursiveOperator = str[i] == '*' && str[i + 1] == '*';

                // Check boundaries for cases such as **\foo\ and *.cs**
                bool isSurroundedBySlashes = (i == 0 || FileUtilities.IsAnySlash(str[i - 1]))
                                             && i < str.Length - 2 && FileUtilities.IsAnySlash(str[i + 2]);

                if (isRecursiveOperator && !isSurroundedBySlashes)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Append the regex equivalents for character sequences in the fixed directory part of a filespec:
        ///
        /// (1) The leading \\ in UNC paths, so that the doubled slash isn't reduced in the last step
        /// 
        /// (2) Common filespec characters
        /// </summary>
        private static void AppendRegularExpressionFromFixedDirectory(ReuseableStringBuilder regex, string fixedDir)
        {
            regex.Append(FileSpecRegexParts.BeginningOfLine);

            bool isUncPath = NativeMethodsShared.IsWindows && fixedDir.Length > 1
                             && fixedDir[0] == '\\' && fixedDir[1] == '\\';
            if (isUncPath)
            {
                regex.Append(FileSpecRegexParts.UncSlashSlash);
            }
            int startIndex = isUncPath ? LastIndexOfDirectorySequence(fixedDir, 0) + 1 : LastIndexOfDirectorySequence(fixedDir, 0);

            for (int i = startIndex; i < fixedDir.Length; i = LastIndexOfDirectorySequence(fixedDir, i + 1))
            {
                AppendRegularExpressionFromChar(regex, fixedDir[i]);
            }
        }

        /// <summary>
        /// Append the regex equivalents for character sequences in the wildcard directory part of a filespec:
        ///
        /// (1) The leading **\ if existing
        ///
        /// (2) Each occurrence of recursive wildcard \**\
        /// 
        /// (3) Common filespec characters
        /// </summary>
        private static void AppendRegularExpressionFromWildcardDirectory(ReuseableStringBuilder regex, string wildcardDir)
        {
            regex.Append(FileSpecRegexParts.WildcardGroupStart);

            bool hasRecursiveOperatorAtStart = wildcardDir.Length > 2 && wildcardDir[0] == '*' && wildcardDir[1] == '*';

            if (hasRecursiveOperatorAtStart)
            {
                regex.Append(FileSpecRegexParts.LeftDirs);
            }
            int startIndex = LastIndexOfDirectoryOrRecursiveSequence(wildcardDir, 0);

            for (int i = startIndex; i < wildcardDir.Length; i = LastIndexOfDirectoryOrRecursiveSequence(wildcardDir, i + 1))
            {
                char ch = wildcardDir[i];
                bool isRecursiveOperator = i < wildcardDir.Length - 2 && wildcardDir[i + 1] == '*' && wildcardDir[i + 2] == '*';

                if (isRecursiveOperator)
                {
                    regex.Append(FileSpecRegexParts.MiddleDirs);
                }
                else
                {
                    AppendRegularExpressionFromChar(regex, ch);
                }
            }

            regex.Append(FileSpecRegexParts.GroupEnd);
        }

        /// <summary>
        /// Append the regex equivalents for character sequences in the filename part of a filespec:
        ///
        /// (1) Trailing dots in file names have to be treated specially.
        ///     We want:
        ///
        ///         *. to match foo
        ///
        ///     but 'foo' doesn't have a trailing '.' so we need to handle this while still being careful
        ///     not to match 'foo.txt' by modifying the generated regex for wildcard characters * and ?
        /// 
        /// (2) Common filespec characters
        ///
        /// (3) Ignore the .* portion of any *.* sequence when no trailing dot exists
        /// </summary>
        private static void AppendRegularExpressionFromFilename(ReuseableStringBuilder regex, string filename)
        {
            regex.Append(FileSpecRegexParts.FilenameGroupStart);

            bool hasTrailingDot = filename.Length > 0 && filename[filename.Length - 1] == '.';
            int partLength = hasTrailingDot ? filename.Length - 1 : filename.Length;

            for (int i = 0; i < partLength; i++)
            {
                char ch = filename[i];

                if (hasTrailingDot && ch == '*')
                {
                    regex.Append(FileSpecRegexParts.AnythingButDot);
                }
                else if (hasTrailingDot && ch == '?')
                {
                    regex.Append(FileSpecRegexParts.AnySingleCharacterButDot);
                }
                else
                {
                    AppendRegularExpressionFromChar(regex, ch);
                }

                if (!hasTrailingDot && i < partLength - 2 && ch == '*' && filename[i + 1] == '.' && filename[i + 2] == '*')
                {
                    i += 2;
                }
            }

            regex.Append(FileSpecRegexParts.GroupEnd);
            regex.Append(FileSpecRegexParts.EndOfLine);
        }

        /// <summary>
        /// Append the regex equivalents for characters common to all filespec parts.
        /// </summary>
        private static void AppendRegularExpressionFromChar(ReuseableStringBuilder regex, char ch)
        {
            if (ch == '*')
            {
                regex.Append(FileSpecRegexParts.AnyNonSeparator);
            }
            else if (ch == '?')
            {
                regex.Append(FileSpecRegexParts.SingleCharacter);
            }
            else if (FileUtilities.IsAnySlash(ch))
            {
                regex.Append(FileSpecRegexParts.DirSeparator);
            }
            else if (IsSpecialRegexCharacter(ch))
            {
                regex.Append('\\');
                regex.Append(ch);
            }
            else
            {
                regex.Append(ch);
            }
        }

        private static bool IsSpecialRegexCharacter(char ch) =>
            ch == '$' || ch == '(' || ch == ')' || ch == '+' || ch == '.'
            || ch == '[' || ch == '^' || ch == '{' || ch == '|';

        /// <summary>
        /// Given an index at a directory separator,
        /// iteratively skip to the end of two sequences:
        ///
        ///  (1) \.\ -> \
        ///     This is an identity, so for example, these two are equivalent,
        ///
        ///         dir1\.\dir2 == dir1\dir2
        /// 
        ///     (2) \\ -> \
        ///         Double directory separators are treated as a single directory separator,
        ///         so, for example, this is an identity:
        ///
        ///             f:\dir1\\dir2 == f:\dir1\dir2
        ///
        ///         The single exemption is for UNC path names, like this:
        ///
        ///             \\server\share != \server\share
        /// 
        ///         This case is handled by isUncPath in
        ///         a prior step.
        ///
        /// </summary>
        /// <returns>The last index of a directory sequence.</returns>
        private static int LastIndexOfDirectorySequence(string str, int startIndex)
        {
            if (startIndex >= str.Length || !FileUtilities.IsAnySlash(str[startIndex]))
            {
                return startIndex;
            }
            int i = startIndex;
            bool isSequenceEndFound = false;

            while (!isSequenceEndFound && i < str.Length)
            {
                bool isSeparator = i < str.Length - 1 && FileUtilities.IsAnySlash(str[i + 1]);
                bool isRelativeSeparator = i < str.Length - 2 && str[i + 1] == '.' && FileUtilities.IsAnySlash(str[i + 2]);

                if (isSeparator)
                {
                    i++;
                }
                else if (isRelativeSeparator)
                {
                    i += 2;
                }
                else
                {
                    isSequenceEndFound = true;
                }
            }

            return i;
        }

        /// <summary>
        /// Given an index at a directory separator or start of a recursive operator,
        /// iteratively skip to the end of three sequences:
        /// 
        /// (1), (2) Both sequences handled by IndexOfNextNonCollapsibleChar
        /// 
        /// (3) \**\**\ -> \**\
        ///              This is an identity, so for example, these two are equivalent,
        ///
        ///                 dir1\**\**\ == dir1\**\
        /// </summary>
        /// <returns>]
        /// If starting at a recursive operator, the last index of a recursive sequence.
        /// Otherwise, the last index of a directory sequence.
        /// </returns>
        private static int LastIndexOfDirectoryOrRecursiveSequence(string str, int startIndex)
        {
            bool isRecursiveSequence = startIndex < str.Length - 1
                                            && str[startIndex] == '*' && str[startIndex + 1] == '*';
            if (!isRecursiveSequence)
            {
                return LastIndexOfDirectorySequence(str, startIndex);
            }

            int i = startIndex + 2;
            bool isSequenceEndFound = false;

            while (!isSequenceEndFound && i < str.Length)
            {
                i = LastIndexOfDirectorySequence(str, i);
                bool isRecursiveOperator = i < str.Length - 2 && str[i + 1] == '*' && str[i + 2] == '*';

                if (isRecursiveOperator)
                {
                    i += 3;
                }
                else
                {
                    isSequenceEndFound = true;
                }
            }

            return i + 1;
        }

        /// <summary>
        /// Given a filespec, get the information needed for file matching. 
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="regexFileMatch">Receives the regular expression.</param>
        /// <param name="needsRecursion">Receives the flag that is true if recursion is required.</param>
        /// <param name="isLegalFileSpec">Receives the flag that is true if the filespec is legal.</param>
        internal void GetFileSpecInfoWithRegexObject(
            string filespec,
            out Regex regexFileMatch,
            out bool needsRecursion,
            out bool isLegalFileSpec)
        {
            GetFileSpecInfo(filespec,
                out string fixedDirectoryPart, out string wildcardDirectoryPart, out string filenamePart,
                out needsRecursion, out isLegalFileSpec);

            if (isLegalFileSpec)
            {
                string matchFileExpression = RegularExpressionFromFileSpec(fixedDirectoryPart, wildcardDirectoryPart, filenamePart);
                regexFileMatch = new Regex(matchFileExpression, DefaultRegexOptions);
            }
            else
            {
                regexFileMatch = null;
            }
        }

        internal delegate (string fixedDirectoryPart, string recursiveDirectoryPart, string fileNamePart) FixupParts(
            string fixedDirectoryPart,
            string recursiveDirectoryPart,
            string filenamePart);

        /// <summary>
        /// Given a filespec, parse it and construct the regular expression string.
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="fixedDirectoryPart">Receives the fixed directory part.</param>
        /// <param name="wildcardDirectoryPart">Receives the wildcard directory part.</param>
        /// <param name="filenamePart">Receives the filename part.</param>
        /// <param name="needsRecursion">Receives the flag that is true if recursion is required.</param>
        /// <param name="isLegalFileSpec">Receives the flag that is true if the filespec is legal.</param>
        /// <param name="fixupParts">hook method to further change the parts</param>
        internal void GetFileSpecInfo(
            string filespec,
            out string fixedDirectoryPart,
            out string wildcardDirectoryPart,
            out string filenamePart,
            out bool needsRecursion,
            out bool isLegalFileSpec,
            FixupParts fixupParts = null)
        {
            needsRecursion = false;
            fixedDirectoryPart = string.Empty;
            wildcardDirectoryPart = string.Empty;
            filenamePart = string.Empty;

            if (!RawFileSpecIsValid(filespec))
            {
                isLegalFileSpec = false;
                return;
            }

            /*
             * Now break up the filespec into constituent parts--fixed, wildcard and filename.
             */
            SplitFileSpec(filespec, out fixedDirectoryPart, out wildcardDirectoryPart, out filenamePart);

            if (fixupParts != null)
            {
                var newParts = fixupParts(fixedDirectoryPart, wildcardDirectoryPart, filenamePart);

                fixedDirectoryPart = newParts.fixedDirectoryPart;
                wildcardDirectoryPart = newParts.recursiveDirectoryPart;
                filenamePart = newParts.fileNamePart;
            }

            /*
             * Was the filespec valid? If not, then just return now.
             */
            isLegalFileSpec = IsLegalFileSpec(wildcardDirectoryPart, filenamePart);
            if (!isLegalFileSpec)
            {
                return;
            }

            /*
             * Determine whether recursion will be required.
             */
            needsRecursion = (wildcardDirectoryPart.Length != 0);
        }

        internal static bool RawFileSpecIsValid(string filespec)
        {
            // filespec cannot contain illegal characters
            if (-1 != filespec.IndexOfAny(s_invalidPathChars))
            {
                return false;
            }

            /*
             * Check for patterns in the filespec that are explicitly illegal.
             * 
             * Any path with "..." in it is illegal.
             */
            if (-1 != filespec.IndexOf("...", StringComparison.Ordinal))
            {
                return false;
            }

            /*
             * If there is a ':' anywhere but the second character, this is an illegal pattern.
             * Catches this case among others,
             * 
             *        http://www.website.com
             * 
             */
            int rightmostColon = filespec.LastIndexOf(":", StringComparison.Ordinal);

            if
            (
                -1 != rightmostColon
                && 1 != rightmostColon
            )
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// The results of a match between a filespec and a file name.
        /// </summary>
        internal sealed class Result
        {
            /// <summary>
            /// Default constructor.
            /// </summary>
            internal Result()
            {
                // do nothing
            }

            internal bool isLegalFileSpec; // initially false
            internal bool isMatch; // initially false
            internal bool isFileSpecRecursive; // initially false
            internal string wildcardDirectoryPart = string.Empty;
        }

        /// <summary>
        /// A wildcard (* and ?) matching algorithm that tests whether the input string matches against the pattern.
        /// </summary>
        /// <param name="input">String which is matched against the pattern.</param>
        /// <param name="pattern">Pattern against which string is matched.</param>
        internal static bool IsMatch(string input, string pattern)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            // Parameter lengths
            int patternLength = pattern.Length;
            int inputLength = input.Length;

            // Used to save the location when a * wildcard is found in the input string
            int patternTmpIndex = -1;
            int inputTmpIndex = -1;

            // Current indexes
            int patternIndex = 0;
            int inputIndex = 0;

            // Store the information whether the tail was checked when a pattern "*?" occurred
            bool tailChecked = false;

#if MONO    // MONO doesn't support local functions
            Func<char, char, int, int, bool> CompareIgnoreCase = (inputChar, patternChar, iIndex, pIndex) =>
#else
            // Function for comparing two characters, ignoring case
            // PERF NOTE:
            // Having a local function instead of a variable increases the speed by approx. 2 times.
            // Passing inputChar and patternChar increases the speed by approx. 10%, when comparing
            // to using the string indexer. The iIndex and pIndex parameters are only used
            // when we have to compare two non ASCII characters. Using just string.Compare for
            // character comparison, would reduce the speed by approx. 5 times.
            bool CompareIgnoreCase(char inputChar, char patternChar, int iIndex, int pIndex)
#endif
            {
                // We will mostly be comparing ASCII characters, check English letters first.
                char inputCharLower = (char)(inputChar | 0x20);
                if (inputCharLower >= 'a' && inputCharLower <= 'z')
                {
                    // This test covers all combinations of lower/upper as both sides are converted to lower case.
                    return inputCharLower == (patternChar | 0x20);
                }
                if (inputChar < 128 || patternChar < 128)
                {
                    // We don't need to compare, an ASCII character cannot have its lowercase/uppercase outside the ASCII table
                    // and a non ASCII character cannot have its lowercase/uppercase inside the ASCII table
                    return inputChar == patternChar;
                }
                return string.Compare(input, iIndex, pattern, pIndex, 1, StringComparison.OrdinalIgnoreCase) == 0;
            }
#if MONO
            ; // The end of the CompareIgnoreCase anonymous function
#endif

            while (inputIndex < inputLength)
            {
                if (patternIndex < patternLength)
                {
                    // Check if there is a * wildcard first as we can have it also in the input string
                    if (pattern[patternIndex] == '*')
                    {
                        // Skip all * wildcards if there are more than one
                        while (++patternIndex < patternLength && pattern[patternIndex] == '*') { }

                        // Return if the last character is a * wildcard
                        if (patternIndex >= patternLength)
                        {
                            return true;
                        }

                        // Mostly, we will be dealing with a file extension pattern e.g. "*.ext", so try to check the tail first
                        if (!tailChecked)
                        {
                            // Iterate from the end of the pattern to the current pattern index
                            // and hope that there is no * wildcard in order to return earlier
                            int inputTailIndex = inputLength;
                            int patternTailIndex = patternLength;
                            while (patternIndex < patternTailIndex && inputTailIndex > inputIndex)
                            {
                                patternTailIndex--;
                                inputTailIndex--;
                                // If we encountered a * wildcard we are not sure if it matches as there can be zero or more than one characters
                                // so we have to fallback to the standard procedure e.g. ("aaaabaaad", "*?b*d")
                                if (pattern[patternTailIndex] == '*')
                                {
                                    break;
                                }
                                // If the tail doesn't match, we can safely return e.g. ("aaa", "*b")
                                if (!CompareIgnoreCase(input[inputTailIndex], pattern[patternTailIndex], patternTailIndex, inputTailIndex) &&
                                    pattern[patternTailIndex] != '?')
                                {
                                    return false;
                                }
                                if (patternIndex == patternTailIndex)
                                {
                                    return true;
                                }
                            }
                            // Alter the lengths to the last valid match so that we don't need to match them again
                            inputLength = inputTailIndex + 1;
                            patternLength = patternTailIndex + 1;
                            tailChecked = true; // Make sure that the tail is checked only once
                        }

                        // Skip to the first character that matches after the *, e.g. ("abcd", "*d")
                        // The ? wildcard cannot be skipped as we will have a wrong result for e.g. ("aab" "*?b")
                        if (pattern[patternIndex] != '?')
                        {
                            while (!CompareIgnoreCase(input[inputIndex], pattern[patternIndex], inputIndex, patternIndex))
                            {
                                // Return if there is no character that match e.g. ("aa", "*b")
                                if (++inputIndex >= inputLength)
                                {
                                    return false;
                                }
                            }
                        }
                        patternTmpIndex = patternIndex;
                        inputTmpIndex = inputIndex;
                        continue;
                    }

                    // If we have a match, step to the next character
                    if (CompareIgnoreCase(input[inputIndex], pattern[patternIndex], inputIndex, patternIndex) ||
                        pattern[patternIndex] == '?')
                    {
                        patternIndex++;
                        inputIndex++;
                        continue;
                    }
                }
                // No match found, if we didn't found a location of a * wildcard, return false e.g. ("ab", "?ab")
                // otherwise set the location after the previous * wildcard and try again with the next character in the input
                if (patternTmpIndex < 0)
                {
                    return false;
                }
                patternIndex = patternTmpIndex;
                inputIndex = inputTmpIndex++;
            }
            // When we reach the end of the input we have to skip all * wildcards as they match also zero characters
            while (patternIndex < patternLength && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }
            return patternIndex >= patternLength;
        }

        /// <summary>
        /// Given a pattern (filespec) and a candidate filename (fileToMatch)
        /// return matching information.
        /// </summary>
        /// <param name="filespec">The filespec.</param>
        /// <param name="fileToMatch">The candidate to match against.</param>
        /// <returns>The result class.</returns>
        internal Result FileMatch
        (
            string filespec,
            string fileToMatch
        )
        {
            Result matchResult = new Result();

            fileToMatch = GetLongPathName(fileToMatch, _getFileSystemEntries);

            Regex regexFileMatch;
            GetFileSpecInfoWithRegexObject
            (
                filespec,
                out regexFileMatch,
                out matchResult.isFileSpecRecursive,
                out matchResult.isLegalFileSpec
            );

            if (matchResult.isLegalFileSpec)
            {
                GetRegexMatchInfo(
                    fileToMatch,
                    regexFileMatch,
                    out matchResult.isMatch,
                    out matchResult.wildcardDirectoryPart,
                    out _);
            }

            return matchResult;
        }

        internal static void GetRegexMatchInfo(
            string fileToMatch,
            Regex fileSpecRegex,
            out bool isMatch,
            out string wildcardDirectoryPart,
            out string filenamePart)
        {
            Match match = fileSpecRegex.Match(fileToMatch);

            isMatch = match.Success;
            wildcardDirectoryPart = string.Empty;
            filenamePart = string.Empty;

            if (isMatch)
            {
                wildcardDirectoryPart = match.Groups["WILDCARDDIR"].Value;
                filenamePart = match.Groups["FILENAME"].Value;
            }
        }

        class TaskOptions
        {
            public TaskOptions(int maxTasks)
            {
                MaxTasks = maxTasks;
            }
            /// <summary>
            /// The maximum number of tasks that are allowed to run concurrently
            /// </summary>
            public readonly int MaxTasks;

            /// <summary>
            /// The number of currently available tasks
            /// </summary>
            public int AvailableTasks;

            /// <summary>
            /// The maximum number of tasks that Parallel.ForEach may use
            /// </summary>
            public int MaxTasksPerIteration;
        }

        /// <summary>
        /// Given a filespec, find the files that match. 
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="projectDirectoryUnescaped">The project directory.</param>
        /// <param name="filespecUnescaped">Get files that match the given file spec.</param>
        /// <param name="excludeSpecsUnescaped">Exclude files that match this file spec.</param>
        /// <returns>The array of files.</returns>
        internal string[] GetFiles
            (
            string projectDirectoryUnescaped,
            string filespecUnescaped,
            List<string> excludeSpecsUnescaped = null
            )
        {
            // For performance. Short-circuit iff there is no wildcard.
            if (!HasWildcards(filespecUnescaped))
            {
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }

            if (_cachedGlobExpansions == null)
            {
                return GetFilesImplementation(
                    projectDirectoryUnescaped,
                    filespecUnescaped,
                    excludeSpecsUnescaped);
            }

            var enumerationKey = ComputeFileEnumerationCacheKey(projectDirectoryUnescaped, filespecUnescaped, excludeSpecsUnescaped);

            IReadOnlyList<string> files;
            if (!_cachedGlobExpansions.TryGetValue(enumerationKey, out files))
            {
                // avoid parallel evaluations of the same wildcard by using a unique lock for each wildcard
                object locks = _cachedGlobExpansionsLock.Value.GetOrAdd(enumerationKey, _ => new object());
                lock (locks)
                {
                    if (!_cachedGlobExpansions.TryGetValue(enumerationKey, out files))
                    {
                        files =
                            _cachedGlobExpansions.GetOrAdd(
                                enumerationKey,
                                (_) =>
                                    GetFilesImplementation(
                                        projectDirectoryUnescaped,
                                        filespecUnescaped,
                                        excludeSpecsUnescaped));
                    }
                }
            }

            // Copy the file enumerations to prevent outside modifications of the cache (e.g. sorting, escaping) and to maintain the original method contract that a new array is created on each call.
            var filesToReturn = files.ToArray();

            return filesToReturn;
        }

        private static string ComputeFileEnumerationCacheKey(string projectDirectoryUnescaped, string filespecUnescaped, List<string> excludes)
        {
            Debug.Assert(projectDirectoryUnescaped != null);
            Debug.Assert(filespecUnescaped != null);
            Debug.Assert(Path.IsPathRooted(projectDirectoryUnescaped));

            const string projectPathPrependedToken = "p";
            const string pathValityExceptionTriggeredToken = "e";

            var excludeSize = 0;

            if (excludes != null)
            {
                foreach (var exclude in excludes)
                {
                    excludeSize += exclude.Length;
                }
            }

            using (var sb = new ReuseableStringBuilder(projectDirectoryUnescaped.Length + filespecUnescaped.Length + excludeSize))
            {
                var pathValidityExceptionTriggered = false;

                try
                {
                    // Ideally, ensure that the cache key is an absolute, normalized path so that other projects evaluating an equivalent glob can get a hit.
                    // Corollary caveat: including the project directory when the glob is independent of it leads to cache misses

                    var filespecUnescapedFullyQualified = Path.Combine(projectDirectoryUnescaped, filespecUnescaped);

                    if (filespecUnescapedFullyQualified.Equals(filespecUnescaped, StringComparison.Ordinal))
                    {
                        // filespec is absolute, don't include the project directory path
                        sb.Append(filespecUnescaped);
                    }
                    else
                    {
                        // filespec is not absolute, include the project directory path
                        // differentiate fully qualified filespecs vs relative filespecs that got prepended with the project directory
                        sb.Append(projectPathPrependedToken);
                        sb.Append(filespecUnescapedFullyQualified);
                    }

                    // increase the chance of cache hits when multiple relative globs refer to the same base directory
                    // todo https://github.com/Microsoft/msbuild/issues/3889
                    //if (FileUtilities.ContainsRelativePathSegments(filespecUnescaped))
                    //{
                    //    filespecUnescaped = FileUtilities.GetFullPathNoThrow(filespecUnescaped);
                    //}
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    pathValidityExceptionTriggered = true;
                }

                if (pathValidityExceptionTriggered)
                {
                    sb.Append(pathValityExceptionTriggeredToken);
                    sb.Append(projectPathPrependedToken);
                    sb.Append(projectDirectoryUnescaped);
                    sb.Append(filespecUnescaped);
                }

                if (excludes != null)
                {
                    foreach (var exclude in excludes)
                    {
                        sb.Append(exclude);
                    }
                }

                return sb.ToString();
            }
        }

        enum SearchAction
        {
            RunSearch,
            ReturnFileSpec,
            ReturnEmptyList,
        }

        private SearchAction GetFileSearchData(
            string projectDirectoryUnescaped,
            string filespecUnescaped,
            out bool stripProjectDirectory,
            out RecursionState result)
        {
            stripProjectDirectory = false;
            result = new RecursionState();

            GetFileSpecInfo
            (
                filespecUnescaped,
                out string fixedDirectoryPart,
                out string wildcardDirectoryPart,
                out string filenamePart,
                out bool needsRecursion,
                out bool isLegalFileSpec
            );

            /*
             * If the filespec is invalid, then just return now.
             */
            if (!isLegalFileSpec)
            {
                return SearchAction.ReturnFileSpec;
            }

            // The projectDirectory is not null only if we are running the evaluation from
            // inside the engine (i.e. not from a task)
            string oldFixedDirectoryPart = fixedDirectoryPart;
            if (projectDirectoryUnescaped != null)
            {
                if (fixedDirectoryPart != null)
                {
                    try
                    {
                        fixedDirectoryPart = Path.Combine(projectDirectoryUnescaped, fixedDirectoryPart);
                    }
                    catch (ArgumentException)
                    {
                        return SearchAction.ReturnEmptyList;
                    }

                    stripProjectDirectory = !string.Equals(fixedDirectoryPart, oldFixedDirectoryPart, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    fixedDirectoryPart = projectDirectoryUnescaped;
                    stripProjectDirectory = true;
                }
            }

            /*
             * If the fixed directory part doesn't exist, then this means no files should be
             * returned.
             */
            if (fixedDirectoryPart.Length > 0 && !_fileSystem.DirectoryExists(fixedDirectoryPart))
            {
                return SearchAction.ReturnEmptyList;
            }

            string directoryPattern = null;
            if (wildcardDirectoryPart.Length > 0)
            {
                // If the wildcard directory part looks like "**/{pattern}/**", we are essentially looking for files that have
                // a matching directory anywhere on their path. This is commonly used when excluding hidden directories using
                // "**/.*/**" for example, and is worth special-casing so it doesn't fall into the slow regex logic.
                string wildcard = wildcardDirectoryPart.TrimTrailingSlashes();
                int wildcardLength = wildcard.Length;
                if (wildcardLength > 6 &&
                    wildcard[0] == '*' &&
                    wildcard[1] == '*' &&
                    FileUtilities.IsAnySlash(wildcard[2]) &&
                    FileUtilities.IsAnySlash(wildcard[wildcardLength - 3]) &&
                    wildcard[wildcardLength - 2] == '*' &&
                    wildcard[wildcardLength - 1] == '*')
                {
                    // Check that there are no other slashes in the wildcard.
                    if (wildcard.IndexOfAny(FileUtilities.Slashes, 3, wildcardLength - 6) == -1)
                    {
                        directoryPattern = wildcard.Substring(3, wildcardLength - 6);
                    }
                }
            }

            // determine if we need to use the regular expression to match the files
            // PERF NOTE: Constructing a Regex object is expensive, so we avoid it whenever possible
            bool matchWithRegex =
                // if we have a directory specification that uses wildcards, and
                (wildcardDirectoryPart.Length > 0) &&
                // the directory pattern is not a simple "**/{pattern}/**", and
                directoryPattern == null &&
                // the specification is not a simple "**"
                !IsRecursiveDirectoryMatch(wildcardDirectoryPart);
            // then we need to use the regular expression

            var searchData = new FilesSearchData(
                // if using the regular expression, ignore the file pattern
                matchWithRegex ? null : filenamePart,
                directoryPattern,
                // if using the file pattern, ignore the regular expression
                matchWithRegex ? new Regex(RegularExpressionFromFileSpec(oldFixedDirectoryPart, wildcardDirectoryPart, filenamePart), RegexOptions.IgnoreCase) : null,
                needsRecursion);

            result.SearchData = searchData;
            result.BaseDirectory = Normalize(fixedDirectoryPart);
            result.RemainingWildcardDirectory = Normalize(wildcardDirectoryPart);

            return SearchAction.RunSearch;
        }

        /// <summary>
        /// Replace all slashes to the OS slash, collapse multiple slashes into one, trim trailing slashes
        /// </summary>
        /// <param name="aString">A string</param>
        /// <returns>The normalized string</returns>
        internal static string Normalize(string aString)
        {
            if (string.IsNullOrEmpty(aString))
            {
                return aString;
            }

            var sb = new StringBuilder(aString.Length);
            var index = 0;

            // preserve meaningful roots and their slashes
            if (aString.Length >= 2 && aString[1] == ':' && IsValidDriveChar(aString[0]))
            {
                sb.Append(aString[0]);
                sb.Append(aString[1]);

                var i = SkipSlashes(aString, 2);

                if (index != i)
                {
                    sb.Append('\\');
                }

                index = i;
            }
            else if (aString.StartsWith("/", StringComparison.Ordinal))
            {
                sb.Append('/');
                index = SkipSlashes(aString, 1);
            }
            else if (aString.StartsWith(@"\\", StringComparison.Ordinal))
            {
                sb.Append(@"\\");
                index = SkipSlashes(aString, 2);
            }
            else if (aString.StartsWith(@"\", StringComparison.Ordinal))
            {
                sb.Append(@"\");
                index = SkipSlashes(aString, 1);
            }

            while (index < aString.Length)
            {
                var afterSlashesIndex = SkipSlashes(aString, index);

                // do not append separator at the end of the string
                if (afterSlashesIndex >= aString.Length)
                {
                    break;
                }
                // replace multiple slashes with the OS separator
                else if (afterSlashesIndex > index)
                {
                    sb.Append(s_directorySeparator);
                }

                // skip non-slashes
                var indexOfAnySlash = aString.IndexOfAny(directorySeparatorCharacters, afterSlashesIndex);
                var afterNonSlashIndex = indexOfAnySlash == -1 ? aString.Length : indexOfAnySlash;

                sb.Append(aString, afterSlashesIndex, afterNonSlashIndex - afterSlashesIndex);

                index = afterNonSlashIndex;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Skips slash characters in a string.
        /// </summary>
        /// <param name="aString">The working string</param>
        /// <param name="startingIndex">Offset in string to start the search in</param>
        /// <returns>First index that is not a slash. Returns the string's length if end of string is reached</returns>
        private static int SkipSlashes(string aString, int startingIndex)
        {
            var index = startingIndex;

            while (index < aString.Length && FileUtilities.IsAnySlash(aString[index]))
            {
                index++;
            }

            return index;
        }

        // copied from https://github.com/dotnet/corefx/blob/master/src/Common/src/System/IO/PathInternal.Windows.cs#L77-L83
        /// <summary>
        /// Returns true if the given character is a valid drive letter
        /// </summary>
        private static bool IsValidDriveChar(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }

        private static string[] CreateArrayWithSingleItemIfNotExcluded(string filespecUnescaped, List<string> excludeSpecsUnescaped)
        {
            if (excludeSpecsUnescaped != null)
            {
                foreach (string excludeSpec in excludeSpecsUnescaped)
                {
                    // Try a path equality check first to:
                    // - avoid the expensive regex
                    // - maintain legacy behaviour where an illegal filespec is treated as a normal string
                    if (FileUtilities.PathsEqual(filespecUnescaped, excludeSpec))
                    {
                        return Array.Empty<string>();
                    }

                    var match = Default.FileMatch(excludeSpec, filespecUnescaped);

                    if (match.isLegalFileSpec && match.isMatch)
                    {
                        return Array.Empty<string>();
                    }
                }
            }
            return new[] { filespecUnescaped };
        }

        /// <summary>
        /// Given a filespec, find the files that match. 
        /// Will never throw IO exceptions: if there is no match, returns the input verbatim.
        /// </summary>
        /// <param name="projectDirectoryUnescaped">The project directory.</param>
        /// <param name="filespecUnescaped">Get files that match the given file spec.</param>
        /// <param name="excludeSpecsUnescaped">Exclude files that match this file spec.</param>
        /// <returns>The array of files.</returns>
        private string[] GetFilesImplementation(
            string projectDirectoryUnescaped,
            string filespecUnescaped,
            List<string> excludeSpecsUnescaped)
        {
            // UNDONE (perf): Short circuit the complex processing when we only have a path and a wildcarded filename

            /*
             * Analyze the file spec and get the information we need to do the matching.
             */
            var action = GetFileSearchData(projectDirectoryUnescaped, filespecUnescaped,
                out bool stripProjectDirectory, out RecursionState state);

            if (action == SearchAction.ReturnEmptyList)
            {
                return Array.Empty<string>();
            }
            else if (action == SearchAction.ReturnFileSpec)
            {
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }
            else if (action != SearchAction.RunSearch)
            {
                //  This means the enum value wasn't valid (or a new one was added without updating code correctly)
                throw new NotSupportedException(action.ToString());
            }

            List<RecursionState> searchesToExclude = null;

            //  Exclude searches which will become active when the recursive search reaches their BaseDirectory.
            //  The BaseDirectory of the exclude search is the key for this dictionary.
            Dictionary<string, List<RecursionState>> searchesToExcludeInSubdirs = null;

            HashSet<string> resultsToExclude = null;
            if (excludeSpecsUnescaped != null)
            {
                searchesToExclude = new List<RecursionState>();
                foreach (string excludeSpec in excludeSpecsUnescaped)
                {
                    //  This is ignored, we always use the include pattern's value for stripProjectDirectory
                    var excludeAction = GetFileSearchData(projectDirectoryUnescaped, excludeSpec,
                        out _, out RecursionState excludeState);

                    if (excludeAction == SearchAction.ReturnFileSpec)
                    {
                        if (resultsToExclude == null)
                        {
                            resultsToExclude = new HashSet<string>();
                        }
                        resultsToExclude.Add(excludeSpec);

                        continue;
                    }
                    else if (excludeAction == SearchAction.ReturnEmptyList)
                    {
                        //  Nothing to do
                        continue;
                    }
                    else if (excludeAction != SearchAction.RunSearch)
                    {
                        //  This means the enum value wasn't valid (or a new one was added without updating code correctly)
                        throw new NotSupportedException(excludeAction.ToString());
                    }

                    var excludeBaseDirectory = excludeState.BaseDirectory;
                    var includeBaseDirectory = state.BaseDirectory;

                    if (!string.Equals(excludeBaseDirectory, includeBaseDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        //  What to do if the BaseDirectory for the exclude search doesn't match the one for inclusion?
                        //  - If paths don't match (one isn't a prefix of the other), then ignore the exclude search.  Examples:
                        //      - c:\Foo\ - c:\Bar\
                        //      - c:\Foo\Bar\ - C:\Foo\Baz\
                        //      - c:\Foo\ - c:\Foo2\
                        if (excludeBaseDirectory.Length == includeBaseDirectory.Length)
                        {
                            //  Same length, but different paths.  Ignore this exclude search
                            continue;
                        }
                        else if (excludeBaseDirectory.Length > includeBaseDirectory.Length)
                        {
                            if (!IsSubdirectoryOf(excludeBaseDirectory, includeBaseDirectory))
                            {
                                //  Exclude path is longer, but doesn't start with include path.  So ignore it.
                                continue;
                            }

                            //  - The exclude BaseDirectory is somewhere under the include BaseDirectory. So
                            //    keep the exclude search, but don't do any processing on it while recursing until the baseDirectory
                            //    in the recursion matches the exclude BaseDirectory.  Examples:
                            //      - Include - Exclude
                            //      - C:\git\msbuild\ - c:\git\msbuild\obj\
                            //      - C:\git\msbuild\ - c:\git\msbuild\src\Common\

                            if (searchesToExcludeInSubdirs == null)
                            {
                                searchesToExcludeInSubdirs = new Dictionary<string, List<RecursionState>>(StringComparer.OrdinalIgnoreCase);
                            }
                            List<RecursionState> listForSubdir;
                            if (!searchesToExcludeInSubdirs.TryGetValue(excludeBaseDirectory, out listForSubdir))
                            {
                                listForSubdir = new List<RecursionState>();

                                searchesToExcludeInSubdirs[excludeBaseDirectory] = listForSubdir;
                            }
                            listForSubdir.Add(excludeState);
                        }
                        else
                        {
                            //  Exclude base directory length is less than include base directory length.
                            if (!IsSubdirectoryOf(state.BaseDirectory, excludeState.BaseDirectory))
                            {
                                //  Include path is longer, but doesn't start with the exclude path.  So ignore exclude path
                                //  (since it won't match anything under the include path)
                                continue;
                            }

                            //  Now check the wildcard part
                            if (excludeState.RemainingWildcardDirectory.Length == 0)
                            {
                                //  The wildcard part is empty, so ignore the exclude search, as it's looking for files non-recursively
                                //  in a folder higher up than the include baseDirectory.
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.cs" exclude="c:\git\msbuild\*.cs"
                                continue;
                            }
                            else if (IsRecursiveDirectoryMatch(excludeState.RemainingWildcardDirectory))
                            {
                                //  The wildcard part is exactly "**\", so the exclude pattern will apply to everything in the include
                                //  pattern, so simply update the exclude's BaseDirectory to be the same as the include baseDirectory
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.*" exclude="c:\git\msbuild\**\*.bak"
                                excludeState.BaseDirectory = state.BaseDirectory;
                                searchesToExclude.Add(excludeState);
                            }
                            else
                            {
                                //  The wildcard part is non-empty and not "**\", so we will need to match it with a Regex.  Fortunately
                                //  these conditions mean that it needs to be matched with a Regex anyway, so here we will update the
                                //  BaseDirectory to be the same as the exclude BaseDirectory, and change the wildcard part to be "**\"
                                //  because we don't know where the different parts of the exclude wildcard part would be matched.
                                //  Example: include="c:\git\msbuild\src\Framework\**\*.*" exclude="c:\git\msbuild\**\bin\**\*.*"
                                Debug.Assert(excludeState.SearchData.RegexFileMatch != null || excludeState.SearchData.DirectoryPattern != null,
                                    "Expected Regex or directory pattern to be used for exclude file matching");
                                excludeState.BaseDirectory = state.BaseDirectory;
                                excludeState.RemainingWildcardDirectory = recursiveDirectoryMatch + s_directorySeparator;
                                searchesToExclude.Add(excludeState);
                            }
                        }
                    }
                    else
                    {
                        // Optimization: ignore excludes whose file names can never match our filespec. For example, if we're looking
                        // for "**/*.cs", we don't have to worry about excluding "{anything}/*.sln" as the intersection of the two will
                        // always be empty.
                        string includeFilespec = state.SearchData.Filespec ?? string.Empty;
                        string excludeFilespec = excludeState.SearchData.Filespec ?? string.Empty;
                        int compareLength = Math.Min(
                            includeFilespec.Length - includeFilespec.LastIndexOfAny(s_wildcardCharacters) - 1,
                            excludeFilespec.Length - excludeFilespec.LastIndexOfAny(s_wildcardCharacters) - 1);
                        if (string.Compare(
                                includeFilespec,
                                includeFilespec.Length - compareLength,
                                excludeFilespec,
                                excludeFilespec.Length - compareLength,
                                compareLength,
                                StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            // The suffix is the same so there is a possibility that the two will match the same files.
                            searchesToExclude.Add(excludeState);
                        }
                    }
                }
            }

            if (searchesToExclude?.Count == 0)
            {
                searchesToExclude = null;
            }

            /*
             * Even though we return a string[] we work internally with a ConcurrentStack.
             * This is because it's cheaper to add items to a ConcurrentStack and this code
             * might potentially do a lot of that.
             */
            var listOfFiles = new ConcurrentStack<List<string>>();

            /*
             * Now get the files that match, starting at the lowest fixed directory.
             */
            try
            {
                // Setup the values for calculating the MaxDegreeOfParallelism option of Parallel.ForEach
                // Set to use only half processors when we have 4 or more of them, in order to not be too aggresive
                // By setting MaxTasksPerIteration to the maximum amount of tasks, which means that only one
                // Parallel.ForEach will run at once, we get a stable number of threads being created.
                var maxTasks = Math.Max(1, NativeMethodsShared.GetLogicalCoreCount() / 2);
                var taskOptions = new TaskOptions(maxTasks)
                {
                    AvailableTasks = maxTasks,
                    MaxTasksPerIteration = maxTasks
                };
                GetFilesRecursive(
                    listOfFiles,
                    state,
                    projectDirectoryUnescaped,
                    stripProjectDirectory,
                    searchesToExclude,
                    searchesToExcludeInSubdirs,
                    taskOptions);
            }
            // Catch exceptions that are thrown inside the Parallel.ForEach
            catch (AggregateException ex)
            {
                // Flatten to get exceptions than are thrown inside a nested Parallel.ForEach
                if (ex.Flatten().InnerExceptions.All(ex => ExceptionHandling.IsIoRelatedException(ex) && !(ex is FileLoadException)))
                {
                    return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
                }
                throw;
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex) && !(ex is FileLoadException))
            {
                // Assume it's not meant to be a path
                return CreateArrayWithSingleItemIfNotExcluded(filespecUnescaped, excludeSpecsUnescaped);
            }

            /*
             * Build the return array.
             */
            var files = resultsToExclude != null
                ? listOfFiles.SelectMany(list => list).Where(f => !resultsToExclude.Contains(f)).ToArray()
                : listOfFiles.SelectMany(list => list).ToArray();

            return files;
        }

        private static bool IsSubdirectoryOf(string possibleChild, string possibleParent)
        {
            if (possibleParent == string.Empty)
            {
                // Something is always possibly a child of nothing
                return true;
            }

            bool prefixMatch = possibleChild.StartsWith(possibleParent, StringComparison.OrdinalIgnoreCase);
            if (!prefixMatch)
            {
                return false;
            }

            // Ensure that the prefix match wasn't to a distinct directory, so that
            // x\y\prefix doesn't falsely match x\y\prefixmatch.
            if (directorySeparatorCharacters.Contains(possibleParent[possibleParent.Length-1]))
            {
                return true;
            }
            else
            {
                return directorySeparatorCharacters.Contains(possibleChild[possibleParent.Length]);
            }
        }

        /// <summary>
        /// Returns true if the last component of the given directory path (assumed to not have any trailing slashes)
        /// matches the given pattern.
        /// </summary>
        /// <param name="directoryPath">The path to test.</param>
        /// <param name="pattern">The pattern to test against.</param>
        /// <returns>True in case of a match (e.g. directoryPath = "dir/subdir" and pattern = "s*"), false otherwise.</returns>
        private static bool DirectoryEndsWithPattern(string directoryPath, string pattern)
        {
            int index = directoryPath.LastIndexOfAny(FileUtilities.Slashes);
            return (index != -1 && IsMatch(directoryPath.Substring(index + 1), pattern));
        }

        /// <summary>
        /// Returns true if <paramref name="pattern"/> is <code>*</code> or <code>*.*</code>.
        /// </summary>
        /// <param name="pattern">The filename pattern to check.</param>
        private static bool IsAllFilesWildcard(string pattern) => pattern?.Length switch
        {
            1 => pattern[0] == '*',
            3 => pattern[0] == '*' && pattern[1] == '.' && pattern[2] == '*',
            _ => false
        };

        internal static bool IsRecursiveDirectoryMatch(string path) => path.TrimTrailingSlashes() == recursiveDirectoryMatch;
    }
}
