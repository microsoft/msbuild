// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Tasks.AssemblyFoldersFromConfig;
using Microsoft.Win32;
using FrameworkNameVersioning = System.Runtime.Versioning.FrameworkName;
using SharedDotNetFrameworkArchitecture = Microsoft.Build.Shared.DotNetFrameworkArchitecture;
using UtilitiesDotNetFrameworkArchitecture = Microsoft.Build.Utilities.DotNetFrameworkArchitecture;

#nullable disable

namespace Microsoft.Build.Utilities
{
    /// <summary>
    /// Used to specify the targeted version of the .NET Framework for some methods of ToolLocationHelper.
    /// </summary>
    public enum TargetDotNetFrameworkVersion
    {
        /// <summary>
        /// version 1.1
        /// </summary>
        Version11 = 0,

        /// <summary>
        /// version 2.0
        /// </summary>
        Version20 = 1,

        /// <summary>
        /// version 3.0
        /// </summary>
        Version30 = 2,

        /// <summary>
        /// version 3.5
        /// </summary>
        Version35 = 3,

        /// <summary>
        /// version 4.0
        /// </summary>
        Version40 = 4,

        /// <summary>
        /// version 4.5
        /// </summary>
        Version45 = 5,

        /// <summary>
        /// version 4.5.1
        /// </summary>
        Version451 = 6,

        /// <summary>
        /// version 4.6
        /// </summary>
        Version46 = 7,

        /// <summary>
        /// version 4.6.1
        /// </summary>
        Version461 = 8,

        /// <summary>
        /// version 4.5.2. Enum is out of order because it was shipped out of band from a Visual Studio update
        /// without a corresponding SDK release.
        /// </summary>
        Version452 = 9,

        /// <summary>
        /// version 4.6.2
        /// </summary>
        Version462 = 10,

        /// <summary>
        /// version 4.7
        /// </summary>
        Version47 = 11,

        /// <summary>
        /// version 4.7.1
        /// </summary>
        Version471 = 12,

        /// <summary>
        /// version 4.7.2
        /// </summary>
        Version472 = 13,

        /// <summary>
        /// version 4.8
        /// </summary>
        Version48 = 14,

        /// <summary>
        /// version 4.8.1
        /// </summary>
        Version481 = 15,

        /// <summary>
        /// The latest version available at the time of major release. This
        /// value should not be updated in minor releases as it could be a
        /// breaking change. Use 'Latest' if possible, but note the
        /// compatibility implications.
        /// </summary>
        VersionLatest = Version48,

        /// <summary>
        /// Sentinel value for the latest version that this version of MSBuild is aware of. Similar
        /// to VersionLatest except the compiled value in the calling application will not need to
        /// change for the update in MSBuild to be used.
        /// </summary>
        /// <remarks>
        /// This value was introduced in Visual Studio 15.1. It is incompatible with previous
        /// versions of MSBuild.
        /// </remarks>
        Latest = 9999
    }

    /// <summary>
    /// Used to specify the version of Visual Studio from which to select associated
    /// tools for some methods of ToolLocationHelper
    /// </summary>
    public enum VisualStudioVersion
    {
        /// <summary>
        /// Visual Studio 2010 (Dev10) and SP1
        /// </summary>
        Version100,

        /// <summary>
        /// Visual Studio 2012 (Dev11)
        /// </summary>
        Version110,

        /// <summary>
        /// Visual Studio 2013 (Dev12)
        /// </summary>
        Version120,

        /// <summary>
        /// Visual Studio 2015 (Dev14)
        /// </summary>
        Version140,

        /// <summary>
        /// Visual Studio 2017 (Dev15)
        /// </summary>
        Version150,

        /// <summary>
        /// Visual Studio 2019 (Dev16)
        /// </summary>
        Version160,

        /// <summary>
        /// Visual Studio 2022 (Dev17)
        /// </summary>
        Version170,

        // keep this up-to-date; always point to the last entry.
        /// <summary>
        /// The latest version available at the time of release
        /// </summary>
        VersionLatest = Version170
    }

    /// <summary>
    /// Used to specify the targeted bitness of the .NET Framework for some methods of ToolLocationHelper
    /// </summary>
    public enum DotNetFrameworkArchitecture
    {
        /// <summary>
        /// Indicates the .NET Framework that is currently being run under.
        /// </summary>
        Current = 0,

        /// <summary>
        /// Indicates the 32-bit .NET Framework
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Bitness", Justification = "Bitness is a reasonable term")]
        Bitness32 = 1,

        /// <summary>
        /// Indicates the 64-bit .NET Framework
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Bitness", Justification = "Bitness is a reasonable term")]
        Bitness64 = 2
    }

    /// <summary>
    /// ToolLocationHelper provides utility methods for locating .NET Framework and .NET Framework SDK directories and files.
    /// NOTE: All public methods of this class are available to MSBuild projects for use in functions - they must be safe for
    /// use during project evaluation.
    /// </summary>
    public static class ToolLocationHelper
    {
        /// <summary>
        /// Cache the results of reading the redist list so that we do not have to read the redist list over and over again to get the chains.
        /// </summary>
        private static Dictionary<string, string> s_chainedReferenceAssemblyPath;

        /// <summary>
        /// Lock object to synchronize chainedReferenceAssemblyPath dictionary
        /// </summary>
        private static readonly object s_locker = new object();

        /// <summary>
        /// Cache the results of calling the GetPathToReferenceAssemblies so that we do not recalculate it every time we call the method
        /// </summary>
        private static Dictionary<string, IList<string>> s_cachedReferenceAssemblyPaths;

        /// <summary>
        /// Cache the frameworkName of the highest version of a framework given its root path and identifier.
        /// This is to optimize calls to GetHighestVersionOfTargetFramework
        /// </summary>
        private static Dictionary<string, FrameworkNameVersioning> s_cachedHighestFrameworkNameForTargetFrameworkIdentifier;

        /// <summary>
        /// Cache the sdk structure as found by enumerating the disk and registry.
        /// </summary>
        private static Dictionary<string, IEnumerable<TargetPlatformSDK>> s_cachedTargetPlatforms;

        /// <summary>
        /// Cache new style extension sdks that we've enumerated
        /// </summary>
        private static Dictionary<string, TargetPlatformSDK> s_cachedExtensionSdks;

        /// <summary>
        /// Cache the display name for the TFM/FrameworkName, keyed by the target framework directory.
        /// This is generated by the "Name" attribute on the root tag of the primary matching redist list.
        /// Value is never an empty string or null: a name will be synthesized if necessary.
        /// </summary>
        private static Dictionary<string, string> s_cachedTargetFrameworkDisplayNames;

        /// <summary>
        /// Cache the set of target platform references for a particular combination of inputs.  For legacy
        /// target platforms, this is just grabbing all winmds from the References\CommonConfiguration\Neutral
        /// folder; for OneCore-based platforms, this involves reading the list from Platform.xml and synthesizing
        /// the locations.
        /// </summary>
        private static Dictionary<string, string[]> s_cachedTargetPlatformReferences;

        /// <summary>
        /// Cache the set of extension Sdk references for a particular combination of inputs.
        /// </summary>
        private static Dictionary<string, string[]> s_cachedExtensionSdkReferences;

        /// <summary>
        /// Cache the list of supported frameworks
        /// </summary>
        private static List<string> s_targetFrameworkMonikers = null;

        /// <summary>
        /// Cache the VS Install folders for particular range of VS versions
        /// </summary>
        private static Dictionary<string, string[]> s_vsInstallFolders;

        /// <summary>
        /// Character used to separate search paths specified for MSBuildExtensionsPath* in
        /// the config file
        /// </summary>
        private static readonly char[] _separatorForFallbackSearchPaths = MSBuildConstants.SemicolonChar;

        private const string retailConfigurationName = "Retail";
        private const string neutralArchitectureName = "Neutral";
        private const string commonConfigurationFolderName = "CommonConfiguration";
        private const string redistFolderName = "Redist";
        private const string referencesFolderName = "References";
        private const string designTimeFolderName = "DesignTime";
        private const string platformsFolderName = "Platforms";
        private const string uapDirectoryName = "Windows Kits";
        private const string uapRegistryName = "Windows";
        private const int uapVersion = 10;
        private static readonly char[] s_diskRootSplitChars = MSBuildConstants.SemicolonChar;

        /// <summary>
        /// Delegate to a method which takes a version enumeration and return a string path
        /// </summary>
        internal delegate string VersionToPath(TargetDotNetFrameworkVersion version);

        #region Public methods

        /// <summary>
        /// The current ToolsVersion.
        /// </summary>
        public static string CurrentToolsVersion => MSBuildConstants.CurrentToolsVersion;

        /// <summary>
        /// Get a sorted list of AssemblyFoldersExInfo which contain information about what directories the 3rd party assemblies are registered under for use during build and design time.
        ///
        /// This method will enumerate the AssemblyFoldersEx registry location and return a list of AssemblyFoldersExInfo in the same order in which
        /// they will be searched during both design and build time for reference assemblies.
        /// </summary>
        /// <param name="registryRoot">The root registry location for the targeted framework. For .NET this is SOFTWARE\MICROSOFT\.NETFramework</param>
        /// <param name="targetFrameworkVersion">The targeted framework version (2.0, 3.0, 3.5, 4.0, etc)</param>
        /// <param name="registryKeySuffix">The name of the folder (AssemblyFoldersEx) could also be PocketPC\AssemblyFoldersEx, or others</param>
        /// <param name="osVersion">Components may declare Min and Max OSVersions in the registry this value can be used filter directories returned based on whether or not the osversion is bounded by the Min  and Max versions declared by the component. If this value is blank or null no filtering is done</param>
        /// <param name="platform">Components may declare platform guids in the registry this can be used to return only directories which have a certain platform guid. If this value is blank or null no filtering is done</param>
        /// <param name="targetProcessorArchitecture">What processor architecture is being targeted. This determines which registry hives are searched in what order.
        /// On a 64 bit operating system we do the following
        ///         If you are targeting 64 bit (target x64 or ia64)
        ///             Add in the 64 bit hive first
        ///             Add in the 32 bit hive second
        ///         If you are not targeting a 64 bit
        ///            Add in the 32 bit hive first
        ///            Add in the 64 bit hive second
        /// On a 32 bit machine we only add in the 32 bit hive.
        /// </param>
        /// <returns>List of AssemblyFoldersExInfo</returns>
        [SupportedOSPlatform("windows")]
        public static IList<AssemblyFoldersExInfo> GetAssemblyFoldersExInfo(string registryRoot, string targetFrameworkVersion, string registryKeySuffix, string osVersion, string platform, System.Reflection.ProcessorArchitecture targetProcessorArchitecture)
        {
            ErrorUtilities.VerifyThrowArgumentLength(registryRoot);
            ErrorUtilities.VerifyThrowArgumentLength(registryKeySuffix);
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkVersion);

            AssemblyFoldersEx assemblyFoldersEx = new AssemblyFoldersEx(registryRoot, targetFrameworkVersion, registryKeySuffix, osVersion, platform, new GetRegistrySubKeyNames(RegistryHelper.GetSubKeyNames), new GetRegistrySubKeyDefaultValue(RegistryHelper.GetDefaultValue), targetProcessorArchitecture, new OpenBaseKey(RegistryHelper.OpenBaseKey));

            var assemblyFolders = new List<AssemblyFoldersExInfo>();
            assemblyFolders.AddRange(assemblyFoldersEx);
            return assemblyFolders;
        }

        /// <summary>
        /// Get a sorted list of AssemblyFoldersFromConfigInfo which contain information about what directories the 3rd party assemblies are registered under for use during build and design time.
        ///
        /// This method will read the specified configuration file and enumerate the and return a list of AssemblyFoldersFromConfigInfo in the same order in which
        /// they will be searched during both design and build time for reference assemblies.
        /// </summary>
        /// <param name="configFile">Full path to the Assembly Folders config file.</param>
        /// <param name="targetFrameworkVersion">The targeted framework version (2.0, 3.0, 3.5, 4.0, etc).</param>
        /// <param name="targetProcessorArchitecture">What processor architecture is being targeted. This determines which registry hives are searched in what order.
        /// On a 64 bit operating system we do the following
        ///         If you are targeting 64 bit (target x64 or ia64)
        ///             Add in the 64 bit assembly folders first
        ///             Add in the 32 bit assembly folders second
        ///         If you are not targeting a 64 bit
        ///            Add in the 32 bit assembly folders first
        ///            Add in the 64 bit assembly folders second
        /// On a 32 bit machine we only add in the 32 bit assembly folders.
        /// </param>
        /// <returns>List of AssemblyFoldersFromConfigInfo</returns>
        public static IList<AssemblyFoldersFromConfigInfo> GetAssemblyFoldersFromConfigInfo(string configFile, string targetFrameworkVersion, System.Reflection.ProcessorArchitecture targetProcessorArchitecture)
        {
            ErrorUtilities.VerifyThrowArgumentLength(configFile);
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkVersion);

            var assemblyFoldersInfos = new AssemblyFoldersFromConfig(configFile, targetFrameworkVersion, targetProcessorArchitecture);

