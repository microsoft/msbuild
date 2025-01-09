// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
#if !CLR2COMPATIBILITY
using System.Collections.Concurrent;
#else
using Microsoft.Build.Shared.Concurrent;
#endif
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class contains utility methods for file IO.
    /// PERF\COVERAGE NOTE: Try to keep classes in 'shared' as granular as possible. All the methods in
    /// each class get pulled into the resulting assembly.
    /// </summary>
    internal static partial class FileUtilities
    {
        // A list of possible test runners. If the program running has one of these substrings in the name, we assume
        // this is a test harness.

        // This flag, when set, indicates that we are running tests. Initially assume it's true. It also implies that
        // the currentExecutableOverride is set to a path (that is non-null). Assume this is not initialized when we
        // have the impossible combination of runningTests = false and currentExecutableOverride = null.

        // This is the fake current executable we use in case we are running tests.

        /// <summary>
        /// The directory where MSBuild stores cache information used during the build.
        /// </summary>
        internal static string cacheDirectory = null;

#if CLR2COMPATIBILITY
        internal static string TempFileDirectory => Path.GetTempPath();
#endif

        /// <summary>
        /// FOR UNIT TESTS ONLY
        /// Clear out the static variable used for the cache directory so that tests that
        /// modify it can validate their modifications.
        /// </summary>
        internal static void ClearCacheDirectoryPath()
        {
            cacheDirectory = null;
        }

        internal static readonly StringComparison PathComparison = GetIsFileSystemCaseSensitive() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        internal static readonly StringComparer PathComparer = GetIsFileSystemCaseSensitive() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Determines whether the file system is case sensitive.
        /// Copied from https://github.com/dotnet/runtime/blob/73ba11f3015216b39cb866d9fb7d3d25e93489f2/src/libraries/Common/src/System/IO/PathInternal.CaseSensitivity.cs#L41-L59
        /// </summary>
        public static bool GetIsFileSystemCaseSensitive()
        {
            try
            {
                string pathWithUpperCase = Path.Combine(Path.GetTempPath(), "CASESENSITIVETEST" + Guid.NewGuid().ToString("N"));
                using (new FileStream(pathWithUpperCase, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 0x1000, FileOptions.DeleteOnClose))
                {
                    string lowerCased = pathWithUpperCase.ToLowerInvariant();
                    return !File.Exists(lowerCased);
                }
            }
            catch (Exception exc)
            {
                // In case something goes terribly wrong, we don't want to fail just because
                // of a casing test, so we assume case-insensitive-but-preserving.
                Debug.Fail("Casing test failed: " + exc);
                return false;
            }
        }

        /// <summary>
        /// Copied from https://github.com/dotnet/corefx/blob/056715ff70e14712419d82d51c8c50c54b9ea795/src/Common/src/System/IO/PathInternal.Windows.cs#L61
        /// MSBuild should support the union of invalid path chars across the supported OSes, so builds can have the same behaviour crossplatform: https://github.com/dotnet/msbuild/issues/781#issuecomment-243942514
        /// </summary>
        internal static readonly char[] InvalidPathChars = new char[]
        {
            '|', '\0',
            (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
            (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
            (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
            (char)31
        };

        /// <summary>
        /// Copied from https://github.com/dotnet/corefx/blob/387cf98c410bdca8fd195b28cbe53af578698f94/src/System.Runtime.Extensions/src/System/IO/Path.Windows.cs#L18
        /// MSBuild should support the union of invalid path chars across the supported OSes, so builds can have the same behaviour crossplatform: https://github.com/dotnet/msbuild/issues/781#issuecomment-243942514
        /// </summary>
        internal static readonly char[] InvalidFileNameChars = new char[]
        {
            '\"', '<', '>', '|', '\0',
            (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
            (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
            (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
            (char)31, ':', '*', '?', '\\', '/'
        };

        internal static readonly char[] Slashes = { '/', '\\' };

        internal static readonly string DirectorySeparatorString = Path.DirectorySeparatorChar.ToString();

        private static readonly ConcurrentDictionary<string, bool> FileExistenceCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static readonly IFileSystem DefaultFileSystem = FileSystems.Default;

        /// <summary>
        /// Retrieves the MSBuild runtime cache directory
        /// </summary>
        internal static string GetCacheDirectory()
        {
            if (cacheDirectory == null)
            {
                cacheDirectory = Path.Combine(TempFileDirectory, string.Format(CultureInfo.CurrentUICulture, "MSBuild{0}-{1}", EnvironmentUtilities.CurrentProcessId, AppDomain.CurrentDomain.Id));
            }

            return cacheDirectory;
        }

        /// <summary>
        /// Get the hex hash string for the string
        /// </summary>
        internal static string GetHexHash(string stringToHash)
        {
            return stringToHash.GetHashCode().ToString("X", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Get the hash for the assemblyPaths
        /// </summary>
        internal static int GetPathsHash(IEnumerable<string> assemblyPaths)
        {
            StringBuilder builder = new StringBuilder();

            foreach (string path in assemblyPaths)
            {
                if (path != null)
                {
                    string directoryPath = path.Trim();
                    if (directoryPath.Length > 0)
                    {
                        DateTime lastModifiedTime;
                        if (NativeMethodsShared.GetLastWriteDirectoryUtcTime(directoryPath, out lastModifiedTime))
                        {
                            builder.Append(lastModifiedTime.Ticks);
                            builder.Append('|');
                            builder.Append(directoryPath.ToUpperInvariant());
                            builder.Append('|');
                        }
                    }
                }
            }

            return builder.ToString().GetHashCode();
        }

        /// <summary>
        /// Returns whether MSBuild can write to the given directory. Throws for PathTooLongExceptions
        /// but not other exceptions.
        /// </summary>
        internal static bool CanWriteToDirectory(string directory)
        {
            try
            {
                string testFilePath = Path.Combine(directory, $"MSBuild_{Guid.NewGuid().ToString("N")}_testFile.txt");
                FileInfo file = new(testFilePath);
                file.Directory.Create(); // If the directory already exists, this method does nothing.
                File.WriteAllText(testFilePath, $"MSBuild process {EnvironmentUtilities.CurrentProcessId} successfully wrote to file.");
                File.Delete(testFilePath);
                return true;
            }
            catch (PathTooLongException)
            {
                ErrorUtilities.ThrowArgument("DebugPathTooLong", directory);
                return false; // Should never reach here.
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Clears the MSBuild runtime cache
        /// </summary>
        internal static void ClearCacheDirectory()
        {
            string cacheDirectory = GetCacheDirectory();

            if (DefaultFileSystem.DirectoryExists(cacheDirectory))
            {
                DeleteDirectoryNoThrow(cacheDirectory, true);
            }
        }

        /// <summary>
        /// If the given path doesn't have a trailing slash then add one.
        /// If the path is an empty string, does not modify it.
        /// </summary>
        /// <param name="fileSpec">The path to check.</param>
        /// <returns>A path with a slash.</returns>
        internal static string EnsureTrailingSlash(string fileSpec)
        {
            fileSpec = FixFilePath(fileSpec);
            if (fileSpec.Length > 0 && !IsSlash(fileSpec[fileSpec.Length - 1]))
            {
                fileSpec += Path.DirectorySeparatorChar;
            }

            return fileSpec;
        }

        /// <summary>
        /// Ensures the path does not have a leading or trailing slash after removing the first 'start' characters.
        /// </summary>
        internal static string EnsureNoLeadingOrTrailingSlash(string path, int start)
        {
            int stop = path.Length;
            while (start < stop && IsSlash(path[start]))
            {
                start++;
            }
            while (start < stop && IsSlash(path[stop - 1]))
            {
                stop--;
            }

            return FixFilePath(path.Substring(start, stop - start));
        }

        /// <summary>
        /// Ensures the path does not have a leading slash after removing the first 'start' characters but does end in a slash.
        /// </summary>
        internal static string EnsureTrailingNoLeadingSlash(string path, int start)
        {
            int stop = path.Length;
            while (start < stop && IsSlash(path[start]))
            {
                start++;
            }

            return FixFilePath(start < stop && IsSlash(path[stop - 1]) ?
                path.Substring(start) :
                path.Substring(start) + Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Ensures the path does not have a trailing slash.
        /// </summary>
        internal static string EnsureNoTrailingSlash(string path)
        {
            path = FixFilePath(path);
            if (EndsWithSlash(path))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return path;
        }

        /// <summary>
        /// Ensures the path is enclosed within single quotes.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>The path enclosed by quotes.</returns>
        internal static string EnsureSingleQuotes(string path)
        {
            return EnsureQuotes(path);
        }

        /// <summary>
        /// Ensures the path is enclosed within double quotes.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>The path enclosed by quotes.</returns>
        internal static string EnsureDoubleQuotes(string path)
        {
            return EnsureQuotes(path, isSingleQuote: false);
        }

        /// <summary>
        /// Ensures the path is enclosed within quotes.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <param name="isSingleQuote">Indicates if single or double quotes should be used</param>
        /// <returns>The path enclosed by quotes.</returns>
        internal static string EnsureQuotes(string path, bool isSingleQuote = true)
        {
            path = FixFilePath(path);

            const char singleQuote = '\'';
            const char doubleQuote = '\"';
            var targetQuote = isSingleQuote ? singleQuote : doubleQuote;
            var convertQuote = isSingleQuote ? doubleQuote : singleQuote;

            if (!string.IsNullOrEmpty(path))
            {
                // Special case: convert the quotes.
                if (path.Length > 1 && path[0] == convertQuote && path[path.Length - 1] == convertQuote)
                {
                    path = $"{targetQuote}{path.Substring(1, path.Length - 2)}{targetQuote}";
                }
                // Enclose the path in a set of the 'target' quote unless the string is already quoted with the 'target' quotes.
                else if (path.Length == 1 || path[0] != targetQuote || path[path.Length - 1] != targetQuote)
                {
                    path = $"{targetQuote}{path}{targetQuote}";
                }
            }

            return path;
        }

        /// <summary>
        /// Indicates if the given file-spec ends with a slash.
        /// </summary>
        /// <param name="fileSpec">The file spec.</param>
        /// <returns>true, if file-spec has trailing slash</returns>
        internal static bool EndsWithSlash(string fileSpec)
        {
            return (fileSpec.Length > 0)
                ? IsSlash(fileSpec[fileSpec.Length - 1])
                : false;
        }

        /// <summary>
        /// Indicates if the given character is a slash.
        /// </summary>
        /// <param name="c"></param>
        /// <returns>true, if slash</returns>
        internal static bool IsSlash(char c)
        {
            return (c == Path.DirectorySeparatorChar) || (c == Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Trims the string and removes any double quotes around it.
        /// </summary>
        internal static string TrimAndStripAnyQuotes(string path)
        {
            if (path is null)
            {
                return path;
            }

            // Trim returns the same string if trimming isn't needed
            path = path.Trim();
            path = path.Trim(new char[] { '"' });

            return path;
        }

        /// <summary>
        /// Get the directory name of a rooted full path
        /// </summary>
        /// <param name="fullPath"></param>
        /// <returns></returns>
        internal static String GetDirectoryNameOfFullPath(String fullPath)
        {
            if (fullPath != null)
            {
                int i = fullPath.Length;
                while (i > 0 && fullPath[--i] != Path.DirectorySeparatorChar && fullPath[i] != Path.AltDirectorySeparatorChar)
                {
                    ;
                }

                return FixFilePath(fullPath.Substring(0, i));
            }
            return null;
        }

        internal static string TruncatePathToTrailingSegments(string path, int trailingSegmentsToKeep)
        {
#if !CLR2COMPATIBILITY
            ErrorUtilities.VerifyThrowInternalLength(path, nameof(path));
            ErrorUtilities.VerifyThrow(trailingSegmentsToKeep >= 0, "trailing segments must be positive");

            var segments = path.Split(Slashes, StringSplitOptions.RemoveEmptyEntries);

            var headingSegmentsToRemove = Math.Max(0, segments.Length - trailingSegmentsToKeep);

            return string.Join(DirectorySeparatorString, segments.Skip(headingSegmentsToRemove));
#else
            return path;
#endif
        }

        internal static bool ContainsRelativePathSegments(string path)
        {
            for (int i = 0; i < path.Length; i++)
            {
                if (i + 1 < path.Length && path[i] == '.' && path[i + 1] == '.')
                {
                    if (RelativePathBoundsAreValid(path, i, i + 1))
                    {
                        return true;
                    }
                    else
                    {
                        i += 2;
                        continue;
                    }
                }

                if (path[i] == '.' && RelativePathBoundsAreValid(path, i, i))
                {
                    return true;
                }
            }

            return false;
        }

#if !CLR2COMPATIBILITY
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool RelativePathBoundsAreValid(string path, int leftIndex, int rightIndex)
        {
            var leftBound = leftIndex - 1 >= 0
                ? path[leftIndex - 1]
                : (char?)null;

            var rightBound = rightIndex + 1 < path.Length
                ? path[rightIndex + 1]
                : (char?)null;

            return IsValidRelativePathBound(leftBound) && IsValidRelativePathBound(rightBound);
        }

#if !CLR2COMPATIBILITY
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsValidRelativePathBound(char? c)
        {
            return c == null || IsAnySlash(c.Value);
        }

        /// <summary>
        /// Gets the canonicalized full path of the provided path.
        /// Guidance for use: call this on all paths accepted through public entry
        /// points that need normalization. After that point, only verify the path
        /// is rooted, using ErrorUtilities.VerifyThrowPathRooted.
        /// ASSUMES INPUT IS ALREADY UNESCAPED.
        /// </summary>
        internal static string NormalizePath(string path)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path, nameof(path));
            string fullPath = GetFullPath(path);
            return FixFilePath(fullPath);
        }

        internal static string NormalizePath(string directory, string file)
        {
            return NormalizePath(Path.Combine(directory, file));
        }

#if !CLR2COMPATIBILITY
        internal static string NormalizePath(params string[] paths)
        {
            return NormalizePath(Path.Combine(paths));
        }
#endif

        private static string GetFullPath(string path)
        {
#if FEATURE_LEGACY_GETFULLPATH
            if (NativeMethodsShared.IsWindows)
            {
                string uncheckedFullPath = NativeMethodsShared.GetFullPath(path);

                if (IsPathTooLong(uncheckedFullPath))
                {
                    string message = ResourceUtilities.FormatString(AssemblyResources.GetString("Shared.PathTooLong"), path, NativeMethodsShared.MaxPath);
                    throw new PathTooLongException(message);
                }

                // We really don't care about extensions here, but Path.HasExtension provides a great way to
                // invoke the CLR's invalid path checks (these are independent of path length)
                Path.HasExtension(uncheckedFullPath);

                // If we detect we are a UNC path then we need to use the regular get full path in order to do the correct checks for UNC formatting
                // and security checks for strings like \\?\GlobalRoot
                return IsUNCPath(uncheckedFullPath) ? Path.GetFullPath(uncheckedFullPath) : uncheckedFullPath;
            }
#endif
            return Path.GetFullPath(path);
        }

#if FEATURE_LEGACY_GETFULLPATH
        private static bool IsUNCPath(string path)
        {
            if (!NativeMethodsShared.IsWindows || !path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }
            bool isUNC = true;
            for (int i = 2; i < path.Length - 1; i++)
            {
                if (path[i] == '\\')
                {
                    isUNC = false;
                    break;
                }
            }

            /*
              From Path.cs in the CLR

              Throw an ArgumentException for paths like \\, \\server, \\server\
              This check can only be properly done after normalizing, so
              \\foo\.. will be properly rejected.  Also, reject \\?\GLOBALROOT\
              (an internal kernel path) because it provides aliases for drives.

              throw new ArgumentException(Environment.GetResourceString("Arg_PathIllegalUNC"));

               // Check for \\?\Globalroot, an internal mechanism to the kernel
               // that provides aliases for drives and other undocumented stuff.
               // The kernel team won't even describe the full set of what
               // is available here - we don't want managed apps mucking
               // with this for security reasons.
            */
            return isUNC || path.IndexOf(@"\\?\globalroot", StringComparison.OrdinalIgnoreCase) != -1;
        }
#endif // FEATURE_LEGACY_GETFULLPATH

        internal static string FixFilePath(string path)
        {
            return string.IsNullOrEmpty(path) || Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/'); // .Replace("//", "/");
        }

#if !CLR2COMPATIBILITY
        /// <summary>
        /// If on Unix, convert backslashes to slashes for strings that resemble paths.
        /// The heuristic is if something resembles paths (contains slashes) check if the
        /// first segment exists and is a directory.
        /// Use a native shared method to massage file path. If the file is adjusted,
        /// that qualifies is as a path.
        ///
        /// @baseDirectory is just passed to LooksLikeUnixFilePath, to help with the check
        /// </summary>
        internal static string MaybeAdjustFilePath(string value, string baseDirectory = "")
        {
            var comparisonType = StringComparison.Ordinal;

            // Don't bother with arrays or properties or network paths, or those that
            // have no slashes.
            if (NativeMethodsShared.IsWindows || string.IsNullOrEmpty(value)
                || value.StartsWith("$(", comparisonType) || value.StartsWith("@(", comparisonType)
                || value.StartsWith("\\\\", comparisonType))
            {
                return value;
            }

            // For Unix-like systems, we may want to convert backslashes to slashes
            Span<char> newValue = ConvertToUnixSlashes(value.ToCharArray());

            // Find the part of the name we want to check, that is remove quotes, if present
            bool shouldAdjust = newValue.IndexOf('/') != -1 && LooksLikeUnixFilePath(RemoveQuotes(newValue), baseDirectory);
            return shouldAdjust ? newValue.ToString() : value;
        }

        /// <summary>
        /// If on Unix, convert backslashes to slashes for strings that resemble paths.
        /// This overload takes and returns ReadOnlyMemory of characters.
        /// </summary>
        internal static ReadOnlyMemory<char> MaybeAdjustFilePath(ReadOnlyMemory<char> value, string baseDirectory = "")
        {
            if (NativeMethodsShared.IsWindows || value.IsEmpty)
            {
                return value;
            }

            // Don't bother with arrays or properties or network paths.
            if (value.Length >= 2)
            {
                var span = value.Span;

                // The condition is equivalent to span.StartsWith("$(") || span.StartsWith("@(") || span.StartsWith("\\\\")
                if ((span[1] == '(' && (span[0] == '$' || span[0] == '@')) ||
                    (span[1] == '\\' && span[0] == '\\'))
                {
                    return value;
                }
            }

            // For Unix-like systems, we may want to convert backslashes to slashes
            Span<char> newValue = ConvertToUnixSlashes(value.ToArray());

            // Find the part of the name we want to check, that is remove quotes, if present
            bool shouldAdjust = newValue.IndexOf('/') != -1 && LooksLikeUnixFilePath(RemoveQuotes(newValue), baseDirectory);
            return shouldAdjust ? newValue.ToString().AsMemory() : value;
        }

        private static Span<char> ConvertToUnixSlashes(Span<char> path)
        {
            return path.IndexOf('\\') == -1 ? path : CollapseSlashes(path);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Span<char> CollapseSlashes(Span<char> str)
        {
            int sliceLength = 0;

            // Performs Regex.Replace(str, @"[\\/]+", "/")
            for (int i = 0; i < str.Length; i++)
            {
                bool isCurSlash = IsAnySlash(str[i]);
                bool isPrevSlash = i > 0 && IsAnySlash(str[i - 1]);

                if (!isCurSlash || !isPrevSlash)
                {
                    str[sliceLength] = str[i] == '\\' ? '/' : str[i];
                    sliceLength++;
                }
            }

            return str.Slice(0, sliceLength);
        }

        private static Span<char> RemoveQuotes(Span<char> path)
        {
            int endId = path.Length - 1;
            char singleQuote = '\'';
            char doubleQuote = '\"';

            bool hasQuotes = path.Length > 2
                && ((path[0] == singleQuote && path[endId] == singleQuote)
                || (path[0] == doubleQuote && path[endId] == doubleQuote));

            return hasQuotes ? path.Slice(1, endId - 1) : path;
        }
#endif

#if !CLR2COMPATIBILITY
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        internal static bool IsAnySlash(char c) => c == '/' || c == '\\';

#if !CLR2COMPATIBILITY
        /// <summary>
        /// If on Unix, check if the string looks like a file path.
        /// The heuristic is if something resembles paths (contains slashes) check if the
        /// first segment exists and is a directory.
        ///
        /// If @baseDirectory is not null, then look for the first segment exists under
        /// that
        /// </summary>
        internal static bool LooksLikeUnixFilePath(string value, string baseDirectory = "")
            => LooksLikeUnixFilePath(value.AsSpan(), baseDirectory);

        internal static bool LooksLikeUnixFilePath(ReadOnlySpan<char> value, string baseDirectory = "")
        {
            if (NativeMethodsShared.IsWindows)
            {
                return false;
            }

            // The first slash will either be at the beginning of the string or after the first directory name
            int directoryLength = value.Slice(1).IndexOf('/') + 1;
            bool shouldCheckDirectory = directoryLength != 0;

            // Check for actual files or directories under / that get missed by the above logic
            bool shouldCheckFileOrDirectory = !shouldCheckDirectory && value.Length > 0 && value[0] == '/';
            ReadOnlySpan<char> directory = value.Slice(0, directoryLength);

            return (shouldCheckDirectory && DefaultFileSystem.DirectoryExists(Path.Combine(baseDirectory, directory.ToString())))
                || (shouldCheckFileOrDirectory && DefaultFileSystem.FileOrDirectoryExists(value.ToString()));
        }
#endif

        /// <summary>
        /// Extracts the directory from the given file-spec.
        /// </summary>
        /// <param name="fileSpec">The filespec.</param>
        /// <returns>directory path</returns>
        internal static string GetDirectory(string fileSpec)
        {
            string directory = Path.GetDirectoryName(FixFilePath(fileSpec));

            // if file-spec is a root directory e.g. c:, c:\, \, \\server\share
            // NOTE: Path.GetDirectoryName also treats invalid UNC file-specs as root directories e.g. \\, \\server
            if (directory == null)
            {
                // just use the file-spec as-is
                directory = fileSpec;
            }
            else if ((directory.Length > 0) && !EndsWithSlash(directory))
            {
                // restore trailing slash if Path.GetDirectoryName has removed it (this happens with non-root directories)
                directory += Path.DirectorySeparatorChar;
            }

            return directory;
        }

        /// <summary>
        /// Determines whether the given assembly file name has one of the listed extensions.
        /// </summary>
        /// <param name="fileName">The name of the file</param>
        /// <param name="allowedExtensions">Array of extensions to consider.</param>
        /// <returns></returns>
        internal static bool HasExtension(string fileName, string[] allowedExtensions)
        {
            Debug.Assert(allowedExtensions?.Length > 0);

            // Easiest way to invoke invalid path chars
            // check, which callers are relying on.
            if (Path.HasExtension(fileName))
            {
                foreach (string extension in allowedExtensions)
                {
                    Debug.Assert(!String.IsNullOrEmpty(extension) && extension[0] == '.');

                    if (fileName.EndsWith(extension, PathComparison))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ISO 8601 Universal time with sortable format
        internal const string FileTimeFormat = "yyyy'-'MM'-'dd HH':'mm':'ss'.'fffffff";

        /// <summary>
        /// Get the currently executing assembly path
        /// </summary>
        internal static string ExecutingAssemblyPath => Path.GetFullPath(AssemblyUtilities.GetAssemblyLocation(typeof(FileUtilities).GetTypeInfo().Assembly));

        /// <summary>
        /// Determines the full path for the given file-spec.
        /// ASSUMES INPUT IS STILL ESCAPED
        /// </summary>
        /// <param name="fileSpec">The file spec to get the full path of.</param>
        /// <param name="currentDirectory"></param>
        /// <returns>full path</returns>
        internal static string GetFullPath(string fileSpec, string currentDirectory)
        {
            // Sending data out of the engine into the filesystem, so time to unescape.
            fileSpec = FixFilePath(EscapingUtilities.UnescapeAll(fileSpec));

            // Data coming back from the filesystem into the engine, so time to escape it back.
            string fullPath = EscapingUtilities.Escape(NormalizePath(Path.Combine(currentDirectory, fileSpec)));

            if (NativeMethodsShared.IsWindows && !EndsWithSlash(fullPath))
            {
                if (FileUtilitiesRegex.IsDrivePattern(fileSpec) ||
                    FileUtilitiesRegex.IsUncPattern(fullPath))
                {
                    // append trailing slash if Path.GetFullPath failed to (this happens with drive-specs and UNC shares)
                    fullPath += Path.DirectorySeparatorChar;
                }
            }

            return fullPath;
        }

        /// <summary>
        /// A variation of Path.GetFullPath that will return the input value
        /// instead of throwing any IO exception.
        /// Useful to get a better path for an error message, without the risk of throwing
        /// if the error message was itself caused by the path being invalid!
        /// </summary>
        internal static string GetFullPathNoThrow(string path)
        {
            try
            {
                path = NormalizePath(path);
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
            }

            return path;
        }

        /// <summary>
        /// Compare if two paths, relative to the given currentDirectory are equal.
        /// Does not throw IO exceptions. See <see cref="GetFullPathNoThrow(string)"/>
        /// </summary>
        /// <param name="first"></param>
        /// <param name="second"></param>
        /// <param name="currentDirectory"></param>
        /// <param name="alwaysIgnoreCase"></param>
        /// <returns></returns>
        internal static bool ComparePathsNoThrow(string first, string second, string currentDirectory, bool alwaysIgnoreCase = false)
        {
            StringComparison pathComparison = alwaysIgnoreCase ? StringComparison.OrdinalIgnoreCase : PathComparison;
            // perf: try comparing the bare strings first
            if (string.Equals(first, second, pathComparison))
            {
                return true;
            }

            var firstFullPath = NormalizePathForComparisonNoThrow(first, currentDirectory);
            var secondFullPath = NormalizePathForComparisonNoThrow(second, currentDirectory);

            return string.Equals(firstFullPath, secondFullPath, pathComparison);
        }

        /// <summary>
        /// Normalizes a path for path comparison
        /// Does not throw IO exceptions. See <see cref="GetFullPathNoThrow(string)"/>
        ///
        /// </summary>
        internal static string NormalizePathForComparisonNoThrow(string path, string currentDirectory)
        {
            // file is invalid, return early to avoid triggering an exception
            if (PathIsInvalid(path))
            {
                return path;
            }

            var normalizedPath = path.NormalizeForPathComparison();
            var fullPath = GetFullPathNoThrow(Path.Combine(currentDirectory, normalizedPath));

            return fullPath;
        }

        internal static bool PathIsInvalid(string path)
        {
            if (path.IndexOfAny(InvalidPathChars) >= 0)
            {
                return true;
            }

            // Path.GetFileName does not react well to malformed filenames.
            // For example, Path.GetFileName("a/b/foo:bar") returns bar instead of foo:bar
            // It also throws exceptions on illegal path characters
            var lastDirectorySeparator = path.LastIndexOfAny(Slashes);

            return path.IndexOfAny(InvalidFileNameChars, lastDirectorySeparator >= 0 ? lastDirectorySeparator + 1 : 0) >= 0;
        }

        /// <summary>
        /// A variation on File.Delete that will throw ExceptionHandling.NotExpectedException exceptions
        /// </summary>
        internal static void DeleteNoThrow(string path)
        {
            try
            {
                File.Delete(FixFilePath(path));
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
            }
        }

        /// <summary>
        /// A variation on Directory.Delete that will throw ExceptionHandling.NotExpectedException exceptions
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", MessageId = "System.Int32.TryParse(System.String,System.Int32@)", Justification = "We expect the out value to be 0 if the parse fails and compensate accordingly")]
        internal static void DeleteDirectoryNoThrow(string path, bool recursive, int retryCount = 0, int retryTimeOut = 0)
        {
            // Try parse will set the out parameter to 0 if the string passed in is null, or is outside the range of an int.
            if (!int.TryParse(Environment.GetEnvironmentVariable("MSBUILDDIRECTORYDELETERETRYCOUNT"), out retryCount))
            {
                retryCount = 0;
            }

            if (!int.TryParse(Environment.GetEnvironmentVariable("MSBUILDDIRECTORYDELETRETRYTIMEOUT"), out retryTimeOut))
            {
                retryTimeOut = 0;
            }

            retryCount = retryCount < 1 ? 2 : retryCount;
            retryTimeOut = retryTimeOut < 1 ? 500 : retryTimeOut;

            path = FixFilePath(path);

            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    if (DefaultFileSystem.DirectoryExists(path))
                    {
                        Directory.Delete(path, recursive);
                        break;
                    }
                }
                catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
                {
                }

                if (i + 1 < retryCount) // should not wait for the final iteration since we not gonna check anyway
                {
                    Thread.Sleep(retryTimeOut);
                }
            }
        }

        /// <summary>
        /// Deletes a directory, ensuring that Directory.Delete does not get a path ending in a slash.
        /// </summary>
        /// <remarks>
        /// This is a workaround for https://github.com/dotnet/corefx/issues/3780, which clashed with a common
        /// pattern in our tests.
        /// </remarks>
        internal static void DeleteWithoutTrailingBackslash(string path, bool recursive = false)
        {
            // Some tests (such as FileMatcher and Evaluation tests) were failing with an UnauthorizedAccessException or directory not empty.
            //  This retry logic works around that issue.
            const int NUM_TRIES = 3;
            for (int i = 0; i < NUM_TRIES; i++)
            {
                try
                {
                    Directory.Delete(EnsureNoTrailingSlash(path), recursive);

                    // If we got here, the directory was successfully deleted
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (i == NUM_TRIES - 1)
                    {
                        // var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                        // string fileString = string.Join(Environment.NewLine, files);
                        // string message = $"Unable to delete directory '{path}'.  Contents:" + Environment.NewLine + fileString;
                        // throw new IOException(message, ex);
                        throw;
                    }
                }

                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Gets a file info object for the specified file path. If the file path
        /// is invalid, or is a directory, or cannot be accessed, or does not exist,
        /// it returns null rather than throwing or returning a FileInfo around a non-existent file.
        /// This allows it to be called where File.Exists() (which never throws, and returns false
        /// for directories) was called - but with the advantage that a FileInfo object is returned
        /// that can be queried (e.g., for LastWriteTime) without hitting the disk again.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>FileInfo around path if it is an existing /file/, else null</returns>
        internal static FileInfo GetFileInfoNoThrow(string filePath)
        {
            filePath = AttemptToShortenPath(filePath);

            FileInfo fileInfo;

            try
            {
                fileInfo = new FileInfo(filePath);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                // Invalid or inaccessible path: treat as if nonexistent file, just as File.Exists does
                return null;
            }

            if (fileInfo.Exists)
            {
                // It's an existing file
                return fileInfo;
            }
            else
            {
                // Nonexistent, or existing but a directory, just as File.Exists behaves
                return null;
            }
        }

        /// <summary>
        /// Returns if the directory exists
        /// </summary>
        /// <param name="fullPath">Full path to the directory in the filesystem</param>
        /// <param name="fileSystem">The file system</param>
        /// <returns></returns>
        internal static bool DirectoryExistsNoThrow(string fullPath, IFileSystem fileSystem = null)
        {
            fullPath = AttemptToShortenPath(fullPath);

            try
            {
                fileSystem ??= DefaultFileSystem;

                return Traits.Instance.CacheFileExistence
                    ? FileExistenceCache.GetOrAdd(fullPath, fullPath => fileSystem.DirectoryExists(fullPath))
                    : fileSystem.DirectoryExists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns if the directory exists
        /// </summary>
        /// <param name="fullPath">Full path to the file in the filesystem</param>
        /// <param name="fileSystem">The file system</param>
        /// <returns></returns>
        internal static bool FileExistsNoThrow(string fullPath, IFileSystem fileSystem = null)
        {
            fullPath = AttemptToShortenPath(fullPath);

            try
            {
                fileSystem ??= DefaultFileSystem;

                return Traits.Instance.CacheFileExistence
                    ? FileExistenceCache.GetOrAdd(fullPath, fullPath => fileSystem.FileExists(fullPath))
                    : fileSystem.FileExists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// If there is a directory or file at the specified path, returns true.
        /// Otherwise, returns false.
        /// Does not throw IO exceptions, to match Directory.Exists and File.Exists.
        /// Unlike calling each of those in turn it only accesses the disk once, which is faster.
        /// </summary>
        internal static bool FileOrDirectoryExistsNoThrow(string fullPath, IFileSystem fileSystem = null)
        {
            fullPath = AttemptToShortenPath(fullPath);

            try
            {
                fileSystem ??= DefaultFileSystem;

                return Traits.Instance.CacheFileExistence
                    ? FileExistenceCache.GetOrAdd(fullPath, fullPath => fileSystem.FileOrDirectoryExists(fullPath))
                    : fileSystem.FileOrDirectoryExists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// This method returns true if the specified filename is a solution file (.sln) or
        /// solution filter file (.slnf); otherwise, it returns false.
        /// </summary>
        /// <remarks>
        /// Solution filters are included because they are a thin veneer over solutions, just
        /// with a more limited set of projects to build, and should be treated the same way.
        /// </remarks>
        internal static bool IsSolutionFilename(string filename)
        {
            return HasExtension(filename, ".sln") || HasExtension(filename, ".slnf");
        }

        internal static bool IsSolutionFilterFilename(string filename)
        {
            return HasExtension(filename, ".slnf");
        }

        /// <summary>
        /// Returns true if the specified filename is a VC++ project file, otherwise returns false
        /// </summary>
        internal static bool IsVCProjFilename(string filename)
        {
            return HasExtension(filename, ".vcproj");
        }

        internal static bool IsDspFilename(string filename)
        {
            return HasExtension(filename, ".dsp");
        }

        /// <summary>
        /// Returns true if the specified filename is a metaproject file (.metaproj), otherwise false.
        /// </summary>
        internal static bool IsMetaprojectFilename(string filename)
        {
            return HasExtension(filename, ".metaproj");
        }

        internal static bool IsBinaryLogFilename(string filename)
        {
            return HasExtension(filename, ".binlog");
        }

        private static bool HasExtension(string filename, string extension)
        {
            if (String.IsNullOrEmpty(filename))
            {
                return false;
            }

            return filename.EndsWith(extension, PathComparison);
        }

        /// <summary>
        /// Given the absolute location of a file, and a disc location, returns relative file path to that disk location.
        /// Throws UriFormatException.
        /// </summary>
        /// <param name="basePath">
        /// The base path we want to be relative to. Must be absolute.
        /// Should <i>not</i> include a filename as the last segment will be interpreted as a directory.
        /// </param>
        /// <param name="path">
        /// The path we need to make relative to basePath.  The path can be either absolute path or a relative path in which case it is relative to the base path.
        /// If the path cannot be made relative to the base path (for example, it is on another drive), it is returned verbatim.
        /// If the basePath is an empty string, returns the path.
        /// </param>
        /// <returns>relative path (can be the full path)</returns>
        internal static string MakeRelative(string basePath, string path)
        {
            ErrorUtilities.VerifyThrowArgumentNull(basePath, nameof(basePath));
            ErrorUtilities.VerifyThrowArgumentLength(path, nameof(path));

            string fullBase = Path.GetFullPath(basePath);
            string fullPath = Path.GetFullPath(path);

            string[] splitBase = fullBase.Split(MSBuildConstants.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string[] splitPath = fullPath.Split(MSBuildConstants.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            ErrorUtilities.VerifyThrow(splitPath.Length > 0, "Cannot call MakeRelative on a path of only slashes.");

            // On a mac, the path could start with any number of slashes and still be valid. We have to check them all.
            int indexOfFirstNonSlashChar = 0;
            while (path[indexOfFirstNonSlashChar] == Path.DirectorySeparatorChar)
            {
                indexOfFirstNonSlashChar++;
            }
            if (path.IndexOf(splitPath[0]) != indexOfFirstNonSlashChar)
            {
                // path was already relative so just return it
                return FixFilePath(path);
            }

            int index = 0;
            while (index < splitBase.Length && index < splitPath.Length && splitBase[index].Equals(splitPath[index], PathComparison))
            {
                index++;
            }

            if (index == splitBase.Length && index == splitPath.Length)
            {
                return ".";
            }

            // If the paths have no component in common, the only valid relative path is the full path.
            if (index == 0)
            {
                return fullPath;
            }

            StringBuilder sb = StringBuilderCache.Acquire();

            for (int i = index; i < splitBase.Length; i++)
            {
                sb.Append("..").Append(Path.DirectorySeparatorChar);
            }
            for (int i = index; i < splitPath.Length; i++)
            {
                sb.Append(splitPath[i]).Append(Path.DirectorySeparatorChar);
            }

            if (fullPath[fullPath.Length - 1] != Path.DirectorySeparatorChar)
            {
                sb.Length--;
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        /// <summary>
        /// Normalizes the path if and only if it is longer than max path,
        /// or would be if rooted by the current directory.
        /// This may make it shorter by removing ".."'s.
        /// </summary>
        internal static string AttemptToShortenPath(string path)
        {
            if (IsPathTooLong(path) || IsPathTooLongIfRooted(path))
            {
                // Attempt to make it shorter -- perhaps there are some \..\ elements
                path = GetFullPathNoThrow(path);
            }
            return FixFilePath(path);
        }

        private static bool IsPathTooLong(string path)
        {
            // >= not > because MAX_PATH assumes a trailing null
            return path.Length >= NativeMethodsShared.MaxPath;
        }

        private static bool IsPathTooLongIfRooted(string path)
        {
            bool hasMaxPath = NativeMethodsShared.HasMaxPath;
            int maxPath = NativeMethodsShared.MaxPath;
            // >= not > because MAX_PATH assumes a trailing null
            return hasMaxPath && !IsRootedNoThrow(path) && NativeMethodsShared.GetCurrentDirectory().Length + path.Length + 1 /* slash */ >= maxPath;
        }

        /// <summary>
        /// A variation of Path.IsRooted that not throw any IO exception.
        /// </summary>
        private static bool IsRootedNoThrow(string path)
        {
            try
            {
                return Path.IsPathRooted(FixFilePath(path));
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// Get the folder N levels above the given. Will stop and return current path when rooted.
        /// </summary>
        /// <param name="path">Path to get the folder above.</param>
        /// <param name="count">Number of levels up to walk.</param>
        /// <returns>Full path to the folder N levels above the path.</returns>
        internal static string GetFolderAbove(string path, int count = 1)
        {
            if (count < 1)
            {
                return path;
            }

            var parent = Directory.GetParent(path);

            while (count > 1 && parent?.Parent != null)
            {
                parent = parent.Parent;
                count--;
            }

            return parent?.FullName ?? path;
        }

        /// <summary>
        /// Combine multiple paths. Should only be used when compiling against .NET 2.0.
        /// <remarks>
        /// Only use in .NET 2.0. Otherwise, use System.IO.Path.Combine(...)
        /// </remarks>
        /// </summary>
        /// <param name="root">Root path.</param>
        /// <param name="paths">Paths to concatenate.</param>
        /// <returns>Combined path.</returns>
        internal static string CombinePaths(string root, params string[] paths)
        {
            ErrorUtilities.VerifyThrowArgumentNull(root, nameof(root));
            ErrorUtilities.VerifyThrowArgumentNull(paths, nameof(paths));

            return paths.Aggregate(root, Path.Combine);
        }

        internal static string TrimTrailingSlashes(this string s)
        {
            return s.TrimEnd(Slashes);
        }

        /// <summary>
        /// Replace all backward slashes to forward slashes
        /// </summary>
        internal static string ToSlash(this string s)
        {
            return s.Replace('\\', '/');
        }

        internal static string ToBackslash(this string s)
        {
            return s.Replace('/', '\\');
        }

        /// <summary>
        /// Ensure all slashes are the current platform's slash
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        internal static string ToPlatformSlash(this string s)
        {
            var separator = Path.DirectorySeparatorChar;

            return s.Replace(separator == '/' ? '\\' : '/', separator);
        }

        internal static string WithTrailingSlash(this string s)
        {
            return EnsureTrailingSlash(s);
        }

        internal static string NormalizeForPathComparison(this string s) => s.ToPlatformSlash().TrimTrailingSlashes();

        // TODO: assumption on file system case sensitivity: https://github.com/dotnet/msbuild/issues/781
        internal static bool PathsEqual(string path1, string path2)
        {
            if (path1 == null && path2 == null)
            {
                return true;
            }
            if (path1 == null || path2 == null)
            {
                return false;
            }

            var endA = path1.Length - 1;
            var endB = path2.Length - 1;

            // Trim trailing slashes
            for (var i = endA; i >= 0; i--)
            {
                var c = path1[i];
                if (c == '/' || c == '\\')
                {
                    endA--;
                }
                else
                {
                    break;
                }
            }

            for (var i = endB; i >= 0; i--)
            {
                var c = path2[i];
                if (c == '/' || c == '\\')
                {
                    endB--;
                }
                else
                {
                    break;
                }
            }

            if (endA != endB)
            {
                // Lengths not the same
                return false;
            }

            for (var i = 0; i <= endA; i++)
            {
                var charA = (uint)path1[i];
                var charB = (uint)path2[i];

                if ((charA | charB) > 0x7F)
                {
                    // Non-ascii chars move to non fast path
                    return PathsEqualNonAscii(path1, path2, i, endA - i + 1);
                }

                // uppercase both chars - notice that we need just one compare per char
                if ((uint)(charA - 'a') <= (uint)('z' - 'a'))
                {
                    charA -= 0x20;
                }

                if ((uint)(charB - 'a') <= (uint)('z' - 'a'))
                {
                    charB -= 0x20;
                }

                // Set path delimiters the same
                if (charA == '\\')
                {
                    charA = '/';
                }
                if (charB == '\\')
                {
                    charB = '/';
                }

                if (charA != charB)
                {
                    return false;
                }
            }

            return true;
        }

        internal static StreamWriter OpenWrite(string path, bool append, Encoding encoding = null)
        {
            const int DefaultFileStreamBufferSize = 4096;
            FileMode mode = append ? FileMode.Append : FileMode.Create;
            Stream fileStream = new FileStream(path, mode, FileAccess.Write, FileShare.Read, DefaultFileStreamBufferSize, FileOptions.SequentialScan);
            if (encoding == null)
            {
                return new StreamWriter(fileStream);
            }
            else
            {
                return new StreamWriter(fileStream, encoding);
            }
        }

        internal static StreamReader OpenRead(string path, Encoding encoding = null, bool detectEncodingFromByteOrderMarks = true)
        {
            const int DefaultFileStreamBufferSize = 4096;
            Stream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultFileStreamBufferSize, FileOptions.SequentialScan);
            if (encoding == null)
            {
                return new StreamReader(fileStream);
            }
            else
            {
                return new StreamReader(fileStream, encoding, detectEncodingFromByteOrderMarks);
            }
        }

        /// <summary>
        /// Locate a file in either the directory specified or a location in the
        /// directory structure above that directory.
        /// </summary>
        internal static string GetDirectoryNameOfFileAbove(string startingDirectory, string fileName, IFileSystem fileSystem = null)
        {
            fileSystem ??= DefaultFileSystem;

            // Canonicalize our starting location
            string lookInDirectory = GetFullPath(startingDirectory);

            do
            {
                // Construct the path that we will use to test against
                string possibleFileDirectory = Path.Combine(lookInDirectory, fileName);

                // If we successfully locate the file in the directory that we're
                // looking in, simply return that location. Otherwise we'll
                // keep moving up the tree.
                if (fileSystem.FileExists(possibleFileDirectory))
                {
                    // We've found the file, return the directory we found it in
                    return lookInDirectory;
                }
                else
                {
                    // GetDirectoryName will return null when we reach the root
                    // terminating our search
                    lookInDirectory = Path.GetDirectoryName(lookInDirectory);
                }
            }
            while (lookInDirectory != null);

            // When we didn't find the location, then return an empty string
            return string.Empty;
        }

        /// <summary>
        /// Searches for a file based on the specified starting directory.
        /// </summary>
        /// <param name="file">The file to search for.</param>
        /// <param name="startingDirectory">An optional directory to start the search in.  The default location is the directory
        ///     of the file containing the property function.</param>
        /// <param name="fileSystem">The filesystem</param>
        /// <returns>The full path of the file if it is found, otherwise an empty string.</returns>
        internal static string GetPathOfFileAbove(string file, string startingDirectory, IFileSystem fileSystem = null)
        {
            // This method does not accept a path, only a file name
            if (file.Any(i => i.Equals(Path.DirectorySeparatorChar) || i.Equals(Path.AltDirectorySeparatorChar)))
            {
                ErrorUtilities.ThrowArgument("InvalidGetPathOfFileAboveParameter", file);
            }

            // Search for a directory that contains that file
            string directoryName = GetDirectoryNameOfFileAbove(startingDirectory, file, fileSystem);

            return String.IsNullOrEmpty(directoryName) ? String.Empty : NormalizePath(directoryName, file);
        }

        internal static void EnsureDirectoryExists(string directoryPath)
        {
            if (!string.IsNullOrEmpty(directoryPath) && !DefaultFileSystem.DirectoryExists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        // Method is simple set of function calls and may inline;
        // we don't want it inlining into the tight loop that calls it as an exit case,
        // so mark as non-inlining
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PathsEqualNonAscii(string strA, string strB, int i, int length)
        {
            if (string.Compare(strA, i, strB, i, length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }

            var slash1 = strA.ToSlash();
            var slash2 = strB.ToSlash();

            if (string.Compare(slash1, i, slash2, i, length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }

            return false;
        }

#if !CLR2COMPATIBILITY
        /// <summary>
        /// Clears the file existence cache.
        /// </summary>
        internal static void ClearFileExistenceCache()
        {
            FileExistenceCache.Clear();
        }
#endif

        internal static void ReadFromStream(this Stream stream, byte[] content, int startIndex, int length)
        {
#if NET7_0_OR_GREATER
            stream.ReadExactly(content, startIndex, length);
#else
#pragma warning disable CA2022 // Avoid inexact read with 'Stream.Read'
            stream.Read(content, 0, length);
#pragma warning restore CA2022 // Avoid inexact read with 'Stream.Read'
#endif
        }
    }
}
