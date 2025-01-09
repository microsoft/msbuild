// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if RUNTIME_TYPE_NETCORE
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Shared;
#endif

#nullable disable

namespace Microsoft.Build.BackEnd
{
    internal static class CurrentHost
    {

#if RUNTIME_TYPE_NETCORE
        private static string s_currentHost;
#endif

        /// <summary>
        /// Identify the .NET host of the current process.
        /// </summary>
        /// <returns>The full path to the executable hosting the current process, or null if running on Full Framework on Windows.</returns>
        public static string GetCurrentHost()
        {
#if RUNTIME_TYPE_NETCORE
            if (s_currentHost == null)
            {
                string dotnetExe = Path.Combine(FileUtilities.GetFolderAbove(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory, 2),
                    NativeMethodsShared.IsWindows ? "dotnet.exe" : "dotnet");
                if (File.Exists(dotnetExe))
                {
                    s_currentHost = dotnetExe;
                }
                else
                {
                    s_currentHost = EnvironmentUtilities.ProcessPath;
                }
            }

            return s_currentHost;
#else
            return null;
#endif
        }
    }
}
