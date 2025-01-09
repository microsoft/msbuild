// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
#if FEATURE_SYSTEM_CONFIGURATION
using System.Configuration;
#endif
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Graph;
using Microsoft.Build.Internal;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.Debugging;
using Microsoft.Build.Shared.FileSystem;
using BinaryLogger = Microsoft.Build.Logging.BinaryLogger;
using ConsoleLogger = Microsoft.Build.Logging.ConsoleLogger;
using FileLogger = Microsoft.Build.Logging.FileLogger;
using ForwardingLoggerRecord = Microsoft.Build.Logging.ForwardingLoggerRecord;
using LoggerDescription = Microsoft.Build.Logging.LoggerDescription;
using SimpleErrorLogger = Microsoft.Build.Logging.SimpleErrorLogger.SimpleErrorLogger;
using TerminalLogger = Microsoft.Build.Logging.TerminalLogger.TerminalLogger;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// This class implements the MSBuild.exe command-line application. It processes
    /// command-line arguments and invokes the build engine.
    /// </summary>
    public static class MSBuildApp
    {
        /// <summary>
        /// Enumeration of the various ways in which the MSBuild.exe application can exit.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible", Justification = "shipped already")]
        public enum ExitType
        {
            /// <summary>
            /// The application executed successfully.
            /// </summary>
            Success,
            /// <summary>
            /// There was a syntax error in a command line argument.
            /// </summary>
            SwitchError,
            /// <summary>
            /// A command line argument was not valid.
            /// </summary>
            InitializationError,
            /// <summary>
            /// The build failed.
            /// </summary>
            BuildError,
            /// <summary>
            /// A logger aborted the build.
            /// </summary>
            LoggerAbort,
            /// <summary>
            /// A logger failed unexpectedly.
            /// </summary>
            LoggerFailure,
            /// <summary>
            /// The build stopped unexpectedly, for example,
            /// because a child died or hung.
            /// </summary>
            Unexpected,
            /// <summary>
            /// A project cache failed unexpectedly.
            /// </summary>
            ProjectCacheFailure,
            /// <summary>
            /// The client for MSBuild server failed unexpectedly, for example,
            /// because the server process died or hung.
            /// </summary>
            MSBuildClientFailure
        }

        /// <summary>
        /// Whether the static constructor ran successfully.
        /// </summary>
        private static bool s_initialized;

        /// <summary>
        /// The object used to synchronize access to shared build state
        /// </summary>
        private static readonly object s_buildLock = new object();

        /// <summary>
        /// Whether a build has started.
        /// </summary>
        private static bool s_hasBuildStarted;

        /// <summary>
        /// Event signaled when the build is complete.
        /// </summary>
        private static readonly ManualResetEvent s_buildComplete = new ManualResetEvent(false);

        /// <summary>
        /// Event signaled when the cancel method is complete.
        /// </summary>
        private static readonly ManualResetEvent s_cancelComplete = new ManualResetEvent(true);

        /// <summary>
        /// Cancel when handling Ctrl-C
        /// </summary>
        private static readonly CancellationTokenSource s_buildCancellationSource = new CancellationTokenSource();

        private static readonly char[] s_commaSemicolon = { ',', ';' };

        /// <summary>
        /// Static constructor
        /// </summary>
#pragma warning disable CA1810 // Initialize reference type static fields inline
        static MSBuildApp()
#pragma warning restore CA1810 // Initialize reference type static fields inline
        {
            try
            {
                ////////////////////////////////////////////////////////////////////////////////
                //  Only initialize static fields here, not inline!                           //
                //  This forces the type to initialize in this static constructor and thus    //
                //  any configuration file exceptions can be caught here.                     //
                ////////////////////////////////////////////////////////////////////////////////
                s_exePath = Path.GetDirectoryName(FileUtilities.ExecutingAssemblyPath);

                s_initialized = true;
            }
            catch (TypeInitializationException ex) when (ex.InnerException is not null
#if FEATURE_SYSTEM_CONFIGURATION
            && ex.InnerException is ConfigurationErrorsException
#endif
            )
            {
                HandleConfigurationException(ex);
            }
#if FEATURE_SYSTEM_CONFIGURATION
            catch (ConfigurationException ex)
            {
                HandleConfigurationException(ex);
            }
#endif
        }

        /// <summary>
        /// Static no-op method to force static constructor to run and initialize resources.
        /// This is useful for unit tests.
        /// </summary>
        internal static void Initialize()
        {
        }

        /// <summary>
        /// Dump any exceptions reading the configuration file, nicely
        /// </summary>
        private static void HandleConfigurationException(Exception ex)
        {
            // Error reading the configuration file - eg., unexpected element
            // Since we expect users to edit it to add toolsets, this is not unreasonable to expect
            StringBuilder builder = new StringBuilder();

            Exception exception = ex;
            do
            {
                string message = exception.Message.TrimEnd();

                builder.Append(message);

                // One of the exceptions is missing a period!
                if (message[message.Length - 1] != '.')
                {
                    builder.Append('.');
                }
                builder.Append(' ');

                exception = exception.InnerException;
            }
            while (exception != null);

            Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidConfigurationFile", builder.ToString()));

            s_initialized = false;
        }

        /// <summary>
        /// This is the entry point for the application.
        /// </summary>
        /// <remarks>
        /// MSBuild no longer runs any arbitrary code (tasks or loggers) on the main thread, so it never needs the
        /// main thread to be in an STA. Accordingly, to avoid ambiguity, we explicitly use the [MTAThread] attribute.
        /// This doesn't actually do any work unless COM interop occurs for some reason.
        /// We use the MultiDomain loader policy because we may create secondary AppDomains and need NGEN images
        /// for our as well as Framework assemblies to be loaded domain neutral so their native images can be used.
        /// See <see cref="NuGetFrameworkWrapper"/>.
        /// </remarks>
        /// <returns>0 on success, 1 on failure</returns>
        [MTAThread]
#if FEATURE_APPDOMAIN
        [LoaderOptimization(LoaderOptimization.MultiDomain)]
#endif
#pragma warning disable SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        public static int Main(
#if !FEATURE_GET_COMMANDLINE
            string[] args
#endif
            )
#pragma warning restore SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        {
            // Setup the console UI.
            using AutomaticEncodingRestorer _ = new();
            SetConsoleUI();

            DebuggerLaunchCheck();

            // Initialize new build telemetry and record start of this build.
            KnownTelemetry.PartialBuildTelemetry = new BuildTelemetry { StartAt = DateTime.UtcNow };

            using PerformanceLogEventListener eventListener = PerformanceLogEventListener.Create();

            if (Environment.GetEnvironmentVariable("MSBUILDDUMPPROCESSCOUNTERS") == "1")
            {
                DumpCounters(true /* initialize only */);
            }

            int exitCode;
            if (
                Environment.GetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName) == "1" &&
                !Traits.Instance.EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout &&
                CanRunServerBasedOnCommandLineSwitches(
#if FEATURE_GET_COMMANDLINE
                    Environment.CommandLine))
#else
                    ConstructArrayArg(args)))
#endif
            {
                Console.CancelKeyPress += Console_CancelKeyPress;


                // Use the client app to execute build in msbuild server. Opt-in feature.
                exitCode = ((s_initialized && MSBuildClientApp.Execute(
#if FEATURE_GET_COMMANDLINE
                Environment.CommandLine,
#else
                ConstructArrayArg(args),
#endif
                s_buildCancellationSource.Token) == ExitType.Success) ? 0 : 1);
            }
            else
            {
                // return 0 on success, non-zero on failure
                exitCode = ((s_initialized && Execute(
#if FEATURE_GET_COMMANDLINE
                Environment.CommandLine)
#else
                ConstructArrayArg(args))
#endif
                == ExitType.Success) ? 0 : 1);
            }

            if (Environment.GetEnvironmentVariable("MSBUILDDUMPPROCESSCOUNTERS") == "1")
            {
                DumpCounters(false /* log to console */);
            }

            return exitCode;
        }


        /// <summary>
        /// Returns true if arguments allows or make sense to leverage msbuild server.
        /// </summary>
        /// <remarks>
        /// Will not throw. If arguments processing fails, we will not run it on server - no reason as it will not run any build anyway.
        /// </remarks>
        private static bool CanRunServerBasedOnCommandLineSwitches(
#if FEATURE_GET_COMMANDLINE
            string commandLine)
#else
            string[] commandLine)
#endif
        {
            bool canRunServer = true;
            try
            {
                GatherAllSwitches(commandLine, out var switchesFromAutoResponseFile, out var switchesNotFromAutoResponseFile, out string fullCommandLine);
                CommandLineSwitches commandLineSwitches = CombineSwitchesRespectingPriority(switchesFromAutoResponseFile, switchesNotFromAutoResponseFile, fullCommandLine);
                if (CheckAndGatherProjectAutoResponseFile(switchesFromAutoResponseFile, commandLineSwitches, false, fullCommandLine))
                {
                    commandLineSwitches = CombineSwitchesRespectingPriority(switchesFromAutoResponseFile, switchesNotFromAutoResponseFile, fullCommandLine);
                }
                string projectFile = ProcessProjectSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Project], commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.IgnoreProjectExtensions], Directory.GetFiles);
                if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Help] ||
                    commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.NodeMode) ||
                    commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Version] ||
                    FileUtilities.IsBinaryLogFilename(projectFile) ||
                    !ProcessNodeReuseSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.NodeReuse]) ||
                    IsInteractiveBuild(commandLineSwitches))
                {
                    canRunServer = false;
                    if (KnownTelemetry.PartialBuildTelemetry != null)
                    {
                        KnownTelemetry.PartialBuildTelemetry.ServerFallbackReason = "Arguments";
                    }
                }
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("Unexpected exception during command line parsing. Can not determine if it is allowed to use Server. Fall back to old behavior. Exception: {0}", ex);
                if (KnownTelemetry.PartialBuildTelemetry != null)
                {
                    KnownTelemetry.PartialBuildTelemetry.ServerFallbackReason = "ErrorParsingCommandLine";
                }
                canRunServer = false;
            }

            return canRunServer;
        }

        private static bool IsInteractiveBuild(CommandLineSwitches commandLineSwitches)
        {
            // In 16.0 we added the /interactive command-line argument so the line below keeps back-compat
            if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Interactive) &&
                ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Interactive], true, "InvalidInteractiveValue"))
            {
                return true;
            }

            // In 15.9 we added support for the global property "NuGetInteractive" to allow SDK resolvers to be interactive.
            foreach (string parameter in commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Property])
            {
                // split each <prop>=<value> string into 2 pieces, breaking on the first = that is found
                string[] parameterSections = parameter.Split(s_propertyValueSeparator, 2);

                if (parameterSections.Length == 2 &&
                    parameterSections[0].Length > 0 &&
                    string.Equals("NuGetInteractive", parameterSections[0], StringComparison.OrdinalIgnoreCase))
                {
                    string nuGetInteractiveValue = parameterSections[1].Trim('"', ' ');
                    if (!string.Equals("false", nuGetInteractiveValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

#if !FEATURE_GET_COMMANDLINE
        /// <summary>
        /// Insert the command executable path as the first element of the args array.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private static string[] ConstructArrayArg(string[] args)
        {
            string[] newArgArray = new string[args.Length + 1];

            newArgArray[0] = BuildEnvironmentHelper.Instance.CurrentMSBuildExePath;
            Array.Copy(args, 0, newArgArray, 1, args.Length);

            return newArgArray;
        }
#endif // !FEATURE_GET_COMMANDLINE

        /// <summary>
        /// Append output file with elapsedTime
        /// </summary>
        /// <comments>
        /// This is a non-supported feature to facilitate timing multiple runs
        /// </comments>
        private static void AppendOutputFile(string path, long elapsedTime)
        {
            if (!FileSystems.Default.FileExists(path))
            {
                using StreamWriter sw = File.CreateText(path);
                sw.WriteLine(elapsedTime);
            }
            else
            {
                using StreamWriter sw = File.AppendText(path);
                sw.WriteLine(elapsedTime);
            }
        }

        /// <summary>
        /// Dump process counters in parseable format.
        /// These can't be gotten after the process ends, so log them here.
        /// These are for the current process only: remote nodes are not counted.
        /// </summary>
        /// <comments>
        /// Because some of these counters give bogus results or are poorly defined,
        /// we only dump counters if an undocumented environment variable is set.
        /// Also, the strings are not localized.
        /// Before execution, this is called with initialize only, causing counters to get called with NextValue() to
        /// initialize them.
        /// </comments>
        private static void DumpCounters(bool initializeOnly)
        {
            using Process currentProcess = Process.GetCurrentProcess();

            if (!initializeOnly)
            {
                Console.WriteLine("\n{0}{1}{0}", new string('=', 41 - ("Process".Length / 2)), "Process");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak Working Set", currentProcess.PeakWorkingSet64, "bytes");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak Paged Memory", currentProcess.PeakPagedMemorySize64, "bytes"); // Not very useful one
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak Virtual Memory", currentProcess.PeakVirtualMemorySize64, "bytes"); // Not very useful one
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak Privileged Processor Time", currentProcess.PrivilegedProcessorTime.TotalMilliseconds, "ms");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak User Processor Time", currentProcess.UserProcessorTime.TotalMilliseconds, "ms");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Peak Total Processor Time", currentProcess.TotalProcessorTime.TotalMilliseconds, "ms");

                Console.WriteLine("{0}{0}", new string('=', 41));
            }

#if FEATURE_PERFORMANCE_COUNTERS
            // Now some Windows performance counters

            // First get the instance name of this process, in order to look them up.
            // Generally, the instance names, such as "msbuild" and "msbuild#2" are non deterministic; we want this process.
            // Don't use the "ID Process" counter out of the "Process" category, as it doesn't use the same naming scheme
            // as the .NET counters. However, the "Process ID" counter out of the ".NET CLR Memory" category apparently uses
            // the same scheme as the other .NET categories.
            string currentInstance = null;
            PerformanceCounterCategory processCategory = new PerformanceCounterCategory("Process");
            foreach (string instance in processCategory.GetInstanceNames())
            {
                using PerformanceCounter counter = new PerformanceCounter(".NET CLR Memory", "Process ID", instance, true);
                try
                {
                    if ((int)counter.RawValue == EnvironmentUtilities.CurrentProcessId)
                    {
                        currentInstance = instance;
                        break;
                    }
                }
                catch (InvalidOperationException) // Instance 'WmiApSrv' does not exist in the specified Category. (??)
                {
                }
            }

            foreach (PerformanceCounterCategory category in PerformanceCounterCategory.GetCategories())
            {
                DumpAllInCategory(currentInstance, category, initializeOnly);
            }
#endif
        }

#if FEATURE_PERFORMANCE_COUNTERS
        /// <summary>
        /// Dumps all counters in the category
        /// </summary>
        private static void DumpAllInCategory(string currentInstance, PerformanceCounterCategory category, bool initializeOnly)
        {
            if (category.CategoryName.IndexOf("remoting", StringComparison.OrdinalIgnoreCase) != -1) // not interesting
            {
                return;
            }

            PerformanceCounter[] counters;
            try
            {
                counters = category.GetCounters(currentInstance);
            }
            catch (InvalidOperationException)
            {
                // This is a system-wide category, ignore those
                return;
            }

            if (!initializeOnly)
            {
                Console.WriteLine("\n{0}{1}{0}", new string('=', 41 - (category.CategoryName.Length / 2)), category.CategoryName);
            }

            foreach (PerformanceCounter counter in counters)
            {
                DumpCounter(counter, initializeOnly);
            }

            if (!initializeOnly)
            {
                Console.WriteLine("{0}{0}", new string('=', 41));
            }
        }

        /// <summary>
        /// Dumps one counter
        /// </summary>
        private static void DumpCounter(PerformanceCounter counter, bool initializeOnly)
        {
            try
            {
                if (counter.CounterName.IndexOf("not displayed", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    return;
                }

                float value = counter.NextValue();

                if (!initializeOnly)
                {
                    string friendlyCounterType = GetFriendlyCounterType(counter.CounterType, counter.CounterName);

                    // At least some (such as % in GC; maybe all) "%" counters are already multiplied by 100. So we don't do that here.

                    // Show decimal places if meaningful
                    string valueFormat = value < 10 ? "{0,20:N2}" : "{0,20:N0}";

                    string valueString = string.Format(CultureInfo.CurrentCulture, valueFormat, value);

                    Console.WriteLine("||{0,50}|{1}|{2,8}|", counter.CounterName, valueString, friendlyCounterType);
                }
            }
            catch (InvalidOperationException) // Instance 'WmiApSrv' does not exist in the specified Category. (??)
            {
            }
        }

        /// <summary>
        /// Gets a friendly representation of the counter units
        /// </summary>
        private static string GetFriendlyCounterType(PerformanceCounterType type, string name)
        {
            if (name.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) != -1)
            {
                return "bytes";
            }

            if (name.IndexOf("threads", StringComparison.OrdinalIgnoreCase) != -1)
            {
                return "threads";
            }

            switch (type)
            {
                case PerformanceCounterType.ElapsedTime:
                case PerformanceCounterType.AverageTimer32:
                    return "s";

                case PerformanceCounterType.Timer100Ns:
                case PerformanceCounterType.Timer100NsInverse:
                    return "100ns";

                case PerformanceCounterType.SampleCounter:
                case PerformanceCounterType.AverageCount64:
                case PerformanceCounterType.NumberOfItems32:
                case PerformanceCounterType.NumberOfItems64:
                case PerformanceCounterType.NumberOfItemsHEX32:
                case PerformanceCounterType.NumberOfItemsHEX64:
                case PerformanceCounterType.RateOfCountsPerSecond32:
                case PerformanceCounterType.RateOfCountsPerSecond64:
                case PerformanceCounterType.CountPerTimeInterval32:
                case PerformanceCounterType.CountPerTimeInterval64:
                case PerformanceCounterType.CounterTimer:
                case PerformanceCounterType.CounterTimerInverse:
                case PerformanceCounterType.CounterMultiTimer:
                case PerformanceCounterType.CounterMultiTimerInverse:
                case PerformanceCounterType.CounterDelta32:
                case PerformanceCounterType.CounterDelta64:
                    return "#";

                case PerformanceCounterType.CounterMultiTimer100Ns:
                case PerformanceCounterType.CounterMultiTimer100NsInverse:
                case PerformanceCounterType.RawFraction:
                case PerformanceCounterType.SampleFraction:
                    return "%";

                case PerformanceCounterType.AverageBase:
                case PerformanceCounterType.RawBase:
                case PerformanceCounterType.SampleBase:
                case PerformanceCounterType.CounterMultiBase:
                default:
                    return "?";
            }
        }
#endif
        /// <summary>
        /// Launch debugger if it's requested by environment variable "MSBUILDDEBUGONSTART".
        /// </summary>
        private static void DebuggerLaunchCheck()
        {
            if (Debugger.IsAttached)
            {
                return;
            }

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
        }

        /// <summary>
        /// Orchestrates the execution of the application, and is also responsible
        /// for top-level error handling.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <returns>A value of type ExitType that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        public static ExitType Execute(
#if FEATURE_GET_COMMANDLINE
            string commandLine)
#else
            string[] commandLine)
