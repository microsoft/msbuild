// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Shared;
using Microsoft.Build.Collections;
using Microsoft.Build.Execution;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared.FileSystem;
using error = Microsoft.Build.Shared.ErrorUtilities;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using InvalidToolsetDefinitionException = Microsoft.Build.Exceptions.InvalidToolsetDefinitionException;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// The abstract base class for all Toolset readers.
    /// </summary>
    internal abstract class ToolsetReader
    {
        /// <summary>
        /// The global properties used to read the toolset.
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _globalProperties;

        /// <summary>
        /// The environment properties used to read the toolset.
        /// </summary>
        private readonly PropertyDictionary<ProjectPropertyInstance> _environmentProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        protected ToolsetReader(
            PropertyDictionary<ProjectPropertyInstance> environmentProperties,
            PropertyDictionary<ProjectPropertyInstance> globalProperties)
        {
            _environmentProperties = environmentProperties;
            _globalProperties = globalProperties;
        }

        /// <summary>
        /// Returns the list of tools versions
        /// </summary>
        protected abstract IEnumerable<ToolsetPropertyDefinition> ToolsVersions
        {
            get;
        }

        /// <summary>
        /// Returns the default tools version, or null if none was specified
        /// </summary>
        protected abstract string DefaultToolsVersion
        {
            get;
        }

        /// <summary>
        /// Returns the path to find override tasks, or null if none was specified
        /// </summary>
        protected abstract string MSBuildOverrideTasksPath
        {
            get;
        }

        /// <summary>
        /// ToolsVersion to use as the default ToolsVersion for this version of MSBuild
        /// </summary>
        protected abstract string DefaultOverrideToolsVersion
        {
            get;
        }

        /// <summary>
        /// Gathers toolset data from the registry and configuration file, if any:
        /// allows you to specify which of the registry and configuration file to
        /// read from by providing ToolsetInitialization
        /// </summary>
        internal static string ReadAllToolsets(Dictionary<string, Toolset> toolsets, PropertyDictionary<ProjectPropertyInstance> environmentProperties, PropertyDictionary<ProjectPropertyInstance> globalProperties, ToolsetDefinitionLocations locations)
        {
            return ReadAllToolsets(toolsets,
#if FEATURE_WIN32_REGISTRY
                null,
#endif
                null,
                environmentProperties, globalProperties, locations);
        }

        /// <summary>
        /// Gathers toolset data from the registry and configuration file, if any.
        /// NOTE:  this method is internal for unit testing purposes only.
        /// </summary>
        internal static string ReadAllToolsets
            (
            Dictionary<string, Toolset> toolsets,
#if FEATURE_WIN32_REGISTRY
            ToolsetRegistryReader registryReader,
#endif
            ToolsetConfigurationReader configurationReader,
            PropertyDictionary<ProjectPropertyInstance> environmentProperties,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            ToolsetDefinitionLocations locations
            )
        {
            var initialProperties =
                new PropertyDictionary<ProjectPropertyInstance>(environmentProperties);

            initialProperties.ImportProperties(globalProperties);

            // The ordering here is important because the configuration file should have greater precedence
            // than the registry, and we do a check and don't read in the new toolset if there's already one. 
            string defaultToolsVersionFromConfiguration = null;
            string overrideTasksPathFromConfiguration = null;
            string defaultOverrideToolsVersionFromConfiguration = null;

            if ((locations & ToolsetDefinitionLocations.ConfigurationFile) == ToolsetDefinitionLocations.ConfigurationFile)
            {
                if (configurationReader == null)
                {
                    configurationReader = new ToolsetConfigurationReader(environmentProperties, globalProperties);
                }

                // Accumulation of properties is okay in the config file because it's deterministically ordered
                defaultToolsVersionFromConfiguration = configurationReader.ReadToolsets(toolsets, globalProperties,
                    initialProperties, true /* accumulate properties */, out overrideTasksPathFromConfiguration,
                    out defaultOverrideToolsVersionFromConfiguration);
            }

            string defaultToolsVersionFromRegistry = null;
            string overrideTasksPathFromRegistry = null;
            string defaultOverrideToolsVersionFromRegistry = null;

            if ((locations & ToolsetDefinitionLocations.Registry) == ToolsetDefinitionLocations.Registry)
            {
#if FEATURE_WIN32_REGISTRY
                if (NativeMethodsShared.IsWindows || registryReader != null)
                {
                    // If we haven't been provided a registry reader (i.e. unit tests), create one
                    registryReader ??= new ToolsetRegistryReader(environmentProperties, globalProperties);

                    // We do not accumulate properties when reading them from the registry, because the order
                    // in which values are returned to us is essentially random: so we disallow one property
                    // in the registry to refer to another also in the registry
                    defaultToolsVersionFromRegistry = registryReader.ReadToolsets(toolsets, globalProperties,
                        initialProperties, false /* do not accumulate properties */, out overrideTasksPathFromRegistry,
                        out defaultOverrideToolsVersionFromRegistry);
                }
                else
#endif
                {
                    var currentDir = BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory.TrimEnd(Path.DirectorySeparatorChar);
                    var props = new PropertyDictionary<ProjectPropertyInstance>();

                    var libraryPath = NativeMethodsShared.FrameworkBasePath;

                    if (!string.IsNullOrEmpty(libraryPath))
                    {
                        // The 4.0 toolset is installed in the framework directory
                        var v4Dir =
                            FrameworkLocationHelper.GetPathToDotNetFrameworkV40(DotNetFrameworkArchitecture.Current);
                        if (v4Dir != null && !toolsets.ContainsKey("4.0"))
                        {
                            // Create standard properties. On Mono they are well known
                            var buildProperties =
                                CreateStandardProperties(globalProperties, "4.0", libraryPath, v4Dir);

                            toolsets.Add(
                                "4.0",
                                new Toolset(
                                    "4.0",
                                    v4Dir,
                                    buildProperties,
                                    environmentProperties,
                                    globalProperties,
                                    null,
                                    currentDir,
                                    string.Empty));
                        }

                        // Other toolsets are installed in the xbuild directory
                        var xbuildToolsetsDir = Path.Combine(libraryPath, $"xbuild{Path.DirectorySeparatorChar}");
                        if (FileSystems.Default.DirectoryExists(xbuildToolsetsDir))
                        {
                            var r = new Regex(Regex.Escape(xbuildToolsetsDir) + @"\d+\.\d+");
                            foreach (var d in Directory.GetDirectories(xbuildToolsetsDir).Where(d => r.IsMatch(d)))
                            {
                                var version = Path.GetFileName(d);
                                var binPath = Path.Combine(d, "bin");
                                if (toolsets.ContainsKey(version))
                                {
                                    continue;
                                }

                                if (NativeMethodsShared.IsMono && Version.TryParse(version, out Version parsedVersion) && parsedVersion.Major > 14)
                                {
                                    continue;
                                }

                                // Create standard properties. On Mono they are well known
                                var buildProperties =
                                    CreateStandardProperties(globalProperties, version, xbuildToolsetsDir, binPath);

                                toolsets.Add(
                                    version,
                                    new Toolset(
                                        version,
                                        binPath,
                                        buildProperties,
                                        environmentProperties,
                                        globalProperties,
                                        null,
                                        currentDir,
                                        string.Empty));
                            }
                        }
                    }
                    if (!toolsets.ContainsKey(MSBuildConstants.CurrentToolsVersion))
                    {
                        toolsets.Add(
                            MSBuildConstants.CurrentToolsVersion,
                            new Toolset(
                                MSBuildConstants.CurrentToolsVersion,
                                currentDir,
                                props,
                                new PropertyDictionary<ProjectPropertyInstance>(),
                                currentDir,
                                string.Empty));
                    }
                }
            }

            // The 2.0 .NET Framework installer did not write a ToolsVersion key for itself in the registry. 
            // The 3.5 installer writes one for 2.0, but 3.5 might not be installed.  
            // The 4.0 and subsequent installers can't keep writing the 2.0 one, because (a) it causes SxS issues and (b) we 
            // don't want it unless 2.0 is installed.
            // So if the 2.0 framework is actually installed, we're reading the registry, and either the registry or the config
            // file have not already created the 2.0 toolset, mock up a fake one.  
            if (((locations & ToolsetDefinitionLocations.Registry) != 0) && !toolsets.ContainsKey("2.0")
                && FrameworkLocationHelper.PathToDotNetFrameworkV20 != null)
            {
                var synthetic20Toolset = new Toolset(
                    "2.0",
                    FrameworkLocationHelper.PathToDotNetFrameworkV20,
                    environmentProperties,
                    globalProperties,
                    null /* 2.0 did not have override tasks */,
                    null /* 2.0 did not have a default override toolsversion */);
                toolsets.Add("2.0", synthetic20Toolset);
            }

            string defaultToolsVersionFromLocal = null;
            string overrideTasksPathFromLocal = null;
            string defaultOverrideToolsVersionFromLocal = null;

            if ((locations & ToolsetDefinitionLocations.Local) == ToolsetDefinitionLocations.Local)
            {
                var localReader = new ToolsetLocalReader(environmentProperties, globalProperties);

                defaultToolsVersionFromLocal = localReader.ReadToolsets(
                        toolsets,
                        globalProperties,
                        initialProperties,
                        false /* accumulate properties */,
                        out overrideTasksPathFromLocal,
                        out defaultOverrideToolsVersionFromLocal);
            }

            // We'll use the path from the configuration file if it was specified, otherwise we'll try
            // the one from the registry.  It's possible (and valid) that neither the configuration file
            // nor the registry specify a override in which case we'll just return null.
            var overrideTasksPath = overrideTasksPathFromConfiguration ?? overrideTasksPathFromRegistry ?? overrideTasksPathFromLocal;

            // We'll use the path from the configuration file if it was specified, otherwise we'll try
            // the one from the registry.  It's possible (and valid) that neither the configuration file
            // nor the registry specify a override in which case we'll just return null.
            var defaultOverrideToolsVersion = defaultOverrideToolsVersionFromConfiguration
                                                 ?? defaultOverrideToolsVersionFromRegistry
                                                 ?? defaultOverrideToolsVersionFromLocal;

            // We'll use the default from the configuration file if it was specified, otherwise we'll try
            // the one from the registry.  It's possible (and valid) that neither the configuration file
            // nor the registry specify a default, in which case we'll just return null.
            var defaultToolsVersion = defaultToolsVersionFromConfiguration ?? defaultToolsVersionFromRegistry ?? defaultToolsVersionFromLocal;

            // If we got a default version from the registry or config file, and it
            // actually exists, fine.
            // Otherwise we have to come up with one.
            if (defaultToolsVersion != null && toolsets.ContainsKey(defaultToolsVersion))
            {
                return defaultToolsVersion;
            }
            // We're going to choose a hard coded default tools version of 2.0.
            defaultToolsVersion = Constants.defaultToolsVersion;

            // But don't overwrite any existing tools path for this default we're choosing.
            if (toolsets.ContainsKey(Constants.defaultToolsVersion))
            {
                return defaultToolsVersion;
            }
            // There's no tools path already for 2.0, so use the path to the v2.0 .NET Framework.
            // If an old-fashioned caller sets BinPath property, or passed a BinPath to the constructor,
            // that will overwrite what we're setting here.
            ErrorUtilities.VerifyThrow(
                Constants.defaultToolsVersion == "2.0",
                "Getting 2.0 FX path so default should be 2.0");
            var pathToFramework = FrameworkLocationHelper.PathToDotNetFrameworkV20;

            // We could not find the default toolsversion because it was not installed on the machine. Fallback to the
            // one we expect to always be there when running msbuild 4.0.
            if (pathToFramework == null)
            {
                pathToFramework = FrameworkLocationHelper.PathToDotNetFrameworkV40;
                defaultToolsVersion = Constants.defaultFallbackToolsVersion;
            }

            // Again don't overwrite any existing tools path for this default we're choosing.
            if (toolsets.ContainsKey(defaultToolsVersion))
            {
                return defaultToolsVersion;
            }
            var defaultToolset = new Toolset(
                defaultToolsVersion,
                pathToFramework,
                environmentProperties,
                globalProperties,
                overrideTasksPath,
                defaultOverrideToolsVersion);
            toolsets.Add(defaultToolsVersion, defaultToolset);

            return defaultToolsVersion;
        }

        /// <summary>
        /// Populates the toolset collection passed in with the toolsets read from some location.
        /// </summary>
        /// <remarks>Internal for unit testing only</remarks>
        /// <returns>the default tools version if available, or null otherwise</returns>
        internal string ReadToolsets
            (
            Dictionary<string, Toolset> toolsets,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            PropertyDictionary<ProjectPropertyInstance> initialProperties,
            bool accumulateProperties,
            out string msBuildOverrideTasksPath,
            out string defaultOverrideToolsVersion
            )
        {
            error.VerifyThrowArgumentNull(toolsets, "Toolsets");

            ReadEachToolset(toolsets, globalProperties, initialProperties, accumulateProperties);

            string defaultToolsVersion = DefaultToolsVersion;

            msBuildOverrideTasksPath = MSBuildOverrideTasksPath;

            defaultOverrideToolsVersion = DefaultOverrideToolsVersion;

            // We don't check whether the default tools version actually
            // corresponds to a toolset definition. That's because our default for
            // the indefinite future is 2.0, and 2.0 might not be installed, which is fine.
            // If a project tries to use 2.0 (or whatever the default is) in these circumstances
            // they'll get a nice error saying that toolset isn't available and listing those that are.
            return defaultToolsVersion;
        }

        /// <summary>
        /// Provides an enumerator over property definitions for a specified tools version
        /// </summary>
        /// <param name="toolsVersion">The tools version.</param>
        /// <returns>An enumeration of property definitions.</returns>
        protected abstract IEnumerable<ToolsetPropertyDefinition> GetPropertyDefinitions(string toolsVersion);

        /// <summary>
        /// Provides an enumerator over the set of sub-toolset names available to a particular
        /// toolsversion
        /// </summary>
        /// <param name="toolsVersion">The tools version.</param>
        /// <returns>An enumeration of the sub-toolsets that belong to that toolsversion.</returns>
        protected abstract IEnumerable<string> GetSubToolsetVersions(string toolsVersion);

        /// <summary>
        /// Provides an enumerator over property definitions for a specified sub-toolset version 
        /// under a specified toolset version. 
        /// </summary>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="subToolsetVersion">The sub-toolset version.</param>
        /// <returns>An enumeration of property definitions.</returns>
        protected abstract IEnumerable<ToolsetPropertyDefinition> GetSubToolsetPropertyDefinitions(string toolsVersion, string subToolsetVersion);

        /// <summary>
        /// Returns a map of MSBuildExtensionsPath* property names/kind to list of search paths
        /// </summary>
        protected abstract Dictionary<string, ProjectImportPathMatch> GetProjectImportSearchPathsTable(string toolsVersion, string os);

        /// <summary>
        /// Reads all the toolsets and populates the given ToolsetCollection with them
        /// </summary>
        private void ReadEachToolset
            (
            Dictionary<string, Toolset> toolsets,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            PropertyDictionary<ProjectPropertyInstance> initialProperties,
            bool accumulateProperties
            )
        {
            foreach (ToolsetPropertyDefinition toolsVersion in ToolsVersions)
            {
                // If there's already an existing toolset, it's of higher precedence, so
                // don't even bother to read this toolset in.  
                if (!toolsets.ContainsKey(toolsVersion.Name))
                {
                    // We clone here because we don't want to interfere with the evaluation 
                    // of subsequent Toolsets; otherwise, properties found during the evaluation
                    // of this Toolset would be persisted in initialProperties and appear
                    // to later Toolsets as Global or Environment properties from the Engine.
                    PropertyDictionary<ProjectPropertyInstance> initialPropertiesClone = new PropertyDictionary<ProjectPropertyInstance>(initialProperties);

                    Toolset toolset = ReadToolset(toolsVersion, globalProperties, initialPropertiesClone, accumulateProperties);

                    if (toolset != null)
                    {
                        toolsets[toolset.ToolsVersion] = toolset;
                    }
                }
            }
        }

        /// <summary>
        /// Reads the settings for a specified tools version
        /// </summary>
        private Toolset ReadToolset
            (
            ToolsetPropertyDefinition toolsVersion,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            PropertyDictionary<ProjectPropertyInstance> initialProperties,
            bool accumulateProperties
            )
        {
            // Initial properties is the set of properties we're going to use to expand property expressions like $(foo)
            // in the values we read out of the registry or config file. We'll add to it as we pick up properties (including binpath)
            // from the registry or config file, so that properties there can be referenced in values below them.
            // After processing all the properties, we don't need initialProperties anymore.
            string toolsPath = null;
            string binPath = null;
            PropertyDictionary<ProjectPropertyInstance> properties = new PropertyDictionary<ProjectPropertyInstance>();

            IEnumerable<ToolsetPropertyDefinition> rawProperties = GetPropertyDefinitions(toolsVersion.Name);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(initialProperties);

            foreach (ToolsetPropertyDefinition property in rawProperties)
            {
                EvaluateAndSetProperty(property, properties, globalProperties, initialProperties, accumulateProperties, ref toolsPath, ref binPath, ref expander);
            }

            Dictionary<string, SubToolset> subToolsets = new Dictionary<string, SubToolset>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> subToolsetVersions = GetSubToolsetVersions(toolsVersion.Name);

            foreach (string subToolsetVersion in subToolsetVersions)
            {
                string subToolsetToolsPath = null;
                string subToolsetBinPath = null;
                IEnumerable<ToolsetPropertyDefinition> rawSubToolsetProperties = GetSubToolsetPropertyDefinitions(toolsVersion.Name, subToolsetVersion);
                PropertyDictionary<ProjectPropertyInstance> subToolsetProperties = new PropertyDictionary<ProjectPropertyInstance>();

                // If we have a sub-toolset, any values defined here will override the toolset properties. 
                foreach (ToolsetPropertyDefinition property in rawSubToolsetProperties)
                {
                    EvaluateAndSetProperty(property, subToolsetProperties, globalProperties, initialProperties, false /* do not ever accumulate sub-toolset properties */, ref subToolsetToolsPath, ref subToolsetBinPath, ref expander);
                }

                if (subToolsetToolsPath != null || subToolsetBinPath != null)
                {
                    InvalidToolsetDefinitionException.Throw("MSBuildToolsPathNotSupportedInSubToolsets", toolsVersion.Name, toolsVersion.Source.LocationString, subToolsetVersion);
                }

                subToolsets[subToolsetVersion] = new SubToolset(subToolsetVersion, subToolsetProperties);
            }

            // All tools versions must specify a value for MSBuildToolsPath (or MSBuildBinPath)
            if (String.IsNullOrEmpty(toolsPath) && String.IsNullOrEmpty(binPath))
            {
                return null;
            }

            // If both MSBuildBinPath and MSBuildToolsPath are present, they must be the same
            if (toolsPath != null && binPath != null && !toolsPath.Equals(binPath, StringComparison.OrdinalIgnoreCase))
            {
                InvalidToolsetDefinitionException.Throw("ConflictingValuesOfMSBuildToolsPath", toolsVersion.Name, toolsVersion.Source.LocationString);
            }

            AppendStandardProperties(properties, globalProperties, toolsVersion.Name, null, toolsPath);
            Toolset toolset = null;

            try
            {
                var importSearchPathsTable = GetProjectImportSearchPathsTable(toolsVersion.Name, NativeMethodsShared.GetOSNameForExtensionsPath());
                toolset = new Toolset(toolsVersion.Name, toolsPath ?? binPath, properties, _environmentProperties, globalProperties, subToolsets, MSBuildOverrideTasksPath, DefaultOverrideToolsVersion, importSearchPathsTable);
            }
            catch (ArgumentException e)
            {
                InvalidToolsetDefinitionException.Throw("ErrorCreatingToolset", toolsVersion.Name, e.Message);
            }

            return toolset;
        }

        /// <summary>
        /// Create a dictionary with standard properties.
        /// </summary>
        private static PropertyDictionary<ProjectPropertyInstance> CreateStandardProperties(
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            string version,
            string root,
            string toolsPath)
        {
            // Create standard properties. On Mono they are well known
            if (!NativeMethodsShared.IsMono)
            {
                return null;
            }
            PropertyDictionary<ProjectPropertyInstance> buildProperties =
                new PropertyDictionary<ProjectPropertyInstance>();
            AppendStandardProperties(buildProperties, globalProperties, version, root, toolsPath);
            return buildProperties;
        }

        /// <summary>
        /// Appends standard properties to a dictionary. These properties are read from
        /// the registry under Windows (they are a part of a toolset definition).
        /// </summary>
        private static void AppendStandardProperties(
            PropertyDictionary<ProjectPropertyInstance> properties,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            string version,
            string root,
            string toolsPath)
        {
            if (NativeMethodsShared.IsMono)
            {
                var v4Dir = FrameworkLocationHelper.GetPathToDotNetFrameworkV40(DotNetFrameworkArchitecture.Current)
                            + Path.DirectorySeparatorChar;
                var v35Dir = FrameworkLocationHelper.GetPathToDotNetFrameworkV35(DotNetFrameworkArchitecture.Current)
                             + Path.DirectorySeparatorChar;

                if (root == null)
                {
                    var libraryPath = NativeMethodsShared.FrameworkBasePath;
                    if (toolsPath.StartsWith(libraryPath))
                    {
                        root = Path.GetDirectoryName(toolsPath);
                        if (toolsPath.EndsWith("bin"))
                        {
                            root = Path.GetDirectoryName(root);
                        }
                    }
                    else
                    {
                        root = libraryPath;
                    }
                }

                root += Path.DirectorySeparatorChar;

                // Global properties cannot be overwritten
                if (globalProperties["FrameworkSDKRoot"] == null && properties["FrameworkSDKRoot"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("FrameworkSDKRoot", root, true, false));
                }
                if (globalProperties["MSBuildToolsRoot"] == null && properties["MSBuildToolsRoot"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("MSBuildToolsRoot", root, true, false));
                }
                if (globalProperties["MSBuildFrameworkToolsPath"] == null
                    && properties["MSBuildFrameworkToolsPath"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("MSBuildFrameworkToolsPath", toolsPath, true, false));
                }
                if (globalProperties["MSBuildFrameworkToolsPath32"] == null
                    && properties["MSBuildFrameworkToolsPath32"] == null)
                {
                    properties.Set(
                        ProjectPropertyInstance.Create("MSBuildFrameworkToolsPath32", toolsPath, true, false));
                }
                if (globalProperties["MSBuildRuntimeVersion"] == null && properties["MSBuildRuntimeVersion"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("MSBuildRuntimeVersion", version, true, false));
                }
                if (!string.IsNullOrEmpty(v35Dir) && globalProperties["SDK35ToolsPath"] == null
                    && properties["SDK35ToolsPath"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("SDK35ToolsPath", v35Dir, true, false));
                }
                if (!string.IsNullOrEmpty(v4Dir) && globalProperties["SDK40ToolsPath"] == null
                    && properties["SDK40ToolsPath"] == null)
                {
                    properties.Set(ProjectPropertyInstance.Create("SDK40ToolsPath", v4Dir, true, false));
                }
            }
        }

        /// <summary>
        /// Processes a particular ToolsetPropertyDefinition into the correct value and location in the initial and/or final property set. 
        /// </summary>
        /// <param name="property">The ToolsetPropertyDefinition being analyzed.</param>
        /// <param name="properties">The final set of properties that we wish this toolset property to be added to. </param>
        /// <param name="globalProperties">The global properties, used for expansion and to make sure none are overridden.</param>
        /// <param name="initialProperties">The initial properties, used for expansion and added to if "accumulateProperties" is true.</param>
        /// <param name="accumulateProperties">If "true", we add this property to the initialProperties dictionary, as well, so that properties later in the toolset can use this value.</param>
        /// <param name="toolsPath">If this toolset property is the "MSBuildToolsPath" property, we will return the value in this parameter.</param>
        /// <param name="binPath">If this toolset property is the "MSBuildBinPath" property, we will return the value in this parameter.</param>
        /// <param name="expander">The expander used to expand the value of the properties.  Ref because if we are accumulating the properties, we need to re-create the expander to account for the new property value.</param>
        private void EvaluateAndSetProperty(ToolsetPropertyDefinition property, PropertyDictionary<ProjectPropertyInstance> properties, PropertyDictionary<ProjectPropertyInstance> globalProperties, PropertyDictionary<ProjectPropertyInstance> initialProperties, bool accumulateProperties, ref string toolsPath, ref string binPath, ref Expander<ProjectPropertyInstance, ProjectItemInstance> expander)
        {
            if (String.Equals(property.Name, ReservedPropertyNames.toolsPath, StringComparison.OrdinalIgnoreCase))
            {
                toolsPath = ExpandPropertyUnescaped(property, expander);
                toolsPath = ExpandRelativePathsRelativeToExeLocation(toolsPath);

                if (accumulateProperties)
                {
                    SetProperty
                    (
                        new ToolsetPropertyDefinition(ReservedPropertyNames.toolsPath, toolsPath, property.Source),
                        initialProperties,
                        globalProperties
                    );
                }
            }
            else if (String.Equals(property.Name, ReservedPropertyNames.binPath, StringComparison.OrdinalIgnoreCase))
            {
                binPath = ExpandPropertyUnescaped(property, expander);
                binPath = ExpandRelativePathsRelativeToExeLocation(binPath);

                if (accumulateProperties)
                {
                    SetProperty
                    (
                        new ToolsetPropertyDefinition(ReservedPropertyNames.binPath, binPath, property.Source),
                        initialProperties,
                        globalProperties
                    );
                }
            }
            else if (ReservedPropertyNames.IsReservedProperty(property.Name))
            {
                // We don't allow toolsets to define reserved properties
                string baseMessage = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("CannotModifyReservedProperty", property.Name);
                InvalidToolsetDefinitionException.Throw("InvalidPropertyNameInToolset", property.Name, property.Source.LocationString, baseMessage);
            }
            else
            {
                // It's an arbitrary property
                property.Value = ExpandPropertyUnescaped(property, expander);

                SetProperty(property, properties, globalProperties);

                if (accumulateProperties)
                {
                    SetProperty(property, initialProperties, globalProperties);
                }
            }

            if (accumulateProperties)
            {
                expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(initialProperties);
            }
        }

        /// <summary>
        /// Expands the given unexpanded property expression using the properties in the
        /// given expander.
        /// </summary>
        private string ExpandPropertyUnescaped(ToolsetPropertyDefinition property, Expander<ProjectPropertyInstance, ProjectItemInstance> expander)
        {
            try
            {
                return expander.ExpandIntoStringAndUnescape(property.Value, ExpanderOptions.ExpandProperties, property.Source);
            }
            catch (InvalidProjectFileException ex)
            {
                InvalidToolsetDefinitionException.Throw(ex, "ErrorEvaluatingToolsetPropertyExpression", property.Value, property.Source.LocationString, ex.BaseMessage);
            }

            return string.Empty;
        }

        /// <summary>
        /// Sets the given property in the given property group.
        /// </summary>
        private void SetProperty(ToolsetPropertyDefinition property, PropertyDictionary<ProjectPropertyInstance> propertyGroup, PropertyDictionary<ProjectPropertyInstance> globalProperties)
        {
            try
            {
                // Global properties cannot be overwritten
                if (globalProperties[property.Name] == null)
                {
                    propertyGroup.Set(ProjectPropertyInstance.Create(property.Name, EscapingUtilities.UnescapeAll(property.Value), true /* may be reserved */, false /* not immutable */));
                }
            }
            catch (ArgumentException ex)
            {
                InvalidToolsetDefinitionException.Throw(ex, "InvalidPropertyNameInToolset", property.Name, property.Source.LocationString, ex.Message);
            }
        }

        /// <summary>
        /// Given a path, de-relativizes it using the location of the currently
        /// executing .exe as the base directory. For example, the path "..\foo"
        /// becomes "c:\windows\microsoft.net\framework\foo" if the current exe is
        /// "c:\windows\microsoft.net\framework\v3.5.1234\msbuild.exe".
        /// If the path is not relative, it is returned without modification.
        /// If the path is invalid, it is returned without modification.
        /// </summary>
        private string ExpandRelativePathsRelativeToExeLocation(string path)
        {
            try
            {
                // Trim, because we don't want to do anything with empty values
                // (those should cause an error)
                string trimmedValue = path.Trim();
                if (trimmedValue.Length > 0 && !Path.IsPathRooted(trimmedValue))
                {
                    path = Path.GetFullPath(
                        Path.Combine(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory, trimmedValue));
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                // This means that the path looked relative, but was an invalid path. In this case, we'll
                // just not expand it, and carry on - to be consistent with what happens when there's a
                // non-relative bin path with invalid characters. The problem will be detected later when
                // it's used in a project file.
            }

            return path;
        }
    }

    /// <summary>
    /// struct representing a reference to MSBuildExtensionsPath* property
    /// </summary>
    internal struct MSBuildExtensionsPathReferenceKind
    {
        /// <summary>
        /// MSBuildExtensionsPathReferenceKind instance for property named "MSBuildExtensionsPath"
        /// </summary>
        public static readonly MSBuildExtensionsPathReferenceKind Default = new MSBuildExtensionsPathReferenceKind("MSBuildExtensionsPath");

        /// <summary>
        /// MSBuildExtensionsPathReferenceKind instance for property named "MSBuildExtensionsPath32"
        /// </summary>
        public static readonly MSBuildExtensionsPathReferenceKind Path32 = new MSBuildExtensionsPathReferenceKind("MSBuildExtensionsPath32");

        /// <summary>
        /// MSBuildExtensionsPathReferenceKind instance for property named "MSBuildExtensionsPath64"
        /// </summary>
        public static readonly MSBuildExtensionsPathReferenceKind Path64 = new MSBuildExtensionsPathReferenceKind("MSBuildExtensionsPath64");

        /// <summary>
        /// MSBuildExtensionsPathReferenceKind instance representing no MSBuildExtensionsPath* property reference
        /// </summary>
        public static readonly MSBuildExtensionsPathReferenceKind None = new MSBuildExtensionsPathReferenceKind(String.Empty);

        private MSBuildExtensionsPathReferenceKind(string value)
        {
            StringRepresentation = value;
        }

        /// <summary>
        /// String representation of the property reference - eg. "MSBuildExtensionsPath32"
        /// </summary>
        public string StringRepresentation { get; private set; }

        /// <summary>
        /// Returns the corresponding property name - eg. "$(MSBuildExtensionsPath32)"
        /// </summary>
        public string MSBuildPropertyName => String.Format($"$({StringRepresentation})");

        /// <summary>
        /// Tries to find a reference to MSBuildExtensionsPath* property in the given string
        /// </summary>
        public static MSBuildExtensionsPathReferenceKind FindIn(string expression)
        {
            if (expression.IndexOf("$(MSBuildExtensionsPath)") >= 0)
            {
                return MSBuildExtensionsPathReferenceKind.Default;
            }

            if (expression.IndexOf("$(MSBuildExtensionsPath32)") >= 0)
            {
                return MSBuildExtensionsPathReferenceKind.Path32;
            }

            if (expression.IndexOf("$(MSBuildExtensionsPath64)") >= 0)
            {
                return MSBuildExtensionsPathReferenceKind.Path64;
            }

            return MSBuildExtensionsPathReferenceKind.None;
        }
     }
}
