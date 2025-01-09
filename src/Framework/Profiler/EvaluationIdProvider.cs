// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Framework.Profiler
{
    /// <summary>
    /// Assigns unique evaluation ids. Thread safe.
    /// </summary>
    internal static class EvaluationIdProvider
    {
        private static long _sAssignedId = -1;
        private static readonly long ProcessId = EnvironmentUtilities.CurrentProcessId;

        /// <summary>
        /// Returns a unique evaluation id
        /// </summary>
        /// <remarks>
        /// The id is guaranteed to be unique across all running processes.
        /// Additionally, it is monotonically increasing for callers on the same process id
        /// </remarks>
        public static long GetNextId()
        {
            checked
            {
                var nextId = Interlocked.Increment(ref _sAssignedId);
                // Returns a unique number based on nextId (a unique number for this process) and the current process Id
                // Uses the Cantor pairing function (https://en.wikipedia.org/wiki/Pairing_function) to guarantee uniqueness
                return (((nextId + ProcessId) * (nextId + ProcessId + 1)) / 2) + ProcessId;
            }
        }
    }
}