#endif
        {
            DebuggerLaunchCheck();

            // Initialize new build telemetry and record start of this build, if not initialized already
            KnownTelemetry.PartialBuildTelemetry ??= new BuildTelemetry { StartAt = DateTime.UtcNow };

            // Indicate to the engine that it can toss extraneous file content
            // when it loads microsoft.*.targets. We can't do this in the general case,
            // because tasks in the build can (and occasionally do) load MSBuild format files
            // with our OM and modify and save them. They'll never do this for Microsoft.*.targets, though,
            // and those form the great majority of our unnecessary memory use.
            Environment.SetEnvironmentVariable("MSBuildLoadMicrosoftTargetsReadOnly", "true");

#if FEATURE_GET_COMMANDLINE
            ErrorUtilities.VerifyThrowArgumentLength(commandLine, nameof(commandLine));
#endif

            AppDomain.CurrentDomain.UnhandledException += ExceptionHandling.UnhandledExceptionHandler;

            ExitType exitType = ExitType.Success;

            ConsoleCancelEventHandler cancelHandler = Console_CancelKeyPress;

            TextWriter preprocessWriter = null;
            TextWriter targetsWriter = null;
            try
            {
#if FEATURE_GET_COMMANDLINE
                MSBuildEventSource.Log.MSBuildExeStart(commandLine);
#else
                if (MSBuildEventSource.Log.IsEnabled())
                {
                    MSBuildEventSource.Log.MSBuildExeStart(string.Join(" ", commandLine));
                }
#endif
                Console.CancelKeyPress += cancelHandler;

                // check the operating system the code is running on
                VerifyThrowSupportedOS();

                // reset the application state for this new build
                ResetBuildState();

                // process the detected command line switches -- gather build information, take action on non-build switches, and
                // check for non-trivial errors
                string projectFile = null;
                string[] targets = Array.Empty<string>();
                string toolsVersion = null;
                Dictionary<string, string> globalProperties = null;
                Dictionary<string, string> restoreProperties = null;
                ILogger[] loggers = Array.Empty<ILogger>();
                LoggerVerbosity verbosity = LoggerVerbosity.Normal;
                LoggerVerbosity originalVerbosity = LoggerVerbosity.Normal;
                List<DistributedLoggerRecord> distributedLoggerRecords = null;
#if FEATURE_XML_SCHEMA_VALIDATION
                bool needToValidateProject = false;
                string schemaFile = null;
#endif
                int cpuCount = 1;
#if FEATURE_NODE_REUSE
                bool enableNodeReuse = true;
#else
                bool enableNodeReuse = false;
#endif
                bool detailedSummary = false;
                ISet<string> warningsAsErrors = null;
                ISet<string> warningsNotAsErrors = null;
                ISet<string> warningsAsMessages = null;
                bool enableRestore = Traits.Instance.EnableRestoreFirst;
                ProfilerLogger profilerLogger = null;
                bool enableProfiler = false;
                bool interactive = false;
                ProjectIsolationMode isolateProjects = ProjectIsolationMode.False;
                GraphBuildOptions graphBuildOptions = null;
                bool lowPriority = false;
                string[] inputResultsCaches = null;
                string outputResultsCache = null;
                bool question = false;
                bool isBuildCheckEnabled = false;
                string[] getProperty = Array.Empty<string>();
                string[] getItem = Array.Empty<string>();
                string[] getTargetResult = Array.Empty<string>();
                string getResultOutputFile = string.Empty;
                BuildResult result = null;
#if FEATURE_REPORTFILEACCESSES
                bool reportFileAccesses = false;
#endif

                GatherAllSwitches(commandLine, out var switchesFromAutoResponseFile, out var switchesNotFromAutoResponseFile, out _);
                bool buildCanBeInvoked = ProcessCommandLineSwitches(
                                            switchesFromAutoResponseFile,
                                            switchesNotFromAutoResponseFile,
                                            ref projectFile,
                                            ref targets,
                                            ref toolsVersion,
                                            ref globalProperties,
                                            ref loggers,
                                            ref verbosity,
                                            ref originalVerbosity,
                                            ref distributedLoggerRecords,
#if FEATURE_XML_SCHEMA_VALIDATION
                                            ref needToValidateProject,
                                            ref schemaFile,
#endif
                                            ref cpuCount,
                                            ref enableNodeReuse,
                                            ref preprocessWriter,
                                            ref targetsWriter,
                                            ref detailedSummary,
                                            ref warningsAsErrors,
                                            ref warningsNotAsErrors,
                                            ref warningsAsMessages,
                                            ref enableRestore,
                                            ref interactive,
                                            ref profilerLogger,
                                            ref enableProfiler,
                                            ref restoreProperties,
                                            ref isolateProjects,
                                            ref graphBuildOptions,
                                            ref inputResultsCaches,
                                            ref outputResultsCache,
#if FEATURE_REPORTFILEACCESSES
                                            ref reportFileAccesses,
#endif
                                            ref lowPriority,
                                            ref question,
                                            ref isBuildCheckEnabled,
                                            ref getProperty,
                                            ref getItem,
                                            ref getTargetResult,
                                            ref getResultOutputFile,
                                            recursing: false,
#if FEATURE_GET_COMMANDLINE
                                            commandLine);
#else
                                            string.Join(' ', commandLine));
#endif

                CommandLineSwitches.SwitchesFromResponseFiles = null;

                if (buildCanBeInvoked)
                {
                    // Unfortunately /m isn't the default, and we are not yet brave enough to make it the default.
                    // However we want to give a hint to anyone who is building single proc without realizing it that there
                    // is a better way.
                    // Only display the message if /m isn't provided
                    if (cpuCount == 1 && FileUtilities.IsSolutionFilename(projectFile) && verbosity > LoggerVerbosity.Minimal
                        && switchesNotFromAutoResponseFile[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount].Length == 0
                        && switchesFromAutoResponseFile[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount].Length == 0
                        && preprocessWriter != null
                        && targetsWriter != null)
                    {
                        Console.WriteLine(ResourceUtilities.GetResourceString("PossiblyOmittedMaxCPUSwitch"));
                    }

                    if (preprocessWriter != null && !BuildEnvironmentHelper.Instance.RunningTests)
                    {
                        // Indicate to the engine that it can NOT toss extraneous file content: we want to
                        // see that in preprocessing/debugging
                        Environment.SetEnvironmentVariable("MSBUILDLOADALLFILESASWRITEABLE", "1");
                    }

                    DateTime t1 = DateTime.Now;

                    bool outputPropertiesItemsOrTargetResults = getProperty.Length > 0 || getItem.Length > 0 || getTargetResult.Length > 0;

                    // If the primary file passed to MSBuild is a .binlog file, play it back into passed loggers
                    // as if a build is happening
                    if (FileUtilities.IsBinaryLogFilename(projectFile))
                    {
                        ReplayBinaryLog(projectFile, loggers, distributedLoggerRecords, cpuCount, isBuildCheckEnabled);
                    }
                    else if (outputPropertiesItemsOrTargetResults && FileUtilities.IsSolutionFilename(projectFile))
                    {
                        exitType = ExitType.BuildError;
                        CommandLineSwitchException.Throw("SolutionBuildInvalidForCommandLineEvaluation",
                            getProperty.Length > 0 ? "getProperty" :
                            getItem.Length > 0 ? "getItem" :
                            "getTargetResult");
                    }
                    else if ((getProperty.Length > 0 || getItem.Length > 0) && (targets is null || targets.Length == 0))
                    {
                        try
                        {
                            using (ProjectCollection collection = new(globalProperties, loggers, ToolsetDefinitionLocations.Default))
                            {
                                Project project = collection.LoadProject(projectFile, globalProperties, toolsVersion);

                                if (getResultOutputFile.Length == 0)
                                {
                                    exitType = OutputPropertiesAfterEvaluation(getProperty, getItem, project, Console.Out);
                                }
                                else
                                {
                                    using (var streamWriter = new StreamWriter(getResultOutputFile))
                                    {
                                        exitType = OutputPropertiesAfterEvaluation(getProperty, getItem, project, streamWriter);
                                    }
                                }
                                collection.LogBuildFinishedEvent(exitType == ExitType.Success);
                            }
                        }
                        catch (InvalidProjectFileException)
                        {
                            exitType = ExitType.BuildError;
                        }
                    }
                    else // regular build
                    {
                        // if everything checks out, and sufficient information is available to start building
                        if (
                            !BuildProject(
                                projectFile,
                                targets,
                                toolsVersion,
                                globalProperties,
                                restoreProperties,
                                loggers,
                                verbosity,
                                distributedLoggerRecords.ToArray(),
#if FEATURE_XML_SCHEMA_VALIDATION
                                needToValidateProject, schemaFile,
#endif
                                    cpuCount,
                                    enableNodeReuse,
                                    preprocessWriter,
                                    targetsWriter,
                                    detailedSummary,
                                    warningsAsErrors,
                                    warningsNotAsErrors,
                                    warningsAsMessages,
                                    enableRestore,
                                    profilerLogger,
                                    enableProfiler,
                                    interactive,
                                    isolateProjects,
                                    graphBuildOptions,
                                    lowPriority,
                                    question,
                                    isBuildCheckEnabled,
                                    inputResultsCaches,
                                    outputResultsCache,
                                    saveProjectResult: outputPropertiesItemsOrTargetResults,
                                    ref result,
#if FEATURE_REPORTFILEACCESSES
                                    reportFileAccesses,
#endif
                                    commandLine))
                        {
                            exitType = ExitType.BuildError;
                        }
                    } // end of build

                    DateTime t2 = DateTime.Now;

                    TimeSpan elapsedTime = t2.Subtract(t1);

                    string timerOutputFilename = Environment.GetEnvironmentVariable("MSBUILDTIMEROUTPUTS");

                    if (outputPropertiesItemsOrTargetResults && targets?.Length > 0 && result is not null)
                    {
                        if (getResultOutputFile.Length == 0)
                        {
                            exitType = OutputBuildInformationInJson(result, getProperty, getItem, getTargetResult, loggers, exitType, Console.Out);
                        }
                        else
                        {
                            using (var streamWriter = new StreamWriter(getResultOutputFile))
                            {
                                exitType = OutputBuildInformationInJson(result, getProperty, getItem, getTargetResult, loggers, exitType, streamWriter);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(timerOutputFilename))
                    {
                        AppendOutputFile(timerOutputFilename, (long)elapsedTime.TotalMilliseconds);
                    }
                }
                else
                {
                    // if there was no need to start the build e.g. because /help was triggered
                    // do nothing
                }
            }
            /**********************************************************************************************************************
             * WARNING: Do NOT add any more catch blocks below! Exceptions should be caught as close to their point of origin as
             * possible, and converted into one of the known exceptions. The code that causes an exception best understands the
             * reason for the exception, and only that code can provide the proper error message. We do NOT want to display
             * messages from unknown exceptions, because those messages are most likely neither localized, nor composed in the
             * canonical form with the correct prefix.
             *********************************************************************************************************************/
            // handle switch errors
            catch (CommandLineSwitchException e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine();
                // prompt user to display help for proper switch usage
                ShowHelpPrompt();

                exitType = ExitType.SwitchError;
            }
            // handle configuration exceptions: problems reading toolset information from msbuild.exe.config or the registry
            catch (InvalidToolsetDefinitionException e)
            {
                // Brief prefix to indicate that it's a configuration failure, and provide the "error" indication
                Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("ConfigurationFailurePrefixNoErrorCode", e.ErrorCode, e.Message));

                exitType = ExitType.InitializationError;
            }
            // handle initialization failures
            catch (InitializationException e)
            {
                Console.WriteLine(e.Message);

                exitType = ExitType.InitializationError;
            }
            // handle polite logger failures: don't dump the stack or trigger watson for these
            catch (LoggerException e)
            {
                // display the localized message from the outer exception in canonical format
                if (e.ErrorCode != null)
                {
                    // Brief prefix to indicate that it's a logger failure, and provide the "error" indication
                    Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("LoggerFailurePrefixNoErrorCode", e.ErrorCode, e.Message));
                }
                else
                {
                    // Brief prefix to indicate that it's a logger failure, adding a generic error code to make sure
                    // there's something for the user to look up in the documentation
                    Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("LoggerFailurePrefixWithErrorCode", e.Message));
                }

                if (e.InnerException != null)
                {
                    // write out exception details -- don't bother triggering Watson, because most of these exceptions will be coming
                    // from buggy loggers written by users
                    Console.WriteLine(e.InnerException.ToString());
                }

                exitType = ExitType.LoggerAbort;
            }
            // handle logger failures (logger bugs)
            catch (InternalLoggerException e)
            {
                if (!e.InitializationException)
                {
                    // display the localized message from the outer exception in canonical format
                    Console.WriteLine($"MSBUILD : error {e.ErrorCode}: {e.Message}");
#if DEBUG
                    Console.WriteLine("This is an unhandled exception from a logger -- PLEASE OPEN A BUG AGAINST THE LOGGER OWNER.");
#endif
                    // write out exception details -- don't bother triggering Watson, because most of these exceptions will be coming
                    // from buggy loggers written by users
                    Console.WriteLine(e.InnerException.ToString());

                    exitType = ExitType.LoggerFailure;
                }
                else
                {
                    Console.WriteLine(
                        $"MSBUILD : error {e.ErrorCode}: {e.Message}{(e.InnerException != null ? $" {e.InnerException.Message}" : "")}");
                    exitType = ExitType.InitializationError;
                }
            }
            catch (ProjectCacheException e)
            {
                Console.WriteLine($"MSBUILD : error {e.ErrorCode}: {e.Message}");

#if DEBUG
                if (!e.HasBeenLoggedByProjectCache && e.InnerException != null)
                {
                    Console.WriteLine("This is an unhandled exception from a project cache -- PLEASE OPEN A BUG AGAINST THE PROJECT CACHE OWNER.");
                }
#endif

                if (e.InnerException is not null)
                {
                    Console.WriteLine(e.InnerException.ToString());
                }

                exitType = ExitType.ProjectCacheFailure;
            }
            catch (BuildAbortedException e)
            {
                Console.WriteLine(
                    $"MSBUILD : error {e.ErrorCode}: {e.Message}{(e.InnerException != null ? $" {e.InnerException.Message}" : string.Empty)}");

                exitType = ExitType.Unexpected;
            }
            // handle fatal errors
            catch (Exception e)
            {
                // display a generic localized message for the user
                Console.WriteLine("{0}\r\n{1}", AssemblyResources.GetString("FatalError"), e.ToString());
#if DEBUG
                Console.WriteLine("This is an unhandled exception in MSBuild Engine -- PLEASE OPEN A BUG AGAINST THE MSBUILD TEAM.\r\n{0}", e.ToString());
#endif
                // rethrow, in case Watson is enabled on the machine -- if not, the CLR will write out exception details
                // allow the build lab to set an env var to avoid jamming the build
                if (Environment.GetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER") != "1")
                {
                    throw;
                }
            }
            finally
            {
                s_buildComplete.Set();
                Console.CancelKeyPress -= cancelHandler;

                // Wait for any pending cancel, so that we get any remaining messages
                s_cancelComplete.WaitOne();

                NativeMethodsShared.RestoreConsoleMode(s_originalConsoleMode);

                preprocessWriter?.Dispose();
                targetsWriter?.Dispose();

#if FEATURE_GET_COMMANDLINE
                MSBuildEventSource.Log.MSBuildExeStop(commandLine);
#else
                if (MSBuildEventSource.Log.IsEnabled())
                {
                    MSBuildEventSource.Log.MSBuildExeStop(string.Join(" ", commandLine));
                }
#endif
            }
            /**********************************************************************************************************************
             * WARNING: Do NOT add any more catch blocks above!
             *********************************************************************************************************************/

            return exitType;
        }

        private static ExitType OutputPropertiesAfterEvaluation(string[] getProperty, string[] getItem, Project project, TextWriter outputStream)
        {
            // Special case if the user requests exactly one property: skip json formatting
            if (getProperty.Length == 1 && getItem.Length == 0)
            {
                outputStream.WriteLine(project.GetPropertyValue(getProperty[0]));
            }
            else
            {
                JsonOutputFormatter jsonOutputFormatter = new();
                jsonOutputFormatter.AddPropertiesInJsonFormat(getProperty, property => project.GetPropertyValue(property));
                jsonOutputFormatter.AddItemsInJsonFormat(getItem, project);
                outputStream.WriteLine(jsonOutputFormatter.ToString());
            }

            outputStream.Flush();

            return ExitType.Success;
        }

        private static ExitType OutputBuildInformationInJson(BuildResult result, string[] getProperty, string[] getItem, string[] getTargetResult, ILogger[] loggers, ExitType exitType, TextWriter outputStream)
        {
            ProjectInstance builtProject = result.ProjectStateAfterBuild;

            ILogger logger = loggers.FirstOrDefault(l => l is SimpleErrorLogger);
            if (logger is not null)
            {
                exitType = exitType == ExitType.Success && (logger as SimpleErrorLogger).HasLoggedErrors ? ExitType.BuildError : exitType;
            }

            if (builtProject is null)
            {
                // Build failed; do not proceed
                Console.Error.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("BuildFailedWithPropertiesItemsOrTargetResultsRequested"));
            }
            // Special case if the user requests exactly one property: skip the json formatting
            else if (getProperty.Length == 1 && getItem.Length == 0 && getTargetResult.Length == 0)
            {
                outputStream.WriteLine(builtProject.GetPropertyValue(getProperty[0]));
            }
            else
            {
                JsonOutputFormatter jsonOutputFormatter = new();
                jsonOutputFormatter.AddPropertiesInJsonFormat(getProperty, property => builtProject.GetPropertyValue(property));
                jsonOutputFormatter.AddItemInstancesInJsonFormat(getItem, builtProject);
                jsonOutputFormatter.AddTargetResultsInJsonFormat(getTargetResult, result);
                outputStream.WriteLine(jsonOutputFormatter.ToString());
            }

            outputStream.Flush();

            return exitType;
        }

        /// <summary>
        /// Handler for when CTRL-C or CTRL-BREAK is called.
        /// CTRL-BREAK means "die immediately"
        /// CTRL-C means "try to stop work and exit cleanly"
        /// </summary>
        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
            {
                Environment.Exit(1); // the process will now be terminated rudely
            }

            e.Cancel = true; // do not terminate rudely

            if (s_buildCancellationSource.IsCancellationRequested)
            {
                return;
            }

            s_buildCancellationSource.Cancel();
            

            // The OS takes a lock in
            // kernel32.dll!_SetConsoleCtrlHandler, so if a task
            // waits for that lock somehow before quitting, it would hang
            // because we're in it here. One way a task can end up here is
            // by calling Microsoft.Win32.SystemEvents.Initialize.
            // So do our work asynchronously so we can return immediately.
            // We're already on a threadpool thread anyway.
            WaitCallback callback = delegate
            {
                try
                {
                    s_cancelComplete.Reset();

                    // If the build is already complete, just exit.
                    if (s_buildComplete.WaitOne(0))
                    {
                        s_cancelComplete.Set();
                        return;
                    }

                    // If the build has already started (or already finished), we will cancel it
                    // If the build has not yet started, it will cancel itself, because
                    // we set alreadyCalled=1
                    bool hasBuildStarted;
                    lock (s_buildLock)
                    {
                        hasBuildStarted = s_hasBuildStarted;
                    }

                    if (hasBuildStarted)
                    {
                        BuildManager.DefaultBuildManager.CancelAllSubmissions();
                        s_buildComplete.WaitOne();
                    }

                    s_cancelComplete.Set(); // This will release our main Execute method so we can finally exit.
                }
                finally
                {
                    // Server node shall terminate after it received CancelKey press.
                    if (s_isServerNode)
                    {
                        Environment.Exit(0); // the process can now be terminated as everything has already been gracefully cancelled.
                    }
                }
            };

            ThreadPoolExtensions.QueueThreadPoolWorkItemWithCulture(callback, CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        }

        /// <summary>
        /// Clears out any state accumulated from previous builds, and resets
        /// member data in preparation for a new build.
        /// </summary>
        private static void ResetBuildState()
        {
            ResetGatheringSwitchesState();
        }

        private static void ResetGatheringSwitchesState()
        {
            s_includedResponseFiles = new List<string>();
            usingSwitchesFromAutoResponseFile = false;
            CommandLineSwitches.SwitchesFromResponseFiles = new();
        }

        /// <summary>
        /// The location of the application executable.
        /// </summary>
        /// <remarks>
        /// Initialized in the static constructor. See comment there.
        /// </remarks>
        private static readonly string s_exePath; // Do not initialize

        /// <summary>
        /// Name of the exe (presumably msbuild.exe)
        /// </summary>
        private static string s_exeName;

        /// <summary>
        /// Default name for the msbuild log file
        /// </summary>
        private const string msbuildLogFileName = "msbuild.log";

        /// <summary>
        /// List of messages to be sent to the logger when it is attached
        /// </summary>
        private static readonly List<BuildManager.DeferredBuildMessage> s_globalMessagesToLogInBuildLoggers = new();

        /// <summary>
        /// The original console output mode if we changed it as part of initialization.
        /// </summary>
        private static uint? s_originalConsoleMode = null;

        /// <summary>
        /// Initializes the build engine, and starts the project building.
        /// </summary>
        /// <returns>true, if build succeeds</returns>
        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Not going to refactor it right now")]
        internal static bool BuildProject(
            string projectFile,
            string[] targets,
            string toolsVersion,
            Dictionary<string, string> globalProperties,
            Dictionary<string, string> restoreProperties,
            ILogger[] loggers,
            LoggerVerbosity verbosity,
            DistributedLoggerRecord[] distributedLoggerRecords,
#if FEATURE_XML_SCHEMA_VALIDATION
            bool needToValidateProject,
            string schemaFile,
#endif
            int cpuCount,
            bool enableNodeReuse,
            TextWriter preprocessWriter,
            TextWriter targetsWriter,
            bool detailedSummary,
            ISet<string> warningsAsErrors,
            ISet<string> warningsNotAsErrors,
            ISet<string> warningsAsMessages,
            bool enableRestore,
            ProfilerLogger profilerLogger,
            bool enableProfiler,
            bool interactive,
            ProjectIsolationMode isolateProjects,
            GraphBuildOptions graphBuildOptions,
            bool lowPriority,
            bool question,
            bool isBuildCheckEnabled,
            string[] inputResultsCaches,
            string outputResultsCache,
            bool saveProjectResult,
            ref BuildResult result,
#if FEATURE_REPORTFILEACCESSES
            bool reportFileAccesses,
#endif
#if FEATURE_GET_COMMANDLINE
            string commandLine)
#else
            string[] commandLine)
