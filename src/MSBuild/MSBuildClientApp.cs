// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using Microsoft.Build.Experimental;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Shared;

#if RUNTIME_TYPE_NETCORE
using System.IO;
using System.Diagnostics;
#endif

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// This class implements client for MSBuild server. It
    /// 1. starts the MSBuild server in a separate process if it does not yet exist.
    /// 2. establishes a connection with MSBuild server and sends a build request.
    /// 3. if server is busy, it falls back to old build behavior.
    /// </summary>
    internal static class MSBuildClientApp
    {
        /// <summary>
        /// This is the entry point for the MSBuild client.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildApp.ExitType"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        /// <remarks>
        /// The locations of msbuild exe/dll and dotnet.exe would be automatically detected if called from dotnet or msbuild cli. Calling this function from other executables might not work.
        /// </remarks>
        public static MSBuildApp.ExitType Execute(
#if FEATURE_GET_COMMANDLINE
            string commandLine,
#else
            string[] commandLine,
#endif
            CancellationToken cancellationToken)
        {
            string msbuildLocation = BuildEnvironmentHelper.Instance.CurrentMSBuildExePath;

            return Execute(
                commandLine,
                msbuildLocation,
                cancellationToken);
        }

        /// <summary>
        /// This is the entry point for the MSBuild client.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <param name="msbuildLocation"> Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildApp.ExitType"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        public static MSBuildApp.ExitType Execute(
#if FEATURE_GET_COMMANDLINE
            string commandLine,
#else
            string[] commandLine,
#endif
            string msbuildLocation,
            CancellationToken cancellationToken)
        {
            MSBuildClient msbuildClient = new MSBuildClient(commandLine, msbuildLocation);
            MSBuildClientExitResult exitResult = msbuildClient.Execute(cancellationToken);

            if (exitResult.MSBuildClientExitType == MSBuildClientExitType.ServerBusy ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.UnableToConnect ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.UnknownServerState ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.LaunchError)
            {
                if (KnownTelemetry.PartialBuildTelemetry != null)
                {
                    KnownTelemetry.PartialBuildTelemetry.ServerFallbackReason = exitResult.MSBuildClientExitType.ToString();
                }

                // Server is busy, fallback to old behavior.
                return MSBuildApp.Execute(commandLine);
            }

            if (exitResult.MSBuildClientExitType == MSBuildClientExitType.Success &&
                Enum.TryParse(exitResult.MSBuildAppExitTypeString, out MSBuildApp.ExitType MSBuildAppExitType))
            {
                // The client successfully set up a build task for MSBuild server and received the result.
                // (Which could be a failure as well). Return the received exit type.
                return MSBuildAppExitType;
            }

            return MSBuildApp.ExitType.MSBuildClientFailure;
        }

        // Copied from NodeProviderOutOfProcBase.cs
#if RUNTIME_TYPE_NETCORE
        private static string? CurrentHost;
        private static string GetCurrentHost()
        {
            if (CurrentHost == null)
            {
                string dotnetExe = Path.Combine(FileUtilities.GetFolderAbove(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory, 2),
                    NativeMethodsShared.IsWindows ? "dotnet.exe" : "dotnet");
                if (File.Exists(dotnetExe))
                {
                    CurrentHost = dotnetExe;
                }
                else
                {
                    CurrentHost = EnvironmentUtilities.ProcessPath ?? throw new InvalidOperationException("Failed to retrieve process executable.");
                }
            }

            return CurrentHost;
        }
#endif
    }
}
