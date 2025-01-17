// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Globbing;
using Microsoft.Build.ObjectModelRemoting;
using Microsoft.Build.Shared;
using Constants = Microsoft.Build.Internal.Constants;
using EvaluationItemExpressionFragment = Microsoft.Build.Evaluation.ItemSpec<Microsoft.Build.Evaluation.ProjectProperty, Microsoft.Build.Evaluation.ProjectItem>.ItemExpressionFragment;
using EvaluationItemSpec = Microsoft.Build.Evaluation.ItemSpec<Microsoft.Build.Evaluation.ProjectProperty, Microsoft.Build.Evaluation.ProjectItem>;
using ForwardingLoggerRecord = Microsoft.Build.Logging.ForwardingLoggerRecord;
using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using ObjectModel = System.Collections.ObjectModel;
using ProjectItemFactory = Microsoft.Build.Evaluation.ProjectItem.ProjectItemFactory;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    using Utilities = Microsoft.Build.Internal.Utilities;

    /// <summary>
    /// Represents an evaluated project with design time semantics.
    /// Always backed by XML; can be built directly, or an instance can be cloned off to add virtual items/properties and build.
    /// Edits to this project always update the backing XML.
    /// </summary>
    // UNDONE: (Multiple configurations.) Protect against problems when attempting to edit, after edits were made to the same ProjectRootElement either directly or through other projects evaluated from that ProjectRootElement.
    [DebuggerDisplay("{FullPath} EffectiveToolsVersion={ToolsVersion} {implementation}")]
    public class Project : ILinkableObject
    {
        /// <summary>
        /// Whether to write information about why we evaluate to debug output.
        /// </summary>
        private static readonly bool s_debugEvaluation = (Environment.GetEnvironmentVariable("MSBUILDDEBUGEVALUATION") != null);

        /// <summary>
        /// * and ? are invalid file name characters, but they occur in globs as wild cards.
        /// </summary>
        private static readonly char[] s_invalidGlobChars = FileUtilities.InvalidFileNameChars.Where(c => c != '*' && c != '?' && c != '/' && c != '\\' && c != ':').ToArray();

        /// <summary>
        /// Context to log messages and events in.
        /// </summary>
        private static readonly BuildEventContext s_buildEventContext = new BuildEventContext(0 /* node ID */, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);

        private ProjectLink implementation;
        private IProjectLinkInternal implementationInternal;

        internal bool IsLinked => implementationInternal.IsLinked;
        internal ProjectLink Link => implementation;
        object ILinkableObject.Link => IsLinked ? Link : null;

        /// <summary>
        /// Host-provided factory for <see cref="IDirectoryCache"/> interfaces to be used during evaluation.
        /// </summary>
        private readonly IDirectoryCacheFactory _directoryCacheFactory;

        /// <summary>
        /// Default project template options (include all features).
        /// </summary>
        internal const NewProjectFileOptions DefaultNewProjectTemplateOptions = NewProjectFileOptions.IncludeAllOptions;

        /// <summary>
        /// Certain item operations split the item element in multiple elements if the include
        /// contains globs, references to items or properties, or multiple item values.
        ///
        /// The items operations that may expand item elements are:
        /// - <see cref="RemoveItem"/>
        /// - <see cref="RemoveItems"/>
        /// - <see cref="AddItem(string,string, IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;)"/>
        /// - <see cref="AddItemFast(string,string, IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;)"/>
        /// - <see cref="ProjectItem.ChangeItemType"/>
        /// - <see cref="ProjectItem.Rename"/>
        /// - <see cref="ProjectItem.RemoveMetadata"/>
        /// - <see cref="ProjectItem.SetMetadataValue(string,string)"/>
        /// - <see cref="ProjectItem.SetMetadataValue(string,string, bool)"/>
        ///
        /// When this property is set to true, the previous item operations throw an <see cref="InvalidOperationException" />
        /// instead of expanding the item element.
        /// </summary>
        public bool ThrowInsteadOfSplittingItemElement
        {
            [DebuggerStepThrough]
            get => implementation.ThrowInsteadOfSplittingItemElement;
            [DebuggerStepThrough]
            set => implementation.ThrowInsteadOfSplittingItemElement = value;
        }

        internal Project(ProjectCollection projectCollection, ProjectLink link)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);
            ErrorUtilities.VerifyThrowArgumentNull(link);
            ProjectCollection = projectCollection;
            implementationInternal = new ProjectLinkInternalNotImplemented();
            implementation = link;
        }

        /// <summary>
        /// Construct an empty project, evaluating with the global project collection's
        /// global properties and default tools version.
        /// Project will be added to the global project collection when it is named.
        /// </summary>
        public Project()
            : this(DefaultNewProjectTemplateOptions)
        {
        }

        /// <summary>
        /// Construct an empty project, evaluating with the global project collection's
        /// global properties and default tools version.
        /// Project will be added to the global project collection when it is named.
        /// </summary>
        public Project(NewProjectFileOptions newProjectFileOptions)
            : this(ProjectRootElement.Create(ProjectCollection.GlobalProjectCollection, newProjectFileOptions))
        {
        }

        /// <summary>
        /// Construct an empty project, evaluating with the specified project collection's
        /// global properties and default tools version.
        /// Project will be added to the specified project collection when it is named.
        /// </summary>
        public Project(ProjectCollection projectCollection)
            : this(ProjectRootElement.Create(projectCollection), null, null, projectCollection)
        {
        }

        /// <summary>
        /// Construct an empty project, evaluating with the specified project collection's
        /// global properties and default tools version.
        /// Project will be added to the specified project collection when it is named.
        /// </summary>
        public Project(ProjectCollection projectCollection, NewProjectFileOptions newProjectFileOptions)
            : this(ProjectRootElement.Create(projectCollection, newProjectFileOptions), null, null, projectCollection)
        {
        }

        /// <summary>
        /// Construct an empty project, evaluating with the specified project collection and
        /// the specified global properties and default tools version, either of which may be null.
        /// Project will be added to the specified project collection when it is named.
        /// </summary>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> the project is added to.</param>
        public Project(IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(ProjectRootElement.Create(projectCollection, DefaultNewProjectTemplateOptions), globalProperties, toolsVersion, projectCollection)
        {
        }

        /// <summary>
        /// Construct an empty project, evaluating with the specified project collection and
        /// the specified global properties and default tools version, either of which may be null.
        /// Project will be added to the specified project collection when it is named.
        /// </summary>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> the project is added to.</param>
        /// <param name="newProjectFileOptions">The <see cref="NewProjectFileOptions"/> to use for the new project.</param>
        public Project(IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection, NewProjectFileOptions newProjectFileOptions)
            : this(ProjectRootElement.Create(projectCollection, newProjectFileOptions), globalProperties, toolsVersion, projectCollection)
        {
        }

        /// <summary>
        /// Construct over a ProjectRootElement object, evaluating with the global project collection's
        /// global properties and default tools version.
        /// Project is added to the global project collection if it has a name, or else when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xml">ProjectRootElement to use.</param>
        public Project(ProjectRootElement xml)
            : this(xml, null, null)
        {
        }

        /// <summary>
        /// Construct over a ProjectRootElement object, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project is added to the global project collection if it has a name, or else when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xml">ProjectRootElement to use.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        public Project(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion)
            : this(xml, globalProperties, toolsVersion, ProjectCollection.GlobalProjectCollection)
        {
        }

        /// <summary>
        /// Construct over a ProjectRootElement object, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project is added to the global project collection if it has a name, or else when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xml">ProjectRootElement to use.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> the project is added to.</param>
        public Project(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(xml, globalProperties, toolsVersion, projectCollection, ProjectLoadSettings.Default)
        {
        }

        /// <summary>
        /// Construct over a ProjectRootElement object, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project is added to the global project collection if it has a name, or else when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xml">ProjectRootElement to use.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> the project is added to.</param>
        /// <param name="loadSettings">The <see cref="ProjectLoadSettings"/> to use for evaluation.</param>
        public Project(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(xml, globalProperties, toolsVersion, null /* no explicit sub-toolset version */, projectCollection, loadSettings)
        {
        }

        /// <summary>
        /// Construct over a ProjectRootElement object, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project is added to the global project collection if it has a name, or else when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xml">ProjectRootElement to use.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> the project is added to.</param>
        /// <param name="loadSettings">The <see cref="ProjectLoadSettings"/> to use for evaluation.</param>
        public Project(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(xml, globalProperties, toolsVersion, subToolsetVersion, projectCollection, loadSettings, evaluationContext: null, directoryCacheFactory: null, interactive: false)
        {
        }

        private Project(ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings,
            EvaluationContext evaluationContext, IDirectoryCacheFactory directoryCacheFactory, bool interactive)
        {
            ErrorUtilities.VerifyThrowArgumentNull(xml);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);
            ProjectCollection = projectCollection;
            var defaultImplementation = new ProjectImpl(this, xml, globalProperties, toolsVersion, subToolsetVersion, loadSettings);
            implementationInternal = (IProjectLinkInternal)defaultImplementation;
            implementation = defaultImplementation;

            _directoryCacheFactory = directoryCacheFactory;
            defaultImplementation.Initialize(globalProperties, toolsVersion, subToolsetVersion, loadSettings, evaluationContext, interactive);
        }

        /// <summary>
        /// Construct over a text reader over project xml, evaluating with the global project collection's
        /// global properties and default tools version.
        /// Project will be added to the global project collection when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from.</param>
        public Project(XmlReader xmlReader)
            : this(xmlReader, null, null)
        {
        }

        /// <summary>
        /// Construct over a text reader over project xml, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project will be added to the global project collection when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        public Project(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion)
            : this(xmlReader, globalProperties, toolsVersion, ProjectCollection.GlobalProjectCollection)
        {
        }

        /// <summary>
        /// Construct over a text reader over project xml, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project will be added to the specified project collection when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        public Project(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(xmlReader, globalProperties, toolsVersion, projectCollection, ProjectLoadSettings.Default)
        {
        }

        /// <summary>
        /// Construct over a text reader over project xml, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project will be added to the specified project collection when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        /// <param name="loadSettings">The <see cref="ProjectLoadSettings"/> to use for evaluation.</param>
        public Project(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(xmlReader, globalProperties, toolsVersion, null /* no explicit sub-toolset version */, projectCollection, loadSettings)
        {
        }

        /// <summary>
        /// Construct over a text reader over project xml, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project will be added to the specified project collection when it is named.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from.</param>
        /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
        /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        /// <param name="loadSettings">The load settings for this project.</param>
        public Project(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(xmlReader, globalProperties, toolsVersion, subToolsetVersion, projectCollection, loadSettings, evaluationContext: null, directoryCacheFactory: null, interactive: false)
        {
        }

        private Project(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings,
            EvaluationContext evaluationContext, IDirectoryCacheFactory directoryCacheFactory, bool interactive)
        {
            ErrorUtilities.VerifyThrowArgumentNull(xmlReader);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);
            ProjectCollection = projectCollection;
            var defaultImplementation = new ProjectImpl(this, xmlReader, globalProperties, toolsVersion, subToolsetVersion, loadSettings, evaluationContext);
            implementationInternal = (IProjectLinkInternal)defaultImplementation;
            implementation = defaultImplementation;

            _directoryCacheFactory = directoryCacheFactory;
            defaultImplementation.Initialize(globalProperties, toolsVersion, subToolsetVersion, loadSettings, evaluationContext, interactive);
        }

        /// <summary>
        /// Construct over an existing project file, evaluating with the global project collection's
        /// global properties and default tools version.
        /// Project is added to the global project collection.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// May throw IO-related exceptions.
        /// </summary>
        /// <exception cref="InvalidProjectFileException">If the evaluation fails.</exception>
        public Project(string projectFile)
            : this(projectFile, null, null)
        {
        }

        /// <summary>
        /// Construct over an existing project file, evaluating with specified
        /// global properties and toolset, either or both of which may be null.
        /// Project is added to the global project collection.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// May throw IO-related exceptions.
        /// </summary>
        public Project(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion)
            : this(projectFile, globalProperties, toolsVersion, ProjectCollection.GlobalProjectCollection)
        {
        }

        /// <summary>
        /// Construct over an existing project file, evaluating with the specified global properties and
        /// using the tools version provided, either or both of which may be null.
        /// Project is added to the global project collection.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// May throw IO-related exceptions.
        /// </summary>
        /// <param name="projectFile">The project file.</param>
        /// <param name="globalProperties">The global properties. May be null.</param>
        /// <param name="toolsVersion">The tools version. May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        public Project(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection)
            : this(projectFile, globalProperties, toolsVersion, projectCollection, ProjectLoadSettings.Default)
        {
        }

        /// <summary>
        /// Construct over an existing project file, evaluating with the specified global properties and
        /// using the tools version provided, either or both of which may be null.
        /// Project is added to the global project collection.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// May throw IO-related exceptions.
        /// </summary>
        /// <param name="projectFile">The project file.</param>
        /// <param name="globalProperties">The global properties. May be null.</param>
        /// <param name="toolsVersion">The tools version. May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        /// <param name="loadSettings">The load settings for this project.</param>
        public Project(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(projectFile, globalProperties, toolsVersion, null /* no explicitly specified sub-toolset version */, projectCollection, loadSettings)
        {
        }

        /// <summary>
        /// Construct over an existing project file, evaluating with the specified global properties and
        /// using the tools version provided, either or both of which may be null.
        /// Project is added to the global project collection.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
        /// May throw IO-related exceptions.
        /// </summary>
        /// <param name="projectFile">The project file.</param>
        /// <param name="globalProperties">The global properties. May be null.</param>
        /// <param name="toolsVersion">The tools version. May be null.</param>
        /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
        /// <param name="projectCollection">The collection with which this project should be associated. May not be null.</param>
        /// <param name="loadSettings">The load settings for this project.</param>
        public Project(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings)
            : this(projectFile, globalProperties, toolsVersion, subToolsetVersion, projectCollection, loadSettings, evaluationContext: null, directoryCacheFactory: null, interactive: false)
        {
        }

        private Project(string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectCollection projectCollection, ProjectLoadSettings loadSettings,
            EvaluationContext evaluationContext, IDirectoryCacheFactory directoryCacheFactory, bool interactive)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectFile);
            ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            ProjectCollection = projectCollection;
            var defaultImplementation = new ProjectImpl(this, projectFile, globalProperties, toolsVersion, subToolsetVersion, loadSettings, evaluationContext);
            implementationInternal = (IProjectLinkInternal)defaultImplementation;
            implementation = defaultImplementation;

            _directoryCacheFactory = directoryCacheFactory;

            // Note: not sure why only this ctor flavor do TryUnloadProject
            // seems the XmlReader based one should also clean the same way.
            try
            {
                defaultImplementation.Initialize(globalProperties, toolsVersion, subToolsetVersion, loadSettings, evaluationContext, interactive);
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                // If possible, clear out the XML we just loaded into the XML cache:
                // if we had loaded the XML from disk into the cache within this constructor,
                // and then are are bailing out because there is a typo in the XML such that
                // evaluation failed, we don't want to leave the bad XML in the cache;
                // the user wouldn't be able to fix the XML file and try again.
                projectCollection.TryUnloadProject(Xml);

                throw;
            }
        }

        /// <summary>
        /// Create a file based project.
        /// </summary>
        /// <param name="file">The file to evaluate the project from.</param>
        /// <param name="options">The <see cref="ProjectOptions"/> to use.</param>
        /// <returns></returns>
        public static Project FromFile(string file, ProjectOptions options)
        {
            return new Project(
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
        /// Create a <see cref="ProjectRootElement"/> based project.
        /// </summary>
        /// <param name="rootElement">The <see cref="ProjectRootElement"/> to evaluate the project from.</param>
        /// <param name="options">The <see cref="ProjectOptions"/> to use.</param>
        public static Project FromProjectRootElement(ProjectRootElement rootElement, ProjectOptions options)
        {
            return new Project(
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
        /// Create a <see cref="XmlReader"/> based project.
        /// </summary>
        /// <param name="reader">The <see cref="XmlReader"/> to evaluate the project from.</param>
        /// <param name="options">The <see cref="ProjectOptions"/> to use.</param>
        public static Project FromXmlReader(XmlReader reader, ProjectOptions options)
        {
            return new Project(
                reader,
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
        /// Whether build is enabled for this project.
        /// </summary>
        private enum BuildEnabledSetting
        {
            /// <summary>
            /// Explicitly enabled
            /// </summary>
            BuildEnabled,

            /// <summary>
            /// Explicitly disabled
            /// </summary>
            BuildDisabled,

            /// <summary>
            /// No explicit setting, uses the setting on the
            /// project collection.
            /// This is the default.
            /// </summary>
            UseProjectCollectionSetting
        }

        internal Data TestOnlyGetPrivateData => (Data)implementationInternal.TestOnlyGetPrivateData;

        /// <summary>
        /// Gets or sets the project collection which contains this project.
        /// Can never be null.
        /// Cannot be modified.
        /// </summary>
        public ProjectCollection ProjectCollection { get; }

        /// <summary>
        /// The backing Xml project.
        /// Can never be null.
        /// </summary>
        /// <remarks>
        /// There is no setter here as that doesn't make sense. If you have a new ProjectRootElement, evaluate it into a new Project.
        /// </remarks>
        public ProjectRootElement Xml => implementation.Xml;

        /// <summary>
        /// Whether this project is dirty such that it needs reevaluation.
        /// This may be because its underlying XML has changed (either through this project or another)
        /// either the XML of the main project or an imported file;
        /// or because its toolset may have changed.
        /// </summary>
        public bool IsDirty => implementation.IsDirty;

        /// <summary>
        /// Read only dictionary of the global properties used in the evaluation
        /// of this project.
        /// </summary>
        /// <remarks>
        /// This is the publicly exposed getter, that translates into a read-only dead IDictionary&lt;string, string&gt;.
        ///
        /// In order to easily tell when we're dirtied, setting and removing global properties is done with
        /// <see cref="SetGlobalProperty">SetGlobalProperty</see> and <see cref="RemoveGlobalProperty">RemoveGlobalProperty</see>.
        /// </remarks>
        public IDictionary<string, string> GlobalProperties => implementation.GlobalProperties;

        /// <summary>
        /// Indicates whether the global properties dictionary contains the specified key.
        /// </summary>
        internal bool GlobalPropertiesContains(string key) => implementation.GlobalPropertiesContains(key);

        /// <summary>
        /// Indicates how many elements are in the global properties dictionary.
        /// </summary>
        internal int GlobalPropertiesCount => implementation.GlobalPropertiesCount();

        /// <summary>
        /// Enumerates the values in the global properties dictionary.
        /// </summary>
        internal IEnumerable<KeyValuePair<string, string>> GlobalPropertiesEnumerable => implementation.GlobalPropertiesEnumerable();

        /// <summary>
        /// Item types in this project.
        /// This is an ordered collection.
        /// </summary>
        /// <comments>
        /// data.ItemTypes is a KeyCollection, so it doesn't need any
        /// additional read-only protection.
        /// </comments>
        public ICollection<string> ItemTypes => implementation.ItemTypes;

        /// <summary>
        /// Properties in this project.
        /// Since evaluation has occurred, this is an unordered collection.
        /// </summary>
        public ICollection<ProjectProperty> Properties => implementation.Properties;

        /// <summary>
        /// Collection of possible values implied for properties contained in the conditions found on properties,
        /// property groups, imports, and whens.
        ///
        /// For example, if the following conditions existed on properties in a project:
        ///
        /// Condition="'$(Configuration)|$(Platform)' == 'Debug|x86'"
        /// Condition="'$(Configuration)' == 'Release'"
        ///
        /// the table would be populated with
        ///
        /// { "Configuration", { "Debug", "Release" }}
        /// { "Platform", { "x86" }}
        ///
        /// This is used by Visual Studio to determine the configurations defined in the project.
        /// </summary>
        public IDictionary<string, List<string>> ConditionedProperties => implementation.ConditionedProperties;

        /// <summary>
        /// Read-only dictionary of item definitions in this project.
        /// Keyed by item type.
        /// </summary>
        public IDictionary<string, ProjectItemDefinition> ItemDefinitions => implementation.ItemDefinitions;

        /// <summary>
        /// Items in this project, ordered within groups of item types.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public ICollection<ProjectItem> Items => implementation.Items;

        /// <summary>
        /// Items in this project, ordered within groups of item types,
        /// including items whose conditions evaluated to false, or that were
        /// contained within item groups who themselves had conditioned evaluated to false.
        /// This is useful for hosts that wish to display all items, even if they might not be part
        /// of the build in the current configuration.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public ICollection<ProjectItem> ItemsIgnoringCondition => implementation.ItemsIgnoringCondition;

        /// <summary>
        /// All the files that during evaluation contributed to this project, as ProjectRootElements,
        /// with the ProjectImportElement that caused them to be imported.
        /// This does not include projects that were never imported because a condition on an Import element was false.
        /// The outer ProjectRootElement that maps to this project itself is not included.
        /// </summary>
        /// <remarks>
        /// This can be used by the host to figure out what projects might be impacted by a change to a particular file.
        /// It could also be used, for example, to find the .user file, and use its ProjectRootElement to modify properties in it.
        /// </remarks>
        public IList<ResolvedImport> Imports => implementation.Imports;

        /// <summary>
        /// This list will contain duplicate imports if an import is imported multiple times. However, only the first import was used in evaluation.
        /// </summary>
        public IList<ResolvedImport> ImportsIncludingDuplicates => implementation.ImportsIncludingDuplicates;

        /// <summary>
        /// Targets in the project. The key to the dictionary is the target's name.
        /// Overridden targets are not included in this collection.
        /// This collection is read-only.
        /// </summary>
        public IDictionary<string, ProjectTargetInstance> Targets => implementation.Targets;

        /// <summary>
        /// Properties encountered during evaluation. These are read during the first evaluation pass.
        /// Unlike those returned by the Properties property, these are ordered, and includes any properties that
        /// were subsequently overridden by others with the same name. It does not include any
        /// properties whose conditions did not evaluate to true.
        /// It does not include any properties added since the last evaluation.
        /// </summary>
        public ICollection<ProjectProperty> AllEvaluatedProperties => implementation.AllEvaluatedProperties;

        /// <summary>
        /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
        /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
        /// were subsequently overridden by others with the same name and item type. It does not include any
        /// elements whose conditions did not evaluate to true.
        /// It does not include any item definition metadata added since the last evaluation.
        /// </summary>
        public ICollection<ProjectMetadata> AllEvaluatedItemDefinitionMetadata => implementation.AllEvaluatedItemDefinitionMetadata;

        /// <summary>
        /// Items encountered during evaluation. These are read during the third evaluation pass.
        /// Unlike those returned by the Items property, these are ordered with respect to all other items
        /// encountered during evaluation, not just ordered with respect to items of the same item type.
        /// In some applications, like the F# language, this complete mutual ordering is significant, and such hosts
        /// can use this property.
        /// It does not include any elements whose conditions did not evaluate to true.
        /// It does not include any items added since the last evaluation.
        /// </summary>
        public ICollection<ProjectItem> AllEvaluatedItems => implementation.AllEvaluatedItems;

        /// <summary>
        /// The tools version this project was evaluated with, if any.
        /// Not necessarily the same as the tools version on the Project tag, if any;
        /// it may have been externally specified, for example with a /tv switch.
        /// The actual tools version on the Project tag, can be gotten from <see cref="Xml">Xml.ToolsVersion</see>.
        /// Cannot be changed once the project has been created.
        /// </summary>
        /// <remarks>
        /// Set by construction.
        /// </remarks>
        public string ToolsVersion => implementation.ToolsVersion;

        /// <summary>
        /// The sub-toolset version that, combined with the ToolsVersion, was used to determine
        /// the toolset properties for this project.
        /// </summary>
        public string SubToolsetVersion => implementation.SubToolsetVersion;

        /// <summary>
        /// The root directory for this project.
        /// Is never null: in-memory projects use the current directory from the time of load.
        /// </summary>
        public string DirectoryPath => Xml.DirectoryPath;

        /// <summary>
        /// The full path to this project's file.
        /// May be null, if the project was not loaded from disk.
        /// Setter renames the project, if it already had a name.
        /// </summary>
        public string FullPath
        {
            [DebuggerStepThrough]
            get => Xml.FullPath;
            [DebuggerStepThrough]
            set => Xml.FullPath = value;
        }

        /// <summary>
        /// Whether ReevaluateIfNecessary is temporarily disabled.
        /// This is useful when the host expects to make a number of reads and writes
        /// to the project, and wants to temporarily sacrifice correctness for performance.
        /// </summary>
        public bool SkipEvaluation
        {
            [DebuggerStepThrough]
            get => implementation.SkipEvaluation;
            [DebuggerStepThrough]
            set => implementation.SkipEvaluation = value;
        }

        /// <summary>
        /// Whether <see cref="MarkDirty()">MarkDirty()</see> is temporarily disabled.
        /// This allows, for example, a global property to be set without the project getting
        /// marked dirty for reevaluation as a consequence.
        /// </summary>
        public bool DisableMarkDirty
        {
            [DebuggerStepThrough]
            get => implementation.DisableMarkDirty;
            [DebuggerStepThrough]
            set => implementation.DisableMarkDirty = value;
        }

        /// <summary>
        /// This controls whether or not the building of targets/tasks is enabled for this
        /// project.  This is for security purposes in case a host wants to closely
        /// control which projects it allows to run targets/tasks.  By default, for a newly
        /// created project, we will use whatever setting is in the parent project collection.
        /// When build is disabled, the Build method on this class will fail. However if
        /// the host has already created a ProjectInstance, it can still build it. (It is
        /// free to put a similar check around where it does this.)
        /// </summary>
        public bool IsBuildEnabled
        {
            [DebuggerStepThrough]
            get => implementation.IsBuildEnabled;
            [DebuggerStepThrough]
            set => implementation.IsBuildEnabled = value;
        }

        /// <summary>
        /// Location of the originating file itself, not any specific content within it.
        /// If the file has not been given a name, returns an empty location.
        /// </summary>
        public ElementLocation ProjectFileLocation => Xml.ProjectFileLocation;

        /// <summary>
        /// Obsolete. Use <see cref="LastEvaluationId"/> instead.
        /// </summary>
        // marked as obsolete in 15.3
        public int EvaluationCounter => LastEvaluationId;

        /// <summary>
        /// The ID of the last evaluation for this Project.
        /// A project is always evaluated upon construction and can subsequently get evaluated multiple times via
        /// <see cref="Project.ReevaluateIfNecessary()" />
        ///
        /// It is an arbitrary number that changes when this project reevaluates.
        /// Hosts don't know whether an evaluation actually happened in an interval, but they can compare this number to
        /// their previously stored value to find out, and if so perhaps decide to update their own state.
        /// Note that the number may not increase monotonically.
        ///
        /// This number corresponds to the <see cref="BuildEventContext.EvaluationId"/> and can be used to connect
        /// evaluation logging events back to the Project instance.
        /// </summary>
        public int LastEvaluationId => implementation.LastEvaluationId;

        /// <summary>
        /// List of names of the properties that, while global, are still treated as overridable.
        /// </summary>
        internal ISet<string> GlobalPropertiesToTreatAsLocal => implementationInternal.GlobalPropertiesToTreatAsLocal;

        /// <summary>
        /// The logging service used for evaluation errors.
        /// </summary>
        internal ILoggingService LoggingService => ProjectCollection.LoggingService;

        /// <summary>
        /// Returns the evaluated, escaped value of the provided item's include.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IItem is an internal interface; this is less confusing to outside customers. ")]
        public static string GetEvaluatedItemIncludeEscaped(ProjectItem item)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).EvaluatedIncludeEscaped;
        }

        /// <summary>
        /// Returns the evaluated, escaped value of the provided item definition's include.
        /// </summary>
        public static string GetEvaluatedItemIncludeEscaped(ProjectItemDefinition item)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).EvaluatedIncludeEscaped;
        }

        /// <summary>
        /// Finds all the globs specified in item includes.
        /// </summary>
        /// <example>
        ///
        /// <code>
        /// <![CDATA[
        /// <P>*.txt</P>
        ///
        /// <Bar Include="bar"/> (both outside and inside project cone)
        /// <Zar Include="C:\**\*.foo"/> (both outside and inside project cone)
        /// <Foo Include="*.a;*.b" Exclude="3.a"/>
        /// <Foo Remove="2.a" />
        /// <Foo Include="**\*.b" Exclude="1.b;**\obj\*.b;**\bar\*.b"/>
        /// <Foo Include="$(P)"/>
        /// <Foo Include="*.a;@(Bar);3.a"/> (If Bar has globs, they will have been included when querying Bar ProjectItems for globs)
        /// <Foo Include="*.cs" Exclude="@(Bar)"/>
        /// ]]>
        /// </code>
        ///
        /// Example result:
        /// <code>
        /// <![CDATA[
        /// [
        /// GlobResult(glob: "C:\**\*.foo", exclude: []),
        /// GlobResult(glob: ["*.a", "*.b"], exclude=["3.a"], remove=["2.a"]),
        /// GlobResult(glob: "**\*.b", exclude=["1.b, **\obj\*.b", **\bar\*.b"]),
        /// GlobResult(glob: "*.txt", exclude=[]),
        /// GlobResult(glob: "*.a", exclude=[]),
        /// GlobResult(glob: "*.cs", exclude=["bar"])
        /// ].
        /// ]]>
        /// </code>
        /// </example>
        /// <remarks>
        /// <para>
        /// <see cref="GlobResult.MsBuildGlob"/> is a <see cref="IMSBuildGlob"/> that combines all globs in the include element and ignores
        /// all the fragments in the exclude attribute and all the fragments in all Remove elements that apply to the include element.
        /// </para>
        ///
        /// Users can construct a composite glob that incorporates all the globs in the Project:
        /// <code>
        /// <![CDATA[
        /// var uberGlob = new CompositeGlob(project.GetAllGlobs().Select(r => r.MSBuildGlob).ToArray());
        /// uberGlob.IsMatch("foo.cs");
        /// ]]>
        /// </code>
        ///
        /// </remarks>
        /// <returns>
        /// List of <see cref="GlobResult"/>.
        /// </returns>
        public List<GlobResult> GetAllGlobs()
        {
            return GetAllGlobs(evaluationContext: null);
        }

        /// <summary>
        /// See <see cref="GetAllGlobs()"/>.
        /// </summary>
        /// <param name="evaluationContext">
        ///     The evaluation context to use in case reevaluation is required.
        ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
        /// </param>
        public List<GlobResult> GetAllGlobs(EvaluationContext evaluationContext)
        {
            return implementation.GetAllGlobs(evaluationContext);
        }

        /// <summary>
        /// Overload of <see cref="GetAllGlobs()"/>.
        /// </summary>
        /// <param name="itemType">Confine search to item elements of this type.</param>
        public List<GlobResult> GetAllGlobs(string itemType)
        {
            return implementation.GetAllGlobs(itemType, null);
        }

        /// <summary>
        /// See <see cref="GetAllGlobs(string)"/>.
        /// </summary>
        /// <param name="itemType">Type of the item.</param>
        /// <param name="evaluationContext">
        ///     The evaluation context to use in case reevaluation is required.
        ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
        /// </param>
        public List<GlobResult> GetAllGlobs(string itemType, EvaluationContext evaluationContext)
        {
            return implementation.GetAllGlobs(itemType, evaluationContext);
        }

        /// <summary>
        /// Finds all the item elements in the logical project with itemspecs that match the given string:
        /// - elements that would include (or exclude) the string
        /// - elements that would update the string (not yet implemented)
        /// - elements that would remove the string (not yet implemented).
        /// </summary>
        ///
        /// <example>
        /// The following snippet shows what <c>GetItemProvenance("a.cs")</c> returns for various item elements.
        /// <code>
        /// <A Include="a.cs;*.cs"/> // Occurrences:2; Operation: Include; Provenance: StringLiteral | Glob
        /// <B Include="*.cs" Exclude="a.cs"/> // Occurrences: 1; Operation: Exclude; Provenance: StringLiteral
        /// <C Include="b.cs"/> // NA
        /// <D Include="@(A)"/> // Occurrences: 2; Operation: Include; Provenance: Inconclusive (it is an indirect occurrence from a referenced item)
        /// <E Include="$(P)"/> // Occurrences: 4; Operation: Include; Provenance: FromLiteral (direct reference in $P) | Glob (direct reference in $P) | Inconclusive (it is an indirect occurrence from referenced properties and items)
        /// <PropertyGroup>
        ///     <P>a.cs;*.cs;@(A)</P>
        /// </PropertyGroup>
        /// </code>
        ///
        /// </example>
        ///
        /// <remarks>
        /// This method and its overloads are useful for clients that need to inspect all the item elements
        /// that might refer to a specific item instance. For example, Visual Studio uses it to inspect
        /// projects with globs. Upon a file system or IDE file artifact change, VS calls this method to find all the items
        /// that might refer to the detected file change (e.g. 'which item elements refer to "Program.cs"?').
        /// It uses such information to know which elements it should edit to reflect the user or file system changes.
        ///
        /// Literal string matching tries to first match the strings. If the check fails, it then tries to match
        /// the strings as if they represented files: it normalizes both strings as files relative to the current project directory
        ///
        /// GetItemProvenance suffers from some sources of inaccuracy:
        /// - it is performed after evaluation, thus is insensitive to item data flow when item references are present
        /// (it sees items as they are at the end of evaluation)
        ///
        /// This API and its return types are prone to change.
        /// </remarks>
        ///
        /// <param name="itemToMatch">The string to perform matching against.</param>
        ///
        /// <returns>
        /// A list of <see cref="ProvenanceResult"/>, sorted in project evaluation order.
        /// </returns>
        public List<ProvenanceResult> GetItemProvenance(string itemToMatch)
        {
            return GetItemProvenance(itemToMatch, evaluationContext: null);
        }

        /// <summary>
        /// See <see cref="GetItemProvenance(string)"/>.
        /// </summary>
        /// <param name="itemToMatch">The string to perform matching against.</param>
        /// <param name="evaluationContext">
        ///     The evaluation context to use in case reevaluation is required.
        ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
        /// </param>
        public List<ProvenanceResult> GetItemProvenance(string itemToMatch, EvaluationContext evaluationContext)
        {
            return implementation.GetItemProvenance(itemToMatch, evaluationContext);
        }

        /// <summary>
        /// Overload of <see cref="GetItemProvenance(string)"/>.
        /// </summary>
        /// <param name="itemToMatch">The string to perform matching against.</param>
        /// <param name="itemType">The item type to constrain the search in.</param>
        public List<ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType)
        {
            return GetItemProvenance(itemToMatch, itemType, null);
        }

        /// <summary>
        /// See <see cref="GetItemProvenance(string, string)"/>.
        /// </summary>
        /// <param name="itemToMatch">The string to perform matching against.</param>
        /// <param name="itemType">The type of the item to perform matching against.</param>
        /// <param name="evaluationContext">
        ///     The evaluation context to use in case reevaluation is required.
        ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
        /// </param>
        public List<ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType, EvaluationContext evaluationContext)
        {
            return implementation.GetItemProvenance(itemToMatch, itemType, evaluationContext);
        }

        /// <summary>
        /// Overload of <see cref="GetItemProvenance(string)"/>.
        /// </summary>
        /// <param name="item">
        /// The ProjectItem object that indicates: the itemspec to match and the item type to constrain the search in.
        /// The search is also constrained on item elements appearing before the item element that produced this <paramref name="item"/>.
        /// The element that produced this <paramref name="item"/> is included in the results.
        /// </param>
        public List<ProvenanceResult> GetItemProvenance(ProjectItem item)
        {
            return implementation.GetItemProvenance(item, null);
        }

        /// <summary>
        /// See <see cref="GetItemProvenance(ProjectItem)"/>.
        /// </summary>
        /// <param name="item">
        /// The ProjectItem object that indicates: the itemspec to match and the item type to constrain the search in.
        /// The search is also constrained on item elements appearing before the item element that produced this <paramref name="item"/>.
        /// The element that produced this <paramref name="item"/> is included in the results.
        /// </param>
        /// <param name="evaluationContext">
        ///     The evaluation context to use in case reevaluation is required.
        ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
        /// </param>
        public List<ProvenanceResult> GetItemProvenance(ProjectItem item, EvaluationContext evaluationContext)
        {
            return implementation.GetItemProvenance(item, evaluationContext);
        }

        /// <summary>
        /// Gets the escaped value of the provided metadatum.
        /// </summary>
        public static string GetMetadataValueEscaped(ProjectMetadata metadatum)
        {
            ErrorUtilities.VerifyThrowArgumentNull(metadatum);

            return metadatum.EvaluatedValueEscaped;
        }

        /// <summary>
        /// Gets the escaped value of the metadatum with the provided name on the provided item.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IItem is an internal interface; this is less confusing to outside customers. ")]
        public static string GetMetadataValueEscaped(ProjectItem item, string name)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).GetMetadataValueEscaped(name);
        }

        /// <summary>
        /// Gets the escaped value of the metadatum with the provided name on the provided item definition.
        /// </summary>
        public static string GetMetadataValueEscaped(ProjectItemDefinition item, string name)
        {
            ErrorUtilities.VerifyThrowArgumentNull(item);

            return ((IItem)item).GetMetadataValueEscaped(name);
        }

        /// <summary>
        /// Get the escaped value of the provided property.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "IProperty is an internal interface; this is less confusing to outside customers. ")]
        public static string GetPropertyValueEscaped(ProjectProperty property)
        {
            ErrorUtilities.VerifyThrowArgumentNull(property);

            return ((IProperty)property).EvaluatedValueEscaped;
        }

        /// <summary>
        /// Returns an iterator over the "logical project". The logical project is defined as
        /// the unevaluated project obtained from the single MSBuild file that is the result
        /// of inlining the text of all imports of the original MSBuild project manifest file.
        /// </summary>
        public IEnumerable<ProjectElement> GetLogicalProject()
        {
            return implementation.GetLogicalProject();
        }

        /// <summary>
        /// Get any property in the project that has the specified name,
        /// otherwise returns null.
        /// </summary>
        [DebuggerStepThrough]
        public ProjectProperty GetProperty(string name)
        {
            return implementation.GetProperty(name);
        }

        /// <summary>
        /// Get the unescaped value of a property in this project, or
        /// an empty string if it does not exist.
        /// </summary>
        /// <remarks>
        /// A property with a value of empty string and no property
        /// at all are not distinguished between by this method.
        /// That makes it easier to use. To find out if a property is set at
        /// all in the project, use GetProperty(name).
        /// </remarks>
        public string GetPropertyValue(string name)
        {
            return implementation.GetPropertyValue(name);
        }

        /// <summary>
        /// Set or add a property with the specified name and value.
        /// Overwrites the value of any property with the same name already in the collection if it did not originate in an imported file.
        /// If there is no such existing property, uses this heuristic:
        /// Updates the last existing property with the specified name that has no condition on itself or its property group, if any,
        /// and is in this project file rather than an imported file.
        /// Otherwise, adds a new property in the first property group without a condition, creating a property group if necessary after
        /// the last existing property group, else at the start of the project.
        /// Returns the property set.
        /// Evaluates on a best-effort basis:
        ///     -expands with all properties. Properties that are defined in the XML below the new property may be used, even though in a real evaluation they would not be.
        ///     -only this property is evaluated. Anything else that would depend on its value is not affected.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        public ProjectProperty SetProperty(string name, string unevaluatedValue)
        {
            return implementation.SetProperty(name, unevaluatedValue);
        }

        /// <summary>
        /// Change a global property after the project has been evaluated.
        /// If the value changes, this makes the project require reevaluation.
        /// If the value changes, returns true, otherwise false.
        /// </summary>
        public bool SetGlobalProperty(string name, string escapedValue)
        {
            return implementation.SetGlobalProperty(name, escapedValue);
        }

        /// <summary>
        /// Adds an item with no metadata to the project.
        /// Any metadata can be added subsequently.
        /// Does not modify the XML if a wildcard expression would already include the new item.
        /// Evaluates on a best-effort basis:
        ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
        ///     -only this item is evaluated. Other items that might depend on it is not affected.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        public IList<ProjectItem> AddItem(string itemType, string unevaluatedInclude)
        {
            return AddItem(itemType, unevaluatedInclude, null);
        }

        /// <summary>
        /// Adds an item with metadata to the project.
        /// Metadata may be null, indicating no metadata.
        /// Does not modify the XML if a wildcard expression would already include the new item.
        /// Evaluates on a best-effort basis:
        ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
        ///     -only this item is evaluated. Other items that might depend on it is not affected.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        public IList<ProjectItem> AddItem(string itemType, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            return implementation.AddItem(itemType, unevaluatedInclude, metadata);
        }

        /// <summary>
        /// Adds an item with no metadata to the project.
        /// Makes no effort to see if an existing wildcard would already match the new item, unless it is the first item in an item group.
        /// Makes no effort to locate the new item near similar items.
        /// Appends the item to the first item group that does not have a condition and has either no children or whose first child is an item of the same type.
        /// Evaluates on a best-effort basis:
        ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
        ///     -only this item is evaluated. Other items that might depend on it is not affected.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        public IList<ProjectItem> AddItemFast(string itemType, string unevaluatedInclude)
        {
            return AddItemFast(itemType, unevaluatedInclude, null);
        }

        /// <summary>
        /// Adds an item with metadata to the project.
        /// Metadata may be null, indicating no metadata.
        /// Makes no effort to see if an existing wildcard would already match the new item, unless it is the first item in an item group.
        /// Makes no effort to locate the new item near similar items.
        /// Appends the item to the first item group that does not have a condition and has either no children or whose first child is an item of the same type.
        /// Evaluates on a best-effort basis:
        ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
        ///     -only this item is evaluated. Other items that might depend on it is not affected.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        public IList<ProjectItem> AddItemFast(string itemType, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            return implementation.AddItemFast(itemType, unevaluatedInclude, metadata);
        }

        /// <summary>
        /// All the items in the project of the specified
        /// type.
        /// If there are none, returns an empty list.
        /// Use AddItem or RemoveItem to modify items in this project.
        /// </summary>
        /// <comments>
        /// data.GetItems returns a read-only collection, so no need to re-wrap it here.
        /// </comments>
        public ICollection<ProjectItem> GetItems(string itemType)
        {
            return implementation.GetItems(itemType);
        }

        /// <summary>
        /// All the items in the project of the specified
        /// type, irrespective of whether the conditions on them evaluated to true.
        /// This is a read-only list: use AddItem or RemoveItem to modify items in this project.
        /// </summary>
        /// <comments>
        /// ItemDictionary[] returns a read only collection, so no need to wrap it.
        /// </comments>
        public ICollection<ProjectItem> GetItemsIgnoringCondition(string itemType)
        {
            return implementation.GetItemsIgnoringCondition(itemType);
        }

        /// <summary>
        /// Returns all items that have the specified evaluated include.
        /// For example, all items that have the evaluated include "bar.cpp".
        /// Typically there will be zero or one, but sometimes there are two items with the
        /// same path and different item types, or even the same item types. This will return
        /// them all.
        /// </summary>
        /// <comments>
        /// data.GetItemsByEvaluatedInclude already returns a read-only collection, so no need
        /// to wrap it further.
        /// </comments>
        public ICollection<ProjectItem> GetItemsByEvaluatedInclude(string evaluatedInclude)
        {
            return implementation.GetItemsByEvaluatedInclude(evaluatedInclude);
        }

        /// <summary>
        /// Removes the specified property.
        /// Property must be associated with this project.
        /// Property must not originate from an imported file.
        /// Returns true if the property was in this evaluated project, otherwise false.
        /// As a convenience, if the parent property group becomes empty, it is also removed.
        /// Updates the evaluated project, but does not affect anything else in the project until reevaluation. For example,
        /// if "p" is removed, it will be removed from the evaluated project, but "q" which is evaluated from "$(p)" will not be modified until reevaluation.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state.
        /// </summary>
        public bool RemoveProperty(ProjectProperty property)
        {
            return implementation.RemoveProperty(property);
        }

        /// <summary>
        /// Removes a global property.
        /// If it was set, returns true, and marks the project
        /// as requiring reevaluation.
        /// </summary>
        public bool RemoveGlobalProperty(string name)
        {
            return implementation.RemoveGlobalProperty(name);
        }

        /// <summary>
        /// Removes an item from the project.
        /// Item must be associated with this project.
        /// Item must not originate from an imported file.
        /// Returns true if the item was in this evaluated project, otherwise false.
        /// As a convenience, if the parent item group becomes empty, it is also removed.
        /// If the item originated from a wildcard or semicolon separated expression, expands that expression into multiple items first.
        /// Updates the evaluated project, but does not affect anything else in the project until reevaluation. For example,
        /// if an item of type "i" is removed, "j" which is evaluated from "@(i)" will not be modified until reevaluation.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
        /// </summary>
        /// <remarks>
        /// Normally this will return true, since if the item isn't in the project, it will throw.
        /// The exception is removing an item that was only in ItemsIgnoringCondition.
        /// </remarks>
        public bool RemoveItem(ProjectItem item)
        {
            return implementation.RemoveItem(item);
        }

        /// <summary>
        /// Removes all the specified items from the project.
        /// Items that are not associated with this project are skipped.
        /// </summary>
        /// <remarks>
        /// Removing one item could cause the backing XML
        /// to be expanded, which could zombie (disassociate) the next item.
        /// To make this case easy for the caller, if an item
        /// is not associated with this project it is simply skipped.
        /// </remarks>
        public void RemoveItems(IEnumerable<ProjectItem> items)
        {
            implementation.RemoveItems(items);
        }

        /// <summary>
        /// Evaluates the provided string by expanding items and properties,
        /// as if it was found at the very end of the project file.
        /// This is useful for some hosts for which this kind of best-effort
        /// evaluation is sufficient.
        /// Does not expand bare metadata expressions.
        /// </summary>
        public string ExpandString(string unexpandedValue)
        {
            return implementation.ExpandString(unexpandedValue);
        }

        /// <summary>
        /// Returns an instance based on this project, but completely disconnected.
        /// This instance can be used to build independently.
        /// Before creating the instance, this will reevaluate the project if necessary, so it will not be dirty.
        /// </summary>
        /// <returns>The created project instance.</returns>
        public ProjectInstance CreateProjectInstance()
        {
            return CreateProjectInstance(ProjectInstanceSettings.None, null);
        }

        /// <summary>
        /// Returns an instance based on this project, but completely disconnected.
        /// This instance can be used to build independently.
        /// Before creating the instance, this will reevaluate the project if necessary, so it will not be dirty.
        /// The instance is immutable; none of the objects that form it can be modified. This makes it safe to
        /// access concurrently from multiple threads.
        /// </summary>
        /// <param name="settings">The project instance creation settings.</param>
        /// <returns>The created project instance.</returns>
        public ProjectInstance CreateProjectInstance(ProjectInstanceSettings settings)
        {
            return CreateProjectInstance(settings, null);
        }

        /// <summary>
        /// See <see cref="CreateProjectInstance(ProjectInstanceSettings)"/>.
        /// </summary>
        /// <param name="settings">The project instance creation settings.</param>
        /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
        /// <returns>The created project instance.</returns>
        public ProjectInstance CreateProjectInstance(ProjectInstanceSettings settings, EvaluationContext evaluationContext)
        {
            return implementation.CreateProjectInstance(settings, evaluationContext);
        }

        /// <summary>
        /// Called to forcibly mark the project as dirty requiring reevaluation. Generally this is not necessary to set; all edits affecting
        /// this project will automatically make it dirty. However there are potential corner cases where it is necessary to mark the project dirty
        /// directly. For example, if the project has an import conditioned on a file existing on disk, and the file did not exist at
        /// evaluation time, then someone subsequently creates that file, the project cannot know that reevaluation would be productive.
        /// In such a case the host can help us by setting the dirty flag explicitly so that <see cref="ReevaluateIfNecessary()">ReevaluateIfNecessary()</see>
        /// will recognize an evaluation is indeed necessary.
        /// Does not mark the underlying project file as requiring saving.
        /// </summary>
        public void MarkDirty()
        {
            implementation.MarkDirty();
        }

        /// <summary>
        /// Reevaluate the project to get it into a queryable state, if it's dirty.
        /// This incorporates all changes previously made to the backing XML by editing this project.
        /// Throws InvalidProjectFileException if the evaluation fails.
        /// </summary>
        public void ReevaluateIfNecessary()
        {
            implementation.ReevaluateIfNecessary(null);
        }

        /// <summary>
        /// See <see cref="ReevaluateIfNecessary()"/>.
        /// </summary>
        /// <param name="evaluationContext">The <see cref="EvaluationContext"/> to use. See <see cref="EvaluationContext"/>.</param>
        public void ReevaluateIfNecessary(EvaluationContext evaluationContext)
        {
            implementation.ReevaluateIfNecessary(evaluationContext);
        }

        /// <summary>
        /// Save the project to the file system, if dirty.
        /// Uses the default encoding.
        /// </summary>
        public void Save()
        {
            Xml.Save();
        }

        /// <summary>
        /// Save the project to the file system, if dirty.
        /// </summary>
        public void Save(Encoding encoding)
        {
            Xml.Save(encoding);
        }

        /// <summary>
        /// Save the project to the file system, if dirty or the path is different.
        /// Uses the default encoding.
        /// </summary>
        public void Save(string path)
        {
            Xml.Save(path);
        }

        /// <summary>
        /// Save the project to the file system, if dirty or the path is different.
        /// </summary>
        public void Save(string path, Encoding encoding)
        {
            Xml.Save(path, encoding);
        }

        /// <summary>
        /// Save the project to the provided TextWriter, whether or not it is dirty.
        /// Uses the encoding of the TextWriter.
        /// Clears the Dirty flag.
        /// </summary>
        public void Save(TextWriter writer)
        {
            Xml.Save(writer);
        }

        /// <summary>
        /// Saves a "logical" or "preprocessed" project file, that includes all the imported
        /// files as if they formed a single file.
        /// </summary>
        public void SaveLogicalProject(TextWriter writer)
        {
            implementation.SaveLogicalProject(writer);
        }

        /// <summary>
        /// Starts a build using this project, building the default targets.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build()
        {
            return Build((string[])null);
        }

        /// <summary>
        /// Starts a build using this project, building the default targets and the specified logger.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="logger">Logger to use.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(ILogger logger)
        {
            var loggers = new List<ILogger>(1) { logger };
            return Build((string[])null, loggers, null);
        }

        /// <summary>
        /// Starts a build using this project, building the default targets and the specified loggers.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="loggers">List of loggers.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(IEnumerable<ILogger> loggers)
        {
            return Build((string[])null, loggers, null);
        }

        /// <summary>
        /// Starts a build using this project, building the default targets and the specified loggers.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="loggers">List of loggers.</param>
        /// <param name="remoteLoggers">Remote loggers for multi proc logging.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            return Build((string[])null, loggers, remoteLoggers);
        }

        /// <summary>
        /// Starts a build using this project, building the specified target.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="target">Target to build.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string target)
        {
            return Build(target, null, null);
        }

        /// <summary>
        /// Starts a build using this project, building the specified target with the specified loggers.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="target">Target to build.</param>
        /// <param name="loggers">List of loggers.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string target, IEnumerable<ILogger> loggers)
        {
            return Build(target, loggers, null);
        }

        /// <summary>
        /// Starts a build using this project, building the specified target with the specified loggers.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="target">Target to build.</param>
        /// <param name="loggers">List of loggers.</param>
        /// <param name="remoteLoggers">Remote loggers for multi proc logging.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string target, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            // targets may be null, but not an entry within it
            string[] targets = (target == null) ? null : [target];

            return Build(targets, loggers, remoteLoggers);
        }

        /// <summary>
        /// Starts a build using this project, building the specified targets.
        /// Returns true on success, false on failure.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="targets">Targets to build.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string[] targets)
        {
            return Build(targets, null, null);
        }

        /// <summary>
        /// Starts a build using this project, building the specified targets with the specified loggers.
        /// Returns true on success, false on failure.
        /// If build is disabled on this project, does not build, and returns false.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="targets">Targets to build.</param>
        /// <param name="loggers">List of loggers.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers)
        {
            return Build(targets, loggers, null);
        }

        /// <summary>
        /// Starts a build using this project, building the specified targets with the specified loggers.
        /// Returns true on success, false on failure.
        /// If build is disabled on this project, does not build, and returns false.
        /// Works on a privately cloned instance. To set or get
        /// virtual items for build purposes, clone an instance explicitly and build that.
        /// Does not modify the Project object.
        /// </summary>
        /// <param name="targets">Targets to build.</param>
        /// <param name="loggers">List of loggers.</param>
        /// <param name="remoteLoggers">Remote loggers for multi proc logging.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            return Build(targets, loggers, remoteLoggers, null);
        }

        /// <summary>
        /// See <see cref="Build(string[], IEnumerable&lt;ILogger&gt;, IEnumerable&lt;ForwardingLoggerRecord&gt;)"/>.
        /// </summary>
        /// <param name="targets">Targets to build.</param>
        /// <param name="loggers">List of loggers.</param>
        /// <param name="remoteLoggers">Remote loggers for multi proc logging.</param>
        /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
        /// <returns>Returns true on success and false on failure or disabled build.</returns>
        public bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, EvaluationContext evaluationContext)
        {
            return implementation.Build(targets, loggers, remoteLoggers, evaluationContext);
        }

        /// <summary>
        /// Tests whether a given project IS or IMPORTS some given project xml root element.
        /// </summary>
        /// <param name="xmlRootElement">The project xml root element in question.</param>
        /// <returns>True if this project is or imports the xml file; false otherwise.</returns>
        internal bool UsesProjectRootElement(ProjectRootElement xmlRootElement)
        {
            return implementationInternal.UsesProjectRootElement(xmlRootElement);
        }

        /// <summary>
        /// If the ProjectItemElement evaluated to more than one ProjectItem, replaces it with a new ProjectItemElement for each one of them.
        /// If the ProjectItemElement did not evaluate into more than one ProjectItem, does nothing.
        /// Returns true if a split occurred, otherwise false.
        /// </summary>
        /// <remarks>
        /// A ProjectItemElement could have resulted in several items if it contains wildcards or item or property expressions.
        /// Before any edit to a ProjectItem (remove, rename, set metadata, or remove metadata) this must be called to make
        /// sure that the edit does not affect any other ProjectItems originating in the same ProjectItemElement.
        ///
        /// For example, an item xml with an include of "@(x)" could evaluate to items "a", "b", and "c". If "b" is removed, then the original
        /// item xml must be removed and replaced with three, then the one corresponding to "b" can be removed.
        ///
        /// This is an unsophisticated approach; the best that can be said is that the result will likely be correct, if not ideal.
        /// For example, perhaps the user would rather remove the item from the original list "x" instead of expanding the list.
        /// Or, perhaps the user would rather the property in "$(p)\a;$(p)\b" not be expanded when "$(p)\b" is removed.
        /// If that's important, the host can manipulate the ProjectItemElement's directly, instead, and it can be as fastidious as it wishes.
        /// </remarks>
        internal bool SplitItemElementIfNecessary(ProjectItemElement itemElement)
        {
            if (!ItemElementRequiresSplitting(itemElement))
            {
                return false;
            }

            ErrorUtilities.VerifyThrowInvalidOperation(!ThrowInsteadOfSplittingItemElement, "OM_CannotSplitItemElementWhenSplittingIsDisabled", itemElement.Location, $"{nameof(Project)}.{nameof(ThrowInsteadOfSplittingItemElement)}");

            var relevantItems = new List<ProjectItem>();

            foreach (ProjectItem item in Items)
            {
                if (item.Xml == itemElement)
                {
                    relevantItems.Add(item);
                }
            }

            foreach (ProjectItem item in relevantItems)
            {
                item.SplitOwnItemElement();
            }

            itemElement.Parent.RemoveChild(itemElement);

            return true;
        }

        internal bool ItemElementRequiresSplitting(ProjectItemElement itemElement)
        {
            var hasCharactersThatRequireSplitting = FileMatcher.HasWildcardsSemicolonItemOrPropertyReferences(itemElement.Include);

            return hasCharactersThatRequireSplitting;
        }

        /// <summary>
        /// Examines the provided ProjectItemElement to see if it has a wildcard that would match the
        /// item we wish to add, and does not have a condition or an exclude.
        /// Works conservatively - if there is anything that might cause doubt, considers the candidate to not be suitable.
        /// Returns true if it is suitable, otherwise false.
        /// </summary>
        /// <remarks>
        /// Outside this class called ONLY from <see cref="ProjectItem.Rename(string)"/>ProjectItem.Rename(string name).
        /// </remarks>
        internal bool IsSuitableExistingItemXml(ProjectItemElement candidateExistingItemXml, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            return implementationInternal.IsSuitableExistingItemXml(candidateExistingItemXml, unevaluatedInclude, metadata);
        }

        /// <summary>
        /// Before an item changes its item type, it must be removed from
        /// our datastructures, which key off item type.
        /// This should be called ONLY by ProjectItems, in this situation.
        /// </summary>
        internal void RemoveItemBeforeItemTypeChange(ProjectItem item)
        {
            implementationInternal.RemoveItemBeforeItemTypeChange(item);
        }

        /// <summary>
        /// After an item has changed its item type, it needs to be added back again,
        /// since our data structures key off the item type.
        /// This should be called ONLY by ProjectItems, in this situation.
        /// </summary>
        internal void ReAddExistingItemAfterItemTypeChange(ProjectItem item)
        {
            implementationInternal.ReAddExistingItemAfterItemTypeChange(item);
        }

        /// <summary>
        /// Provided a property that is already part of this project, does a best-effort expansion
        /// of the unevaluated value provided and sets it as the evaluated value.
        /// </summary>
        /// <remarks>
        /// On project in order to keep Project's expander hidden.
        /// </remarks>
        internal string ExpandPropertyValueBestEffortLeaveEscaped(string unevaluatedValue, ElementLocation propertyLocation)
        {
            return implementationInternal.ExpandPropertyValueBestEffortLeaveEscaped(unevaluatedValue, propertyLocation);
        }

        /// <summary>
        /// Provided an item element that has been renamed with a new unevaluated include,
        /// returns a best effort guess at the evaluated include that results.
        /// If the best effort expansion produces anything other than one item, it just
        /// returns the unevaluated include.
        /// This is not at all generalized, but useful for the majority case where an item is a very
        /// simple file name with perhaps a property prefix.
        /// </summary>
        /// <remarks>
        /// On project in order to keep Project's expander hidden.
        /// </remarks>
        internal string ExpandItemIncludeBestEffortLeaveEscaped(ProjectItemElement renamedItemElement)
        {
            return implementationInternal.ExpandItemIncludeBestEffortLeaveEscaped(renamedItemElement);
        }

        /// <summary>
        /// Provided a metadatum that is already part of this project, does a best-effort expansion
        /// of the unevaluated value provided and returns the resulting value.
        /// This is a interim expansion only: it may not be the value that a full project reevaluation would produce.
        /// The metadata table passed in is that of the parent item or item definition.
        /// </summary>
        /// <remarks>
        /// On project in order to keep Project's expander hidden.
        /// </remarks>
        internal string ExpandMetadataValueBestEffortLeaveEscaped(IMetadataTable metadataTable, string unevaluatedValue, ElementLocation metadataLocation)
        {
            return implementationInternal.ExpandMetadataValueBestEffortLeaveEscaped(metadataTable, unevaluatedValue, metadataLocation);
        }

        /// <summary>
        /// Called by the project collection to indicate to this project that it is no longer loaded.
        /// </summary>
        internal void Zombify()
        {
            implementation.Unload();
            implementationInternal.IsZombified = true;
        }

        /// <summary>
        /// Verify that the project has not been unloaded from its collection.
        /// Once it's been unloaded, it cannot be used.
        /// </summary>
        internal void VerifyThrowInvalidOperationNotZombie()
        {
            ErrorUtilities.VerifyThrow(!implementationInternal.IsZombified, "OM_ProjectIsNoLongerActive");
        }

        /// <summary>
        /// Verify that the provided object location is in the same file as the project.
        /// If it is not, throws an InvalidOperationException indicating that imported evaluated objects should not be modified.
        /// This prevents, for example, accidentally updating something like the OutputPath property, that you want be in the
        /// main project, but for some reason was actually read in from an imported targets file.
        /// </summary>
        internal void VerifyThrowInvalidOperationNotImported(ProjectRootElement otherXml)
        {
            ErrorUtilities.VerifyThrowInternalNull(otherXml);
            ErrorUtilities.VerifyThrowInvalidOperation(ReferenceEquals(Xml, otherXml), "OM_CannotModifyEvaluatedObjectInImportedFile", otherXml.Location.File);
        }

        /// <summary>
        /// Internal project evaluation implementation.
        /// </summary>
        [DebuggerDisplay("#GlobalProperties={_data.GlobalPropertiesDictionary.Count} #Properties={_data.Properties.Count} #ItemTypes={_data.ItemTypes.Count} #ItemDefinitions={_data.ItemDefinitions.Count} #Items={_data.Items.Count} #Targets={_data.Targets.Count}")]
        private class ProjectImpl : ProjectLink, IProjectLinkInternal
        {
            /// <summary>
            /// Backing data; stored in a nested class so it can be passed to the Evaluator to fill
            /// in on re-evaluation, without having to expose property setters for that purpose.
            /// Also it makes it easy to re-evaluate this project without creating a new project object.
            /// </summary>
            private Data _data;

            /// <summary>
            /// The highest version of the backing ProjectRootElements (including imports) that this object was last evaluated from.
            /// Edits to the ProjectRootElement either by this Project or another Project increment the number.
            /// If that number is different from this one a reevaluation is necessary at some point.
            /// </summary>
            private int _evaluatedVersion;

            /// <summary>
            /// The version of the tools information in the project collection against we were last evaluated.
            /// </summary>
            private int _evaluatedToolsetCollectionVersion;

            /// <summary>
            /// Whether the project has been explicitly marked as dirty. Generally this is not necessary to set; all edits affecting
            /// this project will automatically make it dirty. However there are potential corner cases where it is necessary to mark it dirty
            /// directly. For example, if the project has an import conditioned on a file existing on disk, and the file did not exist at
            /// evaluation time, then someone subsequently writes the file, the project will not know that reevaluation would be productive,
            /// and would not dirty itself. In such a case the host should help us by setting the dirty flag explicitly.
            /// </summary>
            private bool _explicitlyMarkedDirty;

            /// <summary>
            /// This controls whether or not the building of targets/tasks is enabled for this
            /// project.  This is for security purposes in case a host wants to closely
            /// control which projects it allows to run targets/tasks.
            /// </summary>
            private BuildEnabledSetting _isBuildEnabled = BuildEnabledSetting.UseProjectCollectionSetting;

            /// <summary>
            /// The load settings, such as to ignore missing imports.
            /// This is retained after construction as it will be needed for reevaluation.
            /// </summary>
            private ProjectLoadSettings _loadSettings;

            /// <summary>
            /// The delegate registered with the ProjectRootElement to be called if the file name
            /// is changed. Retained so that ultimately it can be unregistered.
            /// If it has been set to null, the project has been unloaded from its collection.
            /// </summary>
            private RenameHandlerDelegate _renameHandler;

            /// <summary>
            /// Indicates if the process of loading the project is allowed to interact with the user.
            /// </summary>
            private bool _interactive = false;

            /// <summary>
            ///
            /// </summary>
            /// <param name="owner">The owning project object.</param>
            /// <param name="xml">ProjectRootElement to use.</param>
            /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
            /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
            /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
            /// <param name="loadSettings">The <see cref="ProjectLoadSettings"/> to use for evaluation.</param>
            public ProjectImpl(Project owner, ProjectRootElement xml, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectLoadSettings loadSettings)
            {
                ErrorUtilities.VerifyThrowArgumentNull(xml);
                ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
                ErrorUtilities.VerifyThrowArgumentNull(owner);

                Xml = xml;
                Owner = owner;
            }

            /// <summary>
            /// Construct over a text reader over project xml, evaluating with specified
            /// global properties and toolset, either or both of which may be null.
            /// Project will be added to the specified project collection when it is named.
            /// Throws InvalidProjectFileException if the evaluation fails.
            /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
            /// </summary>
            /// <param name="owner">The owning project object.</param>
            /// <param name="xmlReader">Xml reader to read project from.</param>
            /// <param name="globalProperties">Global properties to evaluate with. May be null in which case the containing project collection's global properties will be used.</param>
            /// <param name="toolsVersion">Tools version to evaluate with. May be null.</param>
            /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
            /// <param name="loadSettings">The load settings for this project.</param>
            /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
            public ProjectImpl(Project owner, XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectLoadSettings loadSettings, EvaluationContext evaluationContext)
            {
                ErrorUtilities.VerifyThrowArgumentNull(xmlReader);
                ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
                ErrorUtilities.VerifyThrowArgumentNull(owner);

                Owner = owner;

                try
                {
                    Xml = ProjectRootElement.Create(xmlReader, ProjectCollection,
                        preserveFormatting: false);
                }
                catch (InvalidProjectFileException ex)
                {
                    LoggingService.LogInvalidProjectFileError(s_buildEventContext, ex);
                    throw;
                }
            }

            /// <summary>
            /// Construct over an existing project file, evaluating with the specified global properties and
            /// using the tools version provided, either or both of which may be null.
            /// Project is added to the global project collection.
            /// Throws InvalidProjectFileException if the evaluation fails.
            /// Throws InvalidOperationException if there is already an equivalent project loaded in the project collection.
            /// May throw IO-related exceptions.
            /// </summary>
            /// <param name="owner">The owning project object.</param>
            /// <param name="projectFile">The project file.</param>
            /// <param name="globalProperties">The global properties. May be null.</param>
            /// <param name="toolsVersion">The tools version. May be null.</param>
            /// <param name="subToolsetVersion">Sub-toolset version to explicitly evaluate the toolset with.  May be null.</param>
            /// <param name="loadSettings">The load settings for this project.</param>
            /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
            public ProjectImpl(Project owner, string projectFile, IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectLoadSettings loadSettings, EvaluationContext evaluationContext)
            {
                ErrorUtilities.VerifyThrowArgumentNull(projectFile);
                ErrorUtilities.VerifyThrowArgumentLengthIfNotNull(toolsVersion, nameof(toolsVersion));
                ErrorUtilities.VerifyThrowArgumentNull(owner);

                Owner = owner;

                // We do not control the current directory at this point, but assume that if we were
                // passed a relative path, the caller assumes we will prepend the current directory.
                projectFile = FileUtilities.NormalizePath(projectFile);

                try
                {
                    Xml = ProjectRootElement.OpenProjectOrSolution(
                        projectFile,
                        globalProperties,
                        toolsVersion,
                        ProjectCollection.ProjectRootElementCache,
                        true /*Explicitly loaded*/);
                }
                catch (InvalidProjectFileException ex)
                {
                    LoggingService.LogInvalidProjectFileError(s_buildEventContext, ex);
                    throw;
                }
            }

            private Project Owner { get; }
            /// <summary>
            /// Gets or sets the project collection which contains this project.
            /// Can never be null.
            /// Cannot be modified.
            /// </summary>
            private ProjectCollection ProjectCollection => Owner.ProjectCollection;

            /// <summary>
            /// Certain item operations split the item element in multiple elements if the include
            /// contains globs, references to items or properties, or multiple item values.
            ///
            /// The items operations that may expand item elements are:
            /// - <see cref="RemoveItem"/>
            /// - <see cref="RemoveItems"/>
            /// - <see cref="AddItem(string,string, IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;)"/>
            /// - <see cref="AddItemFast(string,string, IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;)"/>
            /// - <see cref="ProjectItem.Rename"/>
            /// - <see cref="ProjectItem.RemoveMetadata"/>
            /// - <see cref="ProjectItem.SetMetadataValue(string,string)"/>
            /// - <see cref="ProjectItem.SetMetadataValue(string,string, bool)"/>
            ///
            /// When this property is set to true, the previous item operations throw an <exception cref="InvalidOperationException"></exception>
            /// instead of expanding the item element.
            /// </summary>
            public override bool ThrowInsteadOfSplittingItemElement { get; set; }

            /// <summary>
            /// Whether build is enabled for this project.
            /// </summary>
            private enum BuildEnabledSetting
            {
                /// <summary>
                /// Explicitly enabled
                /// </summary>
                BuildEnabled,

                /// <summary>
                /// Explicitly disabled
                /// </summary>
                BuildDisabled,

                /// <summary>
                /// No explicit setting, uses the setting on the
                /// project collection.
                /// This is the default.
                /// </summary>
                UseProjectCollectionSetting
            }

            public bool IsLinked => false;

            public bool IsZombified
            {
                get => _renameHandler == null;
                set => _renameHandler = null;
            }

            public Data TestOnlyGetPrivateData => _data;

            /// <summary>
            /// The backing Xml project.
            /// Can never be null.
            /// </summary>
            /// <remarks>
            /// There is no setter here as that doesn't make sense. If you have a new ProjectRootElement, evaluate it into a new Project.
            /// </remarks>
            public override ProjectRootElement Xml { get; }

            /// <summary>
            /// Whether this project is dirty such that it needs reevaluation.
            /// This may be because its underlying XML has changed (either through this project or another)
            /// either the XML of the main project or an imported file;
            /// or because its toolset may have changed.
            /// </summary>
            public override bool IsDirty
            {
                get
                {
                    if (_explicitlyMarkedDirty)
                    {
                        if (s_debugEvaluation)
                        {
                            Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Explicitly marked dirty, eg., because a global property was set, or an import, such as a .user file, was created on disk [{0}] [PC Hash {1}]", FullPath, ProjectCollection.GetHashCode()));
                        }

                        return true;
                    }

                    if (_evaluatedVersion < Xml.Version)
                    {
                        if (s_debugEvaluation)
                        {
                            if (Xml.Count > 0) // don't log empty projects, evaluation is not interesting
                            {
                                Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Is dirty because {0} [{1}] [PC Hash {2}]", Xml.LastDirtyReason, FullPath, ProjectCollection.GetHashCode()));
                            }
                        }

                        return true;
                    }

                    if (_evaluatedToolsetCollectionVersion != ProjectCollection.ToolsetsVersion)
                    {
                        if (s_debugEvaluation)
                        {
                            Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Is dirty because toolsets updated [{0}] [PC Hash {1}]", FullPath, ProjectCollection.GetHashCode()));
                        }

                        return true;
                    }

                    foreach (ResolvedImport import in _data.ImportClosure)
                    {
                        if (import.ImportedProject.Version != import.VersionEvaluated || _evaluatedVersion < import.VersionEvaluated)
                        {
                            if (s_debugEvaluation)
                            {
                                string reason = import.ImportedProject.LastDirtyReason;

                                if (reason != null)
                                {
                                    Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Is dirty because {0} [{1} - {2}] [PC Hash {3}]", reason, FullPath, import.ImportedProject.FullPath == FullPath ? String.Empty : import.ImportedProject.FullPath, ProjectCollection.GetHashCode()));
                                }
                            }

                            return true;
                        }
                    }

                    return false;
                }
            }

            /// <summary>
            /// See <see cref="ProjectLink.GlobalPropertiesContains(string)"/>.
            /// </summary>
            /// <param name="key">The key to check for its value.</param>
            /// <returns>Whether the key is in the global properties dictionary.</returns>
            public override bool GlobalPropertiesContains(string key)
            {
                return _data.GlobalPropertiesDictionary.Contains(key);
            }

            /// <summary>
            /// See <see cref="ProjectLink.GlobalPropertiesCount()"/>.
            /// </summary>
            /// <returns>The number of properties in the global properties dictionary</returns>
            public override int GlobalPropertiesCount()
            {
                return _data.GlobalPropertiesDictionary.Count;
            }

            /// <summary>
            /// See <see cref="ProjectLink.GlobalPropertiesEnumerable()"/>.
            /// </summary>
            /// <returns>An IEnumerable of the keys and values of the global properties dictionary</returns>
            public override IEnumerable<KeyValuePair<string, string>> GlobalPropertiesEnumerable()
            {
                foreach (ProjectPropertyInstance property in _data.GlobalPropertiesDictionary)
                {
                    yield return new KeyValuePair<string, string>(property.Name, ((IProperty)property).EvaluatedValueEscaped);
                }
            }

            /// <summary>
            /// Read only dictionary of the global properties used in the evaluation
            /// of this project.
            /// </summary>
            /// <remarks>
            /// This is the publicly exposed getter, that translates into a read-only dead IDictionary&lt;string, string&gt;.
            ///
            /// In order to easily tell when we're dirtied, setting and removing global properties is done with
            /// <see cref="SetGlobalProperty">SetGlobalProperty</see> and <see cref="RemoveGlobalProperty">RemoveGlobalProperty</see>.
            /// </remarks>
            public override IDictionary<string, string> GlobalProperties
            {
                [DebuggerStepThrough]
                get
                {
                    if (_data.GlobalPropertiesDictionary.Count == 0)
                    {
                        return ReadOnlyEmptyDictionary<string, string>.Instance;
                    }

                    var dictionary = new Dictionary<string, string>(_data.GlobalPropertiesDictionary.Count, MSBuildNameIgnoreCaseComparer.Default);
                    foreach (ProjectPropertyInstance property in _data.GlobalPropertiesDictionary)
                    {
                        dictionary[property.Name] = ((IProperty)property).EvaluatedValueEscaped;
                    }

                    return new ObjectModel.ReadOnlyDictionary<string, string>(dictionary);
                }
            }

            /// <summary>
            /// Item types in this project.
            /// This is an ordered collection.
            /// </summary>
            /// <comments>
            /// data.ItemTypes is a KeyCollection, so it doesn't need any
            /// additional read-only protection.
            /// </comments>
            public override ICollection<string> ItemTypes => _data.ItemTypes;

            /// <summary>
            /// Properties in this project.
            /// Since evaluation has occurred, this is an unordered collection.
            /// </summary>
            public override ICollection<ProjectProperty> Properties => new ReadOnlyCollection<ProjectProperty>(_data.Properties);

            /// <summary>
            /// Collection of possible values implied for properties contained in the conditions found on properties,
            /// property groups, imports, and whens.
            ///
            /// For example, if the following conditions existed on properties in a project:
            ///
            /// Condition="'$(Configuration)|$(Platform)' == 'Debug|x86'"
            /// Condition="'$(Configuration)' == 'Release'"
            ///
            /// the table would be populated with
            ///
            /// { "Configuration", { "Debug", "Release" }}
            /// { "Platform", { "x86" }}
            ///
            /// This is used by Visual Studio to determine the configurations defined in the project.
            /// </summary>
            public override IDictionary<string, List<string>> ConditionedProperties
            {
                [DebuggerStepThrough]
                get
                {
                    if (_data.ConditionedProperties == null)
                    {
                        return ReadOnlyEmptyDictionary<string, List<string>>.Instance;
                    }

                    return new ObjectModel.ReadOnlyDictionary<string, List<string>>(_data.ConditionedProperties);
                }
            }

            /// <summary>
            /// Read-only dictionary of item definitions in this project.
            /// Keyed by item type.
            /// </summary>
            public override IDictionary<string, ProjectItemDefinition> ItemDefinitions => _data.ItemDefinitions;

            /// <summary>
            /// Items in this project, ordered within groups of item types.
            /// </summary>
            [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
            public override ICollection<ProjectItem> Items => new ReadOnlyCollection<ProjectItem>(_data.Items);

            /// <summary>
            /// Items in this project, ordered within groups of item types,
            /// including items whose conditions evaluated to false, or that were
            /// contained within item groups who themselves had conditioned evaluated to false.
            /// This is useful for hosts that wish to display all items, even if they might not be part
            /// of the build in the current configuration.
            /// </summary>
            [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
            public override ICollection<ProjectItem> ItemsIgnoringCondition
            {
                [DebuggerStepThrough]
                get
                {
                    if (!(_data.ShouldEvaluateForDesignTime && _data.CanEvaluateElementsWithFalseConditions))
                    {
                        ErrorUtilities.ThrowInvalidOperation("OM_NotEvaluatedBecauseShouldEvaluateForDesignTimeIsFalse", nameof(ItemsIgnoringCondition));
                    }

                    return new ReadOnlyCollection<ProjectItem>(_data.ItemsIgnoringCondition);
                }
            }

            /// <summary>
            /// All the files that during evaluation contributed to this project, as ProjectRootElements,
            /// with the ProjectImportElement that caused them to be imported.
            /// This does not include projects that were never imported because a condition on an Import element was false.
            /// The outer ProjectRootElement that maps to this project itself is not included.
            /// </summary>
            /// <remarks>
            /// This can be used by the host to figure out what projects might be impacted by a change to a particular file.
            /// It could also be used, for example, to find the .user file, and use its ProjectRootElement to modify properties in it.
            /// </remarks>
            public override IList<ResolvedImport> Imports
            {
                get
                {
                    var imports = new List<ResolvedImport>(_data.ImportClosure.Count - 1 /* outer project */);

                    foreach (ResolvedImport import in _data.ImportClosure)
                    {
                        if (import.ImportingElement != null) // Exclude outer project itself
                        {
                            imports.Add(import);
                        }
                    }

                    return imports;
                }
            }

            /// <summary>
            /// This list will contain duplicate imports if an import is imported multiple times. However, only the first import was used in evaluation.
            /// </summary>
            public override IList<ResolvedImport> ImportsIncludingDuplicates
            {
                get
                {
                    ErrorUtilities.VerifyThrowInvalidOperation((_loadSettings & ProjectLoadSettings.RecordDuplicateButNotCircularImports) != 0, "OM_MustSetRecordDuplicateInputs");

                    var imports = new List<ResolvedImport>(_data.ImportClosureWithDuplicates.Count - 1 /* outer project */);

                    foreach (var import in _data.ImportClosureWithDuplicates)
                    {
                        if (import.ImportingElement != null) // Exclude outer project itself
                        {
                            imports.Add(import);
                        }
                    }

                    return imports;
                }
            }

            /// <summary>
            /// Targets in the project. The key to the dictionary is the target's name.
            /// Overridden targets are not included in this collection.
            /// This collection is read-only.
            /// </summary>
            public override IDictionary<string, ProjectTargetInstance> Targets
            {
                [DebuggerStepThrough]
                get
                {
                    if (_data.Targets == null)
                    {
                        return ReadOnlyEmptyDictionary<string, ProjectTargetInstance>.Instance;
                    }

                    return new ObjectModel.ReadOnlyDictionary<string, ProjectTargetInstance>(_data.Targets);
                }
            }

            /// <summary>
            /// Properties encountered during evaluation. These are read during the first evaluation pass.
            /// Unlike those returned by the Properties property, these are ordered, and includes any properties that
            /// were subsequently overridden by others with the same name. It does not include any
            /// properties whose conditions did not evaluate to true.
            /// It does not include any properties added since the last evaluation.
            /// </summary>
            public override ICollection<ProjectProperty> AllEvaluatedProperties
            {
                get
                {
                    ICollection<ProjectProperty> allEvaluatedProperties = _data.AllEvaluatedProperties;

                    if (allEvaluatedProperties == null)
                    {
                        return ReadOnlyEmptyCollection<ProjectProperty>.Instance;
                    }

                    return new ReadOnlyCollection<ProjectProperty>(allEvaluatedProperties);
                }
            }

            /// <summary>
            /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
            /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
            /// were subsequently overridden by others with the same name and item type. It does not include any
            /// elements whose conditions did not evaluate to true.
            /// It does not include any item definition metadata added since the last evaluation.
            /// </summary>
            public override ICollection<ProjectMetadata> AllEvaluatedItemDefinitionMetadata
            {
                get
                {
                    ICollection<ProjectMetadata> allEvaluatedItemDefinitionMetadata = _data.AllEvaluatedItemDefinitionMetadata;

                    if (allEvaluatedItemDefinitionMetadata == null)
                    {
                        return ReadOnlyEmptyCollection<ProjectMetadata>.Instance;
                    }

                    return new ReadOnlyCollection<ProjectMetadata>(allEvaluatedItemDefinitionMetadata);
                }
            }

            /// <summary>
            /// Items encountered during evaluation. These are read during the third evaluation pass.
            /// Unlike those returned by the Items property, these are ordered with respect to all other items
            /// encountered during evaluation, not just ordered with respect to items of the same item type.
            /// In some applications, like the F# language, this complete mutual ordering is significant, and such hosts
            /// can use this property.
            /// It does not include any elements whose conditions did not evaluate to true.
            /// It does not include any items added since the last evaluation.
            /// </summary>
            public override ICollection<ProjectItem> AllEvaluatedItems
            {
                get
                {
                    ICollection<ProjectItem> allEvaluatedItems = _data.AllEvaluatedItems;

                    if (allEvaluatedItems == null)
                    {
                        return ReadOnlyEmptyCollection<ProjectItem>.Instance;
                    }

                    return new ReadOnlyCollection<ProjectItem>(allEvaluatedItems);
                }
            }

            /// <summary>
            /// The tools version this project was evaluated with, if any.
            /// Not necessarily the same as the tools version on the Project tag, if any;
            /// it may have been externally specified, for example with a /tv switch.
            /// The actual tools version on the Project tag, can be gotten from <see cref="Xml">Xml.ToolsVersion</see>.
            /// Cannot be changed once the project has been created.
            /// </summary>
            /// <remarks>
            /// Set by construction.
            /// </remarks>
            public override string ToolsVersion => _data.Toolset.ToolsVersion;

            /// <summary>
            /// The sub-toolset version that, combined with the ToolsVersion, was used to determine
            /// the toolset properties for this project.
            /// </summary>
            public override string SubToolsetVersion => _data.SubToolsetVersion;

            /// <summary>
            /// The root directory for this project.
            /// Is never null: in-memory projects use the current directory from the time of load.
            /// </summary>
            public string DirectoryPath => Xml.DirectoryPath;

            /// <summary>
            /// The full path to this project's file.
            /// May be null, if the project was not loaded from disk.
            /// Setter renames the project, if it already had a name.
            /// </summary>
            public string FullPath
            {
                [DebuggerStepThrough]
                get => Xml.FullPath;
                [DebuggerStepThrough]
                set => Xml.FullPath = value;
            }

            /// <summary>
            /// Whether ReevaluateIfNecessary is temporarily disabled.
            /// This is useful when the host expects to make a number of reads and writes
            /// to the project, and wants to temporarily sacrifice correctness for performance.
            /// </summary>
            public override bool SkipEvaluation { get; set; }

            /// <summary>
            /// Whether <see cref="MarkDirty()">MarkDirty()</see> is temporarily disabled.
            /// This allows, for example, a global property to be set without the project getting
            /// marked dirty for reevaluation as a consequence.
            /// </summary>
            public override bool DisableMarkDirty { get; set; }

            /// <summary>
            /// This controls whether or not the building of targets/tasks is enabled for this
            /// project.  This is for security purposes in case a host wants to closely
            /// control which projects it allows to run targets/tasks.  By default, for a newly
            /// created project, we will use whatever setting is in the parent project collection.
            /// When build is disabled, the Build method on this class will fail. However if
            /// the host has already created a ProjectInstance, it can still build it. (It is
            /// free to put a similar check around where it does this.)
            /// </summary>
            public override bool IsBuildEnabled
            {
                get
                {
                    switch (_isBuildEnabled)
                    {
                        case BuildEnabledSetting.BuildEnabled:
                            return true;

                        case BuildEnabledSetting.BuildDisabled:
                            return false;

                        case BuildEnabledSetting.UseProjectCollectionSetting:
                            return ProjectCollection.IsBuildEnabled;

                        default:
                            ErrorUtilities.ThrowInternalErrorUnreachable();
                            return false;
                    }
                }

                set => _isBuildEnabled = value ? BuildEnabledSetting.BuildEnabled : BuildEnabledSetting.BuildDisabled;
            }

            /// <summary>
            /// Location of the originating file itself, not any specific content within it.
            /// If the file has not been given a name, returns an empty location.
            /// </summary>
            public ElementLocation ProjectFileLocation => Xml.ProjectFileLocation;

            /// <summary>
            /// Obsolete. Use <see cref="LastEvaluationId"/> instead.
            /// </summary>
            // marked as obsolete in 15.3
            public int EvaluationCounter => LastEvaluationId;

            /// <summary>
            /// The ID of the last evaluation for this Project.
            /// A project is always evaluated upon construction and can subsequently get evaluated multiple times via
            /// <see cref="ProjectLink.ReevaluateIfNecessary" />
            ///
            /// It is an arbitrary number that changes when this project reevaluates.
            /// Hosts don't know whether an evaluation actually happened in an interval, but they can compare this number to
            /// their previously stored value to find out, and if so perhaps decide to update their own state.
            /// Note that the number may not increase monotonically.
            ///
            /// This number corresponds to the <see cref="BuildEventContext.EvaluationId"/> and can be used to connect
            /// evaluation logging events back to the Project instance.
            /// </summary>
            public override int LastEvaluationId => _data.EvaluationId;

            /// <summary>
            /// List of names of the properties that, while global, are still treated as overridable.
            /// </summary>
            public ISet<string> GlobalPropertiesToTreatAsLocal => _data.GlobalPropertiesToTreatAsLocal;

            /// <summary>
            /// The logging service used for evaluation errors.
            /// </summary>
            internal ILoggingService LoggingService => ProjectCollection.LoggingService;

            /// <summary>
            /// See <see cref="ProjectLink.GetAllGlobs(EvaluationContext)"/>.
            /// </summary>
            /// <param name="evaluationContext">
            ///     The evaluation context to use in case reevaluation is required.
            ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
            /// </param>
            public override List<GlobResult> GetAllGlobs(EvaluationContext evaluationContext)
            {
                return GetAllGlobs(GetEvaluatedItemElements(evaluationContext));
            }

            /// <summary>
            /// See <see cref="ProjectLink.GetAllGlobs(string, EvaluationContext)"/>.
            /// </summary>
            /// <param name="itemType">The type of items to return.</param>
            /// <param name="evaluationContext">
            ///     The evaluation context to use in case reevaluation is required.
            ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
            /// </param>
            public override List<GlobResult> GetAllGlobs(string itemType, EvaluationContext evaluationContext)
            {
                if (string.IsNullOrEmpty(itemType))
                {
                    return new List<GlobResult>();
                }

                return GetAllGlobs(GetItemElementsByType(GetEvaluatedItemElements(evaluationContext), itemType));
            }

            // represents cumulated remove information for a particular item type
            private struct CumulativeRemoveElementData
            {
                private ImmutableList<IMSBuildGlob>.Builder _globs;
                private ImmutableHashSet<string>.Builder _fragmentStrings;

                public IEnumerable<IMSBuildGlob> Globs => _globs.ToImmutable();
                public IEnumerable<string> FragmentStrings => _fragmentStrings.ToImmutable();

                public static CumulativeRemoveElementData Create()
                {
                    return new CumulativeRemoveElementData
                    {
                        _globs = ImmutableList.CreateBuilder<IMSBuildGlob>(),
                        _fragmentStrings = ImmutableHashSet.CreateBuilder<string>()
                    };
                }

                public readonly void AccumulateInformationFromRemoveItemSpec(EvaluationItemSpec removeSpec)
                {
                    IEnumerable<string> removeSpecFragmentStrings = removeSpec.FlattenFragmentsAsStrings();
                    var removeGlob = removeSpec.ToMSBuildGlob();

                    _globs.Add(removeGlob);

                    foreach (var removeFragment in removeSpecFragmentStrings)
                    {
                        _fragmentStrings.Add(removeFragment);
                    }
                }
            }

            private List<GlobResult> GetAllGlobs(List<ProjectItemElement> projectItemElements)
            {
                if (projectItemElements.Count == 0)
                {
                    return new List<GlobResult>();
                }

                // Scan the project elements in reverse order and build globbing information for each include element.
                // Based on the fact that relevant removes for a particular include element (xml element A) consist of:
                // - all the removes seen by the next include statement of A's type (xml element B which appears after A in file order)
                // - new removes between A and B (removes that apply to A but not to B. Spacially, these are placed between A's element and B's element)

                // Example:
                // 1. <I Include="A"/>
                // 2. <I Remove="..."/> // this remove applies to the include at 1
                // 3. <I Include="B"/>
                // 4. <I Remove="..."/> // this remove applies to the includes at 1, 3
                // 5. <I Include="C"/>
                // 6. <I Remove="..."/> // this remove applies to the includes at 1, 3, 5
                // So A's applicable removes are composed of:
                //
                // The applicable removes for the element at position 1 (xml element A) are composed of:
                // - all the removes seen by the next include statement of I's type (xml element B, position 3, which appears after A in file order). In this example that's Removes at positions 4 and 6.
                // - new removes between A and B. In this example that's Remove 2.

                // use immutable builders because there will be a lot of structural sharing between includes which share increasing subsets of corresponding remove elements
                // item type -> aggregated information about all removes seen so far for that item type
                var removeElementCache = new Dictionary<string, CumulativeRemoveElementData>(projectItemElements.Count);
                var globResults = new List<GlobResult>(projectItemElements.Count);

                for (var i = projectItemElements.Count - 1; i >= 0; i--)
                {
                    var itemElement = projectItemElements[i];

                    if (!string.IsNullOrEmpty(itemElement.Include))
                    {
                        var globResult = BuildGlobResultFromIncludeItem(itemElement, removeElementCache);

                        if (globResult != null)
                        {
                            globResults.Add(globResult);
                        }
                    }
                    else if (!string.IsNullOrEmpty(itemElement.Remove))
                    {
                        CacheInformationFromRemoveItem(itemElement, removeElementCache);
                    }
                }

                globResults.TrimExcess();

                return globResults;
            }

            private GlobResult BuildGlobResultFromIncludeItem(ProjectItemElement itemElement, IReadOnlyDictionary<string, CumulativeRemoveElementData> removeElementCache)
            {
                var includeItemspec = new EvaluationItemSpec(itemElement.Include, _data.Expander, itemElement.IncludeLocation, itemElement.ContainingProject.DirectoryPath);

                ImmutableArray<ItemSpecFragment> includeGlobFragments = includeItemspec.Fragments.Where(f => f is GlobFragment && f.TextFragment.IndexOfAny(s_invalidGlobChars) == -1).ToImmutableArray();
                if (includeGlobFragments.Length == 0)
                {
                    return null;
                }

                ImmutableArray<string> includeGlobStrings = includeGlobFragments.Select(f => f.TextFragment).ToImmutableArray();
                var includeGlob = CompositeGlob.Create(includeGlobFragments.Select(f => f.ToMSBuildGlob()));

                IEnumerable<string> excludeFragmentStrings = [];
                IMSBuildGlob excludeGlob = null;

                if (!string.IsNullOrEmpty(itemElement.Exclude))
                {
                    var excludeItemspec = new EvaluationItemSpec(itemElement.Exclude, _data.Expander, itemElement.ExcludeLocation, itemElement.ContainingProject.DirectoryPath);

                    excludeFragmentStrings = excludeItemspec.FlattenFragmentsAsStrings().ToImmutableHashSet();
                    excludeGlob = excludeItemspec.ToMSBuildGlob();
                }

                IEnumerable<string> removeFragmentStrings = [];
                IMSBuildGlob removeGlob = null;

                if (removeElementCache.TryGetValue(itemElement.ItemType, out CumulativeRemoveElementData removeItemElement))
                {
                    removeFragmentStrings = removeItemElement.FragmentStrings;
                    removeGlob = CompositeGlob.Create(removeItemElement.Globs);
                }

                var includeGlobWithGaps = CreateIncludeGlobWithGaps(includeGlob, excludeGlob, removeGlob);

                return new GlobResult(itemElement, includeGlobStrings, includeGlobWithGaps, excludeFragmentStrings, removeFragmentStrings);
            }

            private static IMSBuildGlob CreateIncludeGlobWithGaps(IMSBuildGlob includeGlob, IMSBuildGlob excludeGlob, IMSBuildGlob removeGlob)
            {
                return (excludeGlob, removeGlob) switch
                {
                    (null, null) => includeGlob,
                    (not null, null) => new MSBuildGlobWithGaps(includeGlob, excludeGlob),
                    (null, not null) => new MSBuildGlobWithGaps(includeGlob, removeGlob),
                    (not null, not null) => new MSBuildGlobWithGaps(includeGlob, new CompositeGlob(excludeGlob, removeGlob))
                };
            }

            private void CacheInformationFromRemoveItem(ProjectItemElement itemElement, Dictionary<string, CumulativeRemoveElementData> removeElementCache)
            {
                if (!removeElementCache.TryGetValue(itemElement.ItemType, out CumulativeRemoveElementData cumulativeRemoveElementData))
                {
                    cumulativeRemoveElementData = CumulativeRemoveElementData.Create();

                    removeElementCache[itemElement.ItemType] = cumulativeRemoveElementData;
                }

                var removeSpec = new EvaluationItemSpec(itemElement.Remove, _data.Expander, itemElement.RemoveLocation, itemElement.ContainingProject.DirectoryPath);

                cumulativeRemoveElementData.AccumulateInformationFromRemoveItemSpec(removeSpec);
            }

            /// <summary>
            /// See <see cref="ProjectLink.GetItemProvenance(string, EvaluationContext)"/>.
            /// </summary>
            /// <param name="itemToMatch">The string to perform matching against.</param>
            /// <param name="evaluationContext">
            ///     The evaluation context to use in case reevaluation is required.
            ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
            /// </param>
            public override List<ProvenanceResult> GetItemProvenance(string itemToMatch, EvaluationContext evaluationContext)
            {
                return GetItemProvenance(itemToMatch, GetEvaluatedItemElements(evaluationContext));
            }

            /// <summary>
            /// See <see cref="ProjectLink.GetItemProvenance(string, string, EvaluationContext)"/>.
            /// </summary>
            /// <param name="itemToMatch">The string to perform matching against.</param>
            /// <param name="itemType">The type of items to return.</param>
            /// <param name="evaluationContext">
            ///     The evaluation context to use in case reevaluation is required.
            ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
            /// </param>
            public override List<ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType, EvaluationContext evaluationContext)
            {
                return GetItemProvenance(itemToMatch, GetItemElementsByType(GetEvaluatedItemElements(evaluationContext), itemType));
            }

            /// <summary>
            /// See <see cref="ProjectLink.GetItemProvenance(ProjectItem, EvaluationContext)"/>.
            /// </summary>
            /// /// <param name="item">
            /// The ProjectItem object that indicates: the itemspec to match and the item type to constrain the search in.
            /// The search is also constrained on item elements appearing before the item element that produced this <paramref name="item"/>.
            /// The element that produced this <paramref name="item"/> is included in the results.
            /// </param>
            /// <param name="evaluationContext">
            ///     The evaluation context to use in case reevaluation is required.
            ///     To avoid reevaluation use <see cref="ProjectLoadSettings.RecordEvaluatedItemElements"/>.
            /// </param>
            public override List<ProvenanceResult> GetItemProvenance(ProjectItem item, EvaluationContext evaluationContext)
            {
                if (item == null)
                {
                    return new List<ProvenanceResult>();
                }

                IEnumerable<ProjectItemElement> itemElementsAbove = GetItemElementsThatMightAffectItem(GetEvaluatedItemElements(evaluationContext), item);

                return GetItemProvenance(item.EvaluatedInclude, itemElementsAbove);
            }

            /// <summary>
            /// Some project APIs need to do analysis that requires the Evaluator to record more data than usual as it evaluates.
            /// This method checks if the Evaluator was run with the extra required settings and if not, does a re-evaluation.
            /// If a re-evaluation was necessary, it saves this information so a next call does not re-evaluate.
            ///
            /// Using this method avoids storing extra data in memory when its not needed.
            /// </summary>
            /// <param name="evaluationContext"></param>
            private List<ProjectItemElement> GetEvaluatedItemElements(EvaluationContext evaluationContext)
            {
                if (!_loadSettings.HasFlag(ProjectLoadSettings.RecordEvaluatedItemElements))
                {
                    _loadSettings |= ProjectLoadSettings.RecordEvaluatedItemElements;
                    Reevaluate(LoggingService, _loadSettings, evaluationContext);
                }

                return _data.EvaluatedItemElements;
            }

            private static IEnumerable<ProjectItemElement> GetItemElementsThatMightAffectItem(List<ProjectItemElement> evaluatedItemElements, ProjectItem item)
            {
                IEnumerable<ProjectItemElement> relevantElementsAfterInclude = evaluatedItemElements
                    // Skip until we encounter the element that produced the item because
                    // there are no item operations that can affect future items
                    .SkipWhile(i => i != item.Xml)
                    .Where(itemElement =>
                        // items operations of different item types cannot affect each other
                        itemElement.ItemType.Equals(item.ItemType) &&
                        // other includes cannot affect the current item
                        itemElement.IncludeLocation == null &&
                        // any remove that matches this item will cause the ProjectItem to not be produced in the first place
                        // all other removes do not apply
                        itemElement.RemoveLocation == null);

                // add the include operation that created the project item element
                return [item.Xml, .. relevantElementsAfterInclude];
            }

            private static List<ProjectItemElement> GetItemElementsByType(IEnumerable<ProjectItemElement> itemElements, string itemType)
            {
                return itemElements.Where(i => i.ItemType.Equals(itemType)).ToList();
            }

            private List<ProvenanceResult> GetItemProvenance(string itemToMatch, IEnumerable<ProjectItemElement> projectItemElements)
            {
                if (string.IsNullOrEmpty(itemToMatch))
                {
                    return new List<ProvenanceResult>();
                }

                return projectItemElements
                    .AsParallel()
                    .Select((item, index) => (Result: ComputeProvenanceResult(itemToMatch, item), Index: index))
                    .Where(pair => pair.Result != null)
                    .AsSequential()
                    .OrderBy(pair => pair.Index)
                    .Select(pair => pair.Result)
                    .ToList();
            }

            // TODO: cache result?
            private ProvenanceResult ComputeProvenanceResult(string itemToMatch, ProjectItemElement itemElement)
            {
                ProvenanceResult SingleItemSpecProvenance(string itemSpec, IElementLocation elementLocation, Operation operation)
                {
                    if (elementLocation != null && !string.IsNullOrEmpty(itemSpec))
                    {
                        EvaluationItemSpec expandedItemSpec = new EvaluationItemSpec(itemSpec, _data.Expander, elementLocation, itemElement.ContainingProject.DirectoryPath, expandProperties: true);
                        int matchOccurrences = ItemMatchesInItemSpec(itemToMatch, expandedItemSpec, out Provenance provenance);
                        return matchOccurrences > 0 ? new ProvenanceResult(itemElement, operation, provenance, matchOccurrences) : null;
                    }

                    return null;
                }

                ProvenanceResult result = SingleItemSpecProvenance(itemElement.Include, itemElement.IncludeLocation, Operation.Include);
                return result == null ?
                    SingleItemSpecProvenance(itemElement.Update, itemElement.UpdateLocation, Operation.Update) ?? SingleItemSpecProvenance(itemElement.Remove, itemElement.RemoveLocation, Operation.Remove) :
                    SingleItemSpecProvenance(itemElement.Exclude, itemElement.ExcludeLocation, Operation.Exclude) ?? result;
            }

            /// <summary>
            /// Since:
            ///     - we have no proper AST and interpreter for itemspecs that we can do analysis on
            ///     - GetItemProvenance needs to have correct counts for exclude strings (as correct as it can get while doing it after evaluation)
            ///
            /// The temporary hack is to use the expander to expand the strings, and if any property or item references were encountered, return Provenance.Inconclusive.
            /// </summary>
            private static int ItemMatchesInItemSpec(string itemToMatch, EvaluationItemSpec itemSpec, out Provenance provenance)
            {
                provenance = Provenance.Undefined;

                IEnumerable<ItemSpecFragment> fragmentsMatchingItem = itemSpec.FragmentsMatchingItem(itemToMatch, out int occurrences);
                foreach (var fragment in fragmentsMatchingItem)
                {
                    if (fragment is ValueFragment)
                    {
                        provenance |= Provenance.StringLiteral;
                    }
                    else if (fragment is GlobFragment)
                    {
                        provenance |= Provenance.Glob;
                    }
                    else if (fragment is EvaluationItemExpressionFragment)
                    {
                        provenance |= Provenance.Inconclusive;
                    }
                    else
                    {
                        ErrorUtilities.ThrowInternalErrorUnreachable();
                    }

                    // Result is inconclusive if properties are present
                    if (itemSpec.ItemSpecString.Contains("$("))
                    {
                        provenance |= Provenance.Inconclusive;
                    }
                }

                return occurrences;
            }

            /// <summary>
            /// Returns an iterator over the "logical project". The logical project is defined as
            /// the unevaluated project obtained from the single MSBuild file that is the result
            /// of inlining the text of all imports of the original MSBuild project manifest file.
            /// </summary>
            public override IEnumerable<ProjectElement> GetLogicalProject()
            {
                // Implicit imports exist in the import closure but not in the project XML so the ImplicitImportLocation.Top
                // imports need to be returned before walking the project XML
                foreach (ProjectRootElement import in _data.ImportClosure.Where(i => i.ImportingElement?.ImplicitImportLocation == ImplicitImportLocation.Top).Select(i => i.ImportedProject))
                {
                    foreach (ProjectElement child in GetLogicalProject(import.AllChildren))
                    {
                        yield return child;
                    }
                }

                foreach (ProjectElement child in GetLogicalProject(Xml.AllChildren))
                {
                    yield return child;
                }

                // Implicit imports exist in the import closure but not in the project XML so the ImplicitImportLocation.Bottom
                // imports need to be returned before walking the project XML
                foreach (ProjectRootElement import in _data.ImportClosure.Where(i => i.ImportingElement?.ImplicitImportLocation == ImplicitImportLocation.Bottom).Select(i => i.ImportedProject))
                {
                    foreach (ProjectElement child in GetLogicalProject(import.AllChildren))
                    {
                        yield return child;
                    }
                }
            }

            /// <summary>
            /// Get any property in the project that has the specified name,
            /// otherwise returns null.
            /// </summary>
            [DebuggerStepThrough]
            public override ProjectProperty GetProperty(string name)
            {
                return _data.Properties[name];
            }

            /// <summary>
            /// Get the unescaped value of a property in this project, or
            /// an empty string if it does not exist.
            /// </summary>
            /// <remarks>
            /// A property with a value of empty string and no property
            /// at all are not distinguished between by this method.
            /// That makes it easier to use. To find out if a property is set at
            /// all in the project, use GetProperty(name).
            /// </remarks>
            public override string GetPropertyValue(string name)
            {
                return _data.GetPropertyValue(name);
            }

            /// <summary>
            /// Set or add a property with the specified name and value.
            /// Overwrites the value of any property with the same name already in the collection if it did not originate in an imported file.
            /// If there is no such existing property, uses this heuristic:
            /// Updates the last existing property with the specified name that has no condition on itself or its property group, if any,
            /// and is in this project file rather than an imported file.
            /// Otherwise, adds a new property in the first property group without a condition, creating a property group if necessary after
            /// the last existing property group, else at the start of the project.
            /// Returns the property set.
            /// Evaluates on a best-effort basis:
            ///     -expands with all properties. Properties that are defined in the XML below the new property may be used, even though in a real evaluation they would not be.
            ///     -only this property is evaluated. Anything else that would depend on its value is not affected.
            /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
            /// </summary>
            public override ProjectProperty SetProperty(string name, string unevaluatedValue)
            {
                ErrorUtilities.VerifyThrowArgumentLength(name);
                ErrorUtilities.VerifyThrowArgumentNull(unevaluatedValue);

                ProjectProperty property = _data.Properties[name];

                ErrorUtilities.VerifyThrowInvalidOperation(property?.IsReservedProperty != true, "OM_ReservedName", name);
                ErrorUtilities.VerifyThrowInvalidOperation(property?.IsGlobalProperty != true, "OM_GlobalProperty", name);

                // If there's an existing regular property, we can reuse it, unless it's not attached to its XML any more
                if (property?.IsEnvironmentProperty == false &&
                    property.Xml.Parent?.Parent != null &&
                    ReferenceEquals(property.Xml.ContainingProject, Xml))
                {
                    property.UnevaluatedValue = unevaluatedValue;
                }
                else
                {
                    ProjectPropertyElement propertyElement = Xml.AddProperty(name, unevaluatedValue);

                    property = ProjectProperty.Create(Owner, propertyElement, unevaluatedValue, null /* predecessor unknown */);

                    _data.Properties[name] = property;
                }

                property.UpdateEvaluatedValue(ExpandPropertyValueBestEffortLeaveEscaped(unevaluatedValue, property.Xml.Location));

                return property;
            }

            /// <summary>
            /// Change a global property after the project has been evaluated.
            /// If the value changes, this makes the project require reevaluation.
            /// If the value changes, returns true, otherwise false.
            /// </summary>
            public override bool SetGlobalProperty(string name, string escapedValue)
            {
                ProjectPropertyInstance existing = _data.GlobalPropertiesDictionary[name];

                if (existing == null || ((IProperty)existing).EvaluatedValueEscaped != escapedValue)
                {
                    string originalValue = (existing == null) ? String.Empty : ((IProperty)existing).EvaluatedValueEscaped;

                    _data.GlobalPropertiesDictionary.Set(ProjectPropertyInstance.Create(name, escapedValue));
                    _data.Properties.Set(ProjectProperty.Create(Owner, name, escapedValue, isGlobalProperty: true, mayBeReserved: false, loggingContext: null));

                    ProjectCollection.AfterUpdateLoadedProjectGlobalProperties(Owner);
                    MarkDirty();

                    if (s_debugEvaluation)
                    {
                        string displayValue = escapedValue.Substring(0, Math.Min(escapedValue.Length, 75)) + ((escapedValue.Length > 75) ? "..." : String.Empty);
                        if (existing == null)
                        {
                            Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Initially set global property {0} to '{1}' [{2}]", name, displayValue, FullPath));
                        }
                        else
                        {
                            string displayOriginalValue = originalValue.Substring(0, Math.Min(originalValue.Length, 75)) + ((originalValue.Length > 75) ? "..." : String.Empty);
                            Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD: Changed global property {0} from '{1}' to '{2}' [{3}]", name, displayOriginalValue, displayValue, FullPath));
                        }
                    }

                    return true;
                }

                return false;
            }

            /// <summary>
            /// Adds an item with metadata to the project.
            /// Metadata may be null, indicating no metadata.
            /// Does not modify the XML if a wildcard expression would already include the new item.
            /// Evaluates on a best-effort basis:
            ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
            ///     -only this item is evaluated. Other items that might depend on it is not affected.
            /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
            /// </summary>
            public override IList<ProjectItem> AddItem(string itemType, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
            {
                // For perf reasons, this method does several jobs in one.
                // If it finds a suitable existing item element, it returns that as the out parameter, otherwise the out parameter returns null.
                // Otherwise, if it finds an item element suitable to be just below our new element, it returns that.
                // Otherwise, if it finds an item group at least that's suitable to put our element in somewhere, it returns that.
                // Otherwise, it returns null.
                ProjectElement element = GetAnySuitableExistingItemXml(itemType, unevaluatedInclude, metadata, out ProjectItemElement itemElement);

                if (itemElement == null)
                {
                    // Didn't find a suitable existing item; maybe the hunt gave us a hint as
                    // to where to put a new one.
                    if (element is ProjectItemElement itemElementToAddBefore)
                    {
                        // It told us an item to add before
                        itemElement = Xml.CreateItemElement(itemType, unevaluatedInclude);
                        itemElementToAddBefore.Parent.InsertBeforeChild(itemElement, itemElementToAddBefore);
                    }
                    else
                    {
                        if (element is ProjectItemGroupElement itemGroupElement)
                        {
                            // It only told us an item group to add it somewhere within
                            itemElement = itemGroupElement.AddItem(itemType, unevaluatedInclude);
                        }
                        else
                        {
                            // It didn't give any hint at all
                            itemElement = Xml.AddItem(itemType, unevaluatedInclude);
                        }
                    }
                }

                // Fix up the evaluated state to match
                return AddItemHelper(itemElement, unevaluatedInclude, metadata);
            }

            /// <summary>
            /// Adds an item with metadata to the project.
            /// Metadata may be null, indicating no metadata.
            /// Makes no effort to see if an existing wildcard would already match the new item, unless it is the first item in an item group.
            /// Makes no effort to locate the new item near similar items.
            /// Appends the item to the first item group that does not have a condition and has either no children or whose first child is an item of the same type.
            /// Evaluates on a best-effort basis:
            ///     -expands with all items. Items that are defined in the XML below the new item may be used, even though in a real evaluation they would not be.
            ///     -only this item is evaluated. Other items that might depend on it is not affected.
            /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
            /// </summary>
            public override IList<ProjectItem> AddItemFast(string itemType, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
            {
                ErrorUtilities.VerifyThrowArgumentLength(itemType);
                ErrorUtilities.VerifyThrowArgumentLength(unevaluatedInclude);

                ProjectItemGroupElement groupToAppendTo = null;

                foreach (ProjectItemGroupElement group in Xml.ItemGroups)
                {
                    if (group.Condition.Length > 0)
                    {
                        continue;
                    }

                    if (group.Count == 0 || MSBuildNameIgnoreCaseComparer.Default.Equals(itemType, group.Items.First().ItemType))
                    {
                        groupToAppendTo = group;

                        break;
                    }
                }

                if (groupToAppendTo == null)
                {
                    groupToAppendTo = Xml.AddItemGroup();
                }

                ProjectItemElement itemElement;

                if (groupToAppendTo.Count == 0 ||
                    FileMatcher.HasWildcardsSemicolonItemOrPropertyReferences(unevaluatedInclude) ||
                    !IsSuitableExistingItemXml(groupToAppendTo.Items.First(), unevaluatedInclude, metadata))
                {
                    itemElement = Xml.CreateItemElement(itemType, unevaluatedInclude);
                    groupToAppendTo.AppendChild(itemElement);
                }
                else
                {
                    itemElement = groupToAppendTo.Items.First();
                }

                return AddItemHelper(itemElement, unevaluatedInclude, metadata);
            }

            /// <summary>
            /// All the items in the project of the specified
            /// type.
            /// If there are none, returns an empty list.
            /// Use AddItem or RemoveItem to modify items in this project.
            /// </summary>
            /// <comments>
            /// data.GetItems returns a read-only collection, so no need to re-wrap it here.
            /// </comments>
            public override ICollection<ProjectItem> GetItems(string itemType)
            {
                ICollection<ProjectItem> items = _data.GetItems(itemType);
                return items;
            }

            /// <summary>
            /// All the items in the project of the specified
            /// type, irrespective of whether the conditions on them evaluated to true.
            /// This is a read-only list: use AddItem or RemoveItem to modify items in this project.
            /// </summary>
            /// <comments>
            /// ItemDictionary[] returns a read only collection, so no need to wrap it.
            /// </comments>
            public override ICollection<ProjectItem> GetItemsIgnoringCondition(string itemType)
            {
                ICollection<ProjectItem> items = _data.ItemsIgnoringCondition[itemType];
                return items;
            }

            /// <summary>
            /// Returns all items that have the specified evaluated include.
            /// For example, all items that have the evaluated include "bar.cpp".
            /// Typically there will be zero or one, but sometimes there are two items with the
            /// same path and different item types, or even the same item types. This will return
            /// them all.
            /// </summary>
            /// <comments>
            /// data.GetItemsByEvaluatedInclude already returns a read-only collection, so no need
            /// to wrap it further.
            /// </comments>
            public override ICollection<ProjectItem> GetItemsByEvaluatedInclude(string evaluatedInclude)
            {
                ICollection<ProjectItem> items = _data.GetItemsByEvaluatedInclude(evaluatedInclude);
                return items;
            }

            /// <summary>
            /// Removes the specified property.
            /// Property must be associated with this project.
            /// Property must not originate from an imported file.
            /// Returns true if the property was in this evaluated project, otherwise false.
            /// As a convenience, if the parent property group becomes empty, it is also removed.
            /// Updates the evaluated project, but does not affect anything else in the project until reevaluation. For example,
            /// if "p" is removed, it will be removed from the evaluated project, but "q" which is evaluated from "$(p)" will not be modified until reevaluation.
            /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state.
            /// </summary>
            public override bool RemoveProperty(ProjectProperty property)
            {
                ErrorUtilities.VerifyThrowArgumentNull(property);
                ErrorUtilities.VerifyThrowInvalidOperation(!property.IsReservedProperty, "OM_ReservedName", property.Name);
                ErrorUtilities.VerifyThrowInvalidOperation(!property.IsGlobalProperty, "OM_GlobalProperty", property.Name);
                ErrorUtilities.VerifyThrowArgument(property.Xml.Parent != null, "OM_IncorrectObjectAssociation", "ProjectProperty", "Project");
                VerifyThrowInvalidOperationNotImported(property.Xml.ContainingProject);

                ProjectElementContainer parent = property.Xml.Parent;

                property.Xml.Parent.RemoveChild(property.Xml);

                if (parent.Count == 0)
                {
                    parent.Parent.RemoveChild(parent);
                }

                bool result = _data.Properties.Remove(property.Name);

                return result;
            }

            /// <summary>
            /// Removes a global property.
            /// If it was set, returns true, and marks the project
            /// as requiring reevaluation.
            /// </summary>
            public override bool RemoveGlobalProperty(string name)
            {
                ErrorUtilities.VerifyThrowArgumentLength(name);

                bool result = _data.GlobalPropertiesDictionary.Remove(name);

                if (result)
                {
                    ProjectCollection.AfterUpdateLoadedProjectGlobalProperties(Owner);
                    MarkDirty();

                    if (s_debugEvaluation)
                    {
                        Trace.WriteLine(String.Format(CultureInfo.InvariantCulture, "MSBUILD:  Remove global property {0}", name));
                    }
                }

                return result;
            }

            /// <summary>
            /// Removes an item from the project.
            /// Item must be associated with this project.
            /// Item must not originate from an imported file.
            /// Returns true if the item was in this evaluated project, otherwise false.
            /// As a convenience, if the parent item group becomes empty, it is also removed.
            /// If the item originated from a wildcard or semicolon separated expression, expands that expression into multiple items first.
            /// Updates the evaluated project, but does not affect anything else in the project until reevaluation. For example,
            /// if an item of type "i" is removed, "j" which is evaluated from "@(i)" will not be modified until reevaluation.
            /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state until reevaluation.
            /// </summary>
            /// <remarks>
            /// Normally this will return true, since if the item isn't in the project, it will throw.
            /// The exception is removing an item that was only in ItemsIgnoringCondition.
            /// </remarks>
            public override bool RemoveItem(ProjectItem item)
            {
                ErrorUtilities.VerifyThrowArgumentNull(item);
                ErrorUtilities.VerifyThrowArgument(item.Project == Owner, "OM_IncorrectObjectAssociation", "ProjectItem", "Project");

                bool result = RemoveItemHelper(item);

                return result;
            }

            /// <summary>
            /// Removes all the specified items from the project.
            /// Items that are not associated with this project are skipped.
            /// </summary>
            /// <remarks>
            /// Removing one item could cause the backing XML
            /// to be expanded, which could zombie (disassociate) the next item.
            /// To make this case easy for the caller, if an item
            /// is not associated with this project it is simply skipped.
            /// </remarks>
            public override void RemoveItems(IEnumerable<ProjectItem> items)
            {
                ErrorUtilities.VerifyThrowArgumentNull(items);

                // Copying to a list makes it possible to remove
                // all items of a particular type with
                //   RemoveItems(p.GetItems("mytype"))
                // without modifying the collection during enumeration.
                var itemsList = new List<ProjectItem>(items);

                foreach (ProjectItem item in itemsList)
                {
                    RemoveItemHelper(item);
                }
            }

            /// <summary>
            /// Evaluates the provided string by expanding items and properties,
            /// as if it was found at the very end of the project file.
            /// This is useful for some hosts for which this kind of best-effort
            /// evaluation is sufficient.
            /// Does not expand bare metadata expressions.
            /// </summary>
            public override string ExpandString(string unexpandedValue)
            {
                ErrorUtilities.VerifyThrowArgumentNull(unexpandedValue);

                string result = _data.Expander.ExpandIntoStringAndUnescape(unexpandedValue, ExpanderOptions.ExpandPropertiesAndItems, ProjectFileLocation);

                return result;
            }

            /// <summary>
            /// See <see cref="ProjectLink.CreateProjectInstance(ProjectInstanceSettings, EvaluationContext)"/>.
            /// </summary>
            /// <param name="settings">Project instance creation settings.</param>
            /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
            /// <returns></returns>
            public override ProjectInstance CreateProjectInstance(ProjectInstanceSettings settings, EvaluationContext evaluationContext)
            {
                return CreateProjectInstance(LoggingService, settings, evaluationContext);
            }

            /// <summary>
            /// Called to forcibly mark the project as dirty requiring reevaluation. Generally this is not necessary to set; all edits affecting
            /// this project will automatically make it dirty. However there are potential corner cases where it is necessary to mark the project dirty
            /// directly. For example, if the project has an import conditioned on a file existing on disk, and the file did not exist at
            /// evaluation time, then someone subsequently creates that file, the project cannot know that reevaluation would be productive.
            /// In such a case the host can help us by setting the dirty flag explicitly so that <see cref="ProjectLink.ReevaluateIfNecessary">ReevaluateIfNecessary()</see>
            /// will recognize an evaluation is indeed necessary.
            /// Does not mark the underlying project file as requiring saving.
            /// </summary>
            public override void MarkDirty()
            {
                if (!DisableMarkDirty && !ProjectCollection.DisableMarkDirty)
                {
                    _explicitlyMarkedDirty = true;
                }

                // Pass up the MarkDirty call even when DisableMarkDirty is true.
                Xml.MarkProjectDirty(Owner);
            }

            /// <summary>
            /// See <see cref="ProjectLink.ReevaluateIfNecessary"/>.
            /// </summary>
            /// <param name="evaluationContext">The <see cref="EvaluationContext"/> to use. See <see cref="EvaluationContext"/>.</param>
            public override void ReevaluateIfNecessary(EvaluationContext evaluationContext)
            {
                ReevaluateIfNecessary(LoggingService, evaluationContext);
            }

            /// <summary>
            /// Saves a "logical" or "preprocessed" project file, that includes all the imported
            /// files as if they formed a single file.
            /// </summary>
            public override void SaveLogicalProject(TextWriter writer)
            {
                XmlDocument document = Preprocessor.GetPreprocessedDocument(Owner);

                using (var projectWriter = new ProjectWriter(writer))
                {
                    projectWriter.Initialize(document);
                    document.Save(projectWriter);
                }
            }

            /// <summary>
            /// See <see cref="ProjectLink.Build"/>.
            /// </summary>
            /// <param name="targets">Targets to build.</param>
            /// <param name="loggers">List of loggers.</param>
            /// <param name="remoteLoggers">Remote loggers for multi proc logging.</param>
            /// <param name="evaluationContext">The evaluation context to use in case reevaluation is required.</param>
            public override bool Build(string[] targets, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, EvaluationContext evaluationContext)
            {
                if (!IsBuildEnabled)
                {
                    LoggingService.LogError(s_buildEventContext, new BuildEventFileInfo(FullPath), "SecurityProjectBuildDisabled");
                    if (LoggingService is LoggingService defaultLoggingService)
                    {
                        defaultLoggingService.WaitForLoggingToProcessEvents();
                    }
                    return false;
                }

                ProjectInstance instance = CreateProjectInstance(LoggingService, ProjectInstanceSettings.None, evaluationContext);

                if (loggers == null && ProjectCollection.Loggers != null)
                {
                    loggers = ProjectCollection.Loggers;
                }

                bool result = instance.Build(targets, loggers, remoteLoggers, null, ProjectCollection.MaxNodeCount, out _);

                return result;
            }

            /// <summary>
            /// Tests whether a given project IS or IMPORTS some given project xml root element.
            /// </summary>
            /// <param name="xmlRootElement">The project xml root element in question.</param>
            /// <returns>True if this project is or imports the xml file; false otherwise.</returns>
            public bool UsesProjectRootElement(ProjectRootElement xmlRootElement)
            {
                if (ReferenceEquals(Xml, xmlRootElement))
                {
                    return true;
                }

                if (_data.ImportClosure.Any(import => ReferenceEquals(import.ImportedProject, xmlRootElement)))
                {
                    return true;
                }

                return false;
            }

            /// <summary>
            /// If the ProjectItemElement evaluated to more than one ProjectItem, replaces it with a new ProjectItemElement for each one of them.
            /// If the ProjectItemElement did not evaluate into more than one ProjectItem, does nothing.
            /// Returns true if a split occurred, otherwise false.
            /// </summary>
            /// <remarks>
            /// A ProjectItemElement could have resulted in several items if it contains wildcards or item or property expressions.
            /// Before any edit to a ProjectItem (remove, rename, set metadata, or remove metadata) this must be called to make
            /// sure that the edit does not affect any other ProjectItems originating in the same ProjectItemElement.
            ///
            /// For example, an item xml with an include of "@(x)" could evaluate to items "a", "b", and "c". If "b" is removed, then the original
            /// item xml must be removed and replaced with three, then the one corresponding to "b" can be removed.
            ///
            /// This is an unsophisticated approach; the best that can be said is that the result will likely be correct, if not ideal.
            /// For example, perhaps the user would rather remove the item from the original list "x" instead of expanding the list.
            /// Or, perhaps the user would rather the property in "$(p)\a;$(p)\b" not be expanded when "$(p)\b" is removed.
            /// If that's important, the host can manipulate the ProjectItemElement's directly, instead, and it can be as fastidious as it wishes.
            /// </remarks>
            internal bool SplitItemElementIfNecessary(ProjectItemElement itemElement)
            {
                if (!ItemElementRequiresSplitting(itemElement))
                {
                    return false;
                }

                ErrorUtilities.VerifyThrowInvalidOperation(!ThrowInsteadOfSplittingItemElement, "OM_CannotSplitItemElementWhenSplittingIsDisabled", itemElement.Location, $"{nameof(Project)}.{nameof(ThrowInsteadOfSplittingItemElement)}");

                var relevantItems = new List<ProjectItem>();

                foreach (ProjectItem item in Items)
                {
                    if (item.Xml == itemElement)
                    {
                        relevantItems.Add(item);
                    }
                }

                foreach (ProjectItem item in relevantItems)
                {
                    item.SplitOwnItemElement();
                }

                itemElement.Parent.RemoveChild(itemElement);

                return true;
            }

            internal bool ItemElementRequiresSplitting(ProjectItemElement itemElement)
            {
                var hasCharactersThatRequireSplitting = FileMatcher.HasWildcardsSemicolonItemOrPropertyReferences(itemElement.Include);

                return hasCharactersThatRequireSplitting;
            }

            /// <summary>
            /// Examines the provided ProjectItemElement to see if it has a wildcard that would match the
            /// item we wish to add, and does not have a condition or an exclude.
            /// Works conservatively - if there is anything that might cause doubt, considers the candidate to not be suitable.
            /// Returns true if it is suitable, otherwise false.
            /// </summary>
            /// <remarks>
            /// Outside this class called ONLY from <see cref="ProjectItem.Rename(string)"/>ProjectItem.Rename(string name).
            /// </remarks>
            public bool IsSuitableExistingItemXml(ProjectItemElement candidateExistingItemXml, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
            {
                if (candidateExistingItemXml.Condition.Length != 0 || candidateExistingItemXml.Exclude.Length != 0 || !candidateExistingItemXml.IncludeHasWildcards)
                {
                    return false;
                }

                if ((metadata?.Any() == true) || candidateExistingItemXml.Count > 0)
                {
                    // Don't try to make sure the metadata are the same.
                    return false;
                }

                string evaluatedExistingInclude = _data.Expander.ExpandIntoStringLeaveEscaped(candidateExistingItemXml.Include, ExpanderOptions.ExpandProperties, candidateExistingItemXml.IncludeLocation);

                string[] existingIncludePieces = evaluatedExistingInclude.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);

                foreach (string existingIncludePiece in existingIncludePieces)
                {
                    if (!FileMatcher.HasWildcards(existingIncludePiece))
                    {
                        continue;
                    }

                    FileMatcher.Result match = FileMatcher.Default.FileMatch(existingIncludePiece, unevaluatedInclude);

                    if (match.isLegalFileSpec && match.isMatch)
                    {
                        // The wildcard in the original item spec will match the new item that
                        // user is trying to add.
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Before an item changes its item type, it must be removed from
            /// our datastructures, which key off item type.
            /// This should be called ONLY by ProjectItems, in this situation.
            /// </summary>
            public void RemoveItemBeforeItemTypeChange(ProjectItem item)
            {
                _data.RemoveItem(item);
            }

            /// <summary>
            /// After an item has changed its item type, it needs to be added back again,
            /// since our data structures key off the item type.
            /// This should be called ONLY by ProjectItems, in this situation.
            /// </summary>
            public void ReAddExistingItemAfterItemTypeChange(ProjectItem item)
            {
                _data.AddItem(item);
                _data.AddItemIgnoringCondition(item);
            }

            /// <summary>
            /// Provided a property that is already part of this project, does a best-effort expansion
            /// of the unevaluated value provided and sets it as the evaluated value.
            /// </summary>
            /// <remarks>
            /// On project in order to keep Project's expander hidden.
            /// </remarks>
            public string ExpandPropertyValueBestEffortLeaveEscaped(string unevaluatedValue, ElementLocation propertyLocation)
            {
                string evaluatedValueEscaped = _data.Expander.ExpandIntoStringLeaveEscaped(unevaluatedValue, ExpanderOptions.ExpandProperties, propertyLocation);

                return evaluatedValueEscaped;
            }

            /// <summary>
            /// Provided an item element that has been renamed with a new unevaluated include,
            /// returns a best effort guess at the evaluated include that results.
            /// If the best effort expansion produces anything other than one item, it just
            /// returns the unevaluated include.
            /// This is not at all generalized, but useful for the majority case where an item is a very
            /// simple file name with perhaps a property prefix.
            /// </summary>
            /// <remarks>
            /// On project in order to keep Project's expander hidden.
            /// </remarks>
            public string ExpandItemIncludeBestEffortLeaveEscaped(ProjectItemElement renamedItemElement)
            {
                if (renamedItemElement.Exclude.Length > 0)
                {
                    return renamedItemElement.Include;
                }

                var itemFactory = new ProjectItemFactory(Owner, renamedItemElement);

                List<ProjectItem> items = Evaluator<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>.CreateItemsFromInclude(
                    DirectoryPath,
                    renamedItemElement,
                    itemFactory,
                    renamedItemElement.Include,
                    _data.Expander,
                    LoggingService,
                    FullPath,
                    s_buildEventContext);

                if (items.Count != 1)
                {
                    return renamedItemElement.Include;
                }

                return ((IItem)items[0]).EvaluatedIncludeEscaped;
            }

            /// <summary>
            /// Provided a metadatum that is already part of this project, does a best-effort expansion
            /// of the unevaluated value provided and returns the resulting value.
            /// This is a interim expansion only: it may not be the value that a full project reevaluation would produce.
            /// The metadata table passed in is that of the parent item or item definition.
            /// </summary>
            /// <remarks>
            /// On project in order to keep Project's expander hidden.
            /// </remarks>
            public string ExpandMetadataValueBestEffortLeaveEscaped(IMetadataTable metadataTable, string unevaluatedValue, ElementLocation metadataLocation)
            {
                ErrorUtilities.VerifyThrow(_data.Expander.Metadata == null, "Should be null");

                _data.Expander.Metadata = metadataTable;
                string evaluatedValueEscaped = _data.Expander.ExpandIntoStringLeaveEscaped(unevaluatedValue, ExpanderOptions.ExpandAll, metadataLocation);
                _data.Expander.Metadata = null;

                return evaluatedValueEscaped;
            }

            /// <summary>
            /// Called by the project collection to indicate to this project that it is no longer loaded.
            /// </summary>
            public override void Unload()
            {
                Xml.OnAfterProjectRename -= _renameHandler;
                Xml.OnProjectXmlChanged -= ProjectRootElement_ProjectXmlChangedHandler;
                _renameHandler = null;
            }

            /// <summary>
            /// Verify that the provided object location is in the same file as the project.
            /// If it is not, throws an InvalidOperationException indicating that imported evaluated objects should not be modified.
            /// This prevents, for example, accidentally updating something like the OutputPath property, that you want be in the
            /// main project, but for some reason was actually read in from an imported targets file.
            /// </summary>
            internal void VerifyThrowInvalidOperationNotImported(ProjectRootElement otherXml)
            {
                ErrorUtilities.VerifyThrowInternalNull(otherXml);
                ErrorUtilities.VerifyThrowInvalidOperation(ReferenceEquals(Xml, otherXml), "OM_CannotModifyEvaluatedObjectInImportedFile", otherXml.Location.File);
            }

            /// <summary>
            /// Common code for the AddItem methods.
            /// </summary>
            private List<ProjectItem> AddItemHelper(ProjectItemElement itemElement, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata)
            {
                var itemFactory = new ProjectItemFactory(Owner, itemElement);

                List<ProjectItem> items = Evaluator<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>.CreateItemsFromInclude(
                    DirectoryPath,
                    itemElement,
                    itemFactory,
                    unevaluatedInclude,
                    _data.Expander,
                    LoggingService,
                    FullPath,
                    s_buildEventContext);

                foreach (ProjectItem item in items)
                {
                    _data.AddItem(item);
                    _data.AddItemIgnoringCondition(item);
                }

                if (metadata != null)
                {
                    foreach (ProjectItem item in items)
                    {
                        foreach (KeyValuePair<string, string> metadatum in metadata)
                        {
                            item.SetMetadataValue(metadatum.Key, metadatum.Value);
                        }
                    }
                }

                // The old OM attempted to evaluate and return the resulting item, or if several then whatever was the "first" returned.
                // This was rather arbitrary, and made it impossible for the caller to retrieve the whole set.
                return items;
            }

            /// <summary>
            /// Helper for <see cref="RemoveItem"/> and <see cref="RemoveItems"/>.
            /// If the item is not associated with a project, returns false.
            /// If the item is not present in the evaluated project, returns false.
            /// If the item is associated with another project, throws ArgumentException.
            /// Otherwise removes the item and returns true.
            /// </summary>
            private bool RemoveItemHelper(ProjectItem item)
            {
                ErrorUtilities.VerifyThrowArgumentNull(item);

                if (item.Project == null || item.Xml.Parent == null)
                {
                    // Return rather than throwing: this is to make it easier
                    // to enumerate over a list of items to remove.
                    return false;
                }

                ErrorUtilities.VerifyThrowArgument(item.Project == Owner, "OM_IncorrectObjectAssociation", "ProjectItem", "Project");

                VerifyThrowInvalidOperationNotImported(item.Xml.ContainingProject);

                SplitItemElementIfNecessary(item.Xml);

                ProjectElementContainer parent = item.Xml.Parent;

                item.Xml.Parent.RemoveChild(item.Xml);

                if (parent.Count == 0)
                {
                    parent.Parent.RemoveChild(parent);
                }

                bool result = _data.RemoveItem(item);

                return result;
            }

            /// <summary>
            /// Re-evaluates the project using the specified logging service.
            /// </summary>
            private void ReevaluateIfNecessary(ILoggingService loggingServiceForEvaluation, EvaluationContext evaluationContext = null)
            {
                ReevaluateIfNecessary(loggingServiceForEvaluation, _loadSettings, evaluationContext);
            }

            /// <summary>
            /// Re-evaluates the project using the specified logging service and load settings.
            /// </summary>
            private void ReevaluateIfNecessary(
                ILoggingService loggingServiceForEvaluation,
                ProjectLoadSettings loadSettings,
                EvaluationContext evaluationContext = null)
            {
                // We will skip the evaluation if the flag is set. This will give us better performance on scenarios
                // that we know we don't have to reevaluate. One example is project conversion bulk addfiles and set attributes.
                if (!SkipEvaluation && !ProjectCollection.SkipEvaluation && IsDirty)
                {
                    try
                    {
                        Reevaluate(loggingServiceForEvaluation, loadSettings, evaluationContext);
                    }
                    catch (InvalidProjectFileException ex)
                    {
                        loggingServiceForEvaluation.LogInvalidProjectFileError(s_buildEventContext, ex);
                        throw;
                    }
                }
            }

            /// <summary>
            /// Creates a project instance based on this project using the specified logging service.
            /// </summary>
            private ProjectInstance CreateProjectInstance(
                ILoggingService loggingServiceForEvaluation,
                ProjectInstanceSettings settings,
                EvaluationContext evaluationContext)
            {
                ReevaluateIfNecessary(loggingServiceForEvaluation, evaluationContext);

                return new ProjectInstance(_data, DirectoryPath, FullPath, ProjectCollection.HostServices, ProjectCollection.EnvironmentProperties, settings);
            }

            private void Reevaluate(
                ILoggingService loggingServiceForEvaluation,
                ProjectLoadSettings loadSettings,
                EvaluationContext evaluationContext = null)
            {
                evaluationContext = evaluationContext?.ContextForNewProject() ?? EvaluationContext.Create(EvaluationContext.SharingPolicy.Isolated, loadSettings);

                Evaluator<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>.Evaluate(
                    _data,
                    Owner,
                    Xml,
                    loadSettings,
                    ProjectCollection.MaxNodeCount,
                    ProjectCollection.EnvironmentProperties,
                    loggingServiceForEvaluation,
                    new ProjectItemFactory(Owner),
                    ProjectCollection,
                    Owner._directoryCacheFactory,
                    ProjectCollection.ProjectRootElementCache,
                    s_buildEventContext,
                    evaluationContext.SdkResolverService,
                    BuildEventContext.InvalidSubmissionId,
                    evaluationContext,
                    _interactive);

                ErrorUtilities.VerifyThrow(LastEvaluationId != BuildEventContext.InvalidEvaluationId, "Evaluation should produce an evaluation ID");

                // We have to do this after evaluation, because evaluation might have changed
                // the imports being pulled in.
                int highestXmlVersion = Xml.Version;

                if (_data.ImportClosure != null)
                {
                    foreach (ResolvedImport import in _data.ImportClosure)
                    {
                        highestXmlVersion = (highestXmlVersion < import.VersionEvaluated)
                            ? import.VersionEvaluated
                            : highestXmlVersion;
                    }
                }

                _explicitlyMarkedDirty = false;
                _evaluatedVersion = highestXmlVersion;
                _evaluatedToolsetCollectionVersion = ProjectCollection.ToolsetsVersion;
                _data.HasUnsavedChanges = false;

                ErrorUtilities.VerifyThrow(!IsDirty, "Should not be dirty now");
            }

            /// <summary>
            /// Common code for the constructors.
            /// Applies global properties that are on the collection.
            /// Global properties provided for the project overwrite any global properties from the collection that have the same name.
            /// Global properties may be null.
            /// Tools version may be null.
            /// </summary>
            internal void Initialize(IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, ProjectLoadSettings loadSettings, EvaluationContext evaluationContext, bool interactive)
            {
                Xml.MarkAsExplicitlyLoaded();

                var globalPropertiesCollection = new PropertyDictionary<ProjectPropertyInstance>();
                foreach (ProjectPropertyInstance property in ProjectCollection.GlobalPropertiesCollection)
                {
                    ProjectPropertyInstance clone = property.DeepClone();
                    globalPropertiesCollection.Set(clone);
                }

                if (globalProperties != null)
                {
                    foreach (KeyValuePair<string, string> pair in globalProperties)
                    {
                        if (String.Equals(pair.Key, Constants.SubToolsetVersionPropertyName, StringComparison.OrdinalIgnoreCase) && subToolsetVersion != null)
                        {
                            // if we have a sub-toolset version explicitly provided by the ProjectInstance constructor, AND a sub-toolset version provided as a global property,
                            // make sure that the one passed in with the constructor wins.  If there isn't a matching global property, the sub-toolset version will be set at
                            // a later point.
                            globalPropertiesCollection.Set(ProjectPropertyInstance.Create(pair.Key, subToolsetVersion));
                        }
                        else
                        {
                            globalPropertiesCollection.Set(ProjectPropertyInstance.Create(pair.Key, pair.Value));
                        }
                    }
                }

                // For back compat Project based evaluations should, by default, evaluate elements with false conditions
                var canEvaluateElementsWithFalseConditions = Traits.Instance.EscapeHatches.EvaluateElementsWithFalseConditionInProjectEvaluation ?? !loadSettings.HasFlag(ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition);

                _data = new Data(Owner, globalPropertiesCollection, toolsVersion, subToolsetVersion, canEvaluateElementsWithFalseConditions);

                _loadSettings = loadSettings;
                _interactive = interactive;

                ErrorUtilities.VerifyThrow(LastEvaluationId == BuildEventContext.InvalidEvaluationId, "This is the first evaluation therefore the last evaluation id is invalid");

                ReevaluateIfNecessary(evaluationContext);

                ErrorUtilities.VerifyThrow(LastEvaluationId != BuildEventContext.InvalidEvaluationId, "Last evaluation ID must be valid after the first evaluation");

                // Cause the project to be actually loaded into the collection, and register for
                // rename notifications so we can subsequently update the collection.
                _renameHandler = (string oldFullPath) => ProjectCollection.OnAfterRenameLoadedProject(oldFullPath, Owner);

                Xml.OnAfterProjectRename += _renameHandler;
                Xml.OnProjectXmlChanged += ProjectRootElement_ProjectXmlChangedHandler;

                _renameHandler(null /* not previously named */);
            }

            /// <summary>
            /// Raised when any XML in the underlying ProjectRootElement has changed.
            /// </summary>
            private void ProjectRootElement_ProjectXmlChangedHandler(object sender, ProjectXmlChangedEventArgs args)
            {
                Xml.MarkProjectDirty(Owner);
            }

            /// <summary>
            /// Tries to find a ProjectItemElement already in the project file XML that has a wildcard that would match the
            /// item we wish to add, does not have a condition or an exclude, and is within an itemgroup without a condition.
            ///
            /// For perf reasons, this method does several jobs in one.
            /// If it finds a suitable existing item element, it returns that as the out parameter, otherwise the out parameter returns null.
            /// Otherwise, if it finds an item element suitable to be just below our new element, it returns that.
            /// Otherwise, if it finds an item group at least that's suitable to put our element in somewhere, it returns that.
            ///
            /// Returns null if the include of the item being added itself has wildcards, or semicolons, as the case is too difficult.
            /// </summary>
            private ProjectElement GetAnySuitableExistingItemXml(string itemType, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata, out ProjectItemElement suitableExistingItemXml)
            {
                suitableExistingItemXml = null;

                if (FileMatcher.HasWildcardsSemicolonItemOrPropertyReferences(unevaluatedInclude))
                {
                    return null;
                }

                if (metadata?.Any() == true)
                {
                    // Don't bother trying to match up metadata
                    return null;
                }

                // In case we don't find a suitable existing item xml, at least find
                // a good item group to add to. Either the first item group with at least one
                // item of the same type, or else the first empty item group without a condition.
                ProjectItemGroupElement itemGroupToAddTo = null;

                ProjectItemElement itemToAddBefore = null;

                foreach (ProjectItemGroupElement itemGroupXml in Xml.ItemGroups)
                {
                    if (itemGroupXml.Condition.Length > 0)
                    {
                        continue;
                    }

                    if (itemGroupXml.DefinitelyAreNoChildrenWithWildcards)
                    {
                        continue;
                    }

                    if (itemGroupToAddTo == null && itemGroupXml.Count == 0)
                    {
                        itemGroupToAddTo = itemGroupXml;
                    }

                    foreach (ProjectItemElement existingItemXml in itemGroupXml.Items)
                    {
                        if (!MSBuildNameIgnoreCaseComparer.Default.Equals(itemType, existingItemXml.ItemType))
                        {
                            continue;
                        }

                        if (itemGroupToAddTo == null || itemGroupToAddTo.Count == 0)
                        {
                            itemGroupToAddTo = itemGroupXml;
                        }

                        // if the include sorts after us, store this item, so we can add
                        // right after it if need be. For example if the item is "b.cs" and we are planning to add "a.cs"
                        // then we know that we will want to add it just above this item. We can avoid another scan to figure that out.
                        if (itemToAddBefore == null && String.Compare(unevaluatedInclude, existingItemXml.Include, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            itemToAddBefore = existingItemXml;
                        }

                        if (IsSuitableExistingItemXml(existingItemXml, unevaluatedInclude, metadata))
                        {
                            suitableExistingItemXml = existingItemXml;
                            return null;
                        }
                    }
                }

                if (itemToAddBefore == null)
                {
                    return itemGroupToAddTo;
                }

                return itemToAddBefore;
            }

            /// <summary>
            /// Recursive helper for <see cref="GetLogicalProject()">GetLogicalProject</see>.
            /// </summary>
            private IEnumerable<ProjectElement> GetLogicalProject(IEnumerable<ProjectElement> projectElements)
            {
                foreach (ProjectElement element in projectElements)
                {
                    if (!(element is ProjectImportElement import))
                    {
                        yield return element;
                    }
                    else
                    {
                        // Get the project root elements of all the imports resulting from this import statement (there could be multiple if there is a wild card).
                        IEnumerable<ProjectRootElement> children = _data.ImportClosure.Where(resolvedImport => ReferenceEquals(resolvedImport.ImportingElement, import)).Select(triple => triple.ImportedProject);

                        foreach (ProjectRootElement child in children)
                        {
                            if (child != null)
                            {
                                // The import's condition must have evaluated to true, to traverse into it
                                IEnumerable<ProjectElement> childElements = GetLogicalProject(child.AllChildren);

                                foreach (ProjectElement childElement in childElements)
                                {
                                    yield return childElement;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Internal
        ///
        /// Note: For deeper integration of remote project we might need to expose [some] of this functionality via IProjectLink3.
        /// </summary>
        private interface IProjectLinkInternal
        {
            bool IsLinked { get; }

            bool IsZombified { get; set; }

            Data TestOnlyGetPrivateData { get; }

            ISet<string> GlobalPropertiesToTreatAsLocal { get; }

            bool UsesProjectRootElement(ProjectRootElement xmlRootElement);

            bool IsSuitableExistingItemXml(ProjectItemElement candidateExistingItemXml, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata);

            void RemoveItemBeforeItemTypeChange(ProjectItem item);

            void ReAddExistingItemAfterItemTypeChange(ProjectItem item);

            string ExpandPropertyValueBestEffortLeaveEscaped(string unevaluatedValue, ElementLocation propertyLocation);

            string ExpandItemIncludeBestEffortLeaveEscaped(ProjectItemElement renamedItemElement);

            string ExpandMetadataValueBestEffortLeaveEscaped(IMetadataTable metadataTable, string unevaluatedValue, ElementLocation metadataLocation);
        }

        private class ProjectLinkInternalNotImplemented : IProjectLinkInternal
        {
            public Data TestOnlyGetPrivateData { get { throw new NotImplementedException(); } }

            public ISet<string> GlobalPropertiesToTreatAsLocal { get { throw new NotImplementedException(); } }

            public bool IsLinked => true;

            public bool IsZombified { get; set; }

            public bool UsesProjectRootElement(ProjectRootElement xmlRootElement) { throw new NotImplementedException(); }

            public bool IsSuitableExistingItemXml(ProjectItemElement candidateExistingItemXml, string unevaluatedInclude, IEnumerable<KeyValuePair<string, string>> metadata) { throw new NotImplementedException(); }

            public void RemoveItemBeforeItemTypeChange(ProjectItem item) { throw new NotImplementedException(); }

            public void ReAddExistingItemAfterItemTypeChange(ProjectItem item) { throw new NotImplementedException(); }

            public string ExpandPropertyValueBestEffortLeaveEscaped(string unevaluatedValue, ElementLocation propertyLocation) { throw new NotImplementedException(); }

            public string ExpandItemIncludeBestEffortLeaveEscaped(ProjectItemElement renamedItemElement) { throw new NotImplementedException(); }

            public string ExpandMetadataValueBestEffortLeaveEscaped(IMetadataTable metadataTable, string unevaluatedValue, ElementLocation metadataLocation) { throw new NotImplementedException(); }
        }

        /// <summary>
        /// Encapsulates the backing data of a Project, so that it can be passed to the Evaluator to
        /// fill in on a re-evaluation without having to expose property setters.
        /// </summary>
        /// <remarks>
        /// This object is only passed to the Evaluator.
        /// </remarks>
        internal class Data : IItemProvider<ProjectItem>, IPropertyProvider<ProjectProperty>, IEvaluatorData<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>
        {
            /// <summary>
            /// Almost always, projects have the same set of targets because they all import the same ones.
            /// So we keep around the last set seen and if ours is the same at the end of evaluation, unify the references.
            /// </summary>
            private static WeakReference<RetrievableEntryHashSet<ProjectTargetInstance>> s_typicalTargetsCollection;

            /// <summary>
            /// List of names of the properties that, while global, are still treated as overridable.
            /// </summary>
            private ISet<string> _globalPropertiesToTreatAsLocal;

            /// <summary>
            /// Constructor taking the immutable global properties and tools version.
            /// Tools version may be null.
            /// </summary>
            internal Data(Project project, PropertyDictionary<ProjectPropertyInstance> globalProperties, string explicitToolsVersion, string explicitSubToolsetVersion, bool CanEvaluateElementsWithFalseConditions)
            {
                Project = project;
                GlobalPropertiesDictionary = globalProperties;
                ExplicitToolsVersion = explicitToolsVersion;
                ExplicitSubToolsetVersion = explicitSubToolsetVersion;
                this.CanEvaluateElementsWithFalseConditions = CanEvaluateElementsWithFalseConditions;
            }

            /// <summary>
            /// Whether evaluation should collect items ignoring condition,
            /// as well as items respecting condition; and collect
            /// conditioned properties, as well as regular properties.
            /// </summary>
            public bool ShouldEvaluateForDesignTime => true;

            public bool CanEvaluateElementsWithFalseConditions { get; }

            /// <summary>
            /// Collection of all evaluated item definitions, one per item-type.
            /// </summary>
            IEnumerable<ProjectItemDefinition> IEvaluatorData<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>.ItemDefinitionsEnumerable => ItemDefinitions.Values;

            /// <summary>
            /// DefaultTargets specified in the project, or
            /// the logically first target if no DefaultTargets is
            /// specified in the project.
            /// </summary>
            public List<string> DefaultTargets { get; set; }

            /// <summary>
            /// The global properties to evaluate with, if any.
            /// Can never be null.
            /// Read-only; to use different global properties, evaluate yourself a new project.
            /// </summary>
            public PropertyDictionary<ProjectPropertyInstance> GlobalPropertiesDictionary { get; }

            /// <summary>
            /// A dictionary of all of the properties read from environment variables during evaluation.
            /// </summary>
            public PropertyDictionary<ProjectPropertyInstance> EnvironmentVariablePropertiesDictionary => this.Project.ProjectCollection.EnvironmentProperties;

            /// <summary>
            /// List of names of the properties that, while global, are still treated as overridable.
            /// </summary>
            public ISet<string> GlobalPropertiesToTreatAsLocal => _globalPropertiesToTreatAsLocal ?? (_globalPropertiesToTreatAsLocal =
                                                                      new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default));

            /// <summary>
            /// InitialTargets specified in the project, plus those
            /// in all imports, gathered depth-first.
            /// </summary>
            public List<string> InitialTargets { get; set; }

            /// <summary>
            /// Sets or retrieves the list of targets which run before the keyed target.
            /// </summary>
            public IDictionary<string, List<TargetSpecification>> BeforeTargets { get; set; }

            /// <summary>
            /// Sets or retrieves the list of targets which run after the keyed target.
            /// </summary>
            public IDictionary<string, List<TargetSpecification>> AfterTargets { get; set; }

            /// <summary>
            /// The externally specified tools version, if any.
            /// For example, the tools version from a /tv switch.
            /// Not necessarily the same as the tools version from the project tag or of the toolset used.
            /// May be null.
            /// Flows through to called projects.
            /// </summary>
            public string ExplicitToolsVersion { get; }

            /// <summary>
            /// The toolset data used during evaluation.
            /// </summary>
            public Toolset Toolset { get; private set; }

            /// <summary>
            /// The externally specified sub-toolset version that, combined with the ToolsVersion, is used to determine
            /// the toolset properties for this project.
            /// </summary>
            public string ExplicitSubToolsetVersion { get; }

            /// <summary>
            /// The sub-toolset version that, combined with the ToolsVersion, was used to determine
            /// the toolset properties for this project.
            /// </summary>
            public string SubToolsetVersion { get; private set; }

            /// <summary>
            /// Items in this project, ordered within groups of item types.
            /// Protected by an upcast to IEnumerable.
            /// </summary>
            public IItemDictionary<ProjectItem> Items { get; private set; }

            public List<ProjectItemElement> EvaluatedItemElements { get; private set; }

            /// <summary>
            /// List of items that link the XML items and evaluated items,
            /// evaluated as if their conditions were true.
            /// This is useful for hosts that wish to display all items regardless of their condition.
            /// This is an ordered collection.
            /// </summary>
            public ItemDictionary<ProjectItem> ItemsIgnoringCondition { get; private set; }

            /// <summary>
            /// Collection of properties that link the XML properties and evaluated properties.
            /// Since evaluation has occurred, this is an unordered collection.
            /// Includes any global and reserved properties.
            /// </summary>
            public PropertyDictionary<ProjectProperty> Properties { get; private set; }

            /// <summary>
            /// Collection of possible values implied for properties contained in the conditions found on properties,
            /// property groups, imports, and whens.
            ///
            /// For example, if the following conditions existed on properties in a project:
            ///
            /// Condition="'$(Configuration)|$(Platform)' == 'Debug|x86'"
            /// Condition="'$(Configuration)' == 'Release'"
            ///
            /// the table would be populated with
            ///
            /// { "Configuration", { "Debug", "Release" }}
            /// { "Platform", { "x86" }}
            ///
            /// This is used by Visual Studio to determine the configurations defined in the project.
            /// </summary>
            public Dictionary<string, List<string>> ConditionedProperties { get; private set; }

            /// <inheritdoc />
            public int EvaluationId { get; set; } = BuildEventContext.InvalidEvaluationId;

            /// <summary>
            /// The root directory for this project.
            /// </summary>
            public string Directory => Project.DirectoryPath;

            /// <summary>
            /// Registry of usingtasks, for build.
            /// </summary>
            public TaskRegistry TaskRegistry { get; set; }

            /// <summary>
            /// Get the item types that have at least one item.
            /// Read only collection.
            /// </summary>
            /// <comments>
            /// item.ItemTypes is a KeyCollection, so it doesn't need any
            /// additional read-only protection.
            /// </comments>
            public ICollection<string> ItemTypes => Items.ItemTypes;

            /// <summary>
            /// Properties encountered during evaluation. These are read during the first evaluation pass.
            /// Unlike those returned by the Properties property, these are ordered, and includes any properties that
            /// were subsequently overridden by others with the same name. It does not include any
            /// properties whose conditions did not evaluate to true.
            /// It does not include any properties added since the last evaluation.
            /// </summary>
            internal IList<ProjectProperty> AllEvaluatedProperties { get; private set; }

            /// <summary>
            /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
            /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
            /// were subsequently overridden by others with the same name and item type. It does not include any
            /// elements whose conditions did not evaluate to true.
            /// It does not include any item definition metadata added since the last evaluation.
            /// </summary>
            internal IList<ProjectMetadata> AllEvaluatedItemDefinitionMetadata { get; private set; }

            /// <summary>
            /// Items encountered during evaluation. These are read during the third evaluation pass.
            /// Unlike those returned by the Items property, these are ordered.
            /// It does not include any elements whose conditions did not evaluate to true.
            /// It does not include any items added since the last evaluation.
            /// </summary>
            internal IList<ProjectItem> AllEvaluatedItems { get; private set; }

            /// <summary>
            /// Expander to use to expand any expressions encountered after the project has been fully evaluated.
            /// For example, to expand the values of any properties added at design time.
            /// It's convenient to store it here.
            /// </summary>
            internal Expander<ProjectProperty, ProjectItem> Expander { get; private set; }

            /// <summary>
            /// Whether something in this data has been modified since evaluation.
            /// For example, a global property has been set.
            /// </summary>
            internal bool HasUnsavedChanges { get; set; }

            /// <summary>
            /// Collection of all evaluated item definitions, one per item-type.
            /// </summary>
            internal RetrievableEntryHashSet<ProjectItemDefinition> ItemDefinitions { get; private set; }

            /// <summary>
            /// Project that owns this data.
            /// </summary>
            internal Project Project { get; }

            /// <summary>
            /// Targets in the project, used to build.
            /// </summary>
            internal RetrievableEntryHashSet<ProjectTargetInstance> Targets { get; set; }

            /// <summary>
            /// Complete list of all imports pulled in during evaluation.
            /// This includes the outer project itself.
            /// </summary>
            internal List<ResolvedImport> ImportClosure { get; private set; }

            /// <summary>
            /// Complete list of all imports pulled in during evaluation including duplicate imports.
            /// This includes the outer project itself.
            /// </summary>
            internal List<ResolvedImport> ImportClosureWithDuplicates { get; private set; }

            /// <summary>
            /// The toolsversion that was originally specified on the project's root element.
            /// </summary>
            internal string OriginalProjectToolsVersion { get; private set; }

            /// <summary>
            /// Whether when we read a ToolsVersion other than the current one in the Project tag, we treat it as the current one.
            /// </summary>
            internal bool UsingDifferentToolsVersionFromProjectFile { get; private set; }

            /// <summary>
            /// expose mutable precalculated cache to outside so that other can take advantage of the cache as well.
            /// </summary>
            internal MultiDictionary<string, ProjectItem> ItemsByEvaluatedIncludeCache { get; private set; }

            /// <summary>
            /// Prepares the data object for evaluation.
            /// </summary>
            public void InitializeForEvaluation(IToolsetProvider toolsetProvider, EvaluationContext evaluationContext, LoggingContext loggingContext)
            {
                DefaultTargets = null;
                Properties = new PropertyDictionary<ProjectProperty>();
                ConditionedProperties = new Dictionary<string, List<string>>(MSBuildNameIgnoreCaseComparer.Default);
                Items = new ItemDictionary<ProjectItem>();
                ItemsIgnoringCondition = new ItemDictionary<ProjectItem>();
                ItemsByEvaluatedIncludeCache = new MultiDictionary<string, ProjectItem>(StringComparer.OrdinalIgnoreCase);
                Expander = new Expander<ProjectProperty, ProjectItem>(Properties, Items, evaluationContext, loggingContext);
                ItemDefinitions = new RetrievableEntryHashSet<ProjectItemDefinition>(MSBuildNameIgnoreCaseComparer.Default);
                Targets = new RetrievableEntryHashSet<ProjectTargetInstance>(StringComparer.OrdinalIgnoreCase);
                ImportClosure = new List<ResolvedImport>();
                ImportClosureWithDuplicates = new List<ResolvedImport>();
                AllEvaluatedProperties = new List<ProjectProperty>();
                AllEvaluatedItemDefinitionMetadata = new List<ProjectMetadata>();
                AllEvaluatedItems = new List<ProjectItem>();
                EvaluatedItemElements = new List<ProjectItemElement>();
                EvaluationId = BuildEventContext.InvalidEvaluationId;

                _globalPropertiesToTreatAsLocal?.Clear();

                // Include the main project in the list of imports, as this list is
                // used to figure out if any of them have changed.
                RecordImport(null, Project.Xml, Project.Xml.Version, null);

                ElementLocation toolsVersionLocation = Project.Xml.ProjectFileLocation;

                if (Project.Xml.ToolsVersion.Length > 0)
                {
                    OriginalProjectToolsVersion = Project.Xml.ToolsVersion;
                    toolsVersionLocation = Project.Xml.ToolsVersionLocation;
                }

                string toolsVersionToUse = Utilities.GenerateToolsVersionToUse(
                    ExplicitToolsVersion,
                    Project.Xml.ToolsVersion,
                    Project.ProjectCollection.GetToolset,
                    Project.ProjectCollection.DefaultToolsVersion,
                    out var usingDifferentToolsVersionFromProjectFile);

                UsingDifferentToolsVersionFromProjectFile = usingDifferentToolsVersionFromProjectFile;

                Toolset = toolsetProvider.GetToolset(toolsVersionToUse);

                if (Toolset == null)
                {
                    string toolsVersionList = Utilities.CreateToolsVersionListString(Project.ProjectCollection.Toolsets);
                    ProjectErrorUtilities.ThrowInvalidProject(toolsVersionLocation, "UnrecognizedToolsVersion", toolsVersionToUse, toolsVersionList);
                }

                if (ExplicitSubToolsetVersion != null)
                {
                    SubToolsetVersion = ExplicitSubToolsetVersion;
                }
                else
                {
                    SubToolsetVersion = Toolset.GenerateSubToolsetVersion(GlobalPropertiesDictionary);
                }

                // Create a task registry which will fall back on the toolset task registry if necessary.
                TaskRegistry = new TaskRegistry(Toolset, Project.ProjectCollection.ProjectRootElementCache);
            }

            /// <summary>
            /// Indicates to the data block that evaluation has completed,
            /// so for example it can mark datastructures read-only.
            /// </summary>
            public void FinishEvaluation()
            {
                // We assume there will be no further changes to the targets collection
                // This also makes sure that we are thread safe
                Targets.MakeReadOnly();

                if (s_typicalTargetsCollection == null)
                {
                    Targets.TrimExcess();
                    s_typicalTargetsCollection = new WeakReference<RetrievableEntryHashSet<ProjectTargetInstance>>(Targets);
                }
                else
                {
                    // Attempt to unify the references, to save space
                    if (s_typicalTargetsCollection.TryGetTarget(out RetrievableEntryHashSet<ProjectTargetInstance> candidate) && candidate.EntriesAreReferenceEquals(Targets))
                    {
                        // Reuse
                        Targets = candidate;
                    }
                    else
                    {
                        // Else we'll guess that this latest one is a potential match for the next,
                        // if it actually has any elements (eg., it's not a .user or .filters file)
                        if (Targets.Count > 0)
                        {
                            Targets.TrimExcess();
                            s_typicalTargetsCollection.SetTarget(Targets);
                        }
                    }
                }
            }

            /// <summary>
            /// Adds a new item.
            /// </summary>
            public void AddItem(ProjectItem item)
            {
                Items.Add(item);
                ItemsByEvaluatedIncludeCache.Add(item.EvaluatedInclude, item);
            }

            /// <summary>
            /// Adds a new item to the collection of all items ignoring condition.
            /// </summary>
            public void AddItemIgnoringCondition(ProjectItem item)
            {
                ItemsIgnoringCondition.Add(item);
            }

            /// <summary>
            /// Properties encountered during evaluation. These are read during the first evaluation pass.
            /// Unlike those returned by the Properties property, these are ordered, and includes any properties that
            /// were subsequently overridden by others with the same name. It does not include any
            /// properties whose conditions did not evaluate to true.
            /// </summary>
            public void AddToAllEvaluatedPropertiesList(ProjectProperty property)
            {
                ErrorUtilities.VerifyThrowInternalNull(property);
                AllEvaluatedProperties.Add(property);
            }

            /// <summary>
            /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
            /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
            /// were subsequently overridden by others with the same name and item type. It does not include any
            /// elements whose conditions did not evaluate to true.
            /// </summary>
            public void AddToAllEvaluatedItemDefinitionMetadataList(ProjectMetadata itemDefinitionMetadatum)
            {
                ErrorUtilities.VerifyThrowInternalNull(itemDefinitionMetadatum);
                AllEvaluatedItemDefinitionMetadata.Add(itemDefinitionMetadatum);
            }

            /// <summary>
            /// Items encountered during evaluation. These are read during the third evaluation pass.
            /// Unlike those returned by the Items property, these are ordered.
            /// It does not include any elements whose conditions did not evaluate to true.
            /// It does not include any items added since the last evaluation.
            /// </summary>
            public void AddToAllEvaluatedItemsList(ProjectItem item)
            {
                ErrorUtilities.VerifyThrowInternalNull(item);
                AllEvaluatedItems.Add(item);
            }

            /// <summary>
            /// Adds a new item definition.
            /// </summary>
            public IItemDefinition<ProjectMetadata> AddItemDefinition(string itemType)
            {
                ProjectItemDefinition newItemDefinition = new ProjectItemDefinition(Project, itemType);

                ItemDefinitions.Add(newItemDefinition);

                return newItemDefinition;
            }

            /// <summary>
            /// Gets an existing item definition, if any.
            /// </summary>
            public IItemDefinition<ProjectMetadata> GetItemDefinition(string itemType)
            {
                ItemDefinitions.TryGetValue(itemType, out ProjectItemDefinition itemDefinition);
                return itemDefinition;
            }

            /// <summary>
            /// Sets a property which is not derived from Xml.
            /// </summary>
            public ProjectProperty SetProperty(string name, string evaluatedValueEscaped, bool isGlobalProperty, bool mayBeReserved, LoggingContext loggingContext, bool isEnvironmentVariable = false)
            {
                ProjectProperty property = ProjectProperty.Create(Project, name, evaluatedValueEscaped, isGlobalProperty, mayBeReserved, loggingContext);
                Properties.Set(property);

                AddToAllEvaluatedPropertiesList(property);

                return property;
            }

            /// <summary>
            /// Sets a property derived from Xml.
            /// </summary>
            public ProjectProperty SetProperty(ProjectPropertyElement propertyElement, string evaluatedValueEscaped, LoggingContext loggingContext)
            {
                ProjectProperty predecessor = GetProperty(propertyElement.Name);
                ProjectProperty property = ProjectProperty.Create(Project, propertyElement, evaluatedValueEscaped, predecessor);
                Properties.Set(property);

                AddToAllEvaluatedPropertiesList(property);

                return property;
            }

            /// <summary>
            /// Retrieves an existing target, if any.
            /// </summary>
            public ProjectTargetInstance GetTarget(string targetName)
            {
                Targets.TryGetValue(targetName, out ProjectTargetInstance target);
                return target;
            }

            /// <summary>
            /// Adds the specified target, overwriting any existing target with the same name.
            /// </summary>
            public void AddTarget(ProjectTargetInstance target)
            {
                Targets[target.Name] = target;
            }

            /// <summary>
            /// Record an import opened during evaluation.
            /// This is used to check later whether any of them have been changed.
            /// </summary>
            /// <remarks>
            /// This may include imported files that ended up contributing nothing to the evaluated project.
            /// These might otherwise have no strong references to them at all.
            /// If they are dirtied, though, they might affect the evaluated project; and that's why we record them.
            /// Mostly these will be common imports, so they'll be shared anyway.
            /// </remarks>
            public void RecordImport(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated, SdkResult sdkResult)
            {
                ImportClosure.Add(new ResolvedImport(Project, importElement, import, versionEvaluated, sdkResult));
                RecordImportWithDuplicates(importElement, import, versionEvaluated);
            }

            /// <summary>
            /// Record a duplicate import, possible a duplicate import opened during evaluation.
            /// </summary>
            public void RecordImportWithDuplicates(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated)
            {
                ImportClosureWithDuplicates.Add(new ResolvedImport(Project, importElement, import, versionEvaluated, null));
            }

            /// <summary>
            /// Evaluates the provided string by expanding items and properties,
            /// using the current items and properties available.
            /// This is useful for the immediate window.
            /// Does not expand bare metadata expressions.
            /// </summary>
            /// <comment>
            /// Not for internal use.
            /// </comment>
            string IEvaluatorData<ProjectProperty, ProjectItem, ProjectMetadata, ProjectItemDefinition>.ExpandString(string unexpandedValue)
            {
                return Project.ExpandString(unexpandedValue);
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
                // This is for the debugger, which should not get a live Project object,
                // so this is not implemented.
                ErrorUtilities.ThrowInternalErrorUnreachable();
                return false;
            }

            #region IItemProvider<ProjectItem> Members

            /// <summary>
            /// Returns a list of items of the specified type.
            /// If there are none, returns an empty list.
            /// </summary>
            /// <comments>
            /// ItemDictionary returns a read-only collection, so no need to wrap it here.
            /// </comments>
            /// <param name="itemType">The type of items to return.</param>
            /// <returns>A list of matching items.</returns>
            public ICollection<ProjectItem> GetItems(string itemType)
            {
                return Items[itemType];
            }

            #endregion

            #region IPropertyProvider<ProjectProperty> Members

            /// <summary>
            /// Returns the property with the specified name or null if it was not present.
            /// </summary>
            /// <param name="name">The property name.</param>
            /// <returns>The property.</returns>
            public ProjectProperty GetProperty(string name)
            {
                return Properties[name];
            }

            /// <summary>
            /// Returns the property with the specified name or null if it was not present.
            /// </summary>
            /// <returns>The property.</returns>
            public ProjectProperty GetProperty(string name, int startIndex, int endIndex)
            {
                return Properties.GetProperty(name, startIndex, endIndex);
            }

            #endregion

            /// <summary>
            /// Removes an item.
            /// Returns true if it was previously present, otherwise false.
            /// </summary>
            internal bool RemoveItem(ProjectItem item)
            {
                bool result = Items.Remove(item);

                // This remove will not succeed if the item include was changed.
                // If many items are modified and then removed, this will leak them
                // until the next reevaluation.
                ItemsByEvaluatedIncludeCache.Remove(item.EvaluatedInclude, item);

                ItemsIgnoringCondition.Remove(item);

                return result;
            }

            /// <summary>
            /// Returns all items that have the specified evaluated include.
            /// For example, all items that have the evaluated include "bar.cpp".
            /// Typically there will be no more than one, but sometimes there are two items with the
            /// same path and different item types, or even the same item types. This will return
            /// them all.
            /// </summary>
            /// <remarks>
            /// Assumes that the evaluated include value is unescaped.
            /// </remarks>
            internal ICollection<ProjectItem> GetItemsByEvaluatedInclude(string evaluatedInclude)
            {
                // Even if there are no items in itemsByEvaluatedInclude[], it will return an IEnumerable, which is non-null
                ICollection<ProjectItem> items = new ReadOnlyCollection<ProjectItem>(ItemsByEvaluatedIncludeCache[evaluatedInclude]);

                return items;
            }

            /// <summary>
            /// Get the value of a property in this project, or
            /// an empty string if it does not exist.
            /// Returns the unescaped value.
            /// </summary>
            /// <remarks>
            /// A property with a value of empty string and no property
            /// at all are not distinguished between by this method.
            /// That makes it easier to use. To find out if a property is set at
            /// all in the project, use GetProperty(name).
            /// </remarks>
            internal string GetPropertyValue(string name)
            {
                ErrorUtilities.VerifyThrowArgumentLength(name);

                ProjectProperty property = Properties[name];
                string value = property?.EvaluatedValue ?? String.Empty;
                return value;
            }
        }
    }

    /// <summary>
    /// Data class representing a result from <see cref="Project.GetAllGlobs()"/> and its overloads.
    /// This represents all globs found in an item include together with the item element it came from,
    /// the excludes that were present on that item, and all the Remove item elements pertaining to the Include item element.
    /// </summary>
    public class GlobResult
    {
        /// <summary>
        /// Gets the original <see cref="ProjectItemElement"/> that contained the globs.
        /// </summary>
        public ProjectItemElement ItemElement { get; }

        /// <summary>
        /// Gets all the evaluated glob strings (properties expanded) from the include.
        /// </summary>
        public IEnumerable<string> IncludeGlobs { get; }

        /// <summary>
        /// A <see cref="IMSBuildGlob"/> representing the include globs. It also takes the excludes and relevant removes into consideration.
        /// </summary>
        public IMSBuildGlob MsBuildGlob { get; set; }

        /// <summary>
        /// Gets an <see cref="ISet{String}"/> containing strings that were excluded.
        /// </summary>
        public IEnumerable<string> Excludes { get; }

        /// <summary>
        /// Gets an <see cref="ISet{String}"/> containing strings that were later removed via the Remove element.
        /// </summary>
        public IEnumerable<string> Removes { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public GlobResult(ProjectItemElement itemElement, IEnumerable<string> includeGlobStrings, IMSBuildGlob globWithGaps, IEnumerable<string> excludeFragmentStrings, IEnumerable<string> removeFragmentStrings)
        {
            ItemElement = itemElement;

            IncludeGlobs = includeGlobStrings;
            MsBuildGlob = globWithGaps;

            Excludes = excludeFragmentStrings;
            Removes = removeFragmentStrings;
        }
    }

    /// <summary>
    /// Bit flag enum that specifies how a string representing an item matched against an itemspec.
    /// </summary>
    [Flags]
    public enum Provenance
    {
        /// <summary>
        /// Undefined is the bottom element and should not appear in actual results
        /// </summary>
        Undefined = 0,

        /// <summary>
        /// A string matched against a string literal from an itemspec
        /// </summary>
        StringLiteral = 1,

        /// <summary>
        /// A string matched against a glob pattern from an itemspec
        /// </summary>
        Glob = 2,

        /// <summary>
        /// Inconclusive means that the match is indirect, coming from either property or item references.
        /// </summary>
        Inconclusive = 4
    }

    /// <summary>
    /// Enum that specifies how an item element references an item.
    /// </summary>
    public enum Operation
    {
        /// <summary>
        /// The element referenced the item by an Include.
        /// </summary>
        Include,
        /// <summary>
        /// The element referenced the item by an Exclude.
        /// </summary>
        Exclude,
        /// <summary>
        /// The element referenced the item by an Update.
        /// </summary>
        Update,
        /// <summary>
        /// The element referenced the item by a Remove.
        /// </summary>
        Remove
    }

    /// <summary>
    /// Data class representing a result from <see cref="Project.GetItemProvenance(string)"/> and its overloads.
    /// </summary>
    public class ProvenanceResult
    {
        /// <summary>
        /// Gets the <see cref="Operation"/> that was performed.
        /// </summary>
        public Operation Operation { get; }

        /// <summary>
        /// Gets the <see cref="ProjectItemElement"/> that contains the operation.
        /// </summary>
        public ProjectItemElement ItemElement { get; }

        /// <summary>
        /// Gets the <see cref="Provenance"/> of how the item appeared in the operation.
        /// </summary>
        public Provenance Provenance { get; }

        /// <summary>
        /// Gets the number of occurrences of the item.
        /// </summary>
        public int Occurrences { get; }

        /// <summary>
        /// Initializes an instance of the ProvenanceResult class.
        /// </summary>
        public ProvenanceResult(ProjectItemElement itemElement, Operation operation, Provenance provenance, int occurrences)
        {
            ItemElement = itemElement;
            Operation = operation;
            Provenance = provenance;
            Occurrences = occurrences;
        }
    }
}
