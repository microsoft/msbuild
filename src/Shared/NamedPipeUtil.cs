// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;

namespace Microsoft.Build.Shared
{
    internal static class NamedPipeUtil
    {
        internal static string GetPlatformSpecificPipeName(int? processId = null)
        {
            if (processId is null)
            {
                processId = EnvironmentUtilities.CurrentProcessId;
            }

            string pipeName = $"MSBuild{processId}";

            return GetPlatformSpecificPipeName(pipeName);
        }

        internal static string GetPlatformSpecificPipeName(string pipeName)
        {
            if (NativeMethodsShared.IsUnixLike)
            {
                // If we're on a Unix machine then named pipes are implemented using Unix Domain Sockets.
                // Most Unix systems have a maximum path length limit for Unix Domain Sockets, with
                // Mac having a particularly short one. Mac also has a generated temp directory that
                // can be quite long, leaving very little room for the actual pipe name. Fortunately,
                // '/tmp' is mandated by POSIX to always be a valid temp directory, so we can use that
                // instead.
#if !CLR2COMPATIBILITY
                return Path.Combine("/tmp", pipeName);
#else
                // We should never get here. This would be a net35 task host running on unix.
                ErrorUtilities.ThrowInternalError("Task host used on unix in retrieving the pipe name.");
                return string.Empty;
#endif
            }
            else
            {
                return pipeName;
            }
        }
    }
}
