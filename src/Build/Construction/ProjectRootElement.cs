// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.ObjectModelRemoting;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;

#nullable disable

namespace Microsoft.Build.Construction
{
    /// <summary>
    /// Event handler for the event fired after this project file is named or renamed.
    /// If the project file has not previously had a name, oldFullPath is null.
    /// </summary>
    internal delegate void RenameHandlerDelegate(string oldFullPath);

    /// <summary>
    /// ProjectRootElement class represents an MSBuild project, an MSBuild targets file or any other file that conforms to MSBuild
    /// project file schema.
    /// This class and its related classes allow a complete MSBuild project or targets file to be read and written.
    /// Comments and whitespace cannot be edited through this model at present.
    ///
    /// Each project root element is associated with exactly one ProjectCollection. This allows the owner of that project collection
    /// to control its lifetime and not be surprised by edits via another project collection.
    /// </summary>
    [DebuggerDisplay("{FullPath} #Children={Count} DefaultTargets={DefaultTargets} ToolsVersion={ToolsVersion} InitialTargets={InitialTargets} ExplicitlyLoaded={IsExplicitlyLoaded}")]
    public partial class ProjectRootElement : ProjectElementContainer
    {
        // Constants for default (empty) project file.
        private const string EmptyProjectFileContent = "{0}<Project{1}{2}>\r\n</Project>";
        private const string EmptyProjectFileXmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";
        private const string EmptyProjectFileToolsVersion = " ToolsVersion=\"" + MSBuildConstants.CurrentToolsVersion + "\"";
        internal const string EmptyProjectFileXmlNamespace = " xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"";

        /// <summary>
        /// The singleton delegate that loads projects into the ProjectRootElement
        /// </summary>
        private static readonly ProjectRootElementCacheBase.OpenProjectRootElement s_openLoaderDelegate = OpenLoader;

        private static readonly ProjectRootElementCacheBase.OpenProjectRootElement s_openLoaderPreserveFormattingDelegate = OpenLoaderPreserveFormatting;

        private const string XmlDeclarationRegExSpecification = @"\A\s*\<\?\s*xml.*\?\>\s*\Z";

#if NET7_0_OR_GREATER
        [GeneratedRegex(XmlDeclarationRegExSpecification, RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture)]
        private static partial Regex XmlDeclarationRegExPattern();
#else
        private static readonly Lazy<Regex> XmlDeclarationRegExPattern = new Lazy<Regex>(() => new Regex(XmlDeclarationRegExSpecification), isThreadSafe: true);
#endif

        /// <summary>
        /// Used to determine if a file is an empty XML file if it ONLY contains an XML declaration like &lt;?xml version="1.0" encoding="utf-8"?&gt;.
        /// </summary>
        private static Regex XmlDeclarationRegEx
        {
            get
            {
#if NET7_0_OR_GREATER
                return XmlDeclarationRegExPattern();
#else
                return XmlDeclarationRegExPattern.Value;
#endif
            }
        }
        /// <summary>
        /// The default encoding to use / assume for a new project.
        /// </summary>
        private static readonly Encoding s_defaultEncoding = Encoding.UTF8;

        /// <summary>
        /// A global counter used to ensure each project version is distinct from every other.
        /// </summary>
        /// <remarks>
        /// This number is static so that it is unique across the appdomain. That is so that a host
        /// can know when a ProjectRootElement has been unloaded (perhaps after modification) and
        /// reloaded -- the version won't reset to '0'.
        /// </remarks>
        private static int s_globalVersionCounter;

        private int _version;

        /// <summary>
        /// Version number of this object that was last saved to disk, or last loaded from disk.
        /// Used to figure whether this object is dirty for saving.
        /// Saving to or loading from a provided stream reader does not modify this value, only saving to or loading from disk.
        /// The actual value is meaningless (since the counter is shared with all projects) --
        /// it should only be compared to a stored value.
        /// Immediately after loading from disk, this has the same value as <see cref="Version">version</see>.
        /// </summary>
        private int _versionOnDisk;

        /// <summary>
        /// The encoding of the project that was (if applicable) loaded off disk, and that will be used to save the project.
        /// </summary>
        /// <value>Defaults to UTF8 for new projects.</value>
        private Encoding _encoding;

        /// <summary>
        /// XML namespace specified and used by this project file. If a namespace was not specified in the project file, this
        /// value will be string.Empty.
        /// </summary>
        internal string XmlNamespace { get; set; }

        /// <summary>
        /// The project file's location. It can be null if the project is not directly loaded from a file.
        /// </summary>
        private ElementLocation _projectFileLocation;

        /// <summary>
        /// The project file's full path, escaped.
        /// </summary>
        private string _escapedFullPath;

        /// <summary>
        /// The directory that the project is in.
        /// Essential for evaluating relative paths.
        /// If the project is not loaded from disk, returns the current-directory from
        /// the time the project was loaded - this is the same behavior as Whidbey/Orcas.
        /// </summary>
        private string _directory;

        /// <summary>
        /// The time that this object was last changed. If it hasn't
        /// been changed since being loaded or created, its value is <see cref="DateTime.MinValue"/>.
        /// Stored as UTC as this is faster when there are a large number of rapid edits.
        /// </summary>
        private DateTime _timeLastChangedUtc;

        /// <summary>
        /// The last-write-time of the file that was read, when it was read.
        /// This can be used to see whether the file has been changed on disk
        /// by an external means.
        /// </summary>
        private DateTime _lastWriteTimeWhenReadUtc;

        /// <summary>
        /// Reason it was last marked dirty; unlocalized, for debugging
        /// </summary>
        private string _dirtyReason = "first created project {0}";

        /// <summary>
        /// Parameter to be formatted into the dirty reason
        /// </summary>
        private string _dirtyParameter = String.Empty;

        internal ProjectRootElementLink RootLink => (ProjectRootElementLink)Link;

        /// <summary>
        /// External projects support
        /// </summary>
        internal ProjectRootElement(ProjectRootElementLink link)
            : base(link)
        {
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from a XmlReader.
        /// May throw InvalidProjectFileException.
        /// Leaves the project dirty, indicating there are unsaved changes.
        /// Used to create a root element for solutions loaded by the 3.5 version of the solution wrapper.
        /// </summary>
        internal ProjectRootElement(XmlReader xmlReader, ProjectRootElementCacheBase projectRootElementCache, bool isExplicitlyLoaded,
            bool preserveFormatting)
        {
            ErrorUtilities.VerifyThrowArgumentNull(xmlReader);
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElementCache);

            IsExplicitlyLoaded = isExplicitlyLoaded;
            ProjectRootElementCache = projectRootElementCache;
            _directory = NativeMethodsShared.GetCurrentDirectory();
            IncrementVersion();

            XmlDocumentWithLocation document = LoadDocument(xmlReader, preserveFormatting);

            ProjectParser.Parse(document, this);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Leaves the project dirty, indicating there are unsaved changes.
        /// </summary>
        private ProjectRootElement(ProjectRootElementCacheBase projectRootElementCache, NewProjectFileOptions projectFileOptions)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElementCache);

            ProjectRootElementCache = projectRootElementCache;
            _directory = NativeMethodsShared.GetCurrentDirectory();
            IncrementVersion();

            var document = new XmlDocumentWithLocation();
            var xrs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };

            var emptyProjectFile = string.Format(EmptyProjectFileContent,
                (projectFileOptions & NewProjectFileOptions.IncludeXmlDeclaration) != 0 ? EmptyProjectFileXmlDeclaration : string.Empty,
                (projectFileOptions & NewProjectFileOptions.IncludeToolsVersion) != 0 ? EmptyProjectFileToolsVersion : string.Empty,
                (projectFileOptions & NewProjectFileOptions.IncludeXmlNamespace) != 0 ? EmptyProjectFileXmlNamespace : string.Empty);

            using (XmlReader xr = XmlReader.Create(new StringReader(emptyProjectFile), xrs))
            {
                document.Load(xr);
            }

