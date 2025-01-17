// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
#if FEATURE_WIN32_REGISTRY
using Microsoft.Win32;
#endif
using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;
using ObjectModel = System.Collections.ObjectModel;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Delegate for loading an Xml file, for unit testing.
    /// </summary>
    /// <param name="path">The path to load.</param>
    /// <returns>An Xml document.</returns>
    internal delegate XmlDocumentWithLocation LoadXmlFromPath(string path);

    /// <summary>
    /// Aggregation of a toolset version (eg. "2.0"), tools path, and optional set of associated properties.
    /// Toolset is immutable.
    /// </summary>
    // UNDONE: Review immutability. If this is not immutable, add a mechanism to notify the project collection/s owning it to increment their toolsetVersion.
    [DebuggerDisplay("ToolsVersion={ToolsVersion} ToolsPath={ToolsPath} #Properties={_properties.Count}")]
    public class Toolset : ITranslatable
    {
        /// <summary>
        /// these files list all default tasks and task assemblies that do not need to be explicitly declared by projects
        /// </summary>
        private const string DefaultTasksFilePattern = "*.tasks";

        /// <summary>
        /// these files list all Override tasks and task assemblies that do not need to be explicitly declared by projects
        /// </summary>
        private const string OverrideTasksFilePattern = "*.overridetasks";

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Regkey that we check to see whether Dev10 is installed.  This should exist if any SKU of Dev10 is installed,
        /// but is not removed even when the last version of Dev10 is uninstalled, due to 10.0\bsln sticking around.
        /// </summary>
        private const string Dev10OverallInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vs\Servicing\10.0";

        /// <summary>
        /// Regkey that we check to see whether Dev10 Ultimate is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10UltimateInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vs\Servicing\10.0\vstscore";

        /// <summary>
        /// Regkey that we check to see whether Dev10 Premium is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10PremiumInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vs\Servicing\10.0\vstdcore";

        /// <summary>
        /// Regkey that we check to see whether Dev10 Professional is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10ProfessionalInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vs\Servicing\10.0\procore";

        /// <summary>
        /// Regkey that we check to see whether C# Express 2010 is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10VCSExpressInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vcs\Servicing\10.0\xcor";

        /// <summary>
        /// Regkey that we check to see whether VB Express 2010 is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10VBExpressInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vb\Servicing\10.0\xcor";

        /// <summary>
        /// Regkey that we check to see whether VC Express 2010 is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10VCExpressInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vc\Servicing\10.0\xcor";

        /// <summary>
        /// Regkey that we check to see whether VWD Express 2010 is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10VWDExpressInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vns\Servicing\10.0\xcor";

        /// <summary>
        /// Regkey that we check to see whether LightSwitch 2010 is installed.  This will exist if it is installed, and be
        /// properly removed after it has been uninstalled.
        /// </summary>
        private const string Dev10LightSwitchInstallKeyRegistryPath = @"Software\Microsoft\DevDiv\vs\Servicing\10.0\vslscore";

        /// <summary>
        /// Null if it hasn't been figured out yet; true if (some variation of) Visual Studio 2010 is installed on
        /// the current machine, false otherwise.
        /// </summary>
        private static bool? s_dev10IsInstalled = null;
#endif // FEATURE_WIN32_REGISTRY

        /// <summary>
        /// Name of the tools version
        /// </summary>
        private string _toolsVersion;

        /// <summary>
        /// The MSBuildBinPath (and ToolsPath) for this tools version
        /// </summary>
        private string _toolsPath;

        /// <summary>
        /// The properties defined by the toolset.
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _properties;

        /// <summary>
        /// Path to look for msbuild override task files.
        /// </summary>
        private string _overrideTasksPath;

        /// <summary>
        /// ToolsVersion to use as the default ToolsVersion for this version of MSBuild
        /// </summary>
        private string _defaultOverrideToolsVersion;

        /// <summary>
        /// The environment properties
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _environmentProperties;

        /// <summary>
        /// The build-global properties
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _globalProperties;

        /// <summary>
        /// indicates if the default tasks file has already been scanned
        /// </summary>
        private bool _defaultTasksRegistrationAttempted;

        /// <summary>
        /// indicates if the override tasks file has already been scanned
        /// </summary>
        private bool _overrideTasksRegistrationAttempted;

        /// <summary>
        /// holds all the default tasks we know about and the assemblies they exist in
        /// </summary>
        private TaskRegistry _defaultTaskRegistry;

        /// <summary>
        /// holds all the override tasks we know about and the assemblies they exist in
        /// </summary>
        private TaskRegistry _overrideTaskRegistry;

        /// <summary>
        /// Delegate to retrieving files.  For unit testing only.
        /// </summary>
        private DirectoryGetFiles _getFiles;

        /// <summary>
        /// Delegate to check to see if a directory exists
        /// </summary>
        private DirectoryExists _directoryExists = null;

        /// <summary>
        /// Delegate for loading Xml.  For unit testing only.
        /// </summary>
        private LoadXmlFromPath _loadXmlFromPath;

        /// <summary>
        /// Expander to expand the properties and items in the using tasks files
        /// </summary>
        private Expander<ProjectPropertyInstance, ProjectItemInstance> _expander;

        /// <summary>
        /// SubToolsets that map to this toolset.
        /// </summary>
        private Dictionary<string, SubToolset> _subToolsets;

        /// <summary>
        /// If no sub-toolset is specified, this is the default sub-toolset version.  Null == no default
        /// sub-toolset, just use the base toolset.
        /// </summary>
        private string _defaultSubToolsetVersion;

        /// <summary>
        /// Map of project import properties to their list of fall-back search paths
        /// </summary>
        private Dictionary<string, ProjectImportPathMatch> _propertySearchPathsTable;

        /// <summary>
        /// Constructor taking only tools version and a matching tools path
        /// </summary>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="projectCollection">The project collection from which to obtain the properties.</param>
        /// <param name="msbuildOverrideTasksPath">The path to search for msbuild overridetasks files.</param>
        public Toolset(string toolsVersion, string toolsPath, ProjectCollection projectCollection, string msbuildOverrideTasksPath)
            : this(toolsVersion, toolsPath, null, projectCollection, msbuildOverrideTasksPath)
        {
        }

        /// <summary>
        /// Constructor that also associates a set of properties with the tools version
        /// </summary>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="buildProperties">
        /// Properties that should be associated with the Toolset.
        /// May be null, in which case an empty property group will be used.
        /// </param>
        /// <param name="projectCollection">The project collection that this toolset should inherit from</param>
        /// <param name="msbuildOverrideTasksPath">The override tasks path.</param>
        public Toolset(string toolsVersion, string toolsPath, IDictionary<string, string> buildProperties, ProjectCollection projectCollection, string msbuildOverrideTasksPath)
            : this(toolsVersion, toolsPath, buildProperties, projectCollection, null, msbuildOverrideTasksPath)
        {
        }

        /// <summary>
        /// Constructor that also associates a set of properties with the tools version
        /// </summary>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="buildProperties">
        /// Properties that should be associated with the Toolset.
        /// May be null, in which case an empty property group will be used.
        /// </param>
        /// <param name="projectCollection">The project collection that this toolset should inherit from</param>
        /// <param name="subToolsets">The set of sub-toolsets to add to this toolset</param>
        /// <param name="msbuildOverrideTasksPath">The override tasks path.</param>
        public Toolset(string toolsVersion, string toolsPath, IDictionary<string, string> buildProperties, ProjectCollection projectCollection, IDictionary<string, SubToolset> subToolsets, string msbuildOverrideTasksPath)
            : this(toolsVersion, toolsPath, null, projectCollection.EnvironmentProperties, projectCollection.GlobalPropertiesCollection, subToolsets, msbuildOverrideTasksPath, defaultOverrideToolsVersion: null)
        {
            _properties = new PropertyDictionary<ProjectPropertyInstance>();
            if (buildProperties != null)
            {
                foreach (KeyValuePair<string, string> keyValuePair in buildProperties)
                {
                    _properties.Set(ProjectPropertyInstance.Create(keyValuePair.Key, keyValuePair.Value, true));
                }
            }
        }

        /// <summary>
        /// Constructor taking only tools version and a matching tools path
        /// </summary>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="environmentProperties">A <see cref="PropertyDictionary{ProjectPropertyInstance}"/> containing the environment properties.</param>
        /// <param name="globalProperties">A <see cref="PropertyDictionary{ProjectPropertyInstance}"/> containing the global properties.</param>
        /// <param name="msbuildOverrideTasksPath">The override tasks path.</param>
        /// <param name="defaultOverrideToolsVersion">ToolsVersion to use as the default ToolsVersion for this version of MSBuild.</param>
        internal Toolset(string toolsVersion, string toolsPath, PropertyDictionary<ProjectPropertyInstance> environmentProperties, PropertyDictionary<ProjectPropertyInstance> globalProperties, string msbuildOverrideTasksPath, string defaultOverrideToolsVersion)
        {
            ErrorUtilities.VerifyThrowArgumentLength(toolsVersion);
            ErrorUtilities.VerifyThrowArgumentLength(toolsPath);
            ErrorUtilities.VerifyThrowArgumentNull(environmentProperties);
            ErrorUtilities.VerifyThrowArgumentNull(globalProperties);

            _toolsVersion = toolsVersion;
            this.ToolsPath = toolsPath;
            _globalProperties = globalProperties;
            _environmentProperties = environmentProperties;
            _overrideTasksPath = msbuildOverrideTasksPath;
            _defaultOverrideToolsVersion = defaultOverrideToolsVersion;
        }

        /// <summary>
        /// Constructor that also associates a set of properties with the tools version
        /// </summary>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="buildProperties">
        /// Properties that should be associated with the Toolset.
        /// May be null, in which case an empty property group will be used.
        /// </param>
        /// <param name="environmentProperties">A <see cref="PropertyDictionary{ProjectPropertyInstance}"/> containing the environment properties.</param>
        /// <param name="globalProperties">A <see cref="PropertyDictionary{ProjectPropertyInstance}"/> containing the global properties.</param>
        /// <param name="subToolsets">A list of <see cref="SubToolset"/> to use.</param>
        /// <param name="msbuildOverrideTasksPath">The override tasks path.</param>
        /// <param name="defaultOverrideToolsVersion">ToolsVersion to use as the default ToolsVersion for this version of MSBuild.</param>
        /// <param name="importSearchPathsTable">Map of parameter name to property search paths for use during Import.</param>
        internal Toolset(
            string toolsVersion,
            string toolsPath,
            PropertyDictionary<ProjectPropertyInstance> buildProperties,
            PropertyDictionary<ProjectPropertyInstance> environmentProperties,
            PropertyDictionary<ProjectPropertyInstance> globalProperties,
            IDictionary<string, SubToolset> subToolsets,
            string msbuildOverrideTasksPath,
            string defaultOverrideToolsVersion,
            Dictionary<string, ProjectImportPathMatch> importSearchPathsTable = null)
            : this(toolsVersion, toolsPath, environmentProperties, globalProperties, msbuildOverrideTasksPath, defaultOverrideToolsVersion)
        {
            if (_properties == null)
            {
                _properties = buildProperties != null
                    ? new PropertyDictionary<ProjectPropertyInstance>(buildProperties)
                    : new PropertyDictionary<ProjectPropertyInstance>();
            }

            if (subToolsets != null)
            {
                Dictionary<string, SubToolset> subToolsetsAsDictionary = subToolsets as Dictionary<string, SubToolset>;
                _subToolsets = subToolsetsAsDictionary ?? new Dictionary<string, SubToolset>(subToolsets);
            }

            if (importSearchPathsTable != null)
            {
                _propertySearchPathsTable = importSearchPathsTable;
            }
        }

        /// <summary>
        /// Additional constructor to make unit testing the TaskRegistry support easier
        /// </summary>
        /// <remarks>
        /// Internal for unit test purposes only.
        /// </remarks>
        /// <param name="toolsVersion">Name of the toolset</param>
        /// <param name="toolsPath">Path to this toolset's tasks and targets</param>
        /// <param name="buildProperties">
        /// Properties that should be associated with the Toolset.
        /// May be null, in which case an empty property group will be used.
        /// </param>
        /// <param name="projectCollection">The project collection.</param>
        /// <param name="getFiles">A delegate to intercept GetFiles calls.  For unit testing.</param>
        /// <param name="loadXmlFromPath">A delegate to intercept Xml load calls.  For unit testing.</param>
        /// <param name="msbuildOverrideTasksPath">The override tasks path.</param>
        /// <param name="directoryExists"></param>
        internal Toolset(string toolsVersion, string toolsPath, PropertyDictionary<ProjectPropertyInstance> buildProperties, ProjectCollection projectCollection, DirectoryGetFiles getFiles, LoadXmlFromPath loadXmlFromPath, string msbuildOverrideTasksPath, DirectoryExists directoryExists)
            : this(toolsVersion, toolsPath, buildProperties, projectCollection.EnvironmentProperties, projectCollection.GlobalPropertiesCollection, null, msbuildOverrideTasksPath, null)
        {
            ErrorUtilities.VerifyThrowInternalNull(getFiles);
            ErrorUtilities.VerifyThrowInternalNull(loadXmlFromPath);

            _directoryExists = directoryExists;
            _getFiles = getFiles;
            _loadXmlFromPath = loadXmlFromPath;
        }

        /// <summary>
        /// Private constructor for serialization.
        /// </summary>
        private Toolset(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);
        }

        /// <summary>
        /// Helper for inspecting internal task registries that might or might not be initialized at this point.
        /// </summary>
        internal void InspectInternalTaskRegistry(Action<TaskRegistry> visitor)
        {
            visitor(_defaultTaskRegistry);
            visitor(_overrideTaskRegistry);
        }

        /// <summary>
        /// Returns a ProjectImportPathMatch struct for the first property found in the expression for which
        /// project import search paths is enabled.
        /// <param name="expression">Expression to search for properties in (first level only, not recursive)</param>
        /// <returns>List of search paths or ProjectImportPathMatch.None if empty</returns>
        /// </summary>
        internal ProjectImportPathMatch GetProjectImportSearchPaths(string expression)
        {
            if (string.IsNullOrEmpty(expression) || ImportPropertySearchPathsTable == null)
            {
                return ProjectImportPathMatch.None;
            }

            foreach (var searchPath in _propertySearchPathsTable.Values)
            {
                if (expression.IndexOf(searchPath.MsBuildPropertyFormat, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return searchPath;
                }
            }

            return ProjectImportPathMatch.None;
        }

        /// <summary>
        /// Name of this toolset
        /// </summary>
        public string ToolsVersion => _toolsVersion;

        /// <summary>
        /// Path to this toolset's tasks and targets. Corresponds to $(MSBuildToolsPath) in a project or targets file.
        /// </summary>
        public string ToolsPath
        {
            get
            {
                return _toolsPath;
            }

            private set
            {
                // Strip the trailing backslash if it exists.  This way, when somebody
                // concatenates does something like "$(MSBuildToolsPath)\CSharp.targets",
                // they don't end up with a double-backslash in the middle.  (It doesn't
                // technically hurt anything, but it doesn't look nice.)
                string toolsPathToUse = value;

                if (FileUtilities.EndsWithSlash(toolsPathToUse))
                {
                    string rootPath = Path.GetPathRoot(Path.GetFullPath(toolsPathToUse));

                    // Only if $(MSBuildBinPath) is *NOT* the root of a drive should we strip trailing slashes
                    if (!String.Equals(rootPath, toolsPathToUse, StringComparison.OrdinalIgnoreCase))
                    {
                        // Trim off one trailing slash
                        toolsPathToUse = toolsPathToUse.Substring(0, toolsPathToUse.Length - 1);
                    }
                }

                _toolsPath = toolsPathToUse;
            }
        }

        /// <summary>
        /// Properties associated with the toolset
        /// </summary>
        public IDictionary<string, ProjectPropertyInstance> Properties
        {
            get
            {
                if (_properties == null)
                {
                    return ReadOnlyEmptyDictionary<string, ProjectPropertyInstance>.Instance;
                }

                return new ObjectModel.ReadOnlyDictionary<string, ProjectPropertyInstance>(_properties);
            }
        }

        /// <summary>
        /// The set of sub-toolsets associated with this toolset.
        /// </summary>
        public IDictionary<string, SubToolset> SubToolsets
        {
            get
            {
                if (_subToolsets == null)
                {
                    return ReadOnlyEmptyDictionary<string, SubToolset>.Instance;
                }

                return new ObjectModel.ReadOnlyDictionary<string, SubToolset>(_subToolsets);
            }
        }

        /// <summary>
        /// Returns the default sub-toolset version for this sub-toolset.  Heuristic used is:
        /// 1) If Visual Studio 2010 is installed and our ToolsVersion is "4.0", use the base toolset, and return
        ///    a sub-toolset version of "10.0", to be set as a publicly visible property so that e.g. targets can
        ///    consume it.  This is to handle the fact that Visual Studio 2010 did not have any concept of sub-toolsets.
        /// 2) Otherwise, use the highest-versioned sub-toolset found.  Sub-toolsets with numbered versions will
        ///    be ordered numerically; any additional sub-toolsets will be prepended to the beginning of the list in
        ///    the order found. We use the highest-versioned sub-toolset because, in the absence of any other information,
        ///    we assume that higher-versioned tools will be more likely to be able to generate something more correct.
        ///
        /// Will return null if there is no sub-toolset available (and Dev10 is not installed).
        /// </summary>
        public string DefaultSubToolsetVersion
        {
            get
            {
                if (_defaultSubToolsetVersion == null)
                {
                    // 1) Workaround for ToolsVersion 4.0 + VS 2010
                    if (String.Equals(ToolsVersion, "4.0", StringComparison.OrdinalIgnoreCase) && Dev10IsInstalled)
                    {
                        return Constants.Dev10SubToolsetValue;
                    }

                    // 2) Otherwise, just pick the highest available.
                    SortedDictionary<Version, string> subToolsetsWithVersion = new SortedDictionary<Version, string>();
                    List<string> additionalSubToolsetNames = new List<string>();

                    foreach (string subToolsetName in SubToolsets.Keys)
                    {
                        Version subToolsetVersion = VersionUtilities.ConvertToVersion(subToolsetName);

                        if (subToolsetVersion != null)
                        {
                            subToolsetsWithVersion.Add(subToolsetVersion, subToolsetName);
                        }
                        else
                        {
                            // if it doesn't parse to an actual version number, shrug and just add it to the end.
                            additionalSubToolsetNames.Add(subToolsetName);
                        }
                    }

                    List<string> orderedSubToolsetList = new List<string>(additionalSubToolsetNames);
                    orderedSubToolsetList.AddRange(subToolsetsWithVersion.Values);

                    if (orderedSubToolsetList.Count > 0)
                    {
                        _defaultSubToolsetVersion = orderedSubToolsetList[orderedSubToolsetList.Count - 1];
                    }
                }

                return _defaultSubToolsetVersion;
            }
        }

        /// <summary>
        /// Null if it hasn't been figured out yet; true if (some variation of) Visual Studio 2010 is installed on
        /// the current machine, false otherwise.
        /// </summary>
        /// <comments>
        /// Internal so that unit tests can use it too.
        /// </comments>
        internal static bool Dev10IsInstalled
        {
            get
            {
#if FEATURE_WIN32_REGISTRY
                if (!NativeMethodsShared.IsWindows)
                {
                    return false;
                }

                if (s_dev10IsInstalled == null)
                {
                    try
                    {
                        // Figure out whether Dev10 is currently installed using the following heuristic:
                        // - Check whether the overall key (installed if any version of Dev10 is installed) is there.
                        //   - If it's not, no version of Dev10 exists or has ever existed on this machine, so return 'false'.
                        //   - If it is, we know that some version of Dev10 has been installed at some point, but we don't know
                        //     for sure whether it's still there or not.  Check the inndividual keys for {Pro, Premium, Ultimate,
                        //     C# Express, VB Express, C++ Express, VWD Express, LightSwitch} 2010
                        //     - If even one of them exists, return 'true'.
                        //     - Otherwise, return 'false.
                        if (!RegistryKeyWrapper.KeyExists(Dev10OverallInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32))
                        {
                            s_dev10IsInstalled = false;
                        }
                        else if (
                                    RegistryKeyWrapper.KeyExists(Dev10UltimateInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10PremiumInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10ProfessionalInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10VCSExpressInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10VBExpressInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10VCExpressInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10VWDExpressInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32) ||
                                    RegistryKeyWrapper.KeyExists(Dev10LightSwitchInstallKeyRegistryPath, RegistryHive.LocalMachine, RegistryView.Registry32))
                        {
                            s_dev10IsInstalled = true;
                        }
                        else
                        {
                            s_dev10IsInstalled = false;
                        }
                    }
                    catch (Exception e) when (!ExceptionHandling.NotExpectedRegistryException(e))
                    {
                        // if it's a registry exception, just shrug, eat it, and move on with life on the assumption that whatever
                        // went wrong, it's pretty clear that Dev10 probably isn't installed.
                        s_dev10IsInstalled = false;
                    }
                }

                return s_dev10IsInstalled.Value;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Path to look for msbuild override task files.
        /// </summary>
        internal string OverrideTasksPath => _overrideTasksPath;

        /// <summary>
        /// ToolsVersion to use as the default ToolsVersion for this version of MSBuild
        /// </summary>
        internal string DefaultOverrideToolsVersion => _defaultOverrideToolsVersion;

        /// <summary>
        /// Map of properties to their list of fall-back search paths
        /// </summary>
        internal Dictionary<string, ProjectImportPathMatch> ImportPropertySearchPathsTable => _propertySearchPathsTable;

        /// <summary>
        /// Map of MSBuildExtensionsPath properties to their list of fallback search paths
        /// </summary>
        internal Dictionary<MSBuildExtensionsPathReferenceKind, IList<string>> MSBuildExtensionsPathSearchPathsTable
        {
            get; set;
        }

        /// <summary>
        /// Function for serialization.
        /// </summary>
        void ITranslatable.Translate(ITranslator translator)
        {
            translator.Translate(ref _toolsVersion);
            translator.Translate(ref _toolsPath);
            translator.TranslateProjectPropertyInstanceDictionary(ref _properties);
            translator.TranslateProjectPropertyInstanceDictionary(ref _environmentProperties);
            translator.TranslateProjectPropertyInstanceDictionary(ref _globalProperties);
            translator.TranslateDictionary(ref _subToolsets, StringComparer.OrdinalIgnoreCase, SubToolset.FactoryForDeserialization);
            translator.Translate(ref _overrideTasksPath);
            translator.Translate(ref _defaultOverrideToolsVersion);
            translator.TranslateDictionary(ref _propertySearchPathsTable, StringComparer.OrdinalIgnoreCase, ProjectImportPathMatch.FactoryForDeserialization);
        }

        /// <summary>
        /// Generates the sub-toolset version to be used with this toolset.  Sub-toolset version is based on:
        /// 1. If "VisualStudioVersion" is set as a property on the toolset itself (global or environment),
        ///    use that.
        /// 2. Otherwise, use the default sub-toolset version for this toolset.
        ///
        /// The sub-toolset version returned may be null; if so, that means that no sub-toolset should be used,
        /// just the base toolset on its own. The sub-toolset version returned may not map to an existing
        /// sub-toolset.
        /// </summary>
        public string GenerateSubToolsetVersion()
        {
            string subToolsetVersion = GenerateSubToolsetVersion(0 /* user doesn't care about solution version */);
            return subToolsetVersion;
        }

        /// <summary>
        /// Generates the sub-toolset version to be used with this toolset.  Sub-toolset version is based on:
        /// 1. If the "VisualStudioVersion" global property exists in the set of properties passed to us, use it.
        /// 2. Otherwise, if "VisualStudioVersion" is set as a property on the toolset itself (global or environment),
        ///    use that.
        /// 3. Otherwise, use Visual Studio version from solution file if it maps to an existing sub-toolset.
        /// 4. Otherwise, use the default sub-toolset version for this toolset.
        ///
        /// The sub-toolset version returned may be null; if so, that means that no sub-toolset should be used,
        /// just the base toolset on its own. The sub-toolset version returned may not map to an existing
        /// sub-toolset.
        ///
        /// The global properties dictionary may be null.
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2233:OperationsShouldNotOverflow", MessageId = "solutionVersion-1", Justification = "Method called in restricted places. Checks done by the callee and inside the method.")]
        public string GenerateSubToolsetVersion(IDictionary<string, string> overrideGlobalProperties, int solutionVersion)
        {
            return GenerateSubToolsetVersionUsingVisualStudioVersion(overrideGlobalProperties, solutionVersion - 1);
        }

        /// <summary>
        /// Given a property name and a sub-toolset version, searches for that property first in the
        /// sub-toolset, then falls back to the base toolset if necessary, and returns the property
        /// if it was found.
        /// </summary>
        public ProjectPropertyInstance GetProperty(string propertyName, string subToolsetVersion)
        {
            SubToolset subToolset;
            ProjectPropertyInstance property = null;

            if (SubToolsets.TryGetValue(subToolsetVersion, out subToolset))
            {
                property = subToolset.Properties[propertyName];
            }

            return property ?? (Properties[propertyName]);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static Toolset FactoryForDeserialization(ITranslator translator)
        {
            Toolset toolset = new Toolset(translator);
            return toolset;
        }

        /// <summary>
        /// Given a search path and a task pattern get a list of task or override task files.
        /// </summary>
        internal static string[] GetTaskFiles(DirectoryGetFiles getFiles, LoggingContext loggingContext, string taskPattern, string searchPath, string taskFileWarning)
        {
            string[] defaultTasksFiles = null;

            try
            {
                if (getFiles != null)
                {
                    defaultTasksFiles = getFiles(searchPath, taskPattern);
                }
                else
                {
                    // The order of the returned file names is not guaranteed per msdn
                    defaultTasksFiles = Directory.GetFiles(searchPath, taskPattern);
                }

                if (defaultTasksFiles.Length == 0)
                {
                    loggingContext.LogWarning(
                        null,
                        new BuildEventFileInfo(/* this warning truly does not involve any file */ String.Empty),
                        taskFileWarning,
                        taskPattern,
                        searchPath,
                        String.Empty);
                }
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                loggingContext.LogWarning(
                    null,
                    new BuildEventFileInfo(/* this warning truly does not involve any file */ String.Empty),
                    taskFileWarning,
                    taskPattern,
                    searchPath,
                    e.Message);
            }

            // Sort the file names to give a deterministic order
            if (defaultTasksFiles != null)
            {
                Array.Sort<string>(defaultTasksFiles, StringComparer.OrdinalIgnoreCase);
                return defaultTasksFiles;
            }
            return [];
        }

        /// <summary>
        /// Generates the sub-toolset version to be used with this toolset.  Sub-toolset version is based on:
        /// 1. If the "VisualStudioVersion" global property exists in the set of properties passed to us, use it.
        /// 2. Otherwise, if "VisualStudioVersion" is set as a property on the toolset itself (global or environment),
        ///    use that.
        /// 3. Otherwise, use Visual Studio version from solution file if it maps to an existing sub-toolset.
        /// 4. Otherwise, use the default sub-toolset version for this toolset.
        ///
        /// The sub-toolset version returned may be null; if so, that means that no sub-toolset should be used,
        /// just the base toolset on its own. The sub-toolset version returned may not map to an existing
        /// sub-toolset.
        ///
        /// The global properties dictionary may be null.
        /// </summary>
        internal string GenerateSubToolsetVersion(PropertyDictionary<ProjectPropertyInstance> overrideGlobalProperties)
        {
            if (overrideGlobalProperties != null)
            {
                ProjectPropertyInstance subToolsetProperty = overrideGlobalProperties[Constants.SubToolsetVersionPropertyName];

                if (subToolsetProperty != null)
                {
                    return subToolsetProperty.EvaluatedValue;
                }
            }

            /* don't care about solution version */
            return GenerateSubToolsetVersion(0);
        }

        /// <summary>
        /// Generates the sub-toolset version to be used with this toolset.  Sub-toolset version is based on:
        /// 1. If the "VisualStudioVersion" global property exists in the set of properties passed to us, use it.
        /// 2. Otherwise, if "VisualStudioVersion" is set as a property on the toolset itself (global or environment),
        ///    use that.
        /// 3. Otherwise, use Visual Studio version from solution file if it maps to an existing sub-toolset.
        /// 4. Otherwise, use the default sub-toolset version for this toolset.
        ///
        /// The sub-toolset version returned may be null; if so, that means that no sub-toolset should be used,
        /// just the base toolset on its own. The sub-toolset version returned may not map to an existing
        /// sub-toolset.
        ///
        /// The global properties dictionary may be null.
        /// </summary>
        internal string GenerateSubToolsetVersion(int visualStudioVersionFromSolution)
        {
            // Next, try the toolset global properties (before environment properties because if there's a clash between the
            // two, global should win)
            if (_globalProperties != null)
            {
                ProjectPropertyInstance visualStudioVersionProperty = _globalProperties[Constants.SubToolsetVersionPropertyName];

                if (visualStudioVersionProperty != null)
                {
                    return visualStudioVersionProperty.EvaluatedValue;
                }
            }

            // Next, try the toolset environment properties
            if (_environmentProperties != null)
            {
                ProjectPropertyInstance visualStudioVersionProperty = _environmentProperties[Constants.SubToolsetVersionPropertyName];

                if (visualStudioVersionProperty != null)
                {
                    return visualStudioVersionProperty.EvaluatedValue;
                }
            }

            // The VisualStudioVersion derived from parsing the solution version in the solution file
            string subToolsetVersion = null;
            if (visualStudioVersionFromSolution > 0)
            {
                Version visualStudioVersionFromSolutionAsVersion = new Version(visualStudioVersionFromSolution, 0);
                subToolsetVersion = SubToolsets.Keys.FirstOrDefault(version => visualStudioVersionFromSolutionAsVersion.Equals(VersionUtilities.ConvertToVersion(version)));
            }

            // Solution version also didn't work out, so fall back to default.
            // If subToolsetVersion is null, there simply wasn't a matching solution version.
            return subToolsetVersion ?? (DefaultSubToolsetVersion);
        }

        /// <summary>
        /// Return a task registry stub for the tasks in the *.tasks file for this toolset
        /// </summary>
        /// <param name="loggingContext">The logging context used to log during task registration.</param>
        /// <param name="projectRootElementCache">The <see cref="ProjectRootElementCache"/> to use.</param>
        /// <returns>The task registry</returns>
        internal TaskRegistry GetTaskRegistry(LoggingContext loggingContext, ProjectRootElementCacheBase projectRootElementCache)
        {
            RegisterDefaultTasks(loggingContext, projectRootElementCache);
            return _defaultTaskRegistry;
        }

        /// <summary>
        /// Get SubToolset version using Visual Studio version from Dev 12 solution file
        /// </summary>
        internal string GenerateSubToolsetVersionUsingVisualStudioVersion(IDictionary<string, string> overrideGlobalProperties, int visualStudioVersionFromSolution)
        {
            string visualStudioVersion;
            if (overrideGlobalProperties != null && overrideGlobalProperties.TryGetValue(Constants.SubToolsetVersionPropertyName, out visualStudioVersion))
            {
                return visualStudioVersion;
            }

            return GenerateSubToolsetVersion(visualStudioVersionFromSolution);
        }

        /// <summary>
        /// Return a task registry for the override tasks in the *.overridetasks file for this toolset
        /// </summary>
        /// <param name="loggingContext">The logging context used to log during task registration.</param>
        /// <param name="projectRootElementCache">The <see cref="ProjectRootElementCache"/> to use.</param>
        /// <returns>The task registry</returns>
        internal TaskRegistry GetOverrideTaskRegistry(LoggingContext loggingContext, ProjectRootElementCacheBase projectRootElementCache)
        {
            RegisterOverrideTasks(loggingContext, projectRootElementCache);
            return _overrideTaskRegistry;
        }

        /// <summary>
        /// Used to load information about default MSBuild tasks i.e. tasks that do not need to be explicitly declared in projects
        /// with the &lt;UsingTask&gt; element. Default task information is read from special files, which are located in the same
        /// directory as the MSBuild binaries.
        /// </summary>
        /// <remarks>
        /// 1) a default tasks file needs the &lt;Project&gt; root tag in order to be well-formed
        /// 2) the XML declaration tag &lt;?xml ...&gt; is ignored
        /// 3) comment tags are always ignored regardless of their placement
        /// 4) the rest of the tags are expected to be &lt;UsingTask&gt; tags
        /// </remarks>
        /// <param name="loggingContext">The logging context to use to log during this registration.</param>
        /// <param name="projectRootElementCache">The <see cref="ProjectRootElementCache"/> to use.</param>
        private void RegisterDefaultTasks(LoggingContext loggingContext, ProjectRootElementCacheBase projectRootElementCache)
        {
            if (!_defaultTasksRegistrationAttempted)
            {
                try
                {
                    _defaultTaskRegistry = new TaskRegistry(projectRootElementCache);

                    InitializeProperties(loggingContext);

                    string[] defaultTasksFiles = GetTaskFiles(_getFiles, loggingContext, DefaultTasksFilePattern, ToolsPath, "DefaultTasksFileLoadFailureWarning");
                    LoadAndRegisterFromTasksFile(defaultTasksFiles, loggingContext, "DefaultTasksFileFailure", projectRootElementCache, _defaultTaskRegistry);
                }
                finally
                {
                    _defaultTasksRegistrationAttempted = true;
                }
            }
        }

        /// <summary>
        /// Initialize the properties which are used to evaluate the tasks files.
        /// </summary>
        private void InitializeProperties(LoggingContext loggingContext)
        {
            if (_expander != null)
            {
                return;
            }

            try
            {

                List<ProjectPropertyInstance> reservedProperties = new List<ProjectPropertyInstance>();

                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.binPath, EscapingUtilities.Escape(ToolsPath), mayBeReserved: true));
                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.toolsVersion, ToolsVersion, mayBeReserved: true));

                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.toolsPath, EscapingUtilities.Escape(ToolsPath), mayBeReserved: true));
                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.assemblyVersion, Constants.AssemblyVersion, mayBeReserved: true));
                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.version, MSBuildAssemblyFileVersion.Instance.MajorMinorBuild, mayBeReserved: true));

                reservedProperties.Add(ProjectPropertyInstance.Create(ReservedPropertyNames.msbuildRuntimeType,
#if RUNTIME_TYPE_NETCORE
                    Traits.Instance.ForceEvaluateAsFullFramework ? "Full" : "Core",
#else
                    "Full",
#endif
                    mayBeReserved: true));


                // Add one for the subtoolset version property -- it may or may not be set depending on whether it has already been set by the
                // environment or global properties, but it's better to create a dictionary that's one too big than one that's one too small.
                int count = _environmentProperties.Count + reservedProperties.Count + Properties.Values.Count + _globalProperties.Count + 1;

                // GenerateSubToolsetVersion checks the environment and global properties, so it's safe to go ahead and gather the
                // subtoolset properties here without fearing that we'll have somehow come up with the wrong subtoolset version.
                string subToolsetVersion = this.GenerateSubToolsetVersion();
                SubToolset subToolset;
                ICollection<ProjectPropertyInstance> subToolsetProperties = null;

                if (subToolsetVersion != null)
                {
                    if (SubToolsets.TryGetValue(subToolsetVersion, out subToolset))
                    {
                        subToolsetProperties = subToolset.Properties.Values;
                        count += subToolsetProperties.Count;
                    }
                }

                PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>(count);

                // Should be imported in the same order as in the evaluator:
                // - Environment
                // - Toolset
                // - Subtoolset (if any)
                // - Global
                propertyBag.ImportProperties(_environmentProperties);

                propertyBag.ImportProperties(reservedProperties);

                propertyBag.ImportProperties(Properties.Values);

                if (subToolsetVersion != null)
                {
                    propertyBag.Set(ProjectPropertyInstance.Create(Constants.SubToolsetVersionPropertyName, subToolsetVersion));
                }

                if (subToolsetProperties != null)
                {
                    propertyBag.ImportProperties(subToolsetProperties);
                }

                propertyBag.ImportProperties(_globalProperties);

                _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, FileSystems.Default, loggingContext);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                loggingContext.LogError(new BuildEventFileInfo(/* this warning truly does not involve any file it is just gathering properties */String.Empty), "TasksPropertyBagError", e.Message);
            }
        }

        /// <summary>
        /// Used to load information about MSBuild override tasks i.e. tasks that override tasks declared in tasks or project files.
        /// </summary>
        private void RegisterOverrideTasks(LoggingContext loggingContext, ProjectRootElementCacheBase projectRootElementCache)
        {
            if (!_overrideTasksRegistrationAttempted)
            {
                try
                {
                    _overrideTaskRegistry = new TaskRegistry(projectRootElementCache);
                    bool overrideDirectoryExists = false;

                    try
                    {
                        // Make sure the override directory exists and is not empty before trying to find the files
                        if (!String.IsNullOrEmpty(_overrideTasksPath))
                        {
                            if (Path.IsPathRooted(_overrideTasksPath))
                            {
                                if (_directoryExists != null)
                                {
                                    overrideDirectoryExists = _directoryExists(_overrideTasksPath);
                                }
                                else
                                {
                                    overrideDirectoryExists = FileSystems.Default.DirectoryExists(_overrideTasksPath);
                                }
                            }

                            if (!overrideDirectoryExists)
                            {
                                string rootedPathMessage = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("OverrideTaskNotRootedPath", _overrideTasksPath);
                                loggingContext.LogWarning(null, new BuildEventFileInfo(String.Empty /* this warning truly does not involve any file*/), "OverrideTasksFileFailure", rootedPathMessage);
                            }
                        }
                    }
                    catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                    {
                        string rootedPathMessage = ResourceUtilities.FormatResourceStringStripCodeAndKeyword("OverrideTaskProblemWithPath", _overrideTasksPath, e.Message);
                        loggingContext.LogWarning(null, new BuildEventFileInfo(String.Empty /* this warning truly does not involve any file*/), "OverrideTasksFileFailure", rootedPathMessage);
                    }

                    if (overrideDirectoryExists)
                    {
                        InitializeProperties(loggingContext);
                        string[] overrideTasksFiles = GetTaskFiles(_getFiles, loggingContext, OverrideTasksFilePattern, _overrideTasksPath, "OverrideTasksFileLoadFailureWarning");

                        // Load and register any override tasks
                        LoadAndRegisterFromTasksFile(overrideTasksFiles, loggingContext, "OverrideTasksFileFailure", projectRootElementCache, _overrideTaskRegistry);
                    }
                }
                finally
                {
                    _overrideTasksRegistrationAttempted = true;
                }
            }
        }

        /// <summary>
        /// Do the actual loading of the tasks or override tasks file and register the tasks in the task registry
        /// </summary>
        private void LoadAndRegisterFromTasksFile(string[] defaultTaskFiles, LoggingContext loggingContext, string taskFileError, ProjectRootElementCacheBase projectRootElementCache, TaskRegistry registry)
        {
            string currentTasksFile = null;
            try
            {
                TaskRegistry.InitializeTaskRegistryFromUsingTaskElements<ProjectPropertyInstance, ProjectItemInstance>(
                    loggingContext,
                    EnumerateTasksRegistrations(),
                    registry,
                    _expander,
                    ExpanderOptions.ExpandProperties,
                    FileSystems.Default);
            }
            catch (XmlException e)
            {
                // handle XML errors in the default tasks file
                ProjectFileErrorUtilities.ThrowInvalidProjectFile(new BuildEventFileInfo(currentTasksFile, e),
                    taskFileError, e.Message);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                loggingContext.LogError(new BuildEventFileInfo(currentTasksFile),
                    taskFileError, e.Message);
            }

            IEnumerable<(ProjectUsingTaskElement projectUsingTaskXml, string directoryOfImportingFile)> EnumerateTasksRegistrations()
            {
                foreach (string defaultTasksFile in defaultTaskFiles)
                {
                    currentTasksFile = defaultTasksFile;
                    // Important to keep the following line since unit tests use the delegate.
                    ProjectRootElement projectRootElement;
                    if (_loadXmlFromPath != null)
                    {
                        XmlDocumentWithLocation defaultTasks = _loadXmlFromPath(defaultTasksFile);
                        projectRootElement = ProjectRootElement.Open(defaultTasks);
                    }
                    else
                    {
                        projectRootElement = ProjectRootElement.Open(defaultTasksFile, projectRootElementCache,
                            false /*The tasks file is not a explicitly loaded file*/,
                            preserveFormatting: false);
                    }

                    foreach (ProjectElement elementXml in projectRootElement.ChildrenEnumerable)
                    {
                        ProjectUsingTaskElement usingTask = elementXml as ProjectUsingTaskElement;

                        if (usingTask == null)
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(
                                elementXml.Location,
                                "UnrecognizedElement",
                                elementXml.XmlElement.Name);
                        }

                        yield return (usingTask, Path.GetDirectoryName(defaultTasksFile));
                    }
                }
            }
        }
    }
}
