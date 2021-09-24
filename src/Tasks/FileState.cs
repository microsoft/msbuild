// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// CopyFile delegate
    /// 
    /// returns  Success = true, Failure = false; Retry = null
    /// </summary>
    /// <param name="source">Source file</param>
    /// <param name="destination">Destination file</param>
    internal delegate bool? CopyFileWithState(FileState source, FileState destination);

    /// <summary>
    /// Short-term cache saves the result of IO operations on a filename. Should only be
    /// used in cases where it is know there will be no exogenous changes to the filesystem
    /// for this file.
    /// </summary>
    /// <remarks>
    /// Uses PInvoke rather than FileInfo because the latter does all kinds of expensive checks.
    /// 
    /// Deficiency: some of the properties eat some or all exceptions. If they are called first, they will
    /// trigger the population and eat. Subsequent calls will then not throw, but instead eg return zero.
    /// This could be fixed by storing the exception from the population, and throwing no matter who does
    /// the population and whether it's been done before.
    /// </remarks>
    internal class FileState
    {
        private class FileDirInfo
        {
            /// <summary>
            /// The name of the file.
            /// </summary>
            private readonly string _filename;

            /// <summary>
            /// Set to true if file or directory exists
            /// </summary>
            public readonly bool Exists;

            /// <summary>
            /// Set to true if the path referred to a directory.
            /// </summary>
            public readonly bool IsDirectory;

            /// <summary>
            /// File length
            /// </summary>
            public readonly long Length;

            /// <summary>
            /// Last time the file was updated
            /// </summary>
            public readonly DateTime LastWriteTimeUtc;

            /// <summary>
            /// True if the file is readonly
            /// </summary>
            public readonly bool IsReadOnly;

            /// <summary>
            /// Exception thrown on creation
            /// </summary>
            private readonly Exception _exceptionThrown;

            /// <summary>
            /// Constructor gets the data for the filename.
            /// On Win32 it uses native means. Otherwise,
            /// uses standard .NET FileInfo/DirInfo
            /// </summary>
            public FileDirInfo(string filename)
            {
                Exists = false;

                // If file/directory does not exist, return 12 midnight 1/1/1601.
                LastWriteTimeUtc = new DateTime(1601, 1, 1);

                _filename = FileUtilities.AttemptToShortenPath(filename); // This is no-op unless the path actually is too long

                int oldMode = 0;

                if (NativeMethodsShared.IsWindows)
                {
                    // THIS COPIED FROM THE BCL:
                    //
                    // For floppy drives, normally the OS will pop up a dialog saying
                    // there is no disk in drive A:, please insert one.  We don't want that. 
                    // SetErrorMode will let us disable this, but we should set the error
                    // mode back, since this may have wide-ranging effects.
                    NativeMethodsShared.SetThreadErrorMode(1 /* ErrorModes.SEM_FAILCRITICALERRORS */, out oldMode);
                }

                try
                {
                    if (NativeMethodsShared.IsWindows)
                    {
                        var data = new NativeMethodsShared.WIN32_FILE_ATTRIBUTE_DATA();
                        bool success = NativeMethodsShared.GetFileAttributesEx(_filename, 0, ref data);

                        if (!success)
                        {
                            int error = Marshal.GetLastWin32Error();

                            // File not found is the most common case, for example we're copying
                            // somewhere without a file yet. Don't do something like FileInfo.Exists to
                            // get a nice error, or we're doing IO again! Don't even format our own string:
                            // that turns out to be unacceptably expensive here as well. Set a flag for this particular case.
                            //
                            // Also, when not under debugger (!) it will give error == 3 for path too long. Make that consistently throw instead.
                            if ((error == 2 /* ERROR_FILE_NOT_FOUND */|| error == 3 /* ERROR_PATH_NOT_FOUND */)
                                && _filename.Length <= NativeMethodsShared.MaxPath)
                            {
                                Exists = false;
                                return;
                            }

                            // Throw nice message as far as we can. At this point IO is OK.
                            Length = new FileInfo(_filename).Length;

                            // Otherwise this will give at least something
                            NativeMethodsShared.ThrowExceptionForErrorCode(error);
                            ErrorUtilities.ThrowInternalErrorUnreachable();
                        }

                        Exists = true;
                        IsDirectory = (data.fileAttributes & NativeMethodsShared.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        IsReadOnly = !IsDirectory
                                      && (data.fileAttributes & NativeMethodsShared.FILE_ATTRIBUTE_READONLY) != 0;
                        LastWriteTimeUtc =
                            DateTime.FromFileTimeUtc(((long)data.ftLastWriteTimeHigh << 0x20) | data.ftLastWriteTimeLow);
                        Length = IsDirectory ? 0 : (((long)data.fileSizeHigh << 0x20) | data.fileSizeLow);
                    }
                    else
                    {
                        var fileInfo = new FileInfo(_filename);

                        if (fileInfo.Exists)
                        {
                            // Use FileInfo to get readonly and last write date
                            Exists = true;
                            IsReadOnly = fileInfo.IsReadOnly;
                            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                            Length = fileInfo.Length;
                        }
                        else
                        {
                            var directoryInfo = new DirectoryInfo(_filename);

                            if (directoryInfo.Exists)
                            {
                                // Use DirectoryInfo to get the last write date
                                Exists = true;
                                IsDirectory = true;
                                IsReadOnly = false;
                                LastWriteTimeUtc = directoryInfo.LastWriteTimeUtc;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Save the exception thrown and assume the file does not exist
                    _exceptionThrown = ex;
                    Exists = false;
                }
                finally
                {
                    // Reset the error mode on Windows
                    if (NativeMethodsShared.IsWindows)
                    {
                        NativeMethodsShared.SetThreadErrorMode(oldMode, out _);
                    }
                }
            }

            /// <summary>
            /// Throw exception as if the FileInfo did it. We
            /// know that getting the length of a file would
            /// throw exception if there are IO problems
            /// </summary>
            public void ThrowFileInfoException(bool doThrow)
            {
                if (doThrow)
                {
                    // Provoke exception
                    var length = (new FileInfo(_filename)).Length;
                }
            }

            /// <summary>
            /// Throw non-IO-related exception if occurred during creation.
            /// Return true if exception did occur, but was IO-related
            /// </summary>
            public bool ThrowNonIoExceptionIfPending()
            {
                if (_exceptionThrown != null)
                {
                    if (!ExceptionHandling.IsIoRelatedException(_exceptionThrown))
                    {
                        throw _exceptionThrown;
                    }

                    return true;
                }

                return false;
            }

            /// <summary>
            /// Throw any exception collected during construction
            /// </summary>
            public void ThrowException()
            {
                if (_exceptionThrown != null)
                {
                    throw _exceptionThrown;
                }
            }
        }

        /// <summary>
        /// The name of the file.
        /// </summary>
        private readonly string _filename;

        /// <summary>
        /// Actual file or directory information
        /// </summary>
        private Lazy<FileDirInfo> _data;

        /// <summary>
        /// Constructor.
        /// Only stores file name: does not grab the file state until first request.
        /// </summary>
        internal FileState(string filename)
        {
            ErrorUtilities.VerifyThrowArgumentLength(filename, nameof(filename));
            _filename = filename;
            _data = new Lazy<FileDirInfo>(() => new FileDirInfo(_filename));
        }

        /// <summary>
        /// Whether the file is readonly.
        /// Returns false for directories.
        /// Throws if file does not exist.
        /// </summary>
        internal bool IsReadOnly => !DirectoryExists && _data.Value.IsReadOnly;

        /// <summary>
        /// Whether the file exists.
        /// Returns false if it is a directory, even if it exists.
        /// Returns false instead of IO related exceptions.
        /// </summary>
        internal bool FileExists => !_data.Value.ThrowNonIoExceptionIfPending() && (_data.Value.Exists && !_data.Value.IsDirectory);

        /// <summary>
        /// Whether the directory exists.
        /// Returns false for files.
        /// Returns false instead of IO related exceptions.
        /// </summary>
        internal bool DirectoryExists => !_data.Value.ThrowNonIoExceptionIfPending() && (_data.Value.Exists && _data.Value.IsDirectory);

        /// <summary>
        /// Last time the file was written.
        /// Works for directories.
        /// </summary>
        internal DateTime LastWriteTime => LastWriteTimeUtcFast.ToLocalTime();

        /// <summary>
        /// Last time the file was written, in UTC. Avoids translation for daylight savings, time zone etc which isn't needed for just comparisons.
        /// If file does not exist, returns 12 midnight 1/1/1601.
        /// Works for directories.
        /// </summary>
        internal DateTime LastWriteTimeUtcFast
        {
            get
            {
                _data.Value.ThrowException();
                return _data.Value.Exists ? _data.Value.LastWriteTimeUtc : new DateTime(1601, 1, 1);
            }
        }

        /// <summary>
        /// Length of the file in bytes.
        /// Throws if it is a directory.
        /// Throws if it does not exist.
        /// </summary>
        internal long Length
        {
            get
            {
                _data.Value.ThrowException();
                _data.Value.ThrowFileInfoException(!_data.Value.Exists || _data.Value.IsDirectory);
                return _data.Value.Length;
            }
        }

        /// <summary>
        /// Name of the file as it was passed in.
        /// Not normalized.
        /// </summary>
        internal string Name => _filename;

        /// <summary>
        /// Whether this is a directory.
        /// Throws if it does not exist.
        /// </summary>
        internal bool IsDirectory
        {
            get
            {
                _data.Value.ThrowException();
                _data.Value.ThrowFileInfoException(!_data.Value.Exists);
                return _data.Value.IsDirectory;
            }
        }

        /// <summary>
        /// Use in case the state is known to have changed exogenously.
        /// </summary>
        internal void Reset()
        {
            _data = new Lazy<FileDirInfo>(() => new FileDirInfo(_filename));
        }
    }
}