#endif
        {
            if (FileUtilities.IsVCProjFilename(projectFile) || FileUtilities.IsDspFilename(projectFile))
            {
                InitializationException.Throw(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("ProjectUpgradeNeededToVcxProj", projectFile), null);
            }

            bool success = true;

            ProjectCollection projectCollection = null;
            bool onlyLogCriticalEvents = false;

            try
            {
                List<ForwardingLoggerRecord> remoteLoggerRecords = new List<ForwardingLoggerRecord>();
                foreach (DistributedLoggerRecord distRecord in distributedLoggerRecords)
                {
                    remoteLoggerRecords.Add(new ForwardingLoggerRecord(distRecord.CentralLogger, distRecord.ForwardingLoggerDescription));
                }

                // Targeted perf optimization for the case where we only have our own parallel console logger, and verbosity is quiet. In such a case
                // we know we won't emit any messages except for errors and warnings, so the engine should not bother even logging them.
                // If we're using the original serial console logger we can't do this, as it shows project started/finished context
                // around errors and warnings.
                // Telling the engine to not bother logging non-critical messages means that typically it can avoid loading any resources in the successful
                // build case.
                if (loggers.Length == 1 &&
                    remoteLoggerRecords.Count == 0 &&
                    verbosity == LoggerVerbosity.Quiet &&
                    loggers[0].Parameters != null &&
                    loggers[0].Parameters.IndexOf("ENABLEMPLOGGING", StringComparison.OrdinalIgnoreCase) != -1 &&
                    loggers[0].Parameters.IndexOf("DISABLEMPLOGGING", StringComparison.OrdinalIgnoreCase) == -1 &&
                    loggers[0].Parameters.IndexOf("V=", StringComparison.OrdinalIgnoreCase) == -1 &&                // Console logger could have had a verbosity
                    loggers[0].Parameters.IndexOf("VERBOSITY=", StringComparison.OrdinalIgnoreCase) == -1)          // override with the /clp switch
                {
                    // Must be exactly the console logger, not a derived type like the file logger.
                    Type t1 = loggers[0].GetType();
                    Type t2 = typeof(ConsoleLogger);
                    if (t1 == t2)
                    {
                        onlyLogCriticalEvents = true;
                    }
                }

                // HACK HACK: this enables task parameter logging.
                // This is a hack for now to make sure the perf hit only happens
                // on diagnostic. This should be changed to pipe it through properly,
                // perhaps as part of a fuller tracing feature.
                bool logTaskInputs = verbosity == LoggerVerbosity.Diagnostic || isBuildCheckEnabled;

                if (!logTaskInputs)
                {
                    foreach (var logger in loggers)
                    {
                        if (logger.Parameters != null &&
                            (logger.Parameters.IndexOf("V=DIAG", StringComparison.OrdinalIgnoreCase) != -1 ||
                             logger.Parameters.IndexOf("VERBOSITY=DIAG", StringComparison.OrdinalIgnoreCase) != -1))
                        {
                            logTaskInputs = true;
                            break;
                        }
                    }
                }

                if (!logTaskInputs)
                {
                    foreach (var logger in distributedLoggerRecords)
                    {
                        if (logger.CentralLogger?.Parameters != null &&
                            (logger.CentralLogger.Parameters.IndexOf("V=DIAG", StringComparison.OrdinalIgnoreCase) != -1 ||
                             logger.CentralLogger.Parameters.IndexOf("VERBOSITY=DIAG", StringComparison.OrdinalIgnoreCase) != -1))
                        {
                            logTaskInputs = true;
                            break;
                        }
                    }
                }

                ToolsetDefinitionLocations toolsetDefinitionLocations = ToolsetDefinitionLocations.Default;

                bool isPreprocess = preprocessWriter != null;
                bool isTargets = targetsWriter != null;

                projectCollection = new ProjectCollection(
                    globalProperties,
                    loggers,
                    null,
                    toolsetDefinitionLocations,
                    cpuCount,
                    onlyLogCriticalEvents,
                    loadProjectsReadOnly: !isPreprocess,
                    useAsynchronousLogging: true,
                    reuseProjectRootElementCache: s_isServerNode);

                if (toolsVersion != null && !projectCollection.ContainsToolset(toolsVersion))
                {
                    ThrowInvalidToolsVersionInitializationException(projectCollection.Toolsets, toolsVersion);
                }

                bool isSolution = FileUtilities.IsSolutionFilename(projectFile);

#if FEATURE_XML_SCHEMA_VALIDATION
                // If the user has requested that the schema be validated, do that here.
                if (needToValidateProject && !isSolution)
                {
                    Microsoft.Build.Evaluation.Project project = projectCollection.LoadProject(projectFile, globalProperties, toolsVersion);
                    Microsoft.Build.Evaluation.Toolset toolset = projectCollection.GetToolset(toolsVersion ?? project.ToolsVersion);

                    if (toolset == null)
                    {
                        ThrowInvalidToolsVersionInitializationException(projectCollection.Toolsets, project.ToolsVersion);
                    }

                    ProjectSchemaValidationHandler.VerifyProjectSchema(projectFile, schemaFile, toolset.ToolsPath);

                    // If there are schema validation errors, an InitializationException is thrown, so if we get here,
                    // we can safely assume that the project successfully validated.
                    projectCollection.UnloadProject(project);
                }
#endif

                if (isPreprocess)
                {
                    success = false;

                    // TODO: Support /preprocess for solution files. https://github.com/dotnet/msbuild/issues/7697
                    if (isSolution)
                    {
                        Console.WriteLine(ResourceUtilities.GetResourceString("UnsupportedSwitchForSolutionFiles"), CommandLineSwitches.ParameterizedSwitch.Preprocess);
                    }
                    else
                    {
                        Project project = projectCollection.LoadProject(projectFile, globalProperties, toolsVersion);

                        project.SaveLogicalProject(preprocessWriter);

                        projectCollection.UnloadProject(project);

                        success = true;
                    }
                }

                if (isTargets && success)
                {
                    success = false;

                    // TODO: Support /targets for solution files. https://github.com/dotnet/msbuild/issues/7697
                    if (isSolution)
                    {
                        Console.WriteLine(ResourceUtilities.GetResourceString("UnsupportedSwitchForSolutionFiles"), CommandLineSwitches.ParameterizedSwitch.Targets);
                    }
                    else
                    {
                        success = PrintTargets(projectFile, toolsVersion, globalProperties, targetsWriter, projectCollection);
                    }
                }

                if (!isPreprocess && !isTargets)
                {
                    success = false;
                    BuildParameters parameters = new BuildParameters(projectCollection);

                    // By default we log synchronously to the console for compatibility with previous versions,
                    // but it is slightly slower
                    if (!string.Equals(Environment.GetEnvironmentVariable("MSBUILDLOGASYNC"), "1", StringComparison.Ordinal))
                    {
                        parameters.UseSynchronousLogging = true;
                    }

                    parameters.EnableNodeReuse = enableNodeReuse;
                    parameters.LowPriority = lowPriority;
#if FEATURE_ASSEMBLY_LOCATION
                    parameters.NodeExeLocation = Assembly.GetExecutingAssembly().Location;
#else
                    parameters.NodeExeLocation = BuildEnvironmentHelper.Instance.CurrentMSBuildExePath;
#endif
                    parameters.MaxNodeCount = cpuCount;
                    parameters.Loggers = projectCollection.Loggers;
                    parameters.ForwardingLoggers = remoteLoggerRecords;
                    parameters.ToolsetDefinitionLocations = Microsoft.Build.Evaluation.ToolsetDefinitionLocations.ConfigurationFile | Microsoft.Build.Evaluation.ToolsetDefinitionLocations.Registry;
                    parameters.DetailedSummary = detailedSummary;
                    parameters.LogTaskInputs = logTaskInputs;
                    parameters.WarningsAsErrors = warningsAsErrors;
                    parameters.WarningsNotAsErrors = warningsNotAsErrors;
                    parameters.WarningsAsMessages = warningsAsMessages;
                    parameters.Interactive = interactive;
                    parameters.ProjectIsolationMode = isolateProjects;
                    parameters.InputResultsCacheFiles = inputResultsCaches;
                    parameters.OutputResultsCacheFile = outputResultsCache;
                    parameters.Question = question;
                    parameters.IsBuildCheckEnabled = isBuildCheckEnabled;
#if FEATURE_REPORTFILEACCESSES
                    parameters.ReportFileAccesses = reportFileAccesses;
#endif

                    // Propagate the profiler flag into the project load settings so the evaluator
                    // can pick it up
                    if (profilerLogger != null || enableProfiler)
                    {
                        parameters.ProjectLoadSettings |= ProjectLoadSettings.ProfileEvaluation;
                    }

                    if (!string.IsNullOrEmpty(toolsVersion))
                    {
                        parameters.DefaultToolsVersion = toolsVersion;
                    }

                    string memoryUseLimit = Environment.GetEnvironmentVariable("MSBUILDMEMORYUSELIMIT");
                    if (!string.IsNullOrEmpty(memoryUseLimit))
                    {
                        parameters.MemoryUseLimit = Convert.ToInt32(memoryUseLimit, CultureInfo.InvariantCulture);

                        // The following ensures that when we divide the use by node count to get the per-limit amount, we always end up with a
                        // positive value - otherwise setting it too low will result in a zero, which will enable only the default cache behavior
                        // which is not what is intended by using this environment variable.
                        if (parameters.MemoryUseLimit < parameters.MaxNodeCount)
                        {
                            parameters.MemoryUseLimit = parameters.MaxNodeCount;
                        }
                    }

                    List<BuildManager.DeferredBuildMessage> messagesToLogInBuildLoggers = new();

                    BuildManager buildManager = BuildManager.DefaultBuildManager;

                    result = null;
                    GraphBuildResult graphResult = null;

                    if (!Traits.Instance.EscapeHatches.DoNotSendDeferredMessagesToBuildManager)
                    {
                        var commandLineString =
#if FEATURE_GET_COMMANDLINE
                            commandLine;
#else
                            string.Join(" ", commandLine);
#endif
                        messagesToLogInBuildLoggers.AddRange(GetMessagesToLogInBuildLoggers(commandLineString));

                        // Log a message for every response file and include it in log
                        foreach (var responseFilePath in s_includedResponseFiles)
                        {
                            messagesToLogInBuildLoggers.Add(
                                new BuildManager.DeferredBuildMessage(
                                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                        "PickedUpSwitchesFromAutoResponse",
                                        responseFilePath),
                                    MessageImportance.Low,
                                    responseFilePath));
                        }
                    }

                    buildManager.BeginBuild(parameters, messagesToLogInBuildLoggers);

                    Exception exception = null;
                    try
                    {
                        try
                        {
                            // Determine if the user specified /Target:Restore which means we should only execute a restore in the fancy way that /restore is executed
                            bool restoreOnly = targets.Length == 1 && string.Equals(targets[0], MSBuildConstants.RestoreTargetName, StringComparison.OrdinalIgnoreCase);

                            // ExecuteRestore below changes the current working directory and does not change back. Therefore, if we try to create the request after
                            // the restore call we end up with incorrectly normalized paths to the project. To avoid that, we are preparing the request before the first
                            // build (restore) request.
                            // PS: We couldn't find a straight forward way to make the restore invocation clean up after itself, so we should this ugly but less risky
                            // approach.
                            GraphBuildRequestData graphBuildRequest = null;
                            BuildRequestData buildRequest = null;
                            if (!restoreOnly)
                            {
                                // By default, the project state is thrown out after a build. The ProvideProjectStateAfterBuild flag adds the project state after build
                                // to the BuildResult passed back at the end of the build. This can then be used to find the value of properties, items, etc. after the
                                // build is complete.
                                BuildRequestDataFlags flags = BuildRequestDataFlags.None;
                                if (saveProjectResult)
                                {
                                    flags |= BuildRequestDataFlags.ProvideProjectStateAfterBuild;
                                }

                                if (graphBuildOptions != null)
                                {
                                    graphBuildRequest = new GraphBuildRequestData(new[] { new ProjectGraphEntryPoint(projectFile, globalProperties) }, targets, null, flags, graphBuildOptions);
                                }
                                else
                                {
                                    buildRequest = new BuildRequestData(projectFile, globalProperties, toolsVersion, targets, null, flags);
                                }
                            }

                            if (enableRestore || restoreOnly)
                            {
                                result = ExecuteRestore(projectFile, toolsVersion, buildManager, restoreProperties.Count > 0 ? restoreProperties : globalProperties, saveProjectResult: saveProjectResult);

                                if (result.OverallResult != BuildResultCode.Success)
                                {
                                    return false;
                                }
                                else
                                {
                                    success = result.OverallResult == BuildResultCode.Success;
                                }
                            }

                            if (!restoreOnly)
                            {
                                if (graphBuildOptions != null)
                                {
                                    graphResult = ExecuteGraphBuild(buildManager, graphBuildRequest);

                                    if (saveProjectResult)
                                    {
                                        ProjectGraphEntryPoint entryPoint = graphBuildRequest.ProjectGraphEntryPoints.Single();
                                        if (!entryPoint.GlobalProperties.ContainsKey(PropertyNames.IsGraphBuild))
                                        {
                                            entryPoint.GlobalProperties[PropertyNames.IsGraphBuild] = "true";
                                        }

                                        result = graphResult.ResultsByNode.Single(
                                            nodeResultKvp =>
                                            nodeResultKvp.Key.ProjectInstance.FullPath.Equals(entryPoint.ProjectFile) &&
                                            nodeResultKvp.Key.ProjectInstance.GlobalProperties.Count == entryPoint.GlobalProperties.Count &&
                                            nodeResultKvp.Key.ProjectInstance.GlobalProperties.All(propertyKvp => entryPoint.GlobalProperties.TryGetValue(propertyKvp.Key, out string entryValue) &&
                                                                                                                                        entryValue.Equals(propertyKvp.Value)))
                                            .Value;
                                    }
                                    success = graphResult.OverallResult == BuildResultCode.Success;
                                }
                                else
                                {
                                    result = ExecuteBuild(buildManager, buildRequest);
                                    success = result.OverallResult == BuildResultCode.Success;
                                }
                            }
                        }
                        finally
                        {
                            buildManager.EndBuild();
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        success = false;
                    }

                    if (exception != null)
                    {
                        success = false;

                        // InvalidProjectFileExceptions and its aggregates have already been logged.
                        if (exception is not InvalidProjectFileException
                            && !(exception is AggregateException aggregateException && aggregateException.InnerExceptions.All(innerException => innerException is InvalidProjectFileException))
                            && exception is not CircularDependencyException)
                        {
                            if (exception is LoggerException or InternalLoggerException or ProjectCacheException)
                            {
                                // We will rethrow these so the outer exception handler can catch them, but we don't
                                // want to log the outer exception stack here.
                                throw exception;
                            }

                            if (exception is BuildAbortedException)
                            {
                                // this is not a bug and should not dump stack. It will already have been logged
                                // appropriately, there is no need to take any further action with it.
                            }
                            else
                            {
                                // After throwing again below the stack will be reset. Make certain we log everything we
                                // can now
                                Console.WriteLine(AssemblyResources.GetString("FatalError"));
#if DEBUG
                                Console.WriteLine("This is an unhandled exception in MSBuild -- PLEASE OPEN A BUG AGAINST THE MSBUILD TEAM.");
#endif
                                Console.WriteLine(exception.ToString());
                                Console.WriteLine();

                                throw exception;
                            }
                        }
                    }
                }
            }
            // handle project file errors
            catch (InvalidProjectFileException ex)
            {
                // just eat the exception because it has already been logged
                ErrorUtilities.VerifyThrow(ex.HasBeenLogged, "Should have been logged");
                success = false;
            }
            finally
            {
                projectCollection?.Dispose();
                FileUtilities.ClearCacheDirectory();

                // Build manager shall be reused for all build sessions.
                // If, for one reason or another, this behavior needs to change in future
                // please be aware that current code creates and keep running  InProcNode even
                // when its owning default build manager is disposed resulting in leek of memory and threads.
                if (!s_isServerNode)
                {
                    BuildManager.DefaultBuildManager.Dispose();
                }
            }

            return success;
        }

        private static bool PrintTargets(string projectFile, string toolsVersion, Dictionary<string, string> globalProperties, TextWriter targetsWriter, ProjectCollection projectCollection)
        {
            try
            {
                Project project = projectCollection.LoadProject(projectFile, globalProperties, toolsVersion);

                foreach (string target in project.Targets.Keys)
                {
                    targetsWriter.WriteLine(target);
                }

                projectCollection.UnloadProject(project);
                return true;
            }
            catch (Exception ex)
            {
                var message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("TargetsCouldNotBePrinted", ex);
                Console.Error.WriteLine(message);
                return false;
            }
        }

        private static List<BuildManager.DeferredBuildMessage> GetMessagesToLogInBuildLoggers(string commandLineString)
        {
            List<BuildManager.DeferredBuildMessage> messages = new(s_globalMessagesToLogInBuildLoggers)
            {
                new BuildManager.DeferredBuildMessage(
                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "Process",
                        EnvironmentUtilities.ProcessPath ?? string.Empty),
                    MessageImportance.Low),
                new BuildManager.DeferredBuildMessage(
                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "MSBExePath",
                        BuildEnvironmentHelper.Instance.CurrentMSBuildExePath),
                    MessageImportance.Low),
                new BuildManager.DeferredBuildMessage(
                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "CommandLine",
                        commandLineString),
                    MessageImportance.Low),
                new BuildManager.DeferredBuildMessage(
                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "CurrentDirectory",
                        Environment.CurrentDirectory),
                    MessageImportance.Low),
                new BuildManager.DeferredBuildMessage(
                    ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "MSBVersion",
                        ProjectCollection.DisplayVersion),
                    MessageImportance.Low),
            };

            NativeMethodsShared.LongPathsStatus longPaths = NativeMethodsShared.IsLongPathsEnabled();
            if (longPaths != NativeMethodsShared.LongPathsStatus.NotApplicable)
            {
                messages.Add(
                    new BuildManager.DeferredBuildMessage(
                        ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                            "LongPaths",
                            ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                "LongPaths_" + longPaths.ToString())),
                        MessageImportance.Low));
            }

            NativeMethodsShared.SAC_State SAC_State = NativeMethodsShared.GetSACState();
            if (SAC_State != NativeMethodsShared.SAC_State.NotApplicable && SAC_State != NativeMethodsShared.SAC_State.Missing)
            {
                messages.Add(
                    new BuildManager.DeferredBuildMessage(
                        ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                            "SAC",
                            ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                "SAC_" + SAC_State.ToString())),
                        MessageImportance.Low));
            }

            if (Traits.Instance.DebugEngine)
            {
                messages.Add(
                    new BuildManager.DeferredBuildMessage(
                        ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                        "MSBuildDebugPath",
                        DebugUtils.DebugPath),
                        MessageImportance.High));
            }

            return messages;
        }

        private static BuildResult ExecuteBuild(BuildManager buildManager, BuildRequestData request)
        {
            BuildSubmission submission;
            lock (s_buildLock)
            {
                submission = buildManager.PendBuildRequest(request);
                s_hasBuildStarted = true;

                // Even if Ctrl-C was already hit, we still pend the build request and then cancel.
                // That's so the build does not appear to have completed successfully.
                if (s_buildCancellationSource.IsCancellationRequested)
                {
                    buildManager.CancelAllSubmissions();
                }
            }

            return submission.Execute();
        }

        private static GraphBuildResult ExecuteGraphBuild(BuildManager buildManager, GraphBuildRequestData request)
        {
            GraphBuildSubmission submission;
            lock (s_buildLock)
            {
                submission = buildManager.PendBuildRequest(request);
                s_hasBuildStarted = true;

                // Even if Ctrl-C was already hit, we still pend the build request and then cancel.
                // That's so the build does not appear to have completed successfully.
                if (s_buildCancellationSource.IsCancellationRequested)
                {
                    buildManager.CancelAllSubmissions();
                }
            }

            return submission.Execute();
        }

        private static BuildResult ExecuteRestore(string projectFile, string toolsVersion, BuildManager buildManager, Dictionary<string, string> globalProperties, bool saveProjectResult = false)
        {
            // Make a copy of the global properties
            Dictionary<string, string> restoreGlobalProperties = new Dictionary<string, string>(globalProperties);

            // Add/set a property with a random value to ensure that restore happens under a different evaluation context
            // If the evaluation context is not different, then projects won't be re-evaluated after restore
            // The initializer syntax can't be used just in case a user set this property to a value
            restoreGlobalProperties[MSBuildConstants.MSBuildRestoreSessionId] = Guid.NewGuid().ToString("D");

            // Add a property to indicate that a Restore is executing
            restoreGlobalProperties[MSBuildConstants.MSBuildIsRestoring] = bool.TrueString;

            // Create a new request with a Restore target only and specify:
            //  - BuildRequestDataFlags.ClearCachesAfterBuild to ensure the projects will be reloaded from disk for subsequent builds
            //  - BuildRequestDataFlags.SkipNonexistentTargets to ignore missing targets since Restore does not require that all targets exist
            //  - BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports to ignore imports that don't exist, are empty, or are invalid because restore might
            //     make available an import that doesn't exist yet and the <Import /> might be missing a condition.
            //  - BuildRequestDataFlags.FailOnUnresolvedSdk to still fail in the case when an MSBuild project SDK can't be resolved since this is fatal and should
            //     fail the build.
            BuildRequestDataFlags flags = BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports | BuildRequestDataFlags.FailOnUnresolvedSdk;
            if (saveProjectResult)
            {
                flags |= BuildRequestDataFlags.ProvideProjectStateAfterBuild;
            }

            BuildRequestData restoreRequest = new BuildRequestData(
                projectFile,
                restoreGlobalProperties,
                toolsVersion,
                targetsToBuild: new[] { MSBuildConstants.RestoreTargetName },
                hostServices: null,
                flags: flags);

            return ExecuteBuild(buildManager, restoreRequest);
        }


        /// <summary>
        /// Verifies that the code is running on a supported operating system.
        /// </summary>
        private static void VerifyThrowSupportedOS()
        {
            if (NativeMethodsShared.IsWindows &&
                (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                 Environment.OSVersion.Version.Major < 6 ||
                 (Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor < 1))) // Windows 7 is minimum
            {
                // If we're running on any of the unsupported OS's, fail immediately.  This way,
                // we don't run into some obscure error down the line, totally confusing the user.
                InitializationException.Throw("UnsupportedOS", null, null, false);
            }
        }

        /// <summary>
        /// MSBuild.exe need to fallback to English if it cannot print Japanese (or other language) characters
        /// </summary>
        internal static void SetConsoleUI()
        {
            Thread thisThread = Thread.CurrentThread;

            // Eliminate the complex script cultures from the language selection.
            var desiredCulture = EncodingUtilities.GetExternalOverriddenUILanguageIfSupportableWithEncoding() ?? CultureInfo.CurrentUICulture.GetConsoleFallbackUICulture();
            thisThread.CurrentUICulture = desiredCulture;

            // For full framework, both the above and below must be set. This is not true in core, but it is a no op in core.
            // https://learn.microsoft.com/dotnet/api/system.globalization.cultureinfo.defaultthreadcurrentculture#remarks
            CultureInfo.CurrentUICulture = desiredCulture;
            CultureInfo.DefaultThreadCurrentUICulture = desiredCulture;

#if RUNTIME_TYPE_NETCORE
            if (EncodingUtilities.CurrentPlatformIsWindowsAndOfficiallySupportsUTF8Encoding())
#else
            if (EncodingUtilities.CurrentPlatformIsWindowsAndOfficiallySupportsUTF8Encoding()
                && !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.InvariantCultureIgnoreCase))
