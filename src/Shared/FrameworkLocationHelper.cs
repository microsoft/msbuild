// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
#if FEATURE_WIN32_REGISTRY
using Microsoft.Win32;
#endif

using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Used to specify the targeted bitness of the .NET Framework for some methods of FrameworkLocationHelper
    /// </summary>
    internal enum DotNetFrameworkArchitecture
    {
        /// <summary>
        /// Indicates the .NET Framework that is currently being run under.
        /// </summary>
        Current = 0,

        /// <summary>
        /// Indicates the 32-bit .NET Framework
        /// </summary>
        Bitness32 = 1,

        /// <summary>
        /// Indicates the 64-bit .NET Framework
        /// </summary>
        Bitness64 = 2,
    }

    /// <summary>
    /// FrameworkLocationHelper provides utility methods for locating .NET Framework and .NET Framework SDK directories and files
    /// </summary>
    internal static class FrameworkLocationHelper
    {
        #region Constants

        internal const string dotNetFrameworkIdentifier = ".NETFramework";

        // .net versions.
        internal static readonly Version dotNetFrameworkVersion11 = new Version(1, 1);
        internal static readonly Version dotNetFrameworkVersion20 = new Version(2, 0);
        internal static readonly Version dotNetFrameworkVersion30 = new Version(3, 0);
        internal static readonly Version dotNetFrameworkVersion35 = new Version(3, 5);
        internal static readonly Version dotNetFrameworkVersion40 = new Version(4, 0);
        internal static readonly Version dotNetFrameworkVersion45 = new Version(4, 5);
        internal static readonly Version dotNetFrameworkVersion451 = new Version(4, 5, 1);
        internal static readonly Version dotNetFrameworkVersion452 = new Version(4, 5, 2);
        internal static readonly Version dotNetFrameworkVersion46 = new Version(4, 6);
        internal static readonly Version dotNetFrameworkVersion461 = new Version(4, 6, 1);
        internal static readonly Version dotNetFrameworkVersion462 = new Version(4, 6, 2);
        internal static readonly Version dotNetFrameworkVersion47 = new Version(4, 7);
        internal static readonly Version dotNetFrameworkVersion471 = new Version(4, 7, 1);
        internal static readonly Version dotNetFrameworkVersion472 = new Version(4, 7, 2);
        internal static readonly Version dotNetFrameworkVersion48 = new Version(4, 8);
        internal static readonly Version dotNetFrameworkVersion481 = new Version(4, 8, 1);

        // visual studio versions.
        internal static readonly Version visualStudioVersion100 = new Version(10, 0);
        internal static readonly Version visualStudioVersion110 = new Version(11, 0);
        internal static readonly Version visualStudioVersion120 = new Version(12, 0);
        internal static readonly Version visualStudioVersion140 = new Version(14, 0);
        internal static readonly Version visualStudioVersion150 = new Version(15, 0);
        internal static readonly Version visualStudioVersion160 = new Version(16, 0);
        internal static readonly Version visualStudioVersion170 = new Version(17, 0);

        // keep this up-to-date; always point to the latest visual studio version.
        internal static readonly Version visualStudioVersionLatest = visualStudioVersion170;

        private const string dotNetFrameworkRegistryPath = "SOFTWARE\\Microsoft\\.NETFramework";
        private const string dotNetFrameworkSetupRegistryPath = "SOFTWARE\\Microsoft\\NET Framework Setup\\NDP";
        private const string dotNetFrameworkSetupRegistryInstalledName = "Install";

        internal const string fullDotNetFrameworkRegistryKey = "HKEY_LOCAL_MACHINE\\" + dotNetFrameworkRegistryPath;
#if FEATURE_WIN32_REGISTRY
        private const string dotNetFrameworkAssemblyFoldersRegistryPath = dotNetFrameworkRegistryPath + "\\AssemblyFolders";
        private const string referenceAssembliesRegistryValueName = "All Assemblies In";
#endif // FEATURE_WIN32_REGISTRY
        internal const string dotNetFrameworkSdkInstallKeyValueV11 = "SDKInstallRootv1.1";
        internal static string dotNetFrameworkVersionFolderPrefixV11 = NativeMethodsShared.IsWindows ? "v1.1" : "1.1"; // v1.1 is for Everett.
        private const string dotNetFrameworkVersionV11 = "v1.1.4322"; // full Everett version to pass to NativeMethodsShared.GetRequestedRuntimeInfo().
        private const string dotNetFrameworkRegistryKeyV11 = dotNetFrameworkSetupRegistryPath + "\\" + dotNetFrameworkVersionV11;

        internal const string dotNetFrameworkSdkInstallKeyValueV20 = "SDKInstallRootv2.0";
        internal static string dotNetFrameworkVersionFolderPrefixV20 = NativeMethodsShared.IsWindows ? "v2.0" : "2.0"; // v2.0 is for Whidbey.
        private const string dotNetFrameworkVersionV20 = "v2.0.50727"; // full Whidbey version to pass to NativeMethodsShared.GetRequestedRuntimeInfo().
        private const string dotNetFrameworkRegistryKeyV20 = dotNetFrameworkSetupRegistryPath + "\\" + dotNetFrameworkVersionV20;

        internal static string dotNetFrameworkVersionFolderPrefixV30 = NativeMethodsShared.IsWindows ? "v3.0" : "3.0"; // v3.0 is for WinFx.
        private static string s_dotNetFrameworkRegistryKeyV30 = dotNetFrameworkSetupRegistryPath + "\\" + dotNetFrameworkVersionFolderPrefixV30 + "\\Setup";

#if FEATURE_WIN32_REGISTRY
        private const string fallbackDotNetFrameworkSdkRegistryInstallPath = "SOFTWARE\\Microsoft\\Microsoft SDKs\\Windows";
#endif // FEATURE_WIN32_REGISTRY
        internal const string fallbackDotNetFrameworkSdkInstallKeyValue = "CurrentInstallFolder";

        private const string dotNetFrameworkSdkRegistryPathForV35ToolsOnWinSDK70A = @"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v7.0A\WinSDK-NetFx35Tools-x86";
        private const string fullDotNetFrameworkSdkRegistryPathForV35ToolsOnWinSDK70A = "HKEY_LOCAL_MACHINE\\" + dotNetFrameworkSdkRegistryPathForV35ToolsOnWinSDK70A;

        private const string dotNetFrameworkSdkRegistryPathForV35ToolsOnManagedToolsSDK80A = @"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v8.0A\WinSDK-NetFx35Tools-x86";
        private const string fullDotNetFrameworkSdkRegistryPathForV35ToolsOnManagedToolsSDK80A = "HKEY_LOCAL_MACHINE\\" + dotNetFrameworkSdkRegistryPathForV35ToolsOnManagedToolsSDK80A;

        internal static string dotNetFrameworkVersionFolderPrefixV35 = NativeMethodsShared.IsWindows ? "v3.5" : "3.5"; // v3.5 is for Orcas.
        private static string s_dotNetFrameworkRegistryKeyV35 = dotNetFrameworkSetupRegistryPath + "\\" + dotNetFrameworkVersionFolderPrefixV35;

        internal const string fullDotNetFrameworkSdkRegistryKeyV35OnVS10 = fullDotNetFrameworkSdkRegistryPathForV35ToolsOnWinSDK70A;
        internal const string fullDotNetFrameworkSdkRegistryKeyV35OnVS11 = fullDotNetFrameworkSdkRegistryPathForV35ToolsOnManagedToolsSDK80A;

        internal static string dotNetFrameworkVersionFolderPrefixV40 = NativeMethodsShared.IsWindows ? "v4.0" : "4.0";
        internal static string dotNetFrameworkVersionFolderPrefixV45 = NativeMethodsShared.IsWindows ? "v4.5" : "4.5";

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Path to the ToolsVersion definitions in the registry
        /// </summary>
        private const string ToolsVersionsRegistryPath = @"SOFTWARE\Microsoft\MSBuild\ToolsVersions";
#endif // FEATURE_WIN32_REGISTRY

        #endregion // Constants

        #region Static member variables

        /// <summary>
        /// By default when a root path is not specified we would like to use the program files directory \ reference assemblies\framework as the root location
        /// to generate the reference assembly paths from.
        /// </summary>
        internal static readonly string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        internal static readonly string programFiles32 = GenerateProgramFiles32();
        internal static readonly string programFiles64 = GenerateProgramFiles64();
        internal static readonly string programFilesReferenceAssemblyLocation = GenerateProgramFilesReferenceAssemblyRoot();

        private static string s_fallbackDotNetFrameworkSdkInstallPath;

        private static string s_pathToV35ToolsInFallbackDotNetFrameworkSdk;

        private static string s_pathToV4ToolsInFallbackDotNetFrameworkSdk;

        /// <summary>
        /// List the supported .net versions.
        /// </summary>
        private static DotNetFrameworkSpec[] DotNetFrameworkSpecs() =>
        [
            // v1.1
            new DotNetFrameworkSpecLegacy(
                dotNetFrameworkVersion11,
                dotNetFrameworkRegistryKeyV11,
                dotNetFrameworkSetupRegistryInstalledName,
                dotNetFrameworkVersionFolderPrefixV11,
                dotNetFrameworkSdkInstallKeyValueV11,
                hasMSBuild: false),

            // v2.0
            new DotNetFrameworkSpecLegacy(
                dotNetFrameworkVersion20,
                dotNetFrameworkRegistryKeyV20,
                dotNetFrameworkSetupRegistryInstalledName,
                dotNetFrameworkVersionFolderPrefixV20,
                dotNetFrameworkSdkInstallKeyValueV20,
                hasMSBuild: true),

            // v3.0
            new DotNetFrameworkSpecV3(
                dotNetFrameworkVersion30,
                s_dotNetFrameworkRegistryKeyV30,
                "InstallSuccess",
                dotNetFrameworkVersionFolderPrefixV30,
                null,
                null,
                hasMSBuild: false),

            // v3.5
            new DotNetFrameworkSpecV3(
                dotNetFrameworkVersion35,
                s_dotNetFrameworkRegistryKeyV35,
                dotNetFrameworkSetupRegistryInstalledName,
                dotNetFrameworkVersionFolderPrefixV35,
                "WinSDK-NetFx35Tools-x86",
                "InstallationFolder",
                hasMSBuild: true),

            // v4.0
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion40, visualStudioVersion100),

            // v4.5
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion45, visualStudioVersion110),

            // v4.5.1
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion451, visualStudioVersion120),

            // v4.5.2
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion452, visualStudioVersion120),

            // v4.6
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion46, visualStudioVersion140),

            // v4.6.1
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion461, visualStudioVersion140),

            // v4.6.2
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion462, visualStudioVersion150),

            // v4.7
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion47, visualStudioVersion150),

            // v4.7.1
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion471, visualStudioVersion150),

            // v4.7.2
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion472, visualStudioVersion150),

            // v4.8
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion48, visualStudioVersion150),

            // v4.8.1
            CreateDotNetFrameworkSpecForV4(dotNetFrameworkVersion481, visualStudioVersion170),
        ];

        /// <summary>
        /// List the supported visual studio versions.
        /// </summary>
        /// <remarks>
        /// The items must be ordered by the version, because some methods depend on that fact to find the previous visual studio version.
        /// </remarks>
        private static readonly Lazy<VisualStudioSpec[]> VisualStudioSpecs = new(() =>
        [
            // VS10
            new VisualStudioSpec(visualStudioVersion100, "Windows\\v7.0A", null, null,
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
            ]),

            // VS11
            new VisualStudioSpec(visualStudioVersion110, "Windows\\v8.0A", "v8.0", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
            ]),

            // VS12
            new VisualStudioSpec(visualStudioVersion120, "Windows\\v8.1A", "v8.1", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
                dotNetFrameworkVersion451,
                dotNetFrameworkVersion452,
            ]),

            // VS14
            new VisualStudioSpec(visualStudioVersion140, "NETFXSDK\\{0}", "v8.1", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
                dotNetFrameworkVersion451,
                dotNetFrameworkVersion452,
                dotNetFrameworkVersion46,
                dotNetFrameworkVersion461,
            ]),

            // VS15
            new VisualStudioSpec(visualStudioVersion150, "NETFXSDK\\{0}", "v8.1", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
                dotNetFrameworkVersion451,
                dotNetFrameworkVersion452,
                dotNetFrameworkVersion46,
                dotNetFrameworkVersion461,
                dotNetFrameworkVersion462,
                dotNetFrameworkVersion47,
                dotNetFrameworkVersion471,
                dotNetFrameworkVersion472,
                dotNetFrameworkVersion48,
            ]),

            // VS16
            new VisualStudioSpec(visualStudioVersion160, "NETFXSDK\\{0}", "v10.0", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
                dotNetFrameworkVersion451,
                dotNetFrameworkVersion452,
                dotNetFrameworkVersion46,
                dotNetFrameworkVersion461,
                dotNetFrameworkVersion462,
                dotNetFrameworkVersion47,
                dotNetFrameworkVersion471,
                dotNetFrameworkVersion472,
                dotNetFrameworkVersion48,
            ]),

            // VS17
            new VisualStudioSpec(visualStudioVersion170, "NETFXSDK\\{0}", "v10.0", "InstallationFolder",
            [
                dotNetFrameworkVersion11,
                dotNetFrameworkVersion20,
                dotNetFrameworkVersion35,
                dotNetFrameworkVersion40,
                dotNetFrameworkVersion45,
                dotNetFrameworkVersion451,
                dotNetFrameworkVersion452,
                dotNetFrameworkVersion46,
                dotNetFrameworkVersion461,
                dotNetFrameworkVersion462,
                dotNetFrameworkVersion47,
                dotNetFrameworkVersion471,
                dotNetFrameworkVersion472,
                dotNetFrameworkVersion48,
                dotNetFrameworkVersion481,
            ]),
        ]);

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Define explicit fallback rules for the request to get path of .net framework sdk tools folder.
        /// The default rule is fallback to previous VS. However, there are some special cases that need
        /// explicit rules, i.e. v4.5.1 on VS12 fallbacks to v4.5 on VS12.
        /// </summary>
        /// <remarks>
        /// The rules are maintained in a 2-dimensions array. Each row defines a rule. The first column
        /// defines the trigger condition. The second column defines the fallback .net and VS versions.
        /// </remarks>
        private static readonly (Version, Version)[,] s_explicitFallbackRulesForPathToDotNetFrameworkSdkTools =
        {
            // VS12
            { (dotNetFrameworkVersion451, visualStudioVersion120), (dotNetFrameworkVersion45, visualStudioVersion120) },
            { (dotNetFrameworkVersion452, visualStudioVersion120), (dotNetFrameworkVersion451, visualStudioVersion120) },

            // VS14
            { (dotNetFrameworkVersion451, visualStudioVersion140), (dotNetFrameworkVersion45, visualStudioVersion140) },
            { (dotNetFrameworkVersion452, visualStudioVersion140), (dotNetFrameworkVersion451, visualStudioVersion140) },
            { (dotNetFrameworkVersion46, visualStudioVersion140), (dotNetFrameworkVersion451, visualStudioVersion140) },
            { (dotNetFrameworkVersion461, visualStudioVersion140), (dotNetFrameworkVersion46, visualStudioVersion140) },

            // VS15
            { (dotNetFrameworkVersion451, visualStudioVersion150), (dotNetFrameworkVersion45, visualStudioVersion150) },
            { (dotNetFrameworkVersion452, visualStudioVersion150), (dotNetFrameworkVersion451, visualStudioVersion150) },
            { (dotNetFrameworkVersion46, visualStudioVersion150), (dotNetFrameworkVersion451, visualStudioVersion150) },
            { (dotNetFrameworkVersion461, visualStudioVersion150), (dotNetFrameworkVersion46, visualStudioVersion150) },
            { (dotNetFrameworkVersion462, visualStudioVersion150), (dotNetFrameworkVersion461, visualStudioVersion150) },
            { (dotNetFrameworkVersion47, visualStudioVersion150), (dotNetFrameworkVersion462, visualStudioVersion150) },
            { (dotNetFrameworkVersion471, visualStudioVersion150), (dotNetFrameworkVersion47, visualStudioVersion150) },
            { (dotNetFrameworkVersion472, visualStudioVersion150), (dotNetFrameworkVersion471, visualStudioVersion150) },
            { (dotNetFrameworkVersion48, visualStudioVersion150), (dotNetFrameworkVersion472, visualStudioVersion150) },

            // VS16
            { (dotNetFrameworkVersion451, visualStudioVersion160), (dotNetFrameworkVersion45, visualStudioVersion160) },
            { (dotNetFrameworkVersion452, visualStudioVersion160), (dotNetFrameworkVersion451, visualStudioVersion160) },
            { (dotNetFrameworkVersion46, visualStudioVersion160), (dotNetFrameworkVersion451, visualStudioVersion160) },
            { (dotNetFrameworkVersion461, visualStudioVersion160), (dotNetFrameworkVersion46, visualStudioVersion160) },
            { (dotNetFrameworkVersion462, visualStudioVersion160), (dotNetFrameworkVersion461, visualStudioVersion160) },
            { (dotNetFrameworkVersion47, visualStudioVersion160), (dotNetFrameworkVersion462, visualStudioVersion160) },
            { (dotNetFrameworkVersion471, visualStudioVersion160), (dotNetFrameworkVersion47, visualStudioVersion160) },
            { (dotNetFrameworkVersion472, visualStudioVersion160), (dotNetFrameworkVersion471, visualStudioVersion160) },
            { (dotNetFrameworkVersion48, visualStudioVersion160), (dotNetFrameworkVersion472, visualStudioVersion160) },

            // VS 17
            { (dotNetFrameworkVersion451, visualStudioVersion170), (dotNetFrameworkVersion45, visualStudioVersion170) },
            { (dotNetFrameworkVersion452, visualStudioVersion170), (dotNetFrameworkVersion451, visualStudioVersion170) },
            { (dotNetFrameworkVersion46, visualStudioVersion170), (dotNetFrameworkVersion451, visualStudioVersion170) },
            { (dotNetFrameworkVersion461, visualStudioVersion170), (dotNetFrameworkVersion46, visualStudioVersion170) },
            { (dotNetFrameworkVersion462, visualStudioVersion170), (dotNetFrameworkVersion461, visualStudioVersion170) },
            { (dotNetFrameworkVersion47, visualStudioVersion170), (dotNetFrameworkVersion462, visualStudioVersion170) },
            { (dotNetFrameworkVersion471, visualStudioVersion170), (dotNetFrameworkVersion47, visualStudioVersion170) },
            { (dotNetFrameworkVersion472, visualStudioVersion170), (dotNetFrameworkVersion471, visualStudioVersion170) },
            { (dotNetFrameworkVersion48, visualStudioVersion170), (dotNetFrameworkVersion472, visualStudioVersion170) },
            { (dotNetFrameworkVersion481, visualStudioVersion170), (dotNetFrameworkVersion48, visualStudioVersion170) },
        };
