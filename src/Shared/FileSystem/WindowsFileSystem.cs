// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#nullable disable

namespace Microsoft.Build.Shared.FileSystem
{
    /// <summary>
    /// The type of file artifact to search for
    /// </summary>
    internal enum FileArtifactType : byte
    {
        /// <nodoc/>
        File,
        /// <nodoc/>
        Directory,
        /// <nodoc/>
        FileOrDirectory
    }

    /// <summary>
    /// Windows-specific implementation of file system operations using Windows native invocations.
    /// TODO For potential extra perf gains, provide native implementations for all IFileSystem methods and stop inheriting from ManagedFileSystem
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsFileSystem : ManagedFileSystem
    {
        private static readonly WindowsFileSystem Instance = new();

        public static new WindowsFileSystem Singleton() => WindowsFileSystem.Instance;

        private WindowsFileSystem() { }

        public override IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
        {
            return EnumerateFileOrDirectories(path, FileArtifactType.File, searchPattern, searchOption);
        }

        public override IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption)
        {
            return EnumerateFileOrDirectories(path, FileArtifactType.Directory, searchPattern, searchOption);
        }

        public override IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern, SearchOption searchOption)
        {
            return EnumerateFileOrDirectories(path, FileArtifactType.FileOrDirectory, searchPattern, searchOption);
        }

        public override bool DirectoryExists(string path)
        {
#if NETFRAMEWORK
            return Microsoft.IO.Directory.Exists(path);
#else
            return Directory.Exists(path);
#endif
        }

        public override bool FileExists(string path)
        {
#if NETFRAMEWORK
            return Microsoft.IO.File.Exists(path);
#else
            return File.Exists(path);
#endif
        }

        public override bool FileOrDirectoryExists(string path)
        {
#if NETFRAMEWORK
            return FileExists(path) || DirectoryExists(path);
#else
            return Path.Exists(path);
#endif
        }

        public override DateTime GetLastWriteTimeUtc(string path)
        {
            var fileLastWriteTime = NativeMethodsShared.GetLastWriteFileUtcTime(path);

            if (fileLastWriteTime != DateTime.MinValue)
            {
                return fileLastWriteTime;
            }
            else
            {
                NativeMethodsShared.GetLastWriteDirectoryUtcTime(path, out var directoryLastWriteTime);
                return directoryLastWriteTime;
            }
        }

        private static IEnumerable<string> EnumerateFileOrDirectories(
            string directoryPath,
            FileArtifactType fileArtifactType,
            string searchPattern,
            SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            var enumeration = new List<string>();

            var result = CustomEnumerateDirectoryEntries(
                directoryPath,
                fileArtifactType,
                searchPattern,
                searchOption,
                enumeration);

            // If the result indicates that the enumeration succeeded or the directory does not exist, then the result is considered success.
            // In particular, if the globed directory does not exist, then we want to return the empty file, and track for the anti-dependency.
            if (
                !(result.Status == WindowsNative.EnumerateDirectoryStatus.Success ||
                  result.Status == WindowsNative.EnumerateDirectoryStatus.SearchDirectoryNotFound))
            {
                throw result.CreateExceptionForError();
            }

            return enumeration;
        }

        private static WindowsNative.EnumerateDirectoryResult CustomEnumerateDirectoryEntries(
            string directoryPath,
            FileArtifactType fileArtifactType,
            string pattern,
            SearchOption searchOption,
            ICollection<string> result)
        {
            var searchDirectoryPath = Path.Combine(directoryPath, "*");

            WindowsNative.Win32FindData findResult;
            using (var findHandle = WindowsNative.FindFirstFileW(searchDirectoryPath, out findResult))
            {
                if (findHandle.IsInvalid)
                {
                    int hr = Marshal.GetLastWin32Error();
                    Debug.Assert(hr != WindowsNative.ErrorFileNotFound);
                    WindowsNative.EnumerateDirectoryStatus findHandleOpenStatus = hr switch
                    {
                        WindowsNative.ErrorFileNotFound => WindowsNative.EnumerateDirectoryStatus.SearchDirectoryNotFound,
                        WindowsNative.ErrorPathNotFound => WindowsNative.EnumerateDirectoryStatus.SearchDirectoryNotFound,
                        WindowsNative.ErrorDirectory => WindowsNative.EnumerateDirectoryStatus.CannotEnumerateFile,
                        WindowsNative.ErrorAccessDenied => WindowsNative.EnumerateDirectoryStatus.AccessDenied,
                        _ => WindowsNative.EnumerateDirectoryStatus.UnknownError,
                    };
                    return new WindowsNative.EnumerateDirectoryResult(directoryPath, findHandleOpenStatus, hr);
                }

                while (true)
                {
                    var isDirectory = (findResult.DwFileAttributes & FileAttributes.Directory) != 0;

                    // There will be entries for the current and parent directories. Ignore those.
                    if (!isDirectory || (findResult.CFileName != "." && findResult.CFileName != ".."))
                    {
                        // Make sure pattern and directory/file filters are honored
                        // We special case the "*" pattern since it is the default when no pattern is specified
                        // so we avoid calling the matching function
                        if (pattern == "*" ||
                            WindowsNative.PathMatchSpecExW(findResult.CFileName, pattern, WindowsNative.DwFlags.PmsfNormal) ==
                            WindowsNative.ErrorSuccess)
                        {
                            if (fileArtifactType == FileArtifactType.FileOrDirectory ||
                                !(fileArtifactType == FileArtifactType.Directory ^ isDirectory))
                            {
                                result.Add(Path.Combine(directoryPath, findResult.CFileName));
                            }
                        }

                        // Recursively go into subfolders if specified
                        if (searchOption == SearchOption.AllDirectories && isDirectory)
                        {
                            var recurs = CustomEnumerateDirectoryEntries(
                                Path.Combine(directoryPath, findResult.CFileName),
                                fileArtifactType,
                                pattern,
                                searchOption,
                                result);

                            if (!recurs.Succeeded)
                            {
                                return recurs;
                            }
                        }
                    }

                    if (!WindowsNative.FindNextFileW(findHandle, out findResult))
                    {
                        int hr = Marshal.GetLastWin32Error();
                        if (hr == WindowsNative.ErrorNoMoreFiles)
                        {
                            // Graceful completion of enumeration.
                            return new WindowsNative.EnumerateDirectoryResult(
                                directoryPath,
                                WindowsNative.EnumerateDirectoryStatus.Success,
                                hr);
                        }

                        Debug.Assert(hr != WindowsNative.ErrorSuccess);
                        return new WindowsNative.EnumerateDirectoryResult(
                            directoryPath,
                            WindowsNative.EnumerateDirectoryStatus.UnknownError,
                            hr);
                    }
                }
            }
        }
    }
}
