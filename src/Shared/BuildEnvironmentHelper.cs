// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Shared
{
    internal sealed class BuildEnvironmentHelper
    {
        // Since this class is added as 'link' to shared source in multiple projects,
        // MSBuildConstants.CurrentVisualStudioVersion is not available in all of them.
        private const string CurrentVisualStudioVersion = "17.0";

        // MSBuildConstants.CurrentToolsVersion
        private const string CurrentToolsVersion = "Current";

        /// <summary>
        /// Name of the Visual Studio (and Blend) process.
        /// VS ASP intellisense server fails without Microsoft.VisualStudio.Web.Host. Remove when issue fixed: https://devdiv.visualstudio.com/DevDiv/_workitems/edit/574986
        /// </summary>
        private static readonly string[] s_visualStudioProcess = { "DEVENV", "BLEND", "Microsoft.VisualStudio.Web.Host" };

        /// <summary>
        /// Name of the MSBuild process(es)
        /// </summary>
        private static readonly string[] s_msBuildProcess = { "MSBUILD", "MSBUILDTASKHOST" };

        /// <summary>
        /// Name of MSBuild executable files.
        /// </summary>
        private static readonly string[] s_msBuildExeNames = { "MSBuild.exe", "MSBuild.dll" };

        /// <summary>
        /// Gets the cached Build Environment instance.
        /// </summary>
        public static BuildEnvironment Instance
        {
            get
            {
                try
                {
                    return BuildEnvironmentHelperSingleton.s_instance;
                }
                catch (TypeInitializationException e)
                {
                    if (e.InnerException != null)
                    {
                        // Throw the error that caused the TypeInitializationException.
                        // (likely InvalidOperationException)
                        throw e.InnerException;
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Find the location of MSBuild.exe based on the current environment.
        /// </summary>
        /// <remarks>
        /// This defines the order and precedence for various methods of discovering MSBuild and associated toolsets.
        /// At a high level, an install under Visual Studio is preferred as the user may have SDKs installed to a
        /// specific instance of Visual Studio and build will only succeed if we can discover those. See
        /// https://github.com/dotnet/msbuild/issues/1461 for details.
        /// </remarks>
        /// <returns>Build environment.</returns>
        private static BuildEnvironment Initialize()
        {
            // See https://github.com/dotnet/msbuild/issues/1461 for specification of ordering and details.
            var possibleLocations = new Func<BuildEnvironment>[]
            {
                TryFromEnvironmentVariable,
                TryFromVisualStudioProcess,
                TryFromMSBuildProcess,
                TryFromMSBuildAssembly,
                TryFromDevConsole,
                TryFromSetupApi,
                TryFromAppContextBaseDirectory
            };

            foreach (var location in possibleLocations)
            {
                var env = location();
                if (env != null)
                {
                    return env;
                }
            }

            // If we can't find a suitable environment, continue in the 'None' mode. If not running tests,
            // we will use the current running process for the CurrentMSBuildExePath value.  This is likely
            // wrong, but many things use the CurrentMSBuildToolsDirectory value which must be set for basic
            // functionality to work.
            //
            // If we are running tests, then the current running process may be a test runner located in the
            // NuGet packages folder.  So in that case, we use the location of the current assembly, which
            // will be in the output path of the test project, which is what we want.

            string msbuildExePath;
            if (s_runningTests())
            {
                msbuildExePath = typeof(BuildEnvironmentHelper).Assembly.Location;
            }
            else
            {
                msbuildExePath = s_getProcessFromRunningProcess();
            }

            return new BuildEnvironment(
                BuildEnvironmentMode.None,
                msbuildExePath,
                runningTests: s_runningTests(),
                runningInMSBuildExe: false,
                runningInVisualStudio: false,
                visualStudioPath: null);
        }

        private static BuildEnvironment TryFromEnvironmentVariable()
        {
            var msBuildExePath = s_getEnvironmentVariable("MSBUILD_EXE_PATH");

            return msBuildExePath == null
                ? null
                : TryFromMSBuildExeUnderVisualStudio(msBuildExePath, allowLegacyToolsVersion: true) ?? TryFromStandaloneMSBuildExe(msBuildExePath);
        }

        private static BuildEnvironment TryFromVisualStudioProcess()
        {
            if (!NativeMethodsShared.IsWindows)
            {
                return null;
            }

            var vsProcess = s_getProcessFromRunningProcess();
            if (!IsProcessInList(vsProcess, s_visualStudioProcess))
            {
                return null;
            }

            var vsRoot = FileUtilities.GetFolderAbove(vsProcess, 3);
            string msBuildExe = GetMSBuildExeFromVsRoot(vsRoot);

            return new BuildEnvironment(
                BuildEnvironmentMode.VisualStudio,
                msBuildExe,
                runningTests: false,
                runningInMSBuildExe: false,
                runningInVisualStudio: true,
                visualStudioPath: vsRoot);
        }

        private static BuildEnvironment TryFromMSBuildProcess()
        {
            var msBuildExe = s_getProcessFromRunningProcess();
            if (!IsProcessInList(msBuildExe, s_msBuildProcess))
            {
                return null;
            }

            // First check if we're in a VS installation
            if (NativeMethodsShared.IsWindows &&
                Regex.IsMatch(msBuildExe, $@".*\\MSBuild\\{CurrentToolsVersion}\\Bin\\.*MSBuild(?:TaskHost)?\.exe", RegexOptions.IgnoreCase))
            {
                return new BuildEnvironment(
                    BuildEnvironmentMode.VisualStudio,
                    msBuildExe,
                    runningTests: false,
                    runningInMSBuildExe: true,
                    runningInVisualStudio: false,
                    visualStudioPath: GetVsRootFromMSBuildAssembly(msBuildExe));
            }

            // Standalone mode running in MSBuild.exe
            return new BuildEnvironment(
                BuildEnvironmentMode.Standalone,
                msBuildExe,
                runningTests: false,
                runningInMSBuildExe: true,
                runningInVisualStudio: false,
                visualStudioPath: null);
        }

        private static BuildEnvironment TryFromMSBuildAssembly()
        {
            var buildAssembly = s_getExecutingAssemblyPath();
            if (buildAssembly == null)
            {
                return null;
            }

            // Check for MSBuild.[exe|dll] next to the current assembly
            var msBuildExe = Path.Combine(FileUtilities.GetFolderAbove(buildAssembly), "MSBuild.exe");
            var msBuildDll = Path.Combine(FileUtilities.GetFolderAbove(buildAssembly), "MSBuild.dll");

            // First check if we're in a VS installation
            var environment = TryFromMSBuildExeUnderVisualStudio(msBuildExe);
            if (environment != null)
            {
                return environment;
            }

            // We're not in VS, check for MSBuild.exe / dll to consider this a standalone environment.
            string msBuildPath = null;
            if (FileSystems.Default.FileExists(msBuildExe))
            {
                msBuildPath = msBuildExe;
            }
            else if (FileSystems.Default.FileExists(msBuildDll))
            {
                msBuildPath = msBuildDll;
            }

            if (!string.IsNullOrEmpty(msBuildPath))
            {
                // Standalone mode with toolset
                return new BuildEnvironment(
                    BuildEnvironmentMode.Standalone,
                    msBuildPath,
                    runningTests: s_runningTests(),
                    runningInMSBuildExe: false,
                    runningInVisualStudio: false,
                    visualStudioPath: null);
            }

            return null;
        }

        private static BuildEnvironment TryFromMSBuildExeUnderVisualStudio(string msbuildExe, bool allowLegacyToolsVersion = false)
        {
            string msBuildPathPattern = allowLegacyToolsVersion
                ? $@".*\\MSBuild\\({CurrentToolsVersion}|\d+\.0)\\Bin\\.*"
                : $@".*\\MSBuild\\{CurrentToolsVersion}\\Bin\\.*";

            if (NativeMethodsShared.IsWindows &&
                Regex.IsMatch(msbuildExe, msBuildPathPattern, RegexOptions.IgnoreCase))
            {
                string visualStudioRoot = GetVsRootFromMSBuildAssembly(msbuildExe);
                return new BuildEnvironment(
                        BuildEnvironmentMode.VisualStudio,
                        GetMSBuildExeFromVsRoot(visualStudioRoot),
                        runningTests: s_runningTests(),
                        runningInMSBuildExe: false,
                        runningInVisualStudio: false,
                        visualStudioPath: visualStudioRoot);
            }

            return null;
        }

        private static BuildEnvironment TryFromDevConsole()
        {
            if (s_runningTests())
            {
                // If running unit tests, then don't try to get the build environment from MSBuild installed on the machine
                //  (we should be using the locally built MSBuild instead)
                return null;
            }

            // VSINSTALLDIR and VisualStudioVersion are set from the Developer Command Prompt.
            var vsInstallDir = s_getEnvironmentVariable("VSINSTALLDIR");
            var vsVersion = s_getEnvironmentVariable("VisualStudioVersion");

            if (string.IsNullOrEmpty(vsInstallDir) || string.IsNullOrEmpty(vsVersion) ||
                vsVersion != CurrentVisualStudioVersion || !FileSystems.Default.DirectoryExists(vsInstallDir))
            {
                return null;
            }

            return new BuildEnvironment(
                BuildEnvironmentMode.VisualStudio,
                GetMSBuildExeFromVsRoot(vsInstallDir),
                runningTests: false,
                runningInMSBuildExe: false,
                runningInVisualStudio: false,
                visualStudioPath: vsInstallDir);
        }

        private static BuildEnvironment TryFromSetupApi()
        {
            if (s_runningTests())
            {
                // If running unit tests, then don't try to get the build environment from MSBuild installed on the machine
                //  (we should be using the locally built MSBuild instead)
                return null;
            }

            Version v = new Version(CurrentVisualStudioVersion);
            var instances = s_getVisualStudioInstances()
                .Where(i => i.Version.Major == v.Major && FileSystems.Default.DirectoryExists(i.Path))
                .ToList();

            if (instances.Count == 0)
            {
                return null;
            }

            if (instances.Count > 1)
            {
                // TODO: Warn user somehow. We may have picked the wrong one.
            }

            return new BuildEnvironment(
                BuildEnvironmentMode.VisualStudio,
                GetMSBuildExeFromVsRoot(instances[0].Path),
                runningTests: false,
                runningInMSBuildExe: false,
                runningInVisualStudio: false,
                visualStudioPath: instances[0].Path);
        }

        private static BuildEnvironment TryFromAppContextBaseDirectory()
        {
            // Assemblies compiled against anything older than .NET 4.0 won't have a System.AppContext
            // Try the base directory that the assembly resolver uses to probe for assemblies.
            // Under certain scenarios the assemblies are loaded from spurious locations like the NuGet package cache
            // but the toolset files are copied to the app's directory via "contentFiles".

            var appContextBaseDirectory = s_getAppContextBaseDirectory();
            if (string.IsNullOrEmpty(appContextBaseDirectory))
            {
                return null;
            }

            // Look for possible MSBuild exe names in the AppContextBaseDirectory
            return s_msBuildExeNames
                .Select((name) => TryFromStandaloneMSBuildExe(Path.Combine(appContextBaseDirectory, name)))
                .FirstOrDefault(env => env != null);
        }

        private static BuildEnvironment TryFromStandaloneMSBuildExe(string msBuildExePath)
        {
            if (!string.IsNullOrEmpty(msBuildExePath) && FileSystems.Default.FileExists(msBuildExePath))
            {
                // MSBuild.exe was found outside of Visual Studio. Assume Standalone mode.
                return new BuildEnvironment(
                    BuildEnvironmentMode.Standalone,
                    msBuildExePath,
                    runningTests: s_runningTests(),
                    runningInMSBuildExe: false,
                    runningInVisualStudio: false,
                    visualStudioPath: null);
            }

            return null;
        }

        private static string GetVsRootFromMSBuildAssembly(string msBuildAssembly)
        {
            string directory = Path.GetDirectoryName(msBuildAssembly);
            return FileUtilities.GetFolderAbove(msBuildAssembly,
                directory.EndsWith(@"\amd64", StringComparison.OrdinalIgnoreCase) ||
                directory.EndsWith(@"\arm64", StringComparison.OrdinalIgnoreCase)
                    ? 5
                    : 4);
        }

        private static string GetMSBuildExeFromVsRoot(string visualStudioRoot)
        {
            return FileUtilities.CombinePaths(
                visualStudioRoot,
                "MSBuild",
                CurrentToolsVersion,
                "Bin",
                NativeMethodsShared.ProcessorArchitecture == Framework.NativeMethods.ProcessorArchitectures.X64 ? "amd64" :
                NativeMethodsShared.ProcessorArchitecture == Framework.NativeMethods.ProcessorArchitectures.ARM64 ? "arm64" : string.Empty,
                "MSBuild.exe");
        }

        private static bool? _runningTests;
        private static readonly object _runningTestsLock = new object();

        private static bool CheckIfRunningTests()
        {
            if (_runningTests != null)
            {
                return _runningTests.Value;
            }

            lock (_runningTestsLock)
            {
                if (_runningTests != null)
                {
                    return _runningTests.Value;
                }

                // Check if running tests via the TestInfo class in Microsoft.Build.Framework.
                //  See the comments on the TestInfo class for an explanation of why it works this way.
                var frameworkAssembly = typeof(Framework.ITask).Assembly;
                var testInfoType = frameworkAssembly.GetType("Microsoft.Build.Framework.TestInfo");
                var runningTestsField = testInfoType.GetField("s_runningTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                _runningTests = (bool)runningTestsField.GetValue(null);

                return _runningTests.Value;
            }
        }

        /// <summary>
        /// Returns true if processName appears in the processList
        /// </summary>
        /// <param name="processName">Name of the process</param>
        /// <param name="processList">List of processes to check</param>
        /// <returns></returns>
        private static bool IsProcessInList(string processName, string[] processList)
        {
            var processFileName = Path.GetFileNameWithoutExtension(processName);

            if (string.IsNullOrEmpty(processFileName))
            {
                return false;
            }

            return processList.Any(s =>
                processFileName.Equals(s, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetProcessFromRunningProcess()
        {
#if RUNTIME_TYPE_NETCORE
            // The EntryAssembly property can return null when a managed assembly has been loaded from
            // an unmanaged application (for example, using custom CLR hosting).
            if (AssemblyUtilities.EntryAssembly == null)
            {
                return EnvironmentUtilities.ProcessPath;
            }

            return AssemblyUtilities.GetAssemblyLocation(AssemblyUtilities.EntryAssembly);
#else

            return EnvironmentUtilities.ProcessPath;
#endif
        }

        private static string GetExecutingAssemblyPath()
        {
            return FileUtilities.ExecutingAssemblyPath;
        }

        private static string GetAppContextBaseDirectory()
        {
#if !CLR2COMPATIBILITY // Assemblies compiled against anything older than .NET 4.0 won't have a System.AppContext
            return AppContext.BaseDirectory;
#else
            return null;
#endif
        }

        private static string GetEnvironmentVariable(string variable)
        {
            return Environment.GetEnvironmentVariable(variable);
        }

        /// <summary>
        /// Resets the current singleton instance (for testing).
        /// </summary>
        internal static void ResetInstance_ForUnitTestsOnly(Func<string> getProcessFromRunningProcess = null,
            Func<string> getExecutingAssemblyPath = null, Func<string> getAppContextBaseDirectory = null,
            Func<IEnumerable<VisualStudioInstance>> getVisualStudioInstances = null,
            Func<string, string> getEnvironmentVariable = null,
            Func<bool> runningTests = null)
        {
            s_getProcessFromRunningProcess = getProcessFromRunningProcess ?? GetProcessFromRunningProcess;
            s_getExecutingAssemblyPath = getExecutingAssemblyPath ?? GetExecutingAssemblyPath;
            s_getAppContextBaseDirectory = getAppContextBaseDirectory ?? GetAppContextBaseDirectory;
            s_getVisualStudioInstances = getVisualStudioInstances ?? VisualStudioLocationHelper.GetInstances;
            s_getEnvironmentVariable = getEnvironmentVariable ?? GetEnvironmentVariable;

            // Tests which specifically test the BuildEnvironmentHelper need it to be able to act as if it is not running tests
            s_runningTests = runningTests ?? CheckIfRunningTests;

            BuildEnvironmentHelperSingleton.s_instance = Initialize();
        }

        /// <summary>
        /// Resets the current singleton instance (for testing).
        /// </summary>
        internal static void ResetInstance_ForUnitTestsOnly(BuildEnvironment buildEnvironment)
        {
            BuildEnvironmentHelperSingleton.s_instance = buildEnvironment;
        }

        private static Func<string> s_getProcessFromRunningProcess = GetProcessFromRunningProcess;
        private static Func<string> s_getExecutingAssemblyPath = GetExecutingAssemblyPath;
        private static Func<string> s_getAppContextBaseDirectory = GetAppContextBaseDirectory;
        private static Func<IEnumerable<VisualStudioInstance>> s_getVisualStudioInstances = VisualStudioLocationHelper.GetInstances;
        private static Func<string, string> s_getEnvironmentVariable = GetEnvironmentVariable;
        private static Func<bool> s_runningTests = CheckIfRunningTests;

        private static class BuildEnvironmentHelperSingleton
        {
            // Explicit static constructor to tell C# compiler
            // not to mark type as beforefieldinit
            static BuildEnvironmentHelperSingleton()
            { }

            public static BuildEnvironment s_instance = Initialize();
        }
    }

    /// <summary>
    /// Enum which defines which environment / mode MSBuild is currently running.
    /// </summary>
    internal enum BuildEnvironmentMode
    {
        /// <summary>
        /// Running from Visual Studio directly or from MSBuild installed under an instance of Visual Studio.
        /// Toolsets and extensions will be loaded from the Visual Studio instance.
        /// </summary>
        VisualStudio,

        /// <summary>
        /// Running in a standalone toolset mode. All toolsets and extensions paths are relative to the app
        /// running and not dependent on Visual Studio. (e.g. dotnet CLI, open source clone of our repo)
        /// </summary>
        Standalone,

        /// <summary>
        /// Running without any defined toolsets. Most functionality limited. Likely will not be able to
        /// build or evaluate a project. (e.g. reference to Microsoft.*.dll without a toolset definition
        /// or Visual Studio instance installed).
        /// </summary>
        None
    }

    /// <summary>
    /// Defines the current environment for build tools.
    /// </summary>
    internal sealed class BuildEnvironment
    {
        public BuildEnvironment(BuildEnvironmentMode mode, string currentMSBuildExePath, bool runningTests, bool runningInMSBuildExe, bool runningInVisualStudio,
                string visualStudioPath)
        {
            FileInfo currentMSBuildExeFile = null;
            DirectoryInfo currentToolsDirectory = null;

            Mode = mode;
            RunningTests = runningTests;
            RunningInMSBuildExe = runningInMSBuildExe;
            RunningInVisualStudio = runningInVisualStudio;
            CurrentMSBuildExePath = currentMSBuildExePath;
            VisualStudioInstallRootDirectory = visualStudioPath;

#if !NO_FRAMEWORK_IVT
            Framework.BuildEnvironmentState.s_runningTests = runningTests;
            Framework.BuildEnvironmentState.s_runningInVisualStudio = runningInVisualStudio;
#endif

            if (!string.IsNullOrEmpty(currentMSBuildExePath))
            {
                currentMSBuildExeFile = new FileInfo(currentMSBuildExePath);
                currentToolsDirectory = currentMSBuildExeFile.Directory;

                CurrentMSBuildToolsDirectory = currentMSBuildExeFile.DirectoryName;
                CurrentMSBuildConfigurationFile = string.Concat(currentMSBuildExePath, ".config");
                MSBuildToolsDirectory32 = CurrentMSBuildToolsDirectory;
                MSBuildToolsDirectory64 = CurrentMSBuildToolsDirectory;
                MSBuildToolsDirectoryRoot = CurrentMSBuildToolsDirectory;
            }

            // We can't detect an environment, don't try to set other paths.
            if (mode == BuildEnvironmentMode.None || currentMSBuildExeFile == null || currentToolsDirectory == null)
            {
                return;
            }

            var msBuildExeName = currentMSBuildExeFile.Name;

            if (mode == BuildEnvironmentMode.VisualStudio)
            {
                // In Visual Studio, the entry-point MSBuild.exe is often from an arch-specific subfolder
                MSBuildToolsDirectoryRoot = NativeMethodsShared.ProcessorArchitecture switch
                {
                    NativeMethodsShared.ProcessorArchitectures.X86 => CurrentMSBuildToolsDirectory,
                    NativeMethodsShared.ProcessorArchitectures.X64 or NativeMethodsShared.ProcessorArchitectures.ARM64
                        => currentToolsDirectory.Parent?.FullName,
                    _ => throw new InternalErrorException("Unknown processor architecture " + NativeMethodsShared.ProcessorArchitecture),
                };
            }
            else
            {
                // In the .NET SDK, there's one copy of MSBuild.dll and it's in the root folder.
                MSBuildToolsDirectoryRoot = CurrentMSBuildToolsDirectory;

                // If we're standalone, we might not be in the SDK. Rely on folder paths at this point.
                if (string.Equals(currentToolsDirectory.Name, "amd64", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(currentToolsDirectory.Name, "arm64", StringComparison.OrdinalIgnoreCase))
                {
                    MSBuildToolsDirectoryRoot = currentToolsDirectory.Parent?.FullName;
                }
            }

            if (MSBuildToolsDirectoryRoot != null)
            {
                // Calculate potential paths to other architecture MSBuild.exe
                var potentialAmd64FromX86 = FileUtilities.CombinePaths(MSBuildToolsDirectoryRoot, "amd64", msBuildExeName);
                var potentialARM64FromX86 = FileUtilities.CombinePaths(MSBuildToolsDirectoryRoot, "arm64", msBuildExeName);

                // Check for existence of an MSBuild file. Note this is not necessary in a VS installation where we always want to
                // assume the correct layout.
                var existsCheck = mode == BuildEnvironmentMode.VisualStudio ? new Func<string, bool>(_ => true) : File.Exists;

                MSBuildToolsDirectory32 = MSBuildToolsDirectoryRoot;
                MSBuildToolsDirectory64 = existsCheck(potentialAmd64FromX86) ? Path.Combine(MSBuildToolsDirectoryRoot, "amd64") : CurrentMSBuildToolsDirectory;
#if RUNTIME_TYPE_NETCORE
                // Fall back to "current" for any architecture since .NET SDK doesn't
                // support cross-arch task invocations.
                MSBuildToolsDirectoryArm64 = existsCheck(potentialARM64FromX86) ? Path.Combine(MSBuildToolsDirectoryRoot, "arm64") : CurrentMSBuildToolsDirectory;
#else
                MSBuildToolsDirectoryArm64 = existsCheck(potentialARM64FromX86) ? Path.Combine(MSBuildToolsDirectoryRoot, "arm64") : null;
#endif
            }

            MSBuildExtensionsPath = mode == BuildEnvironmentMode.VisualStudio
                ? Path.Combine(VisualStudioInstallRootDirectory, "MSBuild")
                : MSBuildToolsDirectory32;
        }

        internal BuildEnvironmentMode Mode { get; }

        /// <summary>
        /// Gets the flag that indicates if we are running in a test harness.
        /// </summary>
        internal bool RunningTests { get; }

        /// <summary>
        /// Returns true when the entry point application is MSBuild.exe.
        /// </summary>
        internal bool RunningInMSBuildExe { get; }

        /// <summary>
        /// Returns true when the entry point application is Visual Studio.
        /// </summary>
        internal bool RunningInVisualStudio { get; }

        /// <summary>
        /// Path to the root of the MSBuild folder (in VS scenarios, <c>MSBuild\Current\bin</c>).
        /// </summary>
        internal string MSBuildToolsDirectoryRoot { get; }

        /// <summary>
        /// Path to the MSBuild 32-bit tools directory.
        /// </summary>
        internal string MSBuildToolsDirectory32 { get; }

        /// <summary>
        /// Path to the MSBuild 64-bit (AMD64) tools directory.
        /// </summary>
        internal string MSBuildToolsDirectory64 { get; }

        /// <summary>
        /// Path to the ARM64 tools directory.
        /// <see langword="null" /> if ARM64 tools are not installed.
        /// </summary>
        internal string MSBuildToolsDirectoryArm64 { get; }

        /// <summary>
        /// Path to the Sdks folder for this MSBuild instance.
        /// </summary>
        internal string MSBuildSDKsPath
        {
            get
            {
                string defaultSdkPath;

                if (VisualStudioInstallRootDirectory != null)
                {
                    // Can't use the N-argument form of Combine because it doesn't exist on .NET 3.5
                    defaultSdkPath = FileUtilities.CombinePaths(VisualStudioInstallRootDirectory, "MSBuild", "Sdks");
                }
                else
                {
                    defaultSdkPath = Path.Combine(CurrentMSBuildToolsDirectory, "Sdks");
                }

                // Allow an environment-variable override of the default SDK location
                return Environment.GetEnvironmentVariable("MSBuildSDKsPath") ?? defaultSdkPath;
            }
        }

        /// <summary>
        /// Full path to the current MSBuild configuration file.
        /// </summary>
        internal string CurrentMSBuildConfigurationFile { get; }

        /// <summary>
        /// Full path to current MSBuild.exe.
        /// <remarks>
        /// This path is likely not the current running process. We may be inside
        /// Visual Studio or a test harness. In that case this will point to the
        /// version of MSBuild found to be associated with the current environment.
        /// </remarks>
        /// </summary>
        internal string CurrentMSBuildExePath { get; private set; }

        /// <summary>
        /// Full path to the current MSBuild tools directory. This will be 32-bit unless
        /// we're executing from the 'AMD64' folder.
        /// </summary>
        internal string CurrentMSBuildToolsDirectory { get; }

        /// <summary>
        /// Path to the root Visual Studio install directory
        /// (e.g. 'C:\Program Files (x86)\Microsoft Visual Studio\Preview\Enterprise')
        /// </summary>
        internal string VisualStudioInstallRootDirectory { get; }

        /// <summary>
        /// MSBuild extensions path. On Standalone this defaults to the MSBuild folder. In
        /// VisualStudio mode this folder will be %VSINSTALLDIR%\MSBuild.
        /// </summary>
        internal string MSBuildExtensionsPath { get; set; }
    }
}