            ProjectParser.Parse(document, this);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance over a project with the specified file path.
        /// Assumes path is already normalized.
        /// May throw InvalidProjectFileException.
        /// </summary>
        private ProjectRootElement(
                string path,
                ProjectRootElementCacheBase projectRootElementCache,
                bool preserveFormatting)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path);
            ErrorUtilities.VerifyThrowInternalRooted(path);
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElementCache);
            ProjectRootElementCache = projectRootElementCache;

            IncrementVersion();
            _versionOnDisk = Version;
            _timeLastChangedUtc = DateTime.UtcNow;

            XmlDocumentWithLocation document = LoadDocument(path, preserveFormatting, projectRootElementCache.LoadProjectsReadOnly);

            ProjectParser.Parse(document, this);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an existing document.
        /// May throw InvalidProjectFileException.
        /// Leaves the project dirty, indicating there are unsaved changes.
        /// </summary>
        /// <remarks>
        /// Do not make public: we do not wish to expose particular XML API's.
        /// </remarks>
        private ProjectRootElement(XmlDocumentWithLocation document, ProjectRootElementCacheBase projectRootElementCache)
        {
            ErrorUtilities.VerifyThrowArgumentNull(document);
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElementCache);

            ProjectRootElementCache = projectRootElementCache;
            _directory = NativeMethodsShared.GetCurrentDirectory();
            IncrementVersion();

            ProjectParser.Parse(document, this);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an existing document.
        /// Helper constructor for the <see cref="ReloadFrom(string,bool,bool?)"/>> mehtod which needs to check if the document parses
        /// </summary>
        /// <remarks>
        /// Do not make public: we do not wish to expose particular XML API's.
        /// </remarks>
        private ProjectRootElement(XmlDocumentWithLocation document)
        {
            ProjectParser.Parse(document, this);
        }

        /// <summary>
        /// Event raised after this project is renamed
        /// </summary>
        internal event RenameHandlerDelegate OnAfterProjectRename;

        /// <summary>
        /// Event raised after the project XML is changed.
        /// </summary>
        internal event EventHandler<ProjectXmlChangedEventArgs> OnProjectXmlChanged;

        /// <summary>
        /// Condition should never be set, but the getter returns null instead of throwing
        /// because a nonexistent condition is implicitly true
        /// </summary>
        public override string Condition
        {
            get => null;
            set => ErrorUtilities.ThrowInvalidOperation("OM_CannotGetSetCondition");
        }

        #region ChildEnumerators
        /// <summary>
        /// Get a read-only collection of the child chooses, if any
        /// </summary>
        /// <remarks>
        /// The name is inconsistent to make it more understandable, per API review.
        /// </remarks>
        public ICollection<ProjectChooseElement> ChooseElements => GetChildrenOfType<ProjectChooseElement>();

        /// <summary>
        /// Get a read-only collection of the child item definition groups, if any
        /// </summary>
        public ICollection<ProjectItemDefinitionGroupElement> ItemDefinitionGroups => GetChildrenOfType<ProjectItemDefinitionGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child item definitions, if any, in all item definition groups anywhere in the project file.
        /// </summary>
        public ICollection<ProjectItemDefinitionElement> ItemDefinitions => new ReadOnlyCollection<ProjectItemDefinitionElement>(GetAllChildrenOfType<ProjectItemDefinitionElement>());

        /// <summary>
        /// Get a read-only collection over the child item groups, if any.
        /// Does not include any that may not be at the root, i.e. inside Choose elements.
        /// </summary>
        public ICollection<ProjectItemGroupElement> ItemGroups => GetChildrenOfType<ProjectItemGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child items, if any, in all item groups anywhere in the project file.
        /// Not restricted to root item groups: traverses through Choose elements.
        /// </summary>
        public ICollection<ProjectItemElement> Items => new ReadOnlyCollection<ProjectItemElement>(GetAllChildrenOfType<ProjectItemElement>());

        /// <summary>
        /// Get a read-only collection of the child import groups, if any.
        /// </summary>
        public ICollection<ProjectImportGroupElement> ImportGroups => GetChildrenOfType<ProjectImportGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child imports
        /// </summary>
        public ICollection<ProjectImportElement> Imports => new ReadOnlyCollection<ProjectImportElement>(GetAllChildrenOfType<ProjectImportElement>());

        /// <summary>
        /// Get a read-only collection of the child property groups, if any.
        /// Does not include any that may not be at the root, i.e. inside Choose elements.
        /// </summary>
        public ICollection<ProjectPropertyGroupElement> PropertyGroups => GetChildrenOfType<ProjectPropertyGroupElement>();

        /// <summary>
        /// Geta read-only collection of the child properties, if any, in all property groups anywhere in the project file.
        /// Not restricted to root property groups: traverses through Choose elements.
        /// </summary>
        public ICollection<ProjectPropertyElement> Properties => new ReadOnlyCollection<ProjectPropertyElement>(GetAllChildrenOfType<ProjectPropertyElement>());

        /// <summary>
        /// Get a read-only collection of the child targets
        /// </summary>
        public ICollection<ProjectTargetElement> Targets => GetChildrenOfType<ProjectTargetElement>();

        /// <summary>
        /// Get a read-only collection of the child usingtasks, if any
        /// </summary>
        public ICollection<ProjectUsingTaskElement> UsingTasks => GetChildrenOfType<ProjectUsingTaskElement>();

        /// <summary>
        /// Get a read-only collection of the child item groups, if any, in reverse order
        /// </summary>
        public ICollection<ProjectItemGroupElement> ItemGroupsReversed => GetChildrenReversedOfType<ProjectItemGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child item definition groups, if any, in reverse order
        /// </summary>
        public ICollection<ProjectItemDefinitionGroupElement> ItemDefinitionGroupsReversed => GetChildrenReversedOfType<ProjectItemDefinitionGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child import groups, if any, in reverse order
        /// </summary>
        public ICollection<ProjectImportGroupElement> ImportGroupsReversed => GetChildrenReversedOfType<ProjectImportGroupElement>();

        /// <summary>
        /// Get a read-only collection of the child property groups, if any, in reverse order
        /// </summary>
        public ICollection<ProjectPropertyGroupElement> PropertyGroupsReversed => GetChildrenReversedOfType<ProjectPropertyGroupElement>();

        #endregion

        /// <summary>
        /// The directory that the project is in.
        /// Essential for evaluating relative paths.
        /// Is never null, even if the FullPath does not contain directory information.
        /// If the project has not been loaded from disk and has not been given a path, returns the current-directory from
        /// the time the project was loaded - this is the same behavior as Whidbey/Orcas.
        /// If the project has not been loaded from disk but has been given a path, this path may not exist.
        /// </summary>
        public string DirectoryPath
        {
            get => Link != null ? RootLink.DirectoryPath : _directory ?? String.Empty;
            internal set => _directory = value;
            // Used during solution load to ensure solutions which were created from a file have a location.
        }

        public string EscapedFullPath => _escapedFullPath ?? (_escapedFullPath = ProjectCollection.Escape(FullPath));

        /// <summary>
        /// Full path to the project file.
        /// If the project has not been loaded from disk and has not been given a path, returns null.
        /// If the project has not been loaded from disk but has been given a path, this path may not exist.
        /// Setter renames the project, if it already had a name.
        /// </summary>
        /// <remarks>
        /// Updates the ProjectRootElement cache.
        /// </remarks>
        public string FullPath
        {
            get => Link != null ? RootLink.FullPath : _projectFileLocation?.File;

            set
            {
                ErrorUtilities.VerifyThrowArgumentLength(value);
                if (Link != null)
                {
                    RootLink.FullPath = value;
                    return;
                }

                string oldFullPath = _projectFileLocation?.File;

                // We do not control the current directory at this point, but assume that if we were
                // passed a relative path, the caller assumes we will prepend the current directory.
                string newFullPath = FileUtilities.NormalizePath(value);

                if (String.Equals(oldFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _projectFileLocation = ElementLocation.Create(newFullPath);
                _escapedFullPath = null;
                _directory = Path.GetDirectoryName(newFullPath);

                if (XmlDocument != null)
                {
                    XmlDocument.FullPath = newFullPath;
                }

                if (oldFullPath == null)
                {
                    ProjectRootElementCache.AddEntry(this);
                }
                else
                {
                    ProjectRootElementCache.RenameEntry(oldFullPath, this);
                }

                OnAfterProjectRename?.Invoke(oldFullPath);

                MarkDirty("Set project FullPath to '{0}'", FullPath);
            }
        }

        /// <summary>
        /// Encoding that the project file is saved in, or will be saved in, unless
        /// otherwise specified.
        /// </summary>
        /// <remarks>
        /// Returns the encoding from the Xml declaration if any, otherwise UTF8.
        /// </remarks>
        public Encoding Encoding
        {
            get
            {
                if (Link != null)
                {
                    return RootLink.Encoding;
                }

                // No thread-safety lock required here because many reader threads would set the same value to the field.
                if (_encoding == null)
                {
                    var declaration = XmlDocument.FirstChild as XmlDeclaration;

                    if (declaration?.Encoding.Length > 0)
                    {
                        _encoding = Encoding.GetEncoding(declaration.Encoding);
                    }
                }

                // Ensure we never return null, in case there was no xml declaration that we could find above.
                return _encoding ?? s_defaultEncoding;
            }
        }

        /// <summary>
        /// Gets or sets the value of DefaultTargets. If there is no DefaultTargets, returns empty string.
        /// If the value is null or empty, removes the attribute.
        /// </summary>
        public string DefaultTargets
        {
            [DebuggerStepThrough]
            get => GetAttributeValue(XMakeAttributes.defaultTargets);

            [DebuggerStepThrough]
            set => SetOrRemoveAttribute(XMakeAttributes.defaultTargets, value, "Set Project DefaultTargets to '{0}'", value);
        }

        /// <summary>
        /// Gets or sets the value of InitialTargets. If there is no InitialTargets, returns empty string.
        /// If the value is null or empty, removes the attribute.
        /// </summary>
        public string InitialTargets
        {
            [DebuggerStepThrough]
            get => GetAttributeValue(XMakeAttributes.initialTargets);

            [DebuggerStepThrough]
            set => SetOrRemoveAttribute(XMakeAttributes.initialTargets, value, "Set project InitialTargets to '{0}'", value);
        }

        /// <summary>
        /// Gets or sets a semicolon delimited list of software development kits (SDK) that the project uses.
        /// If  a value is specified, an Sdk.props is simplicity imported at the top of the project and an
        /// Sdk.targets is simplicity imported at the bottom from the specified SDK.
        /// If the value is null or empty, removes the attribute.
        /// </summary>
        public string Sdk
        {
            [DebuggerStepThrough]
            get => GetAttributeValue(XMakeAttributes.sdk);

            [DebuggerStepThrough]
            set => SetOrRemoveAttribute(XMakeAttributes.sdk, value, "Set project Sdk to '{0}'", value);
        }

        /// <summary>
        /// Gets or sets the value of TreatAsLocalProperty. If there is no tag, returns empty string.
        /// If the value being set is null or empty, removes the attribute.
        /// </summary>
        public string TreatAsLocalProperty
        {
            [DebuggerStepThrough]
            get => GetAttributeValue(XMakeAttributes.treatAsLocalProperty);

            [DebuggerStepThrough]
            set => SetOrRemoveAttribute(XMakeAttributes.treatAsLocalProperty, value, "Set project TreatAsLocalProperty to '{0}'", value);
        }

        /// <summary>
        /// Gets or sets the value of ToolsVersion. If there is no ToolsVersion, returns empty string.
        /// If the value is null or empty, removes the attribute.
        /// </summary>
        public string ToolsVersion
        {
            [DebuggerStepThrough]
            get => GetAttributeValue(XMakeAttributes.toolsVersion);

            [DebuggerStepThrough]
            set => SetOrRemoveAttribute(XMakeAttributes.toolsVersion, value, "Set project ToolsVersion {0}", value);
        }

        /// <summary>
        /// Gets the XML representing this project as a string.
        /// Does not remove any dirty flag.
        /// </summary>
        /// <remarks>
        /// Useful for debugging.
        /// Note that we do not expose an XmlDocument or any other specific XML API.
        /// </remarks>
        public string RawXml
        {
            get
            {
                if (Link != null)
                {
                    return RootLink.RawXml;
                }

                using (var stringWriter = new EncodingStringWriter(Encoding))
                {
                    using (var projectWriter = new ProjectWriter(stringWriter))
                    {
                        projectWriter.Initialize(XmlDocument);
                        XmlDocument.Save(projectWriter);
                    }

                    return stringWriter.ToString();
                }
            }
        }

        /// <summary>
        /// Whether the XML has been modified since it was last loaded or saved.
        /// </summary>
        public bool HasUnsavedChanges => Link != null ? RootLink.HasUnsavedChanges : Version != _versionOnDisk;

        /// <summary>
        /// Whether the XML is preserving formatting or not.
        /// </summary>
        public bool PreserveFormatting => Link != null ? RootLink.PreserveFormatting : XmlDocument?.PreserveWhitespace ?? false;

        /// <summary>
        /// Version number of this object.
        /// A host can compare this to a stored version number to determine whether
        /// a project's XML has changed, even if it has also been saved since.
        ///
        /// The actual value is meaningless: an edit may increment it more than once,
        /// so it should only be compared to a stored value.
        /// </summary>
        /// <remarks>
        /// Used by the Project class to figure whether changes have occurred that
        /// it might want to pick up by reevaluation.
        ///
        /// Used by the ProjectRootElement class to determine whether it needs to save.
        ///
        /// This number is unique to the appdomain. That means that it is possible
        /// to know when a ProjectRootElement has been unloaded (perhaps after modification) and
        /// reloaded -- the version won't reset to '0'.
        ///
        /// We're assuming we don't have over 2 billion edits.
        /// </remarks>
        public int Version
        {
            get => Link != null ? RootLink.Version : _version;
            private set => _version = value;
        }

        /// <summary>
        /// The time that this object was last changed. If it hasn't
        /// been changed since being loaded or created, its value is <see cref="DateTime.MinValue"/>.
        /// </summary>
        /// <remarks>
        /// This is used by the VB/C# project system.
        /// </remarks>
        public DateTime TimeLastChanged => Link != null ? RootLink.TimeLastChanged : _timeLastChangedUtc.ToLocalTime();

        /// <summary>
        /// The last-write-time of the file that was read, when it was read.
        /// This can be used to see whether the file has been changed on disk
        /// by an external means.
        /// </summary>
        public DateTime LastWriteTimeWhenRead => Link != null ? RootLink.LastWriteTimeWhenRead : _lastWriteTimeWhenReadUtc.ToLocalTime();

        internal DateTime? StreamTimeUtc = null;

        /// <summary>
        /// This does not allow conditions, so it should not be called.
        /// </summary>
        public override ElementLocation ConditionLocation
        {
            get
            {
                ErrorUtilities.ThrowInternalError("Should not evaluate this");
                return null;
            }
        }

        /// <summary>
        /// Location of the originating file itself, not any specific content within it.
        /// If the file has not been given a name, returns an empty location.
        /// This is a case where it is legitimate to "not have a location".
        /// </summary>
        public ElementLocation ProjectFileLocation => Link != null ? RootLink.ProjectFileLocation : _projectFileLocation ?? ElementLocation.EmptyLocation;

        /// <summary>
        /// Location of the toolsversion attribute, if any
        /// </summary>
        public ElementLocation ToolsVersionLocation => GetAttributeLocation(XMakeAttributes.toolsVersion);

        /// <summary>
        /// Location of the defaulttargets attribute, if any
        /// </summary>
        public ElementLocation DefaultTargetsLocation => GetAttributeLocation(XMakeAttributes.defaultTargets);

        /// <summary>
        /// Location of the initialtargets attribute, if any
        /// </summary>
        public ElementLocation InitialTargetsLocation => GetAttributeLocation(XMakeAttributes.initialTargets);

        /// <summary>
        /// Location of the Sdk attribute, if any
        /// </summary>
        public ElementLocation SdkLocation => GetAttributeLocation(XMakeAttributes.sdk);

        /// <summary>
        /// Location of the TreatAsLocalProperty attribute, if any
        /// </summary>
        public ElementLocation TreatAsLocalPropertyLocation => GetAttributeLocation(XMakeAttributes.treatAsLocalProperty);

        /// <summary>
        /// Has the project root element been explicitly loaded for a build or has it been implicitly loaded
        /// as part of building another project.
        /// </summary>
        /// <remarks>
        /// Internal code that wants to set this to true should call <see cref="MarkAsExplicitlyLoaded"/>.
        /// The setter is private to make it more difficult to downgrade an existing PRE to an implicitly loaded state, which should never happen.
        /// </remarks>
        internal bool IsExplicitlyLoaded { get; private set; }

        /// <summary>
        /// Retrieves the root element cache with which this root element is associated.
        /// </summary>
        internal ProjectRootElementCacheBase ProjectRootElementCache { get; }

        /// <summary>
        /// Gets a value indicating whether this PRE is known by its containing collection.
        /// </summary>
        internal bool IsMemberOfProjectCollection => _projectFileLocation != null;

        /// <summary>
        /// Indicates whether there are any targets in this project
        /// that use the "Returns" attribute.  If so, then this project file
        /// is automatically assumed to be "Returns-enabled", and the default behavior
        /// for targets without Returns attributes changes from using the Outputs to
        /// returning nothing by default.
        /// </summary>
        internal bool ContainsTargetsWithReturnsAttribute { get; set; }

        /// <summary>
        /// Gets the ProjectExtensions child, if any, otherwise null.
        /// </summary>
        /// <remarks>
        /// Not public as we do not wish to encourage the use of ProjectExtensions.
        /// </remarks>
        internal ProjectExtensionsElement ProjectExtensions
            => GetChildrenReversedOfType<ProjectExtensionsElement>().FirstOrDefault();

        /// <summary>
        /// Returns an unlocalized indication of how this file was last dirtied.
        /// This is for debugging purposes only.
        /// String formatting only occurs when retrieved.
        /// </summary>
        internal string LastDirtyReason
            => _dirtyReason == null ? null : String.Format(CultureInfo.InvariantCulture, _dirtyReason, _dirtyParameter);

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the global project collection.
        /// </summary>
        public static ProjectRootElement Create()
        {
            return Create(ProjectCollection.GlobalProjectCollection, Project.DefaultNewProjectTemplateOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later using the specified <see cref="NewProjectFileOptions"/>.
        /// Uses the global project collection.
        /// </summary>
        public static ProjectRootElement Create(NewProjectFileOptions projectFileOptions)
        {
            return Create(ProjectCollection.GlobalProjectCollection, projectFileOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the specified project collection.
        /// </summary>
        public static ProjectRootElement Create(ProjectCollection projectCollection)
        {
            return Create(projectCollection.ProjectRootElementCache);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later using the specified <see cref="ProjectCollection"/> and <see cref="NewProjectFileOptions"/>.
        /// </summary>
        public static ProjectRootElement Create(ProjectCollection projectCollection, NewProjectFileOptions projectFileOptions)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            return Create(projectCollection.ProjectRootElementCache, projectFileOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the global project collection.
        /// </summary>
        public static ProjectRootElement Create(string path)
        {
            return Create(path, ProjectCollection.GlobalProjectCollection, Project.DefaultNewProjectTemplateOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later using the specified path and <see cref="NewProjectFileOptions"/>.
        /// Uses the global project collection.
        /// </summary>
        public static ProjectRootElement Create(string path, NewProjectFileOptions newProjectFileOptions)
        {
            return Create(path, ProjectCollection.GlobalProjectCollection, newProjectFileOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the specified project collection.
        /// </summary>
        public static ProjectRootElement Create(string path, ProjectCollection projectCollection)
        {
            return Create(path, projectCollection, Project.DefaultNewProjectTemplateOptions);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the specified project collection.
        /// </summary>
        public static ProjectRootElement Create(string path, ProjectCollection projectCollection, NewProjectFileOptions newProjectFileOptions)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path);
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            var projectRootElement = new ProjectRootElement(
                projectCollection.ProjectRootElementCache,
                newProjectFileOptions)
            { FullPath = path };

            return projectRootElement;
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an XmlReader.
        /// Uses the global project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        public static ProjectRootElement Create(XmlReader xmlReader)
        {
            return Create(xmlReader, ProjectCollection.GlobalProjectCollection, preserveFormatting: false);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an XmlReader.
        /// Uses the specified project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        public static ProjectRootElement Create(XmlReader xmlReader, ProjectCollection projectCollection)
        {
            return Create(xmlReader, projectCollection, preserveFormatting: false);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an XmlReader.
        /// Uses the specified project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        public static ProjectRootElement Create(XmlReader xmlReader, ProjectCollection projectCollection, bool preserveFormatting)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            return new ProjectRootElement(xmlReader, projectCollection.ProjectRootElementCache, true /*Explicitly loaded*/,
                preserveFormatting);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance by loading from the specified file path.
        /// Uses the global project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        public static ProjectRootElement Open(string path)
        {
            return Open(path, ProjectCollection.GlobalProjectCollection);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance by loading from the specified file path.
        /// Uses the specified project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        public static ProjectRootElement Open(string path, ProjectCollection projectCollection)
        {
            return Open(path, projectCollection,
                preserveFormatting: null);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance by loading from the specified file path.
        /// Uses the specified project collection and preserves the formatting of the document if specified.
        /// </summary>
        public static ProjectRootElement Open(string path, ProjectCollection projectCollection, bool? preserveFormatting)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path);
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            path = FileUtilities.NormalizePath(path);

            return Open(path, projectCollection.ProjectRootElementCache, true /*Is explicitly loaded*/, preserveFormatting);
        }

        /// <summary>
        /// Returns the ProjectRootElement for the given path if it has been loaded, or null if it is not currently in memory.
        /// Uses the global project collection.
        /// </summary>
        /// <param name="path">The path of the ProjectRootElement, cannot be null.</param>
        /// <returns>The loaded ProjectRootElement, or null if it is not currently in memory.</returns>
        /// <remarks>
        /// It is possible for ProjectRootElements to be brought into memory and discarded due to memory pressure. Therefore
        /// this method returning false does not indicate that it has never been loaded, only that it is not currently in memory.
        /// </remarks>
        public static ProjectRootElement TryOpen(string path)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path);

            return TryOpen(path, ProjectCollection.GlobalProjectCollection);
        }

        /// <summary>
        /// Returns the ProjectRootElement for the given path if it has been loaded, or null if it is not currently in memory.
        /// Uses the specified project collection.
        /// </summary>
        /// <param name="path">The path of the ProjectRootElement, cannot be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> to load the project into.</param>
        /// <returns>The loaded ProjectRootElement, or null if it is not currently in memory.</returns>
        /// <remarks>
        /// It is possible for ProjectRootElements to be brought into memory and discarded due to memory pressure. Therefore
        /// this method returning false does not indicate that it has never been loaded, only that it is not currently in memory.
        /// </remarks>
        public static ProjectRootElement TryOpen(string path, ProjectCollection projectCollection)
        {
            return TryOpen(path, projectCollection, preserveFormatting: null);
        }

        /// <summary>
        /// Returns the ProjectRootElement for the given path if it has been loaded, or null if it is not currently in memory.
        /// Uses the specified project collection.
        /// </summary>
        /// <param name="path">The path of the ProjectRootElement, cannot be null.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> to load the project into.</param>
        /// <param name="preserveFormatting">
        /// The formatting to open with. Must match the formatting in the collection to succeed.
        /// </param>
        /// <returns>The loaded ProjectRootElement, or null if it is not currently in memory.</returns>
        /// <remarks>
        /// It is possible for ProjectRootElements to be brought into memory and discarded due to memory pressure. Therefore
        /// this method returning false does not indicate that it has never been loaded, only that it is not currently in memory.
        /// </remarks>
        public static ProjectRootElement TryOpen(string path, ProjectCollection projectCollection, bool? preserveFormatting)
        {
            ErrorUtilities.VerifyThrowArgumentLength(path);
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection);

            path = FileUtilities.NormalizePath(path);

            ProjectRootElement projectRootElement = projectCollection.ProjectRootElementCache.TryGet(path, preserveFormatting);

            return projectRootElement;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// If import groups exist, inserts into the last one without a condition on it.
        /// Otherwise, creates an import at the end of the project.
        /// </summary>
        public ProjectImportElement AddImport(string project)
        {
            ErrorUtilities.VerifyThrowArgumentLength(project);

            ProjectImportGroupElement importGroupToAddTo =
                ImportGroupsReversed.FirstOrDefault(importGroup => importGroup.Condition.Length <= 0);

            ProjectImportElement import;

            if (importGroupToAddTo != null)
            {
                import = importGroupToAddTo.AddImport(project);
            }
            else
            {
                import = CreateImportElement(project);
                AppendChild(import);
            }

            return import;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Creates an import group at the end of the project.
        /// </summary>
        public ProjectImportGroupElement AddImportGroup()
        {
            ProjectImportGroupElement importGroup = CreateImportGroupElement();
            AppendChild(importGroup);

            return importGroup;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Finds item group with no condition with at least one item of same type, or else adds a new item group;
        /// adds the item to that item group with items of the same type, ordered by include.
        /// </summary>
        /// <remarks>
        /// Per the previous implementation, it actually finds the last suitable item group, not the first.
        /// </remarks>
        public ProjectItemElement AddItem(string itemType, string include)
        {
            return AddItem(itemType, include, null);
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Finds first item group with no condition with at least one item of same type, or else an empty item group; or else adds a new item group;
        /// adds the item to that item group with items of the same type, ordered by include.
        /// Does not attempt to check whether the item matches an existing wildcard expression; that is only possible
        /// in the evaluated world.
        /// </summary>
        /// <remarks>
        /// Per the previous implementation, it actually finds the last suitable item group, not the first.
        /// </remarks>
        public ProjectItemElement AddItem(string itemType, string include, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            ErrorUtilities.VerifyThrowArgumentLength(itemType);
            ErrorUtilities.VerifyThrowArgumentLength(include);

            ProjectItemGroupElement itemGroupToAddTo = null;

            foreach (ProjectItemGroupElement itemGroup in ItemGroups)
            {
                if (itemGroup.Condition.Length > 0)
                {
                    continue;
                }

                if (itemGroupToAddTo == null && itemGroup.Count == 0)
                {
                    itemGroupToAddTo = itemGroup;
                }

                if (itemGroup.Items.Any(item => MSBuildNameIgnoreCaseComparer.Default.Equals(itemType, item.ItemType)))
                {
                    itemGroupToAddTo = itemGroup;
                }

                if (itemGroupToAddTo?.Count > 0)
                {
                    break;
                }
            }

            if (itemGroupToAddTo == null)
            {
                itemGroupToAddTo = AddItemGroup();
            }

            ProjectItemElement newItem = itemGroupToAddTo.AddItem(itemType, include, metadata);

            return newItem;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Adds an item group after the last existing item group, if any; otherwise
        /// adds an item group after the last existing property group, if any; otherwise
        /// adds a new item group at the end of the project.
        /// </summary>
        public ProjectItemGroupElement AddItemGroup()
        {
            ProjectElement reference = ItemGroupsReversed.FirstOrDefault();

            if (reference == null)
            {
                foreach (ProjectPropertyGroupElement propertyGroup in PropertyGroupsReversed)
                {
                    reference = propertyGroup;
                    break;
                }
            }

            ProjectItemGroupElement newItemGroup = CreateItemGroupElement();

            if (reference == null)
            {
                AppendChild(newItemGroup);
            }
            else
            {
                InsertAfterChild(newItemGroup, reference);
            }

            return newItemGroup;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Finds first item definition group with no condition with at least one item definition of same item type, or else adds a new item definition group.
        /// </summary>
        public ProjectItemDefinitionElement AddItemDefinition(string itemType)
        {
            ErrorUtilities.VerifyThrowArgumentLength(itemType);

            ProjectItemDefinitionGroupElement itemDefinitionGroupToAddTo = null;

            foreach (ProjectItemDefinitionGroupElement itemDefinitionGroup in ItemDefinitionGroups)
            {
                if (itemDefinitionGroup.Condition.Length > 0)
                {
                    continue;
                }

                foreach (ProjectItemDefinitionElement itemDefinition in itemDefinitionGroup.ItemDefinitions)
                {
                    if (MSBuildNameIgnoreCaseComparer.Default.Equals(itemType, itemDefinition.ItemType))
                    {
                        itemDefinitionGroupToAddTo = itemDefinitionGroup;
                        break;
                    }
                }

                if (itemDefinitionGroupToAddTo != null)
                {
                    break;
                }
            }

            if (itemDefinitionGroupToAddTo == null)
            {
                itemDefinitionGroupToAddTo = AddItemDefinitionGroup();
            }

            ProjectItemDefinitionElement newItemDefinition = CreateItemDefinitionElement(itemType);

            itemDefinitionGroupToAddTo.AppendChild(newItemDefinition);

            return newItemDefinition;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Adds an item definition group after the last existing item definition group, if any; otherwise
        /// adds an item definition group after the last existing property group, if any; otherwise
        /// adds a new item definition group at the end of the project.
        /// </summary>
        public ProjectItemDefinitionGroupElement AddItemDefinitionGroup()
        {
            ProjectElement reference = null;

            foreach (ProjectItemDefinitionGroupElement itemDefinitionGroup in ItemDefinitionGroupsReversed)
            {
                reference = itemDefinitionGroup;
                break;
            }

            if (reference == null)
            {
                foreach (ProjectPropertyGroupElement propertyGroup in PropertyGroupsReversed)
                {
                    reference = propertyGroup;
                    break;
                }
            }

            ProjectItemDefinitionGroupElement newItemDefinitionGroup = CreateItemDefinitionGroupElement();

            InsertAfterChild(newItemDefinitionGroup, reference);

            return newItemDefinitionGroup;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Adds a new property group after the last existing property group, if any; otherwise
        /// at the start of the project.
        /// </summary>
        public ProjectPropertyGroupElement AddPropertyGroup()
        {
            ProjectPropertyGroupElement reference = null;

            foreach (ProjectPropertyGroupElement propertyGroup in PropertyGroupsReversed)
            {
                reference = propertyGroup;
                break;
            }

            ProjectPropertyGroupElement newPropertyGroup = CreatePropertyGroupElement();

            InsertAfterChild(newPropertyGroup, reference);

            return newPropertyGroup;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic.
        /// Updates the last existing property with the specified name that has no condition on itself or its property group, if any.
        /// Otherwise, adds a new property in the first property group without a condition, creating a property group if necessary after
        /// the last existing property group, else at the start of the project.
        /// </summary>
        public ProjectPropertyElement AddProperty(string name, string value)
        {
            ProjectPropertyGroupElement matchingPropertyGroup = null;
            ProjectPropertyElement matchingProperty = null;

            foreach (ProjectPropertyGroupElement propertyGroup in PropertyGroups)
            {
                if (propertyGroup.Condition.Length > 0)
                {
                    continue;
                }

                if (matchingPropertyGroup == null)
                {
                    matchingPropertyGroup = propertyGroup;
                }

                foreach (ProjectPropertyElement property in propertyGroup.Properties)
                {
                    if (property.Condition.Length > 0)
                    {
                        continue;
                    }

                    if (MSBuildNameIgnoreCaseComparer.Default.Equals(property.Name, name))
                    {
                        matchingProperty = property;
                    }
                }
            }

            if (matchingProperty != null)
            {
                matchingProperty.Value = value;

                return matchingProperty;
            }

            if (matchingPropertyGroup == null)
            {
                matchingPropertyGroup = AddPropertyGroup();
            }

            ProjectPropertyElement newProperty = matchingPropertyGroup.AddProperty(name, value);

            return newProperty;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Creates a target at the end of the project.
        /// </summary>
        public ProjectTargetElement AddTarget(string name)
        {
            ProjectTargetElement target = CreateTargetElement(name);
            AppendChild(target);

            return target;
        }

        /// <summary>
        /// Convenience method that picks a location based on a heuristic:
        /// Creates a usingtask at the end of the project.
        /// Exactly one of assemblyName or assemblyFile must be null.
        /// </summary>
        public ProjectUsingTaskElement AddUsingTask(string name, string assemblyFile, string assemblyName)
        {
            ProjectUsingTaskElement usingTask = CreateUsingTaskElement(name, FileUtilities.FixFilePath(assemblyFile), assemblyName);
            AppendChild(usingTask);

            return usingTask;
        }

        /// <summary>
        /// Creates a choose.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectChooseElement CreateChooseElement()
        {
            return Link != null ? RootLink.CreateChooseElement() : ProjectChooseElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates an import.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectImportElement CreateImportElement(string project)
        {
            return Link != null ? RootLink.CreateImportElement(project) : ProjectImportElement.CreateDisconnected(project, this);
        }

        /// <summary>
        /// Creates an item node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectItemElement CreateItemElement(string itemType)
        {
            return Link != null ? RootLink.CreateItemElement(itemType) : ProjectItemElement.CreateDisconnected(itemType, this);
        }

        /// <summary>
        /// Creates an item node with an include.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectItemElement CreateItemElement(string itemType, string include)
        {
            if (Link != null)
            {
                return RootLink.CreateItemElement(itemType, include);
            }

            ProjectItemElement item = ProjectItemElement.CreateDisconnected(itemType, this);

            item.Include = include;

            return item;
        }

        /// <summary>
        /// Creates an item definition.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectItemDefinitionElement CreateItemDefinitionElement(string itemType)
        {
            return Link != null ? RootLink.CreateItemDefinitionElement(itemType) : ProjectItemDefinitionElement.CreateDisconnected(itemType, this);
        }

        /// <summary>
        /// Creates an item definition group.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectItemDefinitionGroupElement CreateItemDefinitionGroupElement()
        {
            return Link != null ? RootLink.CreateItemDefinitionGroupElement() : ProjectItemDefinitionGroupElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates an item group.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectItemGroupElement CreateItemGroupElement()
        {
            return Link != null ? RootLink.CreateItemGroupElement() : ProjectItemGroupElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates an import group.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectImportGroupElement CreateImportGroupElement()
        {
            return Link != null ? RootLink.CreateImportGroupElement() : ProjectImportGroupElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates a metadata node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectMetadataElement CreateMetadataElement(string name)
        {
            return Link != null ? RootLink.CreateMetadataElement(name) : ProjectMetadataElement.CreateDisconnected(name, this);
        }

        /// <summary>
        /// Creates a metadata node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectMetadataElement CreateMetadataElement(string name, string unevaluatedValue)
        {
            return this.CreateMetadataElement(name, unevaluatedValue, null);
        }

        /// <summary>
        /// Creates a metadata node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectMetadataElement CreateMetadataElement(string name, string unevaluatedValue, ElementLocation location)
        {
            if (Link != null)
            {
                return RootLink.CreateMetadataElement(name, unevaluatedValue);
            }

            ProjectMetadataElement metadatum = ProjectMetadataElement.CreateDisconnected(name, this, location);

            metadatum.Value = unevaluatedValue;

            return metadatum;
        }

        /// <summary>
        /// Creates an on error node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectOnErrorElement CreateOnErrorElement(string executeTargets)
        {
            return Link != null ? RootLink.CreateOnErrorElement(executeTargets) : ProjectOnErrorElement.CreateDisconnected(executeTargets, this);
        }

        /// <summary>
        /// Creates an otherwise node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectOtherwiseElement CreateOtherwiseElement()
        {
            return Link != null ? RootLink.CreateOtherwiseElement() : ProjectOtherwiseElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates an output node.
        /// Exactly one of itemType and propertyName must be specified.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectOutputElement CreateOutputElement(string taskParameter, string itemType, string propertyName)
        {
            return Link != null ? RootLink.CreateOutputElement(taskParameter, itemType, propertyName) : ProjectOutputElement.CreateDisconnected(taskParameter, itemType, propertyName, this);
        }

        /// <summary>
        /// Creates a project extensions node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectExtensionsElement CreateProjectExtensionsElement()
        {
            return Link != null ? RootLink.CreateProjectExtensionsElement() : ProjectExtensionsElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates a property group.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectPropertyGroupElement CreatePropertyGroupElement()
        {
            return Link != null ? RootLink.CreatePropertyGroupElement() : ProjectPropertyGroupElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates a property.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        public ProjectPropertyElement CreatePropertyElement(string name)
        {
            return Link != null ? RootLink.CreatePropertyElement(name) : ProjectPropertyElement.CreateDisconnected(name, this);
        }

        /// <summary>
        /// Creates a target.
        /// Caller must add it to the location of choice in this project.
        /// </summary>
        public ProjectTargetElement CreateTargetElement(string name)
        {
            return Link != null ? RootLink.CreateTargetElement(name) : ProjectTargetElement.CreateDisconnected(name, this);
        }

        /// <summary>
        /// Creates a task.
        /// Caller must add it to the location of choice in this project.
        /// </summary>
        public ProjectTaskElement CreateTaskElement(string name)
        {
            return Link != null ? RootLink.CreateTaskElement(name) : ProjectTaskElement.CreateDisconnected(name, this);
        }

        /// <summary>
        /// Creates a using task.
        /// Caller must add it to the location of choice in the project.
        /// Exactly one of assembly file and assembly name must be provided.
        /// </summary>
        public ProjectUsingTaskElement CreateUsingTaskElement(string taskName, string assemblyFile, string assemblyName)
        {
            return CreateUsingTaskElement(taskName, assemblyFile, assemblyName, null, null);
        }

        /// <summary>
        /// Creates a using task.
        /// Caller must add it to the location of choice in the project.
        /// Exactly one of assembly file and assembly name must be provided.
        /// Also allows providing optional runtime and architecture specifiers.  Null is OK.
        /// </summary>
        public ProjectUsingTaskElement CreateUsingTaskElement(string taskName, string assemblyFile, string assemblyName, string runtime, string architecture)
        {
            return Link != null ? RootLink.CreateUsingTaskElement(taskName, assemblyFile, assemblyName, runtime, architecture) : ProjectUsingTaskElement.CreateDisconnected(taskName, assemblyFile, assemblyName, runtime, architecture, this);
        }

        /// <summary>
        /// Creates a ParameterGroup for use in a using task.
        /// Caller must add it to the location of choice in the project under a using task.
        /// </summary>
        public UsingTaskParameterGroupElement CreateUsingTaskParameterGroupElement()
        {
            return Link != null ? RootLink.CreateUsingTaskParameterGroupElement() : UsingTaskParameterGroupElement.CreateDisconnected(this);
        }

        /// <summary>
        /// Creates a Parameter for use in a using ParameterGroup.
        /// Caller must add it to the location of choice in the project under a using task.
        /// </summary>
        public ProjectUsingTaskParameterElement CreateUsingTaskParameterElement(string name, string output, string required, string parameterType)
        {
            return Link != null ? RootLink.CreateUsingTaskParameterElement(name, output, required, parameterType) : ProjectUsingTaskParameterElement.CreateDisconnected(name, output, required, parameterType, this);
        }

        /// <summary>
        /// Creates a Task element for use in a using task.
        /// Caller must add it to the location of choice in the project under a using task.
        /// </summary>
        public ProjectUsingTaskBodyElement CreateUsingTaskBodyElement(string evaluate, string body)
        {
            return Link != null ? RootLink.CreateUsingTaskBodyElement(evaluate, body) : ProjectUsingTaskBodyElement.CreateDisconnected(evaluate, body, this);
        }

        /// <summary>
        /// Creates a when.
        /// Caller must add it to the location of choice in this project.
        /// </summary>
        public ProjectWhenElement CreateWhenElement(string condition)
        {
            return Link != null ? RootLink.CreateWhenElement(condition) : ProjectWhenElement.CreateDisconnected(condition, this);
        }

        /// <summary>
        /// Creates a project SDK element attached to this project.
        /// </summary>
        public ProjectSdkElement CreateProjectSdkElement(string sdkName, string sdkVersion)
        {
            return Link != null ? RootLink.CreateProjectSdkElement(sdkName, sdkVersion) : ProjectSdkElement.CreateDisconnected(sdkName, sdkVersion, this);
        }

        /// <summary>
        /// Save the project to the file system, if dirty.
        /// Uses the Encoding returned by the Encoding property.
        /// Creates any necessary directories.
        /// May throw IO-related exceptions.
        /// Clears the dirty flag.
        /// </summary>
        public void Save()
        {
            Save(Encoding);
        }

        /// <summary>
        /// Save the project to the file system, if dirty.
        /// Creates any necessary directories.
        /// May throw IO-related exceptions.
        /// Clears the dirty flag.
        /// </summary>
        public void Save(Encoding saveEncoding)
        {
            if (Link != null)
            {
                RootLink.Save(saveEncoding);
                return;
            }

            ErrorUtilities.VerifyThrowInvalidOperation(_projectFileLocation != null, "OM_MustSetFileNameBeforeSave");

            Directory.CreateDirectory(DirectoryPath);

            // LocationString is normally cheap to calculate, but it can occasionally go down a rabbit hole of method calls. This makes it more consistent if this event is not enabled.
            if (MSBuildEventSource.Log.IsEnabled())
            {
                MSBuildEventSource.Log.SaveStart(_projectFileLocation.LocationString);
            }
            // Note: We're using string Equals on encoding and not EncodingUtilities.SimilarToEncoding in order
            // to force a save if the Encoding changed from UTF8 with BOM to UTF8 w/o BOM (for example).
            if (HasUnsavedChanges || !Equals(saveEncoding, Encoding))
            {
                using (var projectWriter = new ProjectWriter(_projectFileLocation.File, saveEncoding))
                {
                    projectWriter.Initialize(XmlDocument);
                    XmlDocument.Save(projectWriter);
                }

                _encoding = saveEncoding;

                FileInfo fileInfo = FileUtilities.GetFileInfoNoThrow(_projectFileLocation.File);

                // If the file was deleted by a race with someone else immediately after it was written above
                // then we obviously can't read the write time. In this obscure case, we'll retain the
                // older last write time, which at worst would cause the next load to unnecessarily
                // come from disk.
                if (fileInfo != null)
                {
                    _lastWriteTimeWhenReadUtc = fileInfo.LastWriteTimeUtc;
                    if (_lastWriteTimeWhenReadUtc > StreamTimeUtc)
                    {
                        StreamTimeUtc = null;
                    }
                }

                _versionOnDisk = Version;
            }
            if (MSBuildEventSource.Log.IsEnabled())
            {
                MSBuildEventSource.Log.SaveStop(_projectFileLocation.LocationString);
            }
        }

        /// <summary>
        /// Save the project to the file system, if dirty or the path is different.
        /// Creates any necessary directories.
        /// May throw IO related exceptions.
        /// Clears the Dirty flag.
        /// </summary>
        public void Save(string path)
        {
            Save(path, Encoding);
        }

        /// <summary>
        /// Save the project to the file system, if dirty or the path is different.
        /// Creates any necessary directories.
        /// May throw IO related exceptions.
        /// Clears the Dirty flag.
        /// </summary>
        public void Save(string path, Encoding encoding)
        {
            FullPath = path;

            Save(encoding);
        }

        /// <summary>
        /// Save the project to the provided TextWriter, whether or not it is dirty.
        /// Uses the encoding of the TextWriter.
        /// Clears the Dirty flag.
        /// </summary>
        public void Save(TextWriter writer)
        {
            if (Link != null)
            {
                RootLink.Save(writer);
                return;
            }

            using (var projectWriter = new ProjectWriter(writer))
            {
                projectWriter.Initialize(XmlDocument);
                XmlDocument.Save(projectWriter);
            }

            StreamTimeUtc = DateTime.UtcNow;
            _versionOnDisk = Version;
        }

        /// <summary>
        /// Returns a clone of this project.
        /// </summary>
        /// <returns>The cloned element.</returns>
        public ProjectRootElement DeepClone()
        {
            return (ProjectRootElement)DeepClone(this, null);
        }

        /// <summary>
        /// Reload the existing project root element from its file.
        /// An <see cref="InvalidOperationException"/> is thrown if the project root element is not associated with any file on disk.
        ///
        /// See <see cref="ProjectRootElement.ReloadFrom(XmlReader, bool, bool?)"/>
        /// </summary>
        public void Reload(bool throwIfUnsavedChanges = true, bool? preserveFormatting = null)
        {
            ErrorUtilities.VerifyThrowInvalidOperation(!string.IsNullOrEmpty(FullPath), "ValueNotSet", $"{nameof(ProjectRootElement)}.{nameof(FullPath)}");

            ReloadFrom(FullPath, throwIfUnsavedChanges, preserveFormatting);
        }

        /// <summary>
        /// Reload the existing project root element from the given path
        /// An <see cref="InvalidOperationException"/> is thrown if the path does not exist.
        ///
        /// See <see cref="ProjectRootElement.ReloadFrom(XmlReader, bool, bool?)"/>
        /// </summary>
        public void ReloadFrom(string path, bool throwIfUnsavedChanges = true, bool? preserveFormatting = null)
        {
            ErrorUtilities.VerifyThrowInvalidOperation(FileSystems.Default.FileExists(path), "FileToReloadFromDoesNotExist", path);

            if (Link != null)
            {
                RootLink.ReloadFrom(path, throwIfUnsavedChanges, preserveFormatting ?? PreserveFormatting);
                return;
            }

            XmlDocumentWithLocation DocumentProducer(bool shouldPreserveFormatting) => LoadDocument(path, shouldPreserveFormatting, ProjectRootElementCache.LoadProjectsReadOnly);
            ReloadFrom(DocumentProducer, throwIfUnsavedChanges, preserveFormatting);
        }

        /// <summary>
        /// Reload the existing project root element from the given <paramref name="reader"/>
        /// A reload operation completely replaces the state of this <see cref="ProjectRootElement"/> object. This operation marks the
        /// object as dirty.
        ///
        /// If the new state has invalid XML or MSBuild syntax, then this method throws an <see cref="InvalidProjectFileException"/>.
        /// When this happens, the state of this object does not change.
        ///
        /// Reloading from an XMLReader will retain the previous root element location (<see cref="FullPath"/>, <see cref="DirectoryPath"/>, <see cref="ProjectFileLocation"/>).
        ///
        /// </summary>
        /// <param name="reader">Reader to read from</param>
        /// <param name="throwIfUnsavedChanges">
        ///   If set to false, the reload operation will discard any unsaved changes.
        ///   Otherwise, an <see cref="InvalidOperationException"/> is thrown when unsaved changes are present.
        /// </param>
        /// <param name="preserveFormatting">
        ///   Whether the reload should preserve formatting or not. A null value causes the reload to reuse the existing <see cref="PreserveFormatting"/> value.
        /// </param>
        public void ReloadFrom(XmlReader reader, bool throwIfUnsavedChanges = true, bool? preserveFormatting = null)
        {
            if (Link != null)
            {
                RootLink.ReloadFrom(reader, throwIfUnsavedChanges, preserveFormatting ?? PreserveFormatting);
                return;
            }

            XmlDocumentWithLocation DocumentProducer(bool shouldPreserveFormatting)
            {
                var document = LoadDocument(reader, shouldPreserveFormatting);

                document.FullPath = FullPath;

                return document;
            }

            ReloadFrom(DocumentProducer, throwIfUnsavedChanges, preserveFormatting);
        }

        private void ReloadFrom(Func<bool, XmlDocumentWithLocation> documentProducer, bool throwIfUnsavedChanges, bool? preserveFormatting)
        {
            ThrowIfUnsavedChanges(throwIfUnsavedChanges);

            var oldDocument = XmlDocument;
            XmlDocumentWithLocation newDocument = documentProducer(preserveFormatting ?? PreserveFormatting);

            // Reload should only mutate the state if there are no parse errors.
            ThrowIfDocumentHasParsingErrors(newDocument);

            RemoveAllChildren();

            ProjectParser.Parse(newDocument, this);

            MarkDirty("Project reloaded", null);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void ThrowIfDocumentHasParsingErrors(XmlDocumentWithLocation document)
        {
            // todo: rather than throw away, copy over the parse results
            var throwaway = new ProjectRootElement(document);
        }

        /// <summary>
        /// Initialize an in-memory, empty ProjectRootElement instance that can be saved later.
        /// Uses the specified project root element cache.
        /// </summary>
        internal static ProjectRootElement Create(ProjectRootElementCacheBase projectRootElementCache)
        {
            return new ProjectRootElement(projectRootElementCache, Project.DefaultNewProjectTemplateOptions);
        }

        internal static ProjectRootElement Create(ProjectRootElementCacheBase projectRootElementCache, NewProjectFileOptions projectFileOptions)
        {
            return new ProjectRootElement(projectRootElementCache, projectFileOptions);
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance by loading from the specified file path.
        /// Assumes path is already normalized.
        /// Uses the specified project root element cache.
        /// May throw InvalidProjectFileException.
        /// </summary>
        internal static ProjectRootElement Open(string path, ProjectRootElementCacheBase projectRootElementCache, bool isExplicitlyLoaded,
            bool? preserveFormatting)
        {
            ErrorUtilities.VerifyThrowInternalRooted(path);

            ProjectRootElement projectRootElement = projectRootElementCache.Get(path,
                preserveFormatting ?? false ? s_openLoaderPreserveFormattingDelegate : s_openLoaderDelegate,
                isExplicitlyLoaded, preserveFormatting);

            return projectRootElement;
        }

        /// <summary>
        /// Initialize a ProjectRootElement instance from an existing document.
        /// Uses the global project collection.
        /// May throw InvalidProjectFileException.
        /// </summary>
        /// <remarks>
        /// This is ultimately for unit testing.
        /// Do not make public: we do not wish to expose particular XML API's.
        /// </remarks>
        internal static ProjectRootElement Open(XmlDocumentWithLocation document)
        {
            ErrorUtilities.VerifyThrow(document.FullPath == null, "Only virtual documents supported");

            return new ProjectRootElement(document, ProjectCollection.GlobalProjectCollection.ProjectRootElementCache);
        }

        /// <summary>
        /// Gets a ProjectRootElement representing an MSBuild file.
        /// Path provided must be a canonicalized full path.
        /// May throw InvalidProjectFileException or an IO-related exception.
        /// </summary>
        internal static ProjectRootElement OpenProjectOrSolution(string fullPath, IDictionary<string, string> globalProperties, string toolsVersion, ProjectRootElementCacheBase projectRootElementCache, bool isExplicitlyLoaded)
        {
            ErrorUtilities.VerifyThrowInternalRooted(fullPath);

            ProjectRootElement projectRootElement = projectRootElementCache.Get(
                fullPath,
                (path, cache) => CreateProjectFromPath(path, cache, preserveFormatting: false),
                isExplicitlyLoaded,
                // don't care about formatting, reuse whatever is there
                preserveFormatting: null);

            return projectRootElement;
        }

        /// <summary>
        /// Creates a metadata node.
        /// Caller must add it to the location of choice in the project.
        /// </summary>
        internal ProjectMetadataElement CreateMetadataElement(XmlAttributeWithLocation attribute)
        {
            return CreateMetadataElement(attribute.Name, attribute.Value, attribute.Location);
        }

        /// <summary>
        /// Creates a XmlElement with the specified name in the document
        /// containing this project.
        /// </summary>
        internal XmlElementWithLocation CreateElement(string name, ElementLocation location = null)
        {
            ErrorUtilities.VerifyThrow(Link == null, "Attempt to edit a document that is not backed by a local xml is disallowed.");
            return (XmlElementWithLocation)XmlDocument.CreateElement(name, XmlNamespace, location);
        }

        /// <summary>
        /// Overridden to verify that the potential parent and siblings
        /// are acceptable. Throws InvalidOperationException if they are not.
        /// </summary>
        internal override void VerifyThrowInvalidOperationAcceptableLocation(ProjectElementContainer parent, ProjectElement previousSibling, ProjectElement nextSibling)
        {
            ErrorUtilities.ThrowInvalidOperation("OM_CannotAcceptParent");
        }

        /// <summary>
        /// Marks this project as dirty.
        /// Typically called by child elements to indicate that they themselves have become dirty.
        /// Accepts a reason for debugging purposes only, and optional reason parameter.
        /// </summary>
        /// <remarks>
        /// This is sealed because it is virtual and called in a constructor; by sealing it we
        /// satisfy the rule that nobody will override it to do something that would rely on
        /// unconstructed state.
        /// Should be protected+internal.
        /// </remarks>
        internal sealed override void MarkDirty(string reason, string param)
        {
            if (Link != null)
            {
                RootLink.MarkDirty(reason, param);
                return;
            }

            IncrementVersion();

            _dirtyReason = reason;
            _dirtyParameter = param;

            _timeLastChangedUtc = DateTime.UtcNow;

            var changedEventArgs = new ProjectXmlChangedEventArgs(this, reason, param);
            EventHandler<ProjectXmlChangedEventArgs> projectXmlChanged = OnProjectXmlChanged;
            projectXmlChanged?.Invoke(this, changedEventArgs);

            // Only bubble this event up if the cache knows about this PRE.
            if (IsMemberOfProjectCollection)
            {
                ProjectRootElementCache.OnProjectRootElementDirtied(this, changedEventArgs);
            }
        }

        /// <summary>
        /// Bubbles a Project dirty notification up to the ProjectRootElementCacheBase and ultimately to the ProjectCollection.
        /// </summary>
        /// <param name="project">The dirtied project.</param>
        internal void MarkProjectDirty(Project project)
        {
            ErrorUtilities.VerifyThrowArgumentNull(project);
            ErrorUtilities.VerifyThrow(Link == null, "Attempt to edit a document that is not backed by a local xml is disallowed.");

            // Only bubble this event up if the cache knows about this PRE, which is equivalent to
            // whether this PRE has a path.
            if (_projectFileLocation != null)
            {
                ProjectRootElementCache.OnProjectDirtied(project, new ProjectChangedEventArgs(project));
            }
        }

        /// <summary>
        /// Sets the <see cref="IsExplicitlyLoaded"/> property to <c>true</c> to indicate that this PRE
        /// should not be removed from the cache until it is explicitly unloaded by some MSBuild client.
        /// </summary>
        internal void MarkAsExplicitlyLoaded()
        {
            IsExplicitlyLoaded = true;
        }

        /// <summary>
        /// Creates and returns a list of <see cref="ProjectImportElement"/> nodes which are implicitly
        /// referenced by the Project.
        /// </summary>
        /// <param name="currentProjectOrImport">Current project</param>
        /// <returns>An <see cref="IEnumerable{SdkReference}"/> containing details of the SDKs referenced by the project.</returns>
        internal List<ProjectImportElement> GetImplicitImportNodes(ProjectRootElement currentProjectOrImport)
        {
            var nodes = new List<ProjectImportElement>();

            string sdkAttribute = Sdk;
            if (!string.IsNullOrWhiteSpace(sdkAttribute))
            {
                foreach (var referencedSdk in ParseSdks(sdkAttribute, SdkLocation))
                {
                    nodes.Add(ProjectImportElement.CreateImplicit("Sdk.props", currentProjectOrImport, ImplicitImportLocation.Top, referencedSdk, this));
                    nodes.Add(ProjectImportElement.CreateImplicit("Sdk.targets", currentProjectOrImport, ImplicitImportLocation.Bottom, referencedSdk, this));
                }
            }

            foreach (ProjectElement child in ChildrenEnumerable)
            {
                if (child is ProjectSdkElement sdkNode)
                {
                    var referencedSdk = new SdkReference(
                        sdkNode.XmlElement.GetAttribute("Name"),
                        sdkNode.XmlElement.GetAttribute("Version"),
                        sdkNode.XmlElement.GetAttribute("MinimumVersion"));

                    nodes.Add(ProjectImportElement.CreateImplicit("Sdk.props", currentProjectOrImport, ImplicitImportLocation.Top, referencedSdk, sdkNode));
                    nodes.Add(ProjectImportElement.CreateImplicit("Sdk.targets", currentProjectOrImport, ImplicitImportLocation.Bottom, referencedSdk, sdkNode));
                }
            }

            return nodes;
        }

        private static IEnumerable<SdkReference> ParseSdks(string sdks, IElementLocation sdkLocation)
        {
            foreach (string sdk in sdks.Split(MSBuildConstants.SemicolonChar).Select(i => i.Trim()))
            {
                if (!SdkReference.TryParse(sdk, out SdkReference sdkReference))
                {
                    ProjectErrorUtilities.ThrowInvalidProject(sdkLocation, "InvalidSdkFormat", sdks);
                }

                yield return sdkReference;
            }
        }

        /// <summary>
        /// Determines if the specified file is an empty XML file meaning it has no contents, contains only whitespace, or
        /// only an XML declaration.  If the file does not exist, it is not considered empty.
        /// </summary>
        /// <param name="path">The full path to a file to check.</param>
        /// <returns><code>true</code> if the file is an empty XML file, otherwise <code>false</code>.</returns>
        internal static bool IsEmptyXmlFile(string path)
        {
            // The maximum number of characters of the file to read to check if its empty or not.  Ideally we
            // would only look at zero-length files but empty XML files can contain just an xml declaration:
            //
            //   <? xml version="1.0" encoding="utf-8" standalone="yes" ?>
            //
            // And this should also be treated as if the file is empty.
            //
            const int maxSizeToConsiderEmpty = 100;

            if (!FileSystems.Default.FileExists(path))
            {
                // Non-existent files are not treated as empty
                //
                return false;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(path);

                if (fileInfo.Length == 0)
                {
                    // Zero length files are empty
                    //
                    return true;
                }

                if (fileInfo.Length > maxSizeToConsiderEmpty)
                {
                    // Files greater than the maximum bytes to check are not empty
                    //
                    return false;
                }

                string contents = File.ReadAllText(path);

                // If the file is only whitespace or the XML declaration then it empty
                //
                return String.IsNullOrEmpty(contents) || XmlDeclarationRegEx.IsMatch(contents);
            }
            catch (Exception)
            {
                // ignored
            }

            return false;
        }

        /// <summary>
        /// Returns a new instance of ProjectRootElement that is affiliated with the same ProjectRootElementCache.
        /// </summary>
        /// <param name="owner">The factory to use for creating the new instance.</param>
        protected override ProjectElement CreateNewInstance(ProjectRootElement owner)
        {
            return Link != null ? Link.CreateNewInstance(owner) : Create(owner.ProjectRootElementCache);
        }

        /// <summary>
        /// Creates a new ProjectRootElement for a specific PRE cache
        /// </summary>
        /// <param name="path">The path to the file to load.</param>
        /// <param name="projectRootElementCache">The cache to load the PRE into.</param>
        private static ProjectRootElement OpenLoader(string path, ProjectRootElementCacheBase projectRootElementCache)
        {
            return OpenLoader(path, projectRootElementCache, preserveFormatting: false);
        }

        private static ProjectRootElement OpenLoaderPreserveFormatting(string path, ProjectRootElementCacheBase projectRootElementCache)
        {
            return OpenLoader(path, projectRootElementCache, preserveFormatting: true);
        }

        private static ProjectRootElement OpenLoader(string path, ProjectRootElementCacheBase projectRootElementCache, bool preserveFormatting)
        {
            return new ProjectRootElement(
                path,
                projectRootElementCache,
                preserveFormatting);
        }

        /// <summary>
        /// Creates a ProjectRootElement representing a file, where the file may be a .sln instead of
        /// an MSBuild format file.
        /// Assumes path is already normalized.
        /// If the file is in MSBuild format, may throw InvalidProjectFileException.
        /// If the file is a solution, will throw an IO-related exception if the file cannot be read.
        /// </summary>
        private static ProjectRootElement CreateProjectFromPath(
                string projectFile,
                ProjectRootElementCacheBase projectRootElementCache,
                bool preserveFormatting)
        {
            ErrorUtilities.VerifyThrowInternalRooted(projectFile);

            try
            {
                if (FileUtilities.IsVCProjFilename(projectFile))
                {
                    ProjectFileErrorUtilities.ThrowInvalidProjectFile(new BuildEventFileInfo(projectFile), "ProjectUpgradeNeededToVcxProj", projectFile);
                }

                // OK it's a regular project file, load it normally.
                return new ProjectRootElement(projectFile, projectRootElementCache, preserveFormatting);
            }
            catch (InvalidProjectFileException)
            {
                throw;
            }
            catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
            {
                ProjectFileErrorUtilities.ThrowInvalidProjectFile(new BuildEventFileInfo(projectFile), ex, "InvalidProjectFile", ex.Message);
                throw; // Without this there's a spurious CS0161 because csc 1.2.0.60317 can't see that the above is an unconditional throw.
            }
        }

        /// <summary>
        /// Constructor helper to load an XmlDocumentWithLocation from a path.
        /// Assumes path is already normalized.
        /// May throw InvalidProjectFileException.
        /// Never returns null.
        /// Does NOT add to the ProjectRootElementCache. Caller should add after verifying subsequent MSBuild parsing succeeds.
        /// </summary>
        /// <param name="fullPath">The full path to the document to load.</param>
        /// <param name="preserveFormatting"><code>true</code> to preserve the formatting of the document, otherwise <code>false</code>.</param>
        /// <param name="loadAsReadOnly">Whether to load the file in read-only mode.</param>
        private XmlDocumentWithLocation LoadDocument(string fullPath, bool preserveFormatting, bool loadAsReadOnly)
        {
            ErrorUtilities.VerifyThrowInternalRooted(fullPath);

            var document = new XmlDocumentWithLocation(loadAsReadOnly ? true : (bool?)null)
            {
                FullPath = fullPath,
                PreserveWhitespace = preserveFormatting
            };

            try
            {
                MSBuildEventSource.Log.LoadDocumentStart(fullPath);
                using (XmlReaderExtension xtr = XmlReaderExtension.Create(fullPath, loadAsReadOnly))
                {
                    _encoding = xtr.Encoding;
                    document.Load(xtr.Reader);
                }

                _projectFileLocation = ElementLocation.Create(fullPath);
                _escapedFullPath = null;
                _directory = Path.GetDirectoryName(fullPath);

                if (XmlDocument != null)
                {
                    XmlDocument.FullPath = fullPath;
                }

                _lastWriteTimeWhenReadUtc = FileUtilities.GetFileInfoNoThrow(fullPath).LastWriteTimeUtc;
                if (StreamTimeUtc < _lastWriteTimeWhenReadUtc)
                {
                    StreamTimeUtc = null;
                }
            }
            catch (Exception ex) when (!ExceptionHandling.NotExpectedIoOrXmlException(ex))
            {
                BuildEventFileInfo fileInfo = ex is XmlException xmlException
                    ? new BuildEventFileInfo(fullPath, xmlException)
                    : new BuildEventFileInfo(fullPath);

                ProjectFileErrorUtilities.ThrowInvalidProjectFile(fileInfo, ex, "InvalidProjectFile", ex.Message);
            }
            MSBuildEventSource.Log.LoadDocumentStop(fullPath);

            return document;
        }

        /// <summary>
        /// Constructor helper to load an XmlDocumentWithLocation from an XmlReader.
        /// May throw InvalidProjectFileException.
        /// Never returns null.
        /// </summary>
        private static XmlDocumentWithLocation LoadDocument(XmlReader reader, bool preserveFormatting)
        {
            var document = new XmlDocumentWithLocation { PreserveWhitespace = preserveFormatting };

            try
            {
                document.Load(reader);
            }
            catch (XmlException ex)
            {
                BuildEventFileInfo fileInfo = new BuildEventFileInfo(ex);

                ProjectFileErrorUtilities.ThrowInvalidProjectFile(fileInfo, "InvalidProjectFile", ex.Message);
            }

            return document;
        }

        /// <summary>
        /// Boost the appdomain-unique version counter for this object.
        /// This is done when it is modified, and also when it is loaded.
        /// </summary>
        private void IncrementVersion()
        {
            Version = Interlocked.Increment(ref s_globalVersionCounter);
        }

        private void ThrowIfUnsavedChanges(bool throwIfUnsavedChanges)
        {
            if (HasUnsavedChanges && throwIfUnsavedChanges)
            {
                ErrorUtilities.ThrowInvalidOperation("NoReloadOnUnsavedChanges", null);
            }
        }
    }
}
