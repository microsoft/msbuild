// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

#nullable disable

namespace Microsoft.Build.Shared.FileSystem
{
    internal sealed class CachingFileSystemWrapper : IFileSystem
    {
        private readonly IFileSystem _fileSystem;
        private readonly ConcurrentDictionary<string, bool> _existenceCache = null;
        private readonly ConcurrentDictionary<string, DateTime> _lastWriteTimeCache = new ConcurrentDictionary<string, DateTime>();
        private readonly bool _skipExistenceCheck = false;

        public CachingFileSystemWrapper(IFileSystem fileSystem)
            : this(fileSystem, false)
        {
        }

        public CachingFileSystemWrapper(IFileSystem fileSystem, bool skipExistenceCheck = false)
        {
            _fileSystem = fileSystem;
            _skipExistenceCheck = skipExistenceCheck;
            if (!_skipExistenceCheck)
            {
                _existenceCache = new ConcurrentDictionary<string, bool>();
            }
        }

        public bool FileOrDirectoryExists(string path)
        {
            return CachedExistenceCheck(path, p => _fileSystem.FileOrDirectoryExists(p));
        }

        public FileAttributes GetAttributes(string path)
        {
            return _fileSystem.GetAttributes(path);
        }

        public DateTime GetLastWriteTimeUtc(string path)
        {
            return _lastWriteTimeCache.GetOrAdd(path, p => _fileSystem.GetLastWriteTimeUtc(p));
        }

        public bool DirectoryExists(string path)
        {
            return CachedExistenceCheck(path, p => _fileSystem.DirectoryExists(p));
        }

        public bool FileExists(string path)
        {
            return CachedExistenceCheck(path, p => _fileSystem.FileExists(p));
        }

        public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            return _fileSystem.EnumerateDirectories(path, searchPattern, searchOption);
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

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            return _fileSystem.EnumerateFiles(path, searchPattern, searchOption);
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            return _fileSystem.EnumerateFileSystemEntries(path, searchPattern, searchOption);
        }

        private bool CachedExistenceCheck(string path, Func<string, bool> existenceCheck)
        {
            if (_skipExistenceCheck)
            {
                return true;
            }

            return _existenceCache != null && _existenceCache.GetOrAdd(path, existenceCheck);
        }
    }
}
