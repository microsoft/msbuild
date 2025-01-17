// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

#nullable disable

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Constants that we want to be shareable across all our assemblies.
    /// </summary>
    internal static class MSBuildConstants
    {
        /// <summary>
        /// The name of the property that indicates the tools path
        /// </summary>
        internal const string ToolsPath = "MSBuildToolsPath";

        /// <summary>
        /// Name of the property that indicates the X64 tools path
        /// </summary>
        internal const string ToolsPath64 = "MSBuildToolsPath64";

        /// <summary>
        /// Name of the property that indicates the root of the SDKs folder
        /// </summary>
        internal const string SdksPath = "MSBuildSDKsPath";

        /// <summary>
        /// Name of the property that indicates that all warnings should be treated as errors.
        /// </summary>
        internal const string TreatWarningsAsErrors = "MSBuildTreatWarningsAsErrors";

        /// <summary>
        /// Name of the property that indicates a list of warnings to treat as errors.
        /// </summary>
        internal const string WarningsAsErrors = "MSBuildWarningsAsErrors";

        /// <summary>
        /// Name of the property that indicates a list of warnings to not treat as errors.
        /// </summary>
        internal const string WarningsNotAsErrors = "MSBuildWarningsNotAsErrors";

        /// <summary>
        /// Name of the property that indicates the list of warnings to treat as messages.
        /// </summary>
        internal const string WarningsAsMessages = "MSBuildWarningsAsMessages";

        /// <summary>
        /// The name of the environment variable that users can specify to override where NuGet assemblies are loaded from in the NuGetSdkResolver.
        /// </summary>
        internal const string NuGetAssemblyPathEnvironmentVariableName = "MSBUILD_NUGET_PATH";

        /// <summary>
        /// The name of the target to run when a user specifies the /restore command-line argument.
        /// </summary>
        internal const string RestoreTargetName = "Restore";
        /// <summary>
        /// The most current Visual Studio Version known to this version of MSBuild.
        /// </summary>
        internal const string CurrentVisualStudioVersion = "17.0";

        /// <summary>
        /// The most current ToolsVersion known to this version of MSBuild.
        /// </summary>
        internal const string CurrentToolsVersion = "Current";

        internal const string MSBuildDummyGlobalPropertyHeader = "MSBuildProjectInstance";

        /// <summary>
        /// A property set during an implicit restore (/restore) or explicit restore (/t:restore) to ensure that the evaluations are not re-used during build
        /// </summary>
        internal const string MSBuildRestoreSessionId = nameof(MSBuildRestoreSessionId);

        /// <summary>
        /// A property set during an implicit restore (/restore) or explicit restore (/t:restore) to indicate that a restore is executing.
        /// </summary>
        internal const string MSBuildIsRestoring = nameof(MSBuildIsRestoring);


        /// <summary>
        /// The most current VSGeneralAssemblyVersion known to this version of MSBuild.
        /// </summary>
        internal const string CurrentAssemblyVersion = "15.1.0.0";

        /// <summary>
        /// Current version of this MSBuild Engine assembly in the form, e.g, "12.0"
        /// </summary>
        internal const string CurrentProductVersion = "17.0";

        /// <summary>
        /// Symbol used in ProjectReferenceTarget items to represent default targets
        /// </summary>
        internal const string DefaultTargetsMarker = ".default";

        /// <summary>
        /// Framework version against which our test projects should be built.
        /// </summary>
        /// <remarks>
        /// The targeting pack for this version of .NET Framework must be installed
        /// on any machine that wants to run tests successfully, so this can be
        /// periodically updated.
        /// </remarks>
        internal const string StandardTestTargetFrameworkVersion = "v4.8";

        /// <summary>
        /// Symbol used in ProjectReferenceTarget items to represent targets specified on the ProjectReference item
        /// with fallback to default targets if the ProjectReference item has no targets specified.
        /// </summary>
        internal const string ProjectReferenceTargetsOrDefaultTargetsMarker = ".projectReferenceTargetsOrDefaultTargets";

        // One-time allocations to avoid implicit allocations for Split(), Trim().
        internal static readonly char[] SemicolonChar = { ';' };
        internal static readonly char[] SpaceChar = { ' ' };
        internal static readonly char[] SingleQuoteChar = { '\'' };
        internal static readonly char[] EqualsChar = { '=' };
        internal static readonly char[] ColonChar = { ':' };
        internal static readonly char[] BackslashChar = { '\\' };
        internal static readonly char[] NewlineChar = { '\n' };
        internal static readonly char[] CrLf = { '\r', '\n' };
        internal static readonly char[] ForwardSlash = { '/' };
        internal static readonly char[] ForwardSlashBackslash = { '/', '\\' };
        internal static readonly char[] WildcardChars = { '*', '?' };
        internal static readonly string[] CharactersForExpansion = { "*", "?", "$(", "@(", "%" };
        internal static readonly char[] CommaChar = { ',' };
        internal static readonly char[] HyphenChar = { '-' };
        internal static readonly char[] DirectorySeparatorChar = { Path.DirectorySeparatorChar };
        internal static readonly char[] DotChar = { '.' };
        internal static readonly string[] EnvironmentNewLine = { Environment.NewLine };
        internal static readonly char[] PipeChar = { '|' };
        internal static readonly char[] PathSeparatorChar = { Path.PathSeparator };
    }

    internal static class PropertyNames
    {
        /// <summary>
        /// Specifies whether the current evaluation / build is happening during a graph build
        /// </summary>
        internal const string IsGraphBuild = nameof(IsGraphBuild);

        internal const string InnerBuildProperty = nameof(InnerBuildProperty);
        internal const string InnerBuildPropertyValues = nameof(InnerBuildPropertyValues);
        internal const string TargetFrameworks = nameof(TargetFrameworks);
        internal const string TargetFramework = nameof(TargetFramework);
        internal const string UsingMicrosoftNETSdk = nameof(UsingMicrosoftNETSdk);
    }

    // TODO: Remove these when VS gets updated to setup project cache plugins.
    internal static class DesignTimeProperties
    {
        internal const string DesignTimeBuild = nameof(DesignTimeBuild);
        internal const string BuildingProject = nameof(BuildingProject);
    }

    internal static class ItemTypeNames
    {
        /// <summary>
        /// References to other msbuild projects
        /// </summary>
        internal const string ProjectReference = nameof(ProjectReference);

        /// <summary>
        /// Statically specifies what targets a project calls on its references
        /// </summary>
        internal const string ProjectReferenceTargets = nameof(ProjectReferenceTargets);

        internal const string GraphIsolationExemptReference = nameof(GraphIsolationExemptReference);

        /// <summary>
        /// Declares a project cache plugin and its configuration.
        /// </summary>
        internal const string ProjectCachePlugin = nameof(ProjectCachePlugin);

        /// <summary>
        /// Embed specified files in the binary log
        /// </summary>
        internal const string EmbedInBinlog = nameof(EmbedInBinlog);
    }

    /// <summary>
    /// Constants naming well-known item metadata.
    /// </summary>
    internal static class ItemMetadataNames
    {
        internal const string fusionName = "FusionName";
        internal const string hintPath = "HintPath";
        internal const string assemblyFolderKey = "AssemblyFolderKey";
        internal const string alias = "Alias";
        internal const string aliases = "Aliases";
        internal const string parentFile = "ParentFile";
        internal const string privateMetadata = "Private";
        internal const string copyLocal = "CopyLocal";
        internal const string isRedistRoot = "IsRedistRoot";
        internal const string redist = "Redist";
        internal const string resolvedFrom = "ResolvedFrom";
        internal const string destinationSubDirectory = "DestinationSubDirectory";
        internal const string destinationSubPath = "DestinationSubPath";
        internal const string specificVersion = "SpecificVersion";
        internal const string link = "Link";
        internal const string subType = "SubType";
        internal const string executableExtension = "ExecutableExtension";
        internal const string embedInteropTypes = "EmbedInteropTypes";
        internal const string frameworkReferenceName = "FrameworkReferenceName";
        internal const string assemblyName = "AssemblyName";
        internal const string assemblyVersion = "AssemblyVersion";
        internal const string publicKeyToken = "PublicKeyToken";
        internal const string culture = "Culture";
        internal const string withCulture = "WithCulture";
        internal const string copyToOutputDirectory = "CopyToOutputDirectory";
        internal const string copyAlways = "Always";
        internal const string managed = "Managed";

        /// <summary>
        /// The output path for a given item.
        /// </summary>
        internal const string targetPath = "TargetPath";
        internal const string dependentUpon = "DependentUpon";
        internal const string msbuildSourceProjectFile = "MSBuildSourceProjectFile";
        internal const string msbuildSourceTargetName = "MSBuildSourceTargetName";
        internal const string isPrimary = "IsPrimary";
        internal const string targetFramework = "RequiredTargetFramework";
        internal const string frameworkDirectory = "FrameworkDirectory";
        internal const string version = "Version";
        internal const string imageRuntime = "ImageRuntime";
        internal const string winMDFile = "WinMDFile";
        internal const string winMDFileType = "WinMDFileType";
        internal const string msbuildReferenceSourceTarget = "ReferenceSourceTarget";
        internal const string msbuildReferenceGrouping = "ReferenceGrouping";
        internal const string msbuildReferenceGroupingDisplayName = "ReferenceGroupingDisplayName";
        internal const string msbuildReferenceFromSDK = "ReferenceFromSDK";
        internal const string winmdImplmentationFile = "Implementation";
        internal const string projectReferenceOriginalItemSpec = "ProjectReferenceOriginalItemSpec";
        internal const string IgnoreVersionForFrameworkReference = "IgnoreVersionForFrameworkReference";
        internal const string frameworkFile = "FrameworkFile";
        internal const string ProjectReferenceTargetsMetadataName = "Targets";
        internal const string PropertiesMetadataName = "Properties";
        internal const string UndefinePropertiesMetadataName = "UndefineProperties";
        internal const string AdditionalPropertiesMetadataName = "AdditionalProperties";
        internal const string ProjectConfigurationDescription = "ProjectConfigurationDescription";
    }

    /// <summary>
    /// Constants naming well-known items.
    /// </summary>
    internal static class ItemNames
    {
        internal const string Compile = "Compile";
        internal const string Content = "Content";
        internal const string EmbeddedResource = "EmbeddedResource";
        internal const string None = "None";
        internal const string Reference = "Reference";
        internal const string ProjectCapability = "ProjectCapability";
    }
}
