// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.IO;

using Microsoft.Build.Collections;
using Microsoft.Build.Shared;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Construction;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared.FileSystem;

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// Wraps an evaluated item for build purposes
    /// </summary>
    /// <remarks>
    /// Does not store XML location information. That is not needed by the build process as all correctness checks
    /// and evaluation has already been performed, so it is unnecessary bulk.
    /// </remarks>
    [DebuggerDisplay("{ItemType}={EvaluatedInclude} #DirectMetadata={DirectMetadataCount})")]
    public class ProjectItemInstance :
        IItem<ProjectMetadataInstance>,
        ITaskItem2,
        IMetadataTable,
        ITranslatable,
        IDeepCloneable<ProjectItemInstance>,
        IMetadataContainer
    {
        /// <summary>
        /// The project instance to which this item belongs.
        /// Never null.
        /// </summary>
        private ProjectInstance _project;

        /// <summary>
        /// Item type, for example "Compile"
        /// Never null.
        /// </summary>
        private string _itemType;

        /// <summary>
        /// Backing task item holding the other data.
        /// Never null.
        /// </summary>
        private TaskItem _taskItem;

        /// <summary>
        /// Constructor for items with no metadata.
        /// Include may be empty.
        /// Called before the build when virtual items are added,
        /// and during the build when tasks emit items.
        /// Mutability follows the project.
        /// </summary>
        internal ProjectItemInstance(ProjectInstance project, string itemType, string includeEscaped, string definingFileEscaped)
            : this(project, itemType, includeEscaped, includeEscaped, definingFileEscaped)
        {
        }

        /// <summary>
        /// Constructor for items with no metadata.
        /// Include may be empty.
        /// Called before the build when virtual items are added,
        /// and during the build when tasks emit items.
        /// Mutability follows the project.
        /// </summary>
        internal ProjectItemInstance(ProjectInstance project, string itemType, string includeEscaped, string includeBeforeWildcardExpansionEscaped, string definingFileEscaped)
            : this(project, itemType, includeEscaped, includeBeforeWildcardExpansionEscaped, null /* no direct metadata */, null /* need to add item definition metadata */, definingFileEscaped)
        {
        }

        /// <summary>
        /// Constructor for items with metadata.
        /// Called before the build when virtual items are added,
        /// and during the build when tasks emit items.
        /// Include may be empty.
        /// Direct metadata may be null, indicating no metadata. It will be cloned.
        /// Builtin metadata may be null, indicating it has not been populated. It will be cloned.
        /// Inherited item definition metadata may be null. It is assumed to ALREADY HAVE BEEN CLONED.
        /// Mutability follows the project.
        /// </summary>
        /// <remarks>
        /// Not public since the only creation scenario is setting on a project.
        /// </remarks>
        internal ProjectItemInstance(ProjectInstance project, string itemType, string includeEscaped, string includeBeforeWildcardExpansionEscaped, CopyOnWritePropertyDictionary<ProjectMetadataInstance> directMetadata, List<ProjectItemDefinitionInstance> itemDefinitions, string definingFileEscaped)
        {
            CommonConstructor(project, itemType, includeEscaped, includeBeforeWildcardExpansionEscaped, directMetadata, itemDefinitions, definingFileEscaped);
        }

        /// <summary>
        /// Constructor for items with metadata.
        /// Called when a ProjectInstance is created.
        /// Include may be empty.
        /// Direct metadata may be null, indicating no metadata. It will be cloned.
        /// Metadata collection provided is cloned.
        /// Mutability follows the project.
        /// </summary>
        /// <remarks>
        /// Not public since the only creation scenario is setting on a project.
        /// </remarks>
        internal ProjectItemInstance(ProjectInstance project, string itemType, string includeEscaped, IEnumerable<KeyValuePair<string, string>> directMetadata, string definingFileEscaped)
        {
            CopyOnWritePropertyDictionary<ProjectMetadataInstance> metadata = null;

            if (directMetadata?.GetEnumerator().MoveNext() == true)
            {
                metadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>(directMetadata.FastCountOrZero());
                foreach (KeyValuePair<string, string> metadatum in directMetadata)
                {
                    metadata.Set(new ProjectMetadataInstance(metadatum.Key, metadatum.Value));
                }
            }

            CommonConstructor(project, itemType, includeEscaped, includeEscaped, metadata, null /* need to add item definition metadata */, definingFileEscaped);
        }

        /// <summary>
        /// Cloning constructor, retaining same parentage.
        /// </summary>
        private ProjectItemInstance(ProjectItemInstance that)
            : this(that, that._project)
        {
        }

        /// <summary>
        /// Cloning constructor.
        /// </summary>
        private ProjectItemInstance(ProjectItemInstance that, ProjectInstance newProject)
        {
            _project = newProject;
            _itemType = that._itemType;
            _taskItem = that._taskItem.DeepClone(newProject.IsImmutable);
        }

        /// <summary>
        /// Constructor for serialization
        /// </summary>
        private ProjectItemInstance(ProjectInstance projectInstance)
        {
            _project = projectInstance;

            // Deserialization continues
        }

        /// <summary>
        /// Owning project
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public ProjectInstance Project
        {
            get { return _project; }
        }

        /// <summary>
        /// Item type, for example "Compile"
        /// </summary>
        /// <remarks>
        /// This cannot be set, as it is used as the key into
        /// the project's items table.
        /// </remarks>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public string ItemType
        {
            [DebuggerStepThrough]
            get
            { return _itemType; }
        }

        /// <summary>
        /// Evaluated include value.
        /// May be empty string.
        /// </summary>
        public string EvaluatedInclude
        {
            [DebuggerStepThrough]
            get
            {
                return _taskItem.ItemSpec;
            }

            [DebuggerStepThrough]
            set
            {
                ErrorUtilities.VerifyThrowArgumentLength(value, "EvaluatedInclude");
                _project.VerifyThrowNotImmutable();

                _taskItem.ItemSpec = value;
            }
        }

        /// <summary>
        /// Evaluated include value, escaped as necessary.
        /// May be empty string.
        /// </summary>
        string IItem.EvaluatedIncludeEscaped
        {
            [DebuggerStepThrough]
            get
            { return _taskItem.IncludeEscaped; }
        }

        /// <summary>
        /// Evaluated include value, escaped as necessary.
        /// May be empty string.
        /// </summary>
        string ITaskItem2.EvaluatedIncludeEscaped
        {
            [DebuggerStepThrough]
            get
            {
                return _taskItem.IncludeEscaped;
            }

            set
            {
                _project.VerifyThrowNotImmutable();

                _taskItem.IncludeEscaped = value;
            }
        }

        /// <summary>
        /// Unordered collection of evaluated metadata on the item.
        /// If there is no metadata, returns an empty collection.
        /// Does not include built-in metadata.
        /// Includes any from item definitions.
        /// This is a read-only collection. To modify the metadata, use <see cref="SetMetadata(string, string)"/>.
        /// </summary>
        /// <comment>
        /// Computed, not necessarily fast.
        /// </comment>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public IEnumerable<ProjectMetadataInstance> Metadata
        {
            get { return _taskItem.MetadataCollection; }
        }

        /// <summary>
        /// Number of pieces of metadata on this item
        /// </summary>
        public int DirectMetadataCount
        {
            get { return _taskItem.DirectMetadataCount; }
        }

        /// <summary>
        /// Implementation of IKeyed exposing the item type
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        string IKeyed.Key
        {
            get { return ItemType; }
        }

        /// <summary>
        /// Returns all the metadata names on this item.
        /// Includes names from any applicable item definitions.
        /// Includes names of built-in metadata.
        /// </summary>
        /// <comment>
        /// Computed, not necessarily fast.
        /// </comment>
        public ICollection<string> MetadataNames
        {
            get { return new ReadOnlyCollection<string>(_taskItem.MetadataNames.Cast<string>()); }
        }

        /// <summary>
        /// ITaskItem implementation
        /// </summary>
        string ITaskItem.ItemSpec
        {
            get
            {
                return EvaluatedInclude;
            }

            set
            {
                _project.VerifyThrowNotImmutable();

                EvaluatedInclude = value;
            }
        }

        /// <summary>
        /// ITaskItem implementation
        /// </summary>
        /// <comment>
        /// Computed, not necessarily fast.
        /// </comment>
        ICollection ITaskItem.MetadataNames
        {
            get { return new List<string>(MetadataNames); }
        }

        /// <summary>
        /// Returns the number of metadata entries.
        /// Includes any from applicable item definitions.
        /// Includes both custom and built-in metadata.
        /// </summary>
        /// <comment>
        /// Computed, not necessarily fast.
        /// </comment>
        public int MetadataCount
        {
            get { return _taskItem.MetadataCount; }
        }

        /// <summary>
        /// The directory of the project being built
        /// Never null: If there is no project filename yet, it will use the current directory
        /// </summary>
        string IItem.ProjectDirectory
        {
            get { return _project.Directory; }
        }

        /// <summary>
        /// Retrieves the comparer used for determining equality between ProjectItemInstances.
        /// </summary>
        internal static IEqualityComparer<ProjectItemInstance> EqualityComparer
        {
            get { return ProjectItemInstanceEqualityComparer.Default; }
        }

        /// <summary>
        /// The full path to the project file being built
        /// Can be null: if the project hasn't been saved yet it will be null
        /// </summary>
        internal string ProjectFullPath
        {
            get { return _project.FullPath; }
        }

        /// <summary>
        /// Get any metadata in the item that has the specified name,
        /// otherwise returns null.
        /// Includes any metadata inherited from item definitions.
        /// Includes any built-in metadata.
        /// </summary>
        public ProjectMetadataInstance GetMetadata(string name)
        {
            return _taskItem.GetMetadataObject(name);
        }

        /// <summary>
        /// Get the value of a metadata on this item, or
        /// String.Empty if it does not exist or has no value.
        /// Includes any metadata inherited from item definitions and any built-in metadata.
        /// To determine whether a piece of metadata is actually present
        /// but with an empty value, use <see cref="HasMetadata(string)">HasMetadata</see>.
        /// </summary>
        public string GetMetadataValue(string name)
        {
            return _taskItem.GetMetadata(name);
        }

        /// <summary>
        /// Returns true if a particular piece of metadata is defined on this item (even if
        /// its value is empty string) otherwise false.
        /// This includes built-in metadata and metadata from item definitions.
        /// </summary>
        /// <remarks>
        /// It has to include all of these because it's used for batching, which doesn't
        /// care where the metadata originated.
        /// </remarks>
        public bool HasMetadata(string name)
        {
            return _taskItem.HasMetadata(name);
        }

        /// <summary>
        /// Add a metadata with the specified name and value.
        /// Overwrites any metadata with the same name already in the collection.
        /// </summary>
        public ProjectMetadataInstance SetMetadata(string name, string evaluatedValue)
        {
            _project.VerifyThrowNotImmutable();

            return _taskItem.SetMetadataObject(name, evaluatedValue, false /* built-in metadata not allowed */);
        }

        /// <summary>
        /// Add a metadata with the specified names and values.
        /// Overwrites any metadata with the same name already in the collection.
        /// </summary>
        public void SetMetadata(IEnumerable<KeyValuePair<string, string>> metadataDictionary)
        {
            _project.VerifyThrowNotImmutable();

            _taskItem.SetMetadata(metadataDictionary);
        }

        /// <summary>
        /// Removes a metadatum with the specified name.
        /// Used by TaskItem
        /// </summary>
        public void RemoveMetadata(string metadataName)
        {
            _project.VerifyThrowNotImmutable();

            _taskItem.RemoveMetadata(metadataName);
        }

        /// <summary>
        /// Produce a string representation.
        /// </summary>
        public override string ToString()
        {
            return _taskItem.ToString();
        }

        /// <summary>
        /// Get the value of a metadata on this item, or
        /// String.Empty if it does not exist or has no value.
        /// Includes any metadata inherited from item definitions and any built-in metadata.
        /// To determine whether a piece of metadata is actually present
        /// but with an empty value, use <see cref="HasMetadata(string)">HasMetadata</see>.
        /// </summary>
        string IItem.GetMetadataValueEscaped(string name)
        {
            return _taskItem.GetMetadataEscaped(name);
        }

        /// <summary>
        /// Sets the specified metadata.  Discards the xml part except for the name.
        /// Discards the location of the original element. This is not interesting in the Execution world
        /// as it should never be needed for any messages, and is just extra bulk.
        /// Predecessor is discarded as it is only needed for design time.
        /// </summary>
        ProjectMetadataInstance IItem<ProjectMetadataInstance>.SetMetadata(ProjectMetadataElement metadataElement, string evaluatedInclude)
        {
            _project.VerifyThrowNotImmutable();

            return SetMetadata(metadataElement.Name, evaluatedInclude);
        }

        /// <summary>
        /// ITaskItem implementation.
        /// </summary>
        /// <remarks>
        /// ITaskItem should not return null if metadata is not present.
        /// </remarks>
        string ITaskItem.GetMetadata(string metadataName)
        {
            return GetMetadataValue(metadataName);
        }

        /// <summary>
        /// ITaskItem2 implementation.
        /// </summary>
        /// <remarks>
        /// ITaskItem2 should not return null if metadata is not present.
        /// </remarks>
        string ITaskItem2.GetMetadataValueEscaped(string name)
        {
            return _taskItem.GetMetadataEscaped(name);
        }

        /// <summary>
        /// ITaskItem implementation
        /// </summary>
        /// <comments>
        /// MetadataValue is assumed to be in its escaped form.
        /// </comments>
        void ITaskItem.SetMetadata(string metadataName, string metadataValue)
        {
            SetMetadata(metadataName, metadataValue);
        }

        /// <summary>
        /// ITaskItem2 implementation
        /// </summary>
        /// <comments>
        /// Assumes metadataValue is unescaped.
        /// </comments>
        void ITaskItem2.SetMetadataValueLiteral(string metadataName, string metadataValue)
        {
            _project.VerifyThrowNotImmutable();

            ((ITaskItem2)_taskItem).SetMetadataValueLiteral(metadataName, metadataValue);
        }

        /// <summary>
        /// ITaskItem implementation
        /// </summary>
        void ITaskItem.CopyMetadataTo(ITaskItem destinationItem)
        {
            _taskItem.CopyMetadataTo(destinationItem);
        }

        /// <summary>
        /// ITaskItem implementation
        /// </summary>
        /// <comments>
        /// Returns a dictionary of the UNESCAPED values of the metadata
        /// </comments>
        IDictionary ITaskItem.CloneCustomMetadata()
        {
            return _taskItem.CloneCustomMetadata();
        }

        /// <summary>
        /// ITaskItem2 implementation
        /// </summary>
        /// <comments>
        /// Returns a dictionary of the ESCAPED values of the metadata
        /// </comments>
        IDictionary ITaskItem2.CloneCustomMetadataEscaped()
        {
            return ((ITaskItem2)_taskItem).CloneCustomMetadataEscaped();
        }

        IEnumerable<KeyValuePair<string, string>> IMetadataContainer.EnumerateMetadata() => _taskItem.EnumerateMetadata();

        #region IMetadataTable Members

        /// <summary>
        /// Retrieves any value we have in our metadata table for the metadata name specified.
        /// If no value is available, returns empty string.
        /// </summary>
        string IMetadataTable.GetEscapedValue(string name)
        {
            return _taskItem.GetMetadataEscaped(name);
        }

        /// <summary>
        /// Retrieves any value we have in our metadata table for the metadata name and item type specified.
        /// If no value is available, returns empty string.
        /// If item type is null, it is ignored, otherwise it must match.
        /// </summary>
        string IMetadataTable.GetEscapedValue(string itemType, string name)
        {
            string value = ((IMetadataTable)this).GetEscapedValueIfPresent(itemType, name);

            return value ?? String.Empty;
        }

        /// <summary>
        /// Returns the value if it exists.
        /// If no value is available, returns null.
        /// If item type is null, it is ignored, otherwise it must match.
        /// </summary>
        string IMetadataTable.GetEscapedValueIfPresent(string itemType, string name)
        {
            if (itemType == null || String.Equals(itemType, _itemType, StringComparison.OrdinalIgnoreCase))
            {
                string value = _taskItem.GetMetadataEscaped(name);

                if (value.Length > 0 || HasMetadata(name))
                {
                    return value;
                }
            }

            return null;
        }

        #endregion

        #region INodePacketTranslatable Members

        /// <summary>
        /// Translation method.
        /// </summary>
        void ITranslatable.Translate(ITranslator translator)
        {
            translator.Translate(ref _itemType);
            translator.Translate(ref _taskItem, TaskItem.FactoryForDeserialization);
        }

        #endregion

        #region IDeepCloneable<T>

        /// <summary>
        /// Deep clone the item.
        /// Any metadata inherited from item definitions are also copied.
        /// </summary>
        ProjectItemInstance IDeepCloneable<ProjectItemInstance>.DeepClone()
        {
            return DeepClone();
        }

        #endregion

        /// <summary>
        /// Set all the supplied metadata on all the supplied items.
        /// </summary>
        internal static void SetMetadata(IEnumerable<KeyValuePair<string, string>> metadataList, IEnumerable<ProjectItemInstance> items)
        {
            // Set up a single dictionary that can be applied to all the items
            CopyOnWritePropertyDictionary<ProjectMetadataInstance> metadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>(metadataList.FastCountOrZero());
            foreach (KeyValuePair<string, string> metadatum in metadataList)
            {
                metadata.Set(new ProjectMetadataInstance(metadatum.Key, metadatum.Value));
            }

            foreach (ProjectItemInstance item in items)
            {
                item._taskItem.SetMetadata(metadata); // Potential copy on write
            }
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        static internal ProjectItemInstance FactoryForDeserialization(ITranslator translator, ProjectInstance projectInstance)
        {
            ProjectItemInstance newItem = new ProjectItemInstance(projectInstance);
            ((ITranslatable)newItem).Translate(translator);
            return newItem;
        }

        /// <summary>
        /// Add a metadata with the specified names and values.
        /// Overwrites any metadata with the same name already in the collection.
        /// </summary>
        internal void SetMetadata(CopyOnWritePropertyDictionary<ProjectMetadataInstance> metadataDictionary)
        {
            _project.VerifyThrowNotImmutable();

            _taskItem.SetMetadata(metadataDictionary);
        }

        /// <summary>
        /// Sets metadata where one built-in metadata is allowed to be set: RecursiveDir.
        /// This is not normally legal to set outside of evaluation. However, the CreateItem
        /// needs to be able to set it as a task output, because it supports wildcards. So as a special exception we allow
        /// tasks to set this particular metadata as a task output.
        /// Other built in metadata names are ignored. That's because often task outputs are items that were passed in,
        /// which legally have built-in metadata. If necessary we can calculate it on the new items we're making if requested.
        /// We don't copy them too because tasks shouldn't set them (they might become inconsistent)
        /// </summary>
        internal void SetMetadataOnTaskOutput(string name, string evaluatedValueEscaped)
        {
            _project.VerifyThrowNotImmutable();

            _taskItem.SetMetadataOnTaskOutput(name, evaluatedValueEscaped);
        }

        /// <summary>
        /// Deep clone the item.
        /// Any metadata inherited from item definitions are also copied.
        /// </summary>
        internal ProjectItemInstance DeepClone()
        {
            return new ProjectItemInstance(this);
        }

        /// <summary>
        /// Deep clone the item.
        /// Any metadata inherited from item definitions are also copied.
        /// </summary>
        internal ProjectItemInstance DeepClone(ProjectInstance newProject)
        {
            return new ProjectItemInstance(this, newProject);
        }

        /// <summary>
        /// Generates a ProjectItemElement representing this instance.
        /// </summary>
        /// <param name="parent">The root element to which the element will belong.</param>
        /// <returns>The new element.</returns>
        internal ProjectItemElement ToProjectItemElement(ProjectElementContainer parent)
        {
            ProjectItemElement item = parent.ContainingProject.CreateItemElement(ItemType);
            item.Include = EvaluatedInclude;
            parent.AppendChild(item);

            foreach (ProjectMetadataInstance metadataInstance in Metadata)
            {
                item.AddMetadata(metadataInstance.Name, metadataInstance.EvaluatedValue);
            }

            return item;
        }

        /// <summary>
        /// Common constructor code.
        /// Direct metadata may be null, indicating no metadata. It will be cloned.
        /// Builtin metadata may be null, indicating it has not been populated. It will be cloned.
        /// Inherited item definition metadata may be null. It is assumed to ALREADY HAVE BEEN CLONED.
        /// Mutability follows the project.
        /// </summary>
        private void CommonConstructor(ProjectInstance projectToUse, string itemTypeToUse, string includeEscaped, string includeBeforeWildcardExpansionEscaped, CopyOnWritePropertyDictionary<ProjectMetadataInstance> directMetadata, List<ProjectItemDefinitionInstance> itemDefinitions, string definingFileEscaped)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectToUse, "project");
            ErrorUtilities.VerifyThrowArgumentLength(itemTypeToUse, "itemType");
            XmlUtilities.VerifyThrowArgumentValidElementName(itemTypeToUse);
            ErrorUtilities.VerifyThrowArgument(!XMakeElements.ReservedItemNames.Contains(itemTypeToUse), "OM_ReservedName", itemTypeToUse);

            // TaskItems don't have an item type. So for their benefit, we have to lookup and add the regular item definition.
            List<ProjectItemDefinitionInstance> inheritedItemDefinitions = (itemDefinitions == null) ? null : new List<ProjectItemDefinitionInstance>(itemDefinitions);

            ProjectItemDefinitionInstance itemDefinition;
            if (projectToUse.ItemDefinitions.TryGetValue(itemTypeToUse, out itemDefinition))
            {
                inheritedItemDefinitions ??= new List<ProjectItemDefinitionInstance>();
                inheritedItemDefinitions.Add(itemDefinition);
            }

            _project = projectToUse;
            _itemType = itemTypeToUse;
            _taskItem = new TaskItem(
                                        includeEscaped,
                                        includeBeforeWildcardExpansionEscaped,
                                        directMetadata?.DeepClone(), // copy on write!
                                        inheritedItemDefinitions,
                                        _project.Directory,
                                        _project.IsImmutable,
                                        definingFileEscaped
                                        );
        }

        /// <summary>
        /// An item without an item type. Cast to an ITaskItem, this is
        /// what is given to tasks. It is also used for target outputs.
        /// </summary>
        internal sealed class TaskItem :
