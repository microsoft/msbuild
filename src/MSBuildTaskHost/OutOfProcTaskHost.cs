// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

// CR: We could move MSBuildApp.ExitType out of MSBuildApp
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// This is the Out-Of-Proc Task Host for supporting Cross-Targeting tasks.
    /// </summary>
    /// <remarks>
    /// It will be responsible for:
    /// - Task execution
    /// - Communicating with the MSBuildApp process, specifically the TaskHostFactory
    ///   (Logging messages, receiving Tasks from TaskHostFactory, sending results and other messages)
    /// </remarks>
    public static class OutOfProcTaskHost
    {
        /// <summary>
        /// Enumeration of the various ways in which the MSBuildTaskHost.exe application can exit.
        /// </summary>
        internal enum ExitType
        {
            /// <summary>
            /// The application executed successfully.
            /// </summary>
            Success,

            /// <summary>
            /// We received a request from MSBuild.exe to terminate
            /// </summary>
            TerminateRequest,

            /// <summary>
            /// A logger aborted the build.
            /// </summary>
            LoggerAbort,

            /// <summary>
            /// A logger failed unexpectedly.
            /// </summary>
            LoggerFailure,

            /// <summary>
            /// The Task Host Node did not terminate gracefully
            /// </summary>
            TaskHostNodeFailed,

            /// <summary>
            /// An unexpected failure
            /// </summary>
            Unexpected
        }

        /// <summary>
        /// Main Entry Point
        /// </summary>
        /// <remarks>
        /// We won't execute any tasks in the main thread, so we don't need to be in an STA
        /// </remarks>
        [MTAThread]
        public static int Main()
        {
            int exitCode = Execute() == ExitType.Success ? 0 : 1;
            return exitCode;
        }

        /// <summary>
        /// Orchestrates the execution of the application.
        /// Also responsible for top-level error handling.
        /// </summary>
        /// <returns>
        /// A value of Success if the bootstrapping succeeds
        /// </returns>
        internal static ExitType Execute()
        {
            switch (Environment.GetEnvironmentVariable("MSBUILDDEBUGONSTART"))
            {
#if FEATURE_DEBUG_LAUNCH
                case "1":
                    Debugger.Launch();
                    break;
#endif
                case "2":
                    // Sometimes easier to attach rather than deal with JIT prompt
                    Console.WriteLine($"Waiting for debugger to attach ({EnvironmentUtilities.ProcessPath} PID {EnvironmentUtilities.CurrentProcessId}).  Press enter to continue...");

                    Console.ReadLine();
                    break;
            }

            bool restart = false;
            do
            {
                OutOfProcTaskHostNode oopTaskHostNode = new OutOfProcTaskHostNode();
                Exception taskHostShutDownException = null;
                NodeEngineShutdownReason taskHostShutDownReason = oopTaskHostNode.Run(out taskHostShutDownException);

                if (taskHostShutDownException != null)
                {
                    return ExitType.TaskHostNodeFailed;
                }

                switch (taskHostShutDownReason)
                {
                    case NodeEngineShutdownReason.BuildComplete:
                        return ExitType.Success;

                    case NodeEngineShutdownReason.BuildCompleteReuse:
                        restart = true;
                        break;

                    default:
                        return ExitType.TaskHostNodeFailed;
                }
            }
            while (restart);

            // Should not happen
            ErrorUtilities.ThrowInternalErrorUnreachable();
            return ExitType.Unexpected;
        }
    }
}