#endif // FEATURE_WIN32_REGISTRY

        private static readonly Lazy<IReadOnlyDictionary<Version, DotNetFrameworkSpec>> DotNetFrameworkSpecDict = new(() => DotNetFrameworkSpecs().ToDictionary(spec => spec.Version));
        private static readonly Lazy<IReadOnlyDictionary<Version, VisualStudioSpec>> VisualStudioSpecDict = new(() => VisualStudioSpecs.Value.ToDictionary(spec => spec.Version));

        #endregion // Static member variables

        #region Static properties

        internal static string PathToDotNetFrameworkV11
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV11)
                           : GetPathToDotNetFrameworkV11(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkV20
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV20)
                           : GetPathToDotNetFrameworkV20(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkV30
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV30)
                           : GetPathToDotNetFrameworkV30(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkV35
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV35)
                           : GetPathToDotNetFrameworkV35(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkV40
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV40)
                           : GetPathToDotNetFrameworkV40(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkV45
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV45)
                           : GetPathToDotNetFrameworkV45(DotNetFrameworkArchitecture.Current);
            }
        }

        internal static string PathToDotNetFrameworkSdkV11
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV11)
                           : GetPathToDotNetFrameworkSdkTools(dotNetFrameworkVersion11, visualStudioVersionLatest);
            }
        }

        internal static string PathToDotNetFrameworkSdkV20
        {
            get
            {
                return NativeMethodsShared.IsUnixLike
                           ? Path.Combine(NativeMethodsShared.FrameworkBasePath, dotNetFrameworkVersionFolderPrefixV20)
                           : GetPathToDotNetFrameworkSdkTools(dotNetFrameworkVersion20, visualStudioVersionLatest);
            }
        }

        /// <summary>
        /// Because there is no longer a strong 1:1 mapping between FX versions and SDK
        /// versions, if we're unable to locate the desired SDK version, we will try to
        /// use whichever SDK version is installed by looking at the key pointing to the
        /// "latest" version.
        ///
        /// This isn't ideal, but it will allow our tasks to function on any of several
        /// related SDKs even if they don't have exactly the same versions.
        ///
        /// NOTE:  This returns the path to the root of the fallback SDK
        /// </summary>
        private static string FallbackDotNetFrameworkSdkInstallPath
        {
            get
            {
                if (s_fallbackDotNetFrameworkSdkInstallPath == null)
                {
                    // For non-Windows just get the current Framework path
                    if (!NativeMethodsShared.IsWindows)
                    {
                        s_fallbackDotNetFrameworkSdkInstallPath = NativeMethodsShared.FrameworkCurrentPath;
                    }
#if FEATURE_WIN32_REGISTRY
                    else
                    {
                        s_fallbackDotNetFrameworkSdkInstallPath =
                            FindRegistryValueUnderKey(
                                fallbackDotNetFrameworkSdkRegistryInstallPath,
                                fallbackDotNetFrameworkSdkInstallKeyValue);

                        if (EnvironmentUtilities.Is64BitProcess && s_fallbackDotNetFrameworkSdkInstallPath == null)
                        {
                            // Since we're 64-bit, what we just checked was the 64-bit fallback key -- so now let's
                            // check the 32-bit one too, just in case.
                            s_fallbackDotNetFrameworkSdkInstallPath =
                                FindRegistryValueUnderKey(
                                    fallbackDotNetFrameworkSdkRegistryInstallPath,
                                    fallbackDotNetFrameworkSdkInstallKeyValue,
                                    RegistryView.Registry32);
                        }
                    }
#endif
                }

                return s_fallbackDotNetFrameworkSdkInstallPath;
            }
        }

        /// <summary>
        /// Because there is no longer a strong 1:1 mapping between FX versions and SDK
        /// versions, if we're unable to locate the desired SDK version, we will try to
        /// use whichever SDK version is installed by looking at the key pointing to the
        /// "latest" version.
        ///
        /// This isn't ideal, but it will allow our tasks to function on any of several
        /// related SDKs even if they don't have exactly the same versions.
        ///
        /// NOTE:  This explicitly returns the path to the 3.5 tools (bin) under the fallback
        /// SDK, to match the data we're pulling from the registry now.
        /// </summary>
        private static string PathToV35ToolsInFallbackDotNetFrameworkSdk
        {
            get
            {
                if (s_pathToV35ToolsInFallbackDotNetFrameworkSdk == null)
                {
                    if (FallbackDotNetFrameworkSdkInstallPath != null)
                    {
                        if (NativeMethodsShared.IsWindows)
                        {
                            var endsWithASlash =
                                FallbackDotNetFrameworkSdkInstallPath.EndsWith(
                                    Path.DirectorySeparatorChar.ToString(),
                                    StringComparison.Ordinal);

                            s_pathToV35ToolsInFallbackDotNetFrameworkSdk =
                                Path.Combine(FallbackDotNetFrameworkSdkInstallPath, "bin");

                            // Path.Combine leaves no trailing slash, so if we had one before, be sure to add it back in
                            if (endsWithASlash)
                            {
                                s_pathToV35ToolsInFallbackDotNetFrameworkSdk += Path.DirectorySeparatorChar;
                            }
                        }
                        else
                        {
                            s_pathToV35ToolsInFallbackDotNetFrameworkSdk = FallbackDotNetFrameworkSdkInstallPath;
                        }
                    }
                }

                return s_pathToV35ToolsInFallbackDotNetFrameworkSdk;
            }
        }

        /// <summary>
        /// Because there is no longer a strong 1:1 mapping between FX versions and SDK
        /// versions, if we're unable to locate the desired SDK version, we will try to
        /// use whichever SDK version is installed by looking at the key pointing to the
        /// "latest" version.
        ///
        /// This isn't ideal, but it will allow our tasks to function on any of several
        /// related SDKs even if they don't have exactly the same versions.
        ///
        /// NOTE:  This explicitly returns the path to the 4.X tools (bin\NetFX 4.0 Tools)
        /// under the fallback SDK, to match the data we're pulling from the registry now.
        /// </summary>
        private static string PathToV4ToolsInFallbackDotNetFrameworkSdk
        {
            get
            {
                if (s_pathToV4ToolsInFallbackDotNetFrameworkSdk == null)
                {
                    if (FallbackDotNetFrameworkSdkInstallPath != null)
                    {
                        if (NativeMethodsShared.IsWindows)
                        {
                            bool endsWithASlash = FallbackDotNetFrameworkSdkInstallPath.EndsWith(
                                "\\",
                                StringComparison.Ordinal);

                            s_pathToV4ToolsInFallbackDotNetFrameworkSdk = Path.Combine(FallbackDotNetFrameworkSdkInstallPath, "bin", "NetFX 4.0 Tools");

                            // Path.Combine leaves no trailing slash, so if we had one before, be sure to add it back in
                            if (endsWithASlash)
                            {
                                s_pathToV4ToolsInFallbackDotNetFrameworkSdk += "\\";
                            }
                        }
                        else
                        {
                            s_pathToV4ToolsInFallbackDotNetFrameworkSdk = FallbackDotNetFrameworkSdkInstallPath;
                        }
                    }
                }

                return s_pathToV4ToolsInFallbackDotNetFrameworkSdk;
            }
        }

        #endregion // Static properties

        #region Internal methods

        internal static string GetDotNetFrameworkSdkRootRegistryKey(Version dotNetFrameworkVersion, Version visualStudioVersion)
        {
            RedirectVersionsIfNecessary(ref dotNetFrameworkVersion, ref visualStudioVersion);

            var dotNetFrameworkSpec = GetDotNetFrameworkSpec(dotNetFrameworkVersion);
            var visualStudioSpec = GetVisualStudioSpec(visualStudioVersion);
            ErrorUtilities.VerifyThrowArgument(visualStudioSpec.SupportedDotNetFrameworkVersions.Contains(dotNetFrameworkVersion), "FrameworkLocationHelper.UnsupportedFrameworkVersion", dotNetFrameworkVersion);
            return dotNetFrameworkSpec.GetDotNetFrameworkSdkRootRegistryKey(visualStudioSpec);
        }

        internal static string GetDotNetFrameworkSdkInstallKeyValue(Version dotNetFrameworkVersion, Version visualStudioVersion)
        {
            RedirectVersionsIfNecessary(ref dotNetFrameworkVersion, ref visualStudioVersion);

            var dotNetFrameworkSpec = GetDotNetFrameworkSpec(dotNetFrameworkVersion);
            var visualStudioSpec = GetVisualStudioSpec(visualStudioVersion);
            ErrorUtilities.VerifyThrowArgument(visualStudioSpec.SupportedDotNetFrameworkVersions.Contains(dotNetFrameworkVersion), "FrameworkLocationHelper.UnsupportedFrameworkVersion", dotNetFrameworkVersion);
            return dotNetFrameworkSpec.DotNetFrameworkSdkRegistryInstallationFolderName;
        }

        internal static string GetDotNetFrameworkVersionFolderPrefix(Version dotNetFrameworkVersion)
        {
            return GetDotNetFrameworkSpec(dotNetFrameworkVersion).DotNetFrameworkFolderPrefix;
        }

        internal static string GetPathToWindowsSdk(Version dotNetFrameworkVersion)
        {
            return GetDotNetFrameworkSpec(dotNetFrameworkVersion).GetPathToWindowsSdk();
        }

        internal static string GetPathToDotNetFrameworkReferenceAssemblies(Version dotNetFrameworkVersion)
        {
            return GetDotNetFrameworkSpec(dotNetFrameworkVersion).GetPathToDotNetFrameworkReferenceAssemblies();
        }

        internal static string GetPathToDotNetFrameworkSdkTools(Version dotNetFrameworkVersion, Version visualStudioVersion)
        {
            RedirectVersionsIfNecessary(ref dotNetFrameworkVersion, ref visualStudioVersion);

            var dotNetFrameworkSpec = GetDotNetFrameworkSpec(dotNetFrameworkVersion);
            var visualStudioSpec = GetVisualStudioSpec(visualStudioVersion);
            ErrorUtilities.VerifyThrowArgument(visualStudioSpec.SupportedDotNetFrameworkVersions.Contains(dotNetFrameworkVersion), "FrameworkLocationHelper.UnsupportedFrameworkVersion", dotNetFrameworkVersion);
            return dotNetFrameworkSpec.GetPathToDotNetFrameworkSdkTools(visualStudioSpec);
        }

        internal static string GetPathToDotNetFrameworkSdk(Version dotNetFrameworkVersion, Version visualStudioVersion)
        {
            RedirectVersionsIfNecessary(ref dotNetFrameworkVersion, ref visualStudioVersion);

            var dotNetFrameworkSpec = GetDotNetFrameworkSpec(dotNetFrameworkVersion);
            var visualStudioSpec = GetVisualStudioSpec(visualStudioVersion);
            ErrorUtilities.VerifyThrowArgument(visualStudioSpec.SupportedDotNetFrameworkVersions.Contains(dotNetFrameworkVersion), "FrameworkLocationHelper.UnsupportedFrameworkVersion", dotNetFrameworkVersion);
            return dotNetFrameworkSpec.GetPathToDotNetFrameworkSdk(visualStudioSpec);
        }

        internal static string GetPathToDotNetFrameworkV11(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion11, architecture);
        }

        internal static string GetPathToDotNetFrameworkV20(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion20, architecture);
        }

        internal static string GetPathToDotNetFrameworkV30(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion30, architecture);
        }

        internal static string GetPathToDotNetFrameworkV35(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion35, architecture);
        }

        internal static string GetPathToDotNetFrameworkV40(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion40, architecture);
        }

        internal static string GetPathToDotNetFrameworkV45(DotNetFrameworkArchitecture architecture)
        {
            return GetPathToDotNetFramework(dotNetFrameworkVersion45, architecture);
        }

        internal static string GetPathToDotNetFramework(Version version)
        {
            return GetPathToDotNetFramework(version, DotNetFrameworkArchitecture.Current);
        }

        internal static string GetPathToDotNetFramework(Version version, DotNetFrameworkArchitecture architecture)
        {
            return GetDotNetFrameworkSpec(version).GetPathToDotNetFramework(architecture);
        }