#if FEATURE_APPDOMAIN
            MarshalByRefObject,
#endif
            ITaskItem2,
            IItem<ProjectMetadataInstance>,
            ITranslatable,
            IEquatable<TaskItem>,
            IMetadataContainer
        {
            /// <summary>
            /// The source file that defined this item.
            /// </summary>
            private string _definingFileEscaped;

            /// <summary>
            /// Evaluated include, escaped as necessary.
            /// </summary>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private string _includeEscaped;

            /// <summary>
            /// The evaluated (escaped) include prior to wildcard expansion.  Used to determine the
            /// RecursiveDir build-in metadata value.
            /// </summary>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private string _includeBeforeWildcardExpansionEscaped;

            /// <summary>
            /// Evaluated metadata.
            /// May be null.
            /// </summary>
            /// <remarks>
            /// Lazily created, as there are huge numbers of items generated in
            /// a build that have no metadata at all.
            /// </remarks>
            private CopyOnWritePropertyDictionary<ProjectMetadataInstance> _directMetadata;

            /// <summary>
            /// Cached value of the fullpath metadata. All other metadata are computed on demand.
            /// </summary>
            private string _fullPath;

            /// <summary>
            /// All the item definitions that apply to this item, in order of
            /// decreasing precedence. At the bottom will be an item definition
            /// that directly applies to the item type that produced this item. The others will
            /// be item definitions inherited from items that were
            /// used to create this item.
            /// </summary>
            private List<ProjectItemDefinitionInstance> _itemDefinitions;

            /// <summary>
            /// Directory of the associated project. If this is available,
            /// it is used to calculate built-in metadata. Otherwise,
            /// the current directory is used.
            /// </summary>
            private string _projectDirectory;

            /// <summary>
            /// Whether the task item is immutable.
            /// </summary>
            private bool _isImmutable;

            /// <summary>
            /// Creates an instance of this class given the item-spec.
            /// </summary>
            internal TaskItem(string includeEscaped, string definingFileEscaped)
                : this(includeEscaped, includeEscaped, null, null, null, /* mutable */ false, definingFileEscaped)
            {
            }

            /// <summary>
            /// Creates an instance of this class given the item-spec and a built-in metadata collection.
            /// Parameters are assumed to be ALREADY CLONED.
            /// </summary>
            internal TaskItem(
                              string includeEscaped,
                              string includeBeforeWildcardExpansionEscaped,
                              CopyOnWritePropertyDictionary<ProjectMetadataInstance> directMetadata,
                              List<ProjectItemDefinitionInstance> itemDefinitions,
                              string projectDirectory,
                              bool immutable,
                              string definingFileEscaped // the actual project file (or import) that defines this item.
                              )
            {
                ErrorUtilities.VerifyThrowArgumentLength(includeEscaped, nameof(includeEscaped));
                ErrorUtilities.VerifyThrowArgumentLength(includeBeforeWildcardExpansionEscaped, nameof(includeBeforeWildcardExpansionEscaped));

                _includeEscaped = FileUtilities.FixFilePath(includeEscaped);
                _includeBeforeWildcardExpansionEscaped = FileUtilities.FixFilePath(includeBeforeWildcardExpansionEscaped);
                _directMetadata = (directMetadata == null || directMetadata.Count == 0) ? null : directMetadata; // If the metadata was all removed, toss the dictionary
                _itemDefinitions = itemDefinitions;
                _projectDirectory = projectDirectory;
                _isImmutable = immutable;
                _definingFileEscaped = definingFileEscaped;
            }

            /// <summary>
            /// Creates a task item by copying the information from a <see cref="ProjectItemInstance"/>.
            /// Parameters are cloned.
            /// </summary>
            internal TaskItem(ProjectItemInstance item)
                : this(item._taskItem, false /* no original itemspec */)
            {
            }

            /// <summary>
            /// Constructor for deserialization only.
            /// </summary>
            private TaskItem()
            {
            }

            /// <summary>
            /// Creates an instance of this class given the backing item.
            /// Does not copy immutability, since there is no connection with the original.
            /// </summary>
            private TaskItem(TaskItem source, bool addOriginalItemSpec)
            {
                _includeEscaped = source._includeEscaped;
                _includeBeforeWildcardExpansionEscaped = source._includeBeforeWildcardExpansionEscaped;
                source.CopyMetadataTo(this, addOriginalItemSpec);
                _fullPath = source._fullPath;
                _definingFileEscaped = source._definingFileEscaped;
            }

            /// <summary>
            /// Private constructor used for serialization.
            /// </summary>
            private TaskItem(ITranslator translator)
            {
                ((ITranslatable)this).Translate(translator);
            }

            /// <summary>
            /// Private constructor used for serialization.
            /// </summary>
            private TaskItem(ITranslator translator, LookasideStringInterner interner)
            {
                this.TranslateWithInterning(translator, interner);
            }

            /// <summary>
            /// Gets or sets the unescaped include, or "name", for the item.
            /// </summary>
            /// <comments>
            /// This one is a bit tricky.  Orcas assumed that the value being set was escaped, but
            /// that the value being returned was unescaped.  Maintain that behaviour here.  To get
            /// the escaped value, use ITaskItem2.EvaluatedIncludeEscaped.
            /// </comments>
            public string ItemSpec
            {
                get
                {
                    return EscapingUtilities.UnescapeAll(_includeEscaped);
                }

                set
                {
                    ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                    // Historically empty string was allowed
                    ErrorUtilities.VerifyThrowArgumentNull(value, "ItemSpec");

                    _includeEscaped = value;
                    _fullPath = null; // Clear cached value
                }
            }

            /// <summary>
            /// Gets or sets the escaped include, or "name", for the item.
            /// </summary>
            /// <remarks>
            /// Taking the opportunity to fix the property name, although this doesn't
            /// make it obvious it's an improvement on ItemSpec.
            /// </remarks>
            string ITaskItem2.EvaluatedIncludeEscaped
            {
                get
                {
                    return _includeEscaped;
                }

                set
                {
                    ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                    // setter on ItemSpec already expects an escaped value.
                    ItemSpec = value;
                }
            }

            /// <summary>
            /// Gets the names of metadata on the item.
            /// Includes all built-in metadata.
            /// Computed, not necessarily fast.
            /// </summary>
            public ICollection MetadataNames
            {
                get
                {
                    List<string> names = new List<string>((List<string>)CustomMetadataNames);

                    foreach (string name in FileUtilities.ItemSpecModifiers.All)
                    {
                        names.Add(name);
                    }

                    return names;
                }
            }

            /// <summary>
            /// Gets the number of metadata set on the item.
            /// Computed, not necessarily fast.
            /// </summary>
            public int MetadataCount
            {
                get { return MetadataNames.Count; }
            }

            /// <summary>
            /// Gets the names of custom metadata on the item.
            /// If there is none, returns an empty collection.
            /// Does not include built-in metadata.
            /// Computed, not necessarily fast.
            /// </summary>
            public ICollection CustomMetadataNames
            {
                get
                {
                    List<string> names = new List<string>();

                    foreach (ProjectMetadataInstance metadatum in MetadataCollection)
                    {
                        names.Add(metadatum.Name);
                    }

                    return names;
                }
            }

            /// <summary>
            /// Gets the number of custom metadata set on the item.
            /// Does not include built-in metadata.
            /// Computed, not necessarily fast.
            /// </summary>
            public int CustomMetadataCount
            {
                get { return CustomMetadataNames.Count; }
            }

            /// <summary>
            /// Gets the evaluated include for this item, unescaped.
            /// </summary>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            string IItem.EvaluatedInclude
            {
                get { return EscapingUtilities.UnescapeAll(_includeEscaped); }
            }

            /// <summary>
            /// Gets the evaluated include for this item, escaped.
            /// </summary>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            string IItem.EvaluatedIncludeEscaped
            {
                get { return _includeEscaped; }
            }

            /// <summary>
            /// The directory of the project owning this TaskItem.
            /// May be null if this is not well defined.
            /// </summary>
            string IItem.ProjectDirectory
            {
                get { return _projectDirectory; }
            }

            #region IKeyed Members

            /// <summary>
            /// Returns some value useful for a key in a dictionary
            /// </summary>
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            string Microsoft.Build.Collections.IKeyed.Key
            {
                get { return _includeEscaped; }
            }

            #endregion

            /// <summary>
            /// The escaped include for this item
            /// </summary>
            internal string IncludeEscaped
            {
                get
                {
                    return _includeEscaped;
                }

                set
                {
                    ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                    ErrorUtilities.VerifyThrowArgumentLength(value, "IncludeEscaped");
                    _includeEscaped = value;
                    _fullPath = null; // Clear cached value
                }
            }

            /// <summary>
            /// The value of the include after evaluation but before wildcard expansion.
            /// Used to determine %(RecursiveDir)
            /// </summary>
            internal string IncludeBeforeWildcardExpansionEscaped
            {
                get { return _includeBeforeWildcardExpansionEscaped; }
            }

            /// <summary>
            /// Number of pieces of metadata directly on this item
            /// </summary>
            internal int DirectMetadataCount
            {
                get { return (_directMetadata == null) ? 0 : _directMetadata.Count; }
            }

            /// <summary>
            /// Efficient way to retrieve metadata used by packet serialization
            /// and binary logger.
            /// </summary>
            public IEnumerable<KeyValuePair<string, string>> EnumerateMetadata()
            {
                // If we have item definitions, call the expensive property that does the right thing.
                // Otherwise use _directMetadata to avoid allocations caused by DeepClone().
                var list = _itemDefinitions != null ? MetadataCollection : _directMetadata;
                if (list != null)
                {
#if FEATURE_APPDOMAIN
                    // Can't send a yield-return iterator across AppDomain boundaries
                    if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                    {
                        return EnumerateMetadataEager(list);
                    }
#endif
                    // Mainline scenario, returns an iterator to avoid allocating an array
                    // to store the results. With the iterator, results can stream to the
                    // consumer (e.g. binlog writer) without allocations.
                    return EnumerateMetadata(list);
                }
                else
                {
                    return Array.Empty<KeyValuePair<string, string>>();
                }
            }

            /// <summary>
            /// Used to return metadata from another AppDomain. Can't use yield return because the
            /// generated state machine is not marked as [Serializable], so we need to allocate.
            /// </summary>
            /// <param name="list">The source list to return metadata from.</param>
            /// <returns>An array of string key-value pairs representing metadata.</returns>
            private IEnumerable<KeyValuePair<string, string>> EnumerateMetadataEager(CopyOnWritePropertyDictionary<ProjectMetadataInstance> list)
            {
                var result = new List<KeyValuePair<string, string>>(list.Count);

                foreach (var projectMetadataInstance in list)
                {
                    if (projectMetadataInstance != null)
                    {
                        result.Add(new KeyValuePair<string, string>(projectMetadataInstance.Name, projectMetadataInstance.EvaluatedValue));
                    }
                }

                // Probably better to send the raw array across the wire even if it's another allocation.
                return result.ToArray();
            }

            private IEnumerable<KeyValuePair<string, string>> EnumerateMetadata(CopyOnWritePropertyDictionary<ProjectMetadataInstance> list)
            {
                foreach (var projectMetadataInstance in list)
                {
                    if (projectMetadataInstance != null)
                    {
                        yield return new KeyValuePair<string, string>(projectMetadataInstance.Name, projectMetadataInstance.EvaluatedValue);
                    }
                }
            }

            /// <summary>
            /// Unordered collection of evaluated metadata on the item.
            /// If there is no metadata, returns an empty collection.
            /// Does not include built-in metadata.
            /// Includes any from item definitions not masked by directly set metadata.
            /// This is a read-only collection. To modify the metadata, use <see cref="SetMetadata(string, string)"/>.
            /// Computed, not necessarily fast.
            /// </summary>
            internal CopyOnWritePropertyDictionary<ProjectMetadataInstance> MetadataCollection
            {
                get
                {
                    // The new item inherits any metadata originating in item definitions, which
                    // takes precedence over its own item definition metadata.
                    //
                    // Order of precedence:
                    // (1) any directly defined metadata on the source item
                    // (2) any item definition metadata the item had accumulated, in order of accumulation
                    //  (last of which is any item definition metadata associated with the destination item's item type)
                    if (_itemDefinitions == null)
                    {
                        return (_directMetadata == null) ? new CopyOnWritePropertyDictionary<ProjectMetadataInstance>() : _directMetadata.DeepClone(); // copy on write!
                    }

                    CopyOnWritePropertyDictionary<ProjectMetadataInstance> allMetadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>(_itemDefinitions.Count + (_directMetadata?.Count ?? 0));

                    // Next, any inherited item definitions. Front of the list is highest priority,
                    // so walk backwards.
                    for (int i = _itemDefinitions.Count - 1; i >= 0; i--)
                    {
                        foreach (ProjectMetadataInstance metadatum in _itemDefinitions[i].Metadata)
                        {
                            if (metadatum != null)
                            {
                                allMetadata.Set(metadatum);
                            }
                            else
                            {
                                Debug.Fail($"metadatum from {_itemDefinitions[i]} is null, see https://github.com/microsoft/msbuild/issues/5267");
                            }
                        }
                    }

                    // Finally any direct metadata win.
                    if (_directMetadata != null)
                    {
                        foreach (ProjectMetadataInstance metadatum in _directMetadata)
                        {
                            if (metadatum != null)
                            {
                                allMetadata.Set(metadatum);
                            }
                            else
                            {
                                Debug.Fail("metadatum in _directMetadata is null, see https://github.com/microsoft/msbuild/issues/5267");
                            }
                        }
                    }

                    return allMetadata;
                }
            }

            IEnumerable<ProjectMetadataInstance> IItem<ProjectMetadataInstance>.Metadata => MetadataCollection;

#region Operators

            /// <summary>
            /// This allows an explicit typecast from a "TaskItem" to a "string", returning the ItemSpec for this item.
            /// </summary>
            public static explicit operator string(TaskItem that)
            {
                return that._includeEscaped;
            }

            /// <summary>
            /// The equivalence operator.
            /// </summary>
            /// <param name="left">The left hand operand.</param>
            /// <param name="right">The right hand operand.</param>
            /// <returns>True if the items are equivalent, false otherwise.</returns>
            public static bool operator ==(TaskItem left, TaskItem right)
            {
                if (!(left is null))
                {
                    return left.Equals(right);
                }
                else if (!(right is null))
                {
                    return right.Equals(left);
                }

                return true;
            }

            /// <summary>
            /// The non-equivalence operator.
            /// </summary>
            /// <param name="left">The left hand operand.</param>
            /// <param name="right">The right hand operand.</param>
            /// <returns>False if the items are equivalent, true otherwise.</returns>
            public static bool operator !=(TaskItem left, TaskItem right)
            {
                return !(left == right);
            }

#endregion

            /// <summary>
            /// Produce a string representation.
            /// </summary>
            public override string ToString()
            {
                return _includeEscaped;
            }

#if FEATURE_APPDOMAIN
            /// <summary>
            /// Overridden to give this class infinite lease time. Otherwise we end up with a limited
            /// lease (5 minutes I think) and instances can expire if they take long time processing.
            /// </summary>
            public override object InitializeLifetimeService()
            {
                // null means infinite lease time
                return null;
            }
#endif

#region IItem and ITaskItem2 Members

            /// <summary>
            /// Returns the metadata with the specified key.
            /// </summary>
            string IItem.GetMetadataValue(string name)
            {
                return GetMetadata(name);
            }

            /// <summary>
            /// Returns the escaped value of the metadata with the specified key.
            /// </summary>
            string IItem.GetMetadataValueEscaped(string name)
            {
                return GetMetadataEscaped(name);
            }

            /// <summary>
            /// Returns the escaped value of the metadata with the specified key.
            /// </summary>
            string ITaskItem2.GetMetadataValueEscaped(string name)
            {
                return GetMetadataEscaped(name);
            }

            /// <summary>
            /// Gets any existing ProjectMetadata on the item, or
            /// else any on an applicable item definition.
            /// This is ONLY called during evaluation.
            /// </summary>
            /// <remarks>
            /// Evaluation never creates ITaskItems, so this should never be called.
            /// </remarks>
            ProjectMetadataInstance IItem<ProjectMetadataInstance>.GetMetadata(string name)
            {
                ErrorUtilities.ThrowInternalErrorUnreachable();
                return null;
            }

            /// <summary>
            /// Set metadata
            /// </summary>
            ProjectMetadataInstance IItem<ProjectMetadataInstance>.SetMetadata(ProjectMetadataElement metadataElement, string evaluatedInclude)
            {
                ErrorUtilities.ThrowInternalErrorUnreachable();
                return null;
            }

            /// <summary>
            /// ITaskItem implementation which returns the specified metadata value, unescaped.
            /// If metadata is not defined, returns empty string.
            /// </summary>
            public string GetMetadata(string metadataName)
            {
                return EscapingUtilities.UnescapeAll(GetMetadataEscaped(metadataName));
            }

            /// <summary>
            /// Returns the specified metadata value, escaped.
            /// If metadata is not defined, returns empty string.
            /// </summary>
            public string GetMetadataEscaped(string metadataName)
            {
                if (string.IsNullOrEmpty(metadataName))
                {
                    ErrorUtilities.VerifyThrowArgumentLength(metadataName, nameof(metadataName));
                }

                ProjectMetadataInstance metadatum;
                if (_directMetadata != null)
                {
                    metadatum = _directMetadata[metadataName];
                    if (metadatum != null)
                    {
                        return metadatum.EvaluatedValueEscaped;
                    }
                }

                metadatum = GetItemDefinitionMetadata(metadataName);

                if (metadatum != null && Expander<ProjectProperty, ProjectItem>.ExpressionMayContainExpandableExpressions(metadatum.EvaluatedValueEscaped))
                {
                    Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(null, null, new BuiltInMetadataTable(null, this));

                    // We don't have a location to use, but this is very unlikely to error
                    return expander.ExpandIntoStringLeaveEscaped(metadatum.EvaluatedValueEscaped, ExpanderOptions.ExpandBuiltInMetadata, ElementLocation.EmptyLocation);
                }
                else if (metadatum != null)
                {
                    return metadatum.EvaluatedValueEscaped;
                }

                string value = GetBuiltInMetadataEscaped(metadataName);

                return value ?? String.Empty;
            }

            /// <summary>
            /// ITaskItem implementation which sets metadata.
            /// </summary>
            /// <comments>
            /// The value is assumed to be escaped.
            /// </comments>
            public void SetMetadata(string metadataName, string metadataValueEscaped)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                SetMetadataObject(metadataName, metadataValueEscaped, true /* built-in metadata allowed */);
            }

            /// <summary>
            /// ITaskItem2 implementation which sets the literal value of metadata -- it is escaped
            /// internally as necessary.
            /// </summary>
            void ITaskItem2.SetMetadataValueLiteral(string metadataName, string metadataValue)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                SetMetadata(metadataName, EscapingUtilities.Escape(metadataValue));
            }

            /// <summary>
            /// ITaskItem implementation which removed the named piece of metadata.
            /// If the metadata is not present, does nothing.
            /// </summary>
            public void RemoveMetadata(string metadataName)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                // If the metadata was all removed, toss the dictionary
                _directMetadata?.Remove(metadataName, clearIfEmpty: true);
            }

            /// <summary>
            /// ITaskItem implementation which copies the metadata on this item to the specified item.
            /// Does not copy built-in metadata, and will not overwrite existing, non-empty metadata.
            /// If the destination implements ITaskItem2, this avoids losing the escaped nature of values.
            /// </summary>
            public void CopyMetadataTo(ITaskItem destinationItem)
            {
                CopyMetadataTo(destinationItem, true /* add original itemspec metadata */);
            }

            /// <summary>
            /// ITaskItem implementation which copies the metadata on this item to the specified item.
            /// Copies direct and item definition metadata.
            /// Does not copy built-in metadata, and will not overwrite existing, non-empty metadata.
            /// If the destination implements ITaskItem2, this avoids losing the escaped nature of values.
            ///
            /// When copying metadata to a task item which can be accessed from a task (Utilities task item)
            /// this method will merge and expand any metadata originating with item definitions.
            /// </summary>
            /// <param name="destinationItem">destination item to copy the metadata from this to</param>
            /// <param name="addOriginalItemSpec">Whether the OriginalItemSpec should be added as a piece
            /// of magic metadata. For copying of items this is useful but for cloning of items this adds
            /// additional metadata which is not useful because the OriginalItemSpec will always be identical
            /// to the ItemSpec, and the addition will and will cause copy-on-write to trigger.
            /// </param>
            public void CopyMetadataTo(ITaskItem destinationItem, bool addOriginalItemSpec)
            {
                ErrorUtilities.VerifyThrowArgumentNull(destinationItem, nameof(destinationItem));

                string originalItemSpec = null;
                if (addOriginalItemSpec)
                {
                    // also copy the original item-spec under a "magic" metadata -- this is useful for tasks that forward metadata
                    // between items, and need to know the source item where the metadata came from.
                    // Get it before the clone, as it will get overwritten on the destination otherwise
                    originalItemSpec = destinationItem.GetMetadata("OriginalItemSpec");
                }

                TaskItem destinationAsTaskItem = destinationItem as TaskItem;

                if (destinationAsTaskItem != null && destinationAsTaskItem._directMetadata == null)
                {
                    ProjectInstance.VerifyThrowNotImmutable(destinationAsTaskItem._isImmutable);

                    // This optimized path is hit most often
                    destinationAsTaskItem._directMetadata = _directMetadata?.DeepClone(); // copy on write!

                    // If the destination item already has item definitions then we want to maintain them
                    // But ours will be of less precedence than those already on the item
                    if (destinationAsTaskItem._itemDefinitions == null)
                    {
                        destinationAsTaskItem._itemDefinitions = (_itemDefinitions == null) ? null : new List<ProjectItemDefinitionInstance>(_itemDefinitions);
                    }
                    else if (_itemDefinitions != null)
                    {
                        destinationAsTaskItem._itemDefinitions.AddRange(_itemDefinitions);
                    }
                }
                else
                {
                    // OK, most likely the destination item was a Microsoft.Build.Utilities.TaskItem.
                    foreach (ProjectMetadataInstance metadatum in MetadataCollection)
                    {
                        // When copying metadata, we do NOT overwrite metadata already on the destination item.
                        string destinationValue = destinationItem.GetMetadata(metadatum.Name);
                        if (String.IsNullOrEmpty(destinationValue))
                        {
                            // Utilities.TaskItem's don't know about item definition metadata. So merge that into the values.
                            destinationItem.SetMetadata(metadatum.Name, GetMetadataEscaped(metadatum.Name));
                        }
                    }
                }

                if (addOriginalItemSpec)
                {
                    if (String.IsNullOrEmpty(originalItemSpec))
                    {
                        // This does not appear to significantly cause a copy-on-write; otherwise, it could go in its own slot.
                        destinationItem.SetMetadata("OriginalItemSpec", _includeEscaped);
                    }
                }
            }

            /// <summary>
            /// ITaskItem implementation which returns a clone of the metadata on this object.
            /// Values returned are unescaped. To get the original escaped values, use ITaskItem2.CloneCustomMetadataEscaped instead.
            /// </summary>
            /// <returns>The cloned metadata.</returns>
            public IDictionary CloneCustomMetadata()
            {
                var metadata = MetadataCollection;
                Dictionary<string, string> clonedMetadata = new Dictionary<string, string>(metadata.Count, MSBuildNameIgnoreCaseComparer.Default);

                foreach (ProjectMetadataInstance metadatum in metadata)
                {
                    clonedMetadata[metadatum.Name] = metadatum.EvaluatedValue;
                }

                return clonedMetadata;
            }

            /// <summary>
            /// ITaskItem2 implementation which returns a clone of the metadata on this object.
            /// Values returned are in their original escaped form.
            /// </summary>
            /// <returns>The cloned metadata.</returns>
            IDictionary ITaskItem2.CloneCustomMetadataEscaped()
            {
                Dictionary<string, string> clonedMetadata = new Dictionary<string, string>(MSBuildNameIgnoreCaseComparer.Default);

                foreach (ProjectMetadataInstance metadatum in MetadataCollection)
                {
                    clonedMetadata[metadatum.Name] = metadatum.EvaluatedValueEscaped;
                }

                return clonedMetadata;
            }

