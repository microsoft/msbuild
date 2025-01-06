// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_FILE_TRACKER

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Resources;
using System.Text;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Utilities
{
    /// <summary>
    /// Class used to store and interrogate inputs and outputs recorded by tracking operations.
    /// </summary>
    public class FlatTrackingData
    {
#pragma warning disable format // region formatting is different in net7.0 and net472, and cannot be fixed for both
        #region Constants
        // The maximum number of outputs that should be logged, if more than this, then no outputs are logged
        private const int MaxLogCount = 100;
        #endregion

        #region Member Data
        // The .write. trackg log files

        // The tlog marker is used if the tracking data is empty
        // even if the tracked execution was successful
        private string _tlogMarker = string.Empty;

        // The TaskLoggingHelper that we log progress to
        private TaskLoggingHelper _log;

        // The oldest file that we have seen
        private DateTime _oldestFileTimeUtc = DateTime.MaxValue;

        // The newest file what we have seen
        private DateTime _newestFileTimeUtc = DateTime.MinValue;

        // Should rooting markers be treated as tracking entries
        private bool _treatRootMarkersAsEntries;

        // If we are not skipping missing files, what DateTime should they be given?
        private DateTime _missingFileTimeUtc = DateTime.MinValue;

        // The newest Tlog that we have seen
        private DateTime _newestTLogTimeUtc = DateTime.MinValue;

        // Cache of last write times
        private readonly IDictionary<string, DateTime> _lastWriteTimeUtcCache = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // The set of paths that contain files that are to be ignored during up to date check - these directories or their subdirectories
        private readonly List<string> _excludedInputPaths = new List<string>();
        #endregion

        #region Properties

        // The output dependency table
        internal Dictionary<string, DateTime> DependencyTable { get; private set; } = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Missing files have been detected in the TLog
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Has shipped as public API, so we can't easily change it now. ")]
        public List<string> MissingFiles { get; set; } = new List<string>();

        /// <summary>
        /// The path for the oldest file we have seen
        /// </summary>
        public string OldestFileName { get; set; } = string.Empty;

        /// <summary>
        /// The time for the oldest file we have seen
        /// </summary>
        public DateTime OldestFileTime
        {
            get => _oldestFileTimeUtc.ToLocalTime();
            set => _oldestFileTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// The time for the oldest file we have seen
        /// </summary>
        public DateTime OldestFileTimeUtc
        {
            get => _oldestFileTimeUtc;
            set => _oldestFileTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// The path for the newest file we have seen
        /// </summary>
        public string NewestFileName { get; set; } = string.Empty;

        /// <summary>
        /// The time for the newest file we have seen
        /// </summary>
        public DateTime NewestFileTime
        {
            get => _newestFileTimeUtc.ToLocalTime();
            set => _newestFileTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// The time for the newest file we have seen
        /// </summary>
        public DateTime NewestFileTimeUtc
        {
            get => _newestFileTimeUtc;
            set => _newestFileTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// Should root markers in the TLog be treated as file accesses, or only as markers?
        /// </summary>
        public bool TreatRootMarkersAsEntries
        {
            get => _treatRootMarkersAsEntries;
            set => _treatRootMarkersAsEntries = value;
        }

        /// <summary>
        /// Should files in the TLog but no longer exist be skipped or recorded?
        /// </summary>
        public bool SkipMissingFiles { get; set; }

        /// <summary>
        /// The TLog files that back this structure
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "Has shipped as public API, so we can't easily change it now. ")]
        public ITaskItem[] TlogFiles { get; set; }

        /// <summary>
        /// The time of the newest Tlog
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public DateTime NewestTLogTime
        {
            get => _newestTLogTimeUtc.ToLocalTime();
            set => _newestTLogTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// The time of the newest Tlog
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public DateTime NewestTLogTimeUtc
        {
            get => _newestTLogTimeUtc;
            set => _newestTLogTimeUtc = value.ToUniversalTime();
        }

        /// <summary>
        /// The path of the newest TLog file
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public string NewestTLogFileName { get; set; } = string.Empty;

        /// <summary>
        /// Are all the TLogs that were passed to us actually available on disk?
        /// </summary>
        public bool TlogsAvailable { get; set; }

        #endregion

        #region Constructors
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        public FlatTrackingData(ITaskItem[] tlogFiles, DateTime missingFileTimeUtc) => InternalConstruct(null, tlogFiles, null, false, missingFileTimeUtc, null);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        /// <param name="tlogFilesToIgnore">The .tlog files to ignore</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        public FlatTrackingData(ITaskItem[] tlogFiles, ITaskItem[] tlogFilesToIgnore, DateTime missingFileTimeUtc) => InternalConstruct(null, tlogFiles, tlogFilesToIgnore, false, missingFileTimeUtc, null);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .tlog files to interpret</param>
        /// <param name="tlogFilesToIgnore">The .tlog files to ignore</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check, including any subdirectories.</param>
        /// <param name="sharedLastWriteTimeUtcCache">Cache to be used for all timestamp/exists comparisons, which can be shared between multiple FlatTrackingData instances.</param>
        public FlatTrackingData(ITaskItem[] tlogFiles, ITaskItem[] tlogFilesToIgnore, DateTime missingFileTimeUtc, string[] excludedInputPaths, IDictionary<string, DateTime> sharedLastWriteTimeUtcCache)
        {
            if (sharedLastWriteTimeUtcCache != null)
            {
                _lastWriteTimeUtcCache = sharedLastWriteTimeUtcCache;
            }

            InternalConstruct(null, tlogFiles, tlogFilesToIgnore, false, missingFileTimeUtc, excludedInputPaths);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .tlog files to interpret</param>
        /// <param name="tlogFilesToIgnore">The .tlog files to ignore</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check, including any subdirectories.</param>
        /// <param name="sharedLastWriteTimeUtcCache">Cache to be used for all timestamp/exists comparisons, which can be shared between multiple FlatTrackingData instances.</param>
        /// <param name="treatRootMarkersAsEntries">Add root markers as inputs.</param>
        public FlatTrackingData(ITaskItem[] tlogFiles, ITaskItem[] tlogFilesToIgnore, DateTime missingFileTimeUtc, string[] excludedInputPaths, IDictionary<string, DateTime> sharedLastWriteTimeUtcCache, bool treatRootMarkersAsEntries)
        {
            _treatRootMarkersAsEntries = treatRootMarkersAsEntries;

            if (sharedLastWriteTimeUtcCache != null)
            {
                _lastWriteTimeUtcCache = sharedLastWriteTimeUtcCache;
            }

            InternalConstruct(null, tlogFiles, tlogFilesToIgnore, false, missingFileTimeUtc, excludedInputPaths);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The tlog files to interpret</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        public FlatTrackingData(ITask ownerTask, ITaskItem[] tlogFiles, DateTime missingFileTimeUtc) => InternalConstruct(ownerTask, tlogFiles, null, false, missingFileTimeUtc, null);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        /// <param name="skipMissingFiles">Ignore files that do not exist on disk</param>
        public FlatTrackingData(ITaskItem[] tlogFiles, bool skipMissingFiles) => InternalConstruct(null, tlogFiles, null, skipMissingFiles, DateTime.MinValue, null);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The tlog files to interpret</param>
        /// <param name="skipMissingFiles">Ignore files that do not exist on disk</param>
        public FlatTrackingData(ITask ownerTask, ITaskItem[] tlogFiles, bool skipMissingFiles) => InternalConstruct(ownerTask, tlogFiles, null, skipMissingFiles, DateTime.MinValue, null);

        /// <summary>
        /// Internal constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFilesLocal">The local .tlog files.</param>
        /// <param name="tlogFilesToIgnore">The .tlog files to ignore</param>
        /// <param name="skipMissingFiles">Ignore files that do not exist on disk</param>
        /// <param name="missingFileTimeUtc">The DateTime that should be recorded for missing file.</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        private void InternalConstruct(ITask ownerTask, ITaskItem[] tlogFilesLocal, ITaskItem[] tlogFilesToIgnore, bool skipMissingFiles, DateTime missingFileTimeUtc, string[] excludedInputPaths)
        {
            if (ownerTask != null)
            {
                _log = new TaskLoggingHelper(ownerTask)
                {
                    TaskResources = AssemblyResources.PrimaryResources,
                    HelpKeywordPrefix = "MSBuild."
                };
            }

            ITaskItem[] expandedTlogFiles = TrackedDependencies.ExpandWildcards(tlogFilesLocal, _log);

            if (tlogFilesToIgnore != null)
            {
                ITaskItem[] expandedTlogFilesToIgnore = TrackedDependencies.ExpandWildcards(tlogFilesToIgnore, _log);

                if (expandedTlogFilesToIgnore.Length > 0)
                {
                    var ignore = new HashSet<string>();
                    var remainingTlogFiles = new List<ITaskItem>();

                    foreach (ITaskItem tlogFileToIgnore in expandedTlogFilesToIgnore)
                    {
                        ignore.Add(tlogFileToIgnore.ItemSpec);
                    }

                    foreach (ITaskItem tlogFile in expandedTlogFiles)
                    {
                        if (!ignore.Contains(tlogFile.ItemSpec))
                        {
                            remainingTlogFiles.Add(tlogFile);
                        }
                    }

                    TlogFiles = remainingTlogFiles.ToArray();
                }
                else
                {
                    TlogFiles = expandedTlogFiles;
                }
            }
            else
            {
                TlogFiles = expandedTlogFiles;
            }

            // We have no TLog files on disk, create a TLog marker from the
            // TLogFiles ItemSpec so we can fabricate one if we need to
            // This becomes our "first" tlog, since on the very first run, no tlogs
            // will exist, and if a compaction has been run (as part of the initial up-to-date check) then this
            // marker tlog will be created as empty.
            if (TlogFiles == null || TlogFiles.Length == 0)
            {
                _tlogMarker = tlogFilesLocal[0].ItemSpec
                    .Replace("*", "1")
                    .Replace("?", "2");
            }

            if (excludedInputPaths != null)
            {
                // Assign our exclude paths to our lookup - and make sure that all recorded paths end in a slash so that
                // our "starts with" comparison doesn't pick up incomplete matches, such as C:\Foo matching C:\FooFile.txt
                foreach (string excludePath in excludedInputPaths)
                {
                    string fullexcludePath = FileUtilities.EnsureTrailingSlash(FileUtilities.NormalizePath(excludePath)).ToUpperInvariant();
                    _excludedInputPaths.Add(fullexcludePath);
                }
            }

            TlogsAvailable = TrackedDependencies.ItemsExist(TlogFiles);
            SkipMissingFiles = skipMissingFiles;
            _missingFileTimeUtc = missingFileTimeUtc.ToUniversalTime();
            if (TlogFiles != null)
            {
                // Read the TLogs into our internal structures
                ConstructFileTable();
            }
        }
        #endregion

        #region Methods
        /// <summary>
        /// Construct our dependency table for our source files
        /// </summary>
        private void ConstructFileTable()
        {
            string tLogRootingMarker;
            try
            {
                // construct a rooting marker from the tlog files
                tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(TlogFiles);
            }
            catch (ArgumentException e)
            {
                FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLog", e.Message);
                return;
            }
            if (!TlogsAvailable)
            {
                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs are not available, they may have been deleted at some point.
                    // Be safe and remove any references from the cache.
                    DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                }
                return;
            }

            DependencyTableCacheEntry cachedEntry;

            lock (DependencyTableCache.DependencyTable)
            {
                // Look in the dependency table cache to see if its available and up to date
                cachedEntry = DependencyTableCache.GetCachedEntry(tLogRootingMarker);
            }

            // We have an up to date cached entry
            if (cachedEntry != null)
            {
                DependencyTable = (Dictionary<string, DateTime>)cachedEntry.DependencyTable;

                // We may have stored the dependency table in the cache, but all the other information
                // (newest file time, number of missing files, etc.) has been reset to default.  Refresh
                // the data.
                UpdateFileEntryDetails();

                // Log information about what we're using
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_TrackingCached");
                foreach (ITaskItem tlogItem in cachedEntry.TlogFiles)
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogItem.ItemSpec);
                }
                return;
            }

            FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_TrackingLogs");
            // Now we need to construct the rest of the table from the TLOG files
            // If there are any errors in the tlogs, we want to warn, stop parsing tlogs, and empty
            // out the dependency table, essentially forcing a rebuild.
            bool encounteredInvalidTLogContents = false;
            string invalidTLogName = null;
            foreach (ITaskItem tlogFileName in TlogFiles)
            {
                try
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogFileName.ItemSpec);

                    DateTime tlogLastWriteTimeUtc = NativeMethodsShared.GetLastWriteFileUtcTime(tlogFileName.ItemSpec);
                    if (tlogLastWriteTimeUtc > _newestTLogTimeUtc)
                    {
                        _newestTLogTimeUtc = tlogLastWriteTimeUtc;
                        NewestTLogFileName = tlogFileName.ItemSpec;
                    }

                    using (StreamReader tlog = File.OpenText(tlogFileName.ItemSpec))
                    {
                        string tlogEntry = tlog.ReadLine();

                        while (tlogEntry != null)
                        {
                            if (tlogEntry.Length == 0) // empty lines are a sign that something has gone wrong
                            {
                                encounteredInvalidTLogContents = true;
                                invalidTLogName = tlogFileName.ItemSpec;
                                break;
                            }
                            // Preprocessing for the line entry
                            else if (tlogEntry[0] == '#') // a comment marker should be skipped
                            {
                                tlogEntry = tlog.ReadLine();
                                continue;
                            }
                            else if (tlogEntry[0] == '^' && TreatRootMarkersAsEntries && tlogEntry.IndexOf('|') < 0) // This is a rooting non composite record, and we should keep it
                            {
                                tlogEntry = tlogEntry.Substring(1);

                                if (tlogEntry.Length == 0)
                                {
                                    encounteredInvalidTLogContents = true;
                                    invalidTLogName = tlogFileName.ItemSpec;
                                    break;
                                }
                            }
                            else if (tlogEntry[0] == '^') // root marker is not being treated as an entry, skip it
                            {
                                tlogEntry = tlog.ReadLine();
                                continue;
                            }

                            // If we haven't seen this file before, then record it
                            if (!DependencyTable.ContainsKey(tlogEntry))
                            {
                                // It may be that this is one of the locations that we should ignore
                                if (!FileTracker.FileIsExcludedFromDependencies(tlogEntry))
                                {
                                    RecordEntryDetails(tlogEntry, true);
                                }
                            }
                            tlogEntry = tlog.ReadLine();
                        }
                    }
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLog", e.Message);
                    break;
                }

                if (encounteredInvalidTLogContents)
                {
                    FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLogContents", invalidTLogName);
                    break;
                }
            }

            lock (DependencyTableCache.DependencyTable)
            {
                // There were problems with the tracking logs -- we've already warned or errored; now we want to make
                // sure that we essentially force a rebuild of this particular root.
                if (encounteredInvalidTLogContents)
                {
                    DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                    DependencyTable = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Record the newly built dependency table in the cache
                    DependencyTableCache.DependencyTable[tLogRootingMarker] = new DependencyTableCacheEntry(TlogFiles, DependencyTable);
                }
            }
        }

        /// <summary>
        /// Update the current state of entry details for the dependency table
        /// </summary>
        public void UpdateFileEntryDetails()
        {
            OldestFileName = string.Empty;
            _oldestFileTimeUtc = DateTime.MaxValue;

            NewestFileName = string.Empty;
            _newestFileTimeUtc = DateTime.MinValue;

            NewestTLogFileName = string.Empty;
            _newestTLogTimeUtc = DateTime.MinValue;

            MissingFiles.Clear();

            // First update the details of our Tlogs
            foreach (ITaskItem tlogFileName in TlogFiles)
            {
                DateTime tlogLastWriteTimeUtc = NativeMethodsShared.GetLastWriteFileUtcTime(tlogFileName.ItemSpec);
                if (tlogLastWriteTimeUtc > _newestTLogTimeUtc)
                {
                    _newestTLogTimeUtc = tlogLastWriteTimeUtc;
                    NewestTLogFileName = tlogFileName.ItemSpec;
                }
            }

            // Now for each entry in the table
            foreach (string entry in DependencyTable.Keys)
            {
                RecordEntryDetails(entry, false);
            }
        }

        /// <summary>
        /// Test to see if the specified file is excluded from tracked dependency checking
        /// </summary>
        /// <param name="fileName">
        /// Full path of the file to test
        /// </param>
        /// <remarks>
        /// The file is excluded if it is within any of the specified excluded input paths or any subdirectory of the paths.
        /// It also assumes the file name is already converted to Uppercase Invariant.
        /// </remarks>
        public bool FileIsExcludedFromDependencyCheck(string fileName)
        {
            foreach (string path in _excludedInputPaths)
            {
                if (fileName.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Record the time and missing state of the entry in the tlog
        /// </summary>
        private void RecordEntryDetails(string tlogEntry, bool populateTable)
        {
            if (FileIsExcludedFromDependencyCheck(tlogEntry))
            {
                return;
            }

            DateTime fileModifiedTimeUtc = GetLastWriteTimeUtc(tlogEntry);
            if (SkipMissingFiles && fileModifiedTimeUtc == DateTime.MinValue) // the file is missing
            {
                return;
            }
            else if (fileModifiedTimeUtc == DateTime.MinValue)
            {
                // Record the file in our table even though it was missing
                // use the missingFileTimeUtc as indicated.
                if (populateTable)
                {
                    DependencyTable[tlogEntry] = _missingFileTimeUtc.ToUniversalTime();
                }
                MissingFiles.Add(tlogEntry);
            }
            else
            {
                if (populateTable)
                {
                    DependencyTable[tlogEntry] = fileModifiedTimeUtc;
                }
            }

            // Record this file if it is newer than our current newest
            if (fileModifiedTimeUtc > _newestFileTimeUtc)
            {
                _newestFileTimeUtc = fileModifiedTimeUtc;
                NewestFileName = tlogEntry;
            }

            // Record this file if it is older than our current oldest
            if (fileModifiedTimeUtc < _oldestFileTimeUtc)
            {
                _oldestFileTimeUtc = fileModifiedTimeUtc;
                OldestFileName = tlogEntry;
            }
        }

        /// <summary>
        /// This method will re-write the tlogs from the output table
        /// </summary>
        public void SaveTlog() => SaveTlog(null);

        /// <summary>
        /// This method will re-write the tlogs from the current table
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public void SaveTlog(DependencyFilter includeInTLog)
        {
            if (TlogFiles?.Length > 0)
            {
                string tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(TlogFiles);

                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs in the cache will be invalidated by this write
                    // remove the cached entries to be sure
                    DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                }

                string firstTlog = TlogFiles[0].ItemSpec;

                // empty all tlogs
                foreach (ITaskItem tlogFile in TlogFiles)
                {
                    File.WriteAllText(tlogFile.ItemSpec, "", Encoding.Unicode);
                }

                // Write out the dependency information as a new tlog
                using (StreamWriter newTlog = FileUtilities.OpenWrite(firstTlog, false, Encoding.Unicode))
                {
                    foreach (string fileEntry in DependencyTable.Keys)
                    {
                        // Give the task a chance to filter dependencies out of the written TLog
                        if (includeInTLog == null || includeInTLog(fileEntry))
                        {
                            // Write out the entry
                            newTlog.WriteLine(fileEntry);
                        }
                    }
                }
            }
            else if (_tlogMarker != string.Empty)
            {
                string markerDirectory = Path.GetDirectoryName(_tlogMarker);
                if (!FileSystems.Default.DirectoryExists(markerDirectory))
                {
                    Directory.CreateDirectory(markerDirectory);
                }

                // There were no TLogs to save, so use the TLog marker
                // to create a marker file that can be used for up-to-date check.
                File.WriteAllText(_tlogMarker, "");
            }
        }

        /// <summary>
        /// Returns cached value for last write time of file. Update the cache if it is the first
        /// time someone asking for that file
        /// </summary>
        public DateTime GetLastWriteTimeUtc(string file)
        {
            if (!_lastWriteTimeUtcCache.TryGetValue(file, out DateTime fileModifiedTimeUtc))
            {
                fileModifiedTimeUtc = NativeMethodsShared.GetLastWriteFileUtcTime(file);
                _lastWriteTimeUtcCache[file] = fileModifiedTimeUtc;
            }

            return fileModifiedTimeUtc;
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Checks to see if the tracking data indicates that everything is up to date according to UpToDateCheckType.
        /// Note: If things are not up to date, then the TLogs are compacted to remove all entries in preparation to
        /// re-track execution of work.
        /// </summary>
        /// <param name="hostTask">The <see cref="Task"/> host</param>
        /// <param name="upToDateCheckType">UpToDateCheckType</param>
        /// <param name="readTLogNames">The array of read tlogs</param>
        /// <param name="writeTLogNames">The array of write tlogs</param>
        /// <returns></returns>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public static bool IsUpToDate(Task hostTask, UpToDateCheckType upToDateCheckType, ITaskItem[] readTLogNames, ITaskItem[] writeTLogNames)
        {
            // Read the input graph (missing inputs are infinitely new - i.e. outputs are out of date)
            FlatTrackingData inputs = new FlatTrackingData(hostTask, readTLogNames, DateTime.MaxValue);

            // Read the output graph (missing outputs are infinitely old - i.e. outputs are out of date)
            FlatTrackingData outputs = new FlatTrackingData(hostTask, writeTLogNames, DateTime.MinValue);

            // Find out if we are up to date
            bool isUpToDate = IsUpToDate(hostTask.Log, upToDateCheckType, inputs, outputs);

            // We're going to execute, so clear out the tlogs so
            // the new execution will correctly populate the tlogs a-new
            if (!isUpToDate)
            {
                // Remove all from inputs tlog
                inputs.DependencyTable.Clear();
                inputs.SaveTlog();

                // Remove all from outputs tlog
                outputs.DependencyTable.Clear();
                outputs.SaveTlog();
            }
            return isUpToDate;
        }

        /// <summary>
        /// Simple check of up to date state according to the tracking data and the UpToDateCheckType.
        /// Note: No tracking log compaction will take place when using this overload
        /// </summary>
        /// <param name="Log">TaskLoggingHelper from the host task</param>
        /// <param name="upToDateCheckType">UpToDateCheckType to use</param>
        /// <param name="inputs">FlatTrackingData structure containing the inputs</param>
        /// <param name="outputs">FlatTrackingData structure containing the outputs</param>
        /// <returns></returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "Log", Justification = "Has shipped as public API; plus it is a closer match to other locations in our codebase where 'Log' is a property and cased properly")]
        public static bool IsUpToDate(TaskLoggingHelper Log, UpToDateCheckType upToDateCheckType, FlatTrackingData inputs, FlatTrackingData outputs)
        {
            bool isUpToDate = false;
            // Keep a record of the task resources that was in use before
            ResourceManager taskResources = Log.TaskResources;

            Log.TaskResources = AssemblyResources.PrimaryResources;

            inputs.UpdateFileEntryDetails();
            outputs.UpdateFileEntryDetails();

            if (!inputs.TlogsAvailable || !outputs.TlogsAvailable || inputs.DependencyTable.Count == 0)
            {
                // 1) The TLogs are somehow missing, which means we need to build
                // 2) Because we are flat tracking, there are no roots which means that all the input file information
                //    comes from the input Tlogs, if they are empty then we must build.
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_LogFilesNotAvailable");
            }
            else if (inputs.MissingFiles.Count > 0 || outputs.MissingFiles.Count > 0)
            {
                // Files are missing from either inputs or outputs, that means we need to build

                // Files are missing from inputs, that means we need to build
                if (inputs.MissingFiles.Count > 0)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "Tracking_MissingInputs");
                }
                // Too much logging leads to poor performance
                if (inputs.MissingFiles.Count > MaxLogCount)
                {
                    FileTracker.LogMessageFromResources(Log, MessageImportance.Low, "Tracking_InputsNotShown", inputs.MissingFiles.Count);
                }
                else
                {
                    // We have our set of inputs, log the details
                    foreach (string input in inputs.MissingFiles)
                    {
                        FileTracker.LogMessage(Log, MessageImportance.Low, "\t" + input);
                    }
                }

                // Files are missing from outputs, that means we need to build
                if (outputs.MissingFiles.Count > 0)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "Tracking_MissingOutputs");
                }
                // Too much logging leads to poor performance
                if (outputs.MissingFiles.Count > MaxLogCount)
                {
                    FileTracker.LogMessageFromResources(Log, MessageImportance.Low, "Tracking_OutputsNotShown", outputs.MissingFiles.Count);
                }
                else
                {
                    // We have our set of inputs, log the details
                    foreach (string output in outputs.MissingFiles)
                    {
                        FileTracker.LogMessage(Log, MessageImportance.Low, "\t" + output);
                    }
                }
            }
            else if (upToDateCheckType == UpToDateCheckType.InputOrOutputNewerThanTracking &&
                    inputs.NewestFileTimeUtc > inputs.NewestTLogTimeUtc)
            {
                // One of the inputs is newer than the input tlog
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_DependencyWasModifiedAt", inputs.NewestFileName, inputs.NewestFileTimeUtc, inputs.NewestTLogFileName, inputs.NewestTLogTimeUtc);
            }
            else if (upToDateCheckType == UpToDateCheckType.InputOrOutputNewerThanTracking &&
                    outputs.NewestFileTimeUtc > outputs.NewestTLogTimeUtc)
            {
                // one of the outputs is newer than the output tlog
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_DependencyWasModifiedAt", outputs.NewestFileName, outputs.NewestFileTimeUtc, outputs.NewestTLogFileName, outputs.NewestTLogTimeUtc);
            }
            else if (upToDateCheckType == UpToDateCheckType.InputNewerThanOutput &&
                    inputs.NewestFileTimeUtc > outputs.NewestFileTimeUtc)
            {
                // One of the inputs is newer than the outputs
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_DependencyWasModifiedAt", inputs.NewestFileName, inputs.NewestFileTimeUtc, outputs.NewestFileName, outputs.NewestFileTimeUtc);
            }
            else if (upToDateCheckType == UpToDateCheckType.InputNewerThanTracking &&
                    inputs.NewestFileTimeUtc > inputs.NewestTLogTimeUtc)
            {
                // One of the inputs is newer than the one of the TLogs
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_DependencyWasModifiedAt", inputs.NewestFileName, inputs.NewestFileTimeUtc, inputs.NewestTLogFileName, inputs.NewestTLogTimeUtc);
            }
            else if (upToDateCheckType == UpToDateCheckType.InputNewerThanTracking &&
                    inputs.NewestFileTimeUtc > outputs.NewestTLogTimeUtc)
            {
                // One of the inputs is newer than the one of the TLogs
                Log.LogMessageFromResources(MessageImportance.Low, "Tracking_DependencyWasModifiedAt", inputs.NewestFileName, inputs.NewestFileTimeUtc, outputs.NewestTLogFileName, outputs.NewestTLogTimeUtc);
            }
            else
            {
                // Nothing appears to have changed..
                isUpToDate = true;
                Log.LogMessageFromResources(MessageImportance.Normal, "Tracking_UpToDate");
            }

            // Set the task resources back now that we're done with it
            Log.TaskResources = taskResources;

            return isUpToDate;
        }

        /// <summary>
        /// Once tracked operations have been completed then we need to compact / finalize the Tlogs based
        /// on the success of the tracked execution. If it fails, then we clean out the TLogs. If it succeeds
        /// then we clean temporary files from the TLogs and re-write them.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLogs", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public static void FinalizeTLogs(bool trackedOperationsSucceeded, ITaskItem[] readTLogNames, ITaskItem[] writeTLogNames, ITaskItem[] trackedFilesToRemoveFromTLogs)
        {
            // Read the input table, skipping missing files
            FlatTrackingData inputs = new FlatTrackingData(readTLogNames, true);

            // Read the output table, skipping missing files
            FlatTrackingData outputs = new FlatTrackingData(writeTLogNames, true);

            // If we failed we need to clean the Tlogs
            if (!trackedOperationsSucceeded)
            {
                // If the tool errors in some way, we assume that any and all inputs and outputs it wrote during
                // execution are wrong. So we compact the read and write tlogs to remove the entries for the
                // set of sources being compiled - the next incremental build will find no entries
                // and correctly cause the sources to be compiled
                // Remove all from inputs tlog
                inputs.DependencyTable.Clear();
                inputs.SaveTlog();

                // Remove all from outputs tlog
                outputs.DependencyTable.Clear();
                outputs.SaveTlog();
            }
            else
            {
                // If all went well with the tool execution, then compact the tlogs
                // to remove any files that are no longer on disk.
                // This removes any temporary files from the dependency graph

                // In addition to temporary file removal, an optional set of files to remove may be been supplied

                if (trackedFilesToRemoveFromTLogs?.Length > 0)
                {
                    IDictionary<string, ITaskItem> trackedFilesToRemove = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);

                    foreach (ITaskItem removeFile in trackedFilesToRemoveFromTLogs)
                    {
                        trackedFilesToRemove.Add(FileUtilities.NormalizePath(removeFile.ItemSpec), removeFile);
                    }

                    // UNDONE: If necessary we could have two independent sets of "ignore" files, one for inputs and one for outputs
                    // Use an anonymous methods to encapsulate the contains check for the input and output tlogs
                    // We need to answer the question "should fullTrackedPath be included in the TLog?"
                    outputs.SaveTlog(fullTrackedPath => !trackedFilesToRemove.ContainsKey(fullTrackedPath));
                    inputs.SaveTlog(fullTrackedPath => !trackedFilesToRemove.ContainsKey(fullTrackedPath));
                }
                else
                {
                    // Compact the write tlog
                    outputs.SaveTlog();

                    // Compact the read tlog
                    inputs.SaveTlog();
                }
            }
        }
        #endregion
#pragma warning restore format
    }

    /// <summary>
    /// The possible types of up to date check that we can support
    /// </summary>
    public enum UpToDateCheckType
    {
        /// <summary>
        /// The input is newer than the output.
        /// </summary>
        InputNewerThanOutput,
        /// <summary>
        /// The input or output are newer than the tracking file.
        /// </summary>
        InputOrOutputNewerThanTracking,
        /// <summary>
        /// The input is newer than the tracking file.
        /// </summary>
        InputNewerThanTracking
    }
}

#endif
