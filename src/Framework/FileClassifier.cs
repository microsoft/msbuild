// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
#if !RUNTIME_TYPE_NETCORE
using System.Diagnostics;
using System.Text.RegularExpressions;
#endif

namespace Microsoft.Build.Framework
{
    /// <summary>
    ///     Attempts to classify project files for various purposes such as safety and performance.
    /// </summary>
    /// <remarks>
    ///     Callers of this class are responsible to respect current OS path string comparison.
    ///     <para>
    ///         The term "project files" refers to the root project file (e.g. <c>MyProject.csproj</c>) and
    ///         any other <c>.props</c> and <c>.targets</c> files it imports.
    ///     </para>
    ///     <para>
    ///         Classifications provided are:
    ///         <list type="number">
    ///             <item>
    ///                 <see cref="IsNonModifiable" /> which indicates the file is not expected to change over time,
    ///                 other than when it is first created. This is a subset of non-user-editable files and
    ///                 generally excludes generated files which can be regenerated in response to user actions.
    ///             </item>
    ///         </list>
    ///     </para>
    /// </remarks>
    internal class FileClassifier
    {
        private bool _isImmutablePathsInitialized;

        /// <summary>
        /// This event notifies subscribers when the immutable paths have been initialized.
        /// </summary>
        public event Action? OnImmutablePathsInitialized;

        /// <summary>
        ///  Tracks whether the immutable paths have been initialized.
        /// </summary>
        public bool IsImmutablePathsInitialized
        {
            get => _isImmutablePathsInitialized;
            private set
            {
                if (!_isImmutablePathsInitialized && value)
                {
                    OnImmutablePathsInitialized?.Invoke();
                }

                _isImmutablePathsInitialized = value;
            }
        }

        /// <summary>
        ///     StringComparison used for comparing paths on current OS.
        /// </summary>
        /// <remarks>
        ///     TODO: Replace RuntimeInformation.IsOSPlatform(OSPlatform.Linux) by NativeMethodsShared.OSUsesCaseSensitivePaths once it is moved out from Shared
        /// </remarks>
        private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        ///     Single, static <see cref="Lazy{T}"/> instance of shared file FileClassifier for <see cref="Shared"/> member.
        /// </summary>
        private static readonly Lazy<FileClassifier> s_sharedInstance = new(() => new FileClassifier());

        /// <summary>
        ///     Serves purpose of thread safe set of known immutable directories.
        /// </summary>
        /// <remarks>
        ///     Although <see cref="ConcurrentDictionary{TKey,TValue}"></see> is not optimal memory-wise, in this particular case it does not matter
        ///     much as the expected size of this set is ~5 and in very extreme cases less then 100.
        /// </remarks>
        private readonly ConcurrentDictionary<string, string> _knownImmutableDirectories = new();

        /// <summary>
        ///     Copy on write snapshot of <see cref="_knownImmutableDirectories"/>.
        /// </summary>
        private volatile IReadOnlyList<string> _knownImmutableDirectoriesSnapshot = [];

        /// <summary>
        ///     Copy on write snapshot of <see cref="_knownImmutableDirectories"/>, without custom logic locations (e.g. nuget cache).
        /// </summary>
        private volatile IReadOnlyList<string> _knownBuiltInLogicDirectoriesSnapshot = [];

