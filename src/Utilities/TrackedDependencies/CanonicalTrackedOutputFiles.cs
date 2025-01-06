// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_FILE_TRACKER

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Utilities
{
    /// <summary>
    /// This class is the filetracking log interpreter for .write. tracking logs in canonical form
    /// Canonical .write. logs need to be rooted, since the outputs need to be associated with an input.
    /// </summary>
    public class CanonicalTrackedOutputFiles
    {
#pragma warning disable format // region formatting is different in net7.0 and net472, and cannot be fixed for both
        #region Member Data
        // The .write. tracking log files
        private ITaskItem[] _tlogFiles;
        // The TaskLoggingHelper that we log progress to
        private TaskLoggingHelper _log;
        // Are the tracking logs that we were constructed with actually available
        private bool _tlogAvailable;
        #endregion

        #region Properties

        /// <summary>
        /// Gets the dependency table.
        /// </summary>
        public Dictionary<string, Dictionary<string, DateTime>> DependencyTable { get; private set; }

        #endregion

        #region Constructors
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        public CanonicalTrackedOutputFiles(ITaskItem[] tlogFiles) => InternalConstruct(null, tlogFiles, true);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        public CanonicalTrackedOutputFiles(ITask ownerTask, ITaskItem[] tlogFiles) => InternalConstruct(ownerTask, tlogFiles, true);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        /// <param name="constructOutputsFromTLogs">The output graph is built from the .write. tlogs</param>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLogs", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public CanonicalTrackedOutputFiles(ITask ownerTask, ITaskItem[] tlogFiles, bool constructOutputsFromTLogs) => InternalConstruct(ownerTask, tlogFiles, constructOutputsFromTLogs);

        /// <summary>
        /// Internal constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .write. tlog files to interpret</param>
        /// <param name="constructOutputsFromTLogs">The output graph is built from the .write. tlogs</param>
        private void InternalConstruct(ITask ownerTask, ITaskItem[] tlogFiles, bool constructOutputsFromTLogs)
        {
            if (ownerTask != null)
            {
                _log = new TaskLoggingHelper(ownerTask)
                {
                    TaskResources = AssemblyResources.PrimaryResources,
                    HelpKeywordPrefix = "MSBuild."
                };
            }

            _tlogFiles = TrackedDependencies.ExpandWildcards(tlogFiles, _log);
            _tlogAvailable = TrackedDependencies.ItemsExist(_tlogFiles);
            DependencyTable = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
            if (_tlogFiles != null && constructOutputsFromTLogs)
            {
                ConstructOutputTable();
            }
        }
        #endregion

        #region Methods
        /// <summary>
        /// Construct our dependency table for our source files
        /// </summary>
        private void ConstructOutputTable()
        {
            string tLogRootingMarker;
            try
            {
                // construct a rooting marker from the tlog files
                tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(_tlogFiles);
            }
            catch (ArgumentException e)
            {
                FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLog", e.Message);
                return;
            }

            // Record the current directory (which under normal circumstances will be the project directory)
            // so that we can compare tracked paths against it for inclusion in the dependency graph
            string currentProjectDirectory = FileUtilities.EnsureTrailingSlash(Directory.GetCurrentDirectory());

            if (!_tlogAvailable)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_TrackingLogNotAvailable");
                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs are not available, they may have been deleted at some point.
                    // Be safe and remove any references from the cache.
                    DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                }
                return;
            }

            DependencyTableCacheEntry cachedEntry = null;

            lock (DependencyTableCache.DependencyTable)
            {
                // Look in the dependency table cache to see if its available and up to date
                cachedEntry = DependencyTableCache.GetCachedEntry(tLogRootingMarker);
            }

            // We have an up to date cached entry
            if (cachedEntry != null)
            {
                DependencyTable = (Dictionary<string, Dictionary<string, DateTime>>)cachedEntry.DependencyTable;
                // Log information about what we're using
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_WriteTrackingCached");
                foreach (ITaskItem tlogItem in cachedEntry.TlogFiles)
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogItem.ItemSpec);
                }
                return;
            }

            FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_WriteTrackingLogs");

            // Now we need to construct the rest of the table from the TLOG files
            // If there are any errors in the tlogs, we want to warn, stop parsing tlogs, and empty
            // out the dependency table, essentially forcing a rebuild.
            bool encounteredInvalidTLogContents = false;
            string invalidTLogName = null;
            foreach (ITaskItem tlogFileName in _tlogFiles)
            {
                FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogFileName.ItemSpec);

                try
                {
                    using (StreamReader tlog = File.OpenText(tlogFileName.ItemSpec))
                    {
                        string tlogEntry = tlog.ReadLine();

                        while (tlogEntry != null)
                        {
                            if (tlogEntry.Length == 0)
                            {
                                encounteredInvalidTLogContents = true;
                                invalidTLogName = tlogFileName.ItemSpec;
                                break;
                            }

                            if (tlogEntry[0] == '^') // This is a rooting record, follow the outputs for it
                            {
                                tlogEntry = tlogEntry.Substring(1);

                                if (tlogEntry.Length == 0)
                                {
                                    encounteredInvalidTLogContents = true;
                                    invalidTLogName = tlogFileName.ItemSpec;
                                    break;
                                }

                                if (!DependencyTable.TryGetValue(tlogEntry, out Dictionary<string, DateTime> dependencies))
                                {
                                    dependencies = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                                    DependencyTable.Add(tlogEntry, dependencies);
                                }

                                // Process each file encountered until we hit a rooting marker
                                do
                                {
                                    tlogEntry = tlog.ReadLine();

                                    if (tlogEntry != null)
                                    {
                                        if (tlogEntry.Length == 0)
                                        {
                                            encounteredInvalidTLogContents = true;
                                            invalidTLogName = tlogFileName.ItemSpec;
                                            break;
                                        }
                                        else if (tlogEntry[0] != '^' && tlogEntry[0] != '#' && !dependencies.ContainsKey(tlogEntry))
                                        {
                                            // Allows incremental build of projects existing under temp, only for those reads / writes that
                                            // either are not under temp, or are recursively beneath the current project directory.
                                            if (FileTracker.FileIsUnderPath(tlogEntry, currentProjectDirectory) || !FileTracker.FileIsExcludedFromDependencies(tlogEntry))
                                            {
                                                DateTime fileModifiedTime = NativeMethodsShared.GetLastWriteFileUtcTime(tlogEntry);

                                                dependencies.Add(tlogEntry, fileModifiedTime);
                                            }
                                        }
                                    }
                                } while (tlogEntry != null && tlogEntry[0] != '^');

                                if (encounteredInvalidTLogContents)
                                {
                                    break;
                                }
                            }
                            else // don't know what this entry is, so skip it
                            {
                                tlogEntry = tlog.ReadLine();
                            }
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
                    DependencyTable = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Record the newly built valid dependency table in the cache
                    DependencyTableCache.DependencyTable[tLogRootingMarker] = new DependencyTableCacheEntry(_tlogFiles, DependencyTable);
                }
            }
        }

        /// <summary>
        /// Given a set of sources, removes from the dependency graph any roots that share
        /// the same outputs as the rooting marker constructed from the given set of sources.
        /// </summary>
        /// <comment>
        /// Used when there's a possibility that more than one set of inputs may produce the
        /// same output -- this is a way to invalidate any other roots that produce that same
        /// outputs, so that the next time the task is run with that other set of inputs, it
        /// won't incorrectly believe that it is up-to-date.
        /// </comment>
        /// <param name="sources">The set of sources that form the rooting marker whose outputs
        /// should not be shared by any other rooting marker.</param>
        /// <returns>An array of the rooting markers that were removed.</returns>
        public string[] RemoveRootsWithSharedOutputs(ITaskItem[] sources)
        {
            ErrorUtilities.VerifyThrowArgumentNull(sources);

            var removedMarkers = new List<string>();
            string currentRoot = FileTracker.FormatRootingMarker(sources);

            if (DependencyTable.TryGetValue(currentRoot, out Dictionary<string, DateTime> currentOutputs))
            {
                // This is O(n*m), but in most cases, both n (the number of roots in the file) and m (the number
                // of outputs per root) should be fairly small.
                // UNDONE: Can we make this faster?
                foreach (KeyValuePair<string, Dictionary<string, DateTime>> root in DependencyTable)
                {
                    if (!currentRoot.Equals(root.Key, StringComparison.Ordinal))
                    {
                        // If the current entry contains any of the outputs of the rooting marker we have sources for,
                        // then we want to remove it from the dependency table.
                        foreach (string output in currentOutputs.Keys)
                        {
                            if (root.Value.ContainsKey(output))
                            {
                                removedMarkers.Add(root.Key);
                                break;
                            }
                        }
                    }
                }

                // Now actually remove the markers that we intend to remove.
                foreach (string removedMarker in removedMarkers)
                {
                    DependencyTable.Remove(removedMarker);
                }
            }

            return removedMarkers.ToArray();
        }

        /// <summary>
        /// Remove the specified ouput from the dependency graph for the given source file
        /// </summary>
        /// <param name="sourceRoot">The source file who's output is to be discarded</param>
        /// <param name="outputPathToRemove">The output path to be removed</param>
        public bool RemoveOutputForSourceRoot(string sourceRoot, string outputPathToRemove)
        {
            if (DependencyTable.TryGetValue(sourceRoot, out var outputPaths))
            {
                bool removed = outputPaths.Remove(outputPathToRemove);
                // If we just removed the last entry for this root, remove the root.
                if (DependencyTable[sourceRoot].Count == 0)
                {
                    DependencyTable.Remove(sourceRoot);
                }
                // If the output didn't exist then return false
                return removed;
            }
            else
            {
                // If we don't have it, then that's as good as success
                return true;
            }
        }

        /// <summary>
        /// This method determines the outputs for a source root (as in the contents of a rooting marker)
        /// </summary>
        /// <param name="sources">The sources to find outputs for</param>
        /// <returns>Array of outputs for the source</returns>
        public ITaskItem[] OutputsForNonCompositeSource(params ITaskItem[] sources)
        {
            var outputs = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
            var outputsArray = new List<ITaskItem>();
            string upperSourcesRoot = FileTracker.FormatRootingMarker(sources);

            // Check each root in the output table to see if meets case 1 or two described above
            foreach (ITaskItem source in sources)
            {
                string upperSourceRoot = FileUtilities.NormalizePath(source.ItemSpec);
                OutputsForSourceRoot(outputs, upperSourceRoot);
            }

            // There were no outputs for the requested root
            if (outputs.Count == 0)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputForRootNotFound", upperSourcesRoot);
            }
            else
            {
                // We have our set of outputs, construct our array to return
                outputsArray.AddRange(outputs.Values);

                // Too much output logging leads to poor performance
                if (outputs.Count > CanonicalTrackedFilesHelper.MaxLogCount)
                {
                    FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputsNotShown", outputs.Count);
                }
                else
                {
                    // We have our set of outputs, log the details
                    FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputsFor", upperSourcesRoot);

                    foreach (ITaskItem outputItem in outputsArray)
                    {
                        FileTracker.LogMessage(_log, MessageImportance.Low, "\t" + outputItem);
                    }
                }
            }

            return outputsArray.ToArray();
        }

        /// <summary>
        /// This method determines the outputs for a source root (as in the contents of a rooting marker)
        /// </summary>
        /// <param name="sources">The sources to find outputs for</param>
        /// <returns>Array of outputs for the source</returns>
        public ITaskItem[] OutputsForSource(params ITaskItem[] sources) => OutputsForSource(sources, true);

        /// <summary>
        /// This method determines the outputs for a source root (as in the contents of a rooting marker)
        /// </summary>
        /// <param name="sources">The sources to find outputs for</param>
        /// <param name="searchForSubRootsInCompositeRootingMarkers">When set true, this will consider using outputs found in rooting markers that are composed of the sub-root.</param>
        /// <returns>Array of outputs for the source</returns>
        public ITaskItem[] OutputsForSource(ITaskItem[] sources, bool searchForSubRootsInCompositeRootingMarkers)
        {
            // We need to find all the outputs for the sources
            // This happens in two ways; Look at all the roots in the output table and..
            // 1. If the root in the table is comprised entirely from sources in the set
            //    being requested then the outputs for that root should be included
            //    *This is the mechanism used by CL, MIDL, RC
            //
            // 2. If the root for the set of sources being requested fully contains (or equals)
            //    the root in the table then the outputs should be included
            //    *This is currently only in use by unit tests (it is a valid scenario)

            // There were no tlogs available, that means we need to build
            if (!_tlogAvailable)
            {
                return null;
            }

            var outputs = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);

            // Construct a rooting marker from the set of sources
            string upperSourcesRoot = FileTracker.FormatRootingMarker(sources);
            var outputsArray = new List<ITaskItem>();

            // Check each root in the output table to see if meets case 1 or two described above
            foreach (string tableEntryRoot in DependencyTable.Keys)
            {
                string upperTableEntryRoot = tableEntryRoot.ToUpperInvariant();
                if (searchForSubRootsInCompositeRootingMarkers &&
                   (upperSourcesRoot.Contains(upperTableEntryRoot) ||
                    upperTableEntryRoot.Contains(upperSourcesRoot) ||
                    CanonicalTrackedFilesHelper.RootContainsAllSubRootComponents(upperSourcesRoot, upperTableEntryRoot)))
                {
                    // Gather the unique outputs for this root
                    OutputsForSourceRoot(outputs, upperTableEntryRoot);
                }
                else if (!searchForSubRootsInCompositeRootingMarkers &&
                         upperTableEntryRoot.Equals(upperSourcesRoot, StringComparison.Ordinal))
                {
                    OutputsForSourceRoot(outputs, upperTableEntryRoot);
                }
            }

            // There were no outputs for the requested root
            if (outputs.Count == 0)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputForRootNotFound", upperSourcesRoot);
            }
            else
            {
                // We have our set of outputs, construct our array to return
                outputsArray.AddRange(outputs.Values);

                // Too much output logging leads to poor performance
                if (outputs.Count > CanonicalTrackedFilesHelper.MaxLogCount)
                {
                    FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputsNotShown", outputs.Count);
                }
                else
                {
                    // We have our set of outputs, log the details
                    FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_OutputsFor", upperSourcesRoot);

                    foreach (ITaskItem outputItem in outputsArray)
                    {
                        FileTracker.LogMessage(_log, MessageImportance.Low, "\t" + outputItem);
                    }
                }
            }

            return outputsArray.ToArray();
        }

        /// <summary>
        /// This method determines the outputs for a source root (as in the contents of a rooting marker)
        /// </summary>
        /// <param name="outputs">List of outputs to populate</param>
        /// <param name="sourceKey">The source to gather outputs for</param>
        private void OutputsForSourceRoot(Dictionary<string, ITaskItem> outputs, string sourceKey)
        {
            if (DependencyTable.TryGetValue(sourceKey, out Dictionary<string, DateTime> dependencies))
            {
                foreach (string dependee in dependencies.Keys)
                {
                    // only if we don't have the output already should we add it again
                    if (!outputs.ContainsKey(dependee))
                    {
                        outputs.Add(dependee, new TaskItem(dependee));
                    }
                }
            }
        }

        /// <summary>
        /// This method adds computed outputs for the given source key to the output graph
        /// </summary>
        /// <param name="sourceKey">The source to add outputs for</param>
        /// <param name="computedOutput">The computed outputs for this source key</param>
        public void AddComputedOutputForSourceRoot(string sourceKey, string computedOutput)
        {
            Dictionary<string, DateTime> dependencies = GetSourceKeyOutputs(sourceKey);
            AddOutput(dependencies, computedOutput);
        }

        /// <summary>
        /// This method adds computed outputs for the given source key to the output graph
        /// </summary>
        /// <param name="sourceKey">The source to add outputs for</param>
        /// <param name="computedOutputs">The computed outputs for this source key</param>
        public void AddComputedOutputsForSourceRoot(string sourceKey, string[] computedOutputs)
        {
            Dictionary<string, DateTime> dependencies = GetSourceKeyOutputs(sourceKey);
            foreach (string computedOutput in computedOutputs)
            {
                AddOutput(dependencies, computedOutput);
            }
        }

        /// <summary>
        /// This method adds computed outputs for the given source key to the output graph
        /// </summary>
        /// <param name="sourceKey">The source to add outputs for</param>
        /// <param name="computedOutputs">The computed outputs for this source key</param>
        public void AddComputedOutputsForSourceRoot(string sourceKey, ITaskItem[] computedOutputs)
        {
            Dictionary<string, DateTime> dependencies = GetSourceKeyOutputs(sourceKey);
            foreach (ITaskItem computedOutput in computedOutputs)
            {
                AddOutput(dependencies, FileUtilities.NormalizePath(computedOutput.ItemSpec));
            }
        }

        /// <summary>
        /// This method returns the output dictionary for the given source key
        /// if non exists, one is created
        /// </summary>
        /// <param name="sourceKey">The source to retrieve outputs for</param>
        private Dictionary<string, DateTime> GetSourceKeyOutputs(string sourceKey)
        {
            if (!DependencyTable.TryGetValue(sourceKey, out Dictionary<string, DateTime> dependencies))
            {
                dependencies = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                DependencyTable.Add(sourceKey, dependencies);
            }
            return dependencies;
        }

        /// <summary>
        /// This method adds a computed output for the given source key to the dictionary specified
        /// </summary>
        /// <param name="dependencies">The dictionary to add outputs to</param>
        /// <param name="computedOutput">The computed outputs for this source key</param>
        private static void AddOutput(Dictionary<string, DateTime> dependencies, string computedOutput)
        {
            string fullComputedOutput = FileUtilities.NormalizePath(computedOutput).ToUpperInvariant();
            if (!dependencies.ContainsKey(fullComputedOutput))
            {
                DateTime fileModifiedTime = FileUtilities.FileExistsNoThrow(fullComputedOutput)
                    ? NativeMethodsShared.GetLastWriteFileUtcTime(fullComputedOutput)
                    : DateTime.MinValue;
                dependencies.Add(fullComputedOutput, fileModifiedTime);
            }
        }

        /// <summary>
        /// This method will re-write the tlogs from the current output table new entries will
        /// be tracked.
        /// </summary>
        public void SaveTlog() => SaveTlog(null);

        /// <summary>
        /// This method will re-write the tlogs from the current output table new entries will
        /// be tracked.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public void SaveTlog(DependencyFilter includeInTLog)
        {
            if (_tlogFiles?.Length > 0)
            {
                string tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(_tlogFiles);

                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs in the cache will be invalidated by this compaction
                    // remove the cached entries to be sure
                    DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                }

                string firstTlog = _tlogFiles[0].ItemSpec;

                // empty all tlogs
                foreach (ITaskItem tlogFile in _tlogFiles)
                {
                    File.WriteAllText(tlogFile.ItemSpec, "", System.Text.Encoding.Unicode);
                }

                // Write out the dependency information as a new tlog
                using (StreamWriter outputs = FileUtilities.OpenWrite(firstTlog, false, System.Text.Encoding.Unicode))
                {
                    foreach (KeyValuePair<string, Dictionary<string, DateTime>> kvp in DependencyTable)
                    {
                        string rootingMarker = kvp.Key;
                        Dictionary<string, DateTime> dependencies = kvp.Value;
                        outputs.WriteLine("^" + rootingMarker);
                        foreach (string file in dependencies.Keys)
                        {
                            // Give the task a chance to filter dependencies out of the written TLog
                            if (includeInTLog == null || includeInTLog(file))
                            {
                                // Write out the entry
                                outputs.WriteLine(file);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        public void RemoveEntriesForSource(ITaskItem source) => RemoveEntriesForSource([source], null);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        /// <param name="correspondingOutput">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveEntriesForSource(ITaskItem source, ITaskItem correspondingOutput) => RemoveEntriesForSource([source], [correspondingOutput]);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        public void RemoveEntriesForSource(ITaskItem[] source) => RemoveEntriesForSource(source, null);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveEntriesForSource(ITaskItem[] source, ITaskItem[] correspondingOutputs)
        {
            // construct a root marker for the sources and outputs to remove from the graph
            string rootMarkerToRemove = FileTracker.FormatRootingMarker(source, correspondingOutputs);

            // remove the entry from the graph for the combined root
            DependencyTable.Remove(rootMarkerToRemove);

            // remove the entry for each source item
            foreach (ITaskItem sourceItem in source)
            {
                DependencyTable.Remove(FileUtilities.NormalizePath(sourceItem.ItemSpec));
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="sources">Sources that should be removed from the graph</param>
        /// <param name="dependencyToRemove">The dependency to remove.</param>
        public void RemoveDependencyFromEntry(ITaskItem[] sources, ITaskItem dependencyToRemove)
        {
            string rootingMarker = FileTracker.FormatRootingMarker(sources);
            RemoveDependencyFromEntry(rootingMarker, dependencyToRemove);
        }

        /// <summary>
        /// Remove the output graph entries for the given source and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        /// <param name="dependencyToRemove">The dependency to remove.</param>
        public void RemoveDependencyFromEntry(ITaskItem source, ITaskItem dependencyToRemove)
        {
            string rootingMarker = FileTracker.FormatRootingMarker(source);
            RemoveDependencyFromEntry(rootingMarker, dependencyToRemove);
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="rootingMarker">Sources that should be removed from the graph</param>
        /// <param name="dependencyToRemove">The dependency to remove.</param>
        private void RemoveDependencyFromEntry(string rootingMarker, ITaskItem dependencyToRemove)
        {
            // construct a root marker for the source that will remove the dependency from
            if (DependencyTable.TryGetValue(rootingMarker, out Dictionary<string, DateTime> dependencies))
            {
                dependencies.Remove(FileUtilities.NormalizePath(dependencyToRemove.ItemSpec));
            }
            else
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Normal, "Tracking_WriteLogEntryNotFound", rootingMarker);
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem source) => RemoveDependenciesFromEntryIfMissing([source], null);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        /// <param name="correspondingOutput">Output that correspond ot the sources (used for same file processing)</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem source, ITaskItem correspondingOutput) => RemoveDependenciesFromEntryIfMissing([source], [correspondingOutput]);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem[] source) => RemoveDependenciesFromEntryIfMissing(source, null);

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem[] source, ITaskItem[] correspondingOutputs)
        {
            // Cache of files and whether or not they exist.
            Dictionary<string, bool> fileCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (correspondingOutputs != null)
            {
                ErrorUtilities.VerifyThrowArgument(source.Length == correspondingOutputs.Length, "Tracking_SourcesAndCorrespondingOutputMismatch");
            }

            // construct a combined root marker for the sources and outputs to remove from the graph
            string rootingMarker = FileTracker.FormatRootingMarker(source, correspondingOutputs);

            RemoveDependenciesFromEntryIfMissing(rootingMarker, fileCache);

            // Remove entries for each individual source
            for (int sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
            {
                rootingMarker = correspondingOutputs != null
                    ? FileTracker.FormatRootingMarker(source[sourceIndex], correspondingOutputs[sourceIndex])
                    : FileTracker.FormatRootingMarker(source[sourceIndex]);
                RemoveDependenciesFromEntryIfMissing(rootingMarker, fileCache);
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given rooting marker
        /// </summary>
        /// <param name="rootingMarker"></param>
        /// <param name="fileCache">The cache used to store whether each file exists or not.</param>
        private void RemoveDependenciesFromEntryIfMissing(string rootingMarker, Dictionary<string, bool> fileCache)
        {
            // In the event of incomplete tracking information (i.e. this root was not present), just continue quietly
            // as the user could have killed the tool being tracked, or another error occurred during its execution.
            if (DependencyTable.TryGetValue(rootingMarker, out Dictionary<string, DateTime> dependencies))
            {
                var dependenciesWithoutMissingFiles = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                int keyIndex = 0;

                foreach (KeyValuePair<string, DateTime> kvp in dependencies)
                {
                    string file = kvp.Key;
                    if (keyIndex++ > 0)
                    {
                        // Record whether or not each file exists and cache it.
                        // We do this to save time (On^2), at the expense of data O(n).
                        bool inFileCache = fileCache.TryGetValue(file, out bool fileExists);

                        // Have we cached the file yet? If not, cache its existence.
                        if (!inFileCache)
                        {
                            fileExists = FileUtilities.FileExistsNoThrow(file);
                            fileCache.Add(file, fileExists);
                        }

                        // Does the cached file exist?
                        if (fileExists)
                        {
                            dependenciesWithoutMissingFiles.Add(file, kvp.Value);
                        }
                    }
                    else
                    {
                        dependenciesWithoutMissingFiles.Add(file, DateTime.Now);
                    }
                }

                DependencyTable[rootingMarker] = dependenciesWithoutMissingFiles;
            }
        }
        #endregion
#pragma warning restore format
    }
}

#endif