#if FEATURE_INSTALLED_MSBUILD
        /// <summary>
        /// Check the registry key and value to see if the .net Framework is installed on the machine.
        /// </summary>
        /// <param name="registryEntryToCheckInstall">Registry path to look for the value</param>
        /// <param name="registryValueToCheckInstall">Key to retrieve the value from</param>
        /// <returns>True if the registry key is 1 false if it is not there. This method also return true if the complus enviornment variables are set.</returns>
        private static bool CheckForFrameworkInstallation(string registryEntryToCheckInstall, string registryValueToCheckInstall)
        {
            // Get the complus install root and version
            string complusInstallRoot = Environment.GetEnvironmentVariable("COMPLUS_INSTALLROOT");
            string complusVersion = Environment.GetEnvironmentVariable("COMPLUS_VERSION");

            // Complus is not set we need to make sure the framework we are targeting is installed. Check the registry key before trying to find the directory.
            // If complus is set then we will return that directory as the framework directory, there is no need to check the registry value for the framework and it may not even be installed.
            if (String.IsNullOrEmpty(complusInstallRoot) && String.IsNullOrEmpty(complusVersion))
            {
                if (NativeMethodsShared.IsWindows)
                {
                    // If the registry entry is 1 then the framework is installed. Go ahead and find the directory. If it is not 1 then the framework is not installed, return null.
                    return String.Equals("1", FindRegistryValueUnderKey(registryEntryToCheckInstall, registryValueToCheckInstall), StringComparison.OrdinalIgnoreCase);
                }
                // False for non-windows since there is nothing in the registry
                else
                {
                    return false;
                }
            }

            return true;
        }
