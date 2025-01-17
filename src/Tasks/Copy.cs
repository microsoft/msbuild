// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks.Dataflow;

using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;

using TPLTask = System.Threading.Tasks.Task;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// A task that copies files.
    /// </summary>
    public class Copy : TaskExtension, IIncrementalTask, ICancelableTask
    {
        internal const string AlwaysRetryEnvVar = "MSBUILDALWAYSRETRY";
        internal const string AlwaysOverwriteReadOnlyFilesEnvVar = "MSBUILDALWAYSOVERWRITEREADONLYFILES";

        // Default parallelism determined empirically - times below are in seconds spent in the Copy task building this repo
        // with "build -skiptests -rebuild -configuration Release /ds" (with hack to build.ps1 to disable creating selfhost
        // build for non-selfhost first build; implies first running build in repo to pull packages and create selfhost)
        // and comparing the task timings from the default and selfhost binlogs with different settings for this parallelism
        // number (via env var override). >=3 samples averaged for each number.
        //
        //                            Parallelism: | 1     2     3     4     5     6     8     MaxInt
        // ----------------------------------------+-------------------------------------------------
        // 2-core (4 hyperthreaded) M.2 SSD laptop | 22.3  17.5  13.4  12.6  13.1  9.52  11.3  10.9
        // 12-core (24 HT) SATA2 SSD 2012 desktop  | 15.1  10.2  9.57  7.29  7.64  7.41  7.67  7.79
        // 12-core (24 HT) 1TB spinny disk         | 22.7  15.03 11.1  9.23  11.7  11.1  9.27  11.1
        //
        // However note that since we are relying on synchronous File.Copy() - which will hold threadpool
        // threads at the advantage of performing file copies more quickly in the kernel - we must avoid
        // taking up the whole threadpool esp. when hosted in Visual Studio. IOW we use a specific number
        // instead of int.MaxValue.
        private static readonly int DefaultCopyParallelism = NativeMethodsShared.GetLogicalCoreCount() > 4 ? 6 : 4;
        private static Thread[] copyThreads;
        private static AutoResetEvent[] copyThreadSignals;
        private AutoResetEvent _signalCopyTasksCompleted;

        private static ConcurrentQueue<Action> _copyActionQueue = new ConcurrentQueue<Action>();

        private static void InitializeCopyThreads()
        {
            lock (_copyActionQueue)
            {
                if (copyThreads == null)
                {
                    copyThreadSignals = new AutoResetEvent[DefaultCopyParallelism];
                    copyThreads = new Thread[DefaultCopyParallelism];
                    for (int i = 0; i < copyThreads.Length; ++i)
                    {
                        AutoResetEvent autoResetEvent = new AutoResetEvent(false);
                        copyThreadSignals[i] = autoResetEvent;
                        Thread newThread = new Thread(ParallelCopyTask);
                        newThread.IsBackground = true;
                        newThread.Name = "Parallel Copy Thread";
                        newThread.Start(autoResetEvent);
                        copyThreads[i] = newThread;
                    }
                }
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Copy()
        {
            RetryDelayMilliseconds = RetryDelayMillisecondsDefault;

            if (DidNotCopyBecauseOfFileMatch == null)
            {
                CreatesDirectory = Log.GetResourceMessage("Copy.CreatesDirectory");
                DidNotCopyBecauseOfFileMatch = Log.GetResourceMessage("Copy.DidNotCopyBecauseOfFileMatch");
                FileComment = Log.GetResourceMessage("Copy.FileComment");
                HardLinkComment = Log.GetResourceMessage("Copy.HardLinkComment");
                RetryingAsFileCopy = Log.GetResourceMessage("Copy.RetryingAsFileCopy");
                RetryingAsSymbolicLink = Log.GetResourceMessage("Copy.RetryingAsSymbolicLink");
                RemovingReadOnlyAttribute = Log.GetResourceMessage("Copy.RemovingReadOnlyAttribute");
                SymbolicLinkComment = Log.GetResourceMessage("Copy.SymbolicLinkComment");
            }

            _signalCopyTasksCompleted = new AutoResetEvent(false);
        }

        private static string CreatesDirectory;
        private static string DidNotCopyBecauseOfFileMatch;
        private static string FileComment;
        private static string HardLinkComment;
        private static string RetryingAsFileCopy;
        private static string RetryingAsSymbolicLink;
        private static string RemovingReadOnlyAttribute;
        private static string SymbolicLinkComment;

        #region Properties

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        // Bool is just a placeholder, we're mainly interested in a threadsafe key set.
        private readonly ConcurrentDictionary<string, bool> _directoriesKnownToExist = new ConcurrentDictionary<string, bool>(DefaultCopyParallelism, DefaultCopyParallelism, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Force the copy to retry even when it hits ERROR_ACCESS_DENIED -- normally we wouldn't retry in this case since
        /// normally there's no point, but occasionally things get into a bad state temporarily, and retrying does actually
        /// succeed.  So keeping around a secret environment variable to allow forcing that behavior if necessary.
        /// </summary>
        private static bool s_alwaysRetryCopy = Environment.GetEnvironmentVariable(AlwaysRetryEnvVar) != null;

        /// <summary>
        /// Global flag to force on UseSymboliclinksIfPossible since Microsoft.Common.targets doesn't expose the functionality.
        /// </summary>
        private static readonly bool s_forceSymlinks = Environment.GetEnvironmentVariable("MSBuildUseSymboliclinksIfPossible") != null;

        private static readonly int s_parallelism = GetParallelismFromEnvironment();

        /// <summary>
        /// Default milliseconds to wait between necessary retries
        /// </summary>
        private const int RetryDelayMillisecondsDefault = 1000;

        public ITaskItem[] SourceFiles { get; set; }

        public ITaskItem[] SourceFolders { get; set; }

        public ITaskItem DestinationFolder { get; set; }

        /// <summary>
        /// Gets or sets the number of times to attempt to copy, if all previous attempts failed.
        /// Warning: using retries may mask a synchronization problem in your build process.
        /// </summary>
        public int Retries { get; set; } = 10;

        /// <summary>
        /// Gets or sets the delay, in milliseconds, between any necessary retries.
        /// Defaults to <see cref="RetryDelayMillisecondsDefault">RetryDelayMillisecondsDefault</see>
        /// </summary>
        public int RetryDelayMilliseconds { get; set; }

        /// <summary>
        /// Gets or sets a value that indicates whether to use hard links for the copied files
        /// rather than copy the files, if it's possible to do so.
        /// </summary>
        public bool UseHardlinksIfPossible { get; set; }

        /// <summary>
        /// Gets or sets a value that indicates whether to create symbolic links for the copied files
        /// rather than copy the files, if it's possible to do so.
        /// </summary>
        public bool UseSymboliclinksIfPossible { get; set; } = s_forceSymlinks;

        /// <summary>
        /// Fail if unable to create a symbolic or hard link instead of falling back to copy
        /// </summary>
        public bool ErrorIfLinkFails { get; set; }

        public bool SkipUnchangedFiles { get; set; }

        [Output]
        public ITaskItem[] DestinationFiles { get; set; }

        /// <summary>
        /// The subset of files that were successfully copied.
        /// </summary>
        [Output]
        public ITaskItem[] CopiedFiles { get; private set; }

        [Output]
        public bool WroteAtLeastOneFile { get; private set; }

        /// <summary>
        /// Gets or sets a value that indicates whether to overwrite files in the destination
        /// that have the read-only attribute set.
        /// </summary>
        public bool OverwriteReadOnlyFiles { get; set; }

        public bool FailIfNotIncremental { get; set; }

        #endregion

        /// <summary>
        /// Stop and return (in an undefined state) as soon as possible.
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource.Cancel();
        }

        #region ITask Members

        /// <summary>
        /// Method compares two files and returns true if their size and timestamp are identical.
        /// </summary>
        /// <param name="sourceFile">The source file</param>
        /// <param name="destinationFile">The destination file</param>
        private static bool IsMatchingSizeAndTimeStamp(
            FileState sourceFile,
            FileState destinationFile)
        {
            // If the destination doesn't exist, then it is not a matching file.
            if (!destinationFile.FileExists)
            {
                return false;
            }

            if (sourceFile.LastWriteTimeUtcFast != destinationFile.LastWriteTimeUtcFast)
            {
                return false;
            }

            if (sourceFile.Length != destinationFile.Length)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// INTERNAL FOR UNIT-TESTING ONLY
        ///
        /// We've got several environment variables that we read into statics since we don't expect them to ever
        /// reasonably change, but we need some way of refreshing their values so that we can modify them for
        /// unit testing purposes.
        /// </summary>
        internal static void RefreshInternalEnvironmentValues()
        {
            s_alwaysRetryCopy = Environment.GetEnvironmentVariable(AlwaysRetryEnvVar) != null;
        }

        /// <summary>
        /// If MSBUILDALWAYSRETRY is set, also log useful diagnostic information -- as
        /// a warning, so it's easily visible.
        /// </summary>
        private void LogAlwaysRetryDiagnosticFromResources(string messageResourceName, params object[] messageArgs)
        {
            if (s_alwaysRetryCopy)
            {
                Log.LogWarningWithCodeFromResources(messageResourceName, messageArgs);
            }
        }

        /// <summary>
        /// Copy one file from source to destination. Create the target directory if necessary and
        /// leave the file read-write.
        /// </summary>
        /// <returns>Return true to indicate success, return false to indicate failure and NO retry, return NULL to indicate retry.</returns>
        private bool? CopyFileWithLogging(
            FileState sourceFileState,
            FileState destinationFileState)
        {
            if (destinationFileState.DirectoryExists)
            {
                Log.LogErrorWithCodeFromResources("Copy.DestinationIsDirectory", sourceFileState.Name, destinationFileState.Name);
                return false;
            }

            if (sourceFileState.DirectoryExists)
            {
                // If the source file passed in is actually a directory instead of a file, log a nice
                // error telling the user so.  Otherwise, .NET Framework's File.Copy method will throw
                // an UnauthorizedAccessException saying "access is denied", which is not very useful
                // to the user.
                Log.LogErrorWithCodeFromResources("Copy.SourceIsDirectory", sourceFileState.Name);
                return false;
            }

            if (!sourceFileState.FileExists)
            {
                Log.LogErrorWithCodeFromResources("Copy.SourceFileNotFound", sourceFileState.Name);
                return false;
            }

            string destinationFolder = Path.GetDirectoryName(destinationFileState.Name);

            if (!string.IsNullOrEmpty(destinationFolder) && !_directoriesKnownToExist.ContainsKey(destinationFolder))
            {
                if (!FileSystems.Default.DirectoryExists(destinationFolder))
                {
                    if (FailIfNotIncremental)
                    {
                        Log.LogError(CreatesDirectory, destinationFolder);
                        return false;
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.Normal, CreatesDirectory, destinationFolder);
                        Directory.CreateDirectory(destinationFolder);
                    }
                }

                // It's very common for a lot of files to be copied to the same folder.
                // Eg., "c:\foo\a"->"c:\bar\a", "c:\foo\b"->"c:\bar\b" and so forth.
                // We don't want to check whether this folder exists for every single file we copy. So store which we've checked.
                _directoriesKnownToExist.TryAdd(destinationFolder, true);
            }

            if (FailIfNotIncremental)
            {
                Log.LogError(FileComment, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath);
                return false;
            }

            if (OverwriteReadOnlyFiles)
            {
                MakeFileWriteable(destinationFileState, true);
            }

            if (!Traits.Instance.EscapeHatches.CopyWithoutDelete &&
                (UseHardlinksIfPossible || UseSymboliclinksIfPossible) &&
                destinationFileState.FileExists &&
                !destinationFileState.IsReadOnly)
            {
                FileUtilities.DeleteNoThrow(destinationFileState.Name);
            }

            bool symbolicLinkCreated = false;
            bool hardLinkCreated = false;
            string errorMessage = string.Empty;

            // Create hard links if UseHardlinksIfPossible is true
            if (UseHardlinksIfPossible)
            {
                TryCopyViaLink(HardLinkComment, MessageImportance.Normal, sourceFileState, destinationFileState, out hardLinkCreated, ref errorMessage, (source, destination, errMessage) => NativeMethods.MakeHardLink(destination, source, ref errorMessage, Log));
                if (!hardLinkCreated)
                {
                    if (UseSymboliclinksIfPossible)
                    {
                        // This is a message for fallback to SymbolicLinks if HardLinks fail when UseHardlinksIfPossible and UseSymboliclinksIfPossible are true
                        Log.LogMessage(MessageImportance.Normal, RetryingAsSymbolicLink, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath, errorMessage);
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.Normal, RetryingAsFileCopy, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath, errorMessage);
                    }
                }
            }

            // Create symbolic link if UseSymboliclinksIfPossible is true and hard link is not created
            if (!hardLinkCreated && UseSymboliclinksIfPossible)
            {
                TryCopyViaLink(SymbolicLinkComment, MessageImportance.Normal, sourceFileState, destinationFileState, out symbolicLinkCreated, ref errorMessage, (source, destination, errMessage) => NativeMethodsShared.MakeSymbolicLink(destination, source, ref errorMessage));
                if (!symbolicLinkCreated)
                {
                    if (!NativeMethodsShared.IsWindows)
                    {
                        errorMessage = Log.FormatResourceString("Copy.NonWindowsLinkErrorMessage", "symlink()", errorMessage);
                    }

                    Log.LogMessage(MessageImportance.Normal, RetryingAsFileCopy, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath, errorMessage);
                }
            }

            if (ErrorIfLinkFails && !hardLinkCreated && !symbolicLinkCreated)
            {
                Log.LogErrorWithCodeFromResources("Copy.LinkFailed", sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath);
                return false;
            }

            // If the link was not created (either because the user didn't want one, or because it couldn't be created)
            // then let's copy the file
            if (!hardLinkCreated && !symbolicLinkCreated)
            {
                // Do not log a fake command line as well, as it's superfluous, and also potentially expensive
                Log.LogMessage(MessageImportance.Normal, FileComment, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath);

                File.Copy(sourceFileState.Name, destinationFileState.Name, true);
            }

            // If the destinationFile file exists, then make sure it's read-write.
            // The File.Copy command copies attributes, but our copy needs to
            // leave the file writeable.
            if (sourceFileState.IsReadOnly)
            {
                destinationFileState.Reset();
                MakeFileWriteable(destinationFileState, false);
            }

            // Files were successfully copied or linked. Those are equivalent here.
            WroteAtLeastOneFile = true;

            return true;
        }

        private void TryCopyViaLink(string linkComment, MessageImportance messageImportance, FileState sourceFileState, FileState destinationFileState, out bool linkCreated, ref string errorMessage, Func<string, string, string, bool> createLink)
        {
            // Do not log a fake command line as well, as it's superfluous, and also potentially expensive
            Log.LogMessage(MessageImportance.Normal, linkComment, sourceFileState.FileNameFullPath, destinationFileState.FileNameFullPath);

            linkCreated = createLink(sourceFileState.Name, destinationFileState.Name, errorMessage);
        }

        /// <summary>
        /// Ensure the read-only attribute on the specified file is off, so
        /// the file is writeable.
        /// </summary>
        private void MakeFileWriteable(FileState file, bool logActivity)
        {
            if (file.FileExists)
            {
                if (file.IsReadOnly)
                {
                    if (logActivity)
                    {
                        Log.LogMessage(MessageImportance.Low, RemovingReadOnlyAttribute, file.Name);
                    }

                    File.SetAttributes(file.Name, FileAttributes.Normal);
                    file.Reset();
                }
            }
        }

        /// <summary>
        /// Copy the files.
        /// </summary>
        /// <param name="copyFile">Delegate used to copy the files.</param>
        /// <param name="parallelism">
        /// Thread parallelism allowed during copies. 1 uses the original algorithm, >1 uses newer algorithm.
        /// </param>
        internal bool Execute(
            CopyFileWithState copyFile,
            int parallelism)
        {
            // If there are no source files then just return success.
            if (IsSourceSetEmpty())
            {
                DestinationFiles = Array.Empty<ITaskItem>();
                CopiedFiles = Array.Empty<ITaskItem>();
                return true;
            }

            if (!(ValidateInputs() && InitializeDestinationFiles()))
            {
                return false;
            }

            // Environment variable stomps on user-requested value if it's set.
            if (Environment.GetEnvironmentVariable(AlwaysOverwriteReadOnlyFilesEnvVar) != null)
            {
                OverwriteReadOnlyFiles = true;
            }

            // Track successfully copied subset.
            List<ITaskItem> destinationFilesSuccessfullyCopied;

            // Use single-threaded code path when requested or when there is only copy to make
            // (no need to create all the parallel infrastructure for that case).
            bool success = false;

            try
            {
                success = parallelism == 1 || DestinationFiles.Length == 1
                    ? CopySingleThreaded(copyFile, out destinationFilesSuccessfullyCopied)
                    : CopyParallel(copyFile, parallelism, out destinationFilesSuccessfullyCopied);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            // copiedFiles contains only the copies that were successful.
            CopiedFiles = destinationFilesSuccessfullyCopied.ToArray();

            return success && !_cancellationTokenSource.IsCancellationRequested;
        }

        /// <summary>
        /// Original copy code that performs single-threaded copies.
        /// Used for single-file copies and when parallelism is 1.
        /// </summary>
        private bool CopySingleThreaded(
            CopyFileWithState copyFile,
            out List<ITaskItem> destinationFilesSuccessfullyCopied)
        {
            bool success = true;
            destinationFilesSuccessfullyCopied = new List<ITaskItem>(DestinationFiles.Length);

            // Set of files we actually copied and the location from which they were originally copied.  The purpose
            // of this collection is to let us skip copying duplicate files.  We will only copy the file if it
            // either has never been copied to this destination before (key doesn't exist) or if we have copied it but
            // from a different location (value is different.)
            // { dest -> source }
            var filesActuallyCopied = new Dictionary<string, string>(
                DestinationFiles.Length, // Set length to common case of 1:1 source->dest.
                StringComparer.OrdinalIgnoreCase);

            // Now that we have a list of destinationFolder files, copy from source to destinationFolder.
            for (int i = 0; i < SourceFiles.Length && !_cancellationTokenSource.IsCancellationRequested; ++i)
            {
                bool copyComplete = false;
                string destPath = DestinationFiles[i].ItemSpec;
                MSBuildEventSource.Log.CopyUpToDateStart(destPath);
                if (filesActuallyCopied.TryGetValue(destPath, out string originalSource))
                {
                    if (String.Equals(originalSource, SourceFiles[i].ItemSpec, StringComparison.OrdinalIgnoreCase))
                    {
                        // Already copied from this location, don't copy again.
                        copyComplete = true;
                    }
                }

                if (!copyComplete)
                {
                    if (DoCopyIfNecessary(new FileState(SourceFiles[i].ItemSpec), new FileState(DestinationFiles[i].ItemSpec), copyFile))
                    {
                        filesActuallyCopied[destPath] = SourceFiles[i].ItemSpec;
                        copyComplete = true;
                    }
                    else
                    {
                        success = false;
                    }
                }
                else
                {
                    MSBuildEventSource.Log.CopyUpToDateStop(destPath, true);
                }

                if (copyComplete)
                {
                    SourceFiles[i].CopyMetadataTo(DestinationFiles[i]);
                    destinationFilesSuccessfullyCopied.Add(DestinationFiles[i]);
                }
            }

            return success;
        }

        private static void ParallelCopyTask(object state)
        {
            AutoResetEvent autoResetEvent = (AutoResetEvent)state;
            while (true)
            {
                if (_copyActionQueue.TryDequeue(out Action copyAction))
                {
                    copyAction();
                }
                else
                {
                    autoResetEvent.WaitOne();
                }
            }
        }

        /// <summary>
        /// Parallelize I/O with the same semantics as the single-threaded copy method above.
        /// ResolveAssemblyReferences tends to generate longer and longer lists of files to send
        /// to CopyTask as we get further and further down the dependency graph.
        /// The OS can handle a lot of parallel I/O so let's minimize wall clock time to get
        /// it all done.
        /// </summary>
        private bool CopyParallel(
            CopyFileWithState copyFile,
            int parallelism,
            out List<ITaskItem> destinationFilesSuccessfullyCopied)
        {
            bool success = true;

            // We must supply the same semantics as the single-threaded version above:
            //
            // - For copy operations in the list that have the same destination, we must
            //   provide for in-order copy attempts that allow re-copying different files
            //   and avoiding copies for later files that match SkipUnchangedFiles semantics.
            //   We must also add a destination file copy item for each attempt.
            // - The order of entries in destinationFilesSuccessfullyCopied must match
            //   the order of entries passed in, along with copied metadata.
            // - Metadata must not be copied to destination item if the copy operation failed.
            //
            // We split the work into different Tasks:
            //
            // - Entries with unique destination file paths each get their own parallel operation.
            // - Each subset of copies into the same destination get their own Task to run
            //   the single-threaded logic in order.
            //
            // At the end we reassemble the result list in the same order as was passed in.

            // Map: Destination path -> indexes in SourceFiles/DestinationItems array indices (ordered low->high).
            var partitionsByDestination = new Dictionary<string, List<int>>(
                DestinationFiles.Length, // Set length to common case of 1:1 source->dest.
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < SourceFiles.Length && !_cancellationTokenSource.IsCancellationRequested; ++i)
            {
                ITaskItem destItem = DestinationFiles[i];
                string destPath = destItem.ItemSpec;
                if (!partitionsByDestination.TryGetValue(destPath, out List<int> sourceIndices))
                {
                    // Use 1 for list length - common case is for no destination overlap.
                    sourceIndices = new List<int>(1);
                    partitionsByDestination[destPath] = sourceIndices;
                }
                sourceIndices.Add(i);
            }

            // Lockless flags updated from each thread - each needs to be a processor word for atomicity.
            var successFlags = new IntPtr[DestinationFiles.Length];

            ConcurrentQueue<List<int>> partitionQueue = new ConcurrentQueue<List<int>>(partitionsByDestination.Values);

            int activeCopyThreads = DefaultCopyParallelism;
            for (int i = 0; i < DefaultCopyParallelism; ++i)
            {
                _copyActionQueue.Enqueue(ProcessPartition);
            }

            InitializeCopyThreads();

            for (int i = 0; i < DefaultCopyParallelism; ++i)
            {
                copyThreadSignals[i].Set();
            }

            _signalCopyTasksCompleted.WaitOne();

            // Assemble an in-order list of destination items that succeeded.
            destinationFilesSuccessfullyCopied = new List<ITaskItem>(DestinationFiles.Length);
            for (int i = 0; i < successFlags.Length; i++)
            {
                if (successFlags[i] != (IntPtr)0)
                {
                    destinationFilesSuccessfullyCopied.Add(DestinationFiles[i]);
                }
            }

            return success;

            void ProcessPartition()
            {
                try
                {
                    while (partitionQueue.TryDequeue(out List<int> partition))
                    {
                        for (int partitionIndex = 0; partitionIndex < partition.Count && !_cancellationTokenSource.IsCancellationRequested; partitionIndex++)
                        {
                            int fileIndex = partition[partitionIndex];
                            ITaskItem sourceItem = SourceFiles[fileIndex];
                            ITaskItem destItem = DestinationFiles[fileIndex];
                            string sourcePath = sourceItem.ItemSpec;

                            // Check if we just copied from this location to the destination, don't copy again.
                            MSBuildEventSource.Log.CopyUpToDateStart(destItem.ItemSpec);
                            bool copyComplete = partitionIndex > 0 &&
                                                String.Equals(
                                                    sourcePath,
                                                    SourceFiles[partition[partitionIndex - 1]].ItemSpec,
                                                    StringComparison.OrdinalIgnoreCase);

                            if (!copyComplete)
                            {
                                if (DoCopyIfNecessary(
                                    new FileState(sourceItem.ItemSpec),
                                    new FileState(destItem.ItemSpec),
                                    copyFile))
                                {
                                    copyComplete = true;
                                }
                                else
                                {
                                    // Thread race to set outer variable but they race to set the same (false) value.
                                    success = false;
                                }
                            }
                            else
                            {
                                MSBuildEventSource.Log.CopyUpToDateStop(destItem.ItemSpec, true);
                            }

                            if (copyComplete)
                            {
                                sourceItem.CopyMetadataTo(destItem);
                                successFlags[fileIndex] = (IntPtr)1;
                            }
                        }
                    }
                }
                finally
                {
                    int count = System.Threading.Interlocked.Decrement(ref activeCopyThreads);
                    if (count == 0)
                    {
                        _signalCopyTasksCompleted.Set();
                    }
                }
            }
        }

        private bool IsSourceSetEmpty()
        {
            return (SourceFiles == null || SourceFiles.Length == 0) && (SourceFolders == null || SourceFolders.Length == 0);
        }

        /// <summary>
        /// Verify that the inputs are correct.
        /// </summary>
        /// <returns>False on an error, implying that the overall copy operation should be aborted.</returns>
        private bool ValidateInputs()
        {
            if (Retries < 0)
            {
                Log.LogErrorWithCodeFromResources("Copy.InvalidRetryCount", Retries);
                return false;
            }

            if (RetryDelayMilliseconds < 0)
            {
                Log.LogErrorWithCodeFromResources("Copy.InvalidRetryDelay", RetryDelayMilliseconds);
                return false;
            }

            // There must be a destination (either files or directory).
            if (DestinationFiles == null && DestinationFolder == null)
            {
                Log.LogErrorWithCodeFromResources("Copy.NeedsDestination", "DestinationFiles", "DestinationFolder");
                return false;
            }

            // There can't be two kinds of destination.
            if (DestinationFiles != null && DestinationFolder != null)
            {
                Log.LogErrorWithCodeFromResources("Copy.ExactlyOneTypeOfDestination", "DestinationFiles", "DestinationFolder");
                return false;
            }

            // SourceFolders and DestinationFiles can't be used together.
            if (SourceFolders != null && DestinationFiles != null)
            {
                Log.LogErrorWithCodeFromResources("Copy.IncompatibleParameters", "SourceFolders", "DestinationFiles");
                return false;
            }

            // If the caller passed in DestinationFiles, then its length must match SourceFiles.
            if (DestinationFiles != null && DestinationFiles.Length != SourceFiles.Length)
            {
                Log.LogErrorWithCodeFromResources("General.TwoVectorsMustHaveSameLength", DestinationFiles.Length, SourceFiles.Length, "DestinationFiles", "SourceFiles");
                return false;
            }

            if (ErrorIfLinkFails && !UseHardlinksIfPossible && !UseSymboliclinksIfPossible)
            {
                Log.LogErrorWithCodeFromResources("Copy.ErrorIfLinkFailsSetWithoutLinkOption");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Set up our list of destination files.
        /// For SourceFiles: Apply DestinationFolder to each SourceFiles item to create a DestinationFiles item.
        /// For SourceFolders: With each SourceFolders item, get the files in the represented directory. Create both SourceFiles and DestinationFiles items.
        /// </summary>
        /// <returns>False if an error occurred, implying aborting the overall copy operation.</returns>
        private bool InitializeDestinationFiles()
        {
            bool isSuccess = true;

            try
            {
                // If the caller passed in DestinationFolder, convert it to DestinationFiles
                if (DestinationFiles == null && SourceFiles != null)
                {
                    DestinationFiles = new ITaskItem[SourceFiles.Length];

                    for (int i = 0; i < SourceFiles.Length; ++i)
                    {
                        // Build the correct path.
                        if (!TryPathOperation(
                                () => Path.Combine(DestinationFolder.ItemSpec, Path.GetFileName(SourceFiles[i].ItemSpec)),
                                SourceFiles[i].ItemSpec,
                                DestinationFolder.ItemSpec,
                                out string destinationFile))
                        {
                            isSuccess = false;
                            break;
                        }

                        // Initialize the destinationFolder item.
                        // ItemSpec is unescaped, and the TaskItem constructor expects an escaped input, so we need to
                        // make sure to re-escape it here.
                        DestinationFiles[i] = new TaskItem(EscapingUtilities.Escape(destinationFile));

                        // Copy meta-data from source to destinationFolder.
                        SourceFiles[i].CopyMetadataTo(DestinationFiles[i]);
                    }
                }

                if (isSuccess && SourceFolders != null && SourceFolders.Length > 0)
                {
                    var sourceFiles = SourceFiles != null ? new List<ITaskItem>(SourceFiles) : new List<ITaskItem>();
                    var destinationFiles = DestinationFiles != null ? new List<ITaskItem>(DestinationFiles) : new List<ITaskItem>();

                    foreach (ITaskItem sourceFolder in SourceFolders)
                    {
                        string src = FileUtilities.NormalizePath(sourceFolder.ItemSpec);
                        string srcName = Path.GetFileName(src);

                        (string[] filesInFolder, _, _, string globFailure) = FileMatcher.Default.GetFiles(src, "**");
                        if (globFailure != null)
                        {
                            Log.LogMessage(MessageImportance.Low, globFailure);
                        }

                        foreach (string file in filesInFolder)
                        {
                            if (!TryPathOperation(
                                    () => Path.Combine(src, file),
                                    sourceFolder.ItemSpec,
                                    DestinationFolder.ItemSpec,
                                    out string sourceFile))
                            {
                                isSuccess = false;
                                break;
                            }

                            if (!TryPathOperation(
                                    () => Path.Combine(DestinationFolder.ItemSpec, srcName, file),
                                    sourceFolder.ItemSpec,
                                    DestinationFolder.ItemSpec,
                                    out string destinationFile))
                            {
                                isSuccess = false;
                                break;
                            }


                            var item = new TaskItem(EscapingUtilities.Escape(sourceFile));
                            sourceFolder.CopyMetadataTo(item);
                            sourceFiles.Add(item);

                            item = new TaskItem(EscapingUtilities.Escape(destinationFile));
                            sourceFolder.CopyMetadataTo(item);
                            destinationFiles.Add(item);
                        }
                    }

                    SourceFiles = sourceFiles.ToArray();
                    DestinationFiles = destinationFiles.ToArray();
                }
            }
            finally
            {
                if (!isSuccess)
                {
                    // Clear the outputs.
                    DestinationFiles = Array.Empty<ITaskItem>();
                }
            }

            return isSuccess;
        }

        /// <summary>
        /// Tries the path operation. Logs a 'Copy.Error' if an exception is thrown.
        /// </summary>
        /// <param name="operation">The operation.</param>
        /// <param name="src">The source to use for the log message.</param>
        /// <param name="dest">The destination to use for the log message.</param>
        /// <param name="resultPathOperation">The result of the path operation.</param>
        /// <returns></returns>
        private bool TryPathOperation(Func<string> operation, string src, string dest, out string resultPathOperation)
        {
            resultPathOperation = string.Empty;

            try
            {
                resultPathOperation = operation();
            }
            catch (ArgumentException e)
            {
                Log.LogErrorWithCodeFromResources("Copy.Error", src, dest, e.Message);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Copy source to destination, unless SkipUnchangedFiles is true and they are equivalent.
        /// </summary>
        /// <returns>True if the file was copied or, on SkipUnchangedFiles, the file was equivalent.</returns>
        private bool DoCopyIfNecessary(FileState sourceFileState, FileState destinationFileState, CopyFileWithState copyFile)
        {
            bool success = true;

            try
            {
                if (SkipUnchangedFiles && IsMatchingSizeAndTimeStamp(sourceFileState, destinationFileState))
                {
                    // If we got here, then the file's time and size match AND
                    // the user set the SkipUnchangedFiles flag which means we
                    // should skip matching files.
                    Log.LogMessage(
                        MessageImportance.Low,
                        DidNotCopyBecauseOfFileMatch,
                        sourceFileState.Name,
                        destinationFileState.Name,
                        "SkipUnchangedFiles",
                        "true");
                    MSBuildEventSource.Log.CopyUpToDateStop(destinationFileState.Name, true);
                }
                else if (!PathsAreIdentical(sourceFileState, destinationFileState))
                {
                    MSBuildEventSource.Log.CopyUpToDateStop(destinationFileState.Name, false);

                    if (FailIfNotIncremental)
                    {
                        Log.LogError(FileComment, sourceFileState.Name, destinationFileState.Name);
                        success = false;
                    }
                    else
                    {
                        success = DoCopyWithRetries(sourceFileState, destinationFileState, copyFile);
                    }
                }
                else
                {
                    MSBuildEventSource.Log.CopyUpToDateStop(destinationFileState.Name, true);
                }
            }
            catch (OperationCanceledException)
            {
                success = false;
            }
            catch (PathTooLongException e)
            {
                Log.LogErrorWithCodeFromResources("Copy.Error", sourceFileState.Name, destinationFileState.Name, e.Message);
                success = false;
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources("Copy.Error", sourceFileState.Name, destinationFileState.Name, e.Message);
                success = false;
            }

            return success;
        }

        /// <summary>
        /// Copy one file with the appropriate number of retries if it fails.
        /// </summary>
        private bool DoCopyWithRetries(FileState sourceFileState, FileState destinationFileState, CopyFileWithState copyFile)
        {
            int retries = 0;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    bool? result = copyFile(sourceFileState, destinationFileState);
                    if (result.HasValue)
                    {
                        return result.Value;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    switch (e)
                    {
                        case ArgumentException: // Invalid chars
                        case NotSupportedException: // Colon in the middle of the path
                        case PathTooLongException:
                            throw;
                        case UnauthorizedAccessException:
                        case IOException: // Not clear why we can get one and not the other
                            int code = Marshal.GetHRForException(e);

                            LogAlwaysRetryDiagnosticFromResources("Copy.IOException", e.ToString(), sourceFileState.Name, destinationFileState.Name, code);
                            if (code == NativeMethods.ERROR_ACCESS_DENIED)
                            {
                                // ERROR_ACCESS_DENIED can either mean there's an ACL preventing us, or the file has the readonly bit set.
                                // In either case, that's likely not a race, and retrying won't help.
                                // Retrying is mainly for ERROR_SHARING_VIOLATION, where someone else is using the file right now.
                                // However, there is a limited set of circumstances where a copy failure will show up as access denied due
                                // to a failure to reset the readonly bit properly, in which case retrying will succeed.  This seems to be
                                // a pretty edge scenario, but since some of our internal builds appear to be hitting it, provide a secret
                                // environment variable to allow overriding the default behavior and forcing retries in this circumstance as well.
                                if (!s_alwaysRetryCopy)
                                {
                                    throw;
                                }
                                else
                                {
                                    LogAlwaysRetryDiagnosticFromResources("Copy.RetryingOnAccessDenied");
                                }
                            }
                            else if (code == NativeMethods.ERROR_INVALID_FILENAME)
                            {
                                // Invalid characters used in file name; no point retrying.
                                throw;
                            }

                            if (e is UnauthorizedAccessException)
                            {
                                break;
                            }

                            if (DestinationFolder != null && FileSystems.Default.FileExists(DestinationFolder.ItemSpec))
                            {
                                // We failed to create the DestinationFolder because it's an existing file. No sense retrying.
                                // We don't check for this case upstream because it'd be another hit to the filesystem.
                                throw;
                            }

                            break;
                    }

                    if (retries < Retries)
                    {
                        retries++;
                        Log.LogWarningWithCodeFromResources("Copy.Retrying", sourceFileState.Name,
                            destinationFileState.Name, retries, RetryDelayMilliseconds, e.Message,
                            LockCheck.GetLockedFileMessage(destinationFileState.Name));

                        // if we have to retry for some reason, wipe the state -- it may not be correct anymore.
                        destinationFileState.Reset();

                        Thread.Sleep(RetryDelayMilliseconds);
                        continue;
                    }
                    else if (Retries > 0)
                    {
                        // Exception message is logged in caller
                        Log.LogErrorWithCodeFromResources("Copy.ExceededRetries", sourceFileState.Name,
                            destinationFileState.Name, Retries, LockCheck.GetLockedFileMessage(destinationFileState.Name));
                        throw;
                    }
                    else
                    {
                        throw;
                    }
                }

                if (retries < Retries)
                {
                    retries++;
                    Log.LogWarningWithCodeFromResources("Copy.Retrying", sourceFileState.Name,
                        destinationFileState.Name, retries, RetryDelayMilliseconds, String.Empty /* no details */,
                        LockCheck.GetLockedFileMessage(destinationFileState.Name));

                    // if we have to retry for some reason, wipe the state -- it may not be correct anymore.
                    destinationFileState.Reset();

                    Thread.Sleep(RetryDelayMilliseconds);
                }
                else if (Retries > 0)
                {
                    Log.LogErrorWithCodeFromResources("Copy.ExceededRetries", sourceFileState.Name,
                        destinationFileState.Name, Retries, LockCheck.GetLockedFileMessage(destinationFileState.Name));
                    return false;
                }
                else
                {
                    return false;
                }
            }

            // Canceling
            return false;
        }

        /// <summary>
        /// Standard entry point.
        /// </summary>
        /// <returns></returns>
        public override bool Execute()
        {
            return Execute(CopyFileWithLogging, s_parallelism);
        }

        #endregion

        /// <summary>
        /// Compares two paths to see if they refer to the same file. We can't solve the general
        /// canonicalization problem, so we just compare strings on the full paths.
        /// </summary>
        private static bool PathsAreIdentical(FileState source, FileState destination)
        {
            if (string.Equals(source.Name, destination.Name, FileUtilities.PathComparison))
            {
                return true;
            }

            source.FileNameFullPath = Path.GetFullPath(source.Name);
            destination.FileNameFullPath = Path.GetFullPath(destination.Name);
            return string.Equals(source.FileNameFullPath, destination.FileNameFullPath, FileUtilities.PathComparison);
        }

        private static int GetParallelismFromEnvironment()
        {
            int parallelism = Traits.Instance.CopyTaskParallelism;
            if (parallelism < 0)
            {
                parallelism = DefaultCopyParallelism;
            }
            else if (parallelism == 0)
            {
                parallelism = int.MaxValue;
            }
            return parallelism;
        }
    }
}