#endregion

#region INodePacketTranslatable Members

            /// <summary>
            /// Reads or writes the packet to the serializer.
            /// Built-in metadata is not transmitted, but other metadata is.
            /// Does not lose escaped nature.
            /// </summary>
            void ITranslatable.Translate(ITranslator translator)
            {
                translator.Translate(ref _includeEscaped);
                translator.Translate(ref _includeBeforeWildcardExpansionEscaped);
                translator.Translate(ref _isImmutable);
                translator.Translate(ref _definingFileEscaped);

                translator.Translate(ref _itemDefinitions, ProjectItemDefinitionInstance.FactoryForDeserialization);
                translator.TranslateDictionary(ref _directMetadata, ProjectMetadataInstance.FactoryForDeserialization);

                if (_itemDefinitions?.Count == 0)
                {
                    // If there are no item definitions, toss the list.
                    _itemDefinitions = null;
                }

                if (_directMetadata?.Count == 0)
                {
                    // If there is no metadata, toss the dictionary.
                    _directMetadata = null;
                }
            }

#endregion

#region IEquatable<TaskItem> Members

            /// <summary>
            /// Override of GetHashCode.
            /// </summary>
            public override int GetHashCode()
            {
                // This is ignore case to ensure that task items whose item specs differ only by 
                // casing still have the same hash code, since this is used to determine if we have duplicates when 
                // we do duplicate removal.
                return StringComparer.OrdinalIgnoreCase.GetHashCode(ItemSpec);
            }

            /// <summary>
            /// Override of Equals
            /// </summary>
            public override bool Equals(object obj)
            {
                return this.Equals(obj as TaskItem);
            }

            /// <summary>
            /// Test for item equivalence.  Items are equivalent if their item specs are the same,
            /// and they have the same custom metadata, case insensitive.
            /// </summary>
            /// <comments>
            /// The metadata value check has to be case insensitive as batching bucketing is case
            /// insensitive.
            /// </comments>
            /// <param name="other">The item against which to compare.</param>
            /// <returns>True if the items are equivalent, false otherwise.</returns>
            public bool Equals(TaskItem other)
            {
                if (other is null)
                {
                    return false;
                }

                if (Object.ReferenceEquals(this, other))
                {
                    return true;
                }

                // Since both sides are this class, we know both sides support ITaskItem2.
                ITaskItem2 thisAsITaskItem2 = this as ITaskItem2;
                ITaskItem2 otherAsITaskItem2 = other as ITaskItem2;

                // This is case-insensitive. See GetHashCode().
                if (!MSBuildNameIgnoreCaseComparer.Default.Equals(thisAsITaskItem2.EvaluatedIncludeEscaped, otherAsITaskItem2.EvaluatedIncludeEscaped))
                {
                    return false;
                }

                if (this.CustomMetadataCount != other.CustomMetadataCount)
                {
                    return false;
                }

                foreach (string name in this.CustomMetadataNames)
                {
                    // This is case-insensitive, so that for example "en-US" and "en-us" match and are bucketed together.
                    // In this respect, therefore, we have to consider item metadata value case as not significant.
                    if (!String.Equals
                            (
                                thisAsITaskItem2.GetMetadataValueEscaped(name),
                                otherAsITaskItem2.GetMetadataValueEscaped(name),
                                StringComparison.OrdinalIgnoreCase
                            )
                       )
                    {
                        return false;
                    }
                }

                // Do not consider mutability for equality comparison
                return true;
            }

