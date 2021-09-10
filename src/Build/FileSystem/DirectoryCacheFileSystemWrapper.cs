// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.Build.FileSystem
{
    internal class DirectoryCacheFileSystemWrapper : IFileSystem
    {
        /// <summary>
        /// The base <see cref="IFileSystem"/> to fall back to for functionality not provided by <see cref="_directoryCache"/>.
        /// </summary>
        private readonly IFileSystem _fileSystem;

        /// <summary>
        /// A host-provided cache used for file existence and directory enumeration.
        /// </summary>
        private readonly IDirectoryCache _directoryCache;

        public DirectoryCacheFileSystemWrapper(IFileSystem fileSystem, IDirectoryCache directoryCache)
        {
            _fileSystem = fileSystem;
            _directoryCache = directoryCache;
        }

        #region IFileSystem implementation based on IDirectoryCache

        public bool FileOrDirectoryExists(string path)
        {
            return _directoryCache.FileExists(path) || _directoryCache.DirectoryExists(path);
        }

        public bool DirectoryExists(string path)
        {
            return _directoryCache.DirectoryExists(path);
        }

        public bool FileExists(string path)
        {
            return _directoryCache.FileExists(path);
        }

        public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (searchOption != SearchOption.TopDirectoryOnly)
            {
                // Recursive enumeration is not used during evaluation, pass it through.
                return _fileSystem.EnumerateDirectories(path, searchPattern, searchOption);
            }
            return EnumerateFullFileSystemPaths(path, searchPattern, includeFiles: false, includeDirectories: true);
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (searchOption != SearchOption.TopDirectoryOnly)
            {
                // Recursive enumeration is not used during evaluation, pass it through.
                return _fileSystem.EnumerateFiles(path, searchPattern, searchOption);
            }
            return EnumerateFullFileSystemPaths(path, searchPattern, includeFiles: true, includeDirectories: false);
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (searchOption != SearchOption.TopDirectoryOnly)
            {
                // Recursive enumeration is not used during evaluation, pass it through.
                return _fileSystem.EnumerateFileSystemEntries(path, searchPattern, searchOption);
            }
            return EnumerateFullFileSystemPaths(path, searchPattern, includeFiles: true, includeDirectories: true);
        }

        private IEnumerable<string> EnumerateFullFileSystemPaths(string path, string searchPattern, bool includeFiles, bool includeDirectories)
        {
            FindPredicate predicate = (ref ReadOnlySpan<char> fileName) =>
            {
                return FileMatcher.IsAllFilesWildcard(searchPattern) || FileMatcher.IsMatch(fileName, searchPattern);
            };
            // TODO: Don't create a new string and use Path.Join when it becomes available.
            FindTransform<string> transform = (ref ReadOnlySpan<char> fileName) => Path.Combine(path, fileName.ToString());

            IEnumerable<string> directories = includeDirectories
                ? _directoryCache.EnumerateDirectories(path, searchPattern, predicate, transform)
                : Enumerable.Empty<string>();
            IEnumerable<string> files = includeFiles
                ? _directoryCache.EnumerateFiles(path, searchPattern, predicate, transform)
                : Enumerable.Empty<string>();

            return Enumerable.Concat(directories, files);
        }

        #endregion

        #region IFileSystem pass-through implementation

        public FileAttributes GetAttributes(string path)
        {
            return _fileSystem.GetAttributes(path);
        }

        public DateTime GetLastWriteTimeUtc(string path)
        {
            return _fileSystem.GetLastWriteTimeUtc(path);
        }

        public TextReader ReadFile(string path)
        {
            return _fileSystem.ReadFile(path);
        }

        public Stream GetFileStream(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return _fileSystem.GetFileStream(path, mode, access, share);
        }

        public string ReadFileAllText(string path)
        {
            return _fileSystem.ReadFileAllText(path);
        }

        public byte[] ReadFileAllBytes(string path)
        {
            return _fileSystem.ReadFileAllBytes(path);
        }

        #endregion
    }
}
