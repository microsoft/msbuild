// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using ErrorUtilities = Microsoft.Build.Shared.ErrorUtilities;
using OutOfProcNode = Microsoft.Build.Execution.OutOfProcNode;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Maintains a cache of all loaded ProjectRootElement's for design time purposes.
    /// Weak references are held to add added ProjectRootElement's.
    /// Strong references are held to a limited number of added ProjectRootElement's.
    ///
    /// 1. Loads of a ProjectRootElement will share any existing loaded ProjectRootElement, rather
    /// than loading and parsing a new one. This is the case whether the ProjectRootElement
    /// is loaded directly or imported.
    ///
    /// 2. For design time, only a weak reference needs to be held, because all users have a strong reference.
    ///
    /// 3. Because all loads of a ProjectRootElement consult this cache, they can be assured that any
    /// entries in this cache are up to date. For example, if a ProjectRootElement is modified and saved,
    /// the cached ProjectRootElement will be the loaded one that was saved, so it will be up to date.
    ///
    /// 4. If, after a project has been loaded, an external app changes the project file content on disk, it is
    /// important that a subsequent load of that project does not return stale ProjectRootElement. To avoid this, the
    /// timestamp of the file on disk is compared to the timestamp of the file at the time that the ProjectRootElement loaded it.
    ///
    /// 5. For build time, some strong references need to be held, as otherwise the ProjectRootElement's for reuseable
    /// imports will be collected, and time will be wasted reparsing them. However we do not want to hold strong references
    /// to all ProjectRootElement's, consuming memory without end. So a simple priority queue is used. All Adds and Gets boost their
    /// entry to the top. As the queue gets too big, low priority entries are dropped.
    ///
    /// No guesses are made at which files are more interesting to cache, beyond the most-recently-used list. For example, ".targets" files
    /// or imported files are not treated specially, as this is a potentially unreliable heuristic. Besides, caching a project file itself could
    /// be useful, if for example you want to build it twice with different sets of properties.
    ///
    /// Because of the strongly typed list, some ProjectRootElement's will be held onto indefinitely. This is an acceptable price to pay for
    /// being able to provide a commonly used ProjectRootElement immediately it's needed. It is mitigated by the list being finite and small, and
    /// because we allow ProjectCollection.UnloadAllProjects to hint to us to clear the list.
    ///
    /// Implicit references are those which were loaded as a result of a build, and not explicitly loaded through, for instance, the project
    /// collection.
    ///
    /// </summary>
    internal class ProjectRootElementCache : ProjectRootElementCacheBase
    {
        /// <summary>
        /// The maximum number of entries to keep strong references to.
        /// This has to be strong enough to make sure that key .targets files aren't pushed
        /// off by transient loads of non-reusable files like .user files.
        /// </summary>
        /// <remarks>
        /// Made this as large as 200 because ASP.NET Core (6.0) projects have
        /// something like 80-90 imports. This was observed to give a noticeable
        /// performance improvement compared to a mid-17.0 MSBuild with the old
        /// value of 50.
        ///
        /// If this number is increased much higher, the datastructure may
        /// need to be changed from a linked list, since it's currently O(n).
        /// </remarks>
        private static readonly int s_maximumStrongCacheSize =
            int.TryParse(Environment.GetEnvironmentVariable("MSBUILDPROJECTROOTELEMENTCACHESIZE"), out int cacheSize) ? cacheSize : 200;

        /// <summary>
        /// Whether the cache should log activity to the Debug.Out stream
        /// </summary>
        private static bool s_debugLogCacheActivity = Environment.GetEnvironmentVariable("MSBUILDDEBUGXMLCACHE") == "1";

        /// <summary>
        /// Whether the cache should check file content for cache entry invalidation.
        /// </summary>
        /// <remarks>
        /// Value shall be true only in case of testing. Outside QA tests it shall be false.
        /// </remarks>
        private static bool s_сheckFileContent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSBUILDCACHECHECKFILECONTENT"));

#if DEBUG
        /// <summary>
        /// A simple IDisposable struct implementing the holder/guard pattern over the Get reentrancy counter.
        /// </summary>
        private struct ReentrancyGuard : IDisposable
        {
            /// <summary>
            /// Number of entries into Get function of the ProjectRootElementCache.
            /// Shall be always 0 or 1. Reentrance to the Get function (value > 1) could lead to race condition.
            /// </summary>
            [ThreadStatic]
            private static int s_getEntriesNumber = 0;

            public ReentrancyGuard()
            {
                s_getEntriesNumber++;
                ErrorUtilities.VerifyThrow(
                    s_getEntriesNumber == 1,
                    "Reentrance to the ProjectRootElementCache.Get function detected.");
            }

            public void Dispose()
            {
                s_getEntriesNumber--;
            }
        }
#endif

        /// <summary>
        /// The map of weakly-held ProjectRootElement's
        /// </summary>
        /// <remarks>
        /// Be sure that the string keys are strongly held, or unpredictable bad
        /// behavior will ensue.
        /// </remarks>
        private WeakValueDictionary<string, ProjectRootElement> _weakCache;

        /// <summary>
        /// Lock objects keyed by project file path.
        /// </summary>
        private ConcurrentDictionary<string, object> _fileLoadLocks;

        /// <summary>
        /// The list of strongly-held ProjectRootElement's
        /// </summary>
        private LinkedList<ProjectRootElement> _strongCache;

        /// <summary>
        /// Whether the cache should check the timestamp of the file on disk
        /// whenever it is requested, and update with the latest content of that
        /// file if it has changed.
        /// </summary>
        private bool _autoReloadFromDisk;

        /// <summary>
        /// Locking object for this shared cache
        /// </summary>
        private object _locker = new object();

        /// <summary>
        /// Creates an empty cache.
        /// </summary>
        internal ProjectRootElementCache(bool autoReloadFromDisk, bool loadProjectsReadOnly = false)
        {
            DebugTraceCache("Constructing with autoreload from disk: ", autoReloadFromDisk);

            _weakCache = new WeakValueDictionary<string, ProjectRootElement>(StringComparer.OrdinalIgnoreCase);
            _strongCache = new LinkedList<ProjectRootElement>();
            _fileLoadLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _autoReloadFromDisk = autoReloadFromDisk;
            LoadProjectsReadOnly = loadProjectsReadOnly;
        }


        /// <summary>
        /// Returns true if given cache entry exists and is outdated.
        /// </summary>
        private bool IsInvalidEntry(string projectFile, ProjectRootElement projectRootElement)
        {
            // When we do not _autoReloadFromDisk we expect that cached value is always valid.
            // Usually lifespan of cache is expected to be build duration (process will terminate after build).
            if (projectRootElement == null || !_autoReloadFromDisk)
            {
                return false;
            }

            // If the project file is non modifiable, assume it is up to date and consider the cached value valid.
            if (!Traits.Instance.EscapeHatches.AlwaysDoImmutableFilesUpToDateCheck && FileClassifier.Shared.IsNonModifiable(projectFile))
            {
                return false;
            }

            FileInfo fileInfo = FileUtilities.GetFileInfoNoThrow(projectFile);

            // If the file doesn't exist on disk, go ahead and use the cached version.
            // It's an in-memory project that hasn't been saved yet.
            if (fileInfo == null)
            {
                return false;
            }

            if (fileInfo.LastWriteTime != projectRootElement.LastWriteTimeWhenRead)
            {
                // File was changed on disk by external means. Cached version is no longer valid.
                // We could throw here or ignore the problem, but it is a common and reasonable pattern to change a file
                // externally and load a new project over it to see the new content. So we dump it from the cache
                // to force a load from disk. There might then exist more than one ProjectRootElement with the same path,
                // but clients ought not get themselves into such a state - and unless they save them to disk,
                // it may not be a problem.
                return true;
            }
            else if (s_сheckFileContent)
            {
                // QA tests run too fast for the timestamp check to work. This environment variable is for their
                // use: it checks the file content as well as the timestamp. That's better than completely disabling
                // the cache as we get test coverage of the rest of the cache code.
                XmlDocument document = new XmlDocument();
                document.PreserveWhitespace = projectRootElement.XmlDocument.PreserveWhitespace;

                using (var xtr = XmlReaderExtension.Create(projectRootElement.FullPath, projectRootElement.ProjectRootElementCache.LoadProjectsReadOnly))
                {
                    document.Load(xtr.Reader);
                }

                string diskContent = document.OuterXml;
                string cacheContent = projectRootElement.XmlDocument.OuterXml;

                if (diskContent != cacheContent)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns an existing ProjectRootElement for the specified file path, if any.
        /// If none exists, calls the provided delegate to load one, and adds that to the cache.
        /// The reason that it calls back to do this is so that the cache is locked between determining
        /// that the entry does not exist and adding the entry.
        ///
        /// If <see cref="_autoReloadFromDisk"/> was set to true, and the file on disk has changed since it was cached,
        /// it will be reloaded before being returned.
        ///
        /// Thread safe.
        /// </summary>
        /// <remarks>
        /// Never needs to consult the strong cache as well, since if the item is in there, it will
        /// not have left the weak cache.
        /// If item is found, boosts it to the top of the strong cache.
        /// </remarks>
        /// <param name="projectFile">The project file which contains the ProjectRootElement.  Must be a full path.</param>
        /// <param name="loadProjectRootElement">The delegate to use to load if necessary. May be null. Must not update the cache.</param>
        /// <param name="isExplicitlyLoaded"><code>true</code> if the project is explicitly loaded, otherwise <code>false</code>.</param>
        /// <param name="preserveFormatting"><code>true</code> to the project was loaded with the formated preserved, otherwise <code>false</code>.</param>
        /// <returns>The ProjectRootElement instance if one exists.  Null otherwise.</returns>
        internal override ProjectRootElement Get(string projectFile, OpenProjectRootElement loadProjectRootElement, bool isExplicitlyLoaded,
            bool? preserveFormatting)
        {
#if DEBUG
            // Verify that loadProjectRootElement delegate does not call ProjectRootElementCache.Get().
            using var reentrancyGuard = new ReentrancyGuard();

            // Verify that we never call this with _locker held, as that would create a lock ordering inversion with the per-file lock.
            ErrorUtilities.VerifyThrow(
                !System.Threading.Monitor.IsEntered(_locker),
                "Detected lock ordering inversion in ProjectRootElementCache.");
#endif
            // Should already have been canonicalized
            ErrorUtilities.VerifyThrowInternalRooted(projectFile);

            // First try getting the ProjectRootElement from the cache.
            ProjectRootElement projectRootElement = GetOrLoad(projectFile, loadProjectRootElement: null, isExplicitlyLoaded, preserveFormatting);

            if (projectRootElement != null || loadProjectRootElement == null)
            {
                // If we found it or no load callback was specified, we are done.
                return projectRootElement;
            }

            try
            {
                // We are about to load. Take a per-file lock to prevent multiple threads from duplicating the work multiple times.
                object perFileLock = _fileLoadLocks.GetOrAdd(projectFile, static _ => new object());
                lock (perFileLock)
                {
                    // Call GetOrLoad again, this time with the OpenProjectRootElement callback.
                    return GetOrLoad(projectFile, loadProjectRootElement, isExplicitlyLoaded, preserveFormatting);
                }
            }
            finally
            {
                // Remove the lock object as we have otherwise no good way of preventing _fileLoadLocks from growing unboundedly.
                // If another thread is inside the lock, we effectively create a race condition where someone else may enter
                // GetOrLoad. This is OK because this fine-grained locking is just a perf optimization, and we have either loaded
                // the ProjectRootElement by now, or it is an error condition where perf is not critical.
                _fileLoadLocks.TryRemove(projectFile, out _);
            }
        }

        /// <summary>
        /// A helper used by <see cref="Get"/>.
        /// </summary>
        private ProjectRootElement GetOrLoad(string projectFile, OpenProjectRootElement loadProjectRootElement, bool isExplicitlyLoaded,
            bool? preserveFormatting)
        {
            ProjectRootElement projectRootElement;
            lock (_locker)
            {
                _weakCache.TryGetValue(projectFile, out projectRootElement);

                if (projectRootElement != null)
                {
                    BoostEntryInStrongCache(projectRootElement);

                    // An implicit load will never reset the explicit flag.
                    if (isExplicitlyLoaded)
                    {
                        projectRootElement.MarkAsExplicitlyLoaded();
                    }
                }
                else
                {
                    DebugTraceCache("Not found in cache: ", projectFile);
                }

                if (preserveFormatting != null && projectRootElement != null && projectRootElement.XmlDocument.PreserveWhitespace != preserveFormatting)
                {
                    // Cached project doesn't match preserveFormatting setting, so reload it
                    projectRootElement.Reload(true, preserveFormatting);
                }
            }

            bool projectRootElementIsInvalid = IsInvalidEntry(projectFile, projectRootElement);
            if (projectRootElementIsInvalid)
            {
                DebugTraceCache("Not satisfied from cache: ", projectFile);
                ForgetEntryIfExists(projectRootElement);
            }

            if (loadProjectRootElement == null)
            {
                if (projectRootElement == null || projectRootElementIsInvalid)
                {
                    return null;
                }
                else
                {
                    DebugTraceCache("Satisfied from XML cache: ", projectFile);
                    return projectRootElement;
                }
            }

            // Use openProjectRootElement to reload the element if the cache element does not exist or need to be reloaded.
            if (projectRootElement == null || projectRootElementIsInvalid)
            {
                projectRootElement = loadProjectRootElement(projectFile, this);
                ErrorUtilities.VerifyThrowInternalNull(projectRootElement, "projectRootElement");
                ErrorUtilities.VerifyThrow(
                    projectRootElement.FullPath.Equals(projectFile, StringComparison.OrdinalIgnoreCase),
                    "Got project back with incorrect path. Expected path: {0}, received path: {1}.",
                    projectFile,
                    projectRootElement.FullPath);

                // An implicit load will never reset the explicit flag.
                if (isExplicitlyLoaded)
                {
                    projectRootElement.MarkAsExplicitlyLoaded();
                }

                // Update cache element.
                // It is unlikely, but it might be that while without the lock, the projectRootElement in cache was updated by another thread.
                // And here its entry will be replaced with the loaded projectRootElement. This is fine:
                // if loaded projectRootElement is out of date (so, it changed since the time we loaded it), it will be updated the next time some thread calls Get function.
                AddEntry(projectRootElement);
            }
            else
            {
                DebugTraceCache("Satisfied from XML cache: ", projectFile);
            }

            return projectRootElement;
        }

        /// <summary>
        /// Add an entry to the cache.
        /// </summary>
        internal override void AddEntry(ProjectRootElement projectRootElement)
        {
            lock (_locker)
            {
                RenameEntryInternal(null, projectRootElement);

                RaiseProjectRootElementAddedToCacheEvent(projectRootElement);
            }
        }

        /// <summary>
        /// Rename an entry in the cache.
        /// Entry must already be in the cache.
        /// </summary>
        internal override void RenameEntry(string oldFullPath, ProjectRootElement projectRootElement)
        {
            lock (_locker)
            {
                ErrorUtilities.VerifyThrowArgumentLength(oldFullPath, nameof(oldFullPath));
                RenameEntryInternal(oldFullPath, projectRootElement);
            }
        }

        /// <summary>
        /// Returns any a ProjectRootElement in the cache with the provided full path,
        /// otherwise null.
        /// </summary>
        internal override ProjectRootElement TryGet(string projectFile)
        {
            return TryGet(projectFile, preserveFormatting: null);
        }

        /// <summary>
        /// Returns any a ProjectRootElement in the cache with the provided full path,
        /// otherwise null.
        /// </summary>
        internal override ProjectRootElement TryGet(string projectFile, bool? preserveFormatting)
        {
            ProjectRootElement result = Get(
                projectFile,
                loadProjectRootElement: null, // no delegate to load it
                isExplicitlyLoaded: false, // Since we are not creating a PRE this can be true or false
                preserveFormatting: preserveFormatting);

            return result;
        }

        /// <summary>
        /// Discards strong references held by the cache.
        /// </summary>
        /// <remarks>
        /// The weak cache is never cleared, as we need it to guarantee that the appdomain never
        /// has two ProjectRootElement's for a particular file. Attempts to clear out the weak cache
        /// resulted in this guarantee being broken and subtle bugs popping up everywhere.
        /// </remarks>
        internal override void DiscardStrongReferences()
        {
            lock (_locker)
            {
                DebugTraceCache("Clearing strong refs: ", _strongCache.Count);

                _strongCache = new LinkedList<ProjectRootElement>();

                // A scavenge of the weak cache is probably not worth it as
                // the GC would have had to run immediately after the line above.
            }
        }

        /// <summary>
        /// Clears out the cache.
        /// Called when all projects are unloaded and possibly when a build is done.
        /// </summary>
        internal override void Clear()
        {
            lock (_locker)
            {
                _weakCache = new WeakValueDictionary<string, ProjectRootElement>(StringComparer.OrdinalIgnoreCase);
                _strongCache = new LinkedList<ProjectRootElement>();
            }
        }

        /// <summary>
        /// Discard any entries (weak and strong) which do not have the explicitlyLoaded flag set.
        /// </summary>
        internal override void DiscardImplicitReferences()
        {
            if (_autoReloadFromDisk)
            {
                // no need to clear it, as auto reload properly invalidates caches if changed.
                return;
            }

            lock (_locker)
            {
                // Make a new Weak cache only with items that have been explicitly loaded, this will be a small number, there will most likely
                // be many items which were not explicitly loaded (ie p2p references).
                WeakValueDictionary<string, ProjectRootElement> oldWeakCache = _weakCache;
                _weakCache = new WeakValueDictionary<string, ProjectRootElement>(StringComparer.OrdinalIgnoreCase);

                LinkedList<ProjectRootElement> oldStrongCache = _strongCache;
                _strongCache = new LinkedList<ProjectRootElement>();

                foreach (KeyValuePair<string, ProjectRootElement> kvp in oldWeakCache)
                {
                    if (kvp.Value is null)
                    {
                        continue;
                    }

                    if (kvp.Value.IsExplicitlyLoaded)
                    {
                        _weakCache[kvp.Key] = kvp.Value;
                    }

                    if (oldStrongCache.Contains(kvp.Value))
                    {
                        if (kvp.Value.IsExplicitlyLoaded)
                        {
                            _strongCache.AddFirst(kvp.Value);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Forces a removal of a project root element from the weak cache if it is present.
        /// </summary>
        /// <param name="projectRootElement">The project root element to remove.</param>
        /// <remarks>
        /// No exception is thrown if this project root element is in use by currently loaded projects
        /// by this method.  The calling method must know that this is a safe operation.
        /// There may of course be strong references to the project root element from customer code.
        /// The assumption is that when they instruct the project collection to unload it, which
        /// leads to this being called, they are releasing their strong references too (or it doesn't matter)
        /// </remarks>
        internal override void DiscardAnyWeakReference(ProjectRootElement projectRootElement)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElement, nameof(projectRootElement));

            // A PRE may be unnamed if it was only used in memory.
            if (projectRootElement.FullPath != null)
            {
                lock (_locker)
                {
                    _weakCache.Remove(projectRootElement.FullPath);
                }
            }
        }

        /// <summary>
        /// Add or rename an entry in the cache.
        /// Old full path may be null iff it was not already in the cache.
        /// </summary>
        /// <remarks>
        /// Must be called within the cache lock.
        /// </remarks>
        private void RenameEntryInternal(string oldFullPathIfAny, ProjectRootElement projectRootElement)
        {
            ErrorUtilities.VerifyThrowInternalNull(projectRootElement.FullPath, "FullPath");

            if (oldFullPathIfAny != null)
            {
                ErrorUtilities.VerifyThrowInternalRooted(oldFullPathIfAny);
                ErrorUtilities.VerifyThrow(_weakCache[oldFullPathIfAny] == projectRootElement, "Should already be present");
                _weakCache.Remove(oldFullPathIfAny);
            }

            // There may already be a ProjectRootElement in the cache with the new name. In this case we cannot throw an exception;
            // we must merely replace it. This is because it may be an unrooted entry
            // (and thus gone from the client's point of view) that merely remains
            // in the cache because we still have a reference to it from our strong cache.
            // Another possibility is that there are two, unrelated, un-saved, in-memory projects that were given the same path.
            // Replacing the cache entry does not in itself cause a problem -- if there are any actual users of the old
            // entry they will not be affected. There would then exist more than one ProjectRootElement with the same path,
            // but clients ought not get themselves into such a state - and unless they save them to disk,
            // it may not be a problem. Replacing also doesn't cause a problem for the strong cache,
            // as it is never consulted by us, but it is reasonable for us to remove the old entry in that case.
            ProjectRootElement existingWeakEntry;
            _weakCache.TryGetValue(projectRootElement.FullPath, out existingWeakEntry);

            if (existingWeakEntry != null && !object.ReferenceEquals(existingWeakEntry, projectRootElement))
            {
                _strongCache.Remove(existingWeakEntry);
            }

            DebugTraceCache("Adding: ", projectRootElement.FullPath);
            _weakCache[projectRootElement.FullPath] = projectRootElement;

            BoostEntryInStrongCache(projectRootElement);
        }

        /// <summary>
        /// Update the strong cache.
        /// If the item is already a member of the list, move it to the top.
        /// Otherwise, just add it to the top.
        /// If the list is too large, remove an entry from the bottom.
        /// </summary>
        /// <remarks>
        /// Must be called within the cache lock.
        /// If the size of strong cache gets large, this needs a faster data structure
        /// than a linked list. It's currently O(n).
        /// </remarks>
        private void BoostEntryInStrongCache(ProjectRootElement projectRootElement)
        {
            LinkedListNode<ProjectRootElement> node = _strongCache.First;

            while (node != null)
            {
                if (Object.ReferenceEquals(node.Value, projectRootElement))
                {
                    // DebugTraceCache("Boosting: ", projectRootElement.FullPath);
                    _strongCache.Remove(node);
                    _strongCache.AddFirst(node);

                    return;
                }

                node = node.Next;
            }

            _strongCache.AddFirst(projectRootElement);

            if (_strongCache.Count > s_maximumStrongCacheSize)
            {
                node = _strongCache.Last;

                DebugTraceCache("Shedding: ", node.Value.FullPath);
                _strongCache.Remove(node);
            }
        }

        /// <summary>
        /// Completely remove an entry from this cache
        /// </summary>
        /// <remarks>
        /// Must be called within the cache lock.
        /// </remarks>
        private void ForgetEntry(ProjectRootElement projectRootElement)
        {
            DebugTraceCache("Forgetting: ", projectRootElement.FullPath);

            _weakCache.Remove(projectRootElement.FullPath);

            LinkedListNode<ProjectRootElement> strongCacheEntry = _strongCache.Find(projectRootElement);
            if (strongCacheEntry != null)
            {
                _strongCache.Remove(strongCacheEntry);
            }

            DebugTraceCache("Out of date dropped from XML cache: ", projectRootElement.FullPath);
        }

        /// <summary>
        /// Completely remove an entry from this cache if it exists.
        /// </summary>
        private void ForgetEntryIfExists(ProjectRootElement projectRootElement)
        {
            lock (_locker)
            {
                if (_weakCache.TryGetValue(projectRootElement.FullPath, out var cached) && cached == projectRootElement)
                {
                    ForgetEntry(projectRootElement);
                }
            }
        }

        /// <summary>
        /// Write debugging messages to the Debug.Out stream.
        /// </summary>
        private void DebugTraceCache(string message, bool param1)
        {
            if (s_debugLogCacheActivity)
            {
                DebugTraceCache(message, Convert.ToString(param1, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Write debugging messages to the Debug.Out stream.
        /// </summary>
        private void DebugTraceCache(string message, int param1)
        {
            if (s_debugLogCacheActivity)
            {
                DebugTraceCache(message, Convert.ToString(param1, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Write debugging messages to the Debug.Out stream.
        /// </summary>
        private void DebugTraceCache(string message, string param1)
        {
            if (s_debugLogCacheActivity)
            {
                string prefix = OutOfProcNode.IsOutOfProcNode ? "C" : "P";
                Trace.WriteLine(prefix + " " + EnvironmentUtilities.CurrentProcessId + " | " + message + param1);
            }
        }
    }
}
