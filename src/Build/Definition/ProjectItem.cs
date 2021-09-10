// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.ObjectModelRemoting;
using Microsoft.Build.Shared;
using System.Collections.Generic;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Build.Shared.FileSystem;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// An evaluated design-time item
    /// </summary>
    /// <remarks>
    /// Edits to this object will indirectly dirty the containing project because they will modify the backing XML.
    /// </remarks>
    /// <comment>
    /// We cannot use a copy-on-write table for the metadata, as ProjectMetadata objects are mutable. However,
    /// we do use it for build-time items.
    /// </comment>
    [DebuggerDisplay("{ItemType}={EvaluatedInclude} [{UnevaluatedInclude}] #DirectMetadata={DirectMetadataCount}")]
    public class ProjectItem : IItem<ProjectMetadata>, IProjectMetadataParent
    {
        /// <summary>
        /// Project that this item lives in.
        /// ProjectItems always live in a project.
        /// Used to get item definitions and project directory.
        /// </summary>
        private readonly Project _project;

        /// <summary>
        /// Fragment of the original include that led to this item,
        /// with properties expanded but not wildcards.  Escaped as necessary
        /// </summary>
        /// <remarks>
        /// This is ONLY used to figure out %(RecursiveDir) when it is requested.
        /// It's likely too expensive to figure that out if it isn't needed, so we store 
        /// the necessary material here.
        /// </remarks>
        private readonly string _evaluatedIncludeBeforeWildcardExpansionEscaped;

        /// <summary>
        /// Item definitions are stored in one single table shared by all items of a particular item type.
        /// 
        /// When an item is created from another item, such as by using an expression like Include="@(x)",
        /// any item definition metadata those source items have must override any item definition metadata 
        /// associated with the new item type. 
        /// 
        /// Copying all those item definition metadata into real metadata on this item would be very inefficient, because
        /// it would turn a single shared table into a separate table for every item.
        /// 
        /// Instead, we get a reference to the item definition of the source items, and consult
        /// that table before we consult our own item type's item definition. Since item definitions can't change at this point,
        /// it's safe to reference their original table.
        /// 
        /// If our item gets copied again, we need a reference to the inherited item definition and we need the real item
        /// definition of the source items. Thus a list is created. On copying, a list is created, beginning with a clone
        /// of any list the source item had, and ending with the item definition list of the source item type.
        /// 
        /// When we look up a metadata value we look at 
        /// (1) directly associated metadata and built-in metadata
        /// (2) the inherited item definition list, starting from the top
        /// (3) the item definition associated with our item type
        /// </summary>
        private readonly List<ProjectItemDefinition> _inheritedItemDefinitions;

        /// <summary>
        /// Backing XML item.
        /// Can never be null
        /// </summary>
        private ProjectItemElement _xml;

        /// <summary>
        /// Evaluated include.
        /// The original XML may have evaluated to several of these items,
        /// each with a different include.
        /// May be empty, for example from expanding an empty list or from a transform with undefined metadata.
        /// Escaped as necessary
        /// </summary>
        private string _evaluatedIncludeEscaped;

        /// <summary>
        /// Collection of metadata that link the XML metadata and evaluated metadata.
        /// Since evaluation has occurred, this is an unordered collection.
        /// May be null.
        /// </summary>
        /// <remarks>
        /// Lazily created, as there are lots of items
        /// that have no metadata at all.
        /// </remarks>
        private PropertyDictionary<ProjectMetadata> _directMetadata;

        /// <summary>
        /// Cached value of the fullpath metadata. All other metadata are computed on demand.
        /// </summary>
        private string _fullPath;

        /// <summary>
        /// External projects support
        /// </summary>
        internal ProjectItem(ProjectItemElement xml, Project project)
        {
            this._project = project;
            this._xml = xml;
        }

        /// <summary>
        /// Called by the Evaluator during project evaluation.
        /// Direct metadata may be null, indicating no metadata. It is assumed to have already been cloned.
        /// Inherited item definition metadata may be null. It is assumed that its list has already been cloned.
        /// ProjectMetadata objects may be shared with other items.
        /// </summary>
        internal ProjectItem(
                             Project project,
                             ProjectItemElement xml,
                             string evaluatedIncludeEscaped,
                             string evaluatedIncludeBeforeWildcardExpansionEscaped,
                             PropertyDictionary<ProjectMetadata> directMetadataCloned,
                             List<ProjectItemDefinition> inheritedItemDefinitionsCloned
                            )
        {
            ErrorUtilities.VerifyThrowInternalNull(project, nameof(project));
            ErrorUtilities.VerifyThrowArgumentNull(xml, nameof(xml));

            // Orcas accidentally allowed empty includes if they resulted from expansion: we preserve that bug
            ErrorUtilities.VerifyThrowArgumentNull(evaluatedIncludeEscaped, nameof(evaluatedIncludeEscaped));
            ErrorUtilities.VerifyThrowArgumentNull(evaluatedIncludeBeforeWildcardExpansionEscaped, nameof(evaluatedIncludeBeforeWildcardExpansionEscaped));

            _xml = xml;
            _project = project;
            _evaluatedIncludeEscaped = evaluatedIncludeEscaped;
            _evaluatedIncludeBeforeWildcardExpansionEscaped = evaluatedIncludeBeforeWildcardExpansionEscaped;
            _directMetadata = directMetadataCloned;
            _inheritedItemDefinitions = inheritedItemDefinitionsCloned;
        }

        internal virtual ProjectItemLink Link => null;

        /// <summary>
        /// Backing XML item.
        /// Can never be null.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public ProjectItemElement Xml
        {
            [DebuggerStepThrough]
            get
            { return _xml; }
        }

        /// <summary>
        /// Gets or sets the type of this item.
        /// </summary>
        public string ItemType
        {
            [DebuggerStepThrough]
            get
            { return _xml.ItemType; }
            set
            {
                if (Link != null)
                {
                    Link.ChangeItemType(value);
                }
                else
                {
                    ChangeItemType(value);
                }
            }
        }

        /// <summary>
        /// Gets or sets the unevaluated value of the Include.
        /// </summary>
        public string UnevaluatedInclude
        {
            [DebuggerStepThrough]
            get
            { return _xml.Include; }
            set { Rename(value); }
        }

        /// <summary>
        /// Gets the evaluated value of the include, unescaped. 
        /// </summary>
        public string EvaluatedInclude
        {
            [DebuggerStepThrough]
            get
            { return Link != null ? Link.EvaluatedInclude : EscapingUtilities.UnescapeAll(_evaluatedIncludeEscaped); }
        }

        /// <summary>
        /// Gets the evaluated value of the include, escaped as necessary.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        string IItem.EvaluatedIncludeEscaped
        {
            [DebuggerStepThrough]
            get
            { return _evaluatedIncludeEscaped; }
        }

        /// <summary>
        /// The directory of the project being built
        /// Never null: If there is no project filename yet, it will use the current directory
        /// </summary>
        string IItem.ProjectDirectory
        {
            get { return _project.DirectoryPath; }
        }

        /// <summary>
        /// Project that this item lives in.
        /// ProjectItems always live in a project.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public Project Project
        {
            [DebuggerStepThrough]
            get
            { return _project; }
        }

        /// <summary>
        /// If the item originated in an imported file, returns true.
        /// Otherwise returns false.
        /// </summary>
        public bool IsImported
        {
            get
            {
                bool isImported = !Object.ReferenceEquals(_xml.ContainingProject, _project.Xml);

                return isImported;
            }
        }

        /// <summary>
        /// Metadata directly on the item, if any.
        /// Does not include metadata from item definitions.
        /// Does not include built-in metadata.
        /// Never returns null.
        /// </summary>
        public IEnumerable<ProjectMetadata> DirectMetadata
        {
            get { return Link != null ? Link.DirectMetadata : (IEnumerable<ProjectMetadata>)_directMetadata ?? (IEnumerable<ProjectMetadata>)ReadOnlyEmptyCollection<ProjectMetadata>.Instance; }
        }

        /// <summary>
        /// Count of direct metadata on this item, if any.
        /// Does NOT count any metadata inherited from item definitions.
        /// Does not count built-in metadata, such as "FullPath".
        /// </summary>
        public int DirectMetadataCount
        {
            [DebuggerStepThrough]
            get
            { return Link != null ? Link.DirectMetadata.Count  : _directMetadata != null ? _directMetadata.Count : 0; }
        }

        /// <summary>
        /// Metadata on the item, if any.  Includes metadata specified by the definition, if any.
        /// If there is no metadata, returns an empty collection.
        /// Does not include built-in metadata, such as "FullPath".
        /// Get the values of built-in metadata using <see cref="GetMetadataValue(string)"/>.
        /// This is a read-only collection. To modify the metadata, use <see cref="SetMetadataValue(string, string)"/>.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public ICollection<ProjectMetadata> Metadata
        {
            [DebuggerStepThrough]
            get
            { return Link != null ? Link.MetadataCollection : MetadataCollection; }
        }

        IEnumerable<ProjectMetadata> IItem<ProjectMetadata>.Metadata => Metadata;

        /// <summary>
        /// Count of metadata on this item, if any.
        /// Includes any metadata inherited from item definitions.
        /// Includes both custom and built-in metadata.
        /// </summary>
        public int MetadataCount
        {
            [DebuggerStepThrough]
            get
            { return Metadata.Count + FileUtilities.ItemSpecModifiers.All.Length; }
        }

        /// <summary>
        /// Implementation of IKeyed exposing the item type, so items
        /// can be put in a dictionary conveniently.
        /// </summary>
        string IKeyed.Key
        {
            [DebuggerStepThrough]
            get
            { return ItemType; }
        }

        /// <summary>
        /// Internal version of <see cref="Metadata">Metadata</see> that returns
        /// a full ICollection.
        /// Unordered collection of evaluated metadata on the item.
        /// If there is no metadata, returns an empty collection.
        /// Does not include built-in metadata.
        /// Includes any from item definitions not masked by directly set metadata.
        /// This is a read-only collection. To modify the metadata, use <see cref="SetMetadataValue(string, string)"/>.
        /// </summary>
        internal ICollection<ProjectMetadata> MetadataCollection
        {
            get
            {
                RetrievableEntryHashSet<ProjectMetadata> allMetadata = new RetrievableEntryHashSet<ProjectMetadata>(MSBuildNameIgnoreCaseComparer.Default);

                // Lowest priority: regular item definitions
                ProjectItemDefinition itemDefinition;
                if (_project.ItemDefinitions.TryGetValue(ItemType, out itemDefinition))
                {
                    foreach (ProjectMetadata metadataFromDefinition in itemDefinition.Metadata)
                    {
                        allMetadata[metadataFromDefinition.Name] = metadataFromDefinition;
                    }
                }

                // Next, any inherited item definitions. Front of the list is highest priority,
                // so walk backwards.
                if (_inheritedItemDefinitions != null)
                {
                    for (int i = _inheritedItemDefinitions.Count - 1; i >= 0; i--)
                    {
                        foreach (ProjectMetadata metadatum in _inheritedItemDefinitions[i].Metadata)
                        {
                            allMetadata[metadatum.Name] = metadatum;
                        }
                    }
                }

                // Finally any direct metadata win.
                if (_directMetadata != null)
                {
                    foreach (ProjectMetadata metadatum in _directMetadata)
                    {
                        allMetadata[metadatum.Name] = metadatum;
                    }
                }

                return allMetadata.Values;
            }
        }

        /// <summary>
        /// Accesses the unescaped evaluated include prior to wildcard expansion
        /// </summary>
        internal string EvaluatedIncludeBeforeWildcardExpansion
        {
            [DebuggerStepThrough]
            get
            { return EscapingUtilities.UnescapeAll(_evaluatedIncludeBeforeWildcardExpansionEscaped); }
        }

        /// <summary>
        /// Accesses the evaluated include prior to wildcard expansion
        /// </summary>
        internal string EvaluatedIncludeBeforeWildcardExpansionEscaped
        {
            [DebuggerStepThrough]
            get
            { return _evaluatedIncludeBeforeWildcardExpansionEscaped; }
        }

        /// <summary>
        /// Accesses the inherited item definitions, if any.
        /// Used ONLY by the ProjectInstance, when cloning a ProjectItem.
        /// </summary>
        internal List<ProjectItemDefinition> InheritedItemDefinitions
        {
            [DebuggerStepThrough]
            get
            { return _inheritedItemDefinitions; }
        }

        /// <summary>
        /// Gets an evaluated metadata on this item.
        /// Potentially includes a metadata from an item definition.
        /// Does not return built-in metadata, such as "FullPath".
        /// Returns null if not found.
        /// </summary>
        public ProjectMetadata GetMetadata(string name)
        {
            if (Link != null)
            {
                return Link.GetMetadata(name);
            }

            ErrorUtilities.VerifyThrowArgumentLength(name, nameof(name));

            ProjectMetadata result = null;

            if (_directMetadata != null)
            {
                result = _directMetadata[name];
            }

            if (result == null)
            {
                result = GetItemDefinitionMetadata(name);
            }

            return result;
        }

        /// <summary>
        /// Get the evaluated value of a metadata on this item, possibly from an item definition. 
        /// Returns empty string if it does not exist.
        /// To determine whether a piece of metadata does not exist vs. simply has no value, use <see cref="HasMetadata(string)">HasMetadata</see>.
        /// May be used to access the value of built-in metadata, such as "FullPath".
        /// Attempting to get built-in metadata on a value that is not a valid path throws InvalidOperationException.
        /// </summary>
        public string GetMetadataValue(string name)
        {
            return Link != null ? Link.GetMetadataValue(name) : EscapingUtilities.UnescapeAll(((IItem)this).GetMetadataValueEscaped(name));
        }

        /// <summary>
        /// Returns true if a particular piece of metadata is defined on this item,
        /// otherwise false.
        /// Includes built-in metadata and metadata inherited from item definitions.
        /// </summary>
        public bool HasMetadata(string name)
        {
            if (Link != null)
            {
                return Link.HasMetadata(name);
            }

            if (_directMetadata?.Contains(name) == true)
            {
                return true;
            }

            if (FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name))
            {
                return true;
            }

            ProjectMetadata metadatum = GetItemDefinitionMetadata(name);
            if (metadatum != null)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// See <see cref="GetMetadataValue(string)">GetMetadataValue</see> for a more detailed explanation.  
        /// Returns the escaped value of the metadatum requested.  
        /// </summary>
        string IItem.GetMetadataValueEscaped(string name)
        {
            ErrorUtilities.VerifyThrowArgumentLength(name, nameof(name));

            string value = null;

            if (_directMetadata != null)
            {
                ProjectMetadata metadatum = _directMetadata[name];
                if (metadatum != null)
                {
                    value = metadatum.EvaluatedValueEscaped;
                }
            }

            if (value == null)
            {
                value = GetBuiltInMetadataEscaped(name);
            }

            if (value == null)
            {
                ProjectMetadata metadatum = GetItemDefinitionMetadata(name);

                if (metadatum != null && Expander<ProjectProperty, ProjectItem>.ExpressionMayContainExpandableExpressions(metadatum.EvaluatedValueEscaped))
                {
                    Expander<ProjectProperty, ProjectItem> expander = new Expander<ProjectProperty, ProjectItem>(null, null, new BuiltInMetadataTable(this));

                    value = expander.ExpandIntoStringLeaveEscaped(metadatum.EvaluatedValueEscaped, ExpanderOptions.ExpandBuiltInMetadata, metadatum.Location);
                }
                else if (metadatum != null)
                {
                    return metadatum.EvaluatedValueEscaped;
                }
            }

            return value ?? String.Empty;
        }

        /// <summary>
        /// Gets any existing ProjectMetadata on the item, or
        /// else any on an applicable item definition.
        /// This is ONLY called during evaluation.
        /// Does not return built-in metadata, such as "FullPath".
        /// Returns null if not found.
        /// </summary>
        ProjectMetadata IItem<ProjectMetadata>.GetMetadata(string name)
        {
            return GetMetadata(name);
        }

        /// <summary>
        /// Adds a ProjectMetadata to the item. 
        /// This is ONLY called during evaluation and does not affect the XML.
        /// </summary>
        ProjectMetadata IItem<ProjectMetadata>.SetMetadata(ProjectMetadataElement metadataElement, string evaluatedInclude)
        {
            _directMetadata ??= new PropertyDictionary<ProjectMetadata>();

            ProjectMetadata predecessor = GetMetadata(metadataElement.Name);

            ProjectMetadata metadatum = new ProjectMetadata(this, metadataElement, evaluatedInclude, predecessor);

            _directMetadata.Set(metadatum);

            return metadatum;
        }

        /// <summary>
        /// Adds metadata with the specified name and value to the item.
        /// Updates an existing metadata if one already exists with the same name on the item directly, as opposed to inherited from an item definition.
        /// Updates the evaluated project, but does not affect anything else in the project until reevaluation. For example,
        /// if a piece of metadata named "m" is added on item of type "i", it does not affect "j" which is evaluated from "@(j->'%(m)')" until reevaluation.
        /// Also if the unevaluated value of "m" is set to something that is modified by evaluation, such as "$(p)", the evaluated value will be set to literally "$(p)" until reevaluation.
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state without a reevaluation.
        /// Returns the new or existing metadatum.
        /// </summary>
        /// <remarks>Unevaluated value is assumed to be escaped as necessary</remarks>
        public ProjectMetadata SetMetadataValue(string name, string unevaluatedValue)
        {
            return Link != null ? Link.SetMetadataValue(name, unevaluatedValue, false) :
                                  SetMetadataOperation(name, unevaluatedValue, propagateMetadataToSiblingItems: false);
        }

        /// <summary>
        /// Overload of <see cref="SetMetadataValue(string,string)"/>. Adds the option of not splitting the item element and thus affecting all sibling items.
        /// Sibling items are defined as all ProjectItem instances that were created from the same item element.
        /// 
        /// This is a convenience that it is understood does not necessarily leave the project in a perfectly self consistent state without a reevaluation
        /// </summary>
        /// /// <param name="name">Metadata name</param>
        /// <param name="unevaluatedValue">Metadata value</param>
        /// <param name="propagateMetadataToSiblingItems">
        /// If true, adds direct metadata to the <see cref="ProjectItemElement"/> from which this <see cref="ProjectItem"/> originated. The intent is to affect all other sibling items.
        /// </param>
        /// <returns>Returns the new or existing metadatum.</returns>
        public ProjectMetadata SetMetadataValue(string name, string unevaluatedValue, bool propagateMetadataToSiblingItems)
        {
            return Link != null ? Link.SetMetadataValue(name, unevaluatedValue, propagateMetadataToSiblingItems) :
                                  SetMetadataOperation(name, unevaluatedValue, propagateMetadataToSiblingItems: propagateMetadataToSiblingItems);
        }

        private ProjectMetadata SetMetadataOperation(string name, string unevaluatedValue, bool propagateMetadataToSiblingItems)
        {
            Project.VerifyThrowInvalidOperationNotImported(_xml.ContainingProject);

            XmlUtilities.VerifyThrowArgumentValidElementName(name);
            ErrorUtilities.VerifyThrowArgument(!FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name), "ItemSpecModifierCannotBeCustomMetadata", name);
            ErrorUtilities.VerifyThrowInvalidOperation(!XMakeElements.ReservedItemNames.Contains(name), "CannotModifyReservedItemMetadata", name);
            ErrorUtilities.VerifyThrowInvalidOperation(_xml.Parent?.Parent != null, "OM_ObjectIsNoLongerActive");

            if (!propagateMetadataToSiblingItems)
            {
                _project.SplitItemElementIfNecessary(_xml);
            }

            ProjectMetadata metadatum;

            if (_directMetadata?.Contains(name) == true)
            {
                metadatum = _directMetadata[name];
                metadatum.UnevaluatedValue = unevaluatedValue;
            }
            else
            {
                ProjectMetadataElement metadatumXml = _xml.AddMetadata(name, unevaluatedValue);

                string evaluatedValueEscaped = _project.ExpandMetadataValueBestEffortLeaveEscaped(this, unevaluatedValue, metadatumXml.Location);

                metadatum = new ProjectMetadata(this, metadatumXml, evaluatedValueEscaped, null /* predecessor unknown */);
            }

            if (!propagateMetadataToSiblingItems)
            {
                _directMetadata ??= new PropertyDictionary<ProjectMetadata>();
                _directMetadata.Set(metadatum);
            }
            else
            {
                var siblingItems = _project.Items.Where(i => i._xml == _xml);

                foreach (var siblingItem in siblingItems)
                {
                    siblingItem._directMetadata ??= new PropertyDictionary<ProjectMetadata>();
                    siblingItem._directMetadata.Set(metadatum.DeepClone());
                }
            }

            return metadatum;
        }

        /// <summary>
        /// Removes any metadata with the specified name.
        /// Returns true if the evaluated metadata existed, otherwise false.
        /// If the metadata name is one of the built-in metadata, like "FullPath", throws InvalidArgumentException.
        /// If the metadata originates in an item definition, and was not overridden, throws InvalidOperationException.
        /// </summary>
        public bool RemoveMetadata(string name)
        {
            if (Link != null)
            {
                return Link.RemoveMetadata(name);
            }

            ErrorUtilities.VerifyThrowArgumentLength(name, nameof(name));
            ErrorUtilities.VerifyThrowArgument(!FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name), "ItemSpecModifierCannotBeCustomMetadata", name);
            Project.VerifyThrowInvalidOperationNotImported(_xml.ContainingProject);
            ErrorUtilities.VerifyThrowInvalidOperation(_xml.Parent?.Parent != null, "OM_ObjectIsNoLongerActive");

            ProjectMetadata metadatum = _directMetadata?[name];

            if (metadatum == null)
            {
                ProjectMetadata itemDefinitionMetadata = GetItemDefinitionMetadata(name);
                ErrorUtilities.VerifyThrowInvalidOperation(itemDefinitionMetadata == null, "OM_CannotRemoveMetadataOriginatingFromItemDefinition", name);
                return false;
            }

            _project.SplitItemElementIfNecessary(_xml);

            // New metadata objects may have been created
            metadatum = _directMetadata[name];

            _xml.RemoveChild(metadatum.Xml);
            _directMetadata.Remove(name);

            return true;
        }

        /// <summary>
        /// Renames the item.
        /// Equivalent to setting the <see cref="UnevaluatedInclude"/> value.
        /// Generally, no expansion occurs. This is because it would potentially result in several items, 
        /// which is not meaningful semantics when renaming a single item.
        /// However if the item does not need to be split (which would invalidate its ProjectItemElement),
        /// and the new value expands to exactly one item, then its evaluated include is updated
        /// with the expanded value, rather than the unexpanded value.
        /// </summary>
        /// <remarks>
        /// Even if the new value expands to zero items, we do not expand it.
        /// The common case we are interested in for expansion here is setting something 
        /// like "$(sourcesroot)\foo.cs" and expanding that to a single item. 
        /// If say "@(foo)" is set as the new name, and it expands to blank, that might 
        /// be surprising to the host and maybe even unhandled, if on full reevaluation 
        /// it wouldn’t expand to blank. That’s why we're being cautious and supporting 
        /// the most common scenario only. 
        /// Many hosts will do a ReevaluateIfNecessary before reading anyway.
        /// </remarks>
        public void Rename(string name)
        {
            if (Link != null)
            {
                Link.Rename(name);
                return;
            }

            Project.VerifyThrowInvalidOperationNotImported(_xml.ContainingProject);
            ErrorUtilities.VerifyThrowInvalidOperation(_xml.Parent?.Parent != null, "OM_ObjectIsNoLongerActive");

            if (String.Equals(UnevaluatedInclude, name, StringComparison.Ordinal))
            {
                return;
            }

            _fullPath = null; // Clear cached value

            if (_xml.Count == 0 /* no metadata */ && _project.IsSuitableExistingItemXml(_xml, name, null /* no metadata */) && !FileMatcher.HasWildcardsSemicolonItemOrPropertyReferences(name))
            {
                _evaluatedIncludeEscaped = name;

                // Fast item lookup tables are invalid now.
                // Make sure that when the caller invokes ReevaluateIfNecessary() that they'll be refreshed.
                Project.MarkDirty();
                return;
            }

            bool splitOccurred = _project.SplitItemElementIfNecessary(_xml);

            _xml.Include = name;

            if (splitOccurred)
            {
                _evaluatedIncludeEscaped = name;
            }
            else
            {
                _evaluatedIncludeEscaped = _project.ExpandItemIncludeBestEffortLeaveEscaped(_xml);
            }
        }

        #region IMetadataTable Members

        /// <summary>
        /// Retrieves any value we have in our metadata table for the metadata name specified.
        /// If no value is available, returns empty string.
        /// Value, if escaped, remains escaped.
        /// </summary>
        string IMetadataTable.GetEscapedValue(string name)
        {
            string value = ((IMetadataTable)this).GetEscapedValue(null, name);

            return value;
        }

        /// <summary>
        /// Retrieves any value we have in our metadata table for the metadata name and item type specified.
        /// If no value is available, returns empty string.
        /// If item type is null, it is ignored, otherwise it must match.
        /// Value, if escaped, remains escaped.
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
        /// Value, if escaped, remains escaped.
        /// </summary>
        string IMetadataTable.GetEscapedValueIfPresent(string itemType, string name)
        {
            if (itemType == null || MSBuildNameIgnoreCaseComparer.Default.Equals(ItemType, itemType))
            {
                string value = ((IItem)this).GetMetadataValueEscaped(name);

                if (value.Length > 0 || HasMetadata(name))
                {
                    return value;
                }
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Changes the item type of this item.
        /// Until reevaluation puts it in the correct place, it will be placed at
        /// the end of the list of items of its new type.
        /// </summary>
        /// <remarks>
        /// This is a little involved, as it requires replacing
        /// the XmlElement, and updating the project's datastructures.
        /// </remarks>
        internal void ChangeItemType(string newItemType)
        {
            ErrorUtilities.VerifyThrowArgumentLength(newItemType, "ItemType");
            Project.VerifyThrowInvalidOperationNotImported(_xml.ContainingProject);
            ErrorUtilities.VerifyThrowInvalidOperation(_xml.Parent?.Parent != null, "OM_ObjectIsNoLongerActive");

            if (String.Equals(ItemType, newItemType, StringComparison.Ordinal))
            {
                return;
            }

            _project.SplitItemElementIfNecessary(_xml);

            _project.RemoveItemBeforeItemTypeChange(this);

            // xml.ChangeItemType will throw if new item type is invalid. Make sure we re-add the item anyway
            try
            {
                _xml.ChangeItemType(newItemType);
            }
            finally
            {
                _project.ReAddExistingItemAfterItemTypeChange(this);
            }
        }

        /// <summary>
        /// Creates new xml objects for itself, disconnecting from the old xml objects.
        /// Called ONLY by <see cref="Microsoft.Build.Evaluation.Project.SplitItemElementIfNecessary(ProjectItemElement)"/>
        /// </summary>
        /// <remarks>
        /// Called when breaking up a single ProjectItemElement that evaluates into several ProjectItems.
        /// </remarks>
        internal void SplitOwnItemElement()
        {
            ProjectItemElement oldXml = _xml;

            _xml = _xml.ContainingProject.CreateItemElement(ItemType, ((IItem)this).EvaluatedIncludeEscaped);

            oldXml.Parent.InsertBeforeChild(_xml, oldXml);

            if (_directMetadata == null)
            {
                return;
            }

            // ProjectMetadata objects may be being shared with other ProjectItem objects, 
            // or originate from item definitions, so it is necessary to replace ours with
            // new ones.
            List<ProjectMetadata> temporary = new List<ProjectMetadata>(_directMetadata.Count);

            foreach (ProjectMetadata metadatum in _directMetadata)
            {
                temporary.Add(metadatum);
            }

            _directMetadata = new PropertyDictionary<ProjectMetadata>(_directMetadata.Count);

            foreach (ProjectMetadata metadatum in temporary)
            {
                SetMetadataValue(metadatum.Name, metadatum.EvaluatedValueEscaped);
            }
        }

        /// <summary>
        /// Helper to get the value of a built-in metadatum with
        /// the specified name, if any.
        /// </summary>
        private string GetBuiltInMetadataEscaped(string name)
        {
            string value = null;

            if (FileUtilities.ItemSpecModifiers.IsItemSpecModifier(name))
            {
                value = BuiltInMetadata.GetMetadataValueEscaped(_project.DirectoryPath, _evaluatedIncludeBeforeWildcardExpansionEscaped, _evaluatedIncludeEscaped, this.Xml.ContainingProject.FullPath, name, ref _fullPath);
            }

            return value;
        }

        /// <summary>
        /// Retrieves the named metadata from the item definition, if any.
        /// If it is not present, returns null
        /// </summary>
        /// <param name="name">The metadata name.</param>
        /// <returns>The value if it exists, null otherwise.</returns>
        private ProjectMetadata GetItemDefinitionMetadata(string name)
        {
            ProjectMetadata metadataFromDefinition = null;

            // Check any inherited item definition metadata first. It's more like
            // direct metadata, but we didn't want to copy the tables.
            if (_inheritedItemDefinitions != null)
            {
                foreach (ProjectItemDefinition inheritedItemDefinition in _inheritedItemDefinitions)
                {
                    metadataFromDefinition = inheritedItemDefinition.GetMetadata(name);

                    if (metadataFromDefinition != null)
                    {
                        return metadataFromDefinition;
                    }
                }
            }

            // Now try regular item definition metadata for this item type.
            ProjectItemDefinition itemDefinition;
            if (_project.ItemDefinitions.TryGetValue(ItemType, out itemDefinition))
            {
                metadataFromDefinition = itemDefinition.GetMetadata(name);
            }

            return metadataFromDefinition;
        }

        /// <summary>
        /// A class factory for ProjectItems.
        /// </summary>
        internal class ProjectItemFactory : IItemFactory<ProjectItem, ProjectItem>
        {
            /// <summary>
            /// The Project with which each item should be associated.
            /// </summary>
            private readonly Project _project;

            /// <summary>
            /// The project item's XML
            /// </summary>
            private ProjectItemElement _xml;

            /// <summary>
            /// Creates an item factory which does not specify an item xml.  The item xml must
            /// be specified later.
            /// </summary>
            /// <param name="project">The project for items generated.</param>
            internal ProjectItemFactory(Project project)
            {
                _project = project;
            }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="project">The project for items generated.</param>
            /// <param name="xml">The xml for items generated.</param>
            internal ProjectItemFactory(Project project, ProjectItemElement xml)
            {
                _project = project;
                _xml = xml;
            }

            /// <summary>
            /// Item type that items created by this factory will have.
            /// </summary>
            public string ItemType
            {
                get { return _xml.ItemType; }
                set { ErrorUtilities.ThrowInternalError("Cannot change the item type on ProjectItem.ProjectItemFactory"); }
            }

            /// <summary>
            /// Set the item xml from which items will be created.
            /// Used by the evaluator only.
            /// </summary>
            public ProjectItemElement ItemElement
            {
                set { _xml = value; }
            }

            /// <summary>
            /// Creates an item with the specified type and evaluated include.
            /// Used for making items from "just strings" and from expressions like "@(Compile, ';')"
            /// </summary>
            /// <param name="include">The include.</param>
            /// <param name="definingProject">The path to the project that defined the item.</param>
            /// <returns>A new project item.</returns>
            /// <comments>
            /// NOTE: defining project is ignored because we already know the ItemElement associated with 
            /// this item, and use that for where it is defined. 
            /// </comments>
            public ProjectItem CreateItem(string include, string definingProject)
            {
                return CreateItem(include, include, definingProject);
            }

            /// <summary>
            /// Creates an item based on the provided item, but with
            /// the project and xml of this factory. Metadata is cloned,
            /// but continues to point to the original ProjectMetadataElement objects.
            /// This is to support the scenario Include="@(i)" where we are copying
            /// metadata, and are happy to see changes in the original metadata, but
            /// setting metadata should create new XML.
            /// </summary>
            /// <comments>
            /// NOTE: defining project is ignored because we already know the ItemElement associated with 
            /// this item, and use that for where it is defined. 
            /// </comments>
            public ProjectItem CreateItem(ProjectItem source, string definingProject)
            {
                return CreateItem(source._evaluatedIncludeEscaped, source._evaluatedIncludeBeforeWildcardExpansionEscaped, source);
            }

            /// <summary>
            /// Creates an item based on the provided item, but with
            /// the project and xml of this factory and the specified include. Metadata is cloned,
            /// but continues to point to the original ProjectMetadataElement objects.
            /// This is to support this scenario: Include="@(i->'xxx')"
            /// </summary>
            /// <remarks>
            /// If the item type of the source is the same as the item type of the destination,
            /// then it's not necessary to copy metadata originating in an item definition.
            /// If it's not, we have to clone that too.
            /// </remarks>
            /// <comments>
            /// NOTE: defining project is ignored because we already know the ItemElement associated with 
            /// this item, and use that for where it is defined. 
            /// </comments>
            public ProjectItem CreateItem(string evaluatedIncludeEscaped, ProjectItem source, string definingProject)
            {
                return CreateItem(evaluatedIncludeEscaped, evaluatedIncludeEscaped, source);
            }

            /// <summary>
            /// Creates an item with the specified include and include before wildcard expansion.
            /// This is to support creating items from an include that may have a wildcard expression in it.
            /// </summary>
            /// <comments>
            /// NOTE: defining project is ignored because we already know the ItemElement associated with 
            /// this item, and use that for where it is defined. 
            /// </comments>
            public ProjectItem CreateItem(string evaluatedIncludeEscaped, string evaluatedIncludeBeforeWildcardExpansion, string definingProject)
            {
                ErrorUtilities.VerifyThrowInternalNull(_xml, "xml");

                return new ProjectItem(_project, _xml, evaluatedIncludeEscaped, evaluatedIncludeBeforeWildcardExpansion, null /* no metadata */, null /* no inherited definition metadata */);
            }

            /// <summary>
            /// Applies the supplied metadata to the destination item.
            /// </summary>
            public void SetMetadata(IEnumerable<Pair<ProjectMetadataElement, string>> metadata, IEnumerable<ProjectItem> destinationItems)
            {
                foreach (IItem<ProjectMetadata> item in destinationItems)
                {
                    foreach (Pair<ProjectMetadataElement, string> metadatum in metadata)
                    {
                        item.SetMetadata(metadatum.Key, metadatum.Value);
                    }
                }
            }

            /// <summary>
            /// Creates an item based on the provided item, with the specified include and item type.
            /// </summary>
            private ProjectItem CreateItem(string evaluatedIncludeEscaped, string evaluatedIncludeBeforeWildcardExpansionEscaped, ProjectItem source)
            {
                ErrorUtilities.VerifyThrowInternalNull(_xml, "xml");

                // The new item inherits any metadata originating in item definitions, which
                // takes precedence over its own item definition metadata.
                //
                // Order of precedence:
                // (1) any directly defined metadata on the source item
                // (2) any inherited item definition metadata the source item had accumulated, in order of accumulation
                // (3) any item definition metadata associated with the source item's item type
                // (4) any item definition metadata associated with the destination item's item type; none yet.

                // Clone for (1)
                PropertyDictionary<ProjectMetadata> directMetadataClone = null;

                if (source.DirectMetadataCount > 0)
                {
                    directMetadataClone = new PropertyDictionary<ProjectMetadata>(source.DirectMetadataCount);

                    foreach (ProjectMetadata metadatum in source._directMetadata)
                    {
                        directMetadataClone.Set(metadatum.DeepClone());
                    }
                }

                // Combine (2) and (3) into a list, (2) on top.
                int inheritedItemDefinitionsCount = (source._inheritedItemDefinitions == null) ? 0 : source._inheritedItemDefinitions.Count;

                List<ProjectItemDefinition> inheritedItemDefinitionsClone = null;

                if (source._inheritedItemDefinitions != null)
                {
                    inheritedItemDefinitionsClone ??= new List<ProjectItemDefinition>(inheritedItemDefinitionsCount + 1);
                    inheritedItemDefinitionsClone.AddRange(source._inheritedItemDefinitions);
                }

                ProjectItemDefinition sourceItemDefinition;
                if (_project.ItemDefinitions.TryGetValue(source.ItemType, out sourceItemDefinition))
                {
                    inheritedItemDefinitionsClone ??= new List<ProjectItemDefinition>(inheritedItemDefinitionsCount + 1);
                    inheritedItemDefinitionsClone.Add(sourceItemDefinition);
                }

                return new ProjectItem(_project, _xml, evaluatedIncludeEscaped, evaluatedIncludeBeforeWildcardExpansionEscaped, directMetadataClone, inheritedItemDefinitionsClone);
            }
        }

        /// <summary>
        /// Implementation of IMetadataTable that can be passed to expander
        /// to expose only built-in metadata on this item.
        /// </summary>
        private class BuiltInMetadataTable : IMetadataTable
        {
            /// <summary>
            /// Backing item
            /// </summary>
            private ProjectItem _item;

            /// <summary>
            /// Constructor.
            /// </summary>
            internal BuiltInMetadataTable(ProjectItem item)
            {
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
            public string GetEscapedValue(string itemType, string name)
            {
                string value = GetEscapedValueIfPresent(itemType, name);

                return value ?? String.Empty;
            }

            /// <summary>
            /// Returns the value if it exists, null otherwise.
            /// If item type is null, it is ignored.
            /// </summary>
            public string GetEscapedValueIfPresent(string itemType, string name)
            {
                string value = null;

                if ((itemType == null) || String.Equals(_item.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
                {
                    value = GetEscapedValue(name);
                }

                return value;
            }
        }
    }
}