        /// <summary>
        ///     Creates default FileClassifier which following immutable folders:
        ///     Classifications provided are:
        ///     <list type="number">
        ///         <item>Program Files\Reference Assemblies\Microsoft</item>
        ///         <item>Program Files (x86)\Reference Assemblies\Microsoft</item>
        ///         <item>Visual Studio installation root</item>
        ///     </list>
        /// </summary>
        /// <remarks>
        ///     Individual projects NuGet folders are added during project build by calling
        ///     <see cref="RegisterImmutableDirectory" />
        /// </remarks>
        public FileClassifier()
        {
            // Register Microsoft "Reference Assemblies" as immutable
            string[] programFilesEnvs = ["ProgramFiles(x86)", "ProgramW6432", "ProgramFiles(Arm)"];
            foreach (string programFilesEnv in programFilesEnvs)
            {
                string? programFiles = Environment.GetEnvironmentVariable(programFilesEnv);
                if (!string.IsNullOrEmpty(programFiles))
                {
                    RegisterImmutableDirectory(Path.Combine(programFiles, "Reference Assemblies", "Microsoft"), false);
                }
            }

#if !RUNTIME_TYPE_NETCORE
            RegisterImmutableDirectory(GetVSInstallationDirectory(), false);

            static string? GetVSInstallationDirectory()
            {
                string? dir = Environment.GetEnvironmentVariable("VSAPPIDDIR");

                if (dir != null)
                {
                    // The path provided is not the installation root, but rather the location of devenv.exe.
                    // __VSSPROPID.VSSPROPID_InstallDirectory has the same value.
                    // Failing a better way to obtain the installation root, remove that suffix.
                    // Obviously this is brittle against changes to the relative path of devenv.exe, however that seems
                    // unlikely and should be easy to work around if ever needed.
                    const string devEnvExeRelativePath = "Common7\\IDE\\";

                    if (dir.EndsWith(devEnvExeRelativePath, PathComparison))
                    {
                        dir = dir.Substring(0, dir.Length - devEnvExeRelativePath.Length);

                        return dir;
                    }
                }

                // TODO: Once BuildEnvironmentHelper makes it from Shared into Framework, rework the code bellow. Hint: implement GetVsRootFromMSBuildAssembly() in BuildEnvironmentHelper

                // Seems like MSBuild did not run from VS but from CLI.
                // Identify current process and run it
                string processName = Process.GetCurrentProcess().MainModule.FileName;
                string processFileName = Path.GetFileNameWithoutExtension(processName);

                if (string.IsNullOrEmpty(processFileName))
                {
                    return null;
                }

                string[] msBuildProcess = { "MSBUILD", "MSBUILDTASKHOST" };
                if (msBuildProcess.Any(s =>
                    processFileName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    // Check if we're in a VS installation
                    if (Regex.IsMatch(processName, $@".*\\MSBuild\\Current\\Bin\\.*MSBuild(?:TaskHost)?\.exe", RegexOptions.IgnoreCase))
                    {
                        return GetVsRootFromMSBuildAssembly(processName);
                    }
                }

                return null;

                static string GetVsRootFromMSBuildAssembly(string msBuildAssembly)
                {
                    return GetFolderAbove(msBuildAssembly,
                        Path.GetDirectoryName(msBuildAssembly)?.EndsWith(@"\amd64", StringComparison.OrdinalIgnoreCase) == true
                            ? 5
                            : 4);
                }

                static string GetFolderAbove(string path, int count = 1)
                {
                    if (count < 1)
                    {
                        return path;
                    }

                    DirectoryInfo? parent = Directory.GetParent(path);

                    while (count > 1 && parent?.Parent != null)
                    {
                        parent = parent.Parent;
                        count--;
                    }

                    return parent?.FullName ?? path;
                }
            }
#endif
        }

        /// <summary>
        ///     Shared singleton instance.
        /// </summary>
        public static FileClassifier Shared => s_sharedInstance.Value;

        /// <summary>
        ///     Try add path into set of known immutable paths.
        ///     Files under any of these folders are considered non-modifiable.
        /// </summary>
        /// <remarks>
        ///     This value is used by <see cref="IsNonModifiable" />.
        ///     Files in the NuGet package cache are not expected to change over time, once they are created.
        /// </remarks>
        private protected void RegisterImmutableDirectory(string? directory, bool isCustomLogicLocation)
        {
            if (directory?.Length > 0)
            {
                string d = EnsureTrailingSlash(directory);

                if (_knownImmutableDirectories.TryAdd(d, d))
                {
                    _knownImmutableDirectoriesSnapshot = new List<string>(_knownImmutableDirectories.Values);

                    // Add the location to the build in logic locations - but create a new readonly destination
                    if (!isCustomLogicLocation)
                    {
                        _knownBuiltInLogicDirectoriesSnapshot =
                            _knownBuiltInLogicDirectoriesSnapshot.Append(d).ToArray();
                    }
                }
            }
        }

        public void RegisterFrameworkLocations(Func<string, string?> getPropertyValue)
        {
            // Register toolset paths into list of immutable directories
            // example: C:\Windows\Microsoft.NET\Framework
            string? frameworksPathPrefix32 = GetExistingRootOrNull(getPropertyValue("MSBuildFrameworkToolsPath32")?.Trim());
            RegisterImmutableDirectory(frameworksPathPrefix32, false);
            // example: C:\Windows\Microsoft.NET\Framework64
            string? frameworksPathPrefix64 = GetExistingRootOrNull(getPropertyValue("MSBuildFrameworkToolsPath64")?.Trim());
            RegisterImmutableDirectory(frameworksPathPrefix64, false);
            // example: C:\Windows\Microsoft.NET\FrameworkArm64
            string? frameworksPathPrefixArm64 = GetExistingRootOrNull(getPropertyValue("MSBuildFrameworkToolsPathArm64")?.Trim());
            RegisterImmutableDirectory(frameworksPathPrefixArm64, false);
        }

        public void RegisterKnownImmutableLocations(Func<string, string?> getPropertyValue)
        {
            // example: C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2
            RegisterImmutableDirectory(getPropertyValue("FrameworkPathOverride")?.Trim(), false);
            // example: C:\Program Files\dotnet\
            RegisterImmutableDirectory(getPropertyValue("NetCoreRoot")?.Trim(), false);
            // example: C:\Users\<username>\.nuget\packages\
            RegisterImmutableDirectory(getPropertyValue("NuGetPackageFolders")?.Trim(), true);

            IsImmutablePathsInitialized = true;
        }

        private static string? GetExistingRootOrNull(string? path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    path = Directory.GetParent(EnsureNoTrailingSlash(path!))?.FullName;

                    if (!Directory.Exists(path))
                    {
                        path = null;
                    }
                }
                catch
                {
                    path = null;
                }
            }