#endif

        /// <summary>
        /// Heuristic that first considers the current runtime path and then searches the base of that path for the given
        /// frameworks version.
        /// </summary>
        /// <param name="currentRuntimePath">The path to the runtime that is currently executing.</param>
        /// <param name="prefix">Should be something like 'v1.2' that indicates the runtime version we want.</param>
        /// <param name="directoryExists">Function that checks if directory exists.</param>
        /// <param name="getDirectories">Delegate to method that can return filesystem entries.</param>
        /// <param name="architecture">.NET framework architecture</param>
        /// <returns>Will return 'null' if there is no target frameworks on this machine.</returns>
        internal static string FindDotNetFrameworkPath(
            string currentRuntimePath,
            string prefix,
            DirectoryExists directoryExists,
            GetDirectories getDirectories,
            DotNetFrameworkArchitecture architecture)
        {
            if (!NativeMethodsShared.IsWindows)
            {
                if (!string.IsNullOrEmpty(prefix)
                    && prefix.Substring(0, 1).Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = prefix.Substring(1);
                }

                var frameworkPath = Path.Combine(NativeMethodsShared.FrameworkBasePath, prefix ?? string.Empty);
                return directoryExists(frameworkPath) ? frameworkPath : null;
            }

            // If the COMPLUS variables are set, they override everything -- that's the directory we want.
            string complusInstallRoot = Environment.GetEnvironmentVariable("COMPLUS_INSTALLROOT");
            string complusVersion = Environment.GetEnvironmentVariable("COMPLUS_VERSION");

            if (!String.IsNullOrEmpty(complusInstallRoot) && !String.IsNullOrEmpty(complusVersion))
            {
                return Path.Combine(complusInstallRoot, complusVersion);
            }

            // If the current runtime starts with correct prefix, then this is the runtime we want to use.
            // However, only if we're requesting current architecture -- otherwise, the base path may be different, so we'll need to look it up.
            string leaf = Path.GetFileName(currentRuntimePath);
            if (leaf.StartsWith(prefix, StringComparison.Ordinal) && architecture == DotNetFrameworkArchitecture.Current)
            {
                return currentRuntimePath;
            }

            // We haven't managed to use exact methods to locate the FX, so
            // search for the correct path with a heuristic.
            string baseLocation = Path.GetDirectoryName(currentRuntimePath);
            string searchPattern = prefix + "*";

            int indexOfFramework64 = baseLocation.IndexOf("Framework64", StringComparison.OrdinalIgnoreCase);

            if (indexOfFramework64 != -1 && architecture == DotNetFrameworkArchitecture.Bitness32)
            {
                // need to get rid of just the 64, but want to look up 'Framework64' rather than '64' to avoid the case where
                // the path is something like 'C:\MyPath\64\Framework64'.  9 = length of 'Framework', to make the index match
                // the location of the '64'.
                int indexOf64 = indexOfFramework64 + 9;
                string tempLocation = baseLocation;
                baseLocation = tempLocation.Substring(0, indexOf64) + tempLocation.Substring(indexOf64 + 2, tempLocation.Length - indexOf64 - 2);
            }
            else if (indexOfFramework64 == -1 && architecture == DotNetFrameworkArchitecture.Bitness64)
            {
                // need to add 64 -- since this is a heuristic, we assume that we just need to append.
                baseLocation += "64";
            }
            // we don't need to do anything if it's DotNetFrameworkArchitecture.Current.

            string[] directories;

            if (directoryExists(baseLocation))
            {
                directories = getDirectories(baseLocation, searchPattern);
            }
            else
            {
                // If we can't even find the base path, might as well give up now.
                return null;
            }

            if (directories.Length == 0)
            {
                // Couldn't find the path, return a null.
                return null;
            }

            // We don't care which one we choose, but we want to be predictible.
            // The intention here is to choose the alphabetical maximum.
            string max = directories[0];

            // the max.EndsWith condition: pre beta 2 versions of v3.5 have build number like v3.5.20111.
            // This was removed in beta2
            // We should favor \v3.5 over \v3.5.xxxxx
            // versions previous to 2.0 have .xxxx version numbers.  3.0 and 3.5 do not.
            if (!max.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < directories.Length; ++i)
                {
                    if (directories[i].EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        max = directories[i];
                        break;
                    }
                    else if (String.Compare(directories[i], max, StringComparison.OrdinalIgnoreCase) > 0)
                    {
                        max = directories[i];
                    }
                }
            }

            return max;
        }

        /// <summary>
        /// Determine the 32 bit program files directory, this is used for finding where the reference assemblies live.
        /// </summary>
        internal static string GenerateProgramFiles32()
        {
            // On a 64 bit machine we always want to use the program files x86.  If we are running as a 64 bit process then this variable will be set correctly
            // If we are on a 32 bit machine or running as a 32 bit process then this variable will be null and the programFiles variable will be correct.
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (String.IsNullOrEmpty(programFilesX86))
            {
                // 32 bit box
                programFilesX86 = programFiles;
            }

            return programFilesX86;
        }

        /// <summary>
        /// Determine the 64-bit program files directory, used as the basis for MSBuildExtensionsPath64.
        /// Returns null if we're not on a 64-bit machine
        /// </summary>
        internal static string GenerateProgramFiles64()
        {
            string programFilesX64;
            if (string.Equals(programFiles, programFiles32))
            {
                // either we're in a 32-bit window, or we're on a 32-bit machine.
                // if we're on a 32-bit machine, ProgramW6432 won't exist
                // if we're on a 64-bit machine, ProgramW6432 will point to the correct Program Files.
                programFilesX64 = Environment.GetEnvironmentVariable("ProgramW6432");
            }
            else
            {
                // 64-bit window on a 64-bit machine; %ProgramFiles% points to the 64-bit
                // Program Files already.
                programFilesX64 = programFiles;
            }

            return programFilesX64;
        }

        /// <summary>
        /// Generate the path to the program files reference assembly location by taking in the program files special folder and then
        /// using that path to generate the path to the reference assemblies location.
        /// </summary>
        internal static string GenerateProgramFilesReferenceAssemblyRoot()
        {
            string combinedPath = Environment.GetEnvironmentVariable("ReferenceAssemblyRoot");
            if (!String.IsNullOrEmpty(combinedPath))
            {
                combinedPath = Path.GetFullPath(combinedPath);
                if (FileSystems.Default.DirectoryExists(combinedPath))
                {
                    return combinedPath;
                }
            }

            combinedPath = NativeMethodsShared.IsWindows
                               ? Path.Combine(programFiles32, "Reference Assemblies\\Microsoft\\Framework")
                               : Path.Combine(NativeMethodsShared.FrameworkBasePath, "xbuild-frameworks");

            return Path.GetFullPath(combinedPath);
        }

        /// <summary>
        /// Given a ToolsVersion, find the path to the build tools folder for that ToolsVersion.
        /// </summary>
        /// <param name="toolsVersion">The ToolsVersion to look up</param>
        /// <param name="architecture">Target build tools architecture.</param>
        /// <returns>The path to the build tools folder for that ToolsVersion, if it exists, or
        /// null otherwise</returns>
        internal static string GeneratePathToBuildToolsForToolsVersion(string toolsVersion, DotNetFrameworkArchitecture architecture)
        {
            if (string.Equals(toolsVersion, MSBuildConstants.CurrentToolsVersion, StringComparison.Ordinal))
            {
                return GetPathToBuildToolsFromEnvironment(architecture);
            }

            string toolsPath = null;
#if FEATURE_WIN32_REGISTRY
            // If we're not looking for the current tools version, try the registry.
            toolsPath = GetPathToBuildToolsFromRegistry(toolsVersion, architecture);
#endif

            // If all else fails, always use the current environment.
            return toolsPath ?? GetPathToBuildToolsFromEnvironment(architecture);
        }

        /// <summary>
        /// Take the parts of the Target framework moniker and formulate the reference assembly path based on the following pattern:
        /// For a framework and version:
        ///     $(TargetFrameworkRootPath)\$(TargetFrameworkIdentifier)\$(TargetFrameworkVersion)
        /// For a subtype:
        ///     $(TargetFrameworkRootPath)\$(TargetFrameworkIdentifier)\$(TargetFrameworkVersion)\SubType\$(TargetFrameworkSubType)
        /// e.g.NET Framework v4.0 would locate its reference assemblies in:
        ///     \Program Files\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0
        /// e.g.Silverlight v2.0 would locate its reference assemblies in:
        ///     \Program Files\Reference Assemblies\Microsoft\Framework\Silverlight\v2.0
        /// e.g.NET Compact Framework v3.5, subtype PocketPC would locate its reference assemblies in:
        ///     \Program Files\Reference Assemblies\Microsoft\Framework\.NETCompactFramework\v3.5\SubType\PocketPC
        /// </summary>
        /// <returns>The path to the reference assembly location</returns>
        internal static string GenerateReferenceAssemblyPath(string targetFrameworkRootPath, FrameworkName frameworkName)
        {
            ErrorUtilities.VerifyThrowArgumentNull(targetFrameworkRootPath);
            ErrorUtilities.VerifyThrowArgumentNull(frameworkName);

            try
            {
                string path = Path.Combine(targetFrameworkRootPath, frameworkName.Identifier, "v" + frameworkName.Version.ToString());
                if (!String.IsNullOrEmpty(frameworkName.Profile))
                {
                    path = Path.Combine(path, "Profile", frameworkName.Profile);
                }

                return Path.GetFullPath(path);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                ErrorUtilities.ThrowInvalidOperation("FrameworkLocationHelper.CouldNotGenerateReferenceAssemblyDirectory", targetFrameworkRootPath, frameworkName.ToString(), e.Message);
                // The compiler does not see the massage above an as exception;
                return null;
            }
        }

        /// <summary>
        /// Given a path, subtracts the requested number of directories and returns the result.
        /// </summary>
        /// <comments>
        /// Internal only so that I can have the unit tests use it too, instead of duplicating the same code
        /// </comments>
        internal static string RemoveDirectories(string path, int numberOfLevelsToRemove)
        {
            ErrorUtilities.VerifyThrowArgumentOutOfRange(numberOfLevelsToRemove > 0, nameof(numberOfLevelsToRemove));

            string fixedPath = null;
            if (path != null)
            {
                // Record whether we had a slash or not so that we can tack it back on if necessary
                var endedWithASlash = path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                                      || path.EndsWith(
                                          Path.AltDirectorySeparatorChar.ToString(),
                                          StringComparison.Ordinal);

                DirectoryInfo fixedPathInfo = new DirectoryInfo(path);
                for (int i = 0; i < numberOfLevelsToRemove; i++)
                {
                    if (fixedPathInfo?.Parent != null)
                    {
                        fixedPathInfo = fixedPathInfo.Parent;
                    }
                }

                if (fixedPathInfo != null)
                {
                    fixedPath = fixedPathInfo.FullName;
                }

                if (fixedPath != null && endedWithASlash)
                {
                    fixedPath += Path.DirectorySeparatorChar;
                }
            }

            return fixedPath;
        }

        /// <summary>
        /// Look up the path to the build tools directory for the requested ToolsVersion in the .exe.config file of this executable
        /// </summary>
        private static string GetPathToBuildToolsFromEnvironment(DotNetFrameworkArchitecture architecture)
        {
            return architecture switch
            {
                DotNetFrameworkArchitecture.Bitness64 => BuildEnvironmentHelper.Instance.MSBuildToolsDirectory64,
                DotNetFrameworkArchitecture.Bitness32 => BuildEnvironmentHelper.Instance.MSBuildToolsDirectory32,
                _ => BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory,
            };
        }

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Look up the path to the build tools directory in the registry for the requested ToolsVersion and requested architecture
        /// </summary>
        private static string GetPathToBuildToolsFromRegistry(string toolsVersion, DotNetFrameworkArchitecture architecture)
        {
            string toolsVersionSpecificKey = ToolsVersionsRegistryPath + "\\" + toolsVersion;

            RegistryView view = RegistryView.Default;

            switch (architecture)
            {
                case DotNetFrameworkArchitecture.Bitness32:
                    view = RegistryView.Registry32;
                    break;
                case DotNetFrameworkArchitecture.Bitness64:
                    view = RegistryView.Registry64;
                    break;
                case DotNetFrameworkArchitecture.Current:
                    view = RegistryView.Default;
                    break;
            }

            string toolsPath = FindRegistryValueUnderKey(toolsVersionSpecificKey, MSBuildConstants.ToolsPath, view);
            return toolsPath;
        }
