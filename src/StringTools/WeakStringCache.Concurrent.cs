// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Microsoft.NET.StringTools
{
    /// <summary>
    /// Implements the WeakStringCache functionality on modern .NET versions where ConcurrentDictionary is available.
    /// </summary>
    internal sealed partial class WeakStringCache : IDisposable
    {
        private readonly ConcurrentDictionary<int, StringWeakHandle> _stringsByHashCode;

        public WeakStringCache()
        {
            _stringsByHashCode = new ConcurrentDictionary<int, StringWeakHandle>(Environment.ProcessorCount, _initialCapacity);
        }

        /// <summary>
        /// Main entrypoint of this cache. Tries to look up a string that matches the given internable. If it succeeds, returns
        /// the string and sets cacheHit to true. If the string is not found, calls ExpensiveConvertToString on the internable,
        /// adds the resulting string to the cache, and returns it, setting cacheHit to false.
        /// </summary>
        /// <param name="internable">The internable describing the string we're looking for.</param>
        /// <param name="cacheHit">true if match found in cache, false otherwise.</param>
        /// <returns>A string matching the given internable.</returns>
        public string GetOrCreateEntry(ref InternableString internable, out bool cacheHit)
        {
            int hashCode = internable.GetHashCode();

            StringWeakHandle handle;
            string? result;

            // Get the existing handle from the cache and lock it while we're dereferencing it to prevent a race with the Scavenge
            // method running on another thread and freeing the handle from underneath us.
            if (_stringsByHashCode.TryGetValue(hashCode, out handle!))
            {
                lock (handle)
                {
                    result = handle.GetString(ref internable);
                    if (result != null)
                    {
                        cacheHit = true;
                        return result;
                    }

                    // We have the handle but it's not referencing the right string - create the right string and store it in the handle.
                    result = internable.ExpensiveConvertToString();
                    handle.SetString(result);

                    cacheHit = false;
                    return result;
                }
            }

            // We don't have the handle in the cache - create the right string, store it in the handle, and add the handle to the cache.
            result = internable.ExpensiveConvertToString();

            handle = new StringWeakHandle();
            handle.SetString(result);
            _stringsByHashCode.TryAdd(hashCode, handle);

            // Remove unused handles if our heuristic indicates that it would be productive.
            int scavengeThreshold = _scavengeThreshold;
            if (_stringsByHashCode.Count >= scavengeThreshold)
            {
                // Before we start scavenging set _scavengeThreshold to a high value to effectively lock other threads from
                // running Scavenge at the same time.
                if (Interlocked.CompareExchange(ref _scavengeThreshold, int.MaxValue, scavengeThreshold) == scavengeThreshold)
                {
                    try
                    {
                        // Get rid of unused handles.
                        Scavenge();
                    }
                    finally
                    {
                        // And do this again when the number of handles reaches double the current after-scavenge number.
                        _scavengeThreshold = _stringsByHashCode.Count * 2;
                    }
                }
            }

            cacheHit = false;
            return result;
        }

        /// <summary>
        /// Iterates over the cache and removes unused GC handles, i.e. handles that don't reference live strings.
        /// This is expensive so try to call such that the cost is amortized to O(1) per GetOrCreateEntry() invocation.
        /// </summary>
        public void Scavenge()
        {
            foreach (KeyValuePair<int, StringWeakHandle> entry in _stringsByHashCode)
            {
                // We can safely dereference entry.Value as the caller guarantees that Scavenge runs only on one thread.
                if (!entry.Value.IsUsed && _stringsByHashCode.TryRemove(entry.Key, out StringWeakHandle? removedHandle))
                {
                    lock (removedHandle)
                    {
                        // Note that the removed handle may be different from the one we got from the enumerator so check again
                        // and try to put it back if it's still in use.
                        if (!removedHandle.IsUsed || !_stringsByHashCode.TryAdd(entry.Key, removedHandle))
                        {
                            removedHandle.Free();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns internal debug counters calculated based on the current state of the cache.
        /// </summary>
        public DebugInfo GetDebugInfo()
        {
            return GetDebugInfoImpl();
        }
    }
}
