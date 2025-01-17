// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.NET.StringTools
{
    /// <summary>
    /// A cache of weak GC handles pointing to strings. Weak GC handles are functionally equivalent to WeakReference's but have less overhead
    /// (they're a struct as opposed to WR which is a finalizable class) at the expense of requiring manual lifetime management. As long as
    /// a string has an ordinary strong GC root elsewhere in the process and another string with the same hashcode hasn't reused the entry,
    /// the cache has a reference to it and can match it to an internable. When the string is collected, it is also automatically "removed"
    /// from the cache by becoming unrecoverable from the GC handle. GC handles that do not reference a live string anymore are freed lazily.
    /// </summary>
    internal sealed partial class WeakStringCache : IDisposable
    {
        /// <summary>
        /// Debug stats returned by GetDebugInfo().
        /// </summary>
        public struct DebugInfo
        {
            public int LiveStringCount;
            public int CollectedStringCount;
        }

        /// <summary>
        /// Holds a weak GC handle to a string. Shared by all strings with the same hash code and referencing the last such string we've seen.
        /// </summary>
        private class StringWeakHandle
        {
            /// <summary>
            /// Weak GC handle to the last string of the given hashcode we've seen.
            /// </summary>
            public GCHandle WeakHandle;

            /// <summary>
            /// Reference used for smaller strings retained by the cache.
            /// </summary>
            private string? referencedString;

            /// <summary>
            /// Returns true if the string referenced by the handle is still alive.
            /// </summary>
            public bool IsUsed => referencedString is not null || WeakHandle.Target != null;

            /// <summary>
            /// Returns the string referenced by this handle if it is equal to the given internable.
            /// </summary>
            /// <param name="internable">The internable describing the string we're looking for.</param>
            /// <returns>The string matching the internable or null if the handle is referencing a collected string or the string is different.</returns>
            public string? GetString(ref InternableString internable)
            {
                if (referencedString is not null && internable.Equals(referencedString))
                {
                    return referencedString;
                }

                if (!WeakHandle.IsAllocated)
                {
                    return null;
                }

                if (WeakHandle.Target is not string str)
                {
                    return null;
                }

                if (internable.Equals(str))
                {
                    return str;
                }

                return null;
            }

            /// <summary>
            /// Sets the handle to the given string. If the handle is still referencing another live string, that string is effectively forgotten.
            /// </summary>
            /// <param name="str">The string to set.</param>
            public void SetString(string str)
            {
                const int stringLengthLimit = 500;
                if (str.Length > stringLengthLimit)
                {
                    if (WeakHandle.IsAllocated)
                    {
                        WeakHandle.Target = str;
                    }
                    else
                    {
                        WeakHandle = GCHandle.Alloc(str, GCHandleType.Weak);
                    }

                    referencedString = null;
                }
                else
                {
                    if (WeakHandle.IsAllocated)
                    {
                        WeakHandle.Target = null;
                    }

                    referencedString = str;
                }
            }

            /// <summary>
            /// Frees the GC handle.
            /// </summary>
            public void Free()
            {
                if (WeakHandle.IsAllocated)
                {
                    WeakHandle.Free();
                }
            }
        }

        /// <summary>
        /// Initial capacity of the underlying dictionary.
        /// </summary>
        private const int _initialCapacity = 503;

        /// <summary>
        /// The maximum size we let the collection grow before scavenging unused entries.
        /// </summary>
        private int _scavengeThreshold = _initialCapacity;

        /// <summary>
        /// Frees all GC handles and clears the cache.
        /// </summary>
        private void DisposeImpl()
        {
            foreach (KeyValuePair<int, StringWeakHandle> entry in _stringsByHashCode)
            {
                entry.Value.Free();
            }
            _stringsByHashCode.Clear();
        }

        public void Dispose()
        {
            DisposeImpl();
        }

        /// <summary>
        /// Returns internal debug counters calculated based on the current state of the cache.
        /// </summary>
        private DebugInfo GetDebugInfoImpl()
        {
            DebugInfo debugInfo = new DebugInfo();

            foreach (KeyValuePair<int, StringWeakHandle> entry in _stringsByHashCode)
            {
                if (entry.Value.IsUsed)
                {
                    debugInfo.LiveStringCount++;
                }
                else
                {
                    debugInfo.CollectedStringCount++;
                }
            }

            return debugInfo;
        }
    }
}