            return path;
        }

        /// <summary>
        /// Ensures the path does not have a trailing slash.
        /// </summary>
        private static string EnsureNoTrailingSlash(string path)
        {
            path = FixFilePath(path);
            if (EndsWithSlash(path))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return path;
        }

        private static string FixFilePath(string path)
        {
            return string.IsNullOrEmpty(path) || Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/'); // .Replace("//", "/");
        }

        /// <summary>
        /// Indicates if the given file-spec ends with a slash.
        /// </summary>
        /// <param name="fileSpec">The file spec.</param>
        /// <returns>true, if file-spec has trailing slash</returns>
        private static bool EndsWithSlash(string fileSpec)
        {
            return (fileSpec.Length > 0) && IsSlash(fileSpec[fileSpec.Length - 1]);
        }

        /// <summary>
        /// Indicates if the given character is a slash.
        /// </summary>
        /// <param name="c"></param>
        /// <returns>true, if slash</returns>
        private static bool IsSlash(char c)
        {
            return (c == Path.DirectorySeparatorChar) || (c == Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSlash(string fileSpec)
        {
            if (fileSpec.Length >= 1 && !EndsWithSlash(fileSpec))
            {
                fileSpec += Path.DirectorySeparatorChar;
            }

            return fileSpec;
        }

        /// <summary>
        ///     Gets whether a file is expected to be produced as a controlled msbuild logic library ( - produced by Microsoft).
        /// </summary>
        /// <param name="filePath">The path to the file to test.</param>
        /// <returns><see langword="true" /> if the file is supposed to be part of the common targets libraries set.<see langword="false" />.</returns>
        public bool IsBuiltInLogic(string filePath)
            => IsInLocationList(filePath, _knownBuiltInLogicDirectoriesSnapshot);

        /// <summary>
        ///     Gets whether a file is expected to not be modified in place on disk once it has been created.
        /// </summary>
        /// <param name="filePath">The path to the file to test.</param>
        /// <returns><see langword="true" /> if the file is non-modifiable, otherwise <see langword="false" />.</returns>
        public bool IsNonModifiable(string filePath)
            => IsInLocationList(filePath, _knownImmutableDirectoriesSnapshot);

        private static bool IsInLocationList(string filePath, IReadOnlyList<string> locations)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }

            // Avoid a foreach loop or linq.Any because they allocate.
            // Copy _knownImmutableDirectoriesSnapshot into a local variable so other threads can't modify it during enumeration.
            for (int i = 0; i < locations.Count; i++)
            {
                if (filePath.StartsWith(locations[i], PathComparison))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