#endif
            {
                try
                {
                    // Setting both encodings causes a change in the CHCP, making it so we don't need to P-Invoke CHCP ourselves.
                    Console.OutputEncoding = Encoding.UTF8;
                    // If the InputEncoding is not set, the encoding will work in CMD but not in PowerShell, as the raw CHCP page won't be changed.
                    Console.InputEncoding = Encoding.UTF8;
                }
                catch (Exception ex) when (ex is IOException || ex is SecurityException)
                {
                    // The encoding is unavailable. Do nothing.
                }
            }

            // Determine if the language can be displayed in the current console codepage, otherwise set to US English
            int codepage;

            try
            {
                codepage = Console.OutputEncoding.CodePage;
            }
            catch (NotSupportedException)
            {
                // Failed to get code page: some customers have hit this and we don't know why
                thisThread.CurrentUICulture = new CultureInfo("en-US");
                return;
            }

            if (
                    codepage != 65001 // 65001 is Unicode
                    &&
                    codepage != thisThread.CurrentUICulture.TextInfo.OEMCodePage
                    &&
                    codepage != thisThread.CurrentUICulture.TextInfo.ANSICodePage
                    && !Equals(CultureInfo.InvariantCulture, thisThread.CurrentUICulture))
            {
                thisThread.CurrentUICulture = new CultureInfo("en-US");
                return;
            }
#if RUNTIME_TYPE_NETCORE
            // https://github.com/dotnet/roslyn/issues/10785#issuecomment-238940601
            // by default, .NET Core doesn't have all code pages needed for Console apps.
            // see the .NET Core Notes in https://msdn.microsoft.com/en-us/library/system.diagnostics.process(v=vs.110).aspx
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        }

        /// <summary>
        /// Gets all specified switches, from the command line, as well as all
        /// response files, including the auto-response file.
        /// </summary>
        /// <param name="commandLine"></param>
        /// <param name="switchesFromAutoResponseFile"></param>
        /// <param name="switchesNotFromAutoResponseFile"></param>
        /// <param name="fullCommandLine"></param>
        /// <returns>Combined bag of switches.</returns>
        private static void GatherAllSwitches(
#if FEATURE_GET_COMMANDLINE
            string commandLine,
#else
            string[] commandLine,
#endif
            out CommandLineSwitches switchesFromAutoResponseFile, out CommandLineSwitches switchesNotFromAutoResponseFile, out string fullCommandLine)
        {
            ResetGatheringSwitchesState();

#if FEATURE_GET_COMMANDLINE
            // split the command line on (unquoted) whitespace
            var commandLineArgs = QuotingUtilities.SplitUnquoted(commandLine);

            s_exeName = FileUtilities.FixFilePath(QuotingUtilities.Unquote(commandLineArgs[0]));
#else
            var commandLineArgs = new List<string>(commandLine);

            s_exeName = BuildEnvironmentHelper.Instance.CurrentMSBuildExePath;
#endif

#if USE_MSBUILD_DLL_EXTN
            var msbuildExtn = ".dll";
#else
            var msbuildExtn = ".exe";
#endif
            if (!s_exeName.EndsWith(msbuildExtn, StringComparison.OrdinalIgnoreCase))
            {
                s_exeName += msbuildExtn;
            }

            // discard the first piece, because that's the path to the executable -- the rest are args
            commandLineArgs.RemoveAt(0);

#if FEATURE_GET_COMMANDLINE
            fullCommandLine = $"'{commandLine}'";
#else
            fullCommandLine = $"'{string.Join(' ', commandLine)}'";
#endif

            // parse the command line, and flag syntax errors and obvious switch errors
            switchesNotFromAutoResponseFile = new CommandLineSwitches();
            GatherCommandLineSwitches(commandLineArgs, switchesNotFromAutoResponseFile, fullCommandLine);

            // parse the auto-response file (if "/noautoresponse" is not specified), and combine those switches with the
            // switches on the command line
            switchesFromAutoResponseFile = new CommandLineSwitches();
            if (!switchesNotFromAutoResponseFile[CommandLineSwitches.ParameterlessSwitch.NoAutoResponse])
            {
                GatherAutoResponseFileSwitches(s_exePath, switchesFromAutoResponseFile, fullCommandLine);
            }
        }

        /// <summary>
        /// Coordinates the parsing of the command line. It detects switches on the command line, gathers their parameters, and
        /// flags syntax errors, and other obvious switch errors.
        /// </summary>
        /// <remarks>
        /// Internal for unit testing only.
        /// </remarks>
        internal static void GatherCommandLineSwitches(List<string> commandLineArgs, CommandLineSwitches commandLineSwitches, string commandLine = "")
        {
            foreach (string commandLineArg in commandLineArgs)
            {
                string unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out var doubleQuotesRemovedFromArg);

                if (unquotedCommandLineArg.Length > 0)
                {
                    // response file switch starts with @
                    if (unquotedCommandLineArg.StartsWith("@", StringComparison.Ordinal))
                    {
                        GatherResponseFileSwitch(unquotedCommandLineArg, commandLineSwitches, commandLine);
                    }
                    else
                    {
                        string switchName;
                        string switchParameters;

                        // all switches should start with - or / or -- unless a project is being specified
                        if (!ValidateSwitchIndicatorInUnquotedArgument(unquotedCommandLineArg) || FileUtilities.LooksLikeUnixFilePath(unquotedCommandLineArg))
                        {
                            switchName = null;
                            // add a (fake) parameter indicator for later parsing
                            switchParameters = $":{commandLineArg}";
                        }
                        else
                        {
                            // check if switch has parameters (look for the : parameter indicator)
                            int switchParameterIndicator = unquotedCommandLineArg.IndexOf(':');

                            // get the length of the beginning sequence considered as a switch indicator (- or / or --)
                            int switchIndicatorsLength = GetLengthOfSwitchIndicator(unquotedCommandLineArg);

                            // extract the switch name and parameters -- the name is sandwiched between the switch indicator (the
                            // leading - or / or --) and the parameter indicator (if the switch has parameters); the parameters (if any)
                            // follow the parameter indicator
                            if (switchParameterIndicator == -1)
                            {
                                switchName = unquotedCommandLineArg.Substring(switchIndicatorsLength);
                                switchParameters = string.Empty;
                            }
                            else
                            {
                                switchName = unquotedCommandLineArg.Substring(switchIndicatorsLength, switchParameterIndicator - switchIndicatorsLength);
                                switchParameters = ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, switchName, switchParameterIndicator, switchIndicatorsLength);
                            }
                        }

                        // Special case: for the switches "/m" (or "/maxCpuCount") and "/bl" (or "/binarylogger") we wish to pretend we saw a default argument
                        // This allows a subsequent /m:n on the command line to override it.
                        // We could create a new kind of switch with optional parameters, but it's a great deal of churn for this single case.
                        // Note that if no "/m" or "/maxCpuCount" switch -- either with or without parameters -- is present, then we still default to 1 cpu
                        // for backwards compatibility.
                        if (string.IsNullOrEmpty(switchParameters))
                        {
                            if (string.Equals(switchName, "m", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(switchName, "maxcpucount", StringComparison.OrdinalIgnoreCase))
                            {
                                int numberOfCpus = NativeMethodsShared.GetLogicalCoreCount();
                                switchParameters = $":{numberOfCpus}";
                            }
                            else if (string.Equals(switchName, "bl", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(switchName, "binarylogger", StringComparison.OrdinalIgnoreCase))
                            {
                                // we have to specify at least one parameter otherwise it's impossible to distinguish the situation
                                // where /bl is not specified at all vs. where /bl is specified without the file name.
                                switchParameters = ":msbuild.binlog";
                            }
                            else if (string.Equals(switchName, "prof", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(switchName, "profileevaluation", StringComparison.OrdinalIgnoreCase))
                            {
                                switchParameters = ":no-file";
                            }
                        }

                        if (CommandLineSwitches.IsParameterlessSwitch(switchName, out var parameterlessSwitch, out var duplicateSwitchErrorMessage))
                        {
                            GatherParameterlessCommandLineSwitch(commandLineSwitches, parameterlessSwitch, switchParameters, duplicateSwitchErrorMessage, unquotedCommandLineArg, commandLine);
                        }
                        else if (CommandLineSwitches.IsParameterizedSwitch(switchName, out var parameterizedSwitch, out duplicateSwitchErrorMessage, out var multipleParametersAllowed, out var missingParametersErrorMessage, out var unquoteParameters, out var allowEmptyParameters))
                        {
                            GatherParameterizedCommandLineSwitch(commandLineSwitches, parameterizedSwitch, switchParameters, duplicateSwitchErrorMessage, multipleParametersAllowed, missingParametersErrorMessage, unquoteParameters, unquotedCommandLineArg, allowEmptyParameters, commandLine);
                        }
                        else
                        {
                            commandLineSwitches.SetUnknownSwitchError(unquotedCommandLineArg, commandLine);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Extracts a switch's parameters after processing all quoting around the switch.
        /// </summary>
        /// <remarks>
        /// This method is marked "internal" for unit-testing purposes only -- ideally it should be "private".
        /// </remarks>
        /// <param name="commandLineArg"></param>
        /// <param name="unquotedCommandLineArg"></param>
        /// <param name="doubleQuotesRemovedFromArg"></param>
        /// <param name="switchName"></param>
        /// <param name="switchParameterIndicator"></param>
        /// <param name="switchIndicatorsLength"></param>
        /// <returns>The given switch's parameters (with interesting quoting preserved).</returns>
        internal static string ExtractSwitchParameters(
            string commandLineArg,
            string unquotedCommandLineArg,
            int doubleQuotesRemovedFromArg,
            string switchName,
            int switchParameterIndicator,
            int switchIndicatorsLength)
        {

            // find the parameter indicator again using the quoted arg
            // NOTE: since the parameter indicator cannot be part of a switch name, quoting around it is not relevant, because a
            // parameter indicator cannot be escaped or made into a literal
            int quotedSwitchParameterIndicator = commandLineArg.IndexOf(':');

            // check if there is any quoting in the name portion of the switch
            string unquotedSwitchIndicatorAndName = QuotingUtilities.Unquote(commandLineArg.Substring(0, quotedSwitchParameterIndicator), out var doubleQuotesRemovedFromSwitchIndicatorAndName);

            ErrorUtilities.VerifyThrow(switchName == unquotedSwitchIndicatorAndName.Substring(switchIndicatorsLength),
                "The switch name extracted from either the partially or completely unquoted arg should be the same.");

            ErrorUtilities.VerifyThrow(doubleQuotesRemovedFromArg >= doubleQuotesRemovedFromSwitchIndicatorAndName,
                "The name portion of the switch cannot contain more quoting than the arg itself.");

            string switchParameters;
            // if quoting in the name portion of the switch was terminated
            if ((doubleQuotesRemovedFromSwitchIndicatorAndName % 2) == 0)
            {
                // get the parameters exactly as specified on the command line i.e. including quoting
                switchParameters = commandLineArg.Substring(quotedSwitchParameterIndicator);
            }
            else
            {
                // if quoting was not terminated in the name portion of the switch, and the terminal double-quote (if any)
                // terminates the switch parameters
                int terminalDoubleQuote = commandLineArg.IndexOf('"', quotedSwitchParameterIndicator + 1);
                if (((doubleQuotesRemovedFromArg - doubleQuotesRemovedFromSwitchIndicatorAndName) <= 1) &&
                    ((terminalDoubleQuote == -1) || (terminalDoubleQuote == (commandLineArg.Length - 1))))
                {
                    // then the parameters are not quoted in any interesting way, so use the unquoted parameters
                    switchParameters = unquotedCommandLineArg.Substring(switchParameterIndicator);
                }
                else
                {
                    // otherwise, use the quoted parameters, after compensating for the quoting that was started in the name
                    // portion of the switch
                    switchParameters = $":\"{commandLineArg.Substring(quotedSwitchParameterIndicator + 1)}";
                }
            }

            ErrorUtilities.VerifyThrow(switchParameters != null, "We must be able to extract the switch parameters.");

            return switchParameters;
        }

        /// <summary>
        /// Used to keep track of response files to prevent them from
        /// being included multiple times (or even recursively).
        /// </summary>
        private static List<string> s_includedResponseFiles;

        /// <summary>
        /// Called when a response file switch is detected on the command line. It loads the specified response file, and parses
        /// each line in it like a command line. It also prevents multiple (or recursive) inclusions of the same response file.
        /// </summary>
        /// <param name="unquotedCommandLineArg"></param>
        /// <param name="commandLineSwitches"></param>
        private static void GatherResponseFileSwitch(string unquotedCommandLineArg, CommandLineSwitches commandLineSwitches, string commandLine)
        {
            try
            {
                string responseFile = FileUtilities.FixFilePath(unquotedCommandLineArg.Substring(1));

                if (responseFile.Length == 0)
                {
                    commandLineSwitches.SetSwitchError("MissingResponseFileError", unquotedCommandLineArg, commandLine);
                }
                else if (!FileSystems.Default.FileExists(responseFile))
                {
                    commandLineSwitches.SetParameterError("ResponseFileNotFoundError", unquotedCommandLineArg, commandLine);
                }
                else
                {
                    // normalize the response file path to help catch multiple (or recursive) inclusions
                    responseFile = Path.GetFullPath(responseFile);
                    // NOTE: for network paths or mapped paths, normalization is not guaranteed to work

                    bool isRepeatedResponseFile = false;

                    foreach (string includedResponseFile in s_includedResponseFiles)
                    {
                        if (string.Equals(responseFile, includedResponseFile, StringComparison.OrdinalIgnoreCase))
                        {
                            commandLineSwitches.SetParameterError("RepeatedResponseFileError", unquotedCommandLineArg, commandLine);
                            isRepeatedResponseFile = true;
                            break;
                        }
                    }

                    if (!isRepeatedResponseFile)
                    {
                        var responseFileDirectory = FileUtilities.EnsureTrailingSlash(Path.GetDirectoryName(responseFile));
                        s_includedResponseFiles.Add(responseFile);

                        List<string> argsFromResponseFile;

#if FEATURE_ENCODING_DEFAULT
                        using (StreamReader responseFileContents = new StreamReader(responseFile, Encoding.Default)) // HIGHCHAR: If response files have no byte-order marks, then assume ANSI rather than ASCII.
#else
                        using (StreamReader responseFileContents = FileUtilities.OpenRead(responseFile)) // HIGHCHAR: If response files have no byte-order marks, then assume ANSI rather than ASCII.
#endif
                        {
                            argsFromResponseFile = new List<string>();

                            while (responseFileContents.Peek() != -1)
                            {
                                // ignore leading whitespace on each line
                                string responseFileLine = responseFileContents.ReadLine().TrimStart();

                                // skip comment lines beginning with #
                                if (!responseFileLine.StartsWith("#", StringComparison.Ordinal))
                                {
                                    // Allow special case to support a path relative to the .rsp file being processed.
                                    responseFileLine = Regex.Replace(responseFileLine, responseFilePathReplacement,
                                        responseFileDirectory, RegexOptions.IgnoreCase);

                                    // treat each line of the response file like a command line i.e. args separated by whitespace
                                    argsFromResponseFile.AddRange(QuotingUtilities.SplitUnquoted(Environment.ExpandEnvironmentVariables(responseFileLine)));
                                }
                            }
                        }

                        CommandLineSwitches.SwitchesFromResponseFiles.Add((responseFile, string.Join(" ", argsFromResponseFile)));

                        GatherCommandLineSwitches(argsFromResponseFile, commandLineSwitches, commandLine);
                    }
                }
            }
            catch (NotSupportedException e)
            {
                commandLineSwitches.SetParameterError("ReadResponseFileError", unquotedCommandLineArg, e, commandLine);
            }
            catch (SecurityException e)
            {
                commandLineSwitches.SetParameterError("ReadResponseFileError", unquotedCommandLineArg, e, commandLine);
            }
            catch (UnauthorizedAccessException e)
            {
                commandLineSwitches.SetParameterError("ReadResponseFileError", unquotedCommandLineArg, e, commandLine);
            }
            catch (IOException e)
            {
                commandLineSwitches.SetParameterError("ReadResponseFileError", unquotedCommandLineArg, e, commandLine);
            }
        }

        /// <summary>
        /// Called when a switch that doesn't take parameters is detected on the command line.
        /// </summary>
        /// <param name="commandLineSwitches"></param>
        /// <param name="parameterlessSwitch"></param>
        /// <param name="switchParameters"></param>
        /// <param name="duplicateSwitchErrorMessage"></param>
        /// <param name="unquotedCommandLineArg"></param>
        private static void GatherParameterlessCommandLineSwitch(
            CommandLineSwitches commandLineSwitches,
            CommandLineSwitches.ParameterlessSwitch parameterlessSwitch,
            string switchParameters,
            string duplicateSwitchErrorMessage,
            string unquotedCommandLineArg,
            string commandLine)
        {
            // switch should not have any parameters
            if (switchParameters.Length == 0)
            {
                // check if switch is duplicated, and if that's allowed
                if (!commandLineSwitches.IsParameterlessSwitchSet(parameterlessSwitch) ||
                    (duplicateSwitchErrorMessage == null))
                {
                    commandLineSwitches.SetParameterlessSwitch(parameterlessSwitch, unquotedCommandLineArg);
                }
                else
                {
                    commandLineSwitches.SetSwitchError(duplicateSwitchErrorMessage, unquotedCommandLineArg, commandLine);
                }
            }
            else
            {
                commandLineSwitches.SetUnexpectedParametersError(unquotedCommandLineArg, commandLine);
            }
        }

        /// <summary>
        /// Called when a switch that takes parameters is detected on the command line. This method flags errors and stores the
        /// switch parameters.
        /// </summary>
        /// <param name="commandLineSwitches"></param>
        /// <param name="parameterizedSwitch"></param>
        /// <param name="switchParameters"></param>
        /// <param name="duplicateSwitchErrorMessage"></param>
        /// <param name="multipleParametersAllowed"></param>
        /// <param name="missingParametersErrorMessage"></param>
        /// <param name="unquoteParameters"></param>
        /// <param name="unquotedCommandLineArg"></param>
        private static void GatherParameterizedCommandLineSwitch(
            CommandLineSwitches commandLineSwitches,
            CommandLineSwitches.ParameterizedSwitch parameterizedSwitch,
            string switchParameters,
            string duplicateSwitchErrorMessage,
            bool multipleParametersAllowed,
            string missingParametersErrorMessage,
            bool unquoteParameters,
            string unquotedCommandLineArg,
            bool allowEmptyParameters,
            string commandLine)
        {
            if (// switch must have parameters
                (switchParameters.Length > 1) ||
                // unless the parameters are optional
                (missingParametersErrorMessage == null))
            {
                // skip the parameter indicator (if any)
                if (switchParameters.Length > 0)
                {
                    switchParameters = switchParameters.Substring(1);
                }

                if (parameterizedSwitch == CommandLineSwitches.ParameterizedSwitch.Project && IsEnvironmentVariable(switchParameters))
                {
                    commandLineSwitches.SetSwitchError("EnvironmentVariableAsSwitch", unquotedCommandLineArg, commandLine);
                }

                // check if switch is duplicated, and if that's allowed
                if (!commandLineSwitches.IsParameterizedSwitchSet(parameterizedSwitch) ||
                    (duplicateSwitchErrorMessage == null))
                {
                    // save the parameters after unquoting and splitting them if necessary
                    if (!commandLineSwitches.SetParameterizedSwitch(parameterizedSwitch, unquotedCommandLineArg, switchParameters, multipleParametersAllowed, unquoteParameters, allowEmptyParameters))
                    {
                        // if parsing revealed there were no real parameters, flag an error, unless the parameters are optional
                        if (missingParametersErrorMessage != null)
                        {
                            commandLineSwitches.SetSwitchError(missingParametersErrorMessage, unquotedCommandLineArg, commandLine);
                        }
                    }
                }
                else
                {
                    commandLineSwitches.SetSwitchError(duplicateSwitchErrorMessage, unquotedCommandLineArg, commandLine);
                }
            }
            else
            {
                commandLineSwitches.SetSwitchError(missingParametersErrorMessage, unquotedCommandLineArg, commandLine);
            }
        }

        /// <summary>
        /// Checks whether envVar is an environment variable. MSBuild uses
        /// Environment.ExpandEnvironmentVariables(string), which only
        /// considers %-delimited variables.
        /// </summary>
        /// <param name="envVar">A possible environment variable</param>
        /// <returns>Whether envVar is an environment variable</returns>
        private static bool IsEnvironmentVariable(string envVar)
        {
            return envVar.StartsWith("%") && envVar.EndsWith("%") && envVar.Length > 1;
        }

        /// <summary>
        /// The name of the auto-response file.
        /// </summary>
        private const string autoResponseFileName = "MSBuild.rsp";

        /// <summary>
        /// The name of an auto-response file to search for in the project directory and above.
        /// </summary>
        private const string directoryResponseFileName = "Directory.Build.rsp";

        /// <summary>
        /// String replacement pattern to support paths in response files.
        /// </summary>
        private const string responseFilePathReplacement = "%MSBuildThisFileDirectory%";

        /// <summary>
        /// Whether switches from the auto-response file are being used.
        /// </summary>
        internal static bool usingSwitchesFromAutoResponseFile = false;

        /// <summary>
        /// Indicates that this process is working as a server.
        /// </summary>
        private static bool s_isServerNode;

        /// <summary>
        /// Parses the auto-response file (assumes the "/noautoresponse" switch is not specified on the command line), and combines the
        /// switches from the auto-response file with the switches passed in.
        /// Returns true if the response file was found.
        /// </summary>
        private static bool GatherAutoResponseFileSwitches(string path, CommandLineSwitches switchesFromAutoResponseFile, string commandLine)
        {
            string autoResponseFile = Path.Combine(path, autoResponseFileName);
            return GatherAutoResponseFileSwitchesFromFullPath(autoResponseFile, switchesFromAutoResponseFile, commandLine);
        }

        private static bool GatherAutoResponseFileSwitchesFromFullPath(string autoResponseFile, CommandLineSwitches switchesFromAutoResponseFile, string commandLine)
        {
            bool found = false;

            // if the auto-response file does not exist, only use the switches on the command line
            if (FileSystems.Default.FileExists(autoResponseFile))
            {
                found = true;
                GatherResponseFileSwitch($"@{autoResponseFile}", switchesFromAutoResponseFile, commandLine);

                // if the "/noautoresponse" switch was set in the auto-response file, flag an error
                if (switchesFromAutoResponseFile[CommandLineSwitches.ParameterlessSwitch.NoAutoResponse])
                {
                    switchesFromAutoResponseFile.SetSwitchError("CannotAutoDisableAutoResponseFile",
                        switchesFromAutoResponseFile.GetParameterlessSwitchCommandLineArg(CommandLineSwitches.ParameterlessSwitch.NoAutoResponse), commandLine);
                }

                if (switchesFromAutoResponseFile.HaveAnySwitchesBeenSet())
                {
                    // we picked up some switches from the auto-response file
                    usingSwitchesFromAutoResponseFile = true;
                }

                // Throw errors found in the response file
                switchesFromAutoResponseFile.ThrowErrors();
            }

            return found;
        }

        /// <summary>
        /// Coordinates the processing of all detected switches. It gathers information necessary to invoke the build engine, and
        /// performs deeper error checking on the switches and their parameters.
        /// </summary>
        /// <returns>true, if build can be invoked</returns>
        private static bool ProcessCommandLineSwitches(
            CommandLineSwitches switchesFromAutoResponseFile,
            CommandLineSwitches switchesNotFromAutoResponseFile,
            ref string projectFile,
            ref string[] targets,
            ref string toolsVersion,
            ref Dictionary<string, string> globalProperties,
            ref ILogger[] loggers,
            ref LoggerVerbosity verbosity,
            ref LoggerVerbosity originalVerbosity,
            ref List<DistributedLoggerRecord> distributedLoggerRecords,
#if FEATURE_XML_SCHEMA_VALIDATION
            ref bool needToValidateProject,
            ref string schemaFile,
#endif
            ref int cpuCount,
            ref bool enableNodeReuse,
            ref TextWriter preprocessWriter,
            ref TextWriter targetsWriter,
            ref bool detailedSummary,
            ref ISet<string> warningsAsErrors,
            ref ISet<string> warningsNotAsErrors,
            ref ISet<string> warningsAsMessages,
            ref bool enableRestore,
            ref bool interactive,
            ref ProfilerLogger profilerLogger,
            ref bool enableProfiler,
            ref Dictionary<string, string> restoreProperties,
            ref ProjectIsolationMode isolateProjects,
            ref GraphBuildOptions graphBuild,
            ref string[] inputResultsCaches,
            ref string outputResultsCache,
#if FEATURE_REPORTFILEACCESSES
            ref bool reportFileAccesses,
#endif
            ref bool lowPriority,
            ref bool question,
            ref bool isBuildCheckEnabled,
            ref string[] getProperty,
            ref string[] getItem,
            ref string[] getTargetResult,
            ref string getResultOutputFile,
            bool recursing,
            string commandLine)
        {
            bool invokeBuild = false;

            CommandLineSwitches commandLineSwitches = CombineSwitchesRespectingPriority(switchesFromAutoResponseFile, switchesNotFromAutoResponseFile, commandLine);

#if DEBUG
            if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.WaitForDebugger])
            {
                BuildManager.WaitForDebugger = true;

                if (!Debugger.IsAttached)
                {
                    Console.WriteLine($"Waiting for debugger to attach... ({EnvironmentUtilities.ProcessPath} PID {EnvironmentUtilities.CurrentProcessId})");
                    while (!Debugger.IsAttached)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
#endif

            bool useTerminalLogger = ProcessTerminalLoggerConfiguration(commandLineSwitches, out string aggregatedTerminalLoggerParameters);

            // This is temporary until we can remove the need for the environment variable.
            // DO NOT use this environment variable for any new features as it will be removed without further notice.
            Environment.SetEnvironmentVariable("_MSBUILDTLENABLED", useTerminalLogger ? "1" : "0");

            DisplayVersionMessageIfNeeded(recursing, useTerminalLogger, commandLineSwitches);

            // Idle priority would prevent the build from proceeding as the user does normal actions.
            // This switch is processed early to capture both the command line case (main node should
            // also be low priority) and the Visual Studio case in which the main node starts and stays
            // at normal priority (not through XMake.cs) but worker nodes still need to honor this switch.
            if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.LowPriority))
            {
                lowPriority = ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.LowPriority], defaultValue: true, resourceName: "InvalidLowPriorityValue");
            }
            try
            {
                if (lowPriority)
                {
                    using Process currentProcess = Process.GetCurrentProcess();
                    if (currentProcess.PriorityClass != ProcessPriorityClass.Idle)
                    {
                        currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                }
            }
            // We avoid increasing priority because that causes failures on mac/linux, but there is no good way to
            // verify that a particular priority is lower than "BelowNormal." If the error appears, ignore it and
            // leave priority where it was.
            catch (Win32Exception) { }

#if FEATURE_REPORTFILEACCESSES
            if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.ReportFileAccesses))
            {
                reportFileAccesses = ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.ReportFileAccesses], defaultValue: true, resourceName: "");
            }