            return assemblyFoldersInfos.ToList();
        }

        /// <summary>
        /// Get a list of SDK's installed on the machine for a given target platform
        /// </summary>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their location. K:SDKName V:SDK installation location</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IDictionary<string, string> GetPlatformExtensionSDKLocations(string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformExtensionSDKLocations(null, null, targetPlatformIdentifier, targetPlatformVersion);

        /// <summary>
        /// Get a list of SDK's installed on the machine for a given target platform
        /// </summary>
        /// <param name="diskRoots">Array of disk locations to search for sdks</param>
        /// <param name="registryRoot">Root registry location to look for sdks</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their location. K:SDKName V:SDK installation location</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IDictionary<string, string> GetPlatformExtensionSDKLocations(string[] diskRoots, string registryRoot, string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformExtensionSDKLocations(diskRoots, null, registryRoot, targetPlatformIdentifier, targetPlatformVersion);

        /// <summary>
        /// Get a list of SDK's installed on the machine for a given target platform
        /// </summary>
        /// <param name="diskRoots">Array of disk locations to search for sdks</param>
        /// <param name="extensionDiskRoots">New style extension SDK roots</param>
        /// <param name="registryRoot">Root registry location to look for sdks</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their location. K:SDKName V:SDK installation location</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IDictionary<string, string> GetPlatformExtensionSDKLocations(string[] diskRoots, string[] extensionDiskRoots, string registryRoot, string targetPlatformIdentifier, Version targetPlatformVersion)
        {
            var extensionSDKs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<TargetPlatformSDK> targetPlatformMonikers = GetTargetPlatformMonikers(diskRoots, extensionDiskRoots, registryRoot, targetPlatformIdentifier, targetPlatformVersion);
            foreach (TargetPlatformSDK moniker in targetPlatformMonikers)
            {
                foreach (KeyValuePair<string, string> extension in moniker.ExtensionSDKs)
                {
                    extensionSDKs[extension.Key] = extension.Value;
                }
            }
            return extensionSDKs;
        }

        /// <summary>
        /// Get a list of SDK's installed on the machine for a given target platform
        /// </summary>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their tuples containing (location, platform version).</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Casing kept to maintain consistency with existing APIs")]
        public static IDictionary<string, Tuple<string, string>> GetPlatformExtensionSDKLocationsAndVersions(string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformExtensionSDKLocationsAndVersions(null, null, targetPlatformIdentifier, targetPlatformVersion);

        /// <summary>
        /// Set of installed SDKs and their location and platform versions
        /// </summary>
        /// <param name="diskRoots">Array of disk locations to search for sdks</param>
        /// <param name="registryRoot">Root registry location to look for sdks</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their tuples containing (location, platform version).</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Casing kept to maintain consistency with existing APIs")]
        public static IDictionary<string, Tuple<string, string>> GetPlatformExtensionSDKLocationsAndVersions(string[] diskRoots, string registryRoot, string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformExtensionSDKLocationsAndVersions(diskRoots, null, registryRoot, targetPlatformIdentifier, targetPlatformVersion);

        /// <summary>
        /// Set of installed SDKs and their location and platform versions
        /// </summary>
        /// <param name="diskRoots">Array of disk locations to search for sdks</param>
        /// <param name="multiPlatformDiskRoots">Array of disk locations to search for SDKs that target multiple versions</param>
        /// <param name="registryRoot">Root registry location to look for sdks</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>IDictionary of installed SDKS and their tuples containing (location, platform version). Version may be null if the SDK targets multiple versions.</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Casing kept to maintain consistency with existing APIs")]
        public static IDictionary<string, Tuple<string, string>> GetPlatformExtensionSDKLocationsAndVersions(string[] diskRoots, string[] multiPlatformDiskRoots, string registryRoot, string targetPlatformIdentifier, Version targetPlatformVersion)
        {
            var extensionSDKsAndVersions = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<TargetPlatformSDK> targetPlatformMonikers = GetTargetPlatformMonikers(diskRoots, multiPlatformDiskRoots, registryRoot, targetPlatformIdentifier, targetPlatformVersion);

            foreach (TargetPlatformSDK moniker in targetPlatformMonikers)
            {
                foreach (KeyValuePair<string, string> extension in moniker.ExtensionSDKs)
                {
                    extensionSDKsAndVersions[extension.Key] = Tuple.Create<string, string>(extension.Value, moniker.TargetPlatformVersion.ToString());
                }
            }
            return extensionSDKsAndVersions;
        }

        /// <summary>
        /// Get target platform monikers used to extract ESDK information in the methods GetPlatformExtensionSDKLocationsAndVersions and GetPlatformExtensionSDKLocations
        /// </summary>
        private static IEnumerable<TargetPlatformSDK> GetTargetPlatformMonikers(string[] diskRoots, string[] extensionDiskRoots, string registryRoot, string targetPlatformIdentifier, Version targetPlatformVersion)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);

            string targetPlatformVersionString = targetPlatformVersion.ToString();

            ErrorUtilities.DebugTraceMessage("GetPlatformExtensionSDKLocations", "Calling with TargetPlatformIdentifier:'{0}' and TargetPlatformVersion: '{1}'", targetPlatformIdentifier, targetPlatformVersionString);
            IEnumerable<TargetPlatformSDK> targetPlatformSDKs = RetrieveTargetPlatformList(diskRoots, extensionDiskRoots, registryRoot);

            return targetPlatformSDKs
                .Where<TargetPlatformSDK>(platformSDK =>
                    string.IsNullOrEmpty(platformSDK.TargetPlatformIdentifier)
                    ||
                    (
                        platformSDK.TargetPlatformIdentifier.Equals(targetPlatformIdentifier, StringComparison.OrdinalIgnoreCase)
                        && platformSDK.TargetPlatformVersion <= targetPlatformVersion) || platformSDK.ContainsPlatform(targetPlatformIdentifier, targetPlatformVersionString))
                .OrderBy<TargetPlatformSDK, Version>(platform => platform.TargetPlatformVersion);
        }

        /// <summary>
        /// Given an SDKName, targetPlatformIdentifier and TargetPlatformVersion search the default sdk locations for the passed in sdk name.
        /// The format of the sdk moniker is  SDKName, Version=X.X
        /// </summary>
        /// <param name="sdkMoniker">Name of the SDK to determine the installation location for.</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformExtensionSDKLocation(sdkMoniker, targetPlatformIdentifier, targetPlatformVersion, null, null);

        /// <summary>
        /// Given an SDKName, targetPlatformIdentifier and TargetPlatformVersion search the default sdk locations for the passed in sdk name.
        /// The format of the sdk moniker is  SDKName, Version=X.X
        /// </summary>
        /// <param name="sdkMoniker">Name of the SDK to determine the installation location for.</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, Version targetPlatformVersion, string[] diskRoots, string registryRoot)
            => GetPlatformExtensionSDKLocation(sdkMoniker, targetPlatformIdentifier, targetPlatformVersion, diskRoots, null, registryRoot);

        /// <summary>
        /// Given an SDKName, targetPlatformIdentifier and TargetPlatformVersion search the default sdk locations for the passed in sdk name.
        /// The format of the sdk moniker is  SDKName, Version=X.X
        /// </summary>
        /// <param name="sdkMoniker">Name of the SDK to determine the installation location for.</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="extensionDiskRoots">List of disk roots to look for manifest driven extension sdks</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, Version targetPlatformVersion, string[] diskRoots, string[] extensionDiskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);
            ErrorUtilities.VerifyThrowArgumentLength(sdkMoniker);

            IEnumerable<TargetPlatformSDK> targetPlatforms = RetrieveTargetPlatformList(diskRoots, extensionDiskRoots, registryRoot);
            var targetPlatformMoniker = targetPlatforms.Where<TargetPlatformSDK>(
                platform =>
                    (
                        string.IsNullOrEmpty(platform.TargetPlatformIdentifier)
                        ||
                        (platform.TargetPlatformIdentifier.Equals(targetPlatformIdentifier, StringComparison.OrdinalIgnoreCase)
                        && platform.TargetPlatformVersion <= targetPlatformVersion))
                    && platform.ExtensionSDKs.ContainsKey(sdkMoniker))
                .OrderByDescending<TargetPlatformSDK, Version>(platform => platform.TargetPlatformVersion)
                .DefaultIfEmpty(null).FirstOrDefault<TargetPlatformSDK>();

            if (targetPlatformMoniker != null)
            {
                return targetPlatformMoniker.ExtensionSDKs[sdkMoniker];
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Given an SDK moniker and the targeted platform get the path to the SDK root if it exists.
        /// </summary>
        /// <param name="sdkMoniker">Moniker for the sdk</param>
        /// <param name="targetPlatformIdentifier">Identifier for the platform</param>
        /// <param name="targetPlatformVersion">Version of the platform</param>
        /// <returns>A full path to the sdk root if the sdk exists in the targeted platform or an empty string if it does not exist.</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, string targetPlatformVersion)
            => GetPlatformExtensionSDKLocation(sdkMoniker, targetPlatformIdentifier, targetPlatformVersion, null, null);

        /// <summary>
        /// Given an SDKName, targetPlatformIdentifier and TargetPlatformVersion search the default sdk locations for the passed in sdk name.
        /// The format of the sdk moniker is  SDKName, Version=X.X
        /// </summary>
        /// <param name="sdkMoniker">Name of the SDK to determine the installation location for.</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string registryRoot)
            => GetPlatformExtensionSDKLocation(sdkMoniker, targetPlatformIdentifier, targetPlatformVersion, diskRoots, null, registryRoot);

        /// <summary>
        /// Given an SDKName, targetPlatformIdentifier and TargetPlatformVersion search the default sdk locations for the passed in sdk name.
        /// The format of the sdk moniker is  SDKName, Version=X.X
        /// </summary>
        /// <param name="sdkMoniker">Name of the SDK to determine the installation location for.</param>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="extensionDiskRoots">List of disk roots to look for manifest driven extension sdks</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformExtensionSDKLocation(string sdkMoniker, string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string extensionDiskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);

            string[] sdkDiskRoots = null;
            if (!string.IsNullOrEmpty(diskRoots))
            {
                sdkDiskRoots = diskRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
            }

            string[] extensionSdkDiskRoots = null;
            if (!string.IsNullOrEmpty(extensionDiskRoots))
            {
                extensionSdkDiskRoots = extensionDiskRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
            }

            string sdkLocation = string.Empty;
            if (Version.TryParse(targetPlatformVersion, out Version platformVersion))
            {
                sdkLocation = GetPlatformExtensionSDKLocation(sdkMoniker, targetPlatformIdentifier, platformVersion, sdkDiskRoots, extensionSdkDiskRoots, registryRoot);
            }

            return sdkLocation;
        }

        /// <summary>
        /// Gets a dictionary containing a collection of extension SDKs and filter it based on the target platform version
        /// if max platform version isn't set in the extension sdk manifest, add the extension sdk to the filtered list
        /// </summary>
        /// <param name="targetPlatformVersion"></param>
        /// <param name="extensionSdks"></param>
        /// <returns>A IDictionary collection of filtered extension SDKs</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Not worth breaking customers")]
        public static IDictionary<string, string> FilterPlatformExtensionSDKs(Version targetPlatformVersion, IDictionary<string, string> extensionSdks)
        {
            var filteredExtensionSdks = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> sdk in extensionSdks)
            {
                ExtensionSDK extensionSDK = new ExtensionSDK(sdk.Key, sdk.Value);

                // filter based on platform version - let pass if not in manifest or parameter
                if (extensionSDK.MaxPlatformVersion == null || targetPlatformVersion == null || extensionSDK.MaxPlatformVersion >= targetPlatformVersion)
                {
                    filteredExtensionSdks.Add(sdk.Key, sdk.Value);
                }
            }
            return filteredExtensionSdks;
        }

        /// <summary>
        /// Get the list of SDK folders which contains the references for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK</param>
        /// <returns>A list of folders in the order which they should be used when looking for references in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKReferenceFolders(string sdkRoot) => GetSDKReferenceFolders(sdkRoot, retailConfigurationName, neutralArchitectureName);

        /// <summary>
        /// Get the list of SDK folders which contains the references for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK</param>
        /// <param name="targetConfiguration">The configuration the SDK is targeting. This should be Debug or Retail</param>
        /// <param name="targetArchitecture">The architecture the SDK is targeting</param>
        /// <returns>A list of folders in the order which they should be used when looking for references in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKReferenceFolders(string sdkRoot, string targetConfiguration, string targetArchitecture)
        {
            ErrorUtilities.VerifyThrowArgumentLength(sdkRoot);
            ErrorUtilities.VerifyThrowArgumentLength(targetConfiguration);
            ErrorUtilities.VerifyThrowArgumentLength(targetArchitecture);

            var referenceDirectories = new List<string>(4);

            string legacyWindowsMetadataLocation = Path.Combine(sdkRoot, "Windows Metadata");
            if (FileUtilities.DirectoryExistsNoThrow(legacyWindowsMetadataLocation))
            {
                legacyWindowsMetadataLocation = FileUtilities.EnsureTrailingSlash(legacyWindowsMetadataLocation);
                referenceDirectories.Add(legacyWindowsMetadataLocation);
            }

            AddSDKPaths(sdkRoot, referencesFolderName, targetConfiguration, targetArchitecture, referenceDirectories);

            return referenceDirectories;
        }

        /// <summary>
        /// Add the set of paths for where sdk files should be found. Where &lt;folderType&gt; is redist, references, designtime
        /// </summary>
        private static void AddSDKPaths(string sdkRoot, string folderName, string targetConfiguration, string targetArchitecture, List<string> directories)
        {
            targetArchitecture = RemapSdkArchitecture(targetArchitecture);

            // <SDKROOT>\<folderType>\Debug\X86
            AddSDKPath(sdkRoot, folderName, targetConfiguration, targetArchitecture, directories);

            if (!neutralArchitectureName.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                // <SDKROOT>\<folderType>\Debug\Neutral
                AddSDKPath(sdkRoot, folderName, targetConfiguration, neutralArchitectureName, directories);
            }

            // <SDKROOT>\<folderType>\CommonConfiguration\x86
            AddSDKPath(sdkRoot, folderName, commonConfigurationFolderName, targetArchitecture, directories);

            if (!neutralArchitectureName.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                // <SDKROOT>\<folderType>\CommonConfiguration\Neutral
                AddSDKPath(sdkRoot, folderName, commonConfigurationFolderName, neutralArchitectureName, directories);
            }
        }

        /// <summary>
        /// Get the list of SDK folders which contains the redist files for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK must contain a redist folder</param>
        /// <returns>A list of folders in the order which they should be used when looking for redist files in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKRedistFolders(string sdkRoot) => GetSDKRedistFolders(sdkRoot, retailConfigurationName, neutralArchitectureName);

        /// <summary>
        /// Get the list of SDK folders which contains the redist files for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK must contain a redist folder</param>
        /// <param name="targetConfiguration">The configuration the SDK is targeting. This should be Debug or Retail</param>
        /// <param name="targetArchitecture">The architecture the SDK is targeting</param>
        /// <returns>A list of folders in the order which they should be used when looking for redist files in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKRedistFolders(string sdkRoot, string targetConfiguration, string targetArchitecture)
        {
            ErrorUtilities.VerifyThrowArgumentLength(sdkRoot);
            ErrorUtilities.VerifyThrowArgumentLength(targetConfiguration);
            ErrorUtilities.VerifyThrowArgumentLength(targetArchitecture);

            var redistDirectories = new List<string>(4);

            AddSDKPaths(sdkRoot, redistFolderName, targetConfiguration, targetArchitecture, redistDirectories);
            return redistDirectories;
        }

        /// <summary>
        /// Get the list of SDK folders which contains the designtime files for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK must contain a Designtime folder</param>
        /// <returns>A list of folders in the order which they should be used when looking for DesignTime files in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKDesignTimeFolders(string sdkRoot) => GetSDKDesignTimeFolders(sdkRoot, retailConfigurationName, neutralArchitectureName);

        /// <summary>
        /// Get the list of SDK folders which contains the DesignTime files for the sdk at the sdkRoot provided
        /// in the order in which they should be searched for references.
        /// </summary>
        /// <param name="sdkRoot">Root folder for the SDK must contain a DesignTime folder</param>
        /// <param name="targetConfiguration">The configuration the SDK is targeting. This should be Debug or Retail</param>
        /// <param name="targetArchitecture">The architecture the SDK is targeting</param>
        /// <returns>A list of folders in the order which they should be used when looking for DesignTime files in the SDK</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static IList<string> GetSDKDesignTimeFolders(string sdkRoot, string targetConfiguration, string targetArchitecture)
        {
            ErrorUtilities.VerifyThrowArgumentLength(sdkRoot);
            ErrorUtilities.VerifyThrowArgumentLength(targetConfiguration);
            ErrorUtilities.VerifyThrowArgumentLength(targetArchitecture);

            var designTimeDirectories = new List<string>(4);

            AddSDKPaths(sdkRoot, designTimeFolderName, targetConfiguration, targetArchitecture, designTimeDirectories);
            return designTimeDirectories;
        }

        /// <summary>
        /// Get a list target platform sdks on the machine.
        /// </summary>
        /// <returns>List of Target Platform SDKs, Item1: TargetPlatformName Item2: Version of SDK Item3: Path to sdk root</returns>
        public static IList<TargetPlatformSDK> GetTargetPlatformSdks() => GetTargetPlatformSdks(null, null);

        /// <summary>
        /// Get a list target platform sdks on the machine.
        /// </summary>
        /// <param name="diskRoots">List of disk locations to search for platform sdks</param>
        /// <param name="registryRoot">Registry root location to look for platform sdks</param>
        /// <returns>List of Target Platform SDKs</returns>
        public static IList<TargetPlatformSDK> GetTargetPlatformSdks(string[] diskRoots, string registryRoot)
        {
            IEnumerable<TargetPlatformSDK> targetPlatforms = RetrieveTargetPlatformList(diskRoots, null, registryRoot);
            return targetPlatforms.Where<TargetPlatformSDK>(platform => platform.Path != null).ToList<TargetPlatformSDK>();
        }

        /// <summary>
        /// Filter list of platform sdks based on minimum OS and VS versions
        /// </summary>
        /// <param name="targetPlatformSdkList">List of platform sdks</param>
        /// <param name="osVersion">Operating System version. Pass null to not filter based on this parameter</param>
        /// <param name="vsVersion">Visual Studio version. Pass null not to filter based on this parameter</param>
        /// <returns>List of Target Platform SDKs</returns>
        public static IList<TargetPlatformSDK> FilterTargetPlatformSdks(IList<TargetPlatformSDK> targetPlatformSdkList, Version osVersion, Version vsVersion)
        {
            var filteredTargetPlatformSdkList = new List<TargetPlatformSDK>();

            foreach (TargetPlatformSDK targetPlatformSdk in targetPlatformSdkList)
            {
                if (
                    (targetPlatformSdk.MinOSVersion == null || osVersion == null || targetPlatformSdk.MinOSVersion <= osVersion) && // filter based on OS version - let pass if not in manifest or parameter
                    (targetPlatformSdk.MinVSVersion == null || vsVersion == null || targetPlatformSdk.MinVSVersion <= vsVersion))    // filter based on VS version - let pass if not in manifest or parameter
                {
                    filteredTargetPlatformSdkList.Add(targetPlatformSdk);
                }
            }

            return filteredTargetPlatformSdkList;
        }

        /// <summary>
        /// Get the location of the target platform SDK props file for a given {SDKI, SDKV, TPI, TPMinV, TPV} combination.
        /// </summary>
        /// <param name="sdkIdentifier">The OneCore SDK identifier that defines OnceCore SDK root</param>
        /// <param name="sdkVersion">The verision of the OneCore SDK</param>
        /// <param name="targetPlatformIdentifier">Identifier for the targeted platform</param>
        /// <param name="targetPlatformMinVersion">The min version of the targeted platform</param>
        /// <param name="targetPlatformVersion">The version of the targeted platform</param>
        /// <returns>Location of the target platform SDK props file without .props filename</returns>
        public static string GetPlatformSDKPropsFileLocation(string sdkIdentifier, string sdkVersion, string targetPlatformIdentifier, string targetPlatformMinVersion, string targetPlatformVersion)
            => GetPlatformSDKPropsFileLocation(sdkIdentifier, sdkVersion, targetPlatformIdentifier, targetPlatformMinVersion, targetPlatformVersion, null, null);

        /// <summary>
        /// Get the location of the target platform SDK props file for a given {SDKI, SDKV, TPI, TPMinV, TPV} combination.
        /// </summary>
        /// <param name="sdkIdentifier">The OneCore SDK identifier that defines OnceCore SDK root</param>
        /// <param name="sdkVersion">The verision of the OneCore SDK</param>
        /// <param name="targetPlatformIdentifier">Identifier for the targeted platform</param>
        /// <param name="targetPlatformMinVersion">The min version of the targeted platform</param>
        /// <param name="targetPlatformVersion">The version of the targeted platform</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the target platform SDK props file without .props filename</returns>
        public static string GetPlatformSDKPropsFileLocation(string sdkIdentifier, string sdkVersion, string targetPlatformIdentifier, string targetPlatformMinVersion, string targetPlatformVersion, string diskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformVersion);

            string propsFileLocation;
            try
            {
                // e.g. C:\Program Files (x86)\Windows Kits\8.2
                string sdkRoot = GetPlatformSDKLocation(targetPlatformIdentifier, targetPlatformVersion, diskRoots, registryRoot);

                if (!string.IsNullOrEmpty(sdkRoot))
                {
                    // In the old SDK world, it is e.g. C:\Program Files (x86)\Windows Kits\8.2\DesignTime\CommonConfiguration\Neutral
                    // In OneCore SDK world, it is e.g. C:\Program Files (x86)\Windows Kits\10.0\DesignTime\CommonConfiguration\Neutral\UAP\0.8.0.0

                    if (string.IsNullOrEmpty(sdkIdentifier))
                    {
                        propsFileLocation = Path.Combine(sdkRoot, designTimeFolderName, commonConfigurationFolderName, neutralArchitectureName);
                    }
                    else
                    {
                        propsFileLocation = Path.Combine(sdkRoot, designTimeFolderName, commonConfigurationFolderName, neutralArchitectureName, targetPlatformIdentifier, targetPlatformVersion);
                    }

                    if (FileSystems.Default.DirectoryExists(propsFileLocation))
                    {
                        return propsFileLocation;
                    }
                    else
                    {
                        ErrorUtilities.DebugTraceMessage("GetPlatformSDKPropsFileLocation", "Target platform props file location '{0}' did not exist.", propsFileLocation);
                    }
                }
                else
                {
                    ErrorUtilities.DebugTraceMessage("GetPlatformSDKPropsFileLocation", "Could not find root SDK location for SDKI = '{0}', SDKV = '{1}'", sdkIdentifier, sdkVersion);
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                ErrorUtilities.DebugTraceMessage("GetPlatformSDKPropsFileLocation", "Encountered exception trying to get the SDK props file Location : {0}", e.Message);
            }

            return null;
        }

        /// <summary>
        /// Gathers the set of platform winmds for a particular {SDKI, SDKV, TPI, TPMinV, TPV} combination
        /// </summary>
        public static string[] GetTargetPlatformReferences(string sdkIdentifier, string sdkVersion, string targetPlatformIdentifier, string targetPlatformMinVersion, string targetPlatformVersion)
            => GetTargetPlatformReferences(sdkIdentifier, sdkVersion, targetPlatformIdentifier, targetPlatformMinVersion, targetPlatformVersion, null, null);

        /// <summary>
        /// Gathers the set of platform winmds for a particular {SDKI, SDKV, TPI, TPMinV, TPV} combination
        /// </summary>
        public static string[] GetTargetPlatformReferences(string sdkIdentifier, string sdkVersion, string targetPlatformIdentifier, string targetPlatformMinVersion, string targetPlatformVersion, string diskRoots, string registryRoot)
        {
            lock (s_locker)
            {
                if (s_cachedTargetPlatformReferences == null)
                {
                    s_cachedTargetPlatformReferences = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                }

                string cacheKey = string.Join("|", sdkIdentifier, sdkVersion, targetPlatformIdentifier, targetPlatformMinVersion, targetPlatformVersion, diskRoots, registryRoot);

                if (s_cachedTargetPlatformReferences.TryGetValue(cacheKey, out string[] targetPlatformReferences))
                {
                    return targetPlatformReferences;
                }

                if (string.IsNullOrEmpty(sdkIdentifier) && string.IsNullOrEmpty(sdkVersion))
                {
                    targetPlatformReferences = GetLegacyTargetPlatformReferences(targetPlatformIdentifier, targetPlatformVersion, diskRoots, registryRoot);
                }
                else
                {
                    targetPlatformReferences = GetTargetPlatformReferencesFromManifest(sdkIdentifier, sdkVersion, targetPlatformIdentifier, targetPlatformMinVersion, targetPlatformVersion, diskRoots, registryRoot);
                }

                s_cachedTargetPlatformReferences.Add(cacheKey, targetPlatformReferences);
                return targetPlatformReferences;
            }
        }

        /// <summary>
        /// Gathers the specified extension SDK references for the given target SDK
        /// </summary>
        /// <param name="extensionSdkMoniker">The moniker is the Name/Version string. Example: "Windows Desktop, Version=10.0.0.1"</param>
        /// <param name="targetSdkIdentifier">The target SDK name.</param>
        /// <param name="targetSdkVersion">The target SDK version.</param>
        /// <param name="diskRoots">The disk roots used to gather installed SDKs.</param>
        /// <param name="extensionDiskRoots">The disk roots used to gather installed extension SDKs.</param>
        /// <param name="registryRoot">The registry root used to gather installed extension SDKs.</param>
        public static string[] GetPlatformOrFrameworkExtensionSdkReferences(string extensionSdkMoniker, string targetSdkIdentifier, string targetSdkVersion, string diskRoots, string extensionDiskRoots, string registryRoot)
            => GetPlatformOrFrameworkExtensionSdkReferences(
                extensionSdkMoniker,
                targetSdkIdentifier,
                targetSdkVersion,
                diskRoots,
                extensionDiskRoots,
                registryRoot,
                targetPlatformIdentifier: null,
                targetPlatformVersion: null);

        /// <summary>
        /// Gathers the specified extension SDK references for the given target SDK
        /// </summary>
        /// <param name="extensionSdkMoniker">The moniker is the Name/Version string. Example: "Windows Desktop, Version=10.0.0.1"</param>
        /// <param name="targetSdkIdentifier">The target SDK name.</param>
        /// <param name="targetSdkVersion">The target SDK version.</param>
        /// <param name="targetPlatformIdentifier">The target platform name.</param>
        /// <param name="targetPlatformVersion">The target platform version.</param>
        /// <param name="diskRoots">The disk roots used to gather installed SDKs.</param>
        /// <param name="extensionDiskRoots">The disk roots used to gather installed extension SDKs.</param>
        /// <param name="registryRoot">The registry root used to gather installed extension SDKs.</param>
        public static string[] GetPlatformOrFrameworkExtensionSdkReferences(
                string extensionSdkMoniker,
                string targetSdkIdentifier,
                string targetSdkVersion,
                string diskRoots,
                string extensionDiskRoots,
                string registryRoot,
                string targetPlatformIdentifier,
                string targetPlatformVersion)
        {
            lock (s_locker)
            {
                if (s_cachedExtensionSdkReferences == null)
                {
                    s_cachedExtensionSdkReferences = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                }

                string cacheKey = string.Join("|", extensionSdkMoniker, targetSdkIdentifier, targetSdkVersion);

                if (s_cachedExtensionSdkReferences.TryGetValue(cacheKey, out string[] extensionSdkReferences))
                {
                    return extensionSdkReferences;
                }

                TargetPlatformSDK matchingSdk = GetMatchingPlatformSDK(targetSdkIdentifier, targetSdkVersion, diskRoots, extensionDiskRoots, registryRoot);

                if (matchingSdk == null)
                {
                    ErrorUtilities.DebugTraceMessage("GetExtensionSdkReferences", "Could not find root SDK for SDKI = '{0}', SDKV = '{1}'", targetSdkIdentifier, targetSdkVersion);
                }
                else
                {
                    string targetSdkPath = matchingSdk.Path;
                    string platformVersion = GetPlatformVersion(matchingSdk, targetPlatformIdentifier, targetPlatformVersion);

                    if (matchingSdk.ExtensionSDKs.TryGetValue(extensionSdkMoniker, out string extensionSdkPath)
                        ||
                        (s_cachedExtensionSdks.TryGetValue(extensionDiskRoots, out matchingSdk)
                        && matchingSdk.ExtensionSDKs.TryGetValue(extensionSdkMoniker, out extensionSdkPath)))
                    {
                        ExtensionSDK extensionSdk = new ExtensionSDK(extensionSdkMoniker, extensionSdkPath);
                        if (extensionSdk.SDKType == SDKType.Framework || extensionSdk.SDKType == SDKType.Platform)
                        {
                            // We don't want to attempt to gather ApiContract references if the framework isn't explicitly marked as Framework/Platform
                            extensionSdkReferences = GetApiContractReferences(extensionSdk.ApiContracts, targetSdkPath, platformVersion);
                        }
                    }
                    else
                    {
                        ErrorUtilities.DebugTraceMessage("GetExtensionSdkReferences", "Could not find matching extension SDK = '{0}'", extensionSdkMoniker);
                    }
                }

                s_cachedExtensionSdkReferences.Add(cacheKey, extensionSdkReferences);
                return extensionSdkReferences;
            }
        }

        /// <summary>
        /// Get platform version string which is used to generate versioned path
        /// </summary>
        /// <param name="targetSdk">The target SDK</param>
        /// <param name="targetPlatformIdentifier">The target platform name.</param>
        /// <param name="targetPlatformVersion">The target platform version.</param>
        /// <returns>Return the version string if the platform is versioned, otherwise return empty string</returns>
        private static string GetPlatformVersion(TargetPlatformSDK targetSdk, string targetPlatformIdentifier, string targetPlatformVersion)
        {
            string platformKey = TargetPlatformSDK.GetSdkKey(targetPlatformIdentifier, targetPlatformVersion);
            if (TryGetPlatformManifest(targetSdk, platformKey, out var manifest) && manifest?.VersionedContent == true)
            {
                return manifest.PlatformVersion;
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Gathers the set of platform winmds based on the assumption that they come from
        /// an SDK that is specified solely by TPI / TPV.
        /// </summary>
        private static string[] GetLegacyTargetPlatformReferences(string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformVersion);

            try
            {
                // TODO: Add caching so that we only have to read all this stuff in once.
                string sdkRoot = GetPlatformSDKLocation(targetPlatformIdentifier, targetPlatformVersion, diskRoots, registryRoot);
                string winmdLocation = null;

                if (!string.IsNullOrEmpty(sdkRoot))
                {
                    winmdLocation = Path.Combine(sdkRoot, referencesFolderName, commonConfigurationFolderName, neutralArchitectureName);

                    if (!FileSystems.Default.DirectoryExists(winmdLocation))
                    {
                        ErrorUtilities.DebugTraceMessage("GetLegacyTargetPlatformReferences", "Target platform location '{0}' did not exist", winmdLocation);
                        winmdLocation = null;
                    }
                }
                else
                {
                    ErrorUtilities.DebugTraceMessage("GetLegacyTargetPlatformReferences", "Could not find root SDK location for TPI = '{0}', TPV = '{1}'", targetPlatformIdentifier, targetPlatformVersion);
                }

                if (!string.IsNullOrEmpty(winmdLocation))
                {
                    string[] winmdPaths = Directory.GetFiles(winmdLocation, "*.winmd");

                    if (winmdPaths.Length > 0)
                    {
                        ErrorUtilities.DebugTraceMessage("GetLegacyTargetPlatformReferences", "Found {0} contract winmds in '{1}'", winmdPaths.Length, winmdLocation);
                        return winmdPaths;
                    }
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                ErrorUtilities.DebugTraceMessage("GetLegacyTargetPlatformReferences", "Encountered exception trying to gather the platform references: {0}", e.Message);
            }

            return [];
        }

        /// <summary>
        /// Gathers the set of platform winmds for a particular {SDKI, SDKV, TPI, TPMinV, TPV} combination,
        /// based on the assumption that it is an SDK that has both {SDKI, SDKV} and TP* specifiers.
        /// </summary>
        private static string[] GetTargetPlatformReferencesFromManifest(
                string sdkIdentifier,
                string sdkVersion,
                string targetPlatformIdentifier,
                string targetPlatformMinVersion,
                string targetPlatformVersion,
                string diskRoots,
                string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentLength(sdkIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(sdkVersion);
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformVersion);

            string[] contractWinMDs = [];

            TargetPlatformSDK matchingSdk = GetMatchingPlatformSDK(targetPlatformIdentifier, targetPlatformVersion, diskRoots, null, registryRoot);
            string platformKey = TargetPlatformSDK.GetSdkKey(targetPlatformIdentifier, targetPlatformVersion);
            if (TryGetPlatformManifest(matchingSdk, platformKey, out PlatformManifest manifest))
            {
                if (manifest.VersionedContent)
                {
                    contractWinMDs = GetApiContractReferences(manifest.ApiContracts, matchingSdk.Path, manifest.PlatformVersion);
                }
                else
                {
                    contractWinMDs = GetApiContractReferences(manifest.ApiContracts, matchingSdk.Path);
                }
            }

            return contractWinMDs;
        }

        /// <summary>
        /// Return the WinMD paths referenced by the given api contracts and target sdk root
        /// </summary>
        /// <param name="apiContracts">The API contract definitions</param>
        /// <param name="targetPlatformSdkRoot">The root of the target platform SDK</param>
        /// <returns>List of matching WinMDs</returns>
        internal static string[] GetApiContractReferences(IEnumerable<ApiContract> apiContracts, string targetPlatformSdkRoot) => GetApiContractReferences(apiContracts, targetPlatformSdkRoot, string.Empty);

        /// <summary>
        /// Return the WinMD paths referenced by the given api contracts and target sdk root
        /// </summary>
        /// <param name="apiContracts">The API contract definitions</param>
        /// <param name="targetPlatformSdkRoot">The root of the target platform SDK</param>
        /// <param name="targetPlatformSdkVersion">The version of the target platform SDK</param>
        /// <returns>List of matching WinMDs</returns>
        internal static string[] GetApiContractReferences(IEnumerable<ApiContract> apiContracts, string targetPlatformSdkRoot, string targetPlatformSdkVersion)
        {
            if (apiContracts == null)
            {
                return [];
            }

            var contractWinMDs = new List<string>();

            string referencesRoot = Path.Combine(targetPlatformSdkRoot, referencesFolderName, targetPlatformSdkVersion);

            foreach (ApiContract contract in apiContracts)
            {
                ErrorUtilities.DebugTraceMessage("GetApiContractReferences", "Gathering contract references for contract with name '{0}' and version '{1}", contract.Name, contract.Version);
                string contractPath = Path.Combine(referencesRoot, contract.Name, contract.Version);

                if (FileSystems.Default.DirectoryExists(contractPath))
                {
                    string[] winmdPaths = Directory.GetFiles(contractPath, "*.winmd");

                    if (winmdPaths.Length > 0)
                    {
                        ErrorUtilities.DebugTraceMessage("GetApiContractReferences", "Found {0} contract winmds in '{1}'", winmdPaths.Length, contractPath);
                        contractWinMDs.AddRange(winmdPaths);
                    }
                }
            }

            return contractWinMDs.ToArray();
        }

        private static bool TryGetPlatformManifest(TargetPlatformSDK matchingSdk, string platformKey, out PlatformManifest manifest)
        {
            manifest = null;
            try
            {
                string platformManifestLocation = null;

                if (matchingSdk != null)
                {
                    if (!matchingSdk.Platforms.TryGetValue(platformKey, out platformManifestLocation))
                    {
                        ErrorUtilities.DebugTraceMessage("GetPlatformManifest", "Target platform location '{0}' did not exist or did not contain Platform.xml", platformManifestLocation);
                    }
                }
                else
                {
                    ErrorUtilities.DebugTraceMessage("GetPlatformManifest", "Could not find root SDK for '{0}'", platformKey);
                }

                if (!string.IsNullOrEmpty(platformManifestLocation))
                {
                    manifest = new PlatformManifest(platformManifestLocation);

                    if (!manifest.ReadError)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                ErrorUtilities.DebugTraceMessage("GetValueUsingMatchingSDKManifest", "Encountered exception trying to check if SDK is versioned: {0}", e.Message);
            }

            return false;
        }

        /// <summary>
        /// Return the versioned/unversioned SDK content folder path
        /// </summary>
        /// <param name="sdkIdentifier">The identifier of the SDK</param>
        /// <param name="sdkVersion">The verision of the SDK</param>
        /// <param name="targetPlatformIdentifier">The identifier of the targeted platform</param>
        /// <param name="targetPlatformMinVersion">The min version of the targeted platform</param>
        /// <param name="targetPlatformVersion">The version of the targeted platform</param>
        /// <param name="folderName">The content folder name under SDK path</param>
        /// <param name="diskRoot">An optional disk root to search.  A value should only be passed from a unit test.</param>
        /// <returns>The SDK content folder path</returns>
        public static string GetSDKContentFolderPath(
              string sdkIdentifier,
              string sdkVersion,
              string targetPlatformIdentifier,
              string targetPlatformMinVersion,
              string targetPlatformVersion,
              string folderName,
              string diskRoot = null)
        {
            ErrorUtilities.VerifyThrowArgumentLength(sdkIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(sdkVersion);

            // Avoid exception in Path.Combine
            if (folderName == null)
            {
                folderName = string.Empty;
            }

            // If no folder name is input or it isn't UWP SDK, return the root SDK path.
            if (string.IsNullOrWhiteSpace(folderName) || sdkVersion != "10.0" || !string.Equals(sdkIdentifier, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                string sdkLocation = GetPlatformSDKLocation(sdkIdentifier, sdkVersion);
                return Path.Combine(sdkLocation, folderName);
            }

            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformVersion);

            string sdkContentFolderPath = null;

            TargetPlatformSDK matchingSdk = GetMatchingPlatformSDK(targetPlatformIdentifier, targetPlatformVersion, diskRoot, null, null);
            string platformKey = TargetPlatformSDK.GetSdkKey(targetPlatformIdentifier, targetPlatformVersion);
            if (TryGetPlatformManifest(matchingSdk, platformKey, out PlatformManifest manifest))
            {
                sdkContentFolderPath = manifest.VersionedContent
                    ? Path.Combine(matchingSdk.Path, folderName, targetPlatformVersion)
                    : Path.Combine(matchingSdk.Path, folderName);
            }

            return sdkContentFolderPath;
        }

        /// <summary>
        /// Given a target platform identifier and a target platform version search the default sdk locations for the platform sdk for the target platform.
        /// </summary>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformSDKLocation(string targetPlatformIdentifier, Version targetPlatformVersion)
            => GetPlatformSDKLocation(targetPlatformIdentifier, targetPlatformVersion, null, null);

        /// <summary>
        /// Given a target platform identifier and a target platform version search the default sdk locations for the platform sdk for the target platform.
        /// </summary>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformSDKLocation(string targetPlatformIdentifier, Version targetPlatformVersion, string[] diskRoots, string registryRoot)
        {
            var targetPlatform = GetMatchingPlatformSDK(targetPlatformIdentifier, targetPlatformVersion, diskRoots, null, registryRoot);
            return targetPlatform?.Path ?? string.Empty;
        }

        /// <summary>
        /// Given a target platform identifier and a target platform version search the default sdk locations for the platform sdk for the target platform.
        /// </summary>
        /// <param name="targetPlatformIdentifier">Identifier for the platform</param>
        /// <param name="targetPlatformVersion">Version of the platform</param>
        /// <returns>A full path to the sdk root if the sdk exists in the targeted platform or an empty string if it does not exist.</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformSDKLocation(string targetPlatformIdentifier, string targetPlatformVersion)
            => GetPlatformSDKLocation(targetPlatformIdentifier, targetPlatformVersion, null, null);

        /// <summary>
        /// Given a target platform identifier and a target platform version search the default sdk locations for the platform sdk for the target platform.
        /// </summary>
        /// <param name="targetPlatformIdentifier">Targeted platform to find SDKs for</param>
        /// <param name="targetPlatformVersion">Targeted platform version to find SDKs for</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>Location of the platform SDK if it is found, empty string if it could not be found</returns>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static string GetPlatformSDKLocation(string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);

            string[] sdkDiskRoots = null;
            if (!string.IsNullOrEmpty(diskRoots))
            {
                sdkDiskRoots = diskRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
            }

            string sdkLocation = string.Empty;
            if (Version.TryParse(targetPlatformVersion, out Version platformVersion))
            {
                sdkLocation = GetPlatformSDKLocation(targetPlatformIdentifier, platformVersion, sdkDiskRoots, registryRoot);
            }

            return sdkLocation;
        }

        /// <summary>
        /// Given a target platform identifier and version, get the display name for that platform SDK.
        /// </summary>
        public static string GetPlatformSDKDisplayName(string targetPlatformIdentifier, string targetPlatformVersion) => GetPlatformSDKDisplayName(targetPlatformIdentifier, targetPlatformVersion, null, null);

        /// <summary>
        /// Given a target platform identifier and version, get the display name for that platform SDK.
        /// </summary>
        public static string GetPlatformSDKDisplayName(string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string registryRoot)
        {
            TargetPlatformSDK targetPlatform = GetMatchingPlatformSDK(targetPlatformIdentifier, targetPlatformVersion, diskRoots, null, registryRoot);
            return targetPlatform?.DisplayName ?? GenerateDefaultSDKDisplayName(targetPlatformIdentifier, targetPlatformVersion);
        }

        /// <summary>
        /// Given an SDK identifier and an SDK version, return a list of installed platforms.
        /// </summary>
        /// <param name="sdkIdentifier">SDK for which to find the installed platforms</param>
        /// <param name="sdkVersion">SDK version for which to find the installed platforms</param>
        /// <returns>A list of keys for the installed platforms for the given SDK</returns>
        public static IEnumerable<string> GetPlatformsForSDK(string sdkIdentifier, Version sdkVersion) => GetPlatformsForSDK(sdkIdentifier, sdkVersion, null, null);

        /// <summary>
        /// Given an SDK identifier and an SDK version, return a list of installed platforms.
        /// </summary>
        /// <param name="sdkIdentifier">SDK for which to find the installed platforms</param>
        /// <param name="sdkVersion">SDK version for which to find the installed platforms</param>
        /// <param name="diskRoots">List of disk roots to search for sdks within</param>
        /// <param name="registryRoot">Registry root to look for sdks within</param>
        /// <returns>A list of keys for the installed platforms for the given SDK</returns>
        public static IEnumerable<string> GetPlatformsForSDK(string sdkIdentifier, Version sdkVersion, string[] diskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentNull(sdkIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(sdkVersion);

            IEnumerable<TargetPlatformSDK> targetPlatformSDKs = RetrieveTargetPlatformList(diskRoots, null, registryRoot);

            var platforms = new List<string>();
            foreach (TargetPlatformSDK sdk in targetPlatformSDKs)
            {
                bool isSDKMatch = string.Equals(sdk.TargetPlatformIdentifier, sdkIdentifier, StringComparison.OrdinalIgnoreCase) && Equals(sdk.TargetPlatformVersion, sdkVersion);
                if (!isSDKMatch || sdk.Platforms == null)
                {
                    continue;
                }

                foreach (string platform in sdk.Platforms.Keys)
                {
                    platforms.Add(platform);
                }
            }

            return platforms;
        }
        /// <summary>
        /// Given an SDK Identifier and SDK version, return the latest installed platform.
        /// </summary>
        /// <param name="sdkIdentifier">SDK for which to find the latest installed platform</param>
        /// <param name="sdkVersion">SDK version for which to find the latest installed platform</param>
        /// <returns>The latest installed version for the given SDK</returns>
        public static string GetLatestSDKTargetPlatformVersion(string sdkIdentifier, string sdkVersion) => GetLatestSDKTargetPlatformVersion(sdkIdentifier, sdkVersion, null);

        /// <summary>
        /// Given an SDK Identifier and SDK version, return the latest installed platform.
        /// </summary>
        /// <param name="sdkIdentifier">SDK for which to find the latest installed platform</param>
        /// <param name="sdkVersion">SDK version for which to find the latest installed platform</param>
        /// <param name="sdkRoots">SDK Root folders</param>
        /// <returns>The latest installed version for the given SDK</returns>
        public static string GetLatestSDKTargetPlatformVersion(string sdkIdentifier, string sdkVersion, string[] sdkRoots)
        {
            ErrorUtilities.VerifyThrowArgumentNull(sdkIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(sdkVersion);

            var availablePlatformVersions = new List<Version>();
            IEnumerable<string> platformMonikerList = GetPlatformsForSDK(sdkIdentifier, new Version(sdkVersion), sdkRoots, null);

            Version platformVersion;
            foreach (string platformMoniker in platformMonikerList)
            {
                if (TryParsePlatformVersion(platformMoniker, out platformVersion))
                {
                    availablePlatformVersions.Add(platformVersion);
                }
            }

            if (availablePlatformVersions?.Count > 0)
            {
                return availablePlatformVersions.OrderByDescending(x => x).FirstOrDefault().ToString();
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        ///  Returns folders in VS installs of specifid range of versions, starting with 15.0.
        /// </summary>
        /// <param name="minVersion">Optional. If specified, only VS instances with this or bigger version will be included.</param>
        /// <param name="maxVersion">Optional. If specified, only VS instances with smaller versions will be included. For instance, 16.0 means that only 15.* versions will be included.</param>
        /// <param name="subFolder">Optional. If specified, the returned list will contain [VSInstallDir][subFolder] paths</param>
        /// <returns>Returns folders in VS installs in VS version descending order</returns>
        public static IEnumerable<string> GetFoldersInVSInstalls(Version minVersion = null, Version maxVersion = null, string subFolder = null)
        {
            string versionRange = $"{minVersion?.ToString() ?? string.Empty};{maxVersion?.ToString() ?? string.Empty}";
            string[] vsInstallFolders;

            lock (s_locker)
            {
                if (s_vsInstallFolders == null)
                {
                    s_vsInstallFolders = new Dictionary<string, string[]>();
                }

                if (!s_vsInstallFolders.TryGetValue(versionRange, out vsInstallFolders))
                {
                    IList<VisualStudioInstance> vsInstances = VisualStudioLocationHelper.GetInstances();

                    var vsInstancePaths = vsInstances
                        .Where(i => (minVersion == null || i.Version >= minVersion) && (maxVersion == null || i.Version < maxVersion))
                        .OrderByDescending(i => i.Version)
                        .Select(i => i.Path);

                    vsInstallFolders = vsInstancePaths.ToArray();
                    s_vsInstallFolders[versionRange] = vsInstallFolders;
                }
            }

            var folders = string.IsNullOrEmpty(subFolder) ? vsInstallFolders : vsInstallFolders.Select(i => Path.Combine(i, subFolder));

            return folders;
        }

        /// <summary>
        ///  Returns folders in VS installs of specified range of versions (starting with 15.0) separated by ';'.
        /// </summary>
        /// <param name="minVersionString">Optional. If specified, only VS instances with this or bigger version will be included.</param>
        /// <param name="maxVersionString">Optional. If specified, only VS instances with smaller versions will be included. For instance, 16.0 means that only 15.* versions will be included.</param>
        /// <param name="subFolder">Optional. If specified, the returned list will contain [VSInstallDir][subFolder] paths</param>
        /// <returns>Returns folders in VS installs in VS version descending order separated by ';'</returns>
        public static string GetFoldersInVSInstallsAsString(string minVersionString = null, string maxVersionString = null, string subFolder = null)
        {
            string foldersString = string.Empty;

            try
            {
                Version minVersion, maxVersion;

                if (string.IsNullOrEmpty(minVersionString) || !Version.TryParse(minVersionString, out minVersion))
                {
                    minVersion = null;
                }
                if (string.IsNullOrEmpty(maxVersionString) || !Version.TryParse(maxVersionString, out maxVersion))
                {
                    maxVersion = null;
                }

                var folders = GetFoldersInVSInstalls(minVersion, maxVersion, subFolder);

                if (folders.Any())
                {
                    foldersString = string.Join(";", folders);
                }
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                // This method will be used in vc props and we don't want to fail project load if it throws for some non critical reason.
            }

            return foldersString;
        }

        /// <summary>
        /// Finds first folder in the list which contains all given files. Returns an empty string if not found.
        /// </summary>
        /// <param name="possibleRoots">Root folders separated by ';'</param>
        /// <param name="relativeFilePaths">Relative file paths to find under root folders, separated by ';'.</param>
        /// <returns>The first root folder in the given list, which contains all files. Empty string if not found.</returns>
        public static string FindRootFolderWhereAllFilesExist(string possibleRoots, string relativeFilePaths)
        {
            if (!string.IsNullOrEmpty(possibleRoots))
            {
                var roots = possibleRoots.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);
                var files = relativeFilePaths.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);

                bool allFilesFound;
                foreach (var root in roots)
                {
                    allFilesFound = true;
                    foreach (var file in files)
                    {
                        try
                        {
                            string fullPath = Path.Combine(root, file);

                            if (!FileSystems.Default.FileExists(fullPath))
                            {
                                allFilesFound = false;
                                break;
                            }
                        }
                        catch (ArgumentException)
                        {
                            allFilesFound = false;
                            break;
                        }
                    }

                    if (allFilesFound)
                    {
                        return root;
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Tries to parse the "version" out of a platformMoniker.
        /// </summary>
        /// <param name="platformMoniker">PlatformMoniker, in the form "PlatformName, Version=version"</param>
        /// <param name="platformVersion">The version of the platform, if the parse was successful - Else set to null</param>
        /// <returns>True if parse was successful, false otherwise </returns>
        private static bool TryParsePlatformVersion(string platformMoniker, out Version platformVersion)
        {
            platformVersion = null;
            FrameworkNameVersioning framework = null;
            try
            {
                framework = new FrameworkNameVersioning(platformMoniker);
            }
            catch (ArgumentException e)
            {
                ErrorUtilities.DebugTraceMessage("TryParsePlatformVersion", "Cannot create FrameworkName object, Exception:{0}", e.Message);
            }

            if (framework != null)
            {
                platformVersion = framework.Version;
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Given a target platform identifier and version and locations in which to search, find the TargetPlatformSDK
        /// object that matches.
        /// </summary>
        private static TargetPlatformSDK GetMatchingPlatformSDK(string targetPlatformIdentifier, string targetPlatformVersion, string diskRoots, string multiPlatformDiskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);

            string[] sdkDiskRoots = null;
            if (!string.IsNullOrEmpty(diskRoots))
            {
                sdkDiskRoots = diskRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
            }

            string[] sdkmultiPlatformDiskRoots = null;
            if (!string.IsNullOrEmpty(multiPlatformDiskRoots))
            {
                sdkmultiPlatformDiskRoots = multiPlatformDiskRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
            }

            if (Version.TryParse(targetPlatformVersion, out Version platformVersion))
            {
                return GetMatchingPlatformSDK(targetPlatformIdentifier, platformVersion, sdkDiskRoots, sdkmultiPlatformDiskRoots, registryRoot);
            }

            return null;
        }

        /// <summary>
        /// Given a target platform identifier and version and locations in which to search, find the TargetPlatformSDK
        /// object that matches.
        /// </summary>
        private static TargetPlatformSDK GetMatchingPlatformSDK(string targetPlatformIdentifier, Version targetPlatformVersion, string[] diskRoots, string[] multiPlatformDiskRoots, string registryRoot)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetPlatformIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(targetPlatformVersion);

            IEnumerable<TargetPlatformSDK> targetPlatforms = RetrieveTargetPlatformList(diskRoots, multiPlatformDiskRoots, registryRoot);

            TargetPlatformSDK matchingSdk = targetPlatforms
                .FirstOrDefault<TargetPlatformSDK>(platform =>
                    string.Equals(platform.TargetPlatformIdentifier, targetPlatformIdentifier, StringComparison.OrdinalIgnoreCase)
                    && Equals(platform.TargetPlatformVersion, targetPlatformVersion));

            // For UAP platforms match against registered platforms...
            // Logic is same as used for managed UAP projects
            // vsproject\flavors\ProjectFlavoring\Microsoft.VisualStudio.ProjectFlavoring\Microsoft\VisualStudio\ProjectFlavoring\Retargeting\Management\VsMultiTargetingPlatformProvider.cs:FindPlatformSdk
            if (matchingSdk == null)
            {
                string versionString = targetPlatformVersion.ToString();
                matchingSdk = targetPlatforms.FirstOrDefault<TargetPlatformSDK>(platform => platform.ContainsPlatform(targetPlatformIdentifier, versionString));
            }
            return matchingSdk;
        }

        /// <summary>
        /// Given a target platform identifier and version, generate a reasonable default display name.
        /// </summary>
        /// <param name="targetPlatformIdentifier"></param>
        /// <param name="targetPlatformVersion"></param>
        private static string GenerateDefaultSDKDisplayName(string targetPlatformIdentifier, string targetPlatformVersion) => targetPlatformIdentifier + " " + targetPlatformVersion;

        /// <summary>
        /// Gets the fully qualified path to the system directory i.e. %SystemRoot%\System32
        /// </summary>
        /// <returns>The system path.</returns>
        public static string PathToSystem
        {
            get
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.System);
            }
        }

        /// <summary>
        /// Returns the prefix of the .NET Framework version folder (e.g. "v2.0")
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns></returns>
        public static string GetDotNetFrameworkVersionFolderPrefix(TargetDotNetFrameworkVersion version) => FrameworkLocationHelper.GetDotNetFrameworkVersionFolderPrefix(TargetDotNetFrameworkVersionToSystemVersion(version));

        /// <summary>
        /// Returns the full name of the .NET Framework root registry key
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns></returns>
        public static string GetDotNetFrameworkRootRegistryKey(TargetDotNetFrameworkVersion version) => FrameworkLocationHelper.fullDotNetFrameworkRegistryKey;

        /// <summary>
        /// Returns the full name of the .NET Framework SDK root registry key.  When targeting .NET 3.5 or
        /// above, looks in the locations associated with Visual Studio 2010.  If you wish to target the
        /// .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override that
        /// specifies a VisualStudioVersion.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        public static string GetDotNetFrameworkSdkRootRegistryKey(TargetDotNetFrameworkVersion version) => GetDotNetFrameworkSdkRootRegistryKey(version, VisualStudioVersion.VersionLatest);

        /// <summary>
        /// Returns the full name of the .NET Framework SDK root registry key
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio the requested SDK is associated with</param>
        public static string GetDotNetFrameworkSdkRootRegistryKey(TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
        {
            var dotNetFrameworkVersion = TargetDotNetFrameworkVersionToSystemVersion(version);
            var vsVersion = VisualStudioVersionToSystemVersion(visualStudioVersion);
            return FrameworkLocationHelper.GetDotNetFrameworkSdkRootRegistryKey(dotNetFrameworkVersion, vsVersion);
        }

        /// <summary>
        /// Name of the value of GetDotNetFrameworkRootRegistryKey that contains the SDK install root path. When
        /// targeting .NET 3.5 or above, looks in the locations associated with Visual Studio 2010.  If you wish
        /// to target the .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override
        /// that specifies a VisualStudioVersion.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        public static string GetDotNetFrameworkSdkInstallKeyValue(TargetDotNetFrameworkVersion version) => GetDotNetFrameworkSdkInstallKeyValue(version, VisualStudioVersion.VersionLatest);

        /// <summary>
        /// Name of the value of GetDotNetFrameworkRootRegistryKey that contains the SDK install root path
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio the requested SDK is associated with</param>
        public static string GetDotNetFrameworkSdkInstallKeyValue(TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
        {
            var dotNetFrameworkVersion = TargetDotNetFrameworkVersionToSystemVersion(version);
            var vsVersion = VisualStudioVersionToSystemVersion(visualStudioVersion);
            return FrameworkLocationHelper.GetDotNetFrameworkSdkInstallKeyValue(dotNetFrameworkVersion, vsVersion);
        }

        /// <summary>
        /// Get a fully qualified path to the frameworks root directory.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns>Will return 'null' if there is no target frameworks on this machine.</returns>
        public static string GetPathToDotNetFramework(TargetDotNetFrameworkVersion version) => GetPathToDotNetFramework(version, UtilitiesDotNetFrameworkArchitecture.Current);

        /// <summary>
        /// Get a fully qualified path to the framework's root directory.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="architecture">Desired architecture, or DotNetFrameworkArchitecture.Current for the architecture this process is currently running under.</param>
        /// <returns></returns>
        public static string GetPathToDotNetFramework(TargetDotNetFrameworkVersion version, UtilitiesDotNetFrameworkArchitecture architecture)
        {
            Version frameworkVersion = TargetDotNetFrameworkVersionToSystemVersion(version);
            SharedDotNetFrameworkArchitecture sharedArchitecture = ConvertToSharedDotNetFrameworkArchitecture(architecture);
            return FrameworkLocationHelper.GetPathToDotNetFramework(frameworkVersion, sharedArchitecture);
        }

        /// <summary>
        /// Returns the path to the "bin" directory of the latest .NET Framework SDK. When targeting .NET 3.5
        /// or above, looks in the locations associated with Visual Studio 2010.  If you wish to target
        /// the .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override
        /// that specifies a VisualStudioVersion.
        /// </summary>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdk() => GetPathToDotNetFrameworkSdk(TargetDotNetFrameworkVersion.Latest);

        /// <summary>
        /// Returns the path to the "bin" directory of the .NET Framework SDK. When targeting .NET 3.5
        /// or above, looks in the locations associated with Visual Studio 2010.  If you wish to target
        /// the .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override
        /// that specifies a VisualStudioVersion.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdk(TargetDotNetFrameworkVersion version) => GetPathToDotNetFrameworkSdk(version, VisualStudioVersion.VersionLatest);

        /// <summary>
        /// Returns the path to the .NET Framework SDK.
        /// </summary>
        /// <param name="version">The <see cref="TargetDotNetFrameworkVersion"/> of the .NET Framework.</param>
        /// <param name="visualStudioVersion">The <see cref="VisualStudioVersion"/> of Visual Studio.</param>
        /// <returns></returns>
        public static string GetPathToDotNetFrameworkSdk(TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
        {
            var dotNetFrameworkVersion = TargetDotNetFrameworkVersionToSystemVersion(version);
            var vsVersion = VisualStudioVersionToSystemVersion(visualStudioVersion);
            return FrameworkLocationHelper.GetPathToDotNetFrameworkSdk(dotNetFrameworkVersion, vsVersion);
        }

        /// <summary>
        /// Returns the path to the reference assemblies location for the given framework version.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkReferenceAssemblies(TargetDotNetFrameworkVersion version)
            => FrameworkLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies(TargetDotNetFrameworkVersionToSystemVersion(version));

        /// <summary>
        /// Returns the path to the reference assemblies location for the given target framework's standard libraries (i.e. mscorlib).
        /// This method will assume the requested ReferenceAssemblyRoot path will be the ProgramFiles directory specified by Environment.SpecialFolder.ProgramFiles
        /// In additon when the .NETFramework or .NET Framework targetFrameworkIdentifiers are seen and targetFrameworkVersion is 2.0, 3.0, 3.5 or 4.0 we will return the correctly chained reference assembly paths
        /// for the legacy .net frameworks. This chaining will use the existing GetPathToDotNetFrameworkReferenceAssemblies to build up the list of reference assembly paths.
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static string GetPathToStandardLibraries(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile)
        {
            IList<string> referenceAssemblyDirectories = GetPathToReferenceAssemblies(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile);

            // Check each returned reference assembly directory for one containing mscorlib.dll
            // When we find it (most of the time it will be the first in the set) we'll
            // return that directory.
            foreach (string referenceAssemblyDirectory in referenceAssemblyDirectories)
            {
                if (FileSystems.Default.FileExists(Path.Combine(referenceAssemblyDirectory, "mscorlib.dll")))
                {
                    // We found the framework reference assembly directory with mscorlib in it
                    // that's our standard lib path, so return it, with no trailing slash.
                    return FileUtilities.EnsureNoTrailingSlash(referenceAssemblyDirectory);
                }
            }

            // We didn't find a standard library path in our set, return empty.
            return string.Empty;
        }

        /// <summary>
        /// Returns the path to mscorlib and system.dll
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <param name="platformTarget">What is the targeted platform, this is used to determine where we should look for the standard libraries. Note, this parameter is only used for .net frameworks less than 4.0</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static string GetPathToStandardLibraries(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile, string platformTarget)
            => GetPathToStandardLibraries(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile, platformTarget, null);

        /// <summary>
        /// Returns the path to mscorlib and system.dll
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <param name="platformTarget">What is the targeted platform, this is used to determine where we should look for the standard libraries. Note, this parameter is only used for .net frameworks less than 4.0</param>
        /// <param name="targetFrameworkRootPath">Root directory where the target framework will be looked for. Uses default path if this is null</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static string GetPathToStandardLibraries(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile, string platformTarget, string targetFrameworkRootPath)
            => GetPathToStandardLibraries(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile, platformTarget, targetFrameworkRootPath, null);

        /// <summary>
        /// Returns the path to mscorlib and system.dll
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <param name="platformTarget">What is the targeted platform, this is used to determine where we should look for the standard libraries. Note, this parameter is only used for .net frameworks less than 4.0</param>
        /// <param name="targetFrameworkRootPath">Root directory where the target framework will be looked for. Uses default path if this is null</param>
        /// <param name="targetFrameworkFallbackSearchPaths">';' separated list of paths that are looked up if the framework cannot be found in @targetFrameworkRootPath</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static string GetPathToStandardLibraries(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile, string platformTarget, string targetFrameworkRootPath, string targetFrameworkFallbackSearchPaths)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkIdentifier);
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkVersion);

            Version frameworkVersion = ConvertTargetFrameworkVersionToVersion(targetFrameworkVersion);
            // For .net framework less than 4 the mscorlib should be found in the .net 2.0 directory
            if (targetFrameworkIdentifier.Equals(FrameworkLocationHelper.dotNetFrameworkIdentifier, StringComparison.OrdinalIgnoreCase) && frameworkVersion.Major < 4 && string.IsNullOrEmpty(targetFrameworkProfile))
            {
                // The default
                SharedDotNetFrameworkArchitecture targetedArchitecture = SharedDotNetFrameworkArchitecture.Current;

                if (NativeMethodsShared.IsWindows && platformTarget != null)
                {
                    // If we are a 32 bit operating system the we should always return the 32 bit directory, or we are targeting x86, arm is also 32 bit
                    if (!EnvironmentUtilities.Is64BitOperatingSystem || platformTarget.Equals("x86", StringComparison.OrdinalIgnoreCase) || platformTarget.Equals("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        targetedArchitecture = SharedDotNetFrameworkArchitecture.Bitness32;
                    }
                    else if (platformTarget.Equals("x64", StringComparison.OrdinalIgnoreCase) || platformTarget.Equals("Itanium", StringComparison.OrdinalIgnoreCase) || platformTarget.Equals("arm64", StringComparison.OrdinalIgnoreCase))
                    {
                        targetedArchitecture = SharedDotNetFrameworkArchitecture.Bitness64;
                    }
                }

                string legacyMsCorlib20Path = FrameworkLocationHelper.GetPathToDotNetFrameworkV20(targetedArchitecture);
                if (legacyMsCorlib20Path != null && FileSystems.Default.FileExists(Path.Combine(legacyMsCorlib20Path, "mscorlib.dll")))
                {
                    // We found the framework reference assembly directory with mscorlib in it
                    // that's our standard lib path, so return it, with no trailing slash.
                    return FileUtilities.EnsureNoTrailingSlash(legacyMsCorlib20Path);
                }

                // If for some reason the 2.0 framework is not installed in its default location then maybe someone is using the ".net 4.0" reference assembly
                // location, if so then we can just use what ever version they passed in because it should be MSIL now and not bit specific.
            }

            IList<string> referenceAssemblyDirectories = GetPathToReferenceAssemblies(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile, targetFrameworkRootPath, targetFrameworkFallbackSearchPaths);
            // Check each returned reference assembly directory for one containing mscorlib.dll
            // When we find it (most of the time it will be the first in the set) we'll
            // return that directory.
            foreach (string referenceAssemblyDirectory in referenceAssemblyDirectories)
            {
                if (FileSystems.Default.FileExists(Path.Combine(referenceAssemblyDirectory, "mscorlib.dll")))
                {
                    // We found the framework reference assembly directory with mscorlib in it
                    // that's our standard lib path, so return it, with no trailing slash.
                    return FileUtilities.EnsureNoTrailingSlash(referenceAssemblyDirectory);
                }
            }

            // We didn't find a standard library path in our set, return empty.
            return string.Empty;
        }

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given target framework.
        /// This method will assume the requested ReferenceAssemblyRoot path will be the ProgramFiles directory specified by Environment.SpecialFolder.ProgramFiles
        /// In additon when the .NETFramework or .NET Framework targetFrameworkIdentifiers are seen and targetFrameworkVersion is 2.0, 3.0, 3.5 or 4.0 we will return the correctly chained reference assembly paths
        /// for the legacy .net frameworks. This chaining will use the existing GetPathToDotNetFrameworkReferenceAssemblies to build up the list of reference assembly paths.
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile)
            => GetPathToReferenceAssemblies(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile, null);

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given target framework.
        /// This method will assume the requested ReferenceAssemblyRoot path will be the ProgramFiles directory specified by Environment.SpecialFolder.ProgramFiles
        /// In additon when the .NETFramework or .NET Framework targetFrameworkIdentifiers are seen and targetFrameworkVersion is 2.0, 3.0, 3.5 or 4.0 we will return the correctly chained reference assembly paths
        /// for the legacy .net frameworks. This chaining will use the existing GetPathToDotNetFrameworkReferenceAssemblies to build up the list of reference assembly paths.
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <param name="targetFrameworkRootPath">Root directory which will be used to calculate the reference assembly path. The references assemblies will be
        /// generated in the following way TargetFrameworkRootPath\TargetFrameworkIdentifier\TargetFrameworkVersion\SubType\TargetFrameworkSubType.
        /// Uses the default path if this is null.
        /// </param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile, string targetFrameworkRootPath)
            => GetPathToReferenceAssemblies(targetFrameworkIdentifier, targetFrameworkVersion, targetFrameworkProfile, targetFrameworkRootPath, null);

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given target framework.
        /// This method will assume the requested ReferenceAssemblyRoot path will be the ProgramFiles directory specified by Environment.SpecialFolder.ProgramFiles
        /// In addition when the .NETFramework or .NET Framework targetFrameworkIdentifiers are seen and targetFrameworkVersion is 2.0, 3.0, 3.5 or 4.0 we will return the correctly chained reference assembly paths
        /// for the legacy .net frameworks. This chaining will use the existing GetPathToDotNetFrameworkReferenceAssemblies to build up the list of reference assembly paths.
        /// </summary>
        /// <param name="targetFrameworkIdentifier">Identifier being targeted</param>
        /// <param name="targetFrameworkVersion">Version being targeted</param>
        /// <param name="targetFrameworkProfile">Profile being targeted</param>
        /// <param name="targetFrameworkRootPath">Root directory which will be used to calculate the reference assembly path. The references assemblies will be
        /// generated in the following way TargetFrameworkRootPath\TargetFrameworkIdentifier\TargetFrameworkVersion\SubType\TargetFrameworkSubType.
        /// Uses the default path if this is null.
        /// </param>
        /// <param name="targetFrameworkFallbackSearchPaths">';' separated list of paths that are looked up if the framework cannot be found in <paramref name="targetFrameworkRootPath"/></param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(string targetFrameworkIdentifier, string targetFrameworkVersion, string targetFrameworkProfile, string targetFrameworkRootPath, string targetFrameworkFallbackSearchPaths)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkVersion);
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkIdentifier);
            ErrorUtilities.VerifyThrowArgumentNull(targetFrameworkProfile);

            Version frameworkVersion = ConvertTargetFrameworkVersionToVersion(targetFrameworkVersion);
            FrameworkNameVersioning targetFrameworkName = new FrameworkNameVersioning(targetFrameworkIdentifier, frameworkVersion, targetFrameworkProfile);

            return GetPathToReferenceAssemblies(targetFrameworkRootPath, targetFrameworkFallbackSearchPaths, targetFrameworkName);
        }

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given target framework.
        /// This method will assume the requested ReferenceAssemblyRoot path will be the ProgramFiles directory specified by Environment.SpecialFolder.ProgramFiles
        /// In additon when the .NETFramework or .NET Framework targetFrameworkIdentifiers are seen and targetFrameworkVersion is 2.0, 3.0, 3.5 or 4.0 we will return the correctly chained reference assembly paths
        /// for the legacy .net frameworks. This chaining will use the existing GetPathToDotNetFrameworkReferenceAssemblies to build up the list of reference assembly paths.
        /// </summary>
        /// <param name="frameworkName">Framework required</param>
        /// <exception cref="ArgumentNullException">When the frameworkName is null</exception>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(FrameworkNameVersioning frameworkName)
        {
            // Verify the framework class passed in is not null. Other than being null the class will ensure the framework moniker is correct
            ErrorUtilities.VerifyThrowArgumentNull(frameworkName);
            IList<string> paths =
                GetPathToReferenceAssemblies(
                    FrameworkLocationHelper.programFilesReferenceAssemblyLocation,
                    frameworkName);
            return paths;
        }

        /// <summary>
        /// Call either the static method or the delegate. This is done purely for performance as the delegate is only required for ease of unit testing and since
        /// the methods being called are static this will be a non 0 cost to use delegates vs the static methods directly.
        /// </summary>
        private static string VersionToDotNetFrameworkPath(VersionToPath PathToDotNetFramework, TargetDotNetFrameworkVersion version)
            => PathToDotNetFramework == null
                ? GetPathToDotNetFramework(version)
                : PathToDotNetFramework(version);

        /// <summary>
        /// Call either the static method or the delegate. This is done purely for performance as the delegate is only required for ease of unit testing and since
        /// the methods being called are static this will be a non 0 cost to use delegates vs the static methods directly.
        /// </summary>
        private static string VersionToDotNetReferenceAssemblies(VersionToPath PathToDotReferenceAssemblies, TargetDotNetFrameworkVersion version)
            => PathToDotReferenceAssemblies == null
                ? GetPathToDotNetFrameworkReferenceAssemblies(version)
                : PathToDotReferenceAssemblies(version);

        /// <summary>
        /// Generate the list of reference assembly paths for well known .net framework versions
        /// </summary>
        /// <param name="frameworkName">Target framework moniker class which contains the targetframeworkVersion</param>
        /// <param name="PathToDotNetFramework"></param>
        /// <param name="PathToReferenceAssemblies"></param>
        /// <returns>A collection of strings which list the chained reference assembly paths with the highest version being first</returns>
        internal static IList<string> HandleLegacyDotNetFrameworkReferenceAssemblyPaths(VersionToPath PathToDotNetFramework, VersionToPath PathToReferenceAssemblies, FrameworkNameVersioning frameworkName)
        {
            if (frameworkName.Version == FrameworkLocationHelper.dotNetFrameworkVersion20)
            {
                return HandleLegacy20(PathToDotNetFramework);
            }
            else if (frameworkName.Version == FrameworkLocationHelper.dotNetFrameworkVersion30)
            {
                return HandleLegacy30(PathToDotNetFramework, PathToReferenceAssemblies);
            }
            else if (frameworkName.Version == FrameworkLocationHelper.dotNetFrameworkVersion35)
            {
                return HandleLegacy35(PathToDotNetFramework, PathToReferenceAssemblies);
            }

            // Don't know the framework send back an empty list because it does not exist
            return new List<string>();
        }

        /// <summary>
        /// Returns the path to the "bin" directory of the .NET Framework SDK.
        /// </summary>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio the requested SDK is associated with</param>
        /// <returns>Path string.</returns>
        private static string GetPathToDotNetFrameworkSdkToolsFolderRoot(TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
        {
            var dotNetFrameworkVersion = TargetDotNetFrameworkVersionToSystemVersion(version);
            var vsVersion = VisualStudioVersionToSystemVersion(visualStudioVersion);
            return FrameworkLocationHelper.GetPathToDotNetFrameworkSdkTools(dotNetFrameworkVersion, vsVersion);
        }

        private static Version TargetDotNetFrameworkVersionToSystemVersion(TargetDotNetFrameworkVersion version)
        {
            switch (version)
            {
                case TargetDotNetFrameworkVersion.Version11:
                    return FrameworkLocationHelper.dotNetFrameworkVersion11;

                case TargetDotNetFrameworkVersion.Version20:
                    return FrameworkLocationHelper.dotNetFrameworkVersion20;

                case TargetDotNetFrameworkVersion.Version30:
                    return FrameworkLocationHelper.dotNetFrameworkVersion30;

                case TargetDotNetFrameworkVersion.Version35:
                    return FrameworkLocationHelper.dotNetFrameworkVersion35;

                case TargetDotNetFrameworkVersion.Version40:
                    return FrameworkLocationHelper.dotNetFrameworkVersion40;

                case TargetDotNetFrameworkVersion.Version45:
                    return FrameworkLocationHelper.dotNetFrameworkVersion45;

                case TargetDotNetFrameworkVersion.Version451:
                    return FrameworkLocationHelper.dotNetFrameworkVersion451;

                case TargetDotNetFrameworkVersion.Version452:
                    return FrameworkLocationHelper.dotNetFrameworkVersion452;

                case TargetDotNetFrameworkVersion.Version46:
                    return FrameworkLocationHelper.dotNetFrameworkVersion46;

                case TargetDotNetFrameworkVersion.Version461:
                    return FrameworkLocationHelper.dotNetFrameworkVersion461;

                case TargetDotNetFrameworkVersion.Version462:
                    return FrameworkLocationHelper.dotNetFrameworkVersion462;

                case TargetDotNetFrameworkVersion.Version47:
                    return FrameworkLocationHelper.dotNetFrameworkVersion47;

                case TargetDotNetFrameworkVersion.Version471:
                    return FrameworkLocationHelper.dotNetFrameworkVersion471;

                case TargetDotNetFrameworkVersion.Version472:
                    return FrameworkLocationHelper.dotNetFrameworkVersion472;

                case TargetDotNetFrameworkVersion.Version48:
                    return FrameworkLocationHelper.dotNetFrameworkVersion48;

                case TargetDotNetFrameworkVersion.Version481:
                case TargetDotNetFrameworkVersion.Latest: // Latest is a special value to indicate the highest version we know about.
                    return FrameworkLocationHelper.dotNetFrameworkVersion481;

                default:
                    ErrorUtilities.ThrowArgument("ToolLocationHelper.UnsupportedFrameworkVersion", version);
                    return null;
            }
        }

        private static Version VisualStudioVersionToSystemVersion(VisualStudioVersion version)
        {
            return version switch
            {
                VisualStudioVersion.Version100 => FrameworkLocationHelper.visualStudioVersion100,
                VisualStudioVersion.Version110 => FrameworkLocationHelper.visualStudioVersion110,
                VisualStudioVersion.Version120 => FrameworkLocationHelper.visualStudioVersion120,
                VisualStudioVersion.Version140 => FrameworkLocationHelper.visualStudioVersion140,
                VisualStudioVersion.Version150 => FrameworkLocationHelper.visualStudioVersion150,
                VisualStudioVersion.Version160 => FrameworkLocationHelper.visualStudioVersion160,
                VisualStudioVersion.Version170 => FrameworkLocationHelper.visualStudioVersion170,
                _ => Unsupported()
            };

            Version Unsupported()
            {
                ErrorUtilities.ThrowArgument("ToolLocationHelper.UnsupportedVisualStudioVersion", version);
                return null;
            }
        }

        /// <summary>
        /// Generate the key which will be used for the reference assembly cache so that multiple static methods will generate it in the same way.
        /// </summary>
        private static string GenerateReferenceAssemblyCacheKey(string targetFrameworkRootPath, FrameworkNameVersioning frameworkName) => targetFrameworkRootPath + "|" + frameworkName.FullName;

        /// <summary>
        /// Create the shared cache if it is not null
        /// </summary>
        private static void CreateReferenceAssemblyPathsCache()
        {
            lock (s_locker)
            {
                if (s_cachedReferenceAssemblyPaths == null)
                {
                    s_cachedReferenceAssemblyPaths = new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Do the correct chaining of .net 3.5, 3.0 and 2.0. Throw an exception if any of the chain is missing
        /// </summary>
        private static IList<string> HandleLegacy35(VersionToPath PathToDotNetFramework, VersionToPath PathToReferenceAssemblies)
        {
            var referencePaths = new List<string>();
            string referenceAssemblyPath = VersionToDotNetReferenceAssemblies(PathToReferenceAssemblies, TargetDotNetFrameworkVersion.Version35);
            string dotNetFrameworkPath = VersionToDotNetFrameworkPath(PathToDotNetFramework, TargetDotNetFrameworkVersion.Version35);

            if (referenceAssemblyPath != null && dotNetFrameworkPath != null)
            {
                referencePaths.Add(referenceAssemblyPath);
                referencePaths.Add(dotNetFrameworkPath);
            }
            else
            {
                return referencePaths;
            }

            // This method will return either an empty list or a list with elements in the order  3.0 2.0
            IList<string> referenceAssembly30Paths = HandleLegacy30(PathToDotNetFramework, PathToReferenceAssemblies);

            referencePaths.AddRange(referenceAssembly30Paths);
            return referencePaths;
        }

        /// <summary>
        /// Do the correct chaining of .net 3.5, 3.0 and 2.0. Throw an exception if any of the chain is missing
        /// </summary>
        private static IList<string> HandleLegacy30(VersionToPath PathToDotNetFramework, VersionToPath PathToReferenceAssemblies)
        {
            var referencePaths = new List<string>();
            string referenceAssemblyPath = VersionToDotNetReferenceAssemblies(PathToReferenceAssemblies, TargetDotNetFrameworkVersion.Version30);
            string dotNetFrameworkPath = VersionToDotNetFrameworkPath(PathToDotNetFramework, TargetDotNetFrameworkVersion.Version30);

            if (referenceAssemblyPath != null && dotNetFrameworkPath != null)
            {
                referencePaths.Add(referenceAssemblyPath);
                referencePaths.Add(dotNetFrameworkPath);
            }
            else
            {
                return referencePaths;
            }

            IList<string> referenceAssembly20Paths = HandleLegacy20(PathToDotNetFramework);
            referencePaths.AddRange(referenceAssembly20Paths);
            return referencePaths;
        }

        /// <summary>
        /// Check to see if .net 2.0 is installed
        /// </summary>
        private static IList<string> HandleLegacy20(VersionToPath PathToDotNetFramework)
        {
            var referencePaths = new List<string>();
            string referencePath = VersionToDotNetFrameworkPath(PathToDotNetFramework, TargetDotNetFrameworkVersion.Version20);

            if (referencePath != null)
            {
                referencePaths.Add(referencePath);
            }

            return referencePaths;
        }

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given framework version relative to a given targetFrameworkRoot.
        /// The method will not check to see if the path exists or not.
        /// </summary>
        /// <param name="targetFrameworkRootPath">Root directory which will be used to calculate the reference assembly path. The references assemblies will be
        /// generated in the following way TargetFrameworkRootPath\TargetFrameworkIdentifier\TargetFrameworkVersion\SubType\TargetFrameworkSubType.
        /// </param>
        /// <param name="targetFrameworkFallbackSearchPaths">';' separated list of paths that are looked up if the framework cannot be found in @targetFrameworkRootPath</param>
        /// <param name="frameworkName">A frameworkName class which represents a TargetFrameworkMoniker. This cannot be null.</param>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(string targetFrameworkRootPath, string targetFrameworkFallbackSearchPaths, FrameworkNameVersioning frameworkName)
        {
            IList<string> pathsList = string.IsNullOrEmpty(targetFrameworkRootPath)
                ? GetPathToReferenceAssemblies(frameworkName)
                : GetPathToReferenceAssemblies(targetFrameworkRootPath, frameworkName);

            if (pathsList?.Count > 0)
            {
                return pathsList;
            }

            if (!string.IsNullOrEmpty(targetFrameworkFallbackSearchPaths))
            {
                foreach (string rootPath in targetFrameworkFallbackSearchPaths.Split(_separatorForFallbackSearchPaths, StringSplitOptions.RemoveEmptyEntries))
                {
                    pathsList = GetPathToReferenceAssemblies(rootPath, frameworkName);
                    if (pathsList?.Count > 0)
                    {
                        return pathsList;
                    }
                }
            }

            return pathsList ?? new List<string>();
        }

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given framework version relative to a given targetFrameworkRoot.
        /// The method will not check to see if the path exists or not.
        /// </summary>
        /// <param name="targetFrameworkRootPath">Root directory which will be used to calculate the reference assembly path. The references assemblies will be
        /// generated in the following way TargetFrameworkRootPath\TargetFrameworkIdentifier\TargetFrameworkVersion\SubType\TargetFrameworkSubType.
        /// </param>
        /// <param name="frameworkName">A frameworkName class which represents a TargetFrameworkMoniker. This cannot be null.</param>
        /// <returns>Collection of reference assembly locations.</returns>
        public static IList<string> GetPathToReferenceAssemblies(string targetFrameworkRootPath, FrameworkNameVersioning frameworkName)
        {
            // Verify the root path is not null throw an ArgumentNullException if the given string parameter is null and ArgumentException if it has zero length.
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkRootPath);
            // Verify the framework class passed in is not null. Other than being null the class will ensure it is consistent and the internal state is correct
            ErrorUtilities.VerifyThrowArgumentNull(frameworkName);

            string referenceAssemblyCacheKey = GenerateReferenceAssemblyCacheKey(targetFrameworkRootPath, frameworkName);
            CreateReferenceAssemblyPathsCache();

            lock (s_locker)
            {
                if (s_cachedReferenceAssemblyPaths.TryGetValue(referenceAssemblyCacheKey, out IList<string> referenceAssemblies))
                {
                    return referenceAssemblies;
                }
            }

            // Try and find the reference assemblies using the reference assembly path generation algorithm
            IList<string> dotNetFrameworkReferenceAssemblies = GetPathAndChainReferenceAssemblyLocations(targetFrameworkRootPath, frameworkName, true);

            // We have not found any reference assembly locations, if we are the .net framework we can try and fallback to the old legacy tool location helper methods
            if (string.Equals(frameworkName.Identifier, ".NETFramework", StringComparison.OrdinalIgnoreCase)
                && dotNetFrameworkReferenceAssemblies.Count == 0)
            {
                if (string.IsNullOrEmpty(frameworkName.Profile)) // profiles are always in new locations
                {
                    // If the identifier is ".NET Framework" and the version is a well know legacy version. Manually generate the list of reference assembly paths
                    // based on the known chaining order. Pass null in for the two delegates so we call the static methods rather than require the creation and calling
                    // of two delegates
                    dotNetFrameworkReferenceAssemblies = HandleLegacyDotNetFrameworkReferenceAssemblyPaths(
                        null,
                        null,
                        frameworkName);
                }
            }

            for (int i = 0; i < dotNetFrameworkReferenceAssemblies.Count; i++)
            {
                if (
                    !dotNetFrameworkReferenceAssemblies[i].EndsWith(
                        Path.DirectorySeparatorChar.ToString(),
                        StringComparison.Ordinal))
                {
                    dotNetFrameworkReferenceAssemblies[i] += Path.DirectorySeparatorChar;
                }
            }

            lock (s_locker)
            {
                s_cachedReferenceAssemblyPaths[referenceAssemblyCacheKey] = dotNetFrameworkReferenceAssemblies;
            }

            return dotNetFrameworkReferenceAssemblies;
        }

        /// <summary>
        /// Figures out a display name given the target framework details.
        /// This is the equivalent of the target framework moniker, but for display.
        /// If one cannot be found from the redist list file, a synthesized one is returned, so there is always a display name.
        /// </summary>
        public static string GetDisplayNameForTargetFrameworkDirectory(string targetFrameworkDirectory, FrameworkNameVersioning frameworkName)
        {
            string displayName;
            lock (s_locker)
            {
                if (s_cachedTargetFrameworkDisplayNames != null
                    && s_cachedTargetFrameworkDisplayNames.TryGetValue(targetFrameworkDirectory, out displayName))
                {
                    return displayName;
                }
            }

            // Not in the cache, try to find it and if so cache it
            ChainReferenceAssemblyPath(targetFrameworkDirectory);

            lock (s_locker)
            {
                if (s_cachedTargetFrameworkDisplayNames.TryGetValue(targetFrameworkDirectory, out displayName))
                {
                    return displayName;
                }
            }

            // Still don't have one.
            // Probably it's 3.5 or earlier: make something reasonable.
            // VS uses the same algorithm to find something to display
            StringBuilder displayNameBuilder = new StringBuilder();

            displayNameBuilder.Append(frameworkName.Identifier);
            displayNameBuilder.Append(' ');
            displayNameBuilder.Append('v').Append(frameworkName.Version.ToString());

            if (!string.IsNullOrEmpty(frameworkName.Profile))
            {
                displayNameBuilder.Append(' ');
                displayNameBuilder.Append(frameworkName.Profile);
            }

            displayName = displayNameBuilder.ToString();

            // Cache it
            lock (s_locker)
            {
                s_cachedTargetFrameworkDisplayNames[targetFrameworkDirectory] = displayName;
            }

            return displayName;
        }

        /// <summary>
        /// Returns the paths to the reference assemblies location for the given framework version and properly chains the reference assemblies if required.
        /// </summary>
        /// <returns>Collection of reference assembly locations.</returns>
        private static IList<string> GetPathAndChainReferenceAssemblyLocations(string targetFrameworkRootPath, FrameworkNameVersioning frameworkName, bool chain)
        {
            var referencePaths = new List<string>();

            string path = FrameworkLocationHelper.GenerateReferenceAssemblyPath(targetFrameworkRootPath, frameworkName);
            if (FileSystems.Default.DirectoryExists(path))
            {
                referencePaths.Add(path);

                if (chain)
                {
                    while (!string.IsNullOrEmpty(path))
                    {
                        // Will return String.Empty when there are no longer any paths to chain to
                        // We will return null if the chain is invalid and we need to return an empty chain.
                        path = ChainReferenceAssemblyPath(path);

                        if (!string.IsNullOrEmpty(path))
                        {
                            if (referencePaths.Contains(path))
                            {
                                break;
                            }
                            referencePaths.Add(path);
                        }
                        else if (path == null)
                        {
                            // We have an invalid chain, we need to clear out any reference paths we have already added.
                            referencePaths.Clear();
                            break;
                        }
                    }
                }
            }

            return referencePaths;
        }

        /// <summary>
        /// Clear out the appdomain wide cache of Platform and Extension SDKs.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1709:IdentifiersShouldBeCasedCorrectly", MessageId = "SDK", Justification = "Shipped this way in Dev11 Beta (go-live)")]
        public static void ClearSDKStaticCache()
        {
            lock (s_locker)
            {
                s_cachedTargetPlatforms?.Clear();
                s_cachedTargetPlatformReferences?.Clear();
                s_cachedExtensionSdks?.Clear();
                s_cachedExtensionSdkReferences?.Clear();
            }
        }

        /// <summary>
        /// Clear our the appdomain wide caches
        /// </summary>
        internal static void ClearStaticCaches()
        {
            lock (s_locker)
            {
                s_chainedReferenceAssemblyPath?.Clear();
                s_cachedHighestFrameworkNameForTargetFrameworkIdentifier?.Clear();
                s_targetFrameworkMonikers?.Clear();
                s_cachedTargetFrameworkDisplayNames?.Clear();
                s_cachedReferenceAssemblyPaths?.Clear();
                s_cachedTargetPlatforms?.Clear();
                s_cachedTargetPlatformReferences?.Clear();
                s_cachedExtensionSdks?.Clear();
                s_cachedExtensionSdkReferences?.Clear();
            }
        }

        /// <summary>
        /// Remap some common architectures to a single one that will be in the SDK.
        /// </summary>
        private static string RemapSdkArchitecture(string targetArchitecture)
        {
            if (targetArchitecture.Equals("msil", StringComparison.OrdinalIgnoreCase) ||
               targetArchitecture.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) ||
               targetArchitecture.Equals("Any CPU", StringComparison.OrdinalIgnoreCase))
            {
                targetArchitecture = "Neutral";
            }
            else if (targetArchitecture.Equals("Amd64", StringComparison.OrdinalIgnoreCase))
            {
                targetArchitecture = "x64";
            }
            return targetArchitecture;
        }

        /// <summary>
        /// Add the reference folder to the list of reference directories if it exists.
        /// </summary>
        private static void AddSDKPath(string sdkRoot, string contentFolderName, string targetConfiguration, string targetArchitecture, List<string> contentDirectories)
        {
            string referenceAssemblyPath = Path.Combine(sdkRoot, contentFolderName, targetConfiguration, targetArchitecture);

            if (FileUtilities.DirectoryExistsNoThrow(referenceAssemblyPath))
            {
                referenceAssemblyPath = FileUtilities.EnsureTrailingSlash(referenceAssemblyPath);
                contentDirectories.Add(referenceAssemblyPath);
            }
        }

        /// <summary>
        /// Get the list of extension sdks for a given platform and version
        /// </summary>
        private static IEnumerable<TargetPlatformSDK> RetrieveTargetPlatformList(string[] diskRoots, string[] extensionDiskRoots, string registrySearchLocation)
        {
            // Get the disk and registry roots to search for sdks under
            List<string> sdkDiskRoots = GetTargetPlatformMonikerDiskRoots(diskRoots);
            List<string> extensionSdkDiskRoots = GetExtensionSdkDiskRoots(extensionDiskRoots);

            string registryRoot = NativeMethodsShared.IsWindows ? GetTargetPlatformMonikerRegistryRoots(registrySearchLocation) : string.Empty;

            string cachedTargetPlatformsKey = string.Join("|",
                string.Join(";", sdkDiskRoots),
                registryRoot);

            string cachedExtensionSdksKey = extensionDiskRoots == null ? string.Empty : string.Join(";", extensionDiskRoots);

            lock (s_locker)
            {
                if (s_cachedTargetPlatforms == null)
                {
                    s_cachedTargetPlatforms = new Dictionary<string, IEnumerable<TargetPlatformSDK>>(StringComparer.OrdinalIgnoreCase);
                }

                if (s_cachedExtensionSdks == null)
                {
                    s_cachedExtensionSdks = new Dictionary<string, TargetPlatformSDK>(StringComparer.OrdinalIgnoreCase);
                }

                if (!s_cachedTargetPlatforms.TryGetValue(cachedTargetPlatformsKey, out IEnumerable<TargetPlatformSDK> collection))
                {
                    var monikers = new Dictionary<TargetPlatformSDK, TargetPlatformSDK>();
                    GatherSDKListFromDirectory(sdkDiskRoots, monikers);

                    if (NativeMethodsShared.IsWindows)
                    {
                        GatherSDKListFromRegistry(registryRoot, monikers);
                    }

                    collection = monikers.Keys.ToList();
                    s_cachedTargetPlatforms.Add(cachedTargetPlatformsKey, collection);
                }

                if (!string.IsNullOrEmpty(cachedExtensionSdksKey))
                {
                    if (!s_cachedExtensionSdks.TryGetValue(cachedExtensionSdksKey, out TargetPlatformSDK extensionSdk))
                    {
                        // These extension SDKs can target multiple platforms under the same Target SDK, stash in a null platform key for later filtering
                        extensionSdk = new TargetPlatformSDK(string.Empty, new Version(0, 0), null);

                        GatherExtensionSDKListFromDirectory(extensionSdkDiskRoots, extensionSdk);
                        s_cachedExtensionSdks.Add(cachedExtensionSdksKey, extensionSdk);
                    }
                    collection = collection.Concat([extensionSdk]);
                }

                return collection;
            }
        }

        /// <summary>
        /// Gets new style extension SDKs (those that are under the target SDK name and version and are driven by manifest, not directory structure).
        /// </summary>
        private static void GatherExtensionSDKListFromDirectory(IEnumerable<string> diskRoots, TargetPlatformSDK extensionSdk)
        {
            // In this case we're passing in roots with the SDK and Version, such as C:\Program Files (x86)\Windows SDKs\1.0
            foreach (string diskRoot in diskRoots)
            {
                DirectoryInfo rootInfo = new DirectoryInfo(diskRoot);
                if (!rootInfo.Exists)
                {
                    ErrorUtilities.DebugTraceMessage("GatherExtensionSDKListFromDirectory", "DiskRoot '{0}'does not exist, skipping it", diskRoot);
                    continue;
                }

                // Leave this entry as partners have already started to develop against this path, we will eventually remove this
                DirectoryInfo extensionSdksDirectory = rootInfo.GetDirectories("Extension SDKs", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (extensionSdksDirectory != null)
                {
                    GatherExtensionSDKs(extensionSdksDirectory, extensionSdk);
                }

                DirectoryInfo extensionSdksDirectory2 = rootInfo.GetDirectories("ExtensionSDKs", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (extensionSdksDirectory2 != null)
                {
                    GatherExtensionSDKs(extensionSdksDirectory2, extensionSdk);
                }
            }
        }

        internal static void GatherExtensionSDKs(DirectoryInfo extensionSdksDirectory, TargetPlatformSDK targetPlatformSDK)
        {
            ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "Found ExtensionsSDK folder '{0}'. ", extensionSdksDirectory.FullName);

            DirectoryInfo[] sdkNameDirectories = extensionSdksDirectory.GetDirectories();
            ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "Found '{0}' sdkName directories under '{1}'", sdkNameDirectories.Length, extensionSdksDirectory.FullName);

            // For each SDKName under the ExtensionSDKs directory
            foreach (DirectoryInfo sdkNameFolders in sdkNameDirectories)
            {
                DirectoryInfo[] sdkVersionDirectories = sdkNameFolders.GetDirectories();
                ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "Found '{0}' sdkVersion directories under '{1}'", sdkVersionDirectories.Length, sdkNameFolders.FullName);

                // For each Version directory under the SDK Name
                foreach (DirectoryInfo sdkVersionDirectory in sdkVersionDirectories)
                {
                    // Make sure the version folder parses to a version, anything that cannot parse directly to a version is to be ignored.
                    ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "Parsed sdk version folder '{0}' under '{1}'", sdkVersionDirectory.Name, sdkVersionDirectory.FullName);
                    if (Version.TryParse(sdkVersionDirectory.Name, out Version _))
                    {
                        // Create SDK name based on the folder structure. We could open the manifest here and read the display name, but that would
                        // add complexity and since things are supposed to be in a certain structure I don't think that is needed at this point.
                        string SDKKey = TargetPlatformSDK.GetSdkKey(sdkNameFolders.Name, sdkVersionDirectory.Name);

                        // Make sure we have not added the SDK to the list of found SDKs before.
                        if (!targetPlatformSDK.ExtensionSDKs.ContainsKey(SDKKey))
                        {
                            ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "SDKKey '{0}' was not already found.", SDKKey);
                            string pathToSDKManifest = Path.Combine(sdkVersionDirectory.FullName, "SDKManifest.xml");
                            if (FileUtilities.FileExistsNoThrow(pathToSDKManifest))
                            {
                                targetPlatformSDK.ExtensionSDKs.Add(SDKKey, FileUtilities.EnsureTrailingSlash(sdkVersionDirectory.FullName));
                            }
                            else
                            {
                                ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "No SDKManifest.xml files could be found at '{0}'. Not adding sdk", pathToSDKManifest);
                            }
                        }
                        else
                        {
                            ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "SDKKey '{0}' was already found, not adding sdk under '{1}'", SDKKey, sdkVersionDirectory.FullName);
                        }
                    }
                    else
                    {
                        ErrorUtilities.DebugTraceMessage("GatherExtensionSDKs", "Failed to parse sdk version folder '{0}' under '{1}'", sdkVersionDirectory.Name, sdkVersionDirectory.FullName);
                    }
                }
            }
        }

        /// <summary>
        /// Given a root disk location and the target platform properties find all of the SDKs installed in that location.
        /// </summary>
        internal static void GatherSDKListFromDirectory(List<string> diskroots, Dictionary<TargetPlatformSDK, TargetPlatformSDK> platformSDKs)
        {
            foreach (string diskRoot in diskroots)
            {
                DirectoryInfo rootInfo = new DirectoryInfo(diskRoot);
                if (!rootInfo.Exists)
                {
                    ErrorUtilities.DebugTraceMessage("GatherSDKListFromDirectory", "DiskRoot '{0}'does not exist, skipping it", diskRoot);
                    continue;
                }

                foreach (DirectoryInfo rootPathWithIdentifier in rootInfo.GetDirectories())
                {
                    // This makes a list of directories under the target framework identifier.
                    // This should make something like c:\Program files\Microsoft SDKs\Windows

                    if (!rootPathWithIdentifier.Exists)
                    {
                        ErrorUtilities.DebugTraceMessage("GatherSDKListFromDirectory", "Disk root with Identifier: '{0}' does not exist. ", rootPathWithIdentifier);
                        continue;
                    }

                    ErrorUtilities.DebugTraceMessage("GatherSDKListFromDirectory", "Disk root with Identifier: '{0}' does exist. Enumerating version folders under it. ", rootPathWithIdentifier);

                    // Get a list of subdirectories under the root path and identifier, Ie. c:\Program files\Microsoft SDKs\Windows we should see things like, V8.0, 8.0, 9.0 ect.
                    // Only grab the folders that have a version number (they can start with a v or not).

                    SortedDictionary<Version, List<string>> versionsInRoot = VersionUtilities.GatherVersionStrings(null, rootPathWithIdentifier.GetDirectories().Select<DirectoryInfo, string>(directory => directory.Name));

                    ErrorUtilities.DebugTraceMessage("GatherSDKListFromDirectory", "Found '{0}' version folders under the identifier path '{1}'. ", versionsInRoot.Count, rootPathWithIdentifier);

                    // Go through each of the targetplatform versions under the targetplatform identifier.
                    foreach (KeyValuePair<Version, List<string>> directoryUnderRoot in versionsInRoot)
                    {
                        TargetPlatformSDK platformSDKKey;
                        if (rootPathWithIdentifier.Name.Equals(uapDirectoryName, StringComparison.OrdinalIgnoreCase) && directoryUnderRoot.Key.Major == uapVersion)
                        {
                            platformSDKKey = new TargetPlatformSDK(uapRegistryName, directoryUnderRoot.Key, null);
                        }
                        else
                        {
                            platformSDKKey = new TargetPlatformSDK(rootPathWithIdentifier.Name, directoryUnderRoot.Key, null);
                        }
                        TargetPlatformSDK targetPlatformSDK = null;

                        // DirectoryUnderRoot.Value will be a list of the raw directory strings under the targetplatform identifier directory that map to the versions specified in directoryUnderRoot.Key.
                        foreach (string version in directoryUnderRoot.Value)
                        {
                            // This should make something like c:\Program files\Microsoft SDKs\Windows\v8.0\
                            string platformSDKDirectory = Path.Combine(rootPathWithIdentifier.FullName, version);
                            string platformSDKManifest = Path.Combine(platformSDKDirectory, "SDKManifest.xml");

                            // If we are gathering the sdk platform manifests then check to see if there is a sdk manifest in the directory if not then skip over it as a platform sdk
                            bool platformSDKManifestExists = FileSystems.Default.FileExists(platformSDKManifest);
                            if (targetPlatformSDK == null && !platformSDKs.TryGetValue(platformSDKKey, out targetPlatformSDK))
                            {
                                targetPlatformSDK = new TargetPlatformSDK(platformSDKKey.TargetPlatformIdentifier, platformSDKKey.TargetPlatformVersion, platformSDKManifestExists ? platformSDKDirectory : null);
                                platformSDKs.Add(targetPlatformSDK, targetPlatformSDK);
                            }

                            if (targetPlatformSDK.Path == null && platformSDKManifestExists)
                            {
                                targetPlatformSDK.Path = platformSDKDirectory;
                            }

                            // Gather the set of platforms supported by this SDK if it's a valid one.
                            if (!string.IsNullOrEmpty(targetPlatformSDK.Path))
                            {
                                GatherPlatformsForSdk(targetPlatformSDK);
                            }

                            // If we are passed an extension sdk dictionary we will continue to look through the extension sdk directories and try and fill it up.
                            // This should make something like c:\Program files\Microsoft SDKs\Windows\v8.0\ExtensionSDKs
                            string sdkFolderPath = Path.Combine(platformSDKDirectory, "ExtensionSDKs");
                            DirectoryInfo extensionSdksDirectory = new DirectoryInfo(sdkFolderPath);

                            if (extensionSdksDirectory.Exists)
                            {
                                GatherExtensionSDKs(extensionSdksDirectory, targetPlatformSDK);
                            }
                            else
                            {
                                ErrorUtilities.DebugTraceMessage("GatherSDKListFromDirectory", "Could not find ExtensionsSDK folder '{0}'. ", sdkFolderPath);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Given a registry location enumerate the registry and find the installed SDKs.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static void GatherSDKsFromRegistryImpl(Dictionary<TargetPlatformSDK, TargetPlatformSDK> platformMonikers, string registryKeyRoot, RegistryView registryView, RegistryHive registryHive, GetRegistrySubKeyNames getRegistrySubKeyNames, GetRegistrySubKeyDefaultValue getRegistrySubKeyDefaultValue, OpenBaseKey openBaseKey, FileExists fileExists)
        {
            ErrorUtilities.VerifyThrowArgumentNull(platformMonikers, "PlatformMonikers");
            if (string.IsNullOrEmpty(registryKeyRoot))
            {
                return;
            }

            // Open the hive for a given view
            using (RegistryKey baseKey = openBaseKey(registryHive, registryView))
            {
                ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Gathering SDKS from registryRoot '{0}', Hive '{1}', View '{2}'", registryKeyRoot, registryHive, registryView);

                // Attach the target platform to the registry root. This should give us something like
                // SOFTWARE\MICROSOFT\Microsoft SDKs\Windows

                // Get all of the platform identifiers
                IEnumerable<string> platformIdentifiers = getRegistrySubKeyNames(baseKey, registryKeyRoot);

                // No identifiers found.
                if (platformIdentifiers == null)
                {
                    ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "No sub keys found under registryKeyRoot {0}", registryKeyRoot);
                    return;
                }

                foreach (string platformIdentifier in platformIdentifiers)
                {
                    string platformIdentifierKey = registryKeyRoot + @"\" + platformIdentifier;

                    // Get all of the version folders under the targetplatform identifier key
                    IEnumerable<string> versions = getRegistrySubKeyNames(baseKey, platformIdentifierKey);

                    // No versions found.
                    if (versions == null)
                    {
                        ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "No sub keys found under platformIdentifierKey {0}", platformIdentifierKey);
                        return;
                    }

                    // Returns a a sorted set of versions and their associated registry strings. The reason we need the original strings is that
                    // they may contain a v where as a version does not support a v.
                    SortedDictionary<Version, List<string>> sortedVersions = VersionUtilities.GatherVersionStrings(null, versions);

                    foreach (KeyValuePair<Version, List<string>> registryVersions in sortedVersions)
                    {
                        TargetPlatformSDK platformSDKKey = new TargetPlatformSDK(platformIdentifier, registryVersions.Key, null);
                        TargetPlatformSDK targetPlatformSDK = null;

                        // Go through each of the raw version strings which were found in the registry
                        foreach (string version in registryVersions.Value)
                        {
                            // Attach the version and extensionSDKs strings to the platformIdentifier key we built up above.
                            // Make something like SOFTWARE\MICROSOFT\Microsoft SDKs\Windows\8.0\
                            string platformSDKsRegistryKey = platformIdentifierKey + @"\" + version;

                            string platformSDKDirectory = getRegistrySubKeyDefaultValue(baseKey, platformSDKsRegistryKey);

                            // May be null because some use installationfolder instead
                            if (platformSDKDirectory == null)
                            {
                                using (RegistryKey versionKey = baseKey.OpenSubKey(platformSDKsRegistryKey))
                                {
                                    if (versionKey != null)
                                    {
                                        platformSDKDirectory = versionKey.GetValue("InstallationFolder") as string;
                                    }
                                }
                            }

                            bool platformSDKmanifestExists = false;

                            if (platformSDKDirectory != null)
                            {
                                string platformSDKManifest = Path.Combine(platformSDKDirectory, "SDKManifest.xml");
                                // Windows kits is special because they do not have an sdk manifest yet, this is for the windows sdk. We will accept them as they are. For others
                                // we will require that an sdkmanifest exists.
                                platformSDKmanifestExists = fileExists(platformSDKManifest) || platformSDKDirectory.IndexOf("Windows Kits", StringComparison.OrdinalIgnoreCase) >= 0;
                            }

                            if (targetPlatformSDK == null && !platformMonikers.TryGetValue(platformSDKKey, out targetPlatformSDK))
                            {
                                targetPlatformSDK = new TargetPlatformSDK(platformSDKKey.TargetPlatformIdentifier, platformSDKKey.TargetPlatformVersion, platformSDKmanifestExists ? platformSDKDirectory : null);
                                platformMonikers.Add(targetPlatformSDK, targetPlatformSDK);
                            }

                            if (targetPlatformSDK.Path == null && platformSDKmanifestExists)
                            {
                                targetPlatformSDK.Path = platformSDKDirectory;
                            }

                            // Gather the set of platforms supported by this SDK if it's a valid one.
                            if (!string.IsNullOrEmpty(targetPlatformSDK.Path))
                            {
                                GatherPlatformsForSdk(targetPlatformSDK);
                            }

                            // Make something like SOFTWARE\MICROSOFT\Microsoft SDKs\Windows\8.0\ExtensionSdks
                            string extensionSDKsKey = platformSDKsRegistryKey + @"\ExtensionSDKs";
                            ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Getting subkeys of '{0}'", extensionSDKsKey);

                            // Get all of the SDK name folders under the ExtensionSDKs registry key
                            IEnumerable<string> sdkNames = getRegistrySubKeyNames(baseKey, extensionSDKsKey);
                            if (sdkNames == null)
                            {
                                ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Could not find subkeys of '{0}'", extensionSDKsKey);
                                continue;
                            }

                            ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Found subkeys of '{0}'", extensionSDKsKey);

                            // For each SDK folder under ExtensionSDKs
                            foreach (string sdkName in sdkNames)
                            {
                                // Combine the SDK Name with the ExtensionSDKs key we have built up above.
                                // Make something like SOFTWARE\MICROSOFT\Windows SDKs\Windows\8.0\ExtensionSDKs\XNA
                                string sdkNameKey = extensionSDKsKey + @"\" + sdkName;

                                // Get all of the version registry keys under the SDK Name Key.
                                IEnumerable<string> sdkVersions = getRegistrySubKeyNames(baseKey, sdkNameKey);

                                ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Getting subkeys of '{0}'", sdkNameKey);
                                if (sdkVersions == null)
                                {
                                    ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Could not find subkeys of '{0}'", sdkNameKey);
                                    continue;
                                }

                                ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Found subkeys of '{0}'", sdkNameKey);

                                // For each version registry entry under the SDK Name registry key
                                foreach (string sdkVersion in sdkVersions)
                                {
                                    // We only want registry keys which parse directly to versions
                                    Version tempVersion;
                                    if (Version.TryParse(sdkVersion, out tempVersion))
                                    {
                                        string sdkDirectoryKey = sdkNameKey + @"\" + sdkVersion;
                                        ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Getting default key for '{0}'", sdkDirectoryKey);

                                        // Now that we found the registry key we need to get its default value which points to the directory this SDK is in.
                                        string directoryName = getRegistrySubKeyDefaultValue(baseKey, sdkDirectoryKey);
                                        string sdkKey = TargetPlatformSDK.GetSdkKey(sdkName, sdkVersion);
                                        if (directoryName != null)
                                        {
                                            ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "SDK installation location = '{0}'", directoryName);

                                            // Make sure the directory exists and that it has not been added before.
                                            if (!targetPlatformSDK.ExtensionSDKs.ContainsKey(sdkKey))
                                            {
                                                if (FileUtilities.DirectoryExistsNoThrow(directoryName))
                                                {
                                                    string sdkManifestFileLocation = Path.Combine(directoryName, "SDKManifest.xml");
                                                    if (fileExists(sdkManifestFileLocation))
                                                    {
                                                        ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Adding SDK '{0}'  at '{1}' to the list of found sdks.", sdkKey, directoryName);
                                                        targetPlatformSDK.ExtensionSDKs.Add(sdkKey, FileUtilities.EnsureTrailingSlash(directoryName));
                                                    }
                                                    else
                                                    {
                                                        ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "No SDKManifest.xml file found at '{0}'.", sdkManifestFileLocation);
                                                    }
                                                }
                                                else
                                                {
                                                    ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "SDK directory '{0}' does not exist", directoryName);
                                                }
                                            }
                                            else
                                            {
                                                ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "SDK key was previously added. '{0}'", sdkKey);
                                            }
                                        }
                                        else
                                        {
                                            ErrorUtilities.DebugTraceMessage("GatherSDKsFromRegistryImpl", "Default key is null for '{0}'", sdkDirectoryKey);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        ///  Gather the list of SDKs installed on the machine from the registry.
        ///  Do not parallelize the getting of these entries, order is important, we want the first ones in to win.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void GatherSDKListFromRegistry(string registryRoot, Dictionary<TargetPlatformSDK, TargetPlatformSDK> platformMonikers)
        {
            // Setup some delegates because the methods we call use them during unit testing.
            GetRegistrySubKeyNames getSubkeyNames = new GetRegistrySubKeyNames(RegistryHelper.GetSubKeyNames);
            GetRegistrySubKeyDefaultValue getRegistrySubKeyDefaultValue = new GetRegistrySubKeyDefaultValue(RegistryHelper.GetDefaultValue);
            OpenBaseKey openBaseKey = new OpenBaseKey(RegistryHelper.OpenBaseKey);
            FileExists fileExists = new FileExists(File.Exists);

            bool is64bitOS = EnvironmentUtilities.Is64BitOperatingSystem;

            // Under WOW64 the HKEY_CURRENT_USER\SOFTWARE key is shared. This means the values are the same in the 64 bit and 32 bit views. This means we only need to get one view of this key.
            GatherSDKsFromRegistryImpl(platformMonikers, registryRoot, RegistryView.Default, RegistryHive.CurrentUser, getSubkeyNames, getRegistrySubKeyDefaultValue, openBaseKey, fileExists);

            // Since SDKS can contain multiple architecture it makes sense to register both 32 bit and 64 bit in one location, but if for some reason that
            // is not possible then we need to look at both hives. Choosing the 32 bit one first because is where we expect to find them usually.
            if (is64bitOS)
            {
                GatherSDKsFromRegistryImpl(platformMonikers, registryRoot, RegistryView.Registry32, RegistryHive.LocalMachine, getSubkeyNames, getRegistrySubKeyDefaultValue, openBaseKey, fileExists);
                GatherSDKsFromRegistryImpl(platformMonikers, registryRoot, RegistryView.Registry64, RegistryHive.LocalMachine, getSubkeyNames, getRegistrySubKeyDefaultValue, openBaseKey, fileExists);
            }
            else
            {
                GatherSDKsFromRegistryImpl(platformMonikers, registryRoot, RegistryView.Default, RegistryHive.LocalMachine, getSubkeyNames, getRegistrySubKeyDefaultValue, openBaseKey, fileExists);
            }
        }

        /// <summary>
        /// Get the disk locations to search for sdks under. This can be overridden by an environment variable
        /// </summary>
        private static void GetDefaultSDKDiskRoots(List<string> diskRoots)
        {
            if (NativeMethodsShared.IsWindows)
            {
                // The order is important here because we want to look in the users location first before the non privileged location.

                // We need this so that a user can also have an sdk installed in a non privileged location
                string userLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (userLocalAppData.Length > 0)
                {
                    string localAppdataFolder = Path.Combine(userLocalAppData, "Microsoft SDKs");
                    if (FileSystems.Default.DirectoryExists(localAppdataFolder))
                    {
                        diskRoots.Add(localAppdataFolder);
                    }
                }

                string defaultProgramFilesLocation = Path.Combine(
                    FrameworkLocationHelper.programFiles32,
                    "Microsoft SDKs");
                diskRoots.Add(defaultProgramFilesLocation);
            }
            else
            {
                diskRoots.Add(NativeMethodsShared.FrameworkBasePath);
            }
        }

        /// <summary>
        /// Extract the disk roots from the environment
        /// </summary>
        private static void ExtractSdkDiskRootsFromEnvironment(List<string> diskRoots, string directoryRoots)
        {
            if (diskRoots != null && !string.IsNullOrEmpty(directoryRoots))
            {
                string[] splitRoots = directoryRoots.Split(s_diskRootSplitChars, StringSplitOptions.RemoveEmptyEntries);
                ErrorUtilities.DebugTraceMessage("ExtractSdkDiskRootsFromEnvironment", "DiskRoots from Registry '{0}'", string.Join(";", splitRoots));
                diskRoots.AddRange(splitRoots);
            }

            if (diskRoots != null)
            {
                diskRoots.ForEach(x => x = x.Trim());
                diskRoots.RemoveAll(x => !FileUtilities.DirectoryExistsNoThrow(x));
            }
        }

        /// <summary>
        /// Get the disk roots to search for both platform and extension sdks in. The environment variable can
        /// override the defaults.
        /// </summary>
        /// <returns></returns>
        private static List<string> GetTargetPlatformMonikerDiskRoots(string[] diskRoots)
        {
            var sdkDiskRoots = new List<string>();
            string sdkDirectoryRootsFromEnvironment = Environment.GetEnvironmentVariable("MSBUILDSDKREFERENCEDIRECTORY");
            ExtractSdkDiskRootsFromEnvironment(sdkDiskRoots, sdkDirectoryRootsFromEnvironment);
            if (sdkDiskRoots.Count == 0)
            {
                if (diskRoots?.Length > 0)
                {
                    ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerDiskRoots", "Passed in DiskRoots '{0}'", string.Join(";", diskRoots));
                    sdkDiskRoots.AddRange(diskRoots);
                }
                else
                {
                    ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerDiskRoots", "Getting default disk roots");
                    GetDefaultSDKDiskRoots(sdkDiskRoots);
                }
            }

            ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerDiskRoots", "Diskroots being used '{0}'", string.Join(";", sdkDiskRoots.ToArray()));
            return sdkDiskRoots;
        }

        /// <summary>
        /// Get the disk roots to search for multi platform extension sdks in. The environment variable can
        /// override the defaults.
        /// </summary>
        private static List<string> GetExtensionSdkDiskRoots(string[] diskRoots)
        {
            var sdkDiskRoots = new List<string>();
            string sdkDirectoryRootsFromEnvironment = Environment.GetEnvironmentVariable("MSBUILDMULTIPLATFORMSDKREFERENCEDIRECTORY");
            ExtractSdkDiskRootsFromEnvironment(sdkDiskRoots, sdkDirectoryRootsFromEnvironment);
            if (sdkDiskRoots.Count == 0 && diskRoots?.Length > 0)
            {
                ErrorUtilities.DebugTraceMessage("GetMultiPlatformSdkDiskRoots", "Passed in DiskRoots '{0}'", string.Join(";", diskRoots));
                sdkDiskRoots.AddRange(diskRoots);
            }

            ErrorUtilities.DebugTraceMessage("GetMultiPlatformSdkDiskRoots", "Diskroots being used '{0}'", string.Join(";", sdkDiskRoots.ToArray()));
            return sdkDiskRoots;
        }

        /// <summary>
        /// Get the registry root to find sdks under. The registry can be disabled if we are in a checked in scenario
        /// </summary>
        /// <returns></returns>
        private static string GetTargetPlatformMonikerRegistryRoots(string registryRootLocation)
        {
            ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerRegistryRoots", "RegistryRoot passed in '{0}'", registryRootLocation ?? string.Empty);

            string disableRegistryForSDKLookup = Environment.GetEnvironmentVariable("MSBUILDDISABLEREGISTRYFORSDKLOOKUP");
            // If we are not disabling the registry for platform sdk lookups then lets look in the default location.
            string registryRoot = string.Empty;
            if (disableRegistryForSDKLookup == null)
            {
                if (!string.IsNullOrEmpty(registryRootLocation))
                {
                    registryRoot = registryRootLocation;
                }
                else
                {
                    registryRoot = @"SOFTWARE\MICROSOFT\Microsoft SDKs\";
                }

                ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerRegistryRoots", "RegistryRoot to be looked under '{0}'", registryRoot);
            }
            else
            {
                ErrorUtilities.DebugTraceMessage("GetTargetPlatformMonikerRegistryRoots", "MSBUILDDISABLEREGISTRYFORSDKLOOKUP is set registry sdk lookup is disabled");
            }

            return registryRoot;
        }

        /// <summary>
        /// Given a platform SDK object, populate its supported platforms.
        /// </summary>
        private static void GatherPlatformsForSdk(TargetPlatformSDK sdk)
        {
            ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(sdk.Path), "SDK path must be set");

            try
            {
                string platformsRoot = Path.Combine(sdk.Path, platformsFolderName);
                DirectoryInfo platformsRootInfo = new DirectoryInfo(platformsRoot);

                if (platformsRootInfo.Exists)
                {
                    DirectoryInfo[] platformIdentifiers = platformsRootInfo.GetDirectories();
                    ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "Found '{0}' platform identifier directories under '{1}'", platformIdentifiers.Length, platformsRoot);

                    // Iterate through all identifiers
                    foreach (DirectoryInfo platformIdentifier in platformIdentifiers)
                    {
                        DirectoryInfo[] platformVersions = platformIdentifier.GetDirectories();
                        ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "Found '{0}' platform version directories under '{1}'", platformVersions.Length, platformIdentifier.FullName);

                        // and all versions under each of those identifiers
                        foreach (DirectoryInfo platformVersion in platformVersions)
                        {
                            // If this version directory is not actually a proper version format, ignore it.
                            Version tempVersion;
                            if (Version.TryParse(platformVersion.Name, out tempVersion))
                            {
                                string sdkKey = TargetPlatformSDK.GetSdkKey(platformIdentifier.Name, platformVersion.Name);

                                // make sure we haven't already seen this one somehow
                                if (!sdk.Platforms.ContainsKey(sdkKey))
                                {
                                    ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "SDKKey '{0}' was not already found.", sdkKey);

                                    string pathToPlatformManifest = Path.Combine(platformVersion.FullName, "Platform.xml");
                                    if (FileUtilities.FileExistsNoThrow(pathToPlatformManifest))
                                    {
                                        sdk.Platforms.Add(sdkKey, FileUtilities.EnsureTrailingSlash(platformVersion.FullName));
                                    }
                                    else
                                    {
                                        ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "No Platform.xml could be found at '{0}'. Not adding this platform", pathToPlatformManifest);
                                    }
                                }
                                else
                                {
                                    ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "SDKKey '{0}' was already found, not adding platform under '{1}'", sdkKey, platformVersion.FullName);
                                }
                            }
                            else
                            {
                                ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "Failed to parse platform version folder '{0}' under '{1}'", platformVersion.Name, platformVersion.FullName);
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                ErrorUtilities.DebugTraceMessage("GatherPlatformsForSdk", "Encountered exception trying to gather platform-specific data: {0}", e.Message);
            }
        }

        /// <summary>
        /// Take the path to a reference assembly directory which contains a RedistList folder which then contains a FrameworkList.xml file.
        /// We will read in the xml file and determine if it has an IncludedFramework element in the redist list. If it does it will calculate
        /// the path where the next link in the chain should be and return that path.
        /// Also, when reading the redist list, if any display name is found it will be cached, keyed off the path passed in.
        /// </summary>
        /// <returns>Return null if we could not chain due to an error or the path not being found. return String.Empty if there is no next element in the chain</returns>
        internal static string ChainReferenceAssemblyPath(string targetFrameworkDirectory)
        {
            string path = Path.GetFullPath(targetFrameworkDirectory);

            lock (s_locker)
            {
                // Cache the results of the chain search so that we do not have to do an expensive read more than once per process per redist list.
                s_chainedReferenceAssemblyPath ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                s_cachedTargetFrameworkDisplayNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (s_chainedReferenceAssemblyPath.TryGetValue(path, out string cachedPath))
                {
                    return cachedPath;
                }
            }

            // Read in the redist list at the specified path, and return
            // the display name and the "include framework" value for chaining.
            // If display name is not available, returns empty string.
            // If include framework is not available, returns null.
            // Caches the display name keyed by the path.

            // Make sure we have a directory with a redist list folder and a FrameworkList.xml file in there as this is what we will use for chaining.
            string redistListFolder = Path.Combine(path, "RedistList");
            string redistFilePath = Path.Combine(redistListFolder, "FrameworkList.xml");

            // If the redist list does not exist then the entire chain is incorrect.
            if (!FileSystems.Default.FileExists(redistFilePath))
            {
                lock (s_locker)
                {
                    s_chainedReferenceAssemblyPath[path] = null;
                    s_cachedTargetFrameworkDisplayNames[path] = null;
                }

                return null;
            }

            string includeFramework = null;
            string displayName = null;
            string redirectPath = null;

            try
            {
                // Read in the xml file looking for the includeFramework inorder to chain.
                XmlReaderSettings readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, CloseInput = true };
                FileStream fs = File.OpenRead(redistFilePath);
                using (XmlReader reader = XmlReader.Create(fs, readerSettings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (string.Equals(reader.Name, "FileList", StringComparison.OrdinalIgnoreCase))
                            {
                                reader.MoveToFirstAttribute();
                                do
                                {
                                    if (string.Equals(reader.Name, "IncludeFramework", StringComparison.OrdinalIgnoreCase))
                                    {
                                        includeFramework = reader.Value;
                                        continue;
                                    }

                                    if (string.Equals(reader.Name, "Name", StringComparison.OrdinalIgnoreCase))
                                    {
                                        displayName = reader.Value;
                                        continue;
                                    }
                                }
                                while (reader.MoveToNextAttribute());
                                reader.MoveToElement();
                                break;
                            }
                        }
                    }
                }
            }
            catch (XmlException ex)
            {
                ErrorUtilities.ThrowInvalidOperation("ToolsLocationHelper.InvalidRedistFile", redistFilePath, ex.Message);
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
                ErrorUtilities.ThrowInvalidOperation("ToolsLocationHelper.InvalidRedistFile", redistFilePath, ex.Message);
            }

            // Cache the display name if we have one
            if (displayName != null)
            {
                lock (s_locker)
                {
                    s_cachedTargetFrameworkDisplayNames[path] = displayName;
                }
            }

            string pathToReturn = string.Empty;

            try
            {
                // The IncludeFramework element could not be found so our chain is done.
                if (!string.IsNullOrEmpty(includeFramework))
                {
                    // Take the path which should point to something like  c:\ProgramFiles\ReferenceAssemblies\Framework\.NETFramework\v4.1
                    // We will take the path, to "up" a directory then append the name found in the redist. For example if the redist list had v4.0
                    // the path which would be expected would be c:\ProgramFiles\ReferenceAssemblies\Framework\.NETFramework\v4.0
                    pathToReturn = path;
                    pathToReturn = Directory.GetParent(pathToReturn).FullName;
                    pathToReturn = Path.Combine(pathToReturn, includeFramework);
                    pathToReturn = Path.GetFullPath(pathToReturn);

                    // The directory which we are chaining to does not exist, return null indicating the chain is incorrect.
                    if (!FileSystems.Default.DirectoryExists(pathToReturn))
                    {
                        pathToReturn = null;
                    }
                }
                // We may also have a redirect path
                else if (!string.IsNullOrEmpty(redirectPath) && FileSystems.Default.DirectoryExists(redirectPath))
                {
                    pathToReturn = redirectPath;
                }

                lock (s_locker)
                {
                    s_chainedReferenceAssemblyPath[path] = pathToReturn;
                }

                return pathToReturn;
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                ErrorUtilities.ThrowInvalidOperation("ToolsLocationHelper.CouldNotCreateChain", path, pathToReturn, e.Message);
            }

            return null;
        }

        /// <summary>
        /// Get a fully qualified path to a file in the latest .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// When targeting .NET 3.5 or above, looks in the locations associated with Visual Studio 2010.  If you wish to
        /// target the .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override that
        /// specifies a VisualStudioVersion.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdkFile(string fileName)
            => GetPathToDotNetFrameworkSdkFile(fileName, TargetDotNetFrameworkVersion.Latest);

        /// <summary>
        /// Get a fully qualified path to a file in the .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// When targeting .NET 3.5 or above, looks in the locations associated with Visual Studio 2010.  If you wish to
        /// target the .NET Framework SDK that ships with Visual Studio Dev11 or later, please use the override that
        /// specifies a VisualStudioVersion.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdkFile(string fileName, TargetDotNetFrameworkVersion version)
            => GetPathToDotNetFrameworkSdkFile(fileName, version, VisualStudioVersion.VersionLatest);

        /// <summary>
        /// Get a fully qualified path to a file in the .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio the requested SDK is associated with</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
            => GetPathToDotNetFrameworkSdkFile(
                fileName,
                version,
                visualStudioVersion,
                UtilitiesDotNetFrameworkArchitecture.Current,
                canFallBackIfNecessary: true); /* If the file is not found for the current architecture, it's OK to follow fallback mechanisms. */

        /// <summary>
        /// Get a fully qualified path to a file in the .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="architecture">The required architecture of the requested file.</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdkFile(string fileName, TargetDotNetFrameworkVersion version, UtilitiesDotNetFrameworkArchitecture architecture) => GetPathToDotNetFrameworkSdkFile(fileName, version, VisualStudioVersion.VersionLatest, architecture);

        /// <summary>
        /// Get a fully qualified path to a file in the .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio</param>
        /// <param name="architecture">The required architecture of the requested file.</param>
        /// <returns>Path string.</returns>
        public static string GetPathToDotNetFrameworkSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion, UtilitiesDotNetFrameworkArchitecture architecture)
            => GetPathToDotNetFrameworkSdkFile(
                fileName,
                version,
                visualStudioVersion,
                architecture,
                false); /* Do _not_ fall back -- if the user is specifically requesting a particular architecture, they want that architecture. */

        /// <summary>
        /// Get a fully qualified path to a file in the .NET Framework SDK. Error if the .NET Framework SDK can't be found.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework SDK directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="visualStudioVersion">Version of Visual Studio</param>
        /// <param name="architecture">The required architecture of the requested file.</param>
        /// <param name="canFallBackIfNecessary">If true, will follow the fallback pattern -- from requested architecture, to
        /// current architecture, to x86.  Otherwise, if the requested architecture path doesn't exist, that's it -- no path
        /// will be returned.</param>
        /// <returns></returns>
        private static string GetPathToDotNetFrameworkSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion, UtilitiesDotNetFrameworkArchitecture architecture, bool canFallBackIfNecessary)
        {
            string pathToSdk = GetPathToDotNetFrameworkSdkToolsFolderRoot(version, visualStudioVersion);
            string filePath = null;

            if (pathToSdk != null)
            {
                string convertedArchitecture = ConvertDotNetFrameworkArchitectureToProcessorArchitecture(architecture);

                // first take a look at the requested architecture
                filePath = GetPathToDotNetFrameworkSdkFile(fileName, pathToSdk, convertedArchitecture);

                if (filePath == null && canFallBackIfNecessary)
                {
                    // Now look for a version of the tool which matches the bitness of this process if we haven't already
                    if (!string.Equals(ProcessorArchitecture.CurrentProcessArchitecture, convertedArchitecture, StringComparison.OrdinalIgnoreCase))
                    {
                        filePath = GetPathToDotNetFrameworkSdkFile(fileName, pathToSdk, ProcessorArchitecture.CurrentProcessArchitecture);
                    }

                    // If we couldn't find that and we're in a non-x86 process, then fall back to the x86 version
                    if (filePath == null && !string.Equals(ProcessorArchitecture.X86, ProcessorArchitecture.CurrentProcessArchitecture, StringComparison.OrdinalIgnoreCase))
                    {
                        filePath = GetPathToDotNetFrameworkSdkFile(fileName, pathToSdk, ProcessorArchitecture.X86);
                    }
                }
            }

            return filePath;
        }

        /// <summary>
        /// Gets the path to a sdk exe based on the processor architecture and the provided bin directory path.
        /// If the fileName cannot be found in the pathToSDK after the processor architecture has been taken into account a null is returned.
        /// </summary>
        internal static string GetPathToDotNetFrameworkSdkFile(string fileName, string pathToSdk, string processorArchitecture)
        {
            if (pathToSdk == null || fileName == null || processorArchitecture == null)
            {
                return null;
            }

            switch (processorArchitecture)
            {
                case ProcessorArchitecture.AMD64:
                    pathToSdk = Path.Combine(pathToSdk, "x64");
                    break;
                case ProcessorArchitecture.IA64:
                    pathToSdk = Path.Combine(pathToSdk, "ia64");
                    break;
                case ProcessorArchitecture.X86:
                case ProcessorArchitecture.ARM:
                default:
                    break;
            }

            string filePath = Path.Combine(pathToSdk, fileName);

            // Use FileInfo instead of FileSystems.Default.FileExists(...) because the latter fails silently (by design) if CAS
            // doesn't grant access. We want the security exception if there is going to be one.
            bool exists = new FileInfo(filePath).Exists;
            if (!exists)
            {
                return null;
            }

            return filePath;
        }

        /// <summary>
        /// Given a member of the DotNetFrameworkArchitecture enumeration, returns the equivalent ProcessorArchitecture string.
        /// Internal for Testing Purposes Only
        /// </summary>
        /// <param name="architecture"></param>
        /// <returns></returns>
        internal static string ConvertDotNetFrameworkArchitectureToProcessorArchitecture(DotNetFrameworkArchitecture architecture)
        {
            switch (architecture)
            {
                case DotNetFrameworkArchitecture.Bitness32:
                    if (ProcessorArchitecture.CurrentProcessArchitecture == ProcessorArchitecture.ARM ||
                        ProcessorArchitecture.CurrentProcessArchitecture == ProcessorArchitecture.ARM64)
                    {
                        return ProcessorArchitecture.ARM;
                    }
                    return ProcessorArchitecture.X86;
                case DotNetFrameworkArchitecture.Bitness64:
                    // We need to know which 64-bit architecture we're on.
                    return NativeMethodsShared.ProcessorArchitectureNative switch
                    {
                        NativeMethodsShared.ProcessorArchitectures.X64 => ProcessorArchitecture.AMD64,
                        NativeMethodsShared.ProcessorArchitectures.IA64 => ProcessorArchitecture.IA64,
                        NativeMethodsShared.ProcessorArchitectures.ARM64 => ProcessorArchitecture.ARM64,
                        // Error, OK, we're trying to get the 64-bit path on a 32-bit machine.
                        // That ... doesn't make sense.
                        NativeMethodsShared.ProcessorArchitectures.X86 => null,
                        NativeMethodsShared.ProcessorArchitectures.ARM => null,
                        // unknown architecture? return null
                        _ => null,
                    };
                case DotNetFrameworkArchitecture.Current:
                    return ProcessorArchitecture.CurrentProcessArchitecture;
            }

            ErrorUtilities.ThrowInternalErrorUnreachable();
            return null;
        }

        /// <summary>
        /// Returns the path to the Windows SDK for the desired .NET Framework and Visual Studio version.  Note that
        /// this is only supported for a targeted .NET Framework version of 4.5 and above.
        /// </summary>
        /// <param name="version">Target .NET Framework version</param>
        /// <param name="visualStudioVersion">Version of Visual Studio associated with the SDK.</param>
        /// <returns>Path to the appropriate Windows SDK location</returns>
        [Obsolete("Consider using GetPlatformSDKLocation instead")]
        public static string GetPathToWindowsSdk(TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
            => FrameworkLocationHelper.GetPathToWindowsSdk(TargetDotNetFrameworkVersionToSystemVersion(version));

        /// <summary>
        /// Returns the path to a file in the Windows SDK for the desired .NET Framework and Visual Studio version.  Note that
        /// this is only supported for a targeted .NET Framework version of 4.5 and above.
        /// </summary>
        /// <param name="fileName">The name of the file being requested.</param>
        /// <param name="version">Target .NET Framework version.</param>
        /// <param name="visualStudioVersion">Version of Visual Studio associated with the SDK.</param>
        /// <returns>Path to the appropriate Windows SDK file</returns>
        [Obsolete("Consider using GetPlatformSDKLocationFile instead")]
        public static string GetPathToWindowsSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion)
            => GetPathToWindowsSdkFile(
                fileName,
                version,
                visualStudioVersion,
                UtilitiesDotNetFrameworkArchitecture.Current,
                true); /* If the file is not found for the current architecture, it's OK to follow fallback mechanisms. */

        /// <summary>
        /// Returns the path to a file in the Windows SDK for the desired .NET Framework and Visual Studio version and the desired
        /// architecture.  Note that this is only supported for a targeted .NET Framework version of 4.5 and above.
        /// </summary>
        /// <param name="fileName">The name of the file being requested.</param>
        /// <param name="version">Target .NET Framework version.</param>
        /// <param name="visualStudioVersion">Version of Visual Studio associated with the SDK.</param>
        /// <param name="architecture">Desired architecture of the resultant file.</param>
        /// <returns>Path to the appropriate Windows SDK file</returns>
        [Obsolete("Consider using GetPlatformSDKLocationFile instead")]
        public static string GetPathToWindowsSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion, DotNetFrameworkArchitecture architecture)
            => GetPathToWindowsSdkFile(
                fileName,
                version,
                visualStudioVersion,
                architecture,
                false); /* Do _not_ fall back -- if the user is specifically requesting a particular architecture, they want that architecture. */

        /// <summary>
        /// Returns the path to a file in the Windows SDK for the desired .NET Framework and Visual Studio version and the desired
        /// architecture.  Note that this is only supported for a targeted .NET Framework version of 4.5 and above.
        /// </summary>
        /// <param name="fileName">The name of the file being requested.</param>
        /// <param name="version">Target .NET Framework version.</param>
        /// <param name="visualStudioVersion">Version of Visual Studio associated with the SDK.</param>
        /// <param name="architecture">Desired architecture of the resultant file.</param>
        /// <param name="canFallBackIfNecessary"><code>true</code> to fallback, otherwise <code>false</code>.</param>
        /// <returns>Path to the appropriate Windows SDK file</returns>
        [Obsolete("Consider using GetPlatformSDKLocationFile instead")]
        private static string GetPathToWindowsSdkFile(string fileName, TargetDotNetFrameworkVersion version, VisualStudioVersion visualStudioVersion, DotNetFrameworkArchitecture architecture, bool canFallBackIfNecessary)
        {
            string pathToSdk = GetPathToWindowsSdk(version, visualStudioVersion);
            string filePath = null;

            if (pathToSdk != null)
            {
                pathToSdk = Path.Combine(pathToSdk, "bin");

                string convertedArchitecture = ConvertDotNetFrameworkArchitectureToProcessorArchitecture(architecture);

                // first take a look at the requested architecture
                filePath = GetPathToWindowsSdkFile(fileName, pathToSdk, convertedArchitecture);

                if (filePath == null && canFallBackIfNecessary)
                {
                    // Now look for a version of the tool which matches the bitness of this process if we haven't already
                    if (!string.Equals(ProcessorArchitecture.CurrentProcessArchitecture, convertedArchitecture, StringComparison.OrdinalIgnoreCase))
                    {
                        filePath = GetPathToWindowsSdkFile(fileName, pathToSdk, ProcessorArchitecture.CurrentProcessArchitecture);
                    }

                    // If we couldn't find that and we're in a non-x86 process, then fall back to the x86 version
                    if (filePath == null && !string.Equals(ProcessorArchitecture.X86, ProcessorArchitecture.CurrentProcessArchitecture, StringComparison.OrdinalIgnoreCase))
                    {
                        filePath = GetPathToWindowsSdkFile(fileName, pathToSdk, ProcessorArchitecture.X86);
                    }
                }
            }

            return filePath;
        }

        /// <summary>
        /// Gets the path to a sdk exe based on the processor architecture and the provided bin directory path.
        /// If the fileName cannot be found in the pathToSDK after the processor architecture has been taken into account a null is returned.
        /// </summary>
        [Obsolete("Consider using GetPlatformSDKLocationFile instead")]
        private static string GetPathToWindowsSdkFile(string fileName, string pathToSdk, string processorArchitecture)
        {
            if (pathToSdk == null || fileName == null || processorArchitecture == null)
            {
                return null;
            }

            switch (processorArchitecture)
            {
                case ProcessorArchitecture.X86:
                    pathToSdk = Path.Combine(pathToSdk, "x86");
                    break;
                case ProcessorArchitecture.AMD64:
                    pathToSdk = Path.Combine(pathToSdk, "x64");
                    break;
                case ProcessorArchitecture.IA64:
                case ProcessorArchitecture.ARM:
                default:
                    break;
            }

            string filePath = Path.Combine(pathToSdk, fileName);

            // Use FileInfo instead of FileSystems.Default.FileExists(...) because the latter fails silently (by design) if CAS
            // doesn't grant access. We want the security exception if there is going to be one.
            bool exists = new FileInfo(filePath).Exists;
            if (!exists)
            {
                return null;
            }

            return filePath;
        }

        /// <summary>
        /// Given a ToolsVersion, return the path to the MSBuild tools for that ToolsVersion
        /// </summary>
        /// <param name="toolsVersion">The ToolsVersion for which to get the tools path</param>
        /// <returns>The tools path folder of the appropriate ToolsVersion if it exists, otherwise null.</returns>
        public static string GetPathToBuildTools(string toolsVersion) => GetPathToBuildTools(toolsVersion, UtilitiesDotNetFrameworkArchitecture.Current);

        /// <summary>
        /// Given a ToolsVersion, return the path to the MSBuild tools for that ToolsVersion
        /// </summary>
        /// <param name="toolsVersion">The ToolsVersion for which to get the tools path</param>
        /// <param name="architecture">The architecture of the build tools location to get</param>
        /// <returns>The tools path folder of the appropriate ToolsVersion if it exists, otherwise null.</returns>
        public static string GetPathToBuildTools(string toolsVersion, UtilitiesDotNetFrameworkArchitecture architecture)
        {
            return toolsVersion switch
            {
                "2.0" => GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20, architecture),
                "3.5" => GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version35, architecture),
                "4.0" => GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version40, architecture),
                // Doesn't map to an existing .NET Framework, so let's grab it out of the toolset.
                _ => FrameworkLocationHelper.GeneratePathToBuildToolsForToolsVersion(toolsVersion, ConvertToSharedDotNetFrameworkArchitecture(architecture)),
            };
        }

        /// <summary>
        /// Given the name of a file and a ToolsVersion, return the path to that file in the MSBuild
        /// tools path for that ToolsVersion
        /// </summary>
        /// <param name="fileName">The file to find the path to</param>
        /// <param name="toolsVersion">The ToolsVersion in which to find the file</param>
        /// <returns>The path to the file in the tools path folder of the appropriate ToolsVersion if it
        /// exists, otherwise null.</returns>
        public static string GetPathToBuildToolsFile(string fileName, string toolsVersion) => GetPathToBuildToolsFile(fileName, toolsVersion, UtilitiesDotNetFrameworkArchitecture.Current);

        /// <summary>
        /// Given the name of a file and a ToolsVersion, return the path to that file in the MSBuild
        /// tools path for that ToolsVersion
        /// </summary>
        /// <param name="fileName">The file to find the path to</param>
        /// <param name="toolsVersion">The ToolsVersion in which to find the file</param>
        /// <param name="architecture">The architecture of the build tools file to get</param>
        /// <returns>The path to the file in the tools path folder of the appropriate ToolsVersion if it
        /// exists, otherwise null.</returns>
        public static string GetPathToBuildToolsFile(string fileName, string toolsVersion, UtilitiesDotNetFrameworkArchitecture architecture)
        {
            string toolPath = GetPathToBuildTools(toolsVersion, architecture);

            if (toolPath != null)
            {
                toolPath = Path.Combine(toolPath, fileName);

                // Rollback see https://developercommunity.visualstudio.com/t/Unable-to-locate-MSBuild-path-with-Lates/10824132 
                if (!File.Exists(toolPath))
                {
                    toolPath = null;
                }
            }

            return toolPath;
        }

        /// <summary>
        /// Get a fully qualified path to a file in the frameworks root directory.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <returns>Will return 'null' if there is no target frameworks on this machine.</returns>
        public static string GetPathToDotNetFrameworkFile(string fileName, TargetDotNetFrameworkVersion version) => GetPathToDotNetFrameworkFile(fileName, version, UtilitiesDotNetFrameworkArchitecture.Current);

        /// <summary>
        /// Get a fully qualified path to a file in the frameworks root directory for the specified architecture.
        /// </summary>
        /// <param name="fileName">File name to locate in the .NET Framework directory</param>
        /// <param name="version">Version of the targeted .NET Framework</param>
        /// <param name="architecture">Desired architecture, or DotNetFrameworkArchitecture.Current for the architecture this process is currently running under.</param>
        /// <returns>Will return 'null' if there is no target frameworks on this machine.</returns>
        public static string GetPathToDotNetFrameworkFile(string fileName, TargetDotNetFrameworkVersion version, UtilitiesDotNetFrameworkArchitecture architecture)
        {
            string pathToFx = GetPathToDotNetFramework(version, architecture);
            return pathToFx == null ? null : Path.Combine(pathToFx, fileName);
        }

        /// <summary>
        /// Get a fully qualified path to a file in the system directory (i.e. %SystemRoot%\System32)
        /// </summary>
        /// <param name="fileName">File name to locate in the system directory</param>
        /// <returns>Path string.</returns>
        public static string GetPathToSystemFile(string fileName) => Path.Combine(PathToSystem, fileName);

        /// <summary>
        /// Gets a IList of supported target framework monikers.
        /// </summary>
        /// <returns>list of supported target framework monikers</returns>
        public static IList<string> GetSupportedTargetFrameworks()
        {
            lock (s_locker)
            {
                if (s_targetFrameworkMonikers == null)
                {
                    s_targetFrameworkMonikers = new List<string>();
                    IList<string> frameworkIdentifiers = GetFrameworkIdentifiers(FrameworkLocationHelper.programFilesReferenceAssemblyLocation);
                    foreach (string frameworkIdentifier in frameworkIdentifiers)
                    {
                        IList<string> frameworkVersions = GetFrameworkVersions(FrameworkLocationHelper.programFilesReferenceAssemblyLocation, frameworkIdentifier);
                        foreach (string frameworkVersion in frameworkVersions)
                        {
                            Version version = VersionUtilities.ConvertToVersion(frameworkVersion);
                            s_targetFrameworkMonikers.Add(new FrameworkNameVersioning(frameworkIdentifier, version, null).FullName);

                            IList<string> frameworkProfile = GetFrameworkProfiles(FrameworkLocationHelper.programFilesReferenceAssemblyLocation, frameworkIdentifier, frameworkVersion);
                            foreach (string profile in frameworkProfile)
                            {
                                s_targetFrameworkMonikers.Add(new FrameworkNameVersioning(frameworkIdentifier, version, profile).FullName);
                            }
                        }
                    }
                }
            }

            return s_targetFrameworkMonikers;
        }

        /// <summary>
        /// This method will return the highest version of a target framework moniker based on the identifier. This method will only
        /// find full frameworks, this means no profiles will be returned.
        /// </summary>
        public static FrameworkNameVersioning HighestVersionOfTargetFrameworkIdentifier(string targetFrameworkRootDirectory, string frameworkIdentifier)
        {
            ErrorUtilities.VerifyThrowArgumentLength(targetFrameworkRootDirectory);
            ErrorUtilities.VerifyThrowArgumentLength(frameworkIdentifier);

            string key = targetFrameworkRootDirectory + ";" + frameworkIdentifier;
            FrameworkNameVersioning highestFrameworkName = null;
            bool foundInCache = false;

            lock (s_locker)
            {
                if (s_cachedHighestFrameworkNameForTargetFrameworkIdentifier == null)
                {
                    s_cachedHighestFrameworkNameForTargetFrameworkIdentifier = new Dictionary<string, FrameworkNameVersioning>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    foundInCache = s_cachedHighestFrameworkNameForTargetFrameworkIdentifier.TryGetValue(key, out highestFrameworkName);
                }

                if (!foundInCache)
                {
                    IList<string> frameworkVersions = GetFrameworkVersions(targetFrameworkRootDirectory, frameworkIdentifier);
                    if (frameworkVersions.Count > 0)
                    {
                        Version targetFrameworkVersion = ConvertTargetFrameworkVersionToVersion(frameworkVersions[frameworkVersions.Count - 1]);
                        highestFrameworkName = new FrameworkNameVersioning(frameworkIdentifier, targetFrameworkVersion);
                    }

                    s_cachedHighestFrameworkNameForTargetFrameworkIdentifier.Add(key, highestFrameworkName);
                }
            }

            return highestFrameworkName;
        }

        /// <summary>
        /// Will return the root location for the reference assembly directory under the program files directory.
        /// </summary>
        /// <returns></returns>
        public static string GetProgramFilesReferenceAssemblyRoot() => FrameworkLocationHelper.programFilesReferenceAssemblyLocation;

        #endregion

        #region private methods

        /// <summary>
        /// Converts a member of the Microsoft.Build.Utilities.DotNetFrameworkArchitecture enum to the equivalent member of the
        /// Microsoft.Build.Shared.DotNetFrameworkArchitecture enum.
        /// </summary>
        private static SharedDotNetFrameworkArchitecture ConvertToSharedDotNetFrameworkArchitecture(UtilitiesDotNetFrameworkArchitecture architecture)
        {
            SharedDotNetFrameworkArchitecture sharedArchitecture = SharedDotNetFrameworkArchitecture.Current;
            switch (architecture)
            {
                case UtilitiesDotNetFrameworkArchitecture.Current:
                    sharedArchitecture = SharedDotNetFrameworkArchitecture.Current;
                    break;
                case UtilitiesDotNetFrameworkArchitecture.Bitness32:
                    sharedArchitecture = SharedDotNetFrameworkArchitecture.Bitness32;
                    break;
                case UtilitiesDotNetFrameworkArchitecture.Bitness64:
                    sharedArchitecture = SharedDotNetFrameworkArchitecture.Bitness64;
                    break;
                default:
                    // Should never reach here -- If any new values are added to the DotNetFrameworkArchitecture enum, they should be added here as well.
                    ErrorUtilities.ThrowInternalErrorUnreachable();
                    break;
            }

            return sharedArchitecture;
        }

        /// <summary>
        /// Given a string which may start with a "v" convert the string to a version object.
        /// </summary>
        private static Version ConvertTargetFrameworkVersionToVersion(string targetFrameworkVersion)
        {
            // Trim off the v if is is there.
            if (!string.IsNullOrEmpty(targetFrameworkVersion) && targetFrameworkVersion.Substring(0, 1).Equals("v", StringComparison.OrdinalIgnoreCase))
            {
                targetFrameworkVersion = targetFrameworkVersion.Substring(1);
            }

            return new Version(targetFrameworkVersion);
        }

        /// <summary>
        /// Gets the installed framework identifiers
        /// </summary>
        /// <param name="frameworkReferenceRoot"></param>
        /// <returns></returns>
        internal static IList<string> GetFrameworkIdentifiers(string frameworkReferenceRoot)
        {
            if (string.IsNullOrEmpty(frameworkReferenceRoot))
            {
                throw new ArgumentException("Invalid frameworkReferenceRoot", nameof(frameworkReferenceRoot));
            }

            var frameworkIdentifiers = new List<string>();

            bool bAddDotNetFrameworkIdentifier = false;
            bool bFoundDotNetFrameworkIdentifier = false;
            bool programFilesReferenceAssemblyLocationFound = false;

            DirectoryInfo di = new DirectoryInfo(frameworkReferenceRoot);
            if (di.Exists)
            {
                if (frameworkReferenceRoot.Equals(FrameworkLocationHelper.programFilesReferenceAssemblyLocation, StringComparison.OrdinalIgnoreCase))
                {
                    programFilesReferenceAssemblyLocationFound = true;
                }

                foreach (DirectoryInfo folder in di.GetDirectories())
                {
                    if (programFilesReferenceAssemblyLocationFound &&
                        (
                            string.Equals(folder.Name, FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV30, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(folder.Name, FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV35, StringComparison.OrdinalIgnoreCase)))
                    {
                        bAddDotNetFrameworkIdentifier = true;
                        continue;
                    }

                    if (string.Equals(folder.Name, FrameworkLocationHelper.dotNetFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
                    {
                        bFoundDotNetFrameworkIdentifier = true;
                    }

                    frameworkIdentifiers.Add(folder.Name);
                }
            }

            if (programFilesReferenceAssemblyLocationFound && !bFoundDotNetFrameworkIdentifier)
            {
                if (!bAddDotNetFrameworkIdentifier)
                {
                    // special case for .NETFramework v2.0 - check also in the framework path because v20 does not have reference
                    // assembly folders

                    string dotNetFx20Path = GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20);
                    if (dotNetFx20Path != null)
                    {
                        if (FileSystems.Default.DirectoryExists(dotNetFx20Path))
                        {
                            frameworkIdentifiers.Add(FrameworkLocationHelper.dotNetFrameworkIdentifier);
                        }
                    }
                }
                else
                {
                    frameworkIdentifiers.Add(FrameworkLocationHelper.dotNetFrameworkIdentifier);
                }
            }

            return frameworkIdentifiers;
        }

        /// <summary>
        /// Gets the installed versions for a given framework
        /// </summary>
        private static IList<string> GetFrameworkVersions(string frameworkReferenceRoot, string frameworkIdentifier)
        {
            if (string.IsNullOrEmpty(frameworkReferenceRoot))
            {
                throw new ArgumentException("Invalid frameworkReferenceRoot", nameof(frameworkReferenceRoot));
            }

            if (string.IsNullOrEmpty(frameworkIdentifier))
            {
                throw new ArgumentException("Invalid frameworkIdentifier", nameof(frameworkIdentifier));
            }

            var frameworkVersions = new List<string>();

            // backward compatibility with orcas
            // In case of orcas .NETFramework v3.0, v3.5 - the version folders are directly under the frameworkReferenceRoot
            // first check here
            if (string.Equals(frameworkIdentifier, FrameworkLocationHelper.dotNetFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                IList<string> versions = GetFx35AndEarlierVersions(frameworkReferenceRoot);
                if (versions.Count > 0)
                {
                    frameworkVersions.AddRange(versions);
                }
            }

            // then look under the extensible multi-targeting layout - even for .NETFramework because future .NETFramework
            // versions would be at the right place
            string frameworkIdentifierPath = Path.Combine(frameworkReferenceRoot, frameworkIdentifier);

            DirectoryInfo dirInfoFxIdentifierPath = new DirectoryInfo(frameworkIdentifierPath);
            if (dirInfoFxIdentifierPath.Exists)
            {
                foreach (DirectoryInfo folder in dirInfoFxIdentifierPath.GetDirectories())
                {
                    // the expected version folder name is of the format v<MajorVersion>.<MinorVersion> e.g. v3.5
                    // only add if the version folder name is of the right format
                    if (folder.Name.Length >= 4 && folder.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    {
                        Version ver;
                        if (Version.TryParse(folder.Name.Substring(1), out ver))
                        {
                            frameworkVersions.Add(folder.Name);
                        }
                    }
                }
            }

            // sort in ascending order of the version numbers, this is important as later when we search for assemblies in other methods
            // we should be looking in ascending order of the framework version folders on disk
            frameworkVersions.Sort(VersionComparer.Instance);

            return frameworkVersions;
        }

        /// <summary>
        /// Get installed framework profiles
        /// </summary>
        /// <param name="frameworkReferenceRoot"></param>
        /// <param name="frameworkIdentifier"></param>
        /// <param name="frameworkVersion"></param>
        /// <returns></returns>
        private static IList<string> GetFrameworkProfiles(string frameworkReferenceRoot, string frameworkIdentifier, string frameworkVersion)
        {
            if (string.IsNullOrEmpty(frameworkReferenceRoot))
            {
                throw new ArgumentException("Invalid frameworkReferenceRoot", nameof(frameworkReferenceRoot));
            }

            if (string.IsNullOrEmpty(frameworkIdentifier))
            {
                throw new ArgumentException("Invalid frameworkIdentifier", nameof(frameworkIdentifier));
            }

            if (string.IsNullOrEmpty(frameworkVersion))
            {
                throw new ArgumentException("Invalid frameworkVersion", nameof(frameworkVersion));
            }

            var frameworkProfiles = new List<string>();

            string frameworkProfilePath = Path.Combine(frameworkReferenceRoot, frameworkIdentifier);
            frameworkProfilePath = Path.Combine(frameworkProfilePath, frameworkVersion);
            frameworkProfilePath = Path.Combine(frameworkProfilePath, "Profiles");

            DirectoryInfo dirInfoFxProfilePath = new DirectoryInfo(frameworkProfilePath);
            if (dirInfoFxProfilePath.Exists)
            {
                foreach (DirectoryInfo subType in dirInfoFxProfilePath.GetDirectories())
                {
                    Version ver = VersionUtilities.ConvertToVersion(frameworkVersion);
                    // check if profile is installed correctly
                    IList<string> refAssemblyPaths = GetPathToReferenceAssemblies(new FrameworkNameVersioning(frameworkIdentifier, ver, subType.Name));
                    if (refAssemblyPaths?.Count > 0)
                    {
                        frameworkProfiles.Add(subType.Name);
                    }
                }
            }

            return frameworkProfiles;
        }

        /// <summary>
        /// returns the .NETFramework versions lessthanOrEqualTo 3.5 installed in the machine
        /// Only returns Fx versions lessthanOrEqualTo 3.5 if DNFx3.5 is installed
        /// </summary>
        /// <param name="frameworkReferenceRoot"></param>
        /// <returns></returns>
        private static IList<string> GetFx35AndEarlierVersions(string frameworkReferenceRoot)
        {
            IList<string> versions = new List<string>();

            // only return v35 and earlier versions if .NetFx35 is installed
            string dotNetFx35Path = GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version35);

            if (dotNetFx35Path != null)
            {
                // .NetFx35 is installed

                // check v20
                string dotNetFx20Path = GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20);
                if (dotNetFx20Path != null)
                {
                    versions.Add("v2.0");
                }

                // check v30
                string dotNextFx30RefPath = Path.Combine(frameworkReferenceRoot, FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV30);
                if (FileSystems.Default.DirectoryExists(dotNextFx30RefPath))
                {
                    versions.Add(FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV30);
                }

                // check v35
                string dotNextFx35RefPath = Path.Combine(frameworkReferenceRoot, FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV35);
                if (FileSystems.Default.DirectoryExists(dotNextFx35RefPath))
                {
                    versions.Add(FrameworkLocationHelper.dotNetFrameworkVersionFolderPrefixV35);
                }
            }

            return versions;
        }

        #endregion

        #region private class
        /// <summary>
        /// Compares framework version strings of the format v4.1.2.3
        /// major version and minor version are mandatory others are optional
        /// </summary>
        private class VersionComparer : IComparer<string>
        {
            public static VersionComparer Instance { get; } = new VersionComparer();

            private VersionComparer()
            {
            }

            public int Compare(string versionX, string versionY)
            {
                return new Version(versionX.Substring(1)).CompareTo(new Version(versionY.Substring(1)));
            }
        }

        #endregion
    }
}