#endregion

            /// <summary>
            /// Returns true if a particular piece of metadata is defined on this item (even if
            /// its value is empty string) otherwise false.
            /// This includes built-in metadata and metadata from item definitions.
            /// </summary>
            /// <remarks>
            /// It has to include all of these because it's used for batching, which doesn't
            /// care where the metadata originated.
            /// </remarks>
            public bool HasMetadata(string name)
            {
                if ((_directMetadata?.Contains(name) == true) ||
                     FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name) ||
                    GetItemDefinitionMetadata(name) != null)
                {
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Add a metadata with the specified names and values.
            /// Overwrites any metadata with the same name already in the collection.
            /// </summary>
            /// <comments>
            /// Assumes that metadataDictionary contains escaped values
            /// </comments>
            public void SetMetadata(IEnumerable<KeyValuePair<string, string>> metadataDictionary)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                foreach (KeyValuePair<string, string> metadataEntry in metadataDictionary)
                {
                    SetMetadata(metadataEntry.Key, metadataEntry.Value);
                }
            }

            /// <summary>
            /// Factory for serialization.
            /// </summary>
            internal static TaskItem FactoryForDeserialization(ITranslator translator)
            {
                return new TaskItem(translator);
            }

            /// <summary>
            /// Factory for serialization.
            /// </summary>
            internal static TaskItem FactoryForDeserialization(ITranslator translator, LookasideStringInterner interner)
            {
                return new TaskItem(translator, interner);
            }

            private void WriteInternString(ITranslator translator, LookasideStringInterner interner, ref string str)
            {
                var key = interner.Intern(str);
                translator.Writer.Write(key);
            }
            
            private void ReadInternString(ITranslator translator, LookasideStringInterner interner, ref string str)
            {
                var val = translator.Reader.ReadInt32();
                str = interner.GetString(val);
            }

            /// <summary>
            /// Reads or writes the task item to the translator using an interner for metadata.
            /// </summary>
            internal void TranslateWithInterning(ITranslator translator, LookasideStringInterner interner)
            {
                translator.Translate(ref _itemDefinitions, ProjectItemDefinitionInstance.FactoryForDeserialization);
                translator.Translate(ref _isImmutable);
                translator.Translate(ref _includeEscaped);

                if (translator.Mode == TranslationDirection.WriteToStream)
                {
                    WriteInternString(translator, interner, ref _includeBeforeWildcardExpansionEscaped);
                    WriteInternString(translator, interner, ref _definingFileEscaped);

                    CopyOnWritePropertyDictionary<ProjectMetadataInstance> temp = MetadataCollection;

                    // Intern the metadata
                    if (translator.TranslateNullable(temp))
                    {
                        int count = temp.Count;
                        translator.Writer.Write(count);
                        foreach (ProjectMetadataInstance metadatum in temp)
                        {
                            int key = interner.Intern(metadatum.Name);
                            int value = interner.Intern(metadatum.EvaluatedValueEscaped);
                            translator.Writer.Write(key);
                            translator.Writer.Write(value);
                        }
                    }
                }
                else
                {
                    ReadInternString(translator, interner, ref _includeBeforeWildcardExpansionEscaped);
                    ReadInternString(translator, interner, ref _definingFileEscaped);
                    if (translator.TranslateNullable(_directMetadata))
                    {
                        int count = translator.Reader.ReadInt32();
                        _directMetadata = (count == 0) ? null : new CopyOnWritePropertyDictionary<ProjectMetadataInstance>(count);
                        for (int i = 0; i < count; i++)
                        {
                            int key = translator.Reader.ReadInt32();
                            int value = translator.Reader.ReadInt32();
                            _directMetadata.Set(new ProjectMetadataInstance(interner.GetString(key), interner.GetString(value), allowItemSpecModifiers: true));
                        }
                    }
                }
            }

            /// <summary>
            /// Gets any metadata with the specified name.
            /// Does not include built-in metadata.
            /// </summary>
            internal ProjectMetadataInstance GetMetadataObject(string name)
            {
                ProjectMetadataInstance value = null;

                if (_directMetadata != null)
                {
                    value = _directMetadata[name];
                }

                if (value == null)
                {
                    value = GetItemDefinitionMetadata(name);
                }

                return value;
            }

            /// <summary>
            /// Add a metadata with the specified name and value.
            /// Overwrites any metadata with the same name already in the collection.
            /// </summary>
            internal void SetMetadata(CopyOnWritePropertyDictionary<ProjectMetadataInstance> metadata)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                if (metadata.Count == 0)
                {
                    return;
                }

                if (_directMetadata == null)
                {
                    _directMetadata = metadata.DeepClone(); // Copy on write !
                }
                else
                {
                    _directMetadata.ImportProperties(metadata);
                }
            }

            /// <summary>
            /// Add a metadata with the specified name and value.
            /// Overwrites any metadata with the same name already in the collection.
            /// Does not allow built-in metadata unless allowItemSpecModifiers is set.
            /// </summary>
            internal ProjectMetadataInstance SetMetadataObject(string name, string metadataValueEscaped, bool allowItemSpecModifiers)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                _directMetadata ??= new CopyOnWritePropertyDictionary<ProjectMetadataInstance>();
                ProjectMetadataInstance metadatum = new ProjectMetadataInstance(name, metadataValueEscaped, allowItemSpecModifiers /* may not be built-in metadata name */);
                _directMetadata.Set(metadatum);

                return metadatum;
            }

            /// <summary>
            /// Sets metadata where one built-in metadata is allowed to be set: RecursiveDir.
            /// This is not normally legal to set outside of evaluation. However, the CreateItem
            /// needs to be able to set it as a task output, because it supports wildcards. So as a special exception we allow
            /// tasks to set this particular metadata as a task output.
            /// Other built in metadata names are ignored. That's because often task outputs are items that were passed in,
            /// which legally have built-in metadata. If necessary we can calculate it on the new items we're making if requested.
            /// We don't copy them too because tasks shouldn't set them (they might become inconsistent)
            /// </summary>
            internal void SetMetadataOnTaskOutput(string name, string evaluatedValueEscaped)
            {
                ProjectInstance.VerifyThrowNotImmutable(_isImmutable);

                if (!FileUtilities.ItemSpecModifiers.IsDerivableItemSpecModifier(name))
                {
                    _directMetadata ??= new CopyOnWritePropertyDictionary<ProjectMetadataInstance>();
                    ProjectMetadataInstance metadatum = new ProjectMetadataInstance(name, evaluatedValueEscaped, true /* may be built-in metadata name */);
                    _directMetadata.Set(metadatum);
                }
            }

            /// <summary>
            /// Deep clone this into another TaskItem
            /// </summary>
            internal TaskItem DeepClone()
            {
                // When making a deep clone we do not want to add the OriginalItemSpec because it is the same as ItemSpec
                return new TaskItem(this, false);
            }

            /// <summary>
            /// Deep clone this into another TaskItem
            /// </summary>
            internal TaskItem DeepClone(bool isImmutable)
            {
                // When making a deep clone we do not want to add the OriginalItemSpec because it is the same as ItemSpec
                var clone = new TaskItem(this, false);
                clone._isImmutable = isImmutable;

                return clone;
            }

            /// <summary>
            /// Helper to get the value of a built-in metadatum with
            /// the specified name, if any.
            /// If value is not available, returns empty string.
            /// </summary>
            private string GetBuiltInMetadataEscaped(string name)
            {
                string value = String.Empty;

                if (FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name))
                {
                    value = BuiltInMetadata.GetMetadataValueEscaped(_projectDirectory, _includeBeforeWildcardExpansionEscaped, _includeEscaped, _definingFileEscaped, name, ref _fullPath);
                }

                return value;
            }

            /// <summary>
            /// Retrieves the named metadata from the item definition, if any.
            /// If it is not present, returns null.
            /// </summary>
            private ProjectMetadataInstance GetItemDefinitionMetadata(string metadataName)
            {

                // Check any inherited item definition metadata first. It's more like
                // direct metadata, but we didn't want to copy the tables.
                if (_itemDefinitions != null)
                {
                    foreach (ProjectItemDefinitionInstance itemDefinition in _itemDefinitions)
                    {
                        ProjectMetadataInstance metadataFromDefinition = itemDefinition.GetMetadata(metadataName);

                        if (metadataFromDefinition != null)
                        {
                            return metadataFromDefinition;
                        }
                    }
                }

                return null;
            }

            /// <summary>
            /// A class factory for instance model items.
            /// </summary>
            internal class ProjectItemInstanceFactory : IItemFactory<ProjectItemInstance, ProjectItemInstance>
            {
                /// <summary>
                /// The project to which item instances created by this factory will belong.
                /// </summary>
                private ProjectInstance _project;

                /// <summary>
                /// Constructor not taking an item type.
                /// This indicates that the user of this factory should set the item type
                /// on it before using it to create items.
                /// </summary>
                internal ProjectItemInstanceFactory(ProjectInstance project)
                {
                    _project = project;
                }

                /// <summary>
                /// Constructor taking the itemtype for the generated items.
                /// </summary>
                internal ProjectItemInstanceFactory(ProjectInstance project, string itemType)
                    : this(project)
                {
                    ErrorUtilities.VerifyThrowInternalLength(itemType, nameof(itemType));
                    this.ItemType = itemType;
                }

                /// <summary>
                /// The item type that generated items should have
                /// </summary>
                public string ItemType
                {
                    get;
                    set;
                }

                /// <summary>
                /// Sets the item type via the item xml.
                /// Used by the evaluator only.
                /// </summary>
                public ProjectItemElement ItemElement
                {
                    set { ItemType = value.ItemType; }
                }

                /// <summary>
                /// Creates an instance-model item.
                /// </summary>
                /// <param name="include">The include.</param>
                /// <param name="definingProject">The project that defined the item.</param>
                /// <returns>A new instance item.</returns>
                public ProjectItemInstance CreateItem(string include, string definingProject)
                {
                    ErrorUtilities.VerifyThrowInternalLength(ItemType, "ItemType");

                    ProjectItemInstance item = new ProjectItemInstance(_project, ItemType, include, definingProject);

                    return item;
                }

                /// <summary>
                /// Create a ProjectItemInstance, changing the item type but keeping the include.
                /// This is to support the scenario Include="@(i)" where we are copying
                /// metadata.
                /// </summary>
                public ProjectItemInstance CreateItem(ProjectItemInstance source, string definingProject)
                {
                    return CreateItem(source._taskItem.IncludeEscaped, source._taskItem.IncludeBeforeWildcardExpansionEscaped, source, definingProject);
                }

                /// <summary>
                /// Create a ProjectItemInstance, changing the item type and include but retaining the
                /// metadata of the original item.
                /// This is to support this scenario: Include="@(i->'xxx')"
                /// </summary>
                public ProjectItemInstance CreateItem(string includeEscaped, ProjectItemInstance source, string definingProject)
                {
                    return CreateItem(includeEscaped, includeEscaped, source, definingProject);
                }

                /// <summary>
                /// Create a new item from the specified include and include before wildcard expansion.
                /// This is to support the scenario Include="@(i)" where we are creating new items before adding metadata.
                /// </summary>
                public ProjectItemInstance CreateItem(string evaluatedInclude, string evaluatedIncludeBeforeWildcardExpansion, string definingProject)
                {
                    ErrorUtilities.VerifyThrowInternalLength(ItemType, "ItemType");

                    return new ProjectItemInstance(_project, ItemType, evaluatedInclude, evaluatedIncludeBeforeWildcardExpansion, definingProject);
                }

                /// <summary>
                /// Applies the supplied metadata to the destination item.
                /// </summary>
                public void SetMetadata(IEnumerable<Pair<ProjectMetadataElement, string>> metadataList, IEnumerable<ProjectItemInstance> destinationItems)
                {
                    // Set up a single dictionary that can be applied to all the items
                    CopyOnWritePropertyDictionary<ProjectMetadataInstance> metadata = new CopyOnWritePropertyDictionary<ProjectMetadataInstance>(metadataList.FastCountOrZero());
                    foreach (Pair<ProjectMetadataElement, string> metadatum in metadataList)
                    {
                        metadata.Set(new ProjectMetadataInstance(metadatum.Key.Name, metadatum.Value));
                    }

                    foreach (ProjectItemInstance item in destinationItems)
                    {
                        item._taskItem.SetMetadata(metadata);
                    }
                }

                /// <summary>
                /// Create a ProjectItemInstance from another item, changing the item type and include.
                /// </summary>
                private ProjectItemInstance CreateItem(string includeEscaped, string includeBeforeWildcardExpansionEscaped, ProjectItemInstance source, string definingProject)
                {
                    ErrorUtilities.VerifyThrowInternalLength(ItemType, "ItemType");
                    ErrorUtilities.VerifyThrowInternalNull(source, nameof(source));

                    // The new item inherits any metadata originating in item definitions, which
                    // takes precedence over its own item definition metadata.
                    //
                    // Order of precedence:
                    // (1) any directly defined metadata on the source item (passed through)
                    // (2) any item definition metadata the source item had accumulated, in order of accumulation
                    // (3) any item definition metadata associated with the source item's item type
                    // (4) any item definition metadata associated with the destination item's item type (none at this point)
                    // For (2) and (3) combine into a list, (2) on top.
                    List<ProjectItemDefinitionInstance> itemDefinitionsClone = null;
                    if (source._taskItem._itemDefinitions != null)
                    {
                        itemDefinitionsClone ??= new List<ProjectItemDefinitionInstance>(source._taskItem._itemDefinitions.Count + 1);
                        itemDefinitionsClone.AddRange(source._taskItem._itemDefinitions);
                    }

                    ProjectItemDefinitionInstance sourceItemDefinition;
                    if (_project.ItemDefinitions.TryGetValue(source.ItemType, out sourceItemDefinition))
                    {
                        itemDefinitionsClone ??= new List<ProjectItemDefinitionInstance>();
                        itemDefinitionsClone.Add(sourceItemDefinition);
                    }

                    return new ProjectItemInstance(_project, ItemType, includeEscaped, includeBeforeWildcardExpansionEscaped, source._taskItem._directMetadata, itemDefinitionsClone, definingProject);
                }
            }

            /// <summary>
            /// A class factory for task items.
            /// </summary>
            internal class TaskItemFactory : IItemFactory<ProjectItem, TaskItem>, IItemFactory<ProjectItemInstance, TaskItem>
            {
                /// <summary>
                /// The singleton instance.
                /// </summary>
                private static TaskItemFactory s_instance = new TaskItemFactory();

                /// <summary>
                /// Private constructor for singleton creation.
                /// </summary>
                private TaskItemFactory()
                {
                }

                /// <summary>
                /// The item type of items created by this factory.
                /// Since TaskItems don't have an item type, this returns null, and cannot be set.
                /// </summary>
                public string ItemType
                {
                    get { return null; }
                    set { /* ignore */ }
                }

                /// <summary>
                /// The item xml for items in this factory.
                /// </summary>
                public ProjectItemElement ItemElement
                {
                    set { /* ignore */ }
                }

                /// <summary>
                /// The singleton instance. Can be cast to the interface required.
                /// </summary>
                internal static TaskItemFactory Instance
                {
                    get { return s_instance; }
                }

                /// <summary>
                /// Creates a taskitem.
                /// </summary>
                /// <param name="includeEscaped">The include.</param>
                /// <param name="definingProject">The project that defined the item.</param>
                /// <returns>A new instance item.</returns>
                public TaskItem CreateItem(string includeEscaped, string definingProject)
                {
                    return new TaskItem(includeEscaped, definingProject);
                }

                /// <summary>
                /// Creates a task item from a ProjectItem
                /// </summary>
                public TaskItem CreateItem(ProjectItem source, string definingProject)
                {
                    TaskItem item = CreateItem(((IItem)source).EvaluatedIncludeEscaped, source, definingProject);

                    return item;
                }

                /// <summary>
                /// Creates a task item from a ProjectItem but changing the itemspec
                /// </summary>
                public TaskItem CreateItem(string includeEscaped, ProjectItem baseItem, string definingProject)
                {
                    TaskItem item = new TaskItem(includeEscaped, definingProject);

                    foreach (ProjectMetadata metadatum in baseItem.Metadata)
                    {
                        item.SetMetadata(metadatum.Name, metadatum.EvaluatedValueEscaped);
                    }

                    return item;
                }

                /// <summary>
                /// Create a task item from a ProjectItemInstance.
                /// </summary>
                public TaskItem CreateItem(ProjectItemInstance source, string definingProject)
                {
                    TaskItem item = CreateItem(((IItem)source).EvaluatedIncludeEscaped, source, definingProject);

                    return item;
                }

                /// <summary>
                /// Creates a task item from a ProjectItem
                /// </summary>
                public TaskItem CreateItem(string includeEscaped, ProjectItemInstance baseItem, string definingProject)
                {
                    TaskItem item = new TaskItem(baseItem);

                    if (Path.DirectorySeparatorChar != '\\' && includeEscaped?.IndexOf('\\') > -1)
                    {
                        includeEscaped = includeEscaped.Replace('\\', '/');
                    }

                    item.IncludeEscaped = includeEscaped;

                    return item;
                }

                /// <summary>
                /// Creates a task item using the specified include and include before wildcard expansion.
                /// </summary>
                public TaskItem CreateItem(string includeEscaped, string includeBeforeWildcardExpansionEscaped, string definingProject)
                {
                    return CreateItem(includeEscaped, definingProject);
                }

                /// <summary>
                /// Applies the supplied metadata to the destination item.
                /// </summary>
                public void SetMetadata(IEnumerable<Pair<ProjectMetadataElement, string>> metadata, IEnumerable<TaskItem> destinationItems)
                {
                    // Not difficult to implement, but we do not expect to go here.
                    ErrorUtilities.ThrowInternalErrorUnreachable();
                }
            }

            /// <summary>
            /// Implementation of IMetadataTable that can be passed to expander to expose only built-in metadata on this item.
            /// Built-in metadata is stored in a separate table so it can be cleared out when the item is renamed, as this invalidates the values.
            /// Also, more importantly, because typically the same regular metadata values can be shared by many items,
            /// and keeping item-specific metadata out of it could allow it to be implemented as a copy-on-write table.
            /// </summary>
            private class BuiltInMetadataTable : IMetadataTable
            {
                /// <summary>
                /// Item type
                /// </summary>
                private string _itemType;

                /// <summary>
                /// Backing item
                /// </summary>
                private TaskItem _item;

                /// <summary>
                /// Constructor.
                /// </summary>
                internal BuiltInMetadataTable(string itemType, TaskItem item)
                {
                    _itemType = itemType;
                    _item = item;
                }

                /// <summary>
                /// Retrieves any value we have in our metadata table for the metadata name specified.
                /// If no value is available, returns empty string.
                /// </summary>
                public string GetEscapedValue(string name)
                {
                    string value = _item.GetBuiltInMetadataEscaped(name);
                    return value;
                }

                /// <summary>
                /// Retrieves any value we have in our metadata table for the metadata name and item type specified.
                /// If item type is null, it is ignored.
                /// If no value is available, returns empty string.
                /// </summary>
                public string GetEscapedValue(string requiredItemType, string name)
                {
                    string value = GetEscapedValueIfPresent(requiredItemType, name);

                    return value ?? String.Empty;
                }

                /// <summary>
                /// Returns the value if it exists, null otherwise.
                /// If item type is null, it is ignored.
                /// </summary>
                public string GetEscapedValueIfPresent(string requiredItemType, string name)
                {
                    string value = null;

                    if ((requiredItemType == null) || MSBuildNameIgnoreCaseComparer.Default.Equals(_itemType, requiredItemType))
                    {
                        value = GetEscapedValue(name);
                    }

                    return value;
                }
            }
        }

        /// <summary>
        /// Implementation of a comparer that determines equality between ProjectItemInstances
        /// </summary>
        internal class ProjectItemInstanceEqualityComparer : IEqualityComparer<ProjectItemInstance>
        {
            /// <summary>
            /// The singleton comparer.
            /// </summary>
            private static ProjectItemInstanceEqualityComparer s_comparer = new ProjectItemInstanceEqualityComparer();

            /// <summary>
            /// Constructor.
            /// </summary>
            private ProjectItemInstanceEqualityComparer()
            {
            }

            /// <summary>
            /// Returns the default comparer instance.
            /// </summary>
            public static IEqualityComparer<ProjectItemInstance> Default
            {
                get { return s_comparer; }
            }

            /// <summary>
            /// Implemtnation of IEqualityComparer.Equals.
            /// </summary>
            /// <param name="x">The left hand side.</param>
            /// <param name="y">The right hand side.</param>
            /// <returns>True of the instances are equivalent, false otherwise.</returns>
            public bool Equals(ProjectItemInstance x, ProjectItemInstance y)
            {
                return x._taskItem.Equals(y._taskItem);
            }

            /// <summary>
            /// Implementation of IEqualityComparer.GetHashCode.
            /// </summary>
            /// <param name="obj">The item instance.</param>
            /// <returns>The hash code of the instance.</returns>
            public int GetHashCode(ProjectItemInstance obj)
            {
                return obj._taskItem.GetHashCode();
            }
        }
    }
}