#endif

            // if help switch is set (regardless of switch errors), show the help message and ignore the other switches
            if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Help])
            {
                ShowHelpMessage();
            }
            else if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.NodeMode))
            {
                StartLocalNode(commandLineSwitches, lowPriority);
            }
            else
            {
                // if help switch is not set, and errors were found, abort (don't process the remaining switches)
                commandLineSwitches.ThrowErrors();

                // if version switch is set, just show the version and quit (ignore the other switches)
                if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Version])
                {
                    ShowVersion();
                }
                // if featureavailability switch is set, just show the feature availability and quit (ignore the other switches)
                else if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.FeatureAvailability))
                {
                    ShowFeatureAvailability(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.FeatureAvailability]);
                }
                else
                {
                    bool foundProjectAutoResponseFile = CheckAndGatherProjectAutoResponseFile(switchesFromAutoResponseFile, commandLineSwitches, recursing, commandLine);

                    if (foundProjectAutoResponseFile)
                    {
                        // we presumably read in more switches, so start our switch processing all over again,
                        // so that we consume switches in the following sequence of increasing priority:
                        // (1) switches from the msbuild.rsp next to msbuild.exe, including recursively included response files
                        // (2) switches from this msbuild.rsp next to the project or solution <<--------- these we have just now merged with (1)
                        // (3) switches from the command line, including recursively included response file switches inserted at the point they are declared with their "@" symbol
                        return ProcessCommandLineSwitches(
                                                           switchesFromAutoResponseFile,
                                                           switchesNotFromAutoResponseFile,
                                                           ref projectFile,
                                                           ref targets,
                                                           ref toolsVersion,
                                                           ref globalProperties,
                                                           ref loggers,
                                                           ref verbosity,
                                                           ref originalVerbosity,
                                                           ref distributedLoggerRecords,
#if FEATURE_XML_SCHEMA_VALIDATION
                                                           ref needToValidateProject,
                                                           ref schemaFile,
#endif
                                                           ref cpuCount,
                                                           ref enableNodeReuse,
                                                           ref preprocessWriter,
                                                           ref targetsWriter,
                                                           ref detailedSummary,
                                                           ref warningsAsErrors,
                                                           ref warningsNotAsErrors,
                                                           ref warningsAsMessages,
                                                           ref enableRestore,
                                                           ref interactive,
                                                           ref profilerLogger,
                                                           ref enableProfiler,
                                                           ref restoreProperties,
                                                           ref isolateProjects,
                                                           ref graphBuild,
                                                           ref inputResultsCaches,
                                                           ref outputResultsCache,
#if FEATURE_REPORTFILEACCESSES
                                                           ref reportFileAccesses,
#endif
                                                           ref lowPriority,
                                                           ref question,
                                                           ref isBuildCheckEnabled,
                                                           ref getProperty,
                                                           ref getItem,
                                                           ref getTargetResult,
                                                           ref getResultOutputFile,
                                                           recursing: true,
                                                           commandLine);
                    }

                    projectFile = ProcessProjectSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Project], commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.IgnoreProjectExtensions], Directory.GetFiles);

                    // figure out which targets we are building
                    targets = ProcessTargetSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Target]);

                    // If we are looking for the value of a specific property or item post-evaluation or a target post-build, figure that out now
                    getProperty = commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.GetProperty] ?? Array.Empty<string>();
                    getItem = commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.GetItem] ?? Array.Empty<string>();
                    getTargetResult = commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.GetTargetResult] ?? Array.Empty<string>();
                    getResultOutputFile = commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.GetResultOutputFile].FirstOrDefault() ?? string.Empty;

                    bool minimizeStdOutOutput = getProperty.Length + getItem.Length + getTargetResult.Length > 0 && getResultOutputFile.Length == 0;
                    if (minimizeStdOutOutput)
                    {
                        commandLineSwitches.SetParameterizedSwitch(CommandLineSwitches.ParameterizedSwitch.Verbosity, "q", "q", true, true, true);
                    }

                    targets = targets.Union(getTargetResult, MSBuildNameIgnoreCaseComparer.Default).ToArray();

                    // figure out which ToolsVersion has been set on the command line
                    toolsVersion = ProcessToolsVersionSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.ToolsVersion]);

                    // figure out which properties have been set on the command line
                    globalProperties = ProcessPropertySwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Property]);

                    // figure out which restore-only properties have been set on the command line
                    restoreProperties = ProcessPropertySwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.RestoreProperty]);

                    // figure out if there was a max cpu count provided
                    cpuCount = ProcessMaxCPUCountSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount]);

                    // figure out if we should reuse nodes
                    // If FEATURE_NODE_REUSE is OFF, just validates that the switch is OK, and always returns False
                    enableNodeReuse = ProcessNodeReuseSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.NodeReuse]);

                    // determine what if any writer to preprocess to
                    preprocessWriter = null;
                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Preprocess))
                    {
                        preprocessWriter = ProcessPreprocessSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Preprocess]);
                    }

                    // determine what if any writer to print targets to
                    targetsWriter = null;
                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Targets))
                    {
                        targetsWriter = ProcessTargetsSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Targets]);
                    }

                    warningsAsErrors = ProcessWarnAsErrorSwitch(commandLineSwitches);

                    warningsNotAsErrors = ProcessWarnNotAsErrorSwitch(commandLineSwitches);

                    warningsAsMessages = ProcessWarnAsMessageSwitch(commandLineSwitches);

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Restore))
                    {
                        enableRestore = ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Restore], defaultValue: true, resourceName: "InvalidRestoreValue");
                    }

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Interactive))
                    {
                        interactive = ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Interactive], defaultValue: true, resourceName: "InvalidInteractiveValue");
                    }

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.IsolateProjects))
                    {
                        isolateProjects = ProcessIsolateProjectsSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.IsolateProjects]);
                    }

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.GraphBuild))
                    {
                        graphBuild = ProcessGraphBuildSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.GraphBuild]);
                    }

                    question = commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Question);

                    isBuildCheckEnabled = IsBuildCheckEnabled(commandLineSwitches);

                    inputResultsCaches = ProcessInputResultsCaches(commandLineSwitches);

                    outputResultsCache = ProcessOutputResultsCache(commandLineSwitches);

                    // figure out which loggers are going to listen to build events
                    string[][] groupedFileLoggerParameters = commandLineSwitches.GetFileLoggerParameters();

                    loggers = ProcessLoggingSwitches(
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Logger],
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.DistributedLogger],
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Verbosity],
                        commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.NoConsoleLogger],
                        commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.DistributedFileLogger],
                        useTerminalLogger,
                        aggregatedTerminalLoggerParameters,
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.FileLoggerParameters], // used by DistributedFileLogger
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.ConsoleLoggerParameters],
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.BinaryLogger],
                        commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.ProfileEvaluation],
                        groupedFileLoggerParameters,
                        minimizeStdOutOutput,
                        out distributedLoggerRecords,
                        out verbosity,
                        out originalVerbosity,
                        cpuCount,
                        out profilerLogger,
                        out enableProfiler);

                    // We're finished with defining individual loggers' verbosity at this point, so we don't need to worry about messing them up.
                    if (Traits.Instance.DebugEngine)
                    {
                        verbosity = LoggerVerbosity.Diagnostic;
                    }

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.DetailedSummary))
                    {
                        detailedSummary = ProcessBooleanSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.DetailedSummary], defaultValue: true, resourceName: "InvalidDetailedSummaryValue");
                    }
                    else if (verbosity == LoggerVerbosity.Diagnostic)
                    {
                        detailedSummary = true;
                    }

                    if (originalVerbosity == LoggerVerbosity.Diagnostic)
                    {
                        string equivalentCommandLine = commandLineSwitches.GetEquivalentCommandLineExceptProjectFile();
                        Console.WriteLine($"{Path.Combine(s_exePath, s_exeName)} {equivalentCommandLine} {projectFile}");
                    }

#if FEATURE_XML_SCHEMA_VALIDATION
                    // figure out if the project needs to be validated against a schema
                    needToValidateProject = commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Validate);
                    schemaFile = ProcessValidateSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Validate]);
