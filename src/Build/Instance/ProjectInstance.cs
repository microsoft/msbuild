// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Instance;
using Microsoft.Build.Instance.ImmutableProjectCollections;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using ForwardingLoggerRecord = Microsoft.Build.Logging.ForwardingLoggerRecord;
using ObjectModel = System.Collections.ObjectModel;
using ProjectItemInstanceFactory = Microsoft.Build.Execution.ProjectItemInstance.TaskItem.ProjectItemInstanceFactory;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.Execution
{
    using Utilities = Microsoft.Build.Internal.Utilities;

    /// <summary>
    /// Enum for controlling project instance creation
    /// </summary>
    [Flags]
    [SuppressMessage("Microsoft.Usage", "CA2217:DoNotMarkEnumsWithFlags", Justification = "ImmutableWithFastItemLookup is a variation on Immutable")]
    public enum ProjectInstanceSettings
    {
        /// <summary>
        /// no options
        /// </summary>
        None = 0x0,

        /// <summary>
        /// create immutable version of project instance
        /// </summary>
        Immutable = 0x1,

        /// <summary>
        /// create project instance with some look up table that improves performance
        /// </summary>
        ImmutableWithFastItemLookup = Immutable | 0x2
    }

    /// <summary>
    /// What the user gets when they clone off a ProjectInstance.
    /// They can hold onto this, change/query items and properties,
    /// and call it several times to build it.
    /// </summary>
    /// <comments>
    /// Neither this class nor any of its constituents are allowed to have
    /// references to any of the Construction or Evaluation objects.
    /// This class is immutable except for adding instance items and setting instance properties.
    /// It only exposes items and properties: targets, host services, and the task registry are not exposed as they are only the concern of build.
    /// Constructors are internal in order to direct users to Project class instead; these are only createable via Project objects.
    /// </comments>
    [DebuggerDisplay(@"{FullPath} #Targets={TargetsCount} DefaultTargets={(DefaultTargets == null) ? System.String.Empty : System.String.Join("";"", DefaultTargets.ToArray())} ToolsVersion={Toolset.ToolsVersion} InitialTargets={(InitialTargets == null) ? System.String.Empty : System.String.Join("";"", InitialTargets.ToArray())} #GlobalProperties={GlobalProperties.Count} #Properties={Properties.Count} #ItemTypes={ItemTypes.Count} #Items={Items.Count}")]
    public class ProjectInstance : IPropertyProvider<ProjectPropertyInstance>, IItemProvider<ProjectItemInstance>, IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>, ITranslatable
    {
        /// <summary>
        /// Targets in the project after overrides have been resolved.
        /// This is an unordered collection keyed by target name.
        /// Only the wrapper around this collection is exposed.
        /// </summary>
        private RetrievableEntryHashSet<ProjectTargetInstance> _actualTargets;

        /// <summary>
        /// Targets in the project after overrides have been resolved.
        /// This is an immutable, unordered collection keyed by target name.
        /// It is just a wrapper around <see cref="_actualTargets">actualTargets</see>.
        /// </summary>
        private IDictionary<string, ProjectTargetInstance> _targets;

        private List<string> _defaultTargets;

        private List<string> _initialTargets;

        private IList<string> _importPaths;

        private IList<string> _importPathsIncludingDuplicates;

        /// <summary>
        /// The global properties evaluation occurred with.
        /// Needed by the build as they traverse between projects.
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _globalProperties;

        /// <summary>
        /// List of names of the properties that, while global, are still treated as overridable
        /// </summary>
        private ISet<string> _globalPropertiesToTreatAsLocal;

        /// <summary>
        /// Whether the tools version used originated from an explicit specification,
        /// for example from an MSBuild task or /tv switch.
        /// </summary>
        private bool _explicitToolsVersionSpecified;

        /// <summary>
        /// Properties in the project. This is a dictionary of name, value pairs.
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _properties;

        /// <summary>
        /// Properties originating from environment variables, gotten from the project collection
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _environmentVariableProperties;

        /// <summary>
        /// Items in the project. This is a dictionary of ordered lists of a single type of items keyed by item type.
        /// </summary>
        private IItemDictionary<ProjectItemInstance> _items;

        /// <summary>
        /// Items organized by evaluatedInclude value
        /// </summary>
        private IMultiDictionary<string, ProjectItemInstance> _itemsByEvaluatedInclude;

        /// <summary>
        /// The project's root directory, for evaluation of relative paths and
        /// setting the current directory during build.
        /// Is never null.
        /// If the project has not been loaded from disk and has not been given a path, returns the current directory from
        /// the time the project was loaded - this is the same behavior as Whidbey/Orcas.
        /// If the project has not been loaded from disk but has been given a path, this path may not exist.
        /// </summary>
        private string _directory;

        /// <summary>
        /// The project file location, for logging.
        /// If the project has not been loaded from disk and has not been given a path, returns null.
        /// If the project has not been loaded from disk but has been given a path, this path may not exist.
        /// </summary>
        private ElementLocation _projectFileLocation;

        /// <summary>
        /// The item definitions from the parent Project.
        /// </summary>
        private IRetrievableEntryHashSet<ProjectItemDefinitionInstance> _itemDefinitions;

        /// <summary>
        /// The HostServices to use during a build.
        /// </summary>
        private HostServices _hostServices;

        /// <summary>
        /// Whether when we read a ToolsVersion that is not equivalent to the current one on the Project tag, we
        /// treat it as the current one.
        /// </summary>
        private bool _usingDifferentToolsVersionFromProjectFile;

        /// <summary>
        /// The toolsversion that was originally on the project's Project root element
        /// </summary>
        private string _originalProjectToolsVersion;

        /// <summary>
        /// Whether the instance is immutable.
        /// The object is always mutable during evaluation.
        /// </summary>
        private bool _isImmutable;

        private IDictionary<string, List<TargetSpecification>> _beforeTargets;
        private IDictionary<string, List<TargetSpecification>> _afterTargets;
        private Toolset _toolset;
        private string _subToolsetVersion;
        private TaskRegistry _taskRegistry;
        private bool _translateEntireState;
        private int _evaluationId = BuildEventContext.InvalidEvaluationId;

        /// <summary>
        /// The property and item filter used when creating this instance, or null if this is not a filtered copy
        /// of another ProjectInstance. <seealso cref="ProjectInstance(ProjectInstance, bool, RequestedProjectState)"/>
        /// </summary>
        private RequestedProjectState _requestedProjectStateFilter;

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Uses the default project collection.
        /// </summary>
        /// <param name="projectFile">The name of the project file.</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(string projectFile)
            : this(projectFile, null, (string)null)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Uses the default project collection.
        /// </summary>
        /// <param name="projectFile">The name of the project file.</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion)
            : this(projectFile, globalProperties, toolsVersion, ProjectCollection.GlobalProjectCollection)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// </summary>
        /// <param name="projectFile">The name of the project file.</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(projectFile, globalProperties, toolsVersion, null /* no sub-toolset version */, projectCollection)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// </summary>
        /// <param name="projectFile">The name of the project file.</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="subToolsetVersion">The sub-toolset version, used in tandem with the ToolsVersion to determine the set of toolset properties.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection)
            : this(projectFile, globalProperties, toolsVersion, subToolsetVersion, projectCollection, projectLoadSettings: null, evaluationContext: null, directoryCacheFactory: null, interactive: false)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// </summary>
        /// <param name="projectFile">The path to the project file.</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version. May be <see langword="null"/>.</param>
        /// <param name="subToolsetVersion">The sub-toolset version, used in tandem with <paramref name="toolsVersion"/> to determine the set of toolset properties. May be <see langword="null"/>.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <param name="context">Context to evaluate inside, potentially sharing caches with other evaluations.</param>
        /// <param name="interactive">Indicates if loading the project is allowed to interact with the user.</param>
        internal ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, EvaluationContext context, bool interactive = false)
            : this(projectFile, globalProperties, toolsVersion, subToolsetVersion, projectCollection, projectLoadSettings: null, evaluationContext: context, directoryCacheFactory: null, interactive: interactive)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Evaluation context may be null.
        /// </summary>
        /// <param name="projectFile">The name of the project file.</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="subToolsetVersion">The sub-toolset version, used in tandem with the ToolsVersion to determine the set of toolset properties.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <param name="projectLoadSettings">Project load settings</param>
        /// <param name="evaluationContext">The context to use for evaluation.</param>
        /// <param name="directoryCacheFactory">The directory cache factory to use for file I/O.</param>
        /// <param name="interactive">Indicates if loading the project is allowed to interact with the user.</param>
        /// <returns>A new project instance</returns>
        private ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection,
            ProjectLoadSettings? projectLoadSettings, EvaluationContext evaluationContext, IDirectoryCacheFactory directoryCacheFactory, bool interactive)
        {
            ErrorUtilities.VerifyThrowArgumentLength(projectFile);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));

            // We do not control the current directory at this point, but assume that if we were
            // passed a relative path, the caller assumes we will prepend the current directory.
            projectFile = FileUtilities.NormalizePath(projectFile);

            BuildParameters buildParameters = new BuildParameters(projectCollection)
            {
                Interactive = interactive
            };

            BuildEventContext buildEventContext = new BuildEventContext(buildParameters.NodeId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);
            ProjectRootElement xml = ProjectRootElement.OpenProjectOrSolution(projectFile, globalProperties, toolsVersion, buildParameters.ProjectRootElementCache, true /*Explicitly Loaded*/);

            Initialize(xml, globalProperties, toolsVersion, subToolsetVersion, 0 /* no solution version provided */, buildParameters, projectCollection.LoggingService, buildEventContext,
                projectLoadSettings: projectLoadSettings, evaluationContext: evaluationContext, directoryCacheFactory: directoryCacheFactory);
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Uses the default project collection.
        /// </summary>
        /// <param name="xml">The project root element</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(ProjectRootElement xml)
            : this(xml, null, null, ProjectCollection.GlobalProjectCollection)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// </summary>
        /// <param name="xml">The project root element</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(xml, globalProperties, toolsVersion, null, projectCollection)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Sub-toolset version may be null, but if specified will override all other methods of determining the sub-toolset.
        /// </summary>
        /// <param name="xml">The project root element</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="subToolsetVersion">The sub-toolset version, used in tandem with the ToolsVersion to determine the set of toolset properties.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <returns>A new project instance</returns>
        public ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection)
            : this(xml, globalProperties, toolsVersion, subToolsetVersion, projectCollection, projectLoadSettings: null, evaluationContext: null, directoryCacheFactory: null, interactive: false)
        {
        }

        /// <summary>
        /// Creates a ProjectInstance from an external created <see cref="Project"/>.
        /// Properties and items are cloned immediately and only the instance data is stored.
        /// </summary>
        public ProjectInstance(Project project, ProjectInstanceSettings settings)
        {
            ErrorUtilities.VerifyThrowInternalNull(project);

            var projectPath = project.FullPath;
            _directory = Path.GetDirectoryName(projectPath);
            _projectFileLocation = ElementLocation.Create(projectPath);
            _hostServices = project.ProjectCollection.HostServices;

            EvaluationId = project.EvaluationCounter;

            var immutable = (settings & ProjectInstanceSettings.Immutable) == ProjectInstanceSettings.Immutable;
            this.CreatePropertiesSnapshot(project.Properties, immutable);
            this.CreateItemDefinitionsSnapshot(project.ItemDefinitions);

            var keepEvaluationCache = (settings & ProjectInstanceSettings.ImmutableWithFastItemLookup) == ProjectInstanceSettings.ImmutableWithFastItemLookup;
            var projectItemToInstanceMap = this.CreateItemsSnapshot(project.Items, project.ItemTypes.Count, keepEvaluationCache);

            this.CreateEvaluatedIncludeSnapshotIfRequested(keepEvaluationCache, project.Items, projectItemToInstanceMap);

            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(project.GlobalPropertiesCount);
            foreach (var property in project.GlobalPropertiesEnumerable)
            {
                _globalProperties.Set(ProjectPropertyInstance.Create(property.Key, property.Value));
            }

            this.CreateEnvironmentVariablePropertiesSnapshot(project.ProjectCollection.EnvironmentProperties);
            this.CreateTargetsSnapshot(project.Targets, null, null, null, null);
            this.CreateImportsSnapshot(project.Imports, project.ImportsIncludingDuplicates);

            this.Toolset = project.ProjectCollection.GetToolset(project.ToolsVersion);
            this.SubToolsetVersion = project.SubToolsetVersion;
            this.TaskRegistry = new TaskRegistry(Toolset, project.ProjectCollection.ProjectRootElementCache);

            this.ProjectRootElementCache = project.ProjectCollection.ProjectRootElementCache;

            this.EvaluatedItemElements = new List<ProjectItemElement>(project.Items.Count);
            foreach (var item in project.Items)
            {
                this.EvaluatedItemElements.Add(item.Xml);
            }

            _usingDifferentToolsVersionFromProjectFile = false;
            _originalProjectToolsVersion = project.ToolsVersion;
            _explicitToolsVersionSpecified = project.SubToolsetVersion != null;

            _isImmutable = immutable;
        }

        /// <summary>
        /// Creates a ProjectInstance from an immutable <see cref="Project"/>.
        /// The resulting <see cref="ProjectInstance"/> object wraps the <see cref="Project"/>
        /// object. Unlike the ProjectInstance(Project project, ProjectInstanceSettings settings)
        /// constructor, the properties and items are not cloned.
        /// </summary>
        /// <param name="linkedProject">The immutable <see cref="Project"/>.</param>
        /// <param name="fastItemLookupNeeded">Whether the fast item lookup cache is required.</param>
        private ProjectInstance(Project linkedProject, bool fastItemLookupNeeded)
        {
            ErrorUtilities.VerifyThrowInternalNull(linkedProject);

            var projectPath = linkedProject.FullPath;
            _directory = Path.GetDirectoryName(projectPath);
            _projectFileLocation = ElementLocation.Create(projectPath);
            _hostServices = linkedProject.ProjectCollection.HostServices;
            _isImmutable = true;

            EvaluationId = linkedProject.EvaluationCounter;

            // ProjectProperties
            _properties = GetImmutablePropertyDictionaryFromImmutableProject(linkedProject);

            // ProjectItemDefinitions
            _itemDefinitions = GetImmutableItemDefinitionsHashSetFromImmutableProject(linkedProject);

            // ProjectItems
            _items = GetImmutableItemsDictionaryFromImmutableProject(linkedProject, this);

            // ItemsByEvaluatedInclude
            if (fastItemLookupNeeded)
            {
                _itemsByEvaluatedInclude = new ImmutableLinkedMultiDictionaryConverter<string, ProjectItem, ProjectItemInstance>(
                                                linkedProject.GetItemsByEvaluatedInclude,
                                                item => ConvertCachedProjectItemToInstance(linkedProject, this, item));
            }

            // GlobalProperties
            var globalPropertiesRetrievableHashSet = new ImmutableGlobalPropertiesCollectionConverter(linkedProject.GlobalProperties, _properties);
            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(globalPropertiesRetrievableHashSet);

            // EnvironmentVariableProperties
            _environmentVariableProperties = linkedProject.ProjectCollection.SharedReadOnlyEnvironmentProperties;

            // Targets
            _targets = linkedProject.Targets;
            InitializeTargetsData(null, null, null, null);

            // Imports
            var importsListConverter = new ImmutableStringValuedListConverter<ResolvedImport>(linkedProject.Imports, GetImportFullPath);
            _importPaths = importsListConverter;
            ImportPaths = importsListConverter;

            importsListConverter = new ImmutableStringValuedListConverter<ResolvedImport>(linkedProject.ImportsIncludingDuplicates, GetImportFullPath);
            _importPathsIncludingDuplicates = importsListConverter;
            ImportPathsIncludingDuplicates = importsListConverter;

            Toolset = linkedProject.ProjectCollection.GetToolset(linkedProject.ToolsVersion);
            SubToolsetVersion = linkedProject.SubToolsetVersion;
            TaskRegistry = new TaskRegistry(Toolset, linkedProject.ProjectCollection.ProjectRootElementCache);

            ProjectRootElementCache = linkedProject.ProjectCollection.ProjectRootElementCache;

            EvaluatedItemElements = new List<ProjectItemElement>(linkedProject.Items.Count);
            foreach (var item in linkedProject.Items)
            {
                EvaluatedItemElements.Add(item.Xml);
            }

            _usingDifferentToolsVersionFromProjectFile = false;
            _originalProjectToolsVersion = linkedProject.ToolsVersion;
            _explicitToolsVersionSpecified = linkedProject.SubToolsetVersion != null;

            _isImmutable = true;
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Sub-toolset version may be null, but if specified will override all other methods of determining the sub-toolset.
        /// </summary>
        /// <param name="xml">The project root element</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="subToolsetVersion">The sub-toolset version, used in tandem with the ToolsVersion to determine the set of toolset properties.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <param name="projectLoadSettings">Project load settings</param>
        /// <param name="evaluationContext">The context to use for evaluation.</param>
        /// <param name="directoryCacheFactory">The directory cache factory to use for file I/O.</param>
        /// <param name="interactive">Indicates if loading the project is allowed to interact with the user.</param>
        /// <returns>A new project instance</returns>
        private ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection,
            ProjectLoadSettings? projectLoadSettings, EvaluationContext evaluationContext, IDirectoryCacheFactory directoryCacheFactory, bool interactive)
        {
            BuildEventContext buildEventContext = new BuildEventContext(0, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);

            BuildParameters buildParameters = new BuildParameters(projectCollection)
            {
                Interactive = interactive
            };

            Initialize(xml, globalProperties, toolsVersion, subToolsetVersion, 0 /* no solution version specified */, buildParameters, projectCollection.LoggingService, buildEventContext,
                projectLoadSettings: projectLoadSettings, evaluationContext: evaluationContext, directoryCacheFactory: directoryCacheFactory);
        }

        /// <summary>
        /// Creates a ProjectInstance directly.  Used to generate solution metaprojects.
        /// </summary>
        /// <param name="projectFile">The full path to give to this project.</param>
        /// <param name="projectToInheritFrom">The traversal project from which global properties and tools version will be inherited.</param>
        /// <param name="globalProperties">An <see cref="IDictionary{String,String}"/> containing global properties.</param>
        internal ProjectInstance(string projectFile, ProjectInstance projectToInheritFrom, IDictionary<string, string> globalProperties)
        {
            _projectFileLocation = ElementLocation.Create(projectFile);
            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(globalProperties.Count);
            this.Toolset = projectToInheritFrom.Toolset;
            this.SubToolsetVersion = projectToInheritFrom.SubToolsetVersion;
            _explicitToolsVersionSpecified = projectToInheritFrom._explicitToolsVersionSpecified;
            _properties = new PropertyDictionary<ProjectPropertyInstance>(projectToInheritFrom._properties); // This brings along the reserved properties, which are important.
            _items = new ItemDictionary<ProjectItemInstance>(); // We don't want any of the items.  That would include things like ProjectReferences, which would just pollute our own.
            _actualTargets = new RetrievableEntryHashSet<ProjectTargetInstance>(StringComparer.OrdinalIgnoreCase);
            _targets = new ObjectModel.ReadOnlyDictionary<string, ProjectTargetInstance>(_actualTargets);
            _environmentVariableProperties = projectToInheritFrom._environmentVariableProperties;
            _itemDefinitions = new RetrievableEntryHashSet<ProjectItemDefinitionInstance>(projectToInheritFrom._itemDefinitions, MSBuildNameIgnoreCaseComparer.Default);
            _hostServices = projectToInheritFrom._hostServices;
            this.ProjectRootElementCache = projectToInheritFrom.ProjectRootElementCache;
            _explicitToolsVersionSpecified = projectToInheritFrom._explicitToolsVersionSpecified;
            this.InitialTargets = new List<string>();
            this.DefaultTargets = new List<string>();
            this.DefaultTargets.Add("Build");
            this.TaskRegistry = projectToInheritFrom.TaskRegistry;
            _isImmutable = projectToInheritFrom._isImmutable;
            _importPaths = projectToInheritFrom._importPaths;
            ImportPaths = new ObjectModel.ReadOnlyCollection<string>(_importPaths);
            _importPathsIncludingDuplicates = projectToInheritFrom._importPathsIncludingDuplicates;
            ImportPathsIncludingDuplicates = new ObjectModel.ReadOnlyCollection<string>(_importPathsIncludingDuplicates);

            this.EvaluatedItemElements = new List<ProjectItemElement>();

            IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance> thisAsIEvaluatorData = this;
            thisAsIEvaluatorData.AfterTargets = new Dictionary<string, List<TargetSpecification>>();
            thisAsIEvaluatorData.BeforeTargets = new Dictionary<string, List<TargetSpecification>>();

            foreach (KeyValuePair<string, string> property in globalProperties)
            {
                _globalProperties[property.Key] = ProjectPropertyInstance.Create(property.Key, property.Value, false /* may not be reserved */, _isImmutable);
            }
        }

        /// <summary>
        /// Creates a ProjectInstance directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Used by SolutionProjectGenerator so that it can explicitly pass the vsVersionFromSolution in for use in
        /// determining the sub-toolset version.
        /// </summary>
        /// <param name="xml">The project root element</param>
        /// <param name="globalProperties">The global properties to use.</param>
        /// <param name="toolsVersion">The tools version.</param>
        /// <param name="visualStudioVersionFromSolution">The version of the solution, used to help determine which sub-toolset to use.</param>
        /// <param name="projectCollection">Project collection</param>
        /// <param name="sdkResolverService">An <see cref="ISdkResolverService"/> instance to use when resolving SDKs.</param>
        /// <param name="submissionId">The current build submission ID.</param>
        /// <returns>A new project instance</returns>
        internal ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, int visualStudioVersionFromSolution, ProjectCollection projectCollection, ISdkResolverService sdkResolverService, int submissionId)
        {
            BuildEventContext buildEventContext = new BuildEventContext(0, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);
            Initialize(xml, globalProperties, toolsVersion, null, visualStudioVersionFromSolution, new BuildParameters(projectCollection), projectCollection.LoggingService, buildEventContext, sdkResolverService, submissionId);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProjectInstance"/> class directly.
        /// No intermediate Project object is created.
        /// This is ideal if the project is simply going to be built, and not displayed or edited.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Used by SolutionProjectGenerator so that it can explicitly pass the vsVersionFromSolution in for use in
        /// determining the sub-toolset version.
        /// </summary>
        internal ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, ILoggingService loggingService, int visualStudioVersionFromSolution, ProjectCollection projectCollection, ISdkResolverService sdkResolverService, int submissionId)
        {
            BuildEventContext buildEventContext = new BuildEventContext(submissionId, 0, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
            Initialize(xml, globalProperties, toolsVersion, null, visualStudioVersionFromSolution, new BuildParameters(projectCollection), loggingService, buildEventContext, sdkResolverService, submissionId);
        }

        /// <summary>
        /// Creates a mutable ProjectInstance directly, using the specified logging service.
        /// Assumes the project path is already normalized.
        /// Used by the RequestBuilder.
        /// </summary>
        internal ProjectInstance(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, BuildParameters buildParameters, ILoggingService loggingService, BuildEventContext buildEventContext, ISdkResolverService sdkResolverService, int submissionId, ProjectLoadSettings? projectLoadSettings)
        {
            ErrorUtilities.VerifyThrowArgumentLength(projectFile);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(buildParameters);

            ProjectRootElement xml = ProjectRootElement.OpenProjectOrSolution(projectFile, globalProperties, toolsVersion, buildParameters.ProjectRootElementCache, false /*Not explicitly loaded*/);

            Initialize(xml, globalProperties, toolsVersion, null, 0 /* no solution version specified */, buildParameters, loggingService, buildEventContext, sdkResolverService, submissionId, projectLoadSettings);
        }

        /// <summary>
        /// Creates a mutable ProjectInstance directly, using the specified logging service.
        /// Assumes the project path is already normalized.
        /// Used by this class when generating legacy solution wrappers.
        /// </summary>
        internal ProjectInstance(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, BuildParameters buildParameters, ILoggingService loggingService, BuildEventContext buildEventContext, ISdkResolverService sdkResolverService, int submissionId)
        {
            ErrorUtilities.VerifyThrowArgumentNull(xml);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(buildParameters);
            Initialize(xml, globalProperties, toolsVersion, null, 0 /* no solution version specified */, buildParameters, loggingService, buildEventContext, sdkResolverService, submissionId);
        }

        /// <summary>
        /// Constructor called by Project's constructor to create a fresh instance.
        /// Properties and items are cloned immediately and only the instance data is stored.
        /// </summary>
        internal ProjectInstance(Evaluation.Project.Data data, string directory, string fullPath, HostServices hostServices, PropertyDictionary<ProjectPropertyInstance> environmentVariableProperties, ProjectInstanceSettings settings)
        {
            ErrorUtilities.VerifyThrowInternalNull(data);
            ErrorUtilities.VerifyThrowInternalLength(directory, nameof(directory));
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(fullPath, nameof(fullPath));

            _directory = directory;
            _projectFileLocation = ElementLocation.Create(fullPath);
            _hostServices = hostServices;

            EvaluationId = data.EvaluationId;

            var immutable = (settings & ProjectInstanceSettings.Immutable) == ProjectInstanceSettings.Immutable;
            this.CreatePropertiesSnapshot(new ReadOnlyCollection<ProjectProperty>(data.Properties), immutable);

            this.CreateItemDefinitionsSnapshot(data.ItemDefinitions);

            var keepEvaluationCache = (settings & ProjectInstanceSettings.ImmutableWithFastItemLookup) == ProjectInstanceSettings.ImmutableWithFastItemLookup;
            var projectItemToInstanceMap = this.CreateItemsSnapshot(new ReadOnlyCollection<ProjectItem>(data.Items), data.ItemTypes.Count, keepEvaluationCache);

            this.CreateEvaluatedIncludeSnapshotIfRequested(keepEvaluationCache, new ReadOnlyCollection<ProjectItem>(data.Items), projectItemToInstanceMap);
            this.CreateGlobalPropertiesSnapshot(data.GlobalPropertiesDictionary);
            this.CreateEnvironmentVariablePropertiesSnapshot(environmentVariableProperties);
            this.CreateTargetsSnapshot(data.Targets, data.DefaultTargets, data.InitialTargets, data.BeforeTargets, data.AfterTargets);
            this.CreateImportsSnapshot(data.ImportClosure, data.ImportClosureWithDuplicates);

            // Toolset and task registry are logically immutable after creation, and shareable by project instances
            //  with same evaluation (global/local properties) - which is guaranteed here (the passed in data is recreated on evaluation if needed)
            this.Toolset = data.Toolset;
            this.SubToolsetVersion = data.SubToolsetVersion;
            this.TaskRegistry = data.TaskRegistry;

            this.ProjectRootElementCache = data.Project.ProjectCollection.ProjectRootElementCache;

            this.EvaluatedItemElements = new List<ProjectItemElement>(data.EvaluatedItemElements);

            _usingDifferentToolsVersionFromProjectFile = data.UsingDifferentToolsVersionFromProjectFile;
            _originalProjectToolsVersion = data.OriginalProjectToolsVersion;
            _explicitToolsVersionSpecified = data.ExplicitToolsVersion != null;

            _isImmutable = immutable;
        }

        /// <summary>
        /// Constructor for deserialization.
        /// </summary>
        private ProjectInstance(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);
        }

        /// <summary>
        /// Deep clone of this object.
        /// Useful for compiling a single file; or for keeping resolved assembly references between builds.
        /// </summary>
        private ProjectInstance(ProjectInstance that, bool isImmutable, RequestedProjectState filter = null)
        {
            ErrorUtilities.VerifyThrow(filter == null || isImmutable,
                "The result of a filtered ProjectInstance clone must be immutable.");

            _directory = that._directory;
            _projectFileLocation = that._projectFileLocation;
            _hostServices = that._hostServices;
            _isImmutable = isImmutable;
            _evaluationId = that.EvaluationId;
            _translateEntireState = that._translateEntireState;
            _requestedProjectStateFilter = filter?.DeepClone();

            if (filter == null)
            {
                _properties = new PropertyDictionary<ProjectPropertyInstance>(that._properties.Count);

                foreach (ProjectPropertyInstance property in that.Properties)
                {
                    _properties.Set(property.DeepClone(_isImmutable));
                }

                _items = new ItemDictionary<ProjectItemInstance>(that._items.Count);

                foreach (ProjectItemInstance item in that.Items)
                {
                    _items.Add(item.DeepClone(this));
                }

                _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(that._globalProperties.Count);

                foreach (ProjectPropertyInstance globalProperty in that.GlobalPropertiesDictionary)
                {
                    _globalProperties.Set(globalProperty.DeepClone(_isImmutable));
                }

                _environmentVariableProperties =
                    new PropertyDictionary<ProjectPropertyInstance>(that._environmentVariableProperties.Count);

                foreach (ProjectPropertyInstance environmentProperty in that._environmentVariableProperties)
                {
                    _environmentVariableProperties.Set(environmentProperty.DeepClone(_isImmutable));
                }

                this.DefaultTargets = new List<string>(that.DefaultTargets);
                this.InitialTargets = new List<string>(that.InitialTargets);
                ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance,
                    ProjectItemDefinitionInstance>)this).BeforeTargets = CreateCloneDictionary(
                    ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance,
                        ProjectItemDefinitionInstance>)that).BeforeTargets, StringComparer.OrdinalIgnoreCase);
                ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance,
                    ProjectItemDefinitionInstance>)this).AfterTargets = CreateCloneDictionary(
                    ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance,
                        ProjectItemDefinitionInstance>)that).AfterTargets, StringComparer.OrdinalIgnoreCase);
                // These are immutable (or logically immutable after creation) so we don't need to clone them:
                this.TaskRegistry = that.TaskRegistry;
                this.Toolset = that.Toolset;
                this.SubToolsetVersion = that.SubToolsetVersion;
                _targets = that._targets;
                _itemDefinitions = that._itemDefinitions;
                _explicitToolsVersionSpecified = that._explicitToolsVersionSpecified;
                _importPaths = that._importPaths;
                ImportPaths = new ObjectModel.ReadOnlyCollection<string>(_importPaths);
                _importPathsIncludingDuplicates = that._importPathsIncludingDuplicates;
                ImportPathsIncludingDuplicates = new ObjectModel.ReadOnlyCollection<string>(_importPathsIncludingDuplicates);

                this.EvaluatedItemElements = that.EvaluatedItemElements;

                this.ProjectRootElementCache = that.ProjectRootElementCache;
            }
            else
            {
                if (filter.PropertyFilters != null)
                {
                    // If PropertyFilters is defined, filter all types of property to contain
                    // only those explicitly specified.

                    // Reserve space assuming all specified properties exist.
                    _properties = new PropertyDictionary<ProjectPropertyInstance>(filter.PropertyFilters.Count);
                    _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(filter.PropertyFilters.Count);
                    _environmentVariableProperties =
                        new PropertyDictionary<ProjectPropertyInstance>(filter.PropertyFilters.Count);

                    // Filter each type of property.
                    foreach (var desiredProperty in filter.PropertyFilters)
                    {
                        var regularProperty = that.GetProperty(desiredProperty);
                        if (regularProperty != null)
                        {
                            _properties.Set(regularProperty.DeepClone(isImmutable: true));
                        }

                        var globalProperty = that.GetProperty(desiredProperty);
                        if (globalProperty != null)
                        {
                            _globalProperties.Set(globalProperty.DeepClone(isImmutable: true));
                        }

                        var environmentProperty = that.GetProperty(desiredProperty);
                        if (environmentProperty != null)
                        {
                            _environmentVariableProperties.Set(environmentProperty.DeepClone(isImmutable: true));
                        }
                    }
                }

                if (filter.ItemFilters != null)
                {
                    // If ItemFilters is defined, filter items down to the list
                    // specified, optionally also filtering metadata.

                    // Temporarily allow editing items to remove metadata that
                    // wasn't explicitly asked for.
                    _isImmutable = false;

                    _items = new ItemDictionary<ProjectItemInstance>(that.Items.Count);

                    foreach (var itemFilter in filter.ItemFilters)
                    {
                        foreach (var actualItem in that.GetItems(itemFilter.Key))
                        {
                            var filteredItem = actualItem.DeepClone(this);

                            if (itemFilter.Value == null)
                            {
                                // No specified list of metadata names, so include all metadata.
                                // The returned list of items is still filtered by item name.
                            }
                            else
                            {
                                // Include only the explicitly-asked-for metadata by removing
                                // any extant metadata.
                                // UNDONE: This could be achieved at lower GC cost by applying
                                // the metadata filter at DeepClone time above.
                                foreach (var metadataName in filteredItem.MetadataNames)
                                {
                                    if (!itemFilter.Value.Contains(metadataName, StringComparer.OrdinalIgnoreCase))
                                    {
                                        filteredItem.RemoveMetadata(metadataName);
                                    }
                                }
                            }

                            _items.Add(filteredItem);
                        }
                    }

                    // Restore immutability after editing newly cloned items.
                    _isImmutable = isImmutable;

                    // A filtered result is not useful for building anyway; ensure that
                    // it has minimal IPC wire cost.
                    _translateEntireState = false;
                }
            }
        }

        /// <summary>
        /// Create a file based ProjectInstance.
        /// </summary>
        /// <param name="file">The file to evaluate the ProjectInstance from.</param>
        /// <param name="options">The <see cref="ProjectOptions"/> to use.</param>
        /// <returns></returns>
        public static ProjectInstance FromFile(string file, ProjectOptions options)
        {
            return new ProjectInstance(
                file,
                options.GlobalProperties,
                options.ToolsVersion,
                options.SubToolsetVersion,
                options.ProjectCollection ?? ProjectCollection.GlobalProjectCollection,
                options.LoadSettings,
                options.EvaluationContext,
                options.DirectoryCacheFactory,
                options.Interactive);
        }

        /// <summary>
        /// Create a <see cref="ProjectRootElement"/> based ProjectInstance.
        /// </summary>
        /// <param name="rootElement">The <see cref="ProjectRootElement"/> to evaluate the ProjectInstance from.</param>
        /// <param name="options">The <see cref="ProjectOptions"/> to use.</param>
        public static ProjectInstance FromProjectRootElement(ProjectRootElement rootElement, ProjectOptions options)
        {
            return new ProjectInstance(
                rootElement,
                options.GlobalProperties,
                options.ToolsVersion,
                options.SubToolsetVersion,
                options.ProjectCollection ?? ProjectCollection.GlobalProjectCollection,
                options.LoadSettings,
                options.EvaluationContext,
                options.DirectoryCacheFactory,
                options.Interactive);
        }

        /// <summary>
        /// Create a ProjectInstance from an immutable project source.
        /// </summary>
        /// <param name="project">The immutable <see cref="Project"/> on which the ProjectInstance is based.</param>
        /// <param name="settings">The <see cref="ProjectInstanceSettings"/> to use.</param>
        public static ProjectInstance FromImmutableProjectSource(Project project, ProjectInstanceSettings settings)
        {
            bool fastItemLookupNeeded = settings.HasFlag(ProjectInstanceSettings.ImmutableWithFastItemLookup);
            return new ProjectInstance(project, fastItemLookupNeeded);
        }

        private static IRetrievableEntryHashSet<ProjectItemDefinitionInstance> GetImmutableItemDefinitionsHashSetFromImmutableProject(Project linkedProject)
        {
            IDictionary<string, ProjectItemDefinition> linkedProjectItemDefinitions = linkedProject.ItemDefinitions;
            VerifyCollectionImplementsRequiredDictionaryInterfaces(
                linkedProjectItemDefinitions,
                out IDictionary<string, ProjectItemDefinition> elementsDictionary,
                out IDictionary<(string, int, int), ProjectItemDefinition> constrainedElementsDictionary);

            var hashSet = new ImmutableElementCollectionConverter<ProjectItemDefinition, ProjectItemDefinitionInstance>(
                                elementsDictionary,
                                constrainedElementsDictionary,
                                ConvertCachedItemDefinitionToInstance);

            return hashSet;
        }

        private static ImmutableItemDictionary<ProjectItem, ProjectItemInstance> GetImmutableItemsDictionaryFromImmutableProject(
            Project linkedProject,
            ProjectInstance owningProjectInstance)
        {
            var itemsByType = linkedProject.Items as IDictionary<string, ICollection<ProjectItem>>;
            if (itemsByType == null)
            {
                throw new ArgumentException(nameof(linkedProject));
            }

            Func<ProjectItem, ProjectItemInstance> convertCachedItemToInstance =
                projectItem => ConvertCachedProjectItemToInstance(linkedProject, owningProjectInstance, projectItem);

            var itemDictionary = new ImmutableItemDictionary<ProjectItem, ProjectItemInstance>(
                linkedProject.Items,
                itemsByType,
                convertCachedItemToInstance,
                projectItemInstance => projectItemInstance.ItemType);

            return itemDictionary;
        }

        private static ProjectItemInstance ConvertCachedProjectItemToInstance(
            Project linkedProject,
            ProjectInstance owningProjectInstance,
            ProjectItem projectItem)
        {
            ProjectItemInstance result = null;
            if (projectItem is IImmutableInstanceProvider<ProjectItemInstance> instanceProvider)
            {
                result = instanceProvider.ImmutableInstance;
                if (result == null)
                {
                    var newInstance = InstantiateProjectItemInstanceFromImmutableProjectSource(
                        linkedProject,
                        owningProjectInstance,
                        projectItem);

                    result = instanceProvider.GetOrSetImmutableInstance(newInstance);
                }
            }

            return result;
        }

        private static ProjectItemDefinitionInstance ConvertCachedItemDefinitionToInstance(ProjectItemDefinition projectItemDefinition)
        {
            ProjectItemDefinitionInstance result = null;

            if (projectItemDefinition is IImmutableInstanceProvider<ProjectItemDefinitionInstance> instanceProvider)
            {
                result = instanceProvider.ImmutableInstance;
                if (result == null)
                {
                    IDictionary<string, ProjectMetadataInstance> metadata = null;
                    if (projectItemDefinition.Metadata is IDictionary<string, ProjectMetadata> linkedMetadataDict)
                    {
                        metadata = new ImmutableElementCollectionConverter<ProjectMetadata, ProjectMetadataInstance>(
                                        linkedMetadataDict,
                                        constrainedProjectElements: null,
                                        ConvertCachedProjectMetadataToInstance);
                    }

                    result = instanceProvider.GetOrSetImmutableInstance(
                        new ProjectItemDefinitionInstance(projectItemDefinition.ItemType, metadata));
                }
            }

            return result;
        }

        private static ProjectMetadataInstance ConvertCachedProjectMetadataToInstance(ProjectMetadata projectMetadata)
        {
            ProjectMetadataInstance result = null;

            if (projectMetadata is IImmutableInstanceProvider<ProjectMetadataInstance> instanceProvider)
            {
                result = instanceProvider.ImmutableInstance;
                if (result == null)
                {
                    result = instanceProvider.GetOrSetImmutableInstance(new ProjectMetadataInstance(projectMetadata));
                }
            }

            return result;
        }

        private static PropertyDictionary<ProjectPropertyInstance> GetImmutablePropertyDictionaryFromImmutableProject(Project linkedProject)
        {
            ICollection<ProjectProperty> linkedProjectProperties = linkedProject.Properties;
            VerifyCollectionImplementsRequiredDictionaryInterfaces(
                linkedProjectProperties,
                out IDictionary<string, ProjectProperty> elementsDictionary,
                out IDictionary<(string, int, int), ProjectProperty> constrainedElementsDictionary);

            var hashSet = new ImmutableValuedElementCollectionConverter<ProjectProperty, ProjectPropertyInstance>(
                                elementsDictionary,
                                constrainedElementsDictionary,
                                ConvertCachedPropertyToInstance);

            return new PropertyDictionary<ProjectPropertyInstance>(hashSet);
        }

        private static ProjectPropertyInstance ConvertCachedPropertyToInstance(ProjectProperty property)
        {
            ProjectPropertyInstance result = null;

            if (property is IImmutableInstanceProvider<ProjectPropertyInstance> instanceProvider)
            {
                result = instanceProvider.ImmutableInstance;
                if (result == null)
                {
                    result = instanceProvider.GetOrSetImmutableInstance(InstantiateProjectPropertyInstance(property, isImmutable: true));
                }
            }

            return result;
        }

        private static void VerifyCollectionImplementsRequiredDictionaryInterfaces<TCached>(
            object elementsCollection,
            out IDictionary<string, TCached> elementsDictionary,
            out IDictionary<(string, int, int), TCached> constrainedElementsDictionary)
        {
            // The elementsCollection we receive here is implemented in CPS as a special collection
            // that is both IDictionary<string, TCached> and also IDictionary<(string, int, int), TCached>.
            // This allows it to represent the fundamental operations of an IRetrievableEntryHashSet.
            // The IDictionary<(string, int, int), TCached> interface is used to handle the
            // IRetrievableEntryHashSet's Get(string key, int index, int length) method. Here we take
            // elementsCollection and put it into an ImmutableElementCollectionConverter, which
            // represents the elementsCollection as an IRetrievableEntryHashSet<T>.
            // That IRetrievableEntryHashSet is then used either directly or as a backing source for
            // another collection wrapper (e.g. PropertyDictionary).
            if (elementsCollection is not IDictionary<string, TCached> elementsDict ||
                elementsCollection is not IDictionary<(string, int, int), TCached> constrainedElementsDict)
            {
                throw new ArgumentException(nameof(elementsCollection));
            }

            elementsDictionary = elementsDict;
            constrainedElementsDictionary = constrainedElementsDict;
        }

        /// <summary>
        /// Global properties this project was evaluated with, if any.
        /// Read only collection.
        /// Traverses project references.
        /// </summary>
        /// <remarks>
        /// This is the publicly exposed getter, that translates into a read-only dead IDictionary&lt;string, string&gt;.
        /// </remarks>
        public IDictionary<string, string> GlobalProperties
        {
            [DebuggerStepThrough]
            get
            {
                if (_globalProperties == null /* cached */ || _globalProperties.Count == 0)
                {
                    return ReadOnlyEmptyDictionary<string, string>.Instance;
                }

                Dictionary<string, string> dictionary = new Dictionary<string, string>(_globalProperties.Count, MSBuildNameIgnoreCaseComparer.Default);

                foreach (ProjectPropertyInstance property in _globalProperties)
                {
                    dictionary[property.Name] = ((IProperty)property).EvaluatedValueEscaped;
                }

                return new ObjectModel.ReadOnlyDictionary<string, string>(dictionary);
            }
        }

        /// <summary>
        /// The tools version this project was evaluated with, if any.
        /// Not necessarily the same as the tools version on the Project tag, if any;
        /// it may have been externally specified, for example with a /tv switch.
        /// </summary>
        public string ToolsVersion
        {
            get { return Toolset.ToolsVersion; }
        }

        /// <summary>
        /// Enumerator over item types of the items in this project
        /// </summary>
        public ICollection<string> ItemTypes
        {
            [DebuggerStepThrough]
            get
            {
                // KeyCollection, which is already read-only
                return _items.ItemTypes;
            }
        }

        bool IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.CanEvaluateElementsWithFalseConditions => false;

        /// <summary>
        /// Enumerator over properties in this project
        /// </summary>
        public ICollection<ProjectPropertyInstance> Properties
        {
            [DebuggerStepThrough]
            get
            {
                return (_properties == null) ?
                    (ICollection<ProjectPropertyInstance>)ReadOnlyEmptyCollection<ProjectPropertyInstance>.Instance :
                    new ReadOnlyCollection<ProjectPropertyInstance>(_properties);
            }
        }

        /// <summary>
        /// Enumerator over items in this project.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public ICollection<ProjectItemInstance> Items
        {
            [DebuggerStepThrough]
            get
            {
                return (_items == null) ?
                    (ICollection<ProjectItemInstance>)ReadOnlyEmptyCollection<ProjectItemInstance>.Instance :
                    new ReadOnlyCollection<ProjectItemInstance>(_items);
            }
        }

        /// <summary>
        /// Gets a <see cref="List{ProjectItemElement}"/> object containing evaluated items.
        /// </summary>
        public List<ProjectItemElement> EvaluatedItemElements
        {
            get;
            private set;
        }

        /// <summary>
        /// Serialize the entire project instance state.
        ///
        /// When false, only a part of the project instance state is serialized (properties and items).
        /// In this case out of proc nodes re-evaluate the project instance from disk to obtain the un-serialized state.
        /// This partial state recombination may lead to build issues when the project instance state differs from what is on disk.
        /// </summary>
        public bool TranslateEntireState
        {
            get => _translateEntireState;
            set => _translateEntireState = value;
        }

        /// <summary>
        /// The ID of the evaluation that produced this ProjectInstance.
        ///
        /// See <see cref="Project.LastEvaluationId"/>.
        /// </summary>
        public int EvaluationId
        {
            get { return _evaluationId; }
            set { _evaluationId = value; }
        }

        /// <summary>
        /// The project's root directory, for evaluation of relative paths and
        /// setting the current directory during build.
        /// Is never null: projects not loaded from disk use the current directory from
        /// the time the build started.
        /// </summary>
        public string Directory
        {
            [DebuggerStepThrough]
            get
            { return _directory; }
        }

        /// <summary>
        /// The full path to the project, for logging.
        /// If the project was never given a path, returns empty string.
        /// </summary>
        public string FullPath
        {
            [DebuggerStepThrough]
            get => _projectFileLocation?.File ?? string.Empty;
        }

        /// <summary>
        /// Read-only dictionary of item definitions in this project.
        /// Keyed by item type
        /// </summary>
        public IDictionary<string, ProjectItemDefinitionInstance> ItemDefinitions
        {
            [DebuggerStepThrough]
            get
            { return _itemDefinitions; }
        }

        /// <summary>
        /// The full file paths of all the files that during evaluation contributed to this project instance.
        /// This does not include projects that were never imported because a condition on an Import element was false.
        /// The outer ProjectRootElement that maps to this project instance itself is not included.
        /// </summary>
        public IReadOnlyList<string> ImportPaths { get; private set; }

        /// <summary>
        /// This list will contain duplicate imports if an import is imported multiple times. However, only the first import was used in evaluation.
        /// </summary>
        public IReadOnlyList<string> ImportPathsIncludingDuplicates { get; private set; }

        /// <summary>
        /// DefaultTargets specified in the project, or
        /// the logically first target if no DefaultTargets is
        /// specified in the project.
        /// The build builds these if no targets are explicitly specified
        /// to build.
        /// </summary>
        public List<string> DefaultTargets
        {
            get { return _defaultTargets; }
            private set { _defaultTargets = value; }
        }

        /// <summary>
        /// InitialTargets specified in the project, plus those
        /// in all imports, gathered depth-first.
        /// The build runs these before anything else.
        /// </summary>
        public List<string> InitialTargets
        {
            get { return _initialTargets; }
            private set { _initialTargets = value; }
        }

        /// <summary>
        /// Targets in the project. The build process can find one by looking for its name
        /// in the dictionary.
        /// This collection is read-only.
        /// </summary>
        public IDictionary<string, ProjectTargetInstance> Targets
        {
            [DebuggerStepThrough]
            get
            { return _targets; }
        }

        /// <summary>
        /// Whether the instance is immutable.
        /// This is set permanently when the instance is created.
        /// </summary>
        public bool IsImmutable
        {
            get { return _isImmutable; }
        }

        /// <summary>
        /// The property and item filter used when creating this instance, or null if this is not a filtered copy
        /// of another ProjectInstance. <seealso cref="ProjectInstance(ProjectInstance, bool, RequestedProjectState)"/>
        /// </summary>
        internal RequestedProjectState RequestedProjectStateFilter => _requestedProjectStateFilter;

        /// <summary>
        /// Task classes and locations known to this project.
        /// This is the project-specific task registry, which is consulted before
        /// the toolset's task registry.
        /// Only set during evaluation, so does not check for immutability.
        /// </summary>
        TaskRegistry IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.TaskRegistry
        {
            [DebuggerStepThrough]
            get
            {
                return TaskRegistry;
            }

            set
            {
                TaskRegistry = value;
            }
        }

        /// <summary>
        /// Gets the Toolset
        /// </summary>
        Toolset IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.Toolset
        {
            [DebuggerStepThrough]
            get
            { return Toolset; }
        }

        /// <summary>
        /// The sub-toolset version we should use during the build, used to determine which set of sub-toolset
        /// properties we should merge into this toolset.
        /// </summary>
        string IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.SubToolsetVersion
        {
            [DebuggerStepThrough]
            get
            { return SubToolsetVersion; }
        }

        /// <summary>
        /// The externally specified tools version, if any.
        /// For example, the tools version from a /tv switch.
        /// Not necessarily the same as the tools version from the project tag or of the toolset used.
        /// May be null.
        /// Flows through to called projects.
        /// </summary>
        string IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.ExplicitToolsVersion
        {
            [DebuggerStepThrough]
            get
            { return ExplicitToolsVersion; }
        }

        /// <summary>
        /// Gets the global properties
        /// </summary>
        PropertyDictionary<ProjectPropertyInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.GlobalPropertiesDictionary
        {
            [DebuggerStepThrough]
            get
            { return _globalProperties; }
        }

        PropertyDictionary<ProjectPropertyInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.EnvironmentVariablePropertiesDictionary
        {
            get => _environmentVariableProperties;
        }

        /// <summary>
        /// List of names of the properties that, while global, are still treated as overridable
        /// </summary>
        ISet<string> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.GlobalPropertiesToTreatAsLocal
        {
            get
            {
                if (_globalPropertiesToTreatAsLocal == null)
                {
                    _globalPropertiesToTreatAsLocal = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
                }

                return _globalPropertiesToTreatAsLocal;
            }
        }

        /// <summary>
        /// Gets the properties
        /// </summary>
        PropertyDictionary<ProjectPropertyInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.Properties
        {
            [DebuggerStepThrough]
            get
            { return _properties; }
        }

        /// <summary>
        /// Gets the item definitions
        /// </summary>
        IEnumerable<ProjectItemDefinitionInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.ItemDefinitionsEnumerable
        {
            [DebuggerStepThrough]
            get
            { return _itemDefinitions.Values; }
        }

        /// <summary>
        /// Gets the items
        /// </summary>
        IItemDictionary<ProjectItemInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.Items
        {
            [DebuggerStepThrough]
            get
            { return _items; }
        }

        /// <summary>
        /// Sets the initial targets
        /// Only set during evaluation, so does not check for immutability.
        /// </summary>
        List<string> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.InitialTargets
        {
            [DebuggerStepThrough]
            get
            {
                return InitialTargets;
            }

            set
            {
                InitialTargets = value;
            }
        }

        /// <summary>
        /// Gets or sets the default targets
        /// Only set during evaluation, so does not check for immutability.
        /// </summary>
        List<string> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.DefaultTargets
        {
            [DebuggerStepThrough]
            get
            {
                return DefaultTargets;
            }

            set
            {
                DefaultTargets = value;
            }
        }

        /// <summary>
        /// Gets or sets the before targets
        /// Only set during evaluation, so does not check for immutability.
        /// </summary>
        IDictionary<string, List<TargetSpecification>> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.BeforeTargets
        {
            get { return _beforeTargets; }
            set { _beforeTargets = value; }
        }

        /// <summary>
        /// Gets or sets the after targets
        /// Only set during evaluation, so does not check for immutability.
        /// </summary>
        IDictionary<string, List<TargetSpecification>> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AfterTargets
        {
            get { return _afterTargets; }
            set { _afterTargets = value; }
        }

        /// <summary>
        /// List of possible values for properties inferred from certain conditions,
        /// keyed by the property name.
        /// </summary>
        /// <remarks>
        /// Because ShouldEvaluateForDesignTime returns false, this should not be called.
        /// </remarks>
        Dictionary<string, List<string>> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.ConditionedProperties
        {
            get
            {
                ErrorUtilities.ThrowInternalErrorUnreachable();
                return null;
            }
        }

        /// <summary>
        /// Whether evaluation should collect items ignoring condition,
        /// as well as items respecting condition; and collect
        /// conditioned properties, as well as regular properties
        /// </summary>
        bool IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.ShouldEvaluateForDesignTime
        {
            get { return false; }
        }

        /// <summary>
        /// Location of the originating file itself, not any specific content within it.
        /// Never returns null, even if the file has not got a path yet.
        /// </summary>
        public ElementLocation ProjectFileLocation
        {
            get { return _projectFileLocation; }
        }

        /// <summary>
        /// Gets the global properties this project was evaluated with, if any.
        /// Traverses project references.
        /// </summary>
        internal PropertyDictionary<ProjectPropertyInstance> GlobalPropertiesDictionary
        {
            [DebuggerStepThrough]
            get
            { return _globalProperties; }
        }

        /// <summary>
        /// The tools version we should use during the build, used to determine which toolset we should access.
        /// </summary>
        internal Toolset Toolset
        {
            get { return _toolset; }
            private set { _toolset = value; }
        }

        /// <summary>
        /// If we are treating a missing toolset as the current ToolsVersion
        /// </summary>
        internal bool UsingDifferentToolsVersionFromProjectFile
        {
            get { return _usingDifferentToolsVersionFromProjectFile; }
        }

        /// <summary>
        /// The toolsversion that was originally specified on the project's root element
        /// </summary>
        internal string OriginalProjectToolsVersion
        {
            get { return _originalProjectToolsVersion; }
        }

        /// <summary>
        /// The externally specified tools version, if any.
        /// For example, the tools version from a /tv switch.
        /// Not necessarily the same as the tools version from the project tag or of the toolset used.
        /// May be null.
        /// Flows through to called projects.
        /// </summary>
        internal string ExplicitToolsVersion
        {
            get { return _explicitToolsVersionSpecified ? Toolset.ToolsVersion : null; }
        }

        /// <summary>
        /// Whether the tools version used originated from an explicit specification,
        /// for example from an MSBuild task or /tv switch.
        /// </summary>
        internal bool ExplicitToolsVersionSpecified
        {
            get { return _explicitToolsVersionSpecified; }
        }

        /// <summary>
        /// The sub-toolset version we should use during the build, used to determine which set of sub-toolset
        /// properties we should merge into this toolset.
        /// </summary>
        internal string SubToolsetVersion
        {
            get { return _subToolsetVersion; }
            private set { _subToolsetVersion = value; }
        }

        /// <summary>
        /// Actual collection of properties in this project,
        /// for the build to start with.
        /// </summary>
        internal PropertyDictionary<ProjectPropertyInstance> PropertiesToBuildWith
        {
            [DebuggerStepThrough]
            get
            { return _properties; }
        }

        internal ICollection<ProjectPropertyInstance> TestEnvironmentalProperties => new ReadOnlyCollection<ProjectPropertyInstance>(_environmentVariableProperties);

        /// <summary>
        /// Actual collection of items in this project,
        /// for the build to start with.
        /// </summary>
        internal IItemDictionary<ProjectItemInstance> ItemsToBuildWith
        {
            [DebuggerStepThrough]
            get
            { return _items; }
        }

        /// <summary>
        /// Task classes and locations known to this project.
        /// This is the project-specific task registry, which is consulted before
        /// the toolset's task registry.
        /// </summary>
        /// <remarks>
        /// UsingTask tags have already been evaluated and entered into this task registry.
        /// </remarks>
        internal TaskRegistry TaskRegistry
        {
            get { return _taskRegistry; }
            private set { _taskRegistry = value; }
        }

        /// <summary>
        /// Number of targets in the project.
        /// </summary>
        internal int TargetsCount
        {
            get { return _targets.Count; }
        }

        /// <summary>
        /// The project root element cache from the project collection
        /// that began the build. This is a thread-safe object.
        /// It's held here so it can get passed to the build.
        /// </summary>
        internal ProjectRootElementCacheBase ProjectRootElementCache
        {
            get;
            private set;
        }

        /// <summary>
        /// Returns the evaluated, escaped value of the provided item's include.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IItem is an internal interface; this is less confusing to outside customers. ")]
        public static string GetEvaluatedItemIncludeEscaped(ProjectItemInstance item)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).EvaluatedIncludeEscaped;
        }

        /// <summary>
        /// Returns the evaluated, escaped value of the provided item definition's include.
        /// </summary>
        public static string GetEvaluatedItemIncludeEscaped(ProjectItemDefinitionInstance item)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).EvaluatedIncludeEscaped;
        }

        /// <summary>
        /// Gets the escaped value of the provided metadatum.
        /// </summary>
        public static string GetMetadataValueEscaped(ProjectMetadataInstance metadatum)
        {
            ErrorUtilities.VerifyThrowArgumentNull(metadatum);

            return metadatum.EvaluatedValueEscaped;
        }

        /// <summary>
        /// Gets the escaped value of the metadatum with the provided name on the provided item.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IItem is an internal interface; this is less confusing to outside customers. ")]
        public static string GetMetadataValueEscaped(ProjectItemInstance item, string name)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).GetMetadataValueEscaped(name);
        }

        /// <summary>
        /// Gets the escaped value of the metadatum with the provided name on the provided item definition.
        /// </summary>
        public static string GetMetadataValueEscaped(ProjectItemDefinitionInstance item, string name)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).GetMetadataValueEscaped(name);
        }

        /// <summary>
        /// Get the escaped value of the provided property
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IProperty is an internal interface; this is less confusing to outside customers. ")]
        public static string GetPropertyValueEscaped(ProjectPropertyInstance property)
        {
            ErrorUtilities.VerifyThrowArgumentNull(property);

            return ((IProperty)property).EvaluatedValueEscaped;
        }

        /// <summary>
        /// Gets items of the specified type.
        /// For internal use.
        /// </summary>
        /// <comments>
        /// Already a readonly collection
        /// </comments>
        ICollection<ProjectItemInstance> IItemProvider<ProjectItemInstance>.GetItems(string itemType)
        {
            return _items[itemType];
        }

        /// <summary>
        /// Initializes the object for evaluation.
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.
            InitializeForEvaluation(IToolsetProvider toolsetProvider, EvaluationContext evaluationContext, LoggingContext loggingContext)
        {
            // All been done in the constructor.  We don't allow re-evaluation of project instances.
        }

        /// <summary>
        /// Indicates to the data block that evaluation has completed,
        /// so for example it can mark datastructures read-only.
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.FinishEvaluation()
        {
            // Ideally we would unify targets collections here (they are almost all the same) as Project.FinishEvaluation() does.
            // However it's trickier as the target collections here are in a few cases mutated: they would have to be copy on write.
        }

        /// <summary>
        /// Adds a new item
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddItem(ProjectItemInstance item)
        {
            _items.Add(item);
        }

        /// <summary>
        /// Adds a new item to the collection of all items ignoring condition
        /// </summary>
        /// <remarks>
        /// Because ShouldEvaluateForDesignTime returns false, this should not be called.
        /// </remarks>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddItemIgnoringCondition(ProjectItemInstance item)
        {
            ErrorUtilities.ThrowInternalErrorUnreachable();
        }

        /// <summary>
        /// Adds a new item definition
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        IItemDefinition<ProjectMetadataInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddItemDefinition(string itemType)
        {
            ProjectItemDefinitionInstance itemDefinitionInstance = new ProjectItemDefinitionInstance(itemType);

            _itemDefinitions.Add(itemDefinitionInstance);

            return itemDefinitionInstance;
        }

        /// <summary>
        /// Properties encountered during evaluation. These are read during the first evaluation pass.
        /// Unlike those returned by the Properties property, these are ordered, and include any properties that
        /// were subsequently overridden by others with the same name. It does not include any
        /// properties whose conditions did not evaluate to true.
        /// </summary>
        /// <remarks>
        /// Because ShouldEvaluateForDesignTime returns false, this should not be called.
        /// </remarks>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddToAllEvaluatedPropertiesList(ProjectPropertyInstance property)
        {
            ErrorUtilities.ThrowInternalErrorUnreachable();
        }

        /// <summary>
        /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
        /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
        /// were subsequently overridden by others with the same name and item type. It does not include any
        /// elements whose conditions did not evaluate to true.
        /// </summary>
        /// <remarks>
        /// Because ShouldEvaluateForDesignTime returns false, this should not be called.
        /// </remarks>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddToAllEvaluatedItemDefinitionMetadataList(ProjectMetadataInstance itemDefinitionMetadatum)
        {
            ErrorUtilities.ThrowInternalErrorUnreachable();
        }

        /// <summary>
        /// Items encountered during evaluation. These are read during the third evaluation pass.
        /// Unlike those returned by the Items property, these are ordered.
        /// It does not include any elements whose conditions did not evaluate to true.
        /// It does not include any items added since the last evaluation.
        /// </summary>
        /// <remarks>
        /// Because ShouldEvaluateForDesignTime returns false, this should not be called.
        /// </remarks>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddToAllEvaluatedItemsList(ProjectItemInstance item)
        {
            ErrorUtilities.ThrowInternalErrorUnreachable();
        }

        /// <summary>
        /// Retrieves an existing item definition, if any.
        /// </summary>
        IItemDefinition<ProjectMetadataInstance> IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.GetItemDefinition(string itemType)
        {
            ProjectItemDefinitionInstance itemDefinitionInstance;

            _itemDefinitions.TryGetValue(itemType, out itemDefinitionInstance);

            return itemDefinitionInstance;
        }

        /// <summary>
        /// Sets a property which does not come from the Xml.
        /// This is where global, environment, and toolset properties are added to the project instance by the evaluator, and we mark them
        /// immutable if we are immutable.
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        ProjectPropertyInstance IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.SetProperty(string name, string evaluatedValueEscaped, bool isGlobalProperty, bool mayBeReserved, LoggingContext loggingContext, bool isEnvironmentVariable, bool isCommandLineProperty)
        {
            // Mutability not verified as this is being populated during evaluation
            ProjectPropertyInstance property = ProjectPropertyInstance.Create(name, evaluatedValueEscaped, mayBeReserved, _isImmutable, isEnvironmentVariable, loggingContext);
            _properties.Set(property);
            return property;
        }

        /// <summary>
        /// Sets a property which comes from the Xml.
        /// Predecessor is discarded as it is a design time only artefact.
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        ProjectPropertyInstance IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.SetProperty(ProjectPropertyElement propertyElement, string evaluatedValueEscaped, LoggingContext loggingContext)
        {
            // Mutability not verified as this is being populated during evaluation
            ProjectPropertyInstance property = ProjectPropertyInstance.Create(propertyElement.Name, evaluatedValueEscaped, false /* may not be reserved */, _isImmutable);
            _properties.Set(property);
            return property;
        }

        /// <summary>
        /// Retrieves an existing target, if any.
        /// </summary>
        ProjectTargetInstance IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.GetTarget(string targetName)
        {
            ProjectTargetInstance targetInstance;

            _targets.TryGetValue(targetName, out targetInstance);

            return targetInstance;
        }

        /// <summary>
        /// Adds a new target.
        /// Only called during evaluation, so does not check for immutability.
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.AddTarget(ProjectTargetInstance target)
        {
            _actualTargets[target.Name] = target;
        }

        /// <summary>
        /// Record an import opened during evaluation.
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.RecordImport(
            ProjectImportElement importElement,
            ProjectRootElement import,
            int versionEvaluated,
            SdkResult sdkResult)
        {
            _importPaths.Add(import.FullPath);
            ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>)this).RecordImportWithDuplicates(importElement, import, versionEvaluated);
        }

        /// <summary>
        /// Record an import opened during evaluation. Include duplicates
        /// </summary>
        void IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.RecordImportWithDuplicates(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated)
        {
            _importPathsIncludingDuplicates.Add(import.FullPath);
        }

        /// <summary>
        /// Get any property in the item that has the specified name,
        /// otherwise returns null
        /// </summary>
        [DebuggerStepThrough]
        public ProjectPropertyInstance GetProperty(string name)
        {
            return _properties[name];
        }

        /// <summary>
        /// Get any property in the item that has the specified name,
        /// otherwise returns null.
        /// Name is the segment of the provided string with the provided start and end indexes.
        /// </summary>
        [DebuggerStepThrough]
        ProjectPropertyInstance IPropertyProvider<ProjectPropertyInstance>.GetProperty(string name, int startIndex, int endIndex)
        {
            return _properties.GetProperty(name, startIndex, endIndex);
        }

        /// <summary>
        /// Get the value of a property in this project, or
        /// an empty string if it does not exist.
        /// </summary>
        /// <remarks>
        /// A property with a value of empty string and no property
        /// at all are not distinguished between by this method.
        /// This is because the build does not distinguish between the two.
        /// The reason this method exists when users can simply do GetProperty(..).EvaluatedValue,
        /// is that the caller would have to check for null every time. For properties, empty and undefined are
        /// not distinguished, so it much more useful to also have a method that returns empty string in
        /// either case.
        /// This function returns the unescaped value.
        /// </remarks>
        public string GetPropertyValue(string name)
        {
            if (!_properties.TryGetPropertyUnescapedValue(name, out string unescapedValue))
            {
                unescapedValue = String.Empty;
            }

            return unescapedValue;
        }

        internal string GetEngineRequiredPropertyValue(string name)
        {
            if (!_properties.TryGetPropertyUnescapedValue(name, out string unescapedValue))
            {
                unescapedValue = String.Empty;
            }
            else
            {
                _loggingContext?.ProcessPropertyRead(
                    new PropertyReadInfo(name, ElementLocation.EmptyLocation, false, PropertyReadContext.Other));
            }

            return unescapedValue;
        }

        /// <summary>
        /// Add a property with the specified name and value.
        /// Overwrites any property with the same name already in the collection.
        /// </summary>
        /// <remarks>
        /// We don't take a ProjectPropertyInstance to make sure we don't have one that's already
        /// in use by another ProjectPropertyInstance.
        /// </remarks>
        public ProjectPropertyInstance SetProperty(string name, string evaluatedValue)
        {
            VerifyThrowNotImmutable();

            ProjectPropertyInstance property = ProjectPropertyInstance.Create(name, evaluatedValue, false /* may not be reserved */, _isImmutable);
            _properties.Set(property);

            _loggingContext?.ProcessPropertyWrite(new PropertyWriteInfo(name, false, ElementLocation.EmptyLocation));

            return property;
        }

        /// <summary>
        /// Adds an item with no metadata to the project
        /// </summary>
        /// <remarks>
        /// We don't take a ProjectItemInstance to make sure we don't have one that's already
        /// in use by another ProjectInstance.
        /// </remarks>
        /// <comments>
        /// For purposes of declaring the project that defined this item (for use with e.g. the
        /// DeclaringProject* metadata), the entrypoint project is used for synthesized items
        /// like those added by this API.
        /// </comments>
        public ProjectItemInstance AddItem(string itemType, string evaluatedInclude)
        {
            VerifyThrowNotImmutable();

            ProjectItemInstance item = new ProjectItemInstance(this, itemType, evaluatedInclude, this.FullPath);
            _items.Add(item);

            return item;
        }

        /// <summary>
        /// Adds an item with metadata to the project.
        /// Metadata may be null.
        /// </summary>
        /// <remarks>
        /// We don't take a ProjectItemInstance to make sure we don't have one that's already
        /// in use by another ProjectInstance.
        /// </remarks>
        /// <comments>
        /// For purposes of declaring the project that defined this item (for use with e.g. the
        /// DeclaringProject* metadata), the entrypoint project is used for synthesized items
        /// like those added by this API.
        /// </comments>
        public ProjectItemInstance AddItem(string itemType, string evaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            VerifyThrowNotImmutable();

            ProjectItemInstance item = new ProjectItemInstance(this, itemType, evaluatedInclude, metadata, this.FullPath);
            _items.Add(item);

            return item;
        }

        /// <summary>
        /// Get a list of all the items in the project of the specified
        /// type, or an empty list if there are none.
        /// This is a read-only list.
        /// </summary>
        public ICollection<ProjectItemInstance> GetItems(string itemType)
        {
            // GetItems already returns a readonly collection
            return ((IItemProvider<ProjectItemInstance>)this).GetItems(itemType);
        }

        /// <summary>
        /// get items by item type and evaluated include value
        /// </summary>
        public IEnumerable<ProjectItemInstance> GetItemsByItemTypeAndEvaluatedInclude(string itemType, string evaluatedInclude)
        {
            // Avoid using LINQ - this is called a lot in VS
            if (_itemsByEvaluatedInclude == null)
            {
                foreach (var item in GetItems(itemType))
                {
                    if (string.Equals(item.EvaluatedInclude, evaluatedInclude, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return item;
                    }
                }
            }
            else
            {
                foreach (var item in GetItemsByEvaluatedInclude(evaluatedInclude))
                {
                    if (string.Equals(item.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return item;
                    }
                }
            }
        }

        /// <summary>
        /// Removes an item from the project, if present.
        /// Returns true if it was present, false otherwise.
        /// </summary>
        public bool RemoveItem(ProjectItemInstance item)
        {
            VerifyThrowNotImmutable();

            return _items.Remove(item);
        }

        /// <summary>
        /// Removes any property with the specified name.
        /// Returns true if the property had a value (possibly empty string), otherwise false.
        /// </summary>
        public bool RemoveProperty(string name)
        {
            VerifyThrowNotImmutable();

            return _properties.Remove(name);
        }

        /// <summary>
        /// Create an independent, deep clone of this object and everything in it.
        /// Useful for compiling a single file; or for keeping build results between builds.
        /// Clone has the same mutability as the original.
        /// </summary>
        public ProjectInstance DeepCopy()
        {
            return DeepCopy(_isImmutable);
        }

        /// <summary>
        /// Create an independent clone of this object, keeping ONLY the explicitly
        /// requested project state.
        /// </summary>
        /// <remarks>
        /// Useful for reducing the wire cost of IPC for out-of-proc nodes used during
        /// design-time builds that only need to populate a known set of data.
        /// </remarks>
        /// <param name="filter">Project state that should be returned.</param>
        /// <returns></returns>
        public ProjectInstance FilteredCopy(RequestedProjectState filter)
        {
            return new ProjectInstance(this, true, filter);
        }

        /// <summary>
        /// Create an independent, deep clone of this object and everything in it, with
        /// specified mutability.
        /// Useful for compiling a single file; or for keeping build results between builds.
        /// </summary>
        public ProjectInstance DeepCopy(bool isImmutable)
        {
            if (isImmutable && _isImmutable)
            {
                // No need to clone
                return this;
            }

            return new ProjectInstance(this, isImmutable);
        }

        /// <summary>
        /// Build default target/s with loggers of the project collection.
        /// Returns true on success, false on failure.
        /// Only valid if mutable.
        /// </summary>
        public bool Build()
        {
            return Build(null);
        }

        /// <summary>
        /// Build default target/s with specified loggers.
        /// Returns true on success, false on failure.
        /// Loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(IEnumerable<ILogger> loggers)
        {
            return Build((string[])null, loggers, null);
        }

        /// <summary>
        /// Build default target/s with specified loggers.
        /// Returns true on success, false on failure.
        /// Loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            return Build((string[])null, loggers, remoteLoggers);
        }

        /// <summary>
        /// Build a target with specified loggers.
        /// Returns true on success, false on failure.
        /// Target may be null.
        /// Loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string target, IEnumerable<ILogger> loggers)
        {
            return Build(target, loggers, null);
        }

        /// <summary>
        /// Build a target with specified loggers.
        /// Returns true on success, false on failure.
        /// Target may be null.
        /// Loggers may be null.
        /// Remote loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string target, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            string[] targets = (target == null) ? [] : [target];

            return Build(targets, loggers, remoteLoggers);
        }

        /// <summary>
        /// Build a list of targets with specified loggers.
        /// Returns true on success, false on failure.
        /// Targets may be null.
        /// Loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers)
        {
            return Build(targets, loggers, null);
        }

        /// <summary>
        /// Build a list of targets with specified loggers.
        /// Returns true on success, false on failure.
        /// Targets may be null.
        /// Loggers may be null.
        /// Remote loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            IDictionary<string, TargetResult> targetOutputs;

            return Build(targets, loggers, remoteLoggers, out targetOutputs);
        }

        /// <summary>
        /// Build a list of targets with specified loggers.
        /// Returns true on success, false on failure.
        /// Targets may be null.
        /// Loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers, out IDictionary<string, TargetResult> targetOutputs)
        {
            return Build(targets, loggers, null, null, out targetOutputs);
        }

        /// <summary>
        /// Build a list of targets with specified loggers.
        /// Returns true on success, false on failure.
        /// Targets may be null.
        /// Loggers may be null.
        /// Remote loggers may be null.
        /// Only valid if mutable.
        /// </summary>
        /// <remarks>
        /// If any of the loggers supplied are already attached to the logging service we
        /// were passed, throws InvalidOperationException.
        /// </remarks>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, out IDictionary<string, TargetResult> targetOutputs)
        {
            return Build(targets, loggers, remoteLoggers, null, out targetOutputs);
        }

        /// <summary>
        /// Evaluates the provided string by expanding items and properties,
        /// using the current items and properties available.
        /// This is useful for some hosts, or for the debugger immediate window.
        /// Does not expand bare metadata expressions.
        /// </summary>
        /// <comment>
        /// Not for internal use.
        /// </comment>
        public string ExpandString(string unexpandedValue)
        {
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(this, this, FileSystems.Default, _loggingContext);

            string result = expander.ExpandIntoStringAndUnescape(unexpandedValue, ExpanderOptions.ExpandPropertiesAndItems, ProjectFileLocation);

            return result;
        }

        /// <summary>
        /// Evaluates the provided string as a condition by expanding items and properties,
        /// using the current items and properties available, then doing a logical evaluation.
        /// This is useful for the immediate window.
        /// Does not expand bare metadata expressions.
        /// </summary>
        /// <comment>
        /// Not for internal use.
        /// </comment>
        public bool EvaluateCondition(string condition)
        {
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(this, this, FileSystems.Default, _loggingContext);

            bool result = ConditionEvaluator.EvaluateCondition(
                condition,
                ParserOptions.AllowPropertiesAndItemLists,
                expander,
                ExpanderOptions.ExpandPropertiesAndItems,
                Directory,
                ProjectFileLocation,
                FileSystems.Default,
                null /* no logging context */);

            return result;
        }

        /// <summary>
        /// Creates a ProjectRootElement from the contents of this ProjectInstance.
        /// </summary>
        /// <returns>A ProjectRootElement which represents this instance.</returns>
        public ProjectRootElement ToProjectRootElement()
        {
            ProjectRootElement rootElement = ProjectRootElement.Create();

            rootElement.InitialTargets = String.Join(";", InitialTargets);
            rootElement.DefaultTargets = String.Join(";", DefaultTargets);
            rootElement.ToolsVersion = ToolsVersion;

            // Add all of the item definitions.
            ProjectItemDefinitionGroupElement itemDefinitionGroupElement = rootElement.AddItemDefinitionGroup();
            foreach (ProjectItemDefinitionInstance itemDefinitionInstance in _itemDefinitions.Values)
            {
                itemDefinitionInstance.ToProjectItemDefinitionElement(itemDefinitionGroupElement);
            }

            // Add all of the items.
            foreach (string itemType in _items.ItemTypes)
            {
                ProjectItemGroupElement itemGroupElement = rootElement.AddItemGroup();
                foreach (ProjectItemInstance item in _items.GetItems(itemType))
                {
                    item.ToProjectItemElement(itemGroupElement);
                }
            }

            // Add all of the properties.
            ProjectPropertyGroupElement propertyGroupElement = rootElement.AddPropertyGroup();
            foreach (ProjectPropertyInstance property in _properties)
            {
                if (!ReservedPropertyNames.IsReservedProperty(property.Name))
                {
                    // Only emit the property if it does not exist in the global or environment properties dictionaries or differs from them.
                    if (!_globalProperties.Contains(property.Name) || !String.Equals(_globalProperties[property.Name].EvaluatedValue, property.EvaluatedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_environmentVariableProperties.Contains(property.Name) || !String.Equals(_environmentVariableProperties[property.Name].EvaluatedValue, property.EvaluatedValue, StringComparison.OrdinalIgnoreCase))
                        {
                            property.ToProjectPropertyElement(propertyGroupElement);
                        }
                    }
                }
            }

            // Add all of the targets.
            foreach (ProjectTargetInstance target in Targets.Values)
            {
                target.ToProjectTargetElement(rootElement);
            }

            return rootElement;
        }

        /// <summary>
        /// Replaces the project state (<see cref="GlobalProperties"/>, <see cref="Properties"/> and <see cref="Items"/>) with that
        /// from the <see cref="ProjectInstance"/> provided.
        /// </summary>
        /// <param name="projectState"><see cref="ProjectInstance"/> with the state to use.</param>
        public void UpdateStateFrom(ProjectInstance projectState)
        {
            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(projectState._globalProperties);
            _properties = new PropertyDictionary<ProjectPropertyInstance>(projectState._properties);
            _items = new ItemDictionary<ProjectItemInstance>(projectState._items);
        }

        internal bool IsLoaded => ProjectRootElementCache != null && TaskRegistry.IsLoaded;

        /// <summary>
        /// When project instances get serialized between nodes, they need to be initialized with node specific information.
        /// The node specific information cannot come from the constructor, because that information is not available to INodePacketTranslators
        /// </summary>
        internal void LateInitialize(ProjectRootElementCacheBase projectRootElementCache, HostServices hostServices)
        {
            ErrorUtilities.VerifyThrow(ProjectRootElementCache == null, $"{nameof(ProjectRootElementCache)} is already set. Cannot set again");
            ErrorUtilities.VerifyThrow(_hostServices == null, $"{nameof(HostServices)} is already set. Cannot set again");
            ErrorUtilities.VerifyThrow(TaskRegistry != null, $"{nameof(TaskRegistry)} Cannot be null after {nameof(ProjectInstance)} object creation.");

            ProjectRootElementCache = projectRootElementCache;
            _taskRegistry.RootElementCache = projectRootElementCache;
            _hostServices = hostServices;
        }

        #region INodePacketTranslatable Members

        /// <summary>
        /// Translate the project instance to or from a stream.
        /// Only translates global properties, properties, items, and mutability.
        /// </summary>
        void ITranslatable.Translate(ITranslator translator)
        {
            if (translator.Mode == TranslationDirection.WriteToStream)
            {
                // When serializing into stream apply Traits.Instance.EscapeHatches.ProjectInstanceTranslation if defined.
                MaybeForceTranslateEntireStateMode();
            }

            translator.Translate(ref _translateEntireState);

            if (_translateEntireState)
            {
                TranslateAllState(translator);
            }
            else
            {
                TranslateMinimalState(translator);
            }
        }

        private void MaybeForceTranslateEntireStateMode()
        {
            var forcedProjectInstanceTranslationMode = Traits.Instance.EscapeHatches.ProjectInstanceTranslation;
            if (forcedProjectInstanceTranslationMode != null)
            {
                switch (forcedProjectInstanceTranslationMode)
                {
                    case EscapeHatches.ProjectInstanceTranslationMode.Full:
                        _translateEntireState = true;
                        break;
                    case EscapeHatches.ProjectInstanceTranslationMode.Partial:
                        _translateEntireState = false;
                        break;
                    default:
                        // if EscapeHatches.ProjectInstanceTranslation has an unexpected value, do not force TranslateEntireStateMode.
                        // Just leave it as is.
                        break;
                }
            }
        }

        internal void TranslateMinimalState(ITranslator translator)
        {
            translator.TranslateDictionary(ref _globalProperties, ProjectPropertyInstance.FactoryForDeserialization);
            translator.TranslateDictionary(ref _properties, ProjectPropertyInstance.FactoryForDeserialization);
            translator.Translate(ref _requestedProjectStateFilter);
            translator.Translate(ref _isImmutable);
            TranslateItems(translator);
        }

        private void TranslateAllState(ITranslator translator)
        {
            TranslateProperties(translator);
            TranslateItems(translator);
            TranslateTargets(translator);
            TranslateToolsetSpecificState(translator);

            translator.Translate(ref _directory);
            translator.Translate(ref _projectFileLocation, ElementLocation.FactoryForDeserialization);
            translator.Translate(ref _taskRegistry, TaskRegistry.FactoryForDeserialization);
            translator.Translate(ref _isImmutable);
            translator.Translate(ref _evaluationId);

            translator.TranslateDictionary(
                ref _itemDefinitions,
                ProjectItemDefinitionInstance.FactoryForDeserialization,
                capacity => new RetrievableEntryHashSet<ProjectItemDefinitionInstance>(capacity, MSBuildNameIgnoreCaseComparer.Default));

            // ignore _importPaths/ImportPaths. Only used by public API users, not nodes
            // ignore _importPathsIncludingDuplicates/ImportPathsIncludingDuplicates. Only used by public API users, not nodes
        }

        private void TranslateToolsetSpecificState(ITranslator translator)
        {
            translator.Translate(ref _toolset, Toolset.FactoryForDeserialization);
            translator.Translate(ref _usingDifferentToolsVersionFromProjectFile);
            translator.Translate(ref _explicitToolsVersionSpecified);
            translator.Translate(ref _originalProjectToolsVersion);
            translator.Translate(ref _subToolsetVersion);
        }

        private void TranslateProperties(ITranslator translator)
        {
            translator.TranslateDictionary(ref _environmentVariableProperties, ProjectPropertyInstance.FactoryForDeserialization);
            translator.TranslateDictionary(ref _globalProperties, ProjectPropertyInstance.FactoryForDeserialization);
            translator.TranslateDictionary(ref _properties, ProjectPropertyInstance.FactoryForDeserialization);

            var globalPropertiesToTreatAsLocal = (HashSet<string>)_globalPropertiesToTreatAsLocal;
            translator.Translate(ref globalPropertiesToTreatAsLocal);

            if (translator.Mode == TranslationDirection.ReadFromStream)
            {
                _globalPropertiesToTreatAsLocal = globalPropertiesToTreatAsLocal;
            }
        }

        private void TranslateTargets(ITranslator translator)
        {
            translator.TranslateDictionary(ref _targets,
                ProjectTargetInstance.FactoryForDeserialization,
                capacity => new RetrievableEntryHashSet<ProjectTargetInstance>(capacity, MSBuildNameIgnoreCaseComparer.Default));

            translator.TranslateDictionary(ref _beforeTargets, TranslatorForTargetSpecificDictionaryKey, TranslatorForTargetSpecificDictionaryValue, count => new Dictionary<string, List<TargetSpecification>>(count));
            translator.TranslateDictionary(ref _afterTargets, TranslatorForTargetSpecificDictionaryKey, TranslatorForTargetSpecificDictionaryValue, count => new Dictionary<string, List<TargetSpecification>>(count));

            translator.Translate(ref _defaultTargets);
            translator.Translate(ref _initialTargets);
        }

        // todo move to nested function after c#7
        private static void TranslatorForTargetSpecificDictionaryKey(ITranslator translator, ref string key)
        {
            translator.Translate(ref key);
        }

        // todo move to nested function after c#7
        private static void TranslatorForTargetSpecificDictionaryValue(ITranslator translator, ref List<TargetSpecification> value)
        {
            translator.Translate(ref value, TargetSpecification.FactoryForDeserialization);
        }

        private void TranslateItems(ITranslator translator)
        {
            // ignore EvaluatedItemElements. Only used by public API users, not nodes
            // ignore itemsByEvaluatedInclude. Only used by public API users, not nodes

            if (translator.Mode == TranslationDirection.ReadFromStream)
            {
                int typeCount = default(int);
                translator.Translate(ref typeCount);
                _items = new ItemDictionary<ProjectItemInstance>(typeCount);
                for (int typeIndex = 0; typeIndex < typeCount; typeIndex++)
                {
                    int itemCount = default(int);
                    translator.Translate(ref itemCount);
                    for (int i = 0; i < itemCount; i++)
                    {
                        ProjectItemInstance item = null;
                        translator.Translate(ref item, delegate { return ProjectItemInstance.FactoryForDeserialization(translator, this); });
                        _items.Add(item);
                    }
                }
            }
            else
            {
                int typeCount = _items.ItemTypes.Count;
                translator.Translate(ref typeCount);

                foreach (string itemType in _items.ItemTypes)
                {
                    ICollection<ProjectItemInstance> itemList = _items[itemType];
                    int itemCount = itemList.Count;
                    translator.Translate(ref itemCount);
                    foreach (ProjectItemInstance item in itemList)
                    {
                        ProjectItemInstance temp = item;
                        translator.Translate(ref temp, delegate { return ProjectItemInstance.FactoryForDeserialization(translator, this); });
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Creates a set of project instances which represent the project dependency graph for a solution build.
        /// </summary>
        internal static ProjectInstance[] LoadSolutionForBuild(
            string projectFile,
            PropertyDictionary<ProjectPropertyInstance> globalPropertiesInstances,
            string toolsVersion,
            BuildParameters buildParameters,
            ILoggingService loggingService,
            BuildEventContext projectBuildEventContext,
            bool isExplicitlyLoaded,
            IReadOnlyCollection<string> targetNames,
            ISdkResolverService sdkResolverService,
            int submissionId)
        {
            ErrorUtilities.VerifyThrowArgumentLength(projectFile);
            ErrorUtilities.VerifyThrowArgumentNull(globalPropertiesInstances);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(buildParameters);
            ErrorUtilities.VerifyThrow(FileUtilities.IsSolutionFilename(projectFile), "Project file {0} is not a solution.", projectFile);

            ProjectInstance[] projectInstances = null;

            Dictionary<string, string> globalProperties = new Dictionary<string, string>(globalPropertiesInstances.Count, StringComparer.OrdinalIgnoreCase);
            foreach (ProjectPropertyInstance propertyInstance in globalPropertiesInstances)
            {
                globalProperties[propertyInstance.Name] = ((IProperty)propertyInstance).EvaluatedValueEscaped;
            }

            // If a ToolsVersion has been passed in using the /tv:xx switch, we want to generate an
            // old-style solution wrapper project if it's < 4.0, to work around ordering issues.
            if (toolsVersion != null)
            {
                if (
                       String.Equals(toolsVersion, "2.0", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(toolsVersion, "3.0", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(toolsVersion, "3.5", StringComparison.OrdinalIgnoreCase))
                {
                    // Spawn the Orcas SolutionWrapperProject generator.
                    loggingService.LogComment(projectBuildEventContext, MessageImportance.Low, "OldWrapperGeneratedExplicitToolsVersion", toolsVersion);
                    projectInstances = GenerateSolutionWrapperUsingOldOM(projectFile, globalProperties, toolsVersion, buildParameters.ProjectRootElementCache, buildParameters, loggingService, projectBuildEventContext, isExplicitlyLoaded, sdkResolverService, submissionId);
                }
                else
                {
                    projectInstances = GenerateSolutionWrapper(projectFile, globalProperties, toolsVersion, loggingService, projectBuildEventContext, targetNames, sdkResolverService, submissionId);
                }
            }

            // If the user didn't pass in a ToolsVersion, still try to make a best-effort guess as to whether
            // we should be generating a 4.0+ or a 3.5-style wrapper project based on the version of the solution.
            else
            {
                projectInstances = CalculateToolsVersionAndGenerateSolutionWrapper(
                    projectFile,
                    buildParameters,
                    loggingService,
                    projectBuildEventContext,
                    globalProperties,
                    isExplicitlyLoaded,
                    targetNames,
                    sdkResolverService,
                    submissionId);
            }

            return projectInstances;
        }

        private static ProjectInstance[] CalculateToolsVersionAndGenerateSolutionWrapper(
            string projectFile,
            BuildParameters buildParameters,
            ILoggingService loggingService,
            BuildEventContext projectBuildEventContext,
            Dictionary<string, string> globalProperties,
            bool isExplicitlyLoaded,
            IReadOnlyCollection<string> targetNames,
            ISdkResolverService sdkResolverService,
            int submissionId)
        {
            string solutionFileName = projectFile;

            if (FileUtilities.IsSolutionFilterFilename(projectFile))
            {
                solutionFileName = SolutionFile.ParseSolutionFromSolutionFilter(projectFile, out _);
            }

            if (SolutionFile.ShouldUseNewParser(solutionFileName))
            {
                // For the new parser we use Current tools version.
                return GenerateSolutionWrapper(projectFile, globalProperties, "Current", loggingService, projectBuildEventContext, targetNames, sdkResolverService, submissionId);
            }

            // For the old parser we try to make a best-effort guess based on the version of the solution.
            string toolsVersion = null;
            ProjectInstance[] projectInstances = null;

            SolutionFile.GetSolutionFileAndVisualStudioMajorVersions(solutionFileName, out int solutionVersion, out int visualStudioVersion);

            // If we get to this point, it's because it's a valid version.  Map the solution version
            // to the equivalent MSBuild ToolsVersion, and unless it's Dev10 or newer, spawn the old
            // engine to generate the solution wrapper.
            if (solutionVersion <= 9) /* Whidbey or before */
            {
                loggingService.LogComment(projectBuildEventContext, MessageImportance.Low, "OldWrapperGeneratedOldSolutionVersion", "2.0", solutionVersion);
                projectInstances = GenerateSolutionWrapperUsingOldOM(projectFile, globalProperties, "2.0", buildParameters.ProjectRootElementCache, buildParameters, loggingService, projectBuildEventContext, isExplicitlyLoaded, sdkResolverService, submissionId);
            }
            else if (solutionVersion == 10) /* Orcas */
            {
                loggingService.LogComment(projectBuildEventContext, MessageImportance.Low, "OldWrapperGeneratedOldSolutionVersion", "3.5", solutionVersion);
                projectInstances = GenerateSolutionWrapperUsingOldOM(projectFile, globalProperties, "3.5", buildParameters.ProjectRootElementCache, buildParameters, loggingService, projectBuildEventContext, isExplicitlyLoaded, sdkResolverService, submissionId);
            }
            else
            {
                if ((solutionVersion == 11) || (solutionVersion == 12 && visualStudioVersion == 0)) /* Dev 10 and Dev 11 */
                {
                    toolsVersion = "4.0";
                }
                else /* Dev 12 and above */
                {
                    toolsVersion = visualStudioVersion.ToString(CultureInfo.InvariantCulture) + ".0";
                }

                string toolsVersionToUse = Utilities.GenerateToolsVersionToUse(
                    explicitToolsVersion: null,
                    toolsVersionFromProject: FileUtilities.IsSolutionFilterFilename(projectFile) ? "Current" : toolsVersion,
                    getToolset: buildParameters.GetToolset,
                    defaultToolsVersion: Constants.defaultSolutionWrapperProjectToolsVersion,
                    usingDifferentToolsVersionFromProjectFile: out _);
                projectInstances = GenerateSolutionWrapper(projectFile, globalProperties, toolsVersionToUse, loggingService, projectBuildEventContext, targetNames, sdkResolverService, submissionId);
            }

            return projectInstances;
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static ProjectInstance FactoryForDeserialization(ITranslator translator)
        {
            return new ProjectInstance(translator);
        }

        /// <summary>
        /// Throws invalid operation exception if the project instance is immutable.
        /// Called before an edit.
        /// </summary>
        internal static void VerifyThrowNotImmutable(bool isImmutable)
        {
            if (isImmutable)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_ProjectInstanceImmutable");
            }
        }

        /// <summary>
        /// Builds a list of targets with the specified loggers.
        /// </summary>
        internal bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, ILoggingService loggingService, int maxNodeCount, out IDictionary<string, TargetResult> targetOutputs)
        {
            VerifyThrowNotImmutable();

            if (targets == null)
            {
                targets = [];
            }

            BuildResult results;

            BuildManager buildManager = BuildManager.DefaultBuildManager;

            BuildRequestData data = new BuildRequestData(this, targets, _hostServices);
            BuildParameters parameters = new BuildParameters();

            if (loggers != null)
            {
                parameters.Loggers = (loggers is ICollection<ILogger> loggersCollection) ? loggersCollection : new List<ILogger>(loggers);

                // Enables task parameter logging based on whether any of the loggers attached
                // to the Project have their verbosity set to Diagnostic. If no logger has
                // been set to log diagnostic then the existing/default value will be persisted.
                parameters.LogTaskInputs =
                    parameters.LogTaskInputs ||
                    loggers.Any(logger => logger.Verbosity == LoggerVerbosity.Diagnostic) ||
                    loggingService?.IncludeTaskInputs == true;
            }

            if (remoteLoggers != null)
            {
                parameters.ForwardingLoggers = remoteLoggers is ICollection<ForwardingLoggerRecord> records ?
                    records :
                    new List<ForwardingLoggerRecord>(remoteLoggers);
            }

            parameters.EnvironmentPropertiesInternal = _environmentVariableProperties;
            parameters.ProjectRootElementCache = ProjectRootElementCache;
            parameters.MaxNodeCount = maxNodeCount;

            results = buildManager.Build(parameters, data);

            targetOutputs = results.ResultsByTarget;

            return results.OverallResult == BuildResultCode.Success;
        }

        /// <summary>
        /// Builds a list of targets with the specified loggers.
        /// </summary>
        internal bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, ILoggingService loggingService, out IDictionary<string, TargetResult> targetOutputs)
        {
            return Build(targets, loggers, remoteLoggers, loggingService, 1, out targetOutputs);
        }

        /// <summary>
        /// Retrieves the list of targets which should run before the specified target.
        /// Never returns null.
        /// </summary>
        internal IList<TargetSpecification> GetTargetsWhichRunBefore(string target)
        {
            List<TargetSpecification> beforeTargetsForTarget;
            if (((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>)this).BeforeTargets.TryGetValue(target, out beforeTargetsForTarget))
            {
                return beforeTargetsForTarget;
            }
            else
            {
                return Array.Empty<TargetSpecification>();
            }
        }

        /// <summary>
        /// Retrieves the list of targets which should run after the specified target.
        /// Never returns null.
        /// </summary>
        internal IList<TargetSpecification> GetTargetsWhichRunAfter(string target)
        {
            List<TargetSpecification> afterTargetsForTarget;
            if (((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>)this).AfterTargets.TryGetValue(target, out afterTargetsForTarget))
            {
                return afterTargetsForTarget;
            }
            else
            {
                return Array.Empty<TargetSpecification>();
            }
        }

        /// <summary>
        /// Cache the contents of this project instance to the translator.
        /// The object is retained, but the bulk of its content is released.
        /// </summary>
        internal void Cache(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);

            if (translator.Mode == TranslationDirection.WriteToStream)
            {
                _globalProperties = null;
                _properties = null;
                _items = null;
            }
        }

        /// <summary>
        /// Retrieve the contents of this project from the translator.
        /// </summary>
        internal void RetrieveFromCache(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);
        }

        /// <summary>
        /// Adds the specified target to the instance.
        /// </summary>
        internal ProjectTargetInstance AddTarget(
            string targetName,
            string condition,
            string inputs,
            string outputs,
            string returns,
            string keepDuplicateOutputs,
            string dependsOnTargets,
            string beforeTargets,
            string afterTargets,
            bool parentProjectSupportsReturnsAttribute)
        {
            VerifyThrowNotImmutable();

            ErrorUtilities.VerifyThrowInternalLength(targetName, nameof(targetName));
            ErrorUtilities.VerifyThrow(!_actualTargets.ContainsKey(targetName), "Target {0} already exists.", targetName);

            ProjectTargetInstance target = new ProjectTargetInstance(
                targetName,
                condition ?? String.Empty,
                inputs ?? String.Empty,
                outputs ?? String.Empty,
                returns, // returns may be null
                keepDuplicateOutputs ?? String.Empty,
                dependsOnTargets ?? String.Empty,
                beforeTargets ?? String.Empty,
                afterTargets ?? String.Empty,
                _projectFileLocation,
                String.IsNullOrEmpty(condition) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(inputs) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(outputs) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(returns) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(keepDuplicateOutputs) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(dependsOnTargets) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(beforeTargets) ? null : ElementLocation.EmptyLocation,
                String.IsNullOrEmpty(afterTargets) ? null : ElementLocation.EmptyLocation,
                new ObjectModel.ReadOnlyCollection<ProjectTargetInstanceChild>(new List<ProjectTargetInstanceChild>()),
                new ObjectModel.ReadOnlyCollection<ProjectOnErrorInstance>(new List<ProjectOnErrorInstance>()),
                parentProjectSupportsReturnsAttribute);

            _actualTargets[targetName] = target;

            return target;
        }

        /// <summary>
        /// Removes the specified target from the instance.
        /// </summary>
        internal void RemoveTarget(string targetName)
        {
            VerifyThrowNotImmutable();

            _actualTargets.Remove(targetName);
        }

        /// <summary>
        /// Throws invalid operation exception if the project instance is immutable.
        /// Called before an edit.
        /// </summary>
        internal void VerifyThrowNotImmutable()
        {
            VerifyThrowNotImmutable(_isImmutable);
        }

        /// <summary>
        /// Generate a 4.0+-style solution wrapper project.
        /// </summary>
        /// <param name="projectFile">The solution file to generate a wrapper for.</param>
        /// <param name="globalProperties">The global properties of this solution.</param>
        /// <param name="toolsVersion">The ToolsVersion to use when generating the wrapper.</param>
        /// <param name="loggingService">The logging service used to log messages etc. from the solution wrapper generator.</param>
        /// <param name="projectBuildEventContext">The build event context in which this project is being constructed.</param>
        /// <param name="targetNames">A collection of target names that the user requested be built.</param>
        /// <param name="sdkResolverService"></param>
        /// <param name="submissionId"></param>
        /// <returns>The ProjectRootElement for the root traversal and each of the metaprojects.</returns>
        private static ProjectInstance[] GenerateSolutionWrapper(

                string projectFile,
                IDictionary<string, string> globalProperties,
                string toolsVersion,
                ILoggingService loggingService,
                BuildEventContext projectBuildEventContext,
                IReadOnlyCollection<string> targetNames,
                ISdkResolverService sdkResolverService,
                int submissionId)
        {
            SolutionFile sp = SolutionFile.Parse(projectFile);

            // Log any comments from the solution parser
            if (sp.SolutionParserComments.Count > 0)
            {
                foreach (string comment in sp.SolutionParserComments)
                {
                    loggingService.LogCommentFromText(projectBuildEventContext, MessageImportance.Low, comment);
                }
            }

            // Pass the toolsVersion of this project through, which will be not null if there was a /tv:nn switch
            // It's needed to determine which <UsingTask> tags to put in, whether to put a ToolsVersion parameter
            // on the <MSBuild> task tags, and what MSBuildToolsPath to use when scanning child projects
            // for dependency information.
            ProjectInstance[] instances = SolutionProjectGenerator.Generate(sp, globalProperties, toolsVersion, projectBuildEventContext, loggingService, targetNames, sdkResolverService, submissionId);
            return instances;
        }

        /// <summary>
        /// Spawn the old engine to generate a solution wrapper project, so that our build ordering is somewhat more correct
        /// when solutions with toolsVersions &lt; 4.0 are passed to us.
        /// </summary>
        /// <comment>
        /// #############################################################################################
        /// #### Segregated into another method to avoid loading the old Engine in the regular case. ####
        /// ####################### Do not move back in to the main code path! ##########################
        /// #############################################################################################
        ///  We have marked this method as NoInlining because we do not want Microsoft.Build.Engine.dll to be loaded unless we really execute this code path
        /// </comment>
        /// <param name="projectFile">The solution file to generate a wrapper for.</param>
        /// <param name="globalProperties">The global properties of this solution.</param>
        /// <param name="toolsVersion">The ToolsVersion to use when generating the wrapper.</param>
        /// <param name="projectRootElementCache">The root element cache which should be used for the generated project.</param>
        /// <param name="buildParameters">The build parameters.</param>
        /// <param name="loggingService">The logging service used to log messages etc. from the solution wrapper generator.</param>
        /// <param name="projectBuildEventContext">The build event context in which this project is being constructed.</param>
        /// <param name="isExplicitlyLoaded"><code>true</code> if the project is explicitly loaded, otherwise <code>false</code>.</param>
        /// <param name="sdkResolverService">An <see cref="ISdkResolverService"/> to use when resolving SDKs.</param>
        /// <param name="submissionId"></param>
        /// <returns>An appropriate ProjectRootElement</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ProjectInstance[] GenerateSolutionWrapperUsingOldOM(
        string projectFile,
            IDictionary<string, string> globalProperties,
            string toolsVersion,
            ProjectRootElementCacheBase projectRootElementCache,
            BuildParameters buildParameters,
            ILoggingService loggingService,
            BuildEventContext projectBuildEventContext,
            bool isExplicitlyLoaded,
            ISdkResolverService sdkResolverService,
            int submissionId)
        {
            // Pass the toolsVersion of this project through, which will never be null -- either we passed the /tv:nn
            // switch straight through, or we fabricated a ToolsVersion based on the solution version.
            // It's needed to determine which <UsingTask> tags to put in, whether to put a ToolsVersion parameter
            // on the <MSBuild> task tags, and what MSBuildToolsPath to use when scanning child projects
            // for dependency information.
            string wrapperProjectXml;

            List<DictionaryEntry> clearedVariables = null;
            try
            {
                // We need to make sure we unset any enviroment variable which is a reserved property or has an illegal name before we call the oldOM as it may crash it.
                foreach (DictionaryEntry environmentVariable in Environment.GetEnvironmentVariables())
                {
                    // We're going to just skip environment variables that contain names
                    // with characters we can't handle. There's no logger registered yet
                    // when this method is called, so we can't really log anything.
                    string environmentVariableName = environmentVariable.Key as string;

                    if (environmentVariableName != null &&
                        (!XmlUtilities.IsValidElementName(environmentVariableName)
                        || XMakeElements.ReservedItemNames.Contains(environmentVariableName)
                        || ReservedPropertyNames.IsReservedProperty(environmentVariableName)))
                    {
                        if (clearedVariables == null)
                        {
                            clearedVariables = new List<DictionaryEntry>();
                        }

                        Environment.SetEnvironmentVariable(environmentVariableName, null);
                        clearedVariables.Add(environmentVariable);
                    }
                }
                wrapperProjectXml = "";
            }
            finally
            {
                // Set the cleared environment variables back to what they were.
                if (clearedVariables != null)
                {
                    foreach (DictionaryEntry clearedVariable in clearedVariables)
                    {
                        Environment.SetEnvironmentVariable(clearedVariable.Key as string, clearedVariable.Value as string);
                    }
                }
            }

            XmlReaderSettings xrs = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore
            };

            StringReader sr = new StringReader(wrapperProjectXml);
            using (XmlReader xmlReader = XmlReader.Create(sr, xrs))
            {
                ProjectRootElement projectRootElement = new(
                    xmlReader,
                    projectRootElementCache,
                    isExplicitlyLoaded,
                    preserveFormatting: false)
                {
                    DirectoryPath = Path.GetDirectoryName(projectFile)
                };
                ProjectInstance instance = new(projectRootElement, globalProperties, toolsVersion, buildParameters, loggingService, projectBuildEventContext, sdkResolverService, submissionId);

                return [instance];
            }
        }

        /// <summary>
        /// Creates a copy of a dictionary and returns a read-only dictionary around the results.
        /// </summary>
        /// <typeparam name="TValue">The value stored in the dictionary</typeparam>
        /// <param name="dictionary">Dictionary to clone.</param>
        /// <param name="strComparer">The <see cref="StringComparer"/> to use for the cloned dictionary.</param>
        private static ObjectModel.ReadOnlyDictionary<string, TValue> CreateCloneDictionary<TValue>(IDictionary<string, TValue> dictionary, StringComparer strComparer)
        {
            Dictionary<string, TValue> clone;
            if (dictionary == null)
            {
                clone = new Dictionary<string, TValue>(0);
            }
            else
            {
                clone = new Dictionary<string, TValue>(dictionary, strComparer);
            }

            return new ObjectModel.ReadOnlyDictionary<string, TValue>(clone);
        }

        /// <summary>
        /// Creates a copy of a dictionary and returns a read-only dictionary around the results.
        /// </summary>
        /// <typeparam name="TValue">The value stored in the dictionary</typeparam>
        /// <param name="dictionary">Dictionary to clone.</param>
        private static IDictionary<string, TValue> CreateCloneDictionary<TValue>(IDictionary<string, TValue> dictionary) where TValue : class, IKeyed
        {
            if (dictionary == null)
            {
                return ReadOnlyEmptyDictionary<string, TValue>.Instance;
            }
            else
            {
                return new ObjectModel.ReadOnlyDictionary<string, TValue>(dictionary);
            }
        }

        private static ProjectPropertyInstance InstantiateProjectPropertyInstance(ProjectProperty property, bool isImmutable)
        {
            // Allow reserved property names, since this is how they are added to the project instance.
            // The caller has prevented users setting them themselves.
            var instance = ProjectPropertyInstance.Create(
                                property.Name,
                                ((IProperty)property).EvaluatedValueEscaped,
                                true /* MAY be reserved name */,
                                isImmutable,
                                property.IsEnvironmentProperty);
            return instance;
        }

        /// <summary>
        /// Logging context - set during the evaluation.
        /// Can be null - especially if the project was fetched from the cache.
        /// </summary>
        private LoggingContext _loggingContext;

        /// <summary>
        /// Common code for the constructors that evaluate directly.
        /// Global properties may be null.
        /// Tools version may be null.
        /// Does not set mutability.
        /// </summary>
        private void Initialize(
            ProjectRootElement xml,
            IDictionary<string, string> globalProperties,
            string explicitToolsVersion,
            string explicitSubToolsetVersion,
            int visualStudioVersionFromSolution,
            BuildParameters buildParameters,
            ILoggingService loggingService,
            BuildEventContext buildEventContext,
            ISdkResolverService sdkResolverService = null,
            int submissionId = BuildEventContext.InvalidSubmissionId,
            ProjectLoadSettings? projectLoadSettings = null,
            EvaluationContext evaluationContext = null,
            IDirectoryCacheFactory directoryCacheFactory = null)
        {
            ErrorUtilities.VerifyThrowArgumentNull(xml);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(explicitToolsVersion, "toolsVersion");
            ErrorUtilities.VerifyThrowArgumentNull(buildParameters);

            _directory = xml.DirectoryPath;
            _projectFileLocation = xml.ProjectFileLocation ?? ElementLocation.EmptyLocation;
            _properties = new PropertyDictionary<ProjectPropertyInstance>();
            _items = new ItemDictionary<ProjectItemInstance>();
            _actualTargets = new RetrievableEntryHashSet<ProjectTargetInstance>(StringComparer.OrdinalIgnoreCase);
            _targets = new ObjectModel.ReadOnlyDictionary<string, ProjectTargetInstance>(_actualTargets);
            _importPaths = new List<string>();
            ImportPaths = new ObjectModel.ReadOnlyCollection<string>(_importPaths);
            _importPathsIncludingDuplicates = new List<string>();
            ImportPathsIncludingDuplicates = new ObjectModel.ReadOnlyCollection<string>(_importPathsIncludingDuplicates);
            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>((globalProperties == null) ? 0 : globalProperties.Count);
            _environmentVariableProperties = buildParameters.EnvironmentPropertiesInternal;
            _itemDefinitions = new RetrievableEntryHashSet<ProjectItemDefinitionInstance>(MSBuildNameIgnoreCaseComparer.Default);
            _hostServices = buildParameters.HostServices;
            this.ProjectRootElementCache = buildParameters.ProjectRootElementCache;
            _loggingContext = new GenericLoggingContext(loggingService, buildEventContext);
            this.EvaluatedItemElements = new List<ProjectItemElement>();

            _explicitToolsVersionSpecified = (explicitToolsVersion != null);
            ElementLocation toolsVersionLocation = xml.Location;

            if (xml.ToolsVersion.Length > 0)
            {
                _originalProjectToolsVersion = xml.ToolsVersion;
                toolsVersionLocation = xml.ToolsVersionLocation;
            }

            var toolsVersionToUse = Utilities.GenerateToolsVersionToUse(
                explicitToolsVersion,
                xml.ToolsVersion,
                buildParameters.GetToolset,
                buildParameters.DefaultToolsVersion,
                out var usingDifferentToolsVersionFromProjectFile);

            _usingDifferentToolsVersionFromProjectFile = usingDifferentToolsVersionFromProjectFile;

            this.Toolset = buildParameters.GetToolset(toolsVersionToUse);

            if (this.Toolset == null)
            {
                string toolsVersionList = Utilities.CreateToolsVersionListString(buildParameters.Toolsets);
                ProjectErrorUtilities.ThrowInvalidProject(toolsVersionLocation, "UnrecognizedToolsVersion", toolsVersionToUse, toolsVersionList);
            }

            if (explicitSubToolsetVersion != null)
            {
                this.SubToolsetVersion = explicitSubToolsetVersion;
            }
            else
            {
                this.SubToolsetVersion = this.Toolset.GenerateSubToolsetVersionUsingVisualStudioVersion(globalProperties, visualStudioVersionFromSolution);
            }

            // Create a task registry which will fall back on the toolset task registry if necessary.
            this.TaskRegistry = new TaskRegistry(this.Toolset, ProjectRootElementCache);

            if (globalProperties != null)
            {
                foreach (KeyValuePair<string, string> globalProperty in globalProperties)
                {
                    if (String.Equals(globalProperty.Key, Constants.SubToolsetVersionPropertyName, StringComparison.OrdinalIgnoreCase) && explicitSubToolsetVersion != null)
                    {
                        // if we have a sub-toolset version explicitly provided by the ProjectInstance constructor, AND a sub-toolset version provided as a global property,
                        // make sure that the one passed in with the constructor wins.  If there isn't a matching global property, the sub-toolset version will be set at
                        // a later point.
                        _globalProperties.Set(ProjectPropertyInstance.Create(globalProperty.Key, explicitSubToolsetVersion, false /* may not be reserved */, _isImmutable));
                    }
                    else
                    {
                        _globalProperties.Set(ProjectPropertyInstance.Create(globalProperty.Key, globalProperty.Value, false /* may not be reserved */, _isImmutable));
                    }
                }
            }

            if (Traits.Instance.EscapeHatches.DebugEvaluation)
            {
                Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Creating a ProjectInstance from an unevaluated state [{0}]", FullPath));
            }

            ErrorUtilities.VerifyThrow(EvaluationId == BuildEventContext.InvalidEvaluationId, "Evaluation ID is invalid prior to evaluation");

            evaluationContext = evaluationContext?.ContextForNewProject() ?? EvaluationContext.Create(EvaluationContext.SharingPolicy.Isolated);

            Evaluator<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>.Evaluate(
                data: this,
                project: null,
                xml,
                projectLoadSettings ?? buildParameters.ProjectLoadSettings, /* Use override ProjectLoadSettings if specified */
                buildParameters.MaxNodeCount,
                buildParameters.EnvironmentPropertiesInternal,
                buildParameters.PropertiesFromCommandLine,
                loggingService,
                new ProjectItemInstanceFactory(this),
                buildParameters.ToolsetProvider,
                directoryCacheFactory,
                ProjectRootElementCache,
                buildEventContext,
                sdkResolverService ?? evaluationContext.SdkResolverService, /* Use override ISdkResolverService if specified */
                submissionId,
                evaluationContext,
                interactive: buildParameters.Interactive);

            ErrorUtilities.VerifyThrow(EvaluationId != BuildEventContext.InvalidEvaluationId, "Evaluation should produce an evaluation ID");
        }

        /// <summary>
        /// Get items by evaluatedInclude value
        /// </summary>
        private IEnumerable<ProjectItemInstance> GetItemsByEvaluatedInclude(string evaluatedInclude)
        {
            // Even if there are no items in itemsByEvaluatedInclude[], it will return an IEnumerable, which is non-null
            return _itemsByEvaluatedInclude[evaluatedInclude];
        }

        /// <summary>
        /// Create various target snapshots
        /// </summary>
        private void CreateTargetsSnapshot(
            IDictionary<string, ProjectTargetInstance> targets,
            List<string> defaultTargets,
            List<string> initialTargets,
            IDictionary<string, List<TargetSpecification>> beforeTargets,
            IDictionary<string, List<TargetSpecification>> afterTargets)
        {
            // ProjectTargetInstances are immutable so only the dictionary must be cloned
            _targets = CreateCloneDictionary(targets);

            InitializeTargetsData(defaultTargets, initialTargets, beforeTargets, afterTargets);
        }

        private void InitializeTargetsData(List<string> defaultTargets,
            List<string> initialTargets,
            IDictionary<string, List<TargetSpecification>> beforeTargets,
            IDictionary<string, List<TargetSpecification>> afterTargets)
        {
            DefaultTargets = defaultTargets == null ? new List<string>(0) : new List<string>(defaultTargets);
            InitialTargets = initialTargets == null ? new List<string>(0) : new List<string>(initialTargets);
            ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>)this).BeforeTargets = CreateCloneDictionary(beforeTargets, StringComparer.OrdinalIgnoreCase);
            ((IEvaluatorData<ProjectPropertyInstance, ProjectItemInstance, ProjectMetadataInstance, ProjectItemDefinitionInstance>)this).AfterTargets = CreateCloneDictionary(afterTargets, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Create various imports snapshots
        /// </summary>
        private void CreateImportsSnapshot(IList<ResolvedImport> importClosure, IList<ResolvedImport> importClosureWithDuplicates)
        {
            var importPaths = new List<string>(Math.Max(0, importClosure.Count - 1) /* outer project */);
            foreach (var resolvedImport in importClosure)
            {
                // Exclude outer project itself
                if (resolvedImport.ImportingElement != null)
                {
                    importPaths.Add(resolvedImport.ImportedProject.FullPath);
                }
            }

            _importPaths = importPaths;
            ImportPaths = importPaths.AsReadOnly();

            var importPathsIncludingDuplicates = new List<string>(Math.Max(0, importClosureWithDuplicates.Count - 1) /* outer project */);
            foreach (var resolvedImport in importClosureWithDuplicates)
            {
                // Exclude outer project itself
                if (resolvedImport.ImportingElement != null)
                {
                    importPathsIncludingDuplicates.Add(resolvedImport.ImportedProject.FullPath);
                }
            }

            _importPathsIncludingDuplicates = importPathsIncludingDuplicates;
            ImportPathsIncludingDuplicates = importPathsIncludingDuplicates.AsReadOnly();
        }

        /// <summary>
        /// Create environment variable properties snapshot
        /// </summary>
        private void CreateEnvironmentVariablePropertiesSnapshot(PropertyDictionary<ProjectPropertyInstance> environmentVariableProperties)
        {
            _environmentVariableProperties = new PropertyDictionary<ProjectPropertyInstance>(environmentVariableProperties.Count);

            foreach (ProjectPropertyInstance environmentProperty in environmentVariableProperties)
            {
                _environmentVariableProperties.Set(environmentProperty.DeepClone());
            }
        }

        /// <summary>
        /// Create global properties snapshot
        /// </summary>
        private void CreateGlobalPropertiesSnapshot(PropertyDictionary<ProjectPropertyInstance> globalPropertiesDictionary)
        {
            _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(globalPropertiesDictionary.Count);

            foreach (ProjectPropertyInstance globalProperty in globalPropertiesDictionary)
            {
                _globalProperties.Set(globalProperty.DeepClone());
            }
        }

        /// <summary>
        /// Create evaluated include cache snapshot
        /// </summary>
        private void CreateEvaluatedIncludeSnapshotIfRequested(bool keepEvaluationCache, ICollection<ProjectItem> items, Dictionary<ProjectItem, ProjectItemInstance> projectItemToInstanceMap)
        {
            if (!keepEvaluationCache)
            {
                return;
            }

            var multiDictionary = new MultiDictionary<string, ProjectItemInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                multiDictionary.Add(item.EvaluatedInclude, projectItemToInstanceMap[item]);
            }

            _itemsByEvaluatedInclude = multiDictionary;
        }

        /// <summary>
        /// Create Items snapshot
        /// </summary>
        private Dictionary<ProjectItem, ProjectItemInstance> CreateItemsSnapshot(ICollection<ProjectItem> items, int itemTypeCount, bool keepEvaluationCache)
        {
            _items = new ItemDictionary<ProjectItemInstance>(itemTypeCount);

            var projectItemToInstanceMap = keepEvaluationCache ? new Dictionary<ProjectItem, ProjectItemInstance>(items.Count) : null;

            foreach (ProjectItem item in items)
            {
                ProjectItemInstance instance = InstantiateProjectItemInstance(item);
                _items.Add(instance);
                projectItemToInstanceMap?.Add(item, instance);
            }

            return projectItemToInstanceMap;
        }

        private ProjectItemInstance InstantiateProjectItemInstance(ProjectItem item)
        {
            List<ProjectItemDefinitionInstance> inheritedItemDefinitions = null;
            if (item.InheritedItemDefinitions != null)
            {
                inheritedItemDefinitions = new List<ProjectItemDefinitionInstance>(item.InheritedItemDefinitions.Count);

                foreach (ProjectItemDefinition inheritedItemDefinition in item.InheritedItemDefinitions)
                {
                    // All item definitions in this list should be present in the collection of item definitions
                    // on the project we are cloning.
                    inheritedItemDefinitions.Add(_itemDefinitions[inheritedItemDefinition.ItemType]);
                }
            }

            CopyOnWritePropertyDictionary<ProjectMetadataInstance> directMetadata = null;
            if (item.DirectMetadata != null)
            {
                directMetadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>();

                IEnumerable<ProjectMetadataInstance> projectMetadataInstances = item.DirectMetadata.Select(directMetadatum => new ProjectMetadataInstance(directMetadatum));
                directMetadata.ImportProperties(projectMetadataInstances);
            }

            GetEvaluatedIncludesFromProjectItem(
                item,
                out string evaluatedIncludeEscaped,
                out string evaluatedIncludeBeforeWildcardExpansionEscaped);

            var instance = new ProjectItemInstance(
                this,
                item.ItemType,
                evaluatedIncludeEscaped,
                evaluatedIncludeBeforeWildcardExpansionEscaped,
                directMetadata,
                inheritedItemDefinitions,
                item.Xml.ContainingProject.EscapedFullPath,
                useItemDefinitionsWithoutModification: false);

            return instance;
        }

        private static void GetEvaluatedIncludesFromProjectItem(
            ProjectItem item,
            out string evaluatedIncludeEscaped,
            out string evaluatedIncludeBeforeWildcardExpansionEscaped)
        {
            // For externally constructed ProjectItem, fall back to the publicly available EvaluateInclude
            evaluatedIncludeEscaped = ((IItem)item).EvaluatedIncludeEscaped;
            evaluatedIncludeEscaped ??= item.EvaluatedInclude;
            evaluatedIncludeBeforeWildcardExpansionEscaped = item.EvaluatedIncludeBeforeWildcardExpansionEscaped;
            evaluatedIncludeBeforeWildcardExpansionEscaped ??= item.EvaluatedInclude;
        }

        private static ProjectItemInstance InstantiateProjectItemInstanceFromImmutableProjectSource(
            Project linkedProject,
            ProjectInstance projectInstance,
            ProjectItem item)
        {
            linkedProject.ItemDefinitions.TryGetValue(item.ItemType, out ProjectItemDefinition itemTypeDefinition);

            IList<ProjectItemDefinitionInstance> inheritedItemDefinitions =
                new ImmutableItemDefinitionsListConverter<ProjectItemDefinition, ProjectItemDefinitionInstance>(
                    item.InheritedItemDefinitions,
                    itemTypeDefinition,
                    ConvertCachedItemDefinitionToInstance);

            ICopyOnWritePropertyDictionary<ProjectMetadataInstance> directMetadata = null;
            if (item.DirectMetadata is not null)
            {
                if (item.DirectMetadata is IDictionary<string, ProjectMetadata> metadataDict)
                {
                    directMetadata = new ImmutablePropertyCollectionConverter<ProjectMetadata, ProjectMetadataInstance>(metadataDict, ConvertCachedProjectMetadataToInstance);
                }
                else
                {
                    directMetadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>();

                    IEnumerable<ProjectMetadataInstance> projectMetadataInstances = item.DirectMetadata.Select(directMetadatum => new ProjectMetadataInstance(directMetadatum));
                    directMetadata.ImportProperties(projectMetadataInstances);
                }
            }

            GetEvaluatedIncludesFromProjectItem(
                item,
                out string evaluatedIncludeEscaped,
                out string evaluatedIncludeBeforeWildcardExpansionEscaped);

            ProjectItemInstance instance = new ProjectItemInstance(
                projectInstance,
                item.ItemType,
                evaluatedIncludeEscaped,
                evaluatedIncludeBeforeWildcardExpansionEscaped,
                directMetadata,
                inheritedItemDefinitions,
                item.Xml.ContainingProject.EscapedFullPath,
                useItemDefinitionsWithoutModification: true);
            return instance;
        }

        private static string GetImportFullPath(ResolvedImport import)
        {
            return import.ImportedProject.FullPath;
        }

        /// <summary>
        /// Create ItemDefinitions snapshot
        /// </summary>
        private void CreateItemDefinitionsSnapshot(IDictionary<string, ProjectItemDefinition> itemDefinitions)
        {
            _itemDefinitions = new RetrievableEntryHashSet<ProjectItemDefinitionInstance>(itemDefinitions.Count, MSBuildNameIgnoreCaseComparer.Default);

            foreach (ProjectItemDefinition definition in itemDefinitions.Values)
            {
                _itemDefinitions.Add(new ProjectItemDefinitionInstance(definition));
            }
        }

        /// <summary>
        /// create property snapshot
        /// </summary>
        private void CreatePropertiesSnapshot(ICollection<ProjectProperty> properties, bool isImmutable)
        {
            _properties = new PropertyDictionary<ProjectPropertyInstance>(properties.Count);

            foreach (ProjectProperty property in properties)
            {
                ProjectPropertyInstance instance = InstantiateProjectPropertyInstance(property, isImmutable);
                _properties.Set(instance);
            }
        }

        internal class GenericLoggingContext : LoggingContext
        {
            public GenericLoggingContext(ILoggingService loggingService, BuildEventContext eventContext)
                : base(loggingService, eventContext) => IsValid = true;
        }
    }
}