#endif

        #endregion // Internal methods

        #region Private methods

        /// <summary>
        /// Will return the path to the dot net framework reference assemblies if they exist under the program files\reference assembies\microsoft\framework directory
        /// or null if the directory does not exist.
        /// </summary>
        private static string GenerateReferenceAssemblyDirectory(string versionPrefix)
        {
            string programFilesReferenceAssemblyDirectory = Path.Combine(programFilesReferenceAssemblyLocation, versionPrefix);
            string referenceAssemblyDirectory = null;

            if (FileSystems.Default.DirectoryExists(programFilesReferenceAssemblyDirectory))
            {
                referenceAssemblyDirectory = programFilesReferenceAssemblyDirectory;
            }

            return referenceAssemblyDirectory;
        }

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Look for the given registry value under the given key.
        /// </summary>
        private static string FindRegistryValueUnderKey(
            string registryBaseKeyName,
            string registryKeyName,
            RegistryView registryView = RegistryView.Default)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView))
            using (RegistryKey subKey = baseKey.OpenSubKey(registryBaseKeyName))
            {
                return subKey?.GetValue(registryKeyName)?.ToString();
            }
        }
#endif

        private static VisualStudioSpec GetVisualStudioSpec(Version version)
        {
            ErrorUtilities.VerifyThrowArgument(VisualStudioSpecDict.Value.TryGetValue(version, out VisualStudioSpec spec), "FrameworkLocationHelper.UnsupportedVisualStudioVersion", version);
            return spec;
        }

        private static DotNetFrameworkSpec GetDotNetFrameworkSpec(Version version)
        {
            ErrorUtilities.VerifyThrowArgument(DotNetFrameworkSpecDict.Value.TryGetValue(version, out DotNetFrameworkSpec spec), "FrameworkLocationHelper.UnsupportedFrameworkVersion", version);
            return spec;
        }

        /// <summary>
        /// Helper method to create an instance of <see cref="DotNetFrameworkSpec"/> for .net v4.x,
        /// because most of attributes are the same for v4.x versions.
        /// </summary>
        /// <param name="version">.net framework version.</param>
        /// <param name="visualStudioVersion">Version of Visual Studio</param>
        /// <returns></returns>
        private static DotNetFrameworkSpec CreateDotNetFrameworkSpecForV4(Version version, Version visualStudioVersion)
        {
            return new DotNetFrameworkSpec(
                version,
                dotNetFrameworkRegistryKey: dotNetFrameworkSetupRegistryPath + "\\v4\\Full",
                dotNetFrameworkSetupRegistryInstalledName: "Install",
                dotNetFrameworkVersionFolderPrefix: NativeMethodsShared.IsWindows ? "v4.0" : "v4.5",
                dotNetFrameworkSdkRegistryToolsKey: "WinSDK-NetFx40Tools-x86",
                dotNetFrameworkSdkRegistryInstallationFolderName: "InstallationFolder",
                hasMSBuild: true,
                visualStudioVersion: visualStudioVersion);
        }

        private static void RedirectVersionsIfNecessary(ref Version dotNetFrameworkVersion, ref Version visualStudioVersion)
        {
            if (dotNetFrameworkVersion == dotNetFrameworkVersion45 && visualStudioVersion == visualStudioVersion100)
            {
                // There is no VS10 equivalent -- so just return the VS11 version
                visualStudioVersion = visualStudioVersion110;
                return;
            }

            if (dotNetFrameworkVersion == dotNetFrameworkVersion35 && visualStudioVersion > visualStudioVersion110)
            {
                // Fall back to Dev11 location -- 3.5 tools MSI was reshipped unchanged, so there
                // essentially are no 12-specific 3.5 tools.
                visualStudioVersion = visualStudioVersion110;
                return;
            }
        }

        #endregion

        private class VisualStudioSpec
        {
            /// <summary>
            /// The key in registry to indicate the corresponding .net framework in this visual studio.
            /// i.e. 'v8.0A' for VS11.
            /// </summary>
            private readonly string _dotNetFrameworkSdkRegistryKey;

            public VisualStudioSpec(
                Version version,
                string dotNetFrameworkSdkRegistryKey,
                string windowsSdkRegistryKey,
                string windowsSdkRegistryInstallationFolderName,
                Version[] supportedDotNetFrameworkVersions)
            {
                Version = version;
                _dotNetFrameworkSdkRegistryKey = dotNetFrameworkSdkRegistryKey;
                WindowsSdkRegistryKey = windowsSdkRegistryKey;
                WindowsSdkRegistryInstallationFolderName = windowsSdkRegistryInstallationFolderName;
                SupportedDotNetFrameworkVersions = supportedDotNetFrameworkVersions;
            }

            /// <summary>
            /// The version of this visual studio.
            /// </summary>
            public Version Version { get; }

            /// <summary>
            /// The list of supported .net framework versions in this visual studio.
            /// </summary>
            public Version[] SupportedDotNetFrameworkVersions { get; }

            /// <summary>
            /// The key in registry to indicate the corresponding windows sdk, i.e. "v8.0" for VS11.
            /// </summary>
            public string WindowsSdkRegistryKey { get; }

            /// <summary>
            /// The name in registry to indicate the sdk installation folder path, i.e. "InstallationFolder" for windows v8.0.
            /// </summary>
            public string WindowsSdkRegistryInstallationFolderName { get; }

            /// <summary>
            /// The key in the registry to indicate the corresponding .net framework in this visual studio.
            /// i.e. 'v8.0A' for VS11.
            /// </summary>
            public string GetDotNetFrameworkSdkRegistryKey(Version dotNetSdkVersion)
            {
                string sdkVersionFolder = "4.6"; // Default for back-compat

                if (dotNetSdkVersion == dotNetFrameworkVersion481)
                {
                    sdkVersionFolder = "4.8.1";
                }
                else if (dotNetSdkVersion == dotNetFrameworkVersion48)
                {
                    sdkVersionFolder = "4.8";
                }
                if (dotNetSdkVersion == dotNetFrameworkVersion472)
                {
                    sdkVersionFolder = "4.7.2";
                }
                if (dotNetSdkVersion == dotNetFrameworkVersion471)
                {
                    sdkVersionFolder = "4.7.1";
                }
                if (dotNetSdkVersion == dotNetFrameworkVersion47)
                {
                    sdkVersionFolder = "4.7";
                }
                else if (dotNetSdkVersion == dotNetFrameworkVersion461)
                {
                    sdkVersionFolder = "4.6.1";
                }
                else if (dotNetSdkVersion == dotNetFrameworkVersion462)
                {
                    sdkVersionFolder = "4.6.2";
                }

                // If the path is formatted to include a version number if we need to include that.
                // (e.g. NETFXSDK\{0} should be NETFXSDK\4.6 or NETFXSDK\4.6.1)
                // Note: before VS2015 this key was the same per instance of VS and didn't need to change.
                // In that case the string will not contain a format item and will not be modified.
                return string.Format(_dotNetFrameworkSdkRegistryKey, sdkVersionFolder);
            }
        }

        private class DotNetFrameworkSpec
        {
            private const string HKLM = "HKEY_LOCAL_MACHINE";
            private const string MicrosoftSDKsRegistryKey = @"SOFTWARE\Microsoft\Microsoft SDKs";

            /// <summary>
            /// The registry key of this .net framework, i.e. "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" for .net v4.5.
            /// </summary>
            private readonly string _dotNetFrameworkRegistryKey;

            /// <summary>
            /// The name in registry to indicate that this .net framework is installed, i.e. "Install" for .net v4.5.
            /// </summary>
            private readonly string _dotNetFrameworkSetupRegistryInstalledName;

            /// <summary>
            /// The key in registry to indicate the sdk tools folder, i.e. "WinSDK-NetFx40Tools-x86" for .net v4.5.
            /// </summary>
            private readonly string _dotNetFrameworkSdkRegistryToolsKey;

            /// <summary>
            /// The version of visual studio that shipped with this .net framework.
            /// </summary>
            private readonly Version _visualStudioVersion;

            /// <summary>
            /// Does this .net framework include MSBuild?
            /// </summary>
            private readonly bool _hasMsBuild;

            /// <summary>
            /// Cached paths of .net framework on different architecture.
            /// </summary>
            private readonly ConcurrentDictionary<DotNetFrameworkArchitecture, string> _pathsToDotNetFramework;

            /// <summary>
            /// Cached paths of .net framework sdk tools folder path on different visual studio version.
            /// </summary>
            private readonly ConcurrentDictionary<Version, string> _pathsToDotNetFrameworkSdkTools;

#if FEATURE_WIN32_REGISTRY
            /// <summary>
            /// Cached path of the corresponding windows sdk.
            /// </summary>
            private string _pathToWindowsSdk;
#endif

            /// <summary>
            /// Cached path of .net framework reference assemblies.
            /// </summary>
            protected string _pathToDotNetFrameworkReferenceAssemblies;

            public DotNetFrameworkSpec(
                Version version,
                string dotNetFrameworkRegistryKey,
                string dotNetFrameworkSetupRegistryInstalledName,
                string dotNetFrameworkVersionFolderPrefix,
                string dotNetFrameworkSdkRegistryToolsKey,
                string dotNetFrameworkSdkRegistryInstallationFolderName,
                bool hasMSBuild = true,
                Version visualStudioVersion = null)
            {
                this.Version = version;
                this._visualStudioVersion = visualStudioVersion;
                this._dotNetFrameworkRegistryKey = dotNetFrameworkRegistryKey;
                this._dotNetFrameworkSetupRegistryInstalledName = dotNetFrameworkSetupRegistryInstalledName;
                this.DotNetFrameworkFolderPrefix = dotNetFrameworkVersionFolderPrefix;
                this._dotNetFrameworkSdkRegistryToolsKey = dotNetFrameworkSdkRegistryToolsKey;
                this.DotNetFrameworkSdkRegistryInstallationFolderName = dotNetFrameworkSdkRegistryInstallationFolderName;
                this._hasMsBuild = hasMSBuild;
                this._pathsToDotNetFramework = new ConcurrentDictionary<DotNetFrameworkArchitecture, string>();
                this._pathsToDotNetFrameworkSdkTools = new ConcurrentDictionary<Version, string>();
            }

            /// <summary>
            /// The version of this .net framework.
            /// </summary>
            public Version Version { get; }

            /// <summary>
            /// The name in registry to indicate the sdk installation folder path, i.e. "InstallationFolder" for .net v4.5.
            /// </summary>
            public string DotNetFrameworkSdkRegistryInstallationFolderName { get; }

            /// <summary>
            /// Folder prefix, i.e. v4.0 for .net v4.5.
            /// </summary>
            public string DotNetFrameworkFolderPrefix { get; }

            /// <summary>
            /// Get the FrameworkName for this version of the .NET Framework.
            /// </summary>
            private FrameworkName FrameworkName => new FrameworkName(dotNetFrameworkIdentifier, this.Version);

            /// <summary>
            /// Gets the full registry key of this .net framework Sdk for the given visual studio version.
            /// i.e. "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Microsoft SDKs\Windows\v8.0A\WinSDK-NetFx40Tools-x86" for .net v4.5 on VS11.
            /// </summary>
            public virtual string GetDotNetFrameworkSdkRootRegistryKey(VisualStudioSpec visualStudioSpec)
            {
                return string.Join(@"\", HKLM, MicrosoftSDKsRegistryKey, visualStudioSpec.GetDotNetFrameworkSdkRegistryKey(Version), _dotNetFrameworkSdkRegistryToolsKey);
            }

            /// <summary>
            /// Gets the full path of .net framework for the given architecture.
            /// </summary>
            public virtual string GetPathToDotNetFramework(DotNetFrameworkArchitecture architecture)
            {
                string cachedPath;
                if (this._pathsToDotNetFramework.TryGetValue(architecture, out cachedPath))
                {
                    return cachedPath;
                }

#if FEATURE_WIN32_REGISTRY
                // Otherwise, check to see if we're even installed.  If not, return null -- no point in setting the static
                // variables to null when that's what they are already.
                if (NativeMethodsShared.IsWindows && !CheckForFrameworkInstallation(
                    this._dotNetFrameworkRegistryKey,
                    this._dotNetFrameworkSetupRegistryInstalledName))
                {
                    return null;
                }
#endif

                // We're installed and we haven't found this framework path yet -- so find it!
                string generatedPathToDotNetFramework =
                                FindDotNetFrameworkPath(
                                    Path.GetDirectoryName(typeof(object).Module.FullyQualifiedName),
                                    this.DotNetFrameworkFolderPrefix,
                                    Directory.Exists,
                                    Directory.GetDirectories,
                                    architecture);

                // Assume if either MSBuild.exe or Microsoft.Build.dll are shipped, there is a valid install.
                // Note: net481 did not ship an ARM64 MSBuild.exe, so checking its dll's is the fallback for a valid install.
                // Context: https://github.com/dotnet/msbuild/pull/7689
                // Rollback see https://developercommunity.visualstudio.com/t/Unable-to-locate-MSBuild-path-with-Lates/10824132 
                if (this._hasMsBuild &&
                    generatedPathToDotNetFramework != null &&
                    (!File.Exists(Path.Combine(generatedPathToDotNetFramework, NativeMethodsShared.IsWindows ? "MSBuild.exe" : "mcs.exe")) &&
                     !File.Exists(Path.Combine(generatedPathToDotNetFramework, "Microsoft.Build.dll"))))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(generatedPathToDotNetFramework))
                {
                    _pathsToDotNetFramework[architecture] = generatedPathToDotNetFramework;
                }

                return generatedPathToDotNetFramework;
            }

            /// <summary>
            /// Gets the full path of .net framework sdk tools for the given visual studio version.
            /// i.e. "C:\Program Files (x86)\Microsoft SDKs\Windows\v8.0A\bin\NETFX 4.0 Tools\" for .net v4.5 on VS11.
            /// </summary>
            public virtual string GetPathToDotNetFrameworkSdkTools(VisualStudioSpec visualStudioSpec)
            {
                string cachedPath;
                if (this._pathsToDotNetFrameworkSdkTools.TryGetValue(visualStudioSpec.Version, out cachedPath))
                {
                    return cachedPath;
                }

                string generatedPathToDotNetFrameworkSdkTools = null;

                if (NativeMethodsShared.IsUnixLike)
                {
                    string frameworkPath = NativeMethodsShared.FrameworkBasePath;
                    if (!string.IsNullOrEmpty(frameworkPath))
                    {
                        frameworkPath = Path.Combine(frameworkPath, this.Version.ToString());
                    }

                    if (!string.IsNullOrEmpty(frameworkPath) && FileSystems.Default.DirectoryExists(frameworkPath))
                    {
                        generatedPathToDotNetFrameworkSdkTools = frameworkPath;
                    }
                }
#if FEATURE_WIN32_REGISTRY
                else
                {
                    string registryPath = string.Join(
                                              @"\",
                                              MicrosoftSDKsRegistryKey,
                                              visualStudioSpec.GetDotNetFrameworkSdkRegistryKey(Version),
                                              this._dotNetFrameworkSdkRegistryToolsKey);

                    // For the Dev10 SDK, we check the registry that corresponds to the current process' bitness, rather than
                    // always the 32-bit one the way we do for Dev11 and onward, since that's what we did in Dev10 as well.
                    // As of Dev11, the SDK reg keys are installed in the 32-bit registry.
                    RegistryView registryView = visualStudioSpec.Version == visualStudioVersion100 ? RegistryView.Default : RegistryView.Registry32;

                    generatedPathToDotNetFrameworkSdkTools = FindRegistryValueUnderKey(
                        registryPath,
                        this.DotNetFrameworkSdkRegistryInstallationFolderName,
                        registryView);

                    if (string.IsNullOrEmpty(generatedPathToDotNetFrameworkSdkTools))
                    {
                        // Fallback mechanisms.

                        // Try to find explicit fallback rule.
                        // i.e. v4.5.1 on VS12 fallbacks to v4.5 on VS12.
                        bool foundExplicitRule = false;
                        for (int i = 0; i < s_explicitFallbackRulesForPathToDotNetFrameworkSdkTools.GetLength(0); ++i)
                        {
                            var trigger = s_explicitFallbackRulesForPathToDotNetFrameworkSdkTools[i, 0];
                            if (trigger.Item1 == this.Version && trigger.Item2 == visualStudioSpec.Version)
                            {
                                foundExplicitRule = true;
                                var fallback = s_explicitFallbackRulesForPathToDotNetFrameworkSdkTools[i, 1];
                                generatedPathToDotNetFrameworkSdkTools = FallbackToPathToDotNetFrameworkSdkToolsInPreviousVersion(
                                    fallback.Item1,
                                    fallback.Item2);
                                break;
                            }
                        }

                        // Otherwise, fallback to previous VS.
                        // i.e. fallback to v110 if the current visual studio version is v120.
                        if (!foundExplicitRule)
                        {
                            int index = Array.IndexOf(VisualStudioSpecs.Value, visualStudioSpec);
                            if (index > 0)
                            {
                                // The items in the array "visualStudioSpecs" must be ordered by version. That would allow us to fallback to the previous visual studio version easily.
                                VisualStudioSpec fallbackVisualStudioSpec = VisualStudioSpecs.Value[index - 1];
                                generatedPathToDotNetFrameworkSdkTools = FallbackToPathToDotNetFrameworkSdkToolsInPreviousVersion(
                                    this.Version,
                                    fallbackVisualStudioSpec.Version);
                            }
                        }
                    }
                }
#endif
                if (string.IsNullOrEmpty(generatedPathToDotNetFrameworkSdkTools))
                {
                    // Fallback to "default" ultimately.
                    generatedPathToDotNetFrameworkSdkTools = FallbackToDefaultPathToDotNetFrameworkSdkTools(this.Version);
                }

                if (!string.IsNullOrEmpty(generatedPathToDotNetFrameworkSdkTools))
                {
                    this._pathsToDotNetFrameworkSdkTools[visualStudioSpec.Version] = generatedPathToDotNetFrameworkSdkTools;
                }

                return generatedPathToDotNetFrameworkSdkTools;
            }

            /// <summary>
            /// Gets the full path of .net framework sdk.
            /// i.e. "C:\Program Files (x86)\Microsoft SDKs\Windows\v8.0A\" for .net v4.5 on VS11.
            /// </summary>
            public virtual string GetPathToDotNetFrameworkSdk(VisualStudioSpec visualStudioSpec)
            {
                string pathToBinRoot = this.GetPathToDotNetFrameworkSdkTools(visualStudioSpec);
                if (NativeMethodsShared.IsWindows)
                {
                    pathToBinRoot = RemoveDirectories(pathToBinRoot, 2);
                }

                return pathToBinRoot;
            }

            /// <summary>
            /// Gets the full path of reference assemblies folder.
            /// i.e. "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\" for .net v4.5.
            /// </summary>
            public virtual string GetPathToDotNetFrameworkReferenceAssemblies()
            {
                if (this._pathToDotNetFrameworkReferenceAssemblies == null)
                {
                    // when a user requests the 40 reference assembly path we don't need to read the redist list because we will not be chaining so we may as well just
                    // generate the path and save us some time.
                    string referencePath = GenerateReferenceAssemblyPath(FrameworkLocationHelper.programFilesReferenceAssemblyLocation, this.FrameworkName);
                    if (FileSystems.Default.DirectoryExists(referencePath))
                    {
                        this._pathToDotNetFrameworkReferenceAssemblies = FileUtilities.EnsureTrailingSlash(referencePath);
                    }
                }

                return this._pathToDotNetFrameworkReferenceAssemblies;
            }

            /// <summary>
            /// Gets the full path of the corresponding windows sdk shipped with this .net framework.
            /// i.e. "C:\Program Files (x86)\Windows Kits\8.0\" for v8.0 (shipped with .net v4.5 and VS11).
            /// </summary>
            public virtual string GetPathToWindowsSdk()
            {
#if FEATURE_WIN32_REGISTRY
                if (this._pathToWindowsSdk == null)
                {
                    ErrorUtilities.VerifyThrowArgument(this._visualStudioVersion != null, "FrameworkLocationHelper.UnsupportedFrameworkVersionForWindowsSdk", this.Version);

                    var visualStudioSpec = GetVisualStudioSpec(this._visualStudioVersion);

                    if (string.IsNullOrEmpty(visualStudioSpec.WindowsSdkRegistryKey) || string.IsNullOrEmpty(visualStudioSpec.WindowsSdkRegistryInstallationFolderName))
                    {
                        ErrorUtilities.ThrowArgument("FrameworkLocationHelper.UnsupportedFrameworkVersionForWindowsSdk", this.Version);
                    }

                    string registryPath = string.Join(@"\", MicrosoftSDKsRegistryKey, "Windows", visualStudioSpec.WindowsSdkRegistryKey);

                    // As of Dev11, the SDK reg keys are installed in the 32-bit registry.
                    this._pathToWindowsSdk = FindRegistryValueUnderKey(
                        registryPath,
                        visualStudioSpec.WindowsSdkRegistryInstallationFolderName,
                        RegistryView.Registry32);
                }
                return this._pathToWindowsSdk;
#else
                return null;
#endif
            }

#if FEATURE_WIN32_REGISTRY
            private static string FallbackToPathToDotNetFrameworkSdkToolsInPreviousVersion(Version dotNetFrameworkVersion, Version visualStudioVersion)
            {
                if (VisualStudioSpecDict.Value.TryGetValue(visualStudioVersion, out VisualStudioSpec visualStudioSpec)
                    && DotNetFrameworkSpecDict.Value.TryGetValue(dotNetFrameworkVersion, out DotNetFrameworkSpec dotNetFrameworkSpec)
                    && visualStudioSpec.SupportedDotNetFrameworkVersions.Contains(dotNetFrameworkVersion))
                {
                    return dotNetFrameworkSpec.GetPathToDotNetFrameworkSdkTools(visualStudioSpec);
                }

                return null;
            }
#endif // FEATURE_WIN32_REGISTRY

            private static string FallbackToDefaultPathToDotNetFrameworkSdkTools(Version dotNetFrameworkVersion)
            {
                if (dotNetFrameworkVersion.Major == 4)
                {
                    return PathToV4ToolsInFallbackDotNetFrameworkSdk;
                }

                if (dotNetFrameworkVersion == dotNetFrameworkVersion35)
                {
                    return PathToV35ToolsInFallbackDotNetFrameworkSdk;
                }

                return null;
            }
        }

        /// <summary>
        /// Specialized implementation for legacy .net framework v1.1 and v2.0.
        /// </summary>
        private class DotNetFrameworkSpecLegacy : DotNetFrameworkSpec
        {
#if FEATURE_WIN32_REGISTRY
            private string _pathToDotNetFrameworkSdkTools;
#endif

            public DotNetFrameworkSpecLegacy(
                Version version,
                string dotNetFrameworkRegistryKey,
                string dotNetFrameworkSetupRegistryInstalledName,
                string dotNetFrameworkVersionFolderPrefix,
                string dotNetFrameworkSdkRegistryInstallationFolderName,
                bool hasMSBuild)
                : base(version,
                      dotNetFrameworkRegistryKey,
                      dotNetFrameworkSetupRegistryInstalledName,
                      dotNetFrameworkVersionFolderPrefix,
                      null,
                      dotNetFrameworkSdkRegistryInstallationFolderName,
                      hasMSBuild)
            {
            }

            /// <summary>
            /// Gets the full registry key of this .net framework Sdk for the given visual studio version.
            /// i.e. "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework" for v1.1 and v2.0.
            /// </summary>
            public override string GetDotNetFrameworkSdkRootRegistryKey(VisualStudioSpec visualStudioSpec)
            {
                return fullDotNetFrameworkRegistryKey;
            }

            /// <summary>
            /// Gets the full path of .net framework sdk tools for the given visual studio version.
            /// </summary>
            public override string GetPathToDotNetFrameworkSdkTools(VisualStudioSpec visualStudioSpec)
            {
#if FEATURE_WIN32_REGISTRY
                if (_pathToDotNetFrameworkSdkTools == null)
                {
                    _pathToDotNetFrameworkSdkTools = FindRegistryValueUnderKey(
                        dotNetFrameworkRegistryPath,
                        this.DotNetFrameworkSdkRegistryInstallationFolderName);
                }

                return _pathToDotNetFrameworkSdkTools;
#else
                return null;
#endif
            }

            /// <summary>
            /// Gets the full path of .net framework sdk, which is the full path of .net framework sdk tools for v1.1 and v2.0.
            /// </summary>
            public override string GetPathToDotNetFrameworkSdk(VisualStudioSpec visualStudioSpec)
            {
                return this.GetPathToDotNetFrameworkSdkTools(visualStudioSpec);
            }

            /// <summary>
            /// Gets the full path of reference assemblies folder, which is the full path of .net framework for v1.1 and v2.0.
            /// </summary>
            public override string GetPathToDotNetFrameworkReferenceAssemblies()
            {
                return this.GetPathToDotNetFramework(DotNetFrameworkArchitecture.Current);
            }
        }

        /// <summary>
        /// Specialized implementation for legacy .net framework v3.0 and v3.5.
        /// </summary>
        private class DotNetFrameworkSpecV3 : DotNetFrameworkSpec
        {
            public DotNetFrameworkSpecV3(
                Version version,
                string dotNetFrameworkRegistryKey,
                string dotNetFrameworkSetupRegistryInstalledName,
                string dotNetFrameworkVersionFolderPrefix,
                string dotNetFrameworkSdkRegistryToolsKey,
                string dotNetFrameworkSdkRegistryInstallationFolderName,
                bool hasMSBuild)
                : base(version,
                      dotNetFrameworkRegistryKey,
                      dotNetFrameworkSetupRegistryInstalledName,
                      dotNetFrameworkVersionFolderPrefix,
                      dotNetFrameworkSdkRegistryToolsKey,
                      dotNetFrameworkSdkRegistryInstallationFolderName,
                      hasMSBuild)
            {
            }

            /// <summary>
            /// Gets the full path of .net framework sdk.
            /// i.e. "C:\Program Files (x86)\Microsoft SDKs\Windows\v7.0A\" for .net v3.5 on VS11.
            /// </summary>
            public override string GetPathToDotNetFrameworkSdk(VisualStudioSpec visualStudioSpec)
            {
                string pathToBinRoot = this.GetPathToDotNetFrameworkSdkTools(visualStudioSpec);
                return RemoveDirectories(pathToBinRoot, 1);
            }

            /// <summary>
            /// Gets the full path of reference assemblies folder.
            /// i.e. "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.5\" for v3.5.
            /// </summary>
            public override string GetPathToDotNetFrameworkReferenceAssemblies()
            {
                if (this._pathToDotNetFrameworkReferenceAssemblies == null)
                {
#if FEATURE_WIN32_REGISTRY
                    this._pathToDotNetFrameworkReferenceAssemblies = FindRegistryValueUnderKey(
                        dotNetFrameworkAssemblyFoldersRegistryPath + "\\" + this.DotNetFrameworkFolderPrefix,
                        referenceAssembliesRegistryValueName);
#endif

                    if (this._pathToDotNetFrameworkReferenceAssemblies == null)
                    {
                        this._pathToDotNetFrameworkReferenceAssemblies = GenerateReferenceAssemblyDirectory(this.DotNetFrameworkFolderPrefix);
                    }
                }

                return this._pathToDotNetFrameworkReferenceAssemblies;
            }
        }
    }
}