#endif
                    invokeBuild = true;

                    if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.WarningsNotAsErrors) &&
                        !WarningsAsErrorsSwitchIsEmpty(commandLineSwitches)!)
                    {
                        commandLineSwitches.SetSwitchError("NotWarnAsErrorWithoutWarnAsError",
                        commandLineSwitches.GetParameterizedSwitchCommandLineArg(CommandLineSwitches.ParameterizedSwitch.WarningsNotAsErrors),
                        commandLine);
                        commandLineSwitches.ThrowErrors();
                    }
                }
            }

            ErrorUtilities.VerifyThrow(!invokeBuild || !string.IsNullOrEmpty(projectFile), "We should have a project file if we're going to build.");

            return invokeBuild;
        }

        private static bool IsBuildCheckEnabled(CommandLineSwitches commandLineSwitches)
        {
            // Opt-in behavior to be determined by: https://github.com/dotnet/msbuild/issues/9723
            bool isBuildCheckEnabled = commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Check);
            return isBuildCheckEnabled;
        }

        private static bool ProcessTerminalLoggerConfiguration(CommandLineSwitches commandLineSwitches, out string aggregatedParameters)
        {
            aggregatedParameters = AggregateParameters(commandLineSwitches);
            string defaultValue = FindDefaultValue(aggregatedParameters);

            string terminalLoggerArg = null;
            if (!TryFromCommandLine(commandLineSwitches) && !TryFromEnvironmentVariables())
            {
                ApplyDefault();
            }

            terminalLoggerArg = NormalizeIntoBooleanValues();

            bool useTerminalLogger = false;
            if (!TrueOrFalse())
            {
                ItMustBeAuto();
            }

            return KnownTelemetry.LoggingConfigurationTelemetry.TerminalLogger = useTerminalLogger;

            static bool CheckIfTerminalIsSupportedAndTryEnableAnsiColorCodes()
            {
                (var acceptAnsiColorCodes, var outputIsScreen, s_originalConsoleMode) = NativeMethodsShared.QueryIsScreenAndTryEnableAnsiColorCodes();

                if (!outputIsScreen)
                {
                    s_globalMessagesToLogInBuildLoggers.Add(
                        new BuildManager.DeferredBuildMessage(ResourceUtilities.GetResourceString("TerminalLoggerNotUsedRedirected"), MessageImportance.Low));
                    return false;
                }

                // TerminalLogger is not used if the terminal does not support ANSI/VT100 escape sequences.
                if (!acceptAnsiColorCodes)
                {
                    s_globalMessagesToLogInBuildLoggers.Add(
                        new BuildManager.DeferredBuildMessage(ResourceUtilities.GetResourceString("TerminalLoggerNotUsedNotSupported"), MessageImportance.Low));
                    return false;
                }

                if (Traits.Instance.EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout)
                {
                    s_globalMessagesToLogInBuildLoggers.Add(
                        new BuildManager.DeferredBuildMessage(ResourceUtilities.GetResourceString("TerminalLoggerNotUsedDisabled"), MessageImportance.Low));
                    return false;
                }

                return true;
            }

            string FindDefaultValue(string s)
            {
                // Find default configuration so it is part of telemetry even when default is not used.
                // Default can be stored in /tlp:default=true|false|on|off|auto
                string terminalLoggerDefault = null;
                foreach (string parameter in s.Split(MSBuildConstants.SemicolonChar))
                {
                    if (string.IsNullOrWhiteSpace(parameter))
                    {
                        continue;
                    }

                    string[] parameterAndValue = parameter.Split(MSBuildConstants.EqualsChar);
                    if (parameterAndValue[0].Equals("default", StringComparison.InvariantCultureIgnoreCase) && parameterAndValue.Length > 1)
                    {
                        terminalLoggerDefault = parameterAndValue[1];
                    }
                }

                if (terminalLoggerDefault == null)
                {
                    terminalLoggerDefault = bool.FalseString;
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerDefault = bool.FalseString;
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerDefaultSource = "msbuild";
                }
                else
                {
                    // Lets check DOTNET CLI env var
                    string dotnetCliEnvVar = Environment.GetEnvironmentVariable("DOTNET_CLI_CONFIGURE_MSBUILD_TERMINAL_LOGGER");
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerDefault = terminalLoggerDefault;
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerDefaultSource = string.IsNullOrWhiteSpace(dotnetCliEnvVar) ? "sdk" : "DOTNET_CLI_CONFIGURE_MSBUILD_TERMINAL_LOGGER";
                }

                return terminalLoggerDefault;
            }

            bool TryFromCommandLine(CommandLineSwitches commandLineSwitches1)
            {
                if (!commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.TerminalLogger))
                {
                    return false;
                }

                // There's a switch set, but there might be more than one
                string[] switches = commandLineSwitches1[CommandLineSwitches.ParameterizedSwitch.TerminalLogger];

                terminalLoggerArg = switches[switches.Length - 1];

                // if the switch was set but not to an explicit value, the value is "auto"
                if (string.IsNullOrEmpty(terminalLoggerArg))
                {
                    terminalLoggerArg = "auto";
                }

                KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntent = terminalLoggerArg ?? string.Empty;
                KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntentSource = "arg";

                return true;
            }

            bool TryFromEnvironmentVariables()
            {
                // Keep MSBUILDLIVELOGGER supporitng existing use. But MSBUILDTERMINALLOGGER takes precedence.
                string liveLoggerArg = Environment.GetEnvironmentVariable("MSBUILDLIVELOGGER");
                terminalLoggerArg = Environment.GetEnvironmentVariable("MSBUILDTERMINALLOGGER");
                if (!string.IsNullOrEmpty(terminalLoggerArg))
                {
                    s_globalMessagesToLogInBuildLoggers.Add(
                        new BuildManager.DeferredBuildMessage($"The environment variable MSBUILDTERMINALLOGGER was set to {terminalLoggerArg}.", MessageImportance.Low));

                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntent = terminalLoggerArg;
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntentSource = "MSBUILDTERMINALLOGGER";
                }
                else if (!string.IsNullOrEmpty(liveLoggerArg))
                {
                    terminalLoggerArg = liveLoggerArg;
                    s_globalMessagesToLogInBuildLoggers.Add(
                        new BuildManager.DeferredBuildMessage($"The environment variable MSBUILDLIVELOGGER was set to {liveLoggerArg}.", MessageImportance.Low));

                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntent = terminalLoggerArg;
                    KnownTelemetry.LoggingConfigurationTelemetry.TerminalLoggerUserIntentSource = "MSBUILDLIVELOGGER";
                }
                else
                {
                    return false;
                }

                return true;
            }

            string NormalizeIntoBooleanValues()
            {
                // We now have a string`. It can be "true" or "false" which means just that:
                if (terminalLoggerArg.Equals("on", StringComparison.InvariantCultureIgnoreCase))
                {
                    terminalLoggerArg = bool.TrueString;
                }
                else if (terminalLoggerArg.Equals("off", StringComparison.InvariantCultureIgnoreCase))
                {
                    terminalLoggerArg = bool.FalseString;
                }

                return terminalLoggerArg;
            }

            void ApplyDefault()
            {
                terminalLoggerArg = defaultValue;
            }

            string AggregateParameters(CommandLineSwitches switches)
            {
                string[] terminalLoggerParameters = switches[CommandLineSwitches.ParameterizedSwitch.TerminalLoggerParameters];
                return terminalLoggerParameters?.Length > 0 ? MSBuildApp.AggregateParameters(string.Empty, terminalLoggerParameters) : string.Empty;
            }

            bool TrueOrFalse()
            {
                if (bool.TryParse(terminalLoggerArg, out bool result))
                {
                    useTerminalLogger = result;

                    // Try Enable Ansi Color Codes when terminal logger is enabled/enforced.
                    if (result)
                    {
                        // This needs to be called so Ansi Color Codes are enabled for the terminal logger.
                        (_, _, s_originalConsoleMode) = NativeMethodsShared.QueryIsScreenAndTryEnableAnsiColorCodes();
                    }

                    return true;
                }

                return false;
            }

            void ItMustBeAuto()
            {
                // or it can be "auto", meaning "enable if we can"
                if (!terminalLoggerArg.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    CommandLineSwitchException.Throw("InvalidTerminalLoggerValue", terminalLoggerArg);
                }

                useTerminalLogger = CheckIfTerminalIsSupportedAndTryEnableAnsiColorCodes();
            }
        }

        private static CommandLineSwitches CombineSwitchesRespectingPriority(CommandLineSwitches switchesFromAutoResponseFile, CommandLineSwitches switchesNotFromAutoResponseFile, string commandLine)
        {
            // combine the auto-response file switches with the command line switches in a left-to-right manner, where the
            // auto-response file switches are on the left (default options), and the command line switches are on the
            // right (overriding options) so that we consume switches in the following sequence of increasing priority:
            // (1) switches from the msbuild.rsp file/s, including recursively included response files
            // (2) switches from the command line, including recursively included response file switches inserted at the point they are declared with their "@" symbol
            CommandLineSwitches commandLineSwitches = new CommandLineSwitches();
            commandLineSwitches.Append(switchesFromAutoResponseFile, commandLine); // lowest precedence
            commandLineSwitches.Append(switchesNotFromAutoResponseFile, commandLine);
            return commandLineSwitches;
        }

        private static string GetProjectDirectory(string[] projectSwitchParameters)
        {
            string projectDirectory = ".";
            ErrorUtilities.VerifyThrow(projectSwitchParameters.Length <= 1, "Expect exactly one project at a time.");

            if (projectSwitchParameters.Length == 1)
            {
                var projectFile = FileUtilities.FixFilePath(projectSwitchParameters[0]);

                if (FileSystems.Default.DirectoryExists(projectFile))
                {
                    // the provided argument value is actually the directory
                    projectDirectory = projectFile;
                }
                else
                {
                    InitializationException.VerifyThrow(FileSystems.Default.FileExists(projectFile), "ProjectNotFoundError", projectFile);
                    projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                }
            }

            return projectDirectory;
        }


        /// <summary>
        /// Identifies if there is rsp files near the project file
        /// </summary>
        /// <returns>true if there autoresponse file was found</returns>
        private static bool CheckAndGatherProjectAutoResponseFile(CommandLineSwitches switchesFromAutoResponseFile, CommandLineSwitches commandLineSwitches, bool recursing, string commandLine)
        {
            bool found = false;

            var projectDirectory = GetProjectDirectory(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Project]);

            if (!recursing && !commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.NoAutoResponse])
            {
                // gather any switches from the first Directory.Build.rsp found in the project directory or above
                string directoryResponseFile = FileUtilities.GetPathOfFileAbove(directoryResponseFileName, projectDirectory);

                found = !string.IsNullOrWhiteSpace(directoryResponseFile) && GatherAutoResponseFileSwitchesFromFullPath(directoryResponseFile, switchesFromAutoResponseFile, commandLine);

                // Don't look for more response files if it's only in the same place we already looked (next to the exe)
                if (!string.Equals(projectDirectory, s_exePath, StringComparison.OrdinalIgnoreCase))
                {
                    // this combines any found, with higher precedence, with the switches from the original auto response file switches
                    found |= GatherAutoResponseFileSwitches(projectDirectory, switchesFromAutoResponseFile, commandLine);
                }
            }

            return found;
        }

        private static bool WarningsAsErrorsSwitchIsEmpty(CommandLineSwitches commandLineSwitches)
        {
            string val = commandLineSwitches.GetParameterizedSwitchCommandLineArg(CommandLineSwitches.ParameterizedSwitch.WarningsAsErrors);
            if (val is null)
            {
                return false;
            }

            int indexOfColon = val.IndexOf(":");
            return indexOfColon < 0 || indexOfColon == val.Length - 1;
        }

        internal static ProjectIsolationMode ProcessIsolateProjectsSwitch(string[] parameters)
        {

            // Before /isolate had parameters, it was treated as a boolean switch.
            // Preserve that in case anyone is using /isolate:{false|true}
            if (parameters.Length == 1 && bool.TryParse(parameters[0], out bool boolValue))
            {
                return boolValue ? ProjectIsolationMode.True : ProjectIsolationMode.False;
            }

            ProjectIsolationMode isolateProjects = ProjectIsolationMode.True;
            foreach (string parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter))
                {
                    continue;
                }

                string trimmedParameter = parameter.Trim();
                if (trimmedParameter.Equals(nameof(ProjectIsolationMode.MessageUponIsolationViolation), StringComparison.OrdinalIgnoreCase)
                    || trimmedParameter.Equals("Message", StringComparison.OrdinalIgnoreCase))
                {
                    isolateProjects = ProjectIsolationMode.MessageUponIsolationViolation;
                }
                else
                {
                    CommandLineSwitchException.Throw("InvalidIsolateProjectsValue", parameter);
                }
            }

            return isolateProjects;
        }

        internal static GraphBuildOptions ProcessGraphBuildSwitch(string[] parameters)
        {
            var options = new GraphBuildOptions();

            // Before /graph had parameters, it was treated as a boolean switch.
            // Preserve that in case anyone is using /graph:{false|true}
            if (parameters.Length == 1 && bool.TryParse(parameters[0], out var boolValue))
            {
                return boolValue ? options : null;
            }

            foreach (var parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter))
                {
                    continue;
                }

                if (parameter.Trim().Equals("NoBuild", StringComparison.OrdinalIgnoreCase))
                {
                    options = options with { Build = false };
                }
                else
                {
                    CommandLineSwitchException.Throw("InvalidGraphBuildValue", parameter);
                }
            }

            return options;
        }

        private static string ProcessOutputResultsCache(CommandLineSwitches commandLineSwitches)
        {
            return commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.OutputResultsCache)
                ? commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.OutputResultsCache].FirstOrDefault(p => p != null) ?? string.Empty
                : null;
        }

        private static string[] ProcessInputResultsCaches(CommandLineSwitches commandLineSwitches)
        {
            return commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.InputResultsCaches)
                ? commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.InputResultsCaches].Where(p => p != null).ToArray()
                : null;
        }

        /// <summary>
        /// Processes the node reuse switch, the user can set node reuse to true, false or not set the switch. If the switch is
        /// not set the system will check to see if the process is being run as an administrator. This check in localnode provider
        /// will determine the node reuse setting for that case.
        /// </summary>
        internal static bool ProcessNodeReuseSwitch(string[] parameters)
        {
            bool enableNodeReuse;
#if FEATURE_NODE_REUSE
            enableNodeReuse = true;
#else
            enableNodeReuse = false;
#endif

            if (Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE") == "1") // For example to disable node reuse in a gated checkin, without using the flag
            {
                enableNodeReuse = false;
            }

            if (parameters.Length > 0)
            {
                try
                {
                    // There does not seem to be a localizable function for this
                    enableNodeReuse = bool.Parse(parameters[parameters.Length - 1]);
                }
                catch (FormatException ex)
                {
                    CommandLineSwitchException.Throw("InvalidNodeReuseValue", parameters[parameters.Length - 1], ex.Message);
                }
                catch (ArgumentNullException ex)
                {
                    CommandLineSwitchException.Throw("InvalidNodeReuseValue", parameters[parameters.Length - 1], ex.Message);
                }
            }

#if !FEATURE_NODE_REUSE
            if (enableNodeReuse) // Only allowed to pass False on the command line for this switch if the feature is disabled for this installation
                CommandLineSwitchException.Throw("InvalidNodeReuseTrueValue", parameters[parameters.Length - 1]);
#endif

            return enableNodeReuse;
        }

        /// <summary>
        /// Figure out what TextWriter we should preprocess the project file to.
        /// If no parameter is provided to the switch, the default is to output to the console.
        /// </summary>
        internal static TextWriter ProcessPreprocessSwitch(string[] parameters)
        {
            TextWriter writer = Console.Out;

            if (parameters.Length > 0)
            {
                try
                {
                    writer = FileUtilities.OpenWrite(parameters[parameters.Length - 1], append: false);
                }
                catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
                {
                    CommandLineSwitchException.Throw("InvalidPreprocessPath", parameters[parameters.Length - 1], ex.Message);
                }
            }

            return writer;
        }

        internal static TextWriter ProcessTargetsSwitch(string[] parameters)
        {
            TextWriter writer = Console.Out;

            if (parameters.Length > 0)
            {
                try
                {
                    writer = FileUtilities.OpenWrite(parameters[parameters.Length - 1], append: false);
                }
                catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
                {
                    CommandLineSwitchException.Throw("TargetsCouldNotBePrinted", parameters[parameters.Length - 1], ex.Message);
                }
            }

            return writer;
        }

        private static ISet<string> ProcessWarningRelatedSwitch(CommandLineSwitches commandLineSwitches, CommandLineSwitches.ParameterizedSwitch warningSwitch)
        {
            if (!commandLineSwitches.IsParameterizedSwitchSet(warningSwitch))
            {
                return null;
            }

            string[] parameters = commandLineSwitches[warningSwitch];

            ISet<string> warningSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string code in parameters
                .SelectMany(parameter => parameter?.Split(s_commaSemicolon, StringSplitOptions.RemoveEmptyEntries) ?? new string[] { null }))
            {
                if (code == null)
                {
                    // An empty /warnaserror is added as "null".  In this case, the list is cleared
                    // so that all warnings are treated errors
                    warningSwitches.Clear();
                }
                else if (!string.IsNullOrWhiteSpace(code))
                {
                    warningSwitches.Add(code.Trim());
                }
            }

            return warningSwitches;
        }

        internal static ISet<string> ProcessWarnAsErrorSwitch(CommandLineSwitches commandLineSwitches)
        {
            return ProcessWarningRelatedSwitch(commandLineSwitches, CommandLineSwitches.ParameterizedSwitch.WarningsAsErrors);
        }

        internal static ISet<string> ProcessWarnAsMessageSwitch(CommandLineSwitches commandLineSwitches)
        {
            return ProcessWarningRelatedSwitch(commandLineSwitches, CommandLineSwitches.ParameterizedSwitch.WarningsAsMessages);
        }

        internal static ISet<string> ProcessWarnNotAsErrorSwitch(CommandLineSwitches commandLineSwitches)
        {
            return ProcessWarningRelatedSwitch(commandLineSwitches, CommandLineSwitches.ParameterizedSwitch.WarningsNotAsErrors);
        }

        internal static bool ProcessBooleanSwitch(string[] parameters, bool defaultValue, string resourceName)
        {
            bool value = defaultValue;

            if (parameters.Length > 0)
            {
                try
                {
                    value = bool.Parse(parameters[parameters.Length - 1]);
                }
                catch (FormatException ex)
                {
                    CommandLineSwitchException.Throw(resourceName, parameters[parameters.Length - 1], ex.Message);
                }
                catch (ArgumentNullException ex)
                {
                    CommandLineSwitchException.Throw(resourceName, parameters[parameters.Length - 1], ex.Message);
                }
            }

            return value;
        }

        /// <summary>
        /// Processes the profiler evaluation switch
        /// </summary>
        /// <remarks>
        /// If the switch is provided, it adds a <see cref="ProfilerLogger"/> to the collection of loggers
        /// and also returns the created logger. Otherwise, the collection of loggers is not affected and null
        /// is returned
        /// </remarks>
        internal static ProfilerLogger ProcessProfileEvaluationSwitch(string[] parameters, List<ILogger> loggers, out bool enableProfiler)
        {
            if (parameters == null || parameters.Length == 0)
            {
                enableProfiler = false;
                return null;
            }

            enableProfiler = true;
            var profilerFile = parameters[parameters.Length - 1];

            // /prof was specified, but don't attach a logger to write a file
            if (profilerFile == "no-file")
            {
                return null;
            }

            // Check if the file name is valid
            try
            {
                new FileInfo(profilerFile);
            }
            catch (ArgumentException ex)
            {
                CommandLineSwitchException.Throw("InvalidProfilerValue", parameters[parameters.Length - 1],
                    ex.Message);
            }
            catch (PathTooLongException ex)
            {
                CommandLineSwitchException.Throw("InvalidProfilerValue", parameters[parameters.Length - 1],
                    ex.Message);
            }
            catch (NotSupportedException ex)
            {
                CommandLineSwitchException.Throw("InvalidProfilerValue", parameters[parameters.Length - 1],
                    ex.Message);
            }

            var logger = new ProfilerLogger(profilerFile);
            loggers.Add(logger);

            return logger;
        }

        /// <summary>
        /// Uses the input from thinNodeMode switch to start a local node server
        /// </summary>
        /// <param name="commandLineSwitches"></param>
        private static void StartLocalNode(CommandLineSwitches commandLineSwitches, bool lowpriority)
        {
            string[] input = commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.NodeMode];
            int nodeModeNumber = 0;

            if (input.Length > 0)
            {
                try
                {
                    nodeModeNumber = int.Parse(input[0], CultureInfo.InvariantCulture);
                }
                catch (FormatException ex)
                {
                    CommandLineSwitchException.Throw("InvalidNodeNumberValue", input[0], ex.Message);
                }
                catch (OverflowException ex)
                {
                    CommandLineSwitchException.Throw("InvalidNodeNumberValue", input[0], ex.Message);
                }

                CommandLineSwitchException.VerifyThrow(nodeModeNumber >= 0, "InvalidNodeNumberValueIsNegative", input[0]);
            }

            bool restart = true;
            while (restart)
            {
                Exception nodeException = null;
                NodeEngineShutdownReason shutdownReason = NodeEngineShutdownReason.Error;

                // normal OOP node case
                if (nodeModeNumber == 1)
                {
                    // If FEATURE_NODE_REUSE is OFF, just validates that the switch is OK, and always returns False
                    bool nodeReuse = ProcessNodeReuseSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.NodeReuse]);
                    OutOfProcNode node = new OutOfProcNode();
                    shutdownReason = node.Run(nodeReuse, lowpriority, out nodeException);

                    FileUtilities.ClearCacheDirectory();
                }
                else if (nodeModeNumber == 2)
                {
                    // TaskHost nodes don't need to worry about node reuse or low priority. Node reuse is always off, and TaskHosts
                    // receive a connection immediately after being launched and shut down as soon as their work is over, so
                    // whatever our priority is is correct.
                    OutOfProcTaskHostNode node = new OutOfProcTaskHostNode();
                    shutdownReason = node.Run(out nodeException);
                }
                else if (nodeModeNumber == 8)
                {
                    // Since build function has to reuse code from *this* class and OutOfProcServerNode is in different assembly
                    // we have to pass down xmake build invocation to avoid circular dependency
                    OutOfProcServerNode.BuildCallback buildFunction = (commandLine) =>
                    {
                        int exitCode;
                        ExitType exitType;

                        if (!s_initialized)
                        {
                            exitType = ExitType.InitializationError;
                        }
                        else
                        {
                            exitType = Execute(commandLine);
                        }

                        exitCode = exitType == ExitType.Success ? 0 : 1;

                        return (exitCode, exitType.ToString());
                    };

                    OutOfProcServerNode node = new(buildFunction);

                    s_isServerNode = true;
                    shutdownReason = node.Run(out nodeException);

                    FileUtilities.ClearCacheDirectory();
                }
                else
                {
                    CommandLineSwitchException.Throw("InvalidNodeNumberValue", nodeModeNumber.ToString());
                }

                if (shutdownReason == NodeEngineShutdownReason.Error)
                {
                    Debug.WriteLine("An error has happened, throwing an exception");
                    throw nodeException;
                }

                if (shutdownReason != NodeEngineShutdownReason.BuildCompleteReuse)
                {
                    restart = false;
                }
            }
        }

        /// <summary>
        /// Process the /m: switch giving the CPU count
        /// </summary>
        /// <remarks>
        /// Internal for unit testing only
        /// </remarks>
        internal static int ProcessMaxCPUCountSwitch(string[] parameters)
        {
            int cpuCount = 1;

            if (parameters.Length > 0)
            {
                try
                {
                    cpuCount = int.Parse(parameters[parameters.Length - 1], CultureInfo.InvariantCulture);
                }
                catch (FormatException ex)
                {
                    CommandLineSwitchException.Throw("InvalidMaxCPUCountValue", parameters[parameters.Length - 1], ex.Message);
                }
                catch (OverflowException ex)
                {
                    CommandLineSwitchException.Throw("InvalidMaxCPUCountValue", parameters[parameters.Length - 1], ex.Message);
                }

                CommandLineSwitchException.VerifyThrow(cpuCount > 0 && cpuCount <= 1024, "InvalidMaxCPUCountValueOutsideRange", parameters[parameters.Length - 1]);
            }

            return cpuCount;
        }

        /// <summary>
        /// Figures out what project to build.
        /// Throws if it cannot figure it out.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns>The project filename/path.</returns>
        /// Internal for testing purposes
        internal static string ProcessProjectSwitch(
                                 string[] parameters,
                                 string[] projectsExtensionsToIgnore,
                                 DirectoryGetFiles getFiles)
        {
            ErrorUtilities.VerifyThrow(parameters.Length <= 1, "Expect exactly one project at a time.");
            string projectFile = null;

            string projectDirectory = null;

            if (parameters.Length == 1)
            {
                projectFile = FileUtilities.FixFilePath(parameters[0]);

                if (FileSystems.Default.DirectoryExists(projectFile))
                {
                    // If the project file is actually a directory then change the directory to be searched
                    // and null out the project file
                    projectDirectory = projectFile;
                    projectFile = null;
                }
                else
                {
                    InitializationException.VerifyThrow(FileSystems.Default.FileExists(projectFile), "ProjectNotFoundError", projectFile);
                }
            }

            // We need to look in a directory for a project file...
            if (projectFile == null)
            {
                ValidateExtensions(projectsExtensionsToIgnore);
                HashSet<string> extensionsToIgnore = new HashSet<string>(projectsExtensionsToIgnore ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                // Get all files in the current directory that have a proj-like extension
                string[] potentialProjectFiles = getFiles(projectDirectory ?? ".", "*.*proj");
                List<string> actualProjectFiles = new List<string>();
                if (potentialProjectFiles != null)
                {
                    foreach (string s in potentialProjectFiles)
                    {
                        if (!extensionsToIgnore.Contains(Path.GetExtension(s)) && !s.EndsWith("~", StringComparison.CurrentCultureIgnoreCase))
                        {
                            actualProjectFiles.Add(s);
                        }
                    }
                }

                // Get all files in the current directory that have a sln extension
                string[] potentialSolutionFiles = getFiles(projectDirectory ?? ".", "*.sln");
                List<string> actualSolutionFiles = new List<string>();
                List<string> solutionFilterFiles = new List<string>();
                if (potentialSolutionFiles != null)
                {
                    foreach (string s in potentialSolutionFiles)
                    {
                        if (!extensionsToIgnore.Contains(Path.GetExtension(s)))
                        {
                            if (FileUtilities.IsSolutionFilterFilename(s))
                            {
                                solutionFilterFiles.Add(s);
                            }
                            else if (FileUtilities.IsSolutionFilename(s))
                            {
                                actualSolutionFiles.Add(s);
                            }
                        }
                    }
                }

                // If there is exactly 1 project file and exactly 1 solution file
                if (actualProjectFiles.Count == 1 && actualSolutionFiles.Count == 1)
                {
                    // Grab the name of both project and solution without extensions
                    string solutionName = Path.GetFileNameWithoutExtension(actualSolutionFiles[0]);
                    string projectName = Path.GetFileNameWithoutExtension(actualProjectFiles[0]);
                    // Compare the names and error if they are not identical
                    InitializationException.VerifyThrow(string.Equals(solutionName, projectName, StringComparison.OrdinalIgnoreCase), projectDirectory == null ? "AmbiguousProjectError" : "AmbiguousProjectDirectoryError", null, projectDirectory);
                    projectFile = actualSolutionFiles[0];
                }
                // If there is more than one solution file in the current directory we have no idea which one to use
                else if (actualSolutionFiles.Count > 1)
                {
                    InitializationException.VerifyThrow(false, projectDirectory == null ? "AmbiguousProjectError" : "AmbiguousProjectDirectoryError", null, projectDirectory, false);
                }
                // If there is more than one project file in the current directory we may be able to figure it out
                else if (actualProjectFiles.Count > 1)
                {
                    // We have more than one project, it is ambiguous at the moment
                    bool isAmbiguousProject = true;

                    // If there are exactly two projects and one of them is a .proj use that one and ignore the other
                    if (actualProjectFiles.Count == 2)
                    {
                        string firstPotentialProjectExtension = Path.GetExtension(actualProjectFiles[0]);
                        string secondPotentialProjectExtension = Path.GetExtension(actualProjectFiles[1]);

                        // If the two projects have the same extension we can't decide which one to pick
                        if (!string.Equals(firstPotentialProjectExtension, secondPotentialProjectExtension, StringComparison.OrdinalIgnoreCase))
                        {
                            // Check to see if the first project is the proj, if it is use it
                            if (string.Equals(firstPotentialProjectExtension, ".proj", StringComparison.OrdinalIgnoreCase))
                            {
                                projectFile = actualProjectFiles[0];
                                // We have made a decision
                                isAmbiguousProject = false;
                            }
                            // If the first project is not the proj check to see if the second one is the proj, if so use it
                            else if (string.Equals(secondPotentialProjectExtension, ".proj", StringComparison.OrdinalIgnoreCase))
                            {
                                projectFile = actualProjectFiles[1];
                                // We have made a decision
                                isAmbiguousProject = false;
                            }
                        }
                    }
                    InitializationException.VerifyThrow(!isAmbiguousProject, projectDirectory == null ? "AmbiguousProjectError" : "AmbiguousProjectDirectoryError", null, projectDirectory);
                }
                // if there are no project, solution filter, or solution files in the directory, we can't build
                else if (actualProjectFiles.Count == 0 &&
                         actualSolutionFiles.Count == 0 &&
                         solutionFilterFiles.Count == 0)
                {
                    InitializationException.Throw("MissingProjectError", null, null, false);
                }
                else
                {
                    // We are down to only one project, solution, or solution filter.
                    // If only 1 solution build the solution.  If only 1 project build the project. Otherwise, build the solution filter.
                    projectFile = actualSolutionFiles.Count == 1 ? actualSolutionFiles[0] : actualProjectFiles.Count == 1 ? actualProjectFiles[0] : solutionFilterFiles[0];
                    InitializationException.VerifyThrow(actualSolutionFiles.Count == 1 || actualProjectFiles.Count == 1 || solutionFilterFiles.Count == 1, projectDirectory == null ? "AmbiguousProjectError" : "AmbiguousProjectDirectoryError", null, projectDirectory);
                }
            }

            return projectFile;
        }

        private static void ValidateExtensions(string[] projectExtensionsToIgnore)
        {
            if (projectExtensionsToIgnore?.Length > 0)
            {
                foreach (string extension in projectExtensionsToIgnore)
                {
                    // There has to be more than a . passed in as the extension.
                    InitializationException.VerifyThrow(extension?.Length >= 2, "InvalidExtensionToIgnore", extension);

                    // There is an invalid char in the extensionToIgnore.
                    InitializationException.VerifyThrow(extension.IndexOfAny(Path.GetInvalidPathChars()) == -1, "InvalidExtensionToIgnore", extension, null, false);

                    // There were characters before the extension.
                    InitializationException.VerifyThrow(string.Equals(extension, Path.GetExtension(extension), StringComparison.OrdinalIgnoreCase), "InvalidExtensionToIgnore", extension, null, false);

                    // Make sure that no wild cards are in the string because for now we don't allow wild card extensions.
                    InitializationException.VerifyThrow(extension.IndexOfAny(s_wildcards) == -1, "InvalidExtensionToIgnore", extension, null, false);
                }
            }
        }

        /// <summary>
        /// Checks whether an argument given as a parameter starts with valid indicator,
        /// <br/>which means, whether switch begins with one of: "/", "-", "--"
        /// </summary>
        /// <param name="unquotedCommandLineArgument">Command line argument with beginning indicator (e.g. --help).
        /// <br/>This argument has to be unquoted, otherwise the first character will always be a quote character "</param>
        /// <returns>true if argument's beginning matches one of possible indicators
        /// <br/>false if argument's beginning doesn't match any of correct indicator
        /// </returns>
        private static bool ValidateSwitchIndicatorInUnquotedArgument(string unquotedCommandLineArgument)
        {
            return unquotedCommandLineArgument.StartsWith("-", StringComparison.Ordinal) // superset of "--"
                || unquotedCommandLineArgument.StartsWith("/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets the length of the switch indicator (- or / or --)
        /// <br/>The length returned from this method is deduced from the beginning sequence of unquoted argument.
        /// <br/>This way it will "assume" that there's no further error (e.g. //  or ---) which would also be considered as a correct indicator.
        /// </summary>
        /// <param name="unquotedSwitch">Unquoted argument with leading indicator and name</param>
        /// <returns>Correct length of used indicator
        /// <br/>0 if no leading sequence recognized as correct indicator</returns>
        /// Internal for testing purposes
        internal static int GetLengthOfSwitchIndicator(string unquotedSwitch)
        {
            if (unquotedSwitch.StartsWith("--", StringComparison.Ordinal))
            {
                return 2;
            }
            else if (unquotedSwitch.StartsWith("-", StringComparison.Ordinal) || unquotedSwitch.StartsWith("/", StringComparison.Ordinal))
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Figures out which targets are to be built.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns>List of target names.</returns>
        private static string[] ProcessTargetSwitch(string[] parameters)
        {
            foreach (string parameter in parameters)
            {
                int indexOfSpecialCharacter = parameter.IndexOfAny(XMakeElements.InvalidTargetNameCharacters);
                if (indexOfSpecialCharacter >= 0)
                {
                    CommandLineSwitchException.Throw("NameInvalid", nameof(XMakeElements.target), parameter, parameter[indexOfSpecialCharacter].ToString());
                }
            }
            return parameters;
        }

        /// <summary>
        /// The = sign is used to pair properties with their values on the command line.
        /// </summary>
        private static readonly char[] s_propertyValueSeparator = MSBuildConstants.EqualsChar;

        /// <summary>
        /// This is a set of wildcard chars which can cause a file extension to be invalid
        /// </summary>
        private static readonly char[] s_wildcards = MSBuildConstants.WildcardChars;

        /// <summary>
        /// Determines which ToolsVersion was specified on the command line.  If more than
        /// one ToolsVersion was specified, we honor only the final ToolsVersion.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        private static string ProcessToolsVersionSwitch(string[] parameters)
        {
            if (parameters.Length > 0)
            {
                // We don't do any validation on the value of the ToolsVersion here, since we don't
                // know what a valid value looks like.  The engine will take care of this later.
                return parameters[parameters.Length - 1];
            }

            return null;
        }

        /// <summary>
        /// Figures out which properties were set on the command line.
        /// Internal for unit testing.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns>BuildProperty bag.</returns>
        internal static Dictionary<string, string> ProcessPropertySwitch(string[] parameters)
        {
            Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string parameter in parameters)
            {
                // split each <prop>=<value> string into 2 pieces, breaking on the first = that is found
                string[] parameterSections = parameter.Split(s_propertyValueSeparator, 2);

                Debug.Assert((parameterSections.Length >= 1) && (parameterSections.Length <= 2),
                    "String.Split() will return at least one string, and no more than two.");

                // check that the property name is not blank, and the property has a value
                CommandLineSwitchException.VerifyThrow((parameterSections[0].Length > 0) && (parameterSections.Length == 2),
                    "InvalidPropertyError", parameter);

                // Validation of whether the property has a reserved name will occur when
                // we start to build: and it will be logged then, too.
                properties[parameterSections[0]] = parameterSections[1];
            }

            return properties;
        }

        /// <summary>
        /// Instantiates the loggers that are going to listen to events from this build.
        /// </summary>
        /// <returns>List of loggers.</returns>
        private static ILogger[] ProcessLoggingSwitches(
            string[] loggerSwitchParameters,
            string[] distributedLoggerSwitchParameters,
            string[] verbositySwitchParameters,
            bool noConsoleLogger,
            bool distributedFileLogger,
            bool terminalloggerOptIn,
            string aggregatedTerminalLoggerParameters,
            string[] fileLoggerParameters,
            string[] consoleLoggerParameters,
            string[] binaryLoggerParameters,
            string[] profileEvaluationParameters,
            string[][] groupedFileLoggerParameters,
            bool useSimpleErrorLogger,
            out List<DistributedLoggerRecord> distributedLoggerRecords,
            out LoggerVerbosity verbosity,
            out LoggerVerbosity originalVerbosity,
            int cpuCount,
            out ProfilerLogger profilerLogger,
            out bool enableProfiler)
        {
            // if verbosity level is not specified, use the default
            originalVerbosity = LoggerVerbosity.Normal;
            verbosity = originalVerbosity;

            if (verbositySwitchParameters.Length > 0)
            {
                // Read the last verbosity switch found
                originalVerbosity = ProcessVerbositySwitch(verbositySwitchParameters[verbositySwitchParameters.Length - 1]);
                verbosity = originalVerbosity;
            }
            var loggers = new List<ILogger>();

            var outVerbosity = verbosity;
            ProcessBinaryLogger(binaryLoggerParameters, loggers, ref outVerbosity);

            // When returning the result of evaluation from the command line, do not use custom loggers.
            if (!useSimpleErrorLogger)
            {
                ProcessLoggerSwitch(loggerSwitchParameters, loggers, verbosity);
            }

            // Add any loggers which have been specified on the commandline
            distributedLoggerRecords = ProcessDistributedLoggerSwitch(distributedLoggerSwitchParameters, verbosity);

            // Otherwise choose default console logger: None, TerminalLogger, or the older ConsoleLogger
            if (useSimpleErrorLogger)
            {
                loggers.Add(new SimpleErrorLogger());
            }
            else if (terminalloggerOptIn)
            {
                ProcessTerminalLogger(noConsoleLogger, aggregatedTerminalLoggerParameters, distributedLoggerRecords, verbosity, cpuCount, loggers);
            }
            else
            {
                ProcessConsoleLoggerSwitch(noConsoleLogger, consoleLoggerParameters, distributedLoggerRecords, verbosity, cpuCount, loggers);
            }

            ProcessDistributedFileLogger(distributedFileLogger, fileLoggerParameters, distributedLoggerRecords);

            ProcessFileLoggers(groupedFileLoggerParameters, distributedLoggerRecords, cpuCount, loggers);

            verbosity = outVerbosity;

            profilerLogger = ProcessProfileEvaluationSwitch(profileEvaluationParameters, loggers, out enableProfiler);

            return loggers.ToArray();
        }

        /// <summary>
        /// Parameters for a particular logger may be passed in fragments that we have to aggregate: for example,
        ///   /flp:foo=bar;baz=biz /flp:boz=bez becomes "foo=bar;baz=biz;boz=bez"
        /// We are going to aggregate the LoggerParameters into one LoggerParameters string
        /// to do this we must first trim off the ; from the start and the end of the strings as
        /// this would interfere with the use of string.Join by possibly having ;; at the beginning or end of a
        /// logger parameter
        /// </summary>
        internal static string AggregateParameters(string anyPrefixingParameter, string[] parametersToAggregate)
        {
            for (int i = 0; i < parametersToAggregate.Length; i++)
            {
                parametersToAggregate[i] = parametersToAggregate[i].Trim(MSBuildConstants.SemicolonChar);
            }

            // Join the logger parameters into one string separated by semicolons
            string result = anyPrefixingParameter ?? string.Empty;

            // Ensure traling ';' so parametersToAggregate are properly separated
            if (!string.IsNullOrEmpty(result) && result[result.Length - 1] != ';')
            {
                result += ';';
            }

            result += string.Join(";", parametersToAggregate);

            return result;
        }

        /// <summary>
        /// Add a file logger with the appropriate parameters to the loggers list for each
        /// non-empty set of file logger parameters provided.
        /// </summary>
        private static void ProcessFileLoggers(string[][] groupedFileLoggerParameters, List<DistributedLoggerRecord> distributedLoggerRecords, int cpuCount, List<ILogger> loggers)
        {
            for (int i = 0; i < groupedFileLoggerParameters.Length; i++)
            {
                // If we had no, say, "/fl5" then continue; we may have a "/fl6" and so on
                if (groupedFileLoggerParameters[i] == null)
                {
                    continue;
                }

                string fileParameters = "SHOWPROJECTFILE=TRUE;";
                // Use a default log file name of "msbuild.log", "msbuild1.log", "msbuild2.log", etc; put this first on the parameter
                // list so that any supplied log file parameter will override it
                if (i == 0)
                {
                    fileParameters += "logfile=msbuild.log;";
                }
                else
                {
                    fileParameters += $"logfile=msbuild{i}.log;";
                }

                if (groupedFileLoggerParameters[i].Length > 0)
                {
                    // Join the file logger parameters into one string separated by semicolons
                    fileParameters = AggregateParameters(fileParameters, groupedFileLoggerParameters[i]);
                }

                FileLogger fileLogger = new FileLogger();
                // Set to detailed by default, can be overidden by fileLoggerParameters
                LoggerVerbosity defaultFileLoggerVerbosity = LoggerVerbosity.Detailed;
                fileLogger.Verbosity = defaultFileLoggerVerbosity;

                // Check to see if there is a possibility we will be logging from an out-of-proc node.
                // If so (we're multi-proc or the in-proc node is disabled), we register a distributed logger.
                if (cpuCount == 1 && !Traits.Instance.InProcNodeDisabled)
                {
                    // We've decided to use the MP logger even in single proc mode.
                    // Switch it on here, rather than in the logger, so that other hosts that use
                    // the existing ConsoleLogger don't see the behavior change in single proc.
                    fileLogger.Parameters = $"ENABLEMPLOGGING;{fileParameters}";
                    loggers.Add(fileLogger);
                }
                else
                {
                    fileLogger.Parameters = fileParameters;

                    // For performance, register this logger using the forwarding logger mechanism, rather than as an old-style
                    // central logger.
                    DistributedLoggerRecord forwardingLoggerRecord = CreateForwardingLoggerRecord(fileLogger, fileParameters, defaultFileLoggerVerbosity);
                    distributedLoggerRecords.Add(forwardingLoggerRecord);
                }
            }
        }

        private static void ProcessBinaryLogger(string[] binaryLoggerParameters, List<ILogger> loggers, ref LoggerVerbosity verbosity)
        {
            if (binaryLoggerParameters == null || binaryLoggerParameters.Length == 0)
            {
                return;
            }

            string arguments = binaryLoggerParameters[binaryLoggerParameters.Length - 1];

            BinaryLogger logger = new BinaryLogger { Parameters = arguments };

            // If we have a binary logger, force verbosity to diagnostic.
            // The only place where verbosity is used downstream is to determine whether to log task inputs.
            // Since we always want task inputs for a binary logger, set it to diagnostic.
            verbosity = LoggerVerbosity.Diagnostic;

            loggers.Add(logger);
        }

        /// <summary>
        /// Process the noconsole switch and attach or not attach the correct console loggers
        /// </summary>
        internal static void ProcessConsoleLoggerSwitch(
            bool noConsoleLogger,
            string[] consoleLoggerParameters,
            List<DistributedLoggerRecord> distributedLoggerRecords,
            LoggerVerbosity verbosity,
            int cpuCount,
            List<ILogger> loggers)
        {
            // the console logger is always active, unless specifically disabled
            if (!noConsoleLogger)
            {
                // A central logger will be created for single proc and multiproc
                ConsoleLogger logger = new ConsoleLogger(verbosity);
                string consoleParameters = "SHOWPROJECTFILE=TRUE;";

                if ((consoleLoggerParameters?.Length > 0))
                {
                    consoleParameters = AggregateParameters(consoleParameters, consoleLoggerParameters);
                }

                // Always use ANSI escape codes when the build is initiated by server
                if (s_isServerNode)
                {
                    consoleParameters = $"PREFERCONSOLECOLOR;{consoleParameters}";
                }

                // Check to see if there is a possibility we will be logging from an out-of-proc node.
                // If so (we're multi-proc or the in-proc node is disabled), we register a distributed logger.
                if (cpuCount == 1 && !Traits.Instance.InProcNodeDisabled)
                {
                    // We've decided to use the MP logger even in single proc mode.
                    // Switch it on here, rather than in the logger, so that other hosts that use
                    // the existing ConsoleLogger don't see the behavior change in single proc.
                    logger.Parameters = $"ENABLEMPLOGGING;{consoleParameters}";
                    loggers.Add(logger);
                }
                else
                {
                    logger.Parameters = consoleParameters;

                    // For performance, register this logger using the forwarding logger mechanism, rather than as an old-style
                    // central logger.
                    DistributedLoggerRecord forwardingLoggerRecord = CreateForwardingLoggerRecord(logger, consoleParameters, verbosity);
                    distributedLoggerRecords.Add(forwardingLoggerRecord);
                }
            }
        }

        private static void ProcessTerminalLogger(bool noConsoleLogger,
            string aggregatedLoggerParameters,
            List<DistributedLoggerRecord> distributedLoggerRecords,
            LoggerVerbosity verbosity,
            int cpuCount,
            List<ILogger> loggers)
        {
            if (!noConsoleLogger)
            {
                // A central logger will be created for both single proc and multiproc.
                TerminalLogger logger = new TerminalLogger(verbosity)
                {
                    Parameters = aggregatedLoggerParameters
                };

                // Check to see if there is a possibility we will be logging from an out-of-proc node.
                // If so (we're multi-proc or the in-proc node is disabled), we register a distributed logger.
                if (cpuCount == 1 && !Traits.Instance.InProcNodeDisabled)
                {
                    loggers.Add(logger);
                }
                else
                {
                    // For performance, register this logger using the forwarding logger mechanism.
                    DistributedLoggerRecord forwardingLoggerRecord = CreateForwardingLoggerRecord(logger, string.Join(";", TerminalLogger.ConfigurableForwardingLoggerParameters), LoggerVerbosity.Quiet);
                    distributedLoggerRecords.Add(forwardingLoggerRecord);
                }
            }
        }

        /// <summary>
        /// Returns a DistributedLoggerRecord containing this logger and a ConfigurableForwardingLogger.
        /// Looks at the logger's parameters for any verbosity parameter in order to make sure it is setting up the ConfigurableForwardingLogger
        /// with the verbosity level that the logger will actually use.
        /// </summary>
        private static DistributedLoggerRecord CreateForwardingLoggerRecord(ILogger logger, string loggerParameters, LoggerVerbosity defaultVerbosity)
        {
            string verbosityParameter = ExtractAnyLoggerParameter(loggerParameters, "verbosity", "v");

            string verbosityValue = ExtractAnyParameterValue(verbosityParameter);

            LoggerVerbosity effectiveVerbosity = defaultVerbosity;
            if (!string.IsNullOrEmpty(verbosityValue))
            {
                effectiveVerbosity = ProcessVerbositySwitch(verbosityValue);
            }

            // Ensure that the forwarding logger is passed evaluation-finished
            // and project-started events unless the user has specified individual
            // events of interest.
            loggerParameters += ";FORWARDPROJECTCONTEXTEVENTS";

            // Gets the currently loaded assembly in which the specified class is defined
            Assembly engineAssembly = typeof(ProjectCollection).GetTypeInfo().Assembly;
            string loggerClassName = "Microsoft.Build.Logging.ConfigurableForwardingLogger";
            string loggerAssemblyName = engineAssembly.GetName().FullName;
            LoggerDescription forwardingLoggerDescription = new LoggerDescription(loggerClassName, loggerAssemblyName, null, loggerParameters, effectiveVerbosity);
            DistributedLoggerRecord distributedLoggerRecord = new DistributedLoggerRecord(logger, forwardingLoggerDescription);

            return distributedLoggerRecord;
        }

        /// <summary>
        /// Process the file logger switches and attach the correct file loggers. Internal for testing
        /// </summary>
        internal static void ProcessDistributedFileLogger(
            bool distributedFileLogger,
            string[] fileLoggerParameters,
            List<DistributedLoggerRecord> distributedLoggerRecords)
        {
            if (distributedFileLogger)
            {
                string fileParameters = string.Empty;
                if ((fileLoggerParameters?.Length > 0))
                {
                    // Join the file logger parameters into one string separated by semicolons
                    fileParameters = AggregateParameters(null, fileLoggerParameters);
                }

                // Check to see if the logfile parameter has been set, if not set it to the current directory
                string logFileParameter = ExtractAnyLoggerParameter(fileParameters, "logfile");

                string logFileName = FileUtilities.FixFilePath(ExtractAnyParameterValue(logFileParameter));

                try
                {
                    // If the path is not an absolute path set the path to the current directory of the exe combined with the relative path
                    // If the string is empty then send it through as the distributed file logger WILL deal with EMPTY logfile paths
                    if (!string.IsNullOrEmpty(logFileName) && !Path.IsPathRooted(logFileName))
                    {
                        fileParameters = fileParameters.Replace(logFileParameter,
                            $"logFile={Path.Combine(Directory.GetCurrentDirectory(), logFileName)}");
                    }
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    throw new LoggerException(e.Message, e);
                }

                if (string.IsNullOrEmpty(logFileName))
                {
                    // If the string is not empty and it does not end in a ;, we need to add a ; to separate what is in the parameter from the logfile
                    // if the string is empty, no ; is needed because logfile is the only parameter which will be passed in
                    if (!string.IsNullOrEmpty(fileParameters) && !fileParameters.EndsWith(";", StringComparison.OrdinalIgnoreCase))
                    {
                        fileParameters += ";";
                    }

                    fileParameters += $"logFile={Path.Combine(Directory.GetCurrentDirectory(), msbuildLogFileName)}";
                }

                // Gets the currently loaded assembly in which the specified class is defined
                Assembly engineAssembly = typeof(ProjectCollection).GetTypeInfo().Assembly;
                string loggerClassName = "Microsoft.Build.Logging.DistributedFileLogger";
                string loggerAssemblyName = engineAssembly.GetName().FullName;
                // Node the verbosity parameter is not used by the Distributed file logger so changing it here has no effect. It must be changed in the distributed file logger
                LoggerDescription forwardingLoggerDescription = new LoggerDescription(loggerClassName, loggerAssemblyName, null, fileParameters, LoggerVerbosity.Detailed);
                // Use the null as the central Logger, this will cause the engine to instantiate the NullCentralLogger, this logger will throw an exception if anything except for the buildstarted and buildFinished events are sent
                DistributedLoggerRecord distributedLoggerRecord = new DistributedLoggerRecord(null, forwardingLoggerDescription);
                distributedLoggerRecords.Add(distributedLoggerRecord);
            }
        }

        /// <summary>
        /// Given a string of aggregated parameters, such as "foo=bar;baz;biz=boz" and a list of parameter names,
        /// such as "biz", tries to find and return the LAST matching parameter, such as "biz=boz"
        /// </summary>
        internal static string ExtractAnyLoggerParameter(string parameters, params string[] parameterNames)
        {
            string[] nameValues = parameters.Split(MSBuildConstants.SemicolonChar);
            string result = null;

            foreach (string nameValue in nameValues)
            {
                foreach (string name in parameterNames)
                {
                    bool found = nameValue.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase) ||   // Parameters with value, such as "logfile=foo.txt"
                                 string.Equals(name, nameValue, StringComparison.OrdinalIgnoreCase);       // Parameters without value, such as "append"

                    if (found)
                    {
                        result = nameValue;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Given a parameter, such as "foo=bar", tries to find and return
        /// the value part, ie "bar"; otherwise returns null.
        /// </summary>
        private static string ExtractAnyParameterValue(string parameter)
        {
            string value = null;

            if (!string.IsNullOrEmpty(parameter))
            {
                string[] nameValuePair = parameter.Split(MSBuildConstants.EqualsChar);

                value = (nameValuePair.Length > 1) ? nameValuePair[1] : null;
            }

            return value;
        }

        /// <summary>
        /// Figures out what verbosity level to assign to loggers.
        /// </summary>
        /// <remarks>
        /// Internal for unit testing only
        /// </remarks>
        /// <param name="value"></param>
        /// <returns>The logger verbosity level.</returns>
        internal static LoggerVerbosity ProcessVerbositySwitch(string value)
        {
            LoggerVerbosity verbosity = LoggerVerbosity.Normal;

            if (string.Equals(value, "q", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LoggerVerbosity.Quiet;
            }
            else if (string.Equals(value, "m", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "minimal", StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LoggerVerbosity.Minimal;
            }
            else if (string.Equals(value, "n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LoggerVerbosity.Normal;
            }
            else if (string.Equals(value, "d", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "detailed", StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LoggerVerbosity.Detailed;
            }
            else if (string.Equals(value, "diag", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "diagnostic", StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LoggerVerbosity.Diagnostic;
            }
            else
            {
                CommandLineSwitchException.Throw("InvalidVerbosityError", value);
            }

            return verbosity;
        }

        /// <summary>
        /// Figures out which additional loggers are going to listen to build events.
        /// </summary>
        /// <returns>List of loggers.</returns>
        private static void ProcessLoggerSwitch(string[] parameters, List<ILogger> loggers, LoggerVerbosity verbosity)
        {
            foreach (string parameter in parameters)
            {
                string unquotedParameter = QuotingUtilities.Unquote(parameter);

                LoggerDescription loggerDescription = ParseLoggingParameter(parameter, unquotedParameter, verbosity);

                if (CreateAndConfigureLogger(loggerDescription, verbosity, unquotedParameter, out ILogger logger))
                {
                    loggers.Add(logger);
                }
            }
        }

        /// <summary>
        /// Parses command line arguments describing the distributed loggers
        /// </summary>
        /// <returns>List of distributed logger records</returns>
        private static List<DistributedLoggerRecord> ProcessDistributedLoggerSwitch(string[] parameters, LoggerVerbosity verbosity)
        {
            List<DistributedLoggerRecord> distributedLoggers = new List<DistributedLoggerRecord>();

            foreach (string parameter in parameters)
            {
                // split each <central logger>|<node logger> string into two pieces, breaking on the first | that is found
                var loggerSpec = QuotingUtilities.SplitUnquoted(parameter, 2, true /* keep empty splits */, false /* keep quotes */, out _, '*');

                ErrorUtilities.VerifyThrow((loggerSpec.Count >= 1) && (loggerSpec.Count <= 2),
                    "SplitUnquoted() must return at least one string, and no more than two.");

                string unquotedParameter = QuotingUtilities.Unquote(loggerSpec[0]);
                LoggerDescription centralLoggerDescription =
                    ParseLoggingParameter(loggerSpec[0], unquotedParameter, verbosity);

                if (!CreateAndConfigureLogger(centralLoggerDescription, verbosity, unquotedParameter, out ILogger centralLogger))
                {
                    continue;
                }

                // By default if no forwarding logger description is specified the same logger is used for both functions
                LoggerDescription forwardingLoggerDescription = centralLoggerDescription;

                if (loggerSpec.Count > 1)
                {
                    unquotedParameter = QuotingUtilities.Unquote(loggerSpec[1]);
                    forwardingLoggerDescription = ParseLoggingParameter(loggerSpec[1], unquotedParameter, verbosity);
                }

                DistributedLoggerRecord distributedLoggerRecord =
                    new DistributedLoggerRecord(centralLogger, forwardingLoggerDescription);

                distributedLoggers.Add(distributedLoggerRecord);
            }

            return distributedLoggers;
        }

        /// <summary>
        /// Parse a command line logger argument into a LoggerDescription structure
        /// </summary>
        /// <param name="parameter">the command line string</param>
        /// <param name="unquotedParameter">the command line string</param>
        /// <param name="verbosity">logging verbosity</param>
        /// <returns></returns>
        private static LoggerDescription ParseLoggingParameter(string parameter, string unquotedParameter, LoggerVerbosity verbosity)
        {
            string loggerClassName;
            string loggerParameters = null;
            bool isOptional = false;

            // split each <logger type>;<logger parameters> string into two pieces, breaking on the first ; that is found
            var loggerSpec = QuotingUtilities.SplitUnquoted(parameter, 2, true /* keep empty splits */, false /* keep quotes */, out _, ';');

            ErrorUtilities.VerifyThrow((loggerSpec.Count >= 1) && (loggerSpec.Count <= 2),
                "SplitUnquoted() must return at least one string, and no more than two.");

            // check that the logger is specified
            CommandLineSwitchException.VerifyThrow(loggerSpec[0].Length > 0,
                "InvalidLoggerError", unquotedParameter);

            // extract logger parameters if present
            if (loggerSpec.Count == 2)
            {
                loggerParameters = QuotingUtilities.Unquote(loggerSpec[1]);
            }

            // split each <logger class>,<logger assembly>[,<option1>][,option2] parameters string into pieces
            var loggerTypeSpec = QuotingUtilities.SplitUnquoted(loggerSpec[0], int.MaxValue, true /* keep empty splits */, false /* keep quotes */, out _, ',');

            ErrorUtilities.VerifyThrow(loggerTypeSpec.Count >= 1, "SplitUnquoted() must return at least one string");

            string loggerAssemblySpec;

            // if the logger class and assembly are both specified
            if (loggerTypeSpec.Count >= 2)
            {
                loggerClassName = QuotingUtilities.Unquote(loggerTypeSpec[0]);
                loggerAssemblySpec = QuotingUtilities.Unquote(loggerTypeSpec[1]);
            }
            else
            {
                loggerClassName = string.Empty;
                loggerAssemblySpec = QuotingUtilities.Unquote(loggerTypeSpec[0]);
            }

            // Loop through the remaining items as options
            for (int i = 2; i < loggerTypeSpec.Count; i++)
            {
                if (string.Equals(loggerTypeSpec[i] as string, nameof(isOptional), StringComparison.OrdinalIgnoreCase))
                {
                    isOptional = true;
                }
            }

            CommandLineSwitchException.VerifyThrow(loggerAssemblySpec.Length > 0,
                "InvalidLoggerError", unquotedParameter);

            string loggerAssemblyName = null;
            string loggerAssemblyFile = null;

            // DDB Bug msbuild.exe -Logger:FileLogger,Microsoft.Build.Engine fails due to moved engine file.
            // Only add strong naming if the assembly is a non-strong named 'Microsoft.Build.Engine' (i.e, no additional characteristics)
            // Concat full Strong Assembly to match v4.0
            if (string.Equals(loggerAssemblySpec, "Microsoft.Build.Engine", StringComparison.OrdinalIgnoreCase))
            {
                loggerAssemblySpec = "Microsoft.Build.Engine,Version=4.0.0.0,Culture=neutral,PublicKeyToken=b03f5f7f11d50a3a";
            }

            // figure out whether the assembly's identity (strong/weak name), or its filename/path is provided
            string testFile = FileUtilities.FixFilePath(loggerAssemblySpec);
            if (FileSystems.Default.FileExists(testFile))
            {
                loggerAssemblyFile = testFile;
            }
            else
            {
                loggerAssemblyName = loggerAssemblySpec;
            }

            return new LoggerDescription(loggerClassName, loggerAssemblyName, loggerAssemblyFile, loggerParameters, verbosity, isOptional);
        }

        /// <summary>
        /// Loads a logger from its assembly, instantiates it, and handles errors.
        /// </summary>
        /// <returns>Instantiated logger.</returns>
        private static bool CreateAndConfigureLogger(
            LoggerDescription loggerDescription,
            LoggerVerbosity verbosity,
            string unquotedParameter,
            out ILogger logger)
        {
            logger = null;

            try
            {
                logger = loggerDescription.CreateLogger();

                InitializationException.VerifyThrow(logger != null, "LoggerNotFoundError", unquotedParameter);
            }
            catch (IOException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerCreationError", unquotedParameter, e, false);
            }
            catch (BadImageFormatException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerCreationError", unquotedParameter, e, false);
            }
            catch (SecurityException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerCreationError", unquotedParameter, e, false);
            }
            catch (ReflectionTypeLoadException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerCreationError", unquotedParameter, e, false);
            }
            catch (MemberAccessException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerCreationError", unquotedParameter, e, false);
            }
            catch (TargetInvocationException e) when (!loggerDescription.IsOptional)
            {
                InitializationException.Throw("LoggerFatalError", unquotedParameter, e.InnerException, true);
            }
            catch (Exception e) when (loggerDescription.IsOptional)
            {
                Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("OptionalLoggerCreationMessage", loggerDescription.Name, e.Message));
                return false;
            }

            // Configure the logger by setting the verbosity level and parameters
            try
            {
                // set its verbosity level
                logger.Verbosity = verbosity;

                // set the logger parameters (if any)
                if (loggerDescription.LoggerSwitchParameters != null)
                {
                    logger.Parameters = loggerDescription.LoggerSwitchParameters;
                }
            }
            catch (LoggerException)
            {
                // Logger failed politely during parameter/verbosity setting
                throw;
            }
            catch (Exception e)
            {
                InitializationException.Throw("LoggerFatalError", unquotedParameter, e, true);
            }

            return true;
        }

        private static void ReplayBinaryLog(
            string binaryLogFilePath,
            ILogger[] loggers,
            IEnumerable<DistributedLoggerRecord> distributedLoggerRecords,
            int cpuCount,
            bool isBuildCheckEnabled)
        {

            var replayEventSource = new BinaryLogReplayEventSource();

            var eventSource = isBuildCheckEnabled ?
                BuildCheckReplayModeConnector.GetMergedEventSource(BuildManager.DefaultBuildManager, replayEventSource) :
                replayEventSource;

            foreach (var distributedLoggerRecord in distributedLoggerRecords)
            {
                ILogger centralLogger = distributedLoggerRecord.CentralLogger;
                if (centralLogger is INodeLogger nodeLogger)
                {
                    nodeLogger.Initialize(eventSource, cpuCount);
                }
                else
                {
                    centralLogger?.Initialize(eventSource);
                }
            }

            foreach (var logger in loggers)
            {
                if (logger is INodeLogger nodeLogger)
                {
                    nodeLogger.Initialize(eventSource, cpuCount);
                }
                else
                {
                    logger.Initialize(eventSource);
                }
            }

            try
            {
                replayEventSource.Replay(binaryLogFilePath, s_buildCancellationSource.Token);
            }
            catch (Exception ex)
            {
                var message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidLogFileFormat", ex.Message);
                Console.WriteLine(message);
            }

            foreach (var logger in loggers)
            {
                logger.Shutdown();
            }

            foreach (var distributedLoggerRecord in distributedLoggerRecords)
            {
                distributedLoggerRecord.CentralLogger?.Shutdown();
            }
        }

        /// <summary>
        /// Figures out if the project needs to be validated against a schema.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns>The schema to validate against, or null.</returns>
        private static string ProcessValidateSwitch(string[] parameters)
        {
            string schemaFile = null;

            foreach (string parameter in parameters)
            {
                InitializationException.VerifyThrow(schemaFile == null, "MultipleSchemasError", parameter);
                string fileName = FileUtilities.FixFilePath(parameter);
                InitializationException.VerifyThrow(FileSystems.Default.FileExists(fileName), "SchemaNotFoundError", fileName);

                schemaFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            }

            return schemaFile;
        }

        /// <summary>
        /// Given an invalid ToolsVersion string and the collection of valid toolsets,
        /// throws an InitializationException with the appropriate message.
        /// </summary>
        private static void ThrowInvalidToolsVersionInitializationException(IEnumerable<Toolset> toolsets, string toolsVersion)
        {
            string toolsVersionList = string.Empty;
            foreach (Toolset toolset in toolsets)
            {
                toolsVersionList += $"\"{toolset.ToolsVersion}\", ";
            }

            // Remove trailing comma and space
            if (toolsVersionList.Length > 0)
            {
                toolsVersionList = toolsVersionList.Substring(0, toolsVersionList.Length - 2);
            }

            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(
                "UnrecognizedToolsVersion",
                toolsVersion,
                toolsVersionList);
            message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidToolsVersionError", message);

            InitializationException.Throw(message, toolsVersion);
        }

        /// <summary>
        /// Displays the application version message/logo.
        /// </summary>
        private static void DisplayVersionMessageIfNeeded(bool recursing, bool useTerminalLogger, CommandLineSwitches commandLineSwitches)
        {
            if (recursing)
            {
                return;
            }

            // Show the versioning information if the user has not disabled it or msbuild is not running in a mode
            //  where it is not appropriate to show the versioning information (information querying mode that can be plugged into CLI scripts,
            //  terminal logger mode, where we want to display only the most relevant info, while output is not meant for investigation).
            // NOTE: response files are not reflected in this check. So enabling TL in response file will lead to version message still being shown.
            bool shouldShowLogo = !commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.NoLogo] &&
                                  !commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.Preprocess) &&
                                  !commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.GetProperty) &&
                                  !commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.GetItem) &&
                                  !commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.GetTargetResult) &&
                                  !commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.FeatureAvailability) &&
                                  !useTerminalLogger;

            if (shouldShowLogo)
            {
                Console.WriteLine(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("MSBuildVersionMessage", ProjectCollection.DisplayVersion, NativeMethods.FrameworkName));
            }
        }

        /// <summary>
        /// Displays the help message that explains switch usage and syntax.
        /// </summary>
        private static void ShowHelpMessage()
        {
            // NOTE: the help message is broken into pieces because localization
            // prefers it that way -- see VSW #482758 "Entire command line help
            // message is stored in a single resource"
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_1_Syntax"));
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_2_Description"));
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_3_SwitchesHeader"));
            foreach (string parameterizedSwitchRsouceId in CommandLineSwitches.GetParameterizedSwitchResourceIds())
            {
                Console.WriteLine(AssemblyResources.GetString(parameterizedSwitchRsouceId));
            }
            foreach (string parameterlessSwitchRsouceId in CommandLineSwitches.GetParameterlessSwitchResourceIds())
            {
                Console.WriteLine(AssemblyResources.GetString(parameterlessSwitchRsouceId));
            }
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_7_ResponseFile"));
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_16_Examples"));
            Console.WriteLine(AssemblyResources.GetString("HelpMessage_37_DocsLink"));
        }

        /// <summary>
        /// Displays a message prompting the user to look up help.
        /// </summary>
        private static void ShowHelpPrompt()
        {
            Console.WriteLine(AssemblyResources.GetString("HelpPrompt"));
        }

        /// <summary>
        /// Displays the build engine's version number.
        /// </summary>
        private static void ShowVersion()
        {
            // Change Version switch output to finish with a newline https://github.com/dotnet/msbuild/pull/9485
            if (ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave17_10))
            {
                Console.WriteLine(ProjectCollection.Version.ToString());
            }
            else
            {
                Console.Write(ProjectCollection.Version.ToString());
            }
        }

        private static void ShowFeatureAvailability(string[] features)
        {
            if (features.Length == 1)
            {
                string featureName = features[0];
                FeatureStatus availability = Features.CheckFeatureAvailability(featureName);
                Console.WriteLine(availability);
            }
            else
            {
                var jsonNode = new JsonObject();
                foreach (string featureName in features)
                {
                    jsonNode[featureName] = Features.CheckFeatureAvailability(featureName).ToString();
                }

                var options = new JsonSerializerOptions() { AllowTrailingCommas = false, WriteIndented = true };
                Console.WriteLine(jsonNode.ToJsonString(options));
            }
        }
    }
}
