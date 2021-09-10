// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace Microsoft.Build.BackEnd.SdkResolution
{
    public partial class SdkResolverException : System.Exception
    {
        public SdkResolverException(string resourceName, Microsoft.Build.Framework.SdkResolver resolver, Microsoft.Build.Framework.SdkReference sdk, System.Exception innerException, params string[] args) { }
        public Microsoft.Build.Framework.SdkResolver Resolver { get { throw null; } }
        public Microsoft.Build.Framework.SdkReference Sdk { get { throw null; } }
    }
}
namespace Microsoft.Build.Construction
{
    public abstract partial class ElementLocation
    {
        protected ElementLocation() { }
        public abstract int Column { get; }
        public abstract string File { get; }
        public abstract int Line { get; }
        public string LocationString { get { throw null; } }
        public override bool Equals(object obj) { throw null; }
        public override int GetHashCode() { throw null; }
        public override string ToString() { throw null; }
    }
    public enum ImplicitImportLocation
    {
        None = 0,
        Top = 1,
        Bottom = 2,
    }
    public partial class ProjectChooseElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectChooseElement() { }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public Microsoft.Build.Construction.ProjectOtherwiseElement OtherwiseElement { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectWhenElement> WhenElements { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public sealed partial class ProjectConfigurationInSolution
    {
        internal ProjectConfigurationInSolution() { }
        public string ConfigurationName { get { throw null; } }
        public string FullName { get { throw null; } }
        public bool IncludeInBuild { get { throw null; } }
        public string PlatformName { get { throw null; } }
    }
    public abstract partial class ProjectElement : Microsoft.Build.Framework.IProjectElement
    {
        internal ProjectElement() { }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Construction.ProjectElementContainer> AllParents { get { throw null; } }
        public virtual string Condition { get { throw null; } set { } }
        public virtual Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public Microsoft.Build.Construction.ProjectRootElement ContainingProject { get { throw null; } }
        public string ElementName { get { throw null; } }
        public string Label { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation LabelLocation { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElement NextSibling { get { throw null; } }
        public string OuterElement { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElementContainer Parent { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElement PreviousSibling { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElement Clone() { throw null; }
        protected internal virtual Microsoft.Build.Construction.ProjectElement Clone(Microsoft.Build.Construction.ProjectRootElement factory) { throw null; }
        public virtual void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected abstract Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner);
        protected virtual bool ShouldCloneXmlAttribute(System.Xml.XmlAttribute attribute) { throw null; }
    }
    public abstract partial class ProjectElementContainer : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectElementContainer() { }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Construction.ProjectElement> AllChildren { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectElement> Children { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectElement> ChildrenReversed { get { throw null; } }
        public int Count { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElement FirstChild { get { throw null; } }
        public Microsoft.Build.Construction.ProjectElement LastChild { get { throw null; } }
        public void AppendChild(Microsoft.Build.Construction.ProjectElement child) { }
        protected internal virtual Microsoft.Build.Construction.ProjectElementContainer DeepClone(Microsoft.Build.Construction.ProjectRootElement factory, Microsoft.Build.Construction.ProjectElementContainer parent) { throw null; }
        public virtual void DeepCopyFrom(Microsoft.Build.Construction.ProjectElementContainer element) { }
        public void InsertAfterChild(Microsoft.Build.Construction.ProjectElement child, Microsoft.Build.Construction.ProjectElement reference) { }
        public void InsertBeforeChild(Microsoft.Build.Construction.ProjectElement child, Microsoft.Build.Construction.ProjectElement reference) { }
        public void PrependChild(Microsoft.Build.Construction.ProjectElement child) { }
        public void RemoveAllChildren() { }
        public void RemoveChild(Microsoft.Build.Construction.ProjectElement child) { }
    }
    public partial class ProjectExtensionsElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectExtensionsElement() { }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string Content { get { throw null; } set { } }
        public string this[string name] { get { throw null; } set { } }
        public override void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectImportElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectImportElement() { }
        public Microsoft.Build.Construction.ImplicitImportLocation ImplicitImportLocation { get { throw null; } }
        public string MinimumVersion { get { throw null; } set { } }
        public Microsoft.Build.Construction.ProjectElement OriginalElement { get { throw null; } }
        public string Project { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ProjectLocation { get { throw null; } }
        public string Sdk { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation SdkLocation { get { throw null; } }
        public string Version { get { throw null; } set { } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectImportGroupElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectImportGroupElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectImportElement> Imports { get { throw null; } }
        public Microsoft.Build.Construction.ProjectImportElement AddImport(string project) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public sealed partial class ProjectInSolution
    {
        internal ProjectInSolution() { }
        public string AbsolutePath { get { throw null; } }
        public System.Collections.Generic.IReadOnlyList<string> Dependencies { get { throw null; } }
        public string ParentProjectGuid { get { throw null; } }
        public System.Collections.Generic.IReadOnlyDictionary<string, Microsoft.Build.Construction.ProjectConfigurationInSolution> ProjectConfigurations { get { throw null; } }
        public string ProjectGuid { get { throw null; } }
        public string ProjectName { get { throw null; } }
        public Microsoft.Build.Construction.SolutionProjectType ProjectType { get { throw null; } set { } }
        public string RelativePath { get { throw null; } }
    }
    public partial class ProjectItemDefinitionElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectItemDefinitionElement() { }
        public string ItemType { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectMetadataElement> Metadata { get { throw null; } }
        public Microsoft.Build.Construction.ProjectMetadataElement AddMetadata(string name, string unevaluatedValue) { throw null; }
        public Microsoft.Build.Construction.ProjectMetadataElement AddMetadata(string name, string unevaluatedValue, bool expressAsAttribute) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        protected override bool ShouldCloneXmlAttribute(System.Xml.XmlAttribute attribute) { throw null; }
    }
    public partial class ProjectItemDefinitionGroupElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectItemDefinitionGroupElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemDefinitionElement> ItemDefinitions { get { throw null; } }
        public Microsoft.Build.Construction.ProjectItemDefinitionElement AddItemDefinition(string itemType) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectItemElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectItemElement() { }
        public string Exclude { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ExcludeLocation { get { throw null; } }
        public bool HasMetadata { get { throw null; } }
        public string Include { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation IncludeLocation { get { throw null; } }
        public string ItemType { get { throw null; } set { } }
        public string KeepDuplicates { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation KeepDuplicatesLocation { get { throw null; } }
        public string KeepMetadata { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation KeepMetadataLocation { get { throw null; } }
        public string MatchOnMetadata { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation MatchOnMetadataLocation { get { throw null; } }
        public string MatchOnMetadataOptions { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation MatchOnMetadataOptionsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectMetadataElement> Metadata { get { throw null; } }
        public string Remove { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation RemoveLocation { get { throw null; } }
        public string RemoveMetadata { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation RemoveMetadataLocation { get { throw null; } }
        public string Update { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation UpdateLocation { get { throw null; } }
        public Microsoft.Build.Construction.ProjectMetadataElement AddMetadata(string name, string unevaluatedValue) { throw null; }
        public Microsoft.Build.Construction.ProjectMetadataElement AddMetadata(string name, string unevaluatedValue, bool expressAsAttribute) { throw null; }
        public override void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        protected override bool ShouldCloneXmlAttribute(System.Xml.XmlAttribute attribute) { throw null; }
    }
    public partial class ProjectItemGroupElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectItemGroupElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemElement> Items { get { throw null; } }
        public Microsoft.Build.Construction.ProjectItemElement AddItem(string itemType, string include) { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement AddItem(string itemType, string include, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata) { throw null; }
        public override void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectMetadataElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectMetadataElement() { }
        public bool ExpressedAsAttribute { get { throw null; } set { } }
        public string Name { get { throw null; } set { } }
        public string Value { get { throw null; } set { } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectOnErrorElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectOnErrorElement() { }
        public string ExecuteTargetsAttribute { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ExecuteTargetsLocation { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectOtherwiseElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectOtherwiseElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectChooseElement> ChooseElements { get { throw null; } }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemGroupElement> ItemGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyGroupElement> PropertyGroups { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectOutputElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectOutputElement() { }
        public bool IsOutputItem { get { throw null; } }
        public bool IsOutputProperty { get { throw null; } }
        public string ItemType { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ItemTypeLocation { get { throw null; } }
        public string PropertyName { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation PropertyNameLocation { get { throw null; } }
        public string TaskParameter { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation TaskParameterLocation { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectPropertyElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectPropertyElement() { }
        public string Name { get { throw null; } set { } }
        public string Value { get { throw null; } set { } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectPropertyGroupElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectPropertyGroupElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyElement> Properties { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyElement> PropertiesReversed { get { throw null; } }
        public Microsoft.Build.Construction.ProjectPropertyElement AddProperty(string name, string unevaluatedValue) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyElement SetProperty(string name, string unevaluatedValue) { throw null; }
    }
    public partial class ProjectRootElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectRootElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectChooseElement> ChooseElements { get { throw null; } }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string DefaultTargets { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation DefaultTargetsLocation { get { throw null; } }
        public string DirectoryPath { get { throw null; } }
        public System.Text.Encoding Encoding { get { throw null; } }
        public string EscapedFullPath { get { throw null; } }
        public string FullPath { get { throw null; } set { } }
        public bool HasUnsavedChanges { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectImportGroupElement> ImportGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectImportGroupElement> ImportGroupsReversed { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectImportElement> Imports { get { throw null; } }
        public string InitialTargets { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation InitialTargetsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemDefinitionGroupElement> ItemDefinitionGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemDefinitionGroupElement> ItemDefinitionGroupsReversed { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemDefinitionElement> ItemDefinitions { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemGroupElement> ItemGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemGroupElement> ItemGroupsReversed { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemElement> Items { get { throw null; } }
        public System.DateTime LastWriteTimeWhenRead { get { throw null; } }
        public bool PreserveFormatting { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ProjectFileLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyElement> Properties { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyGroupElement> PropertyGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyGroupElement> PropertyGroupsReversed { get { throw null; } }
        public string RawXml { get { throw null; } }
        public string Sdk { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation SdkLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectTargetElement> Targets { get { throw null; } }
        public System.DateTime TimeLastChanged { get { throw null; } }
        public string ToolsVersion { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ToolsVersionLocation { get { throw null; } }
        public string TreatAsLocalProperty { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation TreatAsLocalPropertyLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectUsingTaskElement> UsingTasks { get { throw null; } }
        public int Version { get { throw null; } }
        public Microsoft.Build.Construction.ProjectImportElement AddImport(string project) { throw null; }
        public Microsoft.Build.Construction.ProjectImportGroupElement AddImportGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement AddItem(string itemType, string include) { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement AddItem(string itemType, string include, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata) { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionElement AddItemDefinition(string itemType) { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionGroupElement AddItemDefinitionGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectItemGroupElement AddItemGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyElement AddProperty(string name, string value) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyGroupElement AddPropertyGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectTargetElement AddTarget(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskElement AddUsingTask(string name, string assemblyFile, string assemblyName) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create() { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(Microsoft.Build.Evaluation.NewProjectFileOptions projectFileOptions) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(Microsoft.Build.Evaluation.ProjectCollection projectCollection) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.NewProjectFileOptions projectFileOptions) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(string path) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(string path, Microsoft.Build.Evaluation.NewProjectFileOptions newProjectFileOptions) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.NewProjectFileOptions newProjectFileOptions) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(System.Xml.XmlReader xmlReader) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(System.Xml.XmlReader xmlReader, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Create(System.Xml.XmlReader xmlReader, Microsoft.Build.Evaluation.ProjectCollection projectCollection, bool preserveFormatting) { throw null; }
        public Microsoft.Build.Construction.ProjectChooseElement CreateChooseElement() { throw null; }
        public Microsoft.Build.Construction.ProjectImportElement CreateImportElement(string project) { throw null; }
        public Microsoft.Build.Construction.ProjectImportGroupElement CreateImportGroupElement() { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionElement CreateItemDefinitionElement(string itemType) { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionGroupElement CreateItemDefinitionGroupElement() { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement CreateItemElement(string itemType) { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement CreateItemElement(string itemType, string include) { throw null; }
        public Microsoft.Build.Construction.ProjectItemGroupElement CreateItemGroupElement() { throw null; }
        public Microsoft.Build.Construction.ProjectMetadataElement CreateMetadataElement(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectMetadataElement CreateMetadataElement(string name, string unevaluatedValue) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        public Microsoft.Build.Construction.ProjectOnErrorElement CreateOnErrorElement(string executeTargets) { throw null; }
        public Microsoft.Build.Construction.ProjectOtherwiseElement CreateOtherwiseElement() { throw null; }
        public Microsoft.Build.Construction.ProjectOutputElement CreateOutputElement(string taskParameter, string itemType, string propertyName) { throw null; }
        public Microsoft.Build.Construction.ProjectExtensionsElement CreateProjectExtensionsElement() { throw null; }
        public Microsoft.Build.Construction.ProjectSdkElement CreateProjectSdkElement(string sdkName, string sdkVersion) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyElement CreatePropertyElement(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyGroupElement CreatePropertyGroupElement() { throw null; }
        public Microsoft.Build.Construction.ProjectTargetElement CreateTargetElement(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectTaskElement CreateTaskElement(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskBodyElement CreateUsingTaskBodyElement(string evaluate, string body) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskElement CreateUsingTaskElement(string taskName, string assemblyFile, string assemblyName) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskElement CreateUsingTaskElement(string taskName, string assemblyFile, string assemblyName, string runtime, string architecture) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskParameterElement CreateUsingTaskParameterElement(string name, string output, string required, string parameterType) { throw null; }
        public Microsoft.Build.Construction.UsingTaskParameterGroupElement CreateUsingTaskParameterGroupElement() { throw null; }
        public Microsoft.Build.Construction.ProjectWhenElement CreateWhenElement(string condition) { throw null; }
        public Microsoft.Build.Construction.ProjectRootElement DeepClone() { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Open(string path) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Open(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement Open(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection, bool? preserveFormatting) { throw null; }
        public void Reload(bool throwIfUnsavedChanges = true, bool? preserveFormatting = default(bool?)) { }
        public void ReloadFrom(string path, bool throwIfUnsavedChanges = true, bool? preserveFormatting = default(bool?)) { }
        public void ReloadFrom(System.Xml.XmlReader reader, bool throwIfUnsavedChanges = true, bool? preserveFormatting = default(bool?)) { }
        public void Save() { }
        public void Save(System.IO.TextWriter writer) { }
        public void Save(string path) { }
        public void Save(string path, System.Text.Encoding encoding) { }
        public void Save(System.Text.Encoding saveEncoding) { }
        public static Microsoft.Build.Construction.ProjectRootElement TryOpen(string path) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement TryOpen(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { throw null; }
        public static Microsoft.Build.Construction.ProjectRootElement TryOpen(string path, Microsoft.Build.Evaluation.ProjectCollection projectCollection, bool? preserveFormatting) { throw null; }
    }
    public partial class ProjectSdkElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectSdkElement() { }
        public string MinimumVersion { get { throw null; } set { } }
        public string Name { get { throw null; } set { } }
        public string Version { get { throw null; } set { } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectTargetElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectTargetElement() { }
        public string AfterTargets { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation AfterTargetsLocation { get { throw null; } }
        public string BeforeTargets { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation BeforeTargetsLocation { get { throw null; } }
        public string DependsOnTargets { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation DependsOnTargetsLocation { get { throw null; } }
        public string Inputs { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation InputsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemGroupElement> ItemGroups { get { throw null; } }
        public string KeepDuplicateOutputs { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation KeepDuplicateOutputsLocation { get { throw null; } }
        public string Name { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation NameLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectOnErrorElement> OnErrors { get { throw null; } }
        public string Outputs { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation OutputsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyGroupElement> PropertyGroups { get { throw null; } }
        public string Returns { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ReturnsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectTaskElement> Tasks { get { throw null; } }
        public Microsoft.Build.Construction.ProjectItemGroupElement AddItemGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyGroupElement AddPropertyGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectTaskElement AddTask(string taskName) { throw null; }
        public override void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectTaskElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectTaskElement() { }
        public string ContinueOnError { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ContinueOnErrorLocation { get { throw null; } }
        public string MSBuildArchitecture { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation MSBuildArchitectureLocation { get { throw null; } }
        public string MSBuildRuntime { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation MSBuildRuntimeLocation { get { throw null; } }
        public string Name { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectOutputElement> Outputs { get { throw null; } }
        public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, Microsoft.Build.Construction.ElementLocation>> ParameterLocations { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, string> Parameters { get { throw null; } }
        public Microsoft.Build.Construction.ProjectOutputElement AddOutputItem(string taskParameter, string itemType) { throw null; }
        public Microsoft.Build.Construction.ProjectOutputElement AddOutputItem(string taskParameter, string itemType, string condition) { throw null; }
        public Microsoft.Build.Construction.ProjectOutputElement AddOutputProperty(string taskParameter, string propertyName) { throw null; }
        public Microsoft.Build.Construction.ProjectOutputElement AddOutputProperty(string taskParameter, string propertyName, string condition) { throw null; }
        public override void CopyFrom(Microsoft.Build.Construction.ProjectElement element) { }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        public string GetParameter(string name) { throw null; }
        public void RemoveAllParameters() { }
        public void RemoveParameter(string name) { }
        public void SetParameter(string name, string unevaluatedValue) { }
    }
    public partial class ProjectUsingTaskBodyElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectUsingTaskBodyElement() { }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string Evaluate { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation EvaluateLocation { get { throw null; } }
        public string TaskBody { get { throw null; } set { } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectUsingTaskElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectUsingTaskElement() { }
        public string Architecture { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ArchitectureLocation { get { throw null; } }
        public string AssemblyFile { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation AssemblyFileLocation { get { throw null; } }
        public string AssemblyName { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation AssemblyNameLocation { get { throw null; } }
        public Microsoft.Build.Construction.UsingTaskParameterGroupElement ParameterGroup { get { throw null; } }
        public string Runtime { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation RuntimeLocation { get { throw null; } }
        public Microsoft.Build.Construction.ProjectUsingTaskBodyElement TaskBody { get { throw null; } }
        public string TaskFactory { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation TaskFactoryLocation { get { throw null; } }
        public string TaskName { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation TaskNameLocation { get { throw null; } }
        public Microsoft.Build.Construction.UsingTaskParameterGroupElement AddParameterGroup() { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskBodyElement AddUsingTaskBody(string evaluate, string taskBody) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectUsingTaskParameterElement : Microsoft.Build.Construction.ProjectElement
    {
        internal ProjectUsingTaskParameterElement() { }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string Name { get { throw null; } set { } }
        public string Output { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation OutputLocation { get { throw null; } }
        public string ParameterType { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation ParameterTypeLocation { get { throw null; } }
        public string Required { get { throw null; } set { } }
        public Microsoft.Build.Construction.ElementLocation RequiredLocation { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public partial class ProjectWhenElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal ProjectWhenElement() { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectChooseElement> ChooseElements { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectItemGroupElement> ItemGroups { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectPropertyGroupElement> PropertyGroups { get { throw null; } }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
    public sealed partial class SolutionConfigurationInSolution
    {
        internal SolutionConfigurationInSolution() { }
        public string ConfigurationName { get { throw null; } }
        public string FullName { get { throw null; } }
        public string PlatformName { get { throw null; } }
    }
    public sealed partial class SolutionFile
    {
        internal SolutionFile() { }
        public System.Collections.Generic.IReadOnlyDictionary<string, Microsoft.Build.Construction.ProjectInSolution> ProjectsByGuid { get { throw null; } }
        public System.Collections.Generic.IReadOnlyList<Microsoft.Build.Construction.ProjectInSolution> ProjectsInOrder { get { throw null; } }
        public System.Collections.Generic.IReadOnlyList<Microsoft.Build.Construction.SolutionConfigurationInSolution> SolutionConfigurations { get { throw null; } }
        public string GetDefaultConfigurationName() { throw null; }
        public string GetDefaultPlatformName() { throw null; }
        public static Microsoft.Build.Construction.SolutionFile Parse(string solutionFile) { throw null; }
    }
    public enum SolutionProjectType
    {
        Unknown = 0,
        KnownToBeMSBuildFormat = 1,
        SolutionFolder = 2,
        WebProject = 3,
        WebDeploymentProject = 4,
        EtpSubProject = 5,
        SharedProject = 6,
    }
    public partial class UsingTaskParameterGroupElement : Microsoft.Build.Construction.ProjectElementContainer
    {
        internal UsingTaskParameterGroupElement() { }
        public override string Condition { get { throw null; } set { } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Construction.ProjectUsingTaskParameterElement> Parameters { get { throw null; } }
        public Microsoft.Build.Construction.ProjectUsingTaskParameterElement AddParameter(string name) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskParameterElement AddParameter(string name, string output, string required, string parameterType) { throw null; }
        protected override Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
    }
}
namespace Microsoft.Build.Definition
{
    public partial class ProjectOptions
    {
        public ProjectOptions() { }
        public Microsoft.Build.FileSystem.IDirectoryCacheFactory DirectoryCacheFactory { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.Context.EvaluationContext EvaluationContext { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.ProjectLoadSettings LoadSettings { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.ProjectCollection ProjectCollection { get { throw null; } set { } }
        public string SubToolsetVersion { get { throw null; } set { } }
        public string ToolsVersion { get { throw null; } set { } }
    }
}
namespace Microsoft.Build.Evaluation
{
    public partial class GlobResult
    {
        public GlobResult(Microsoft.Build.Construction.ProjectItemElement itemElement, System.Collections.Generic.IEnumerable<string> includeGlobStrings, Microsoft.Build.Globbing.IMSBuildGlob globWithGaps, System.Collections.Generic.IEnumerable<string> excludeFragmentStrings, System.Collections.Generic.IEnumerable<string> removeFragmentStrings) { }
        public System.Collections.Generic.IEnumerable<string> Excludes { get { throw null; } }
        public System.Collections.Generic.IEnumerable<string> IncludeGlobs { get { throw null; } }
        public Microsoft.Build.Construction.ProjectItemElement ItemElement { get { throw null; } }
        public Microsoft.Build.Globbing.IMSBuildGlob MsBuildGlob { get { throw null; } set { } }
        public System.Collections.Generic.IEnumerable<string> Removes { get { throw null; } set { } }
    }
    public static partial class MatchOnMetadataConstants
    {
        public const Microsoft.Build.Evaluation.MatchOnMetadataOptions MatchOnMetadataOptionsDefaultValue = Microsoft.Build.Evaluation.MatchOnMetadataOptions.CaseSensitive;
    }
    public enum MatchOnMetadataOptions
    {
        CaseSensitive = 0,
        CaseInsensitive = 1,
        PathLike = 2,
    }
    [System.FlagsAttribute]
    public enum NewProjectFileOptions
    {
        IncludeAllOptions = -1,
        None = 0,
        IncludeXmlDeclaration = 1,
        IncludeToolsVersion = 2,
        IncludeXmlNamespace = 4,
    }
    public enum Operation
    {
        Include = 0,
        Exclude = 1,
        Update = 2,
        Remove = 3,
    }
    public partial class Project
    {
        public Project() { }
        public Project(Microsoft.Build.Construction.ProjectRootElement xml) { }
        public Project(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { }
        public Project(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Project(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public Project(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public Project(Microsoft.Build.Evaluation.NewProjectFileOptions newProjectFileOptions) { }
        public Project(Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Project(Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.NewProjectFileOptions newProjectFileOptions) { }
        public Project(System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Project(System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.NewProjectFileOptions newProjectFileOptions) { }
        public Project(string projectFile) { }
        public Project(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { }
        public Project(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Project(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public Project(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public Project(System.Xml.XmlReader xmlReader) { }
        public Project(System.Xml.XmlReader xmlReader, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { }
        public Project(System.Xml.XmlReader xmlReader, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Project(System.Xml.XmlReader xmlReader, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public Project(System.Xml.XmlReader xmlReader, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Evaluation.ProjectLoadSettings loadSettings) { }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> AllEvaluatedItemDefinitionMetadata { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> AllEvaluatedItems { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectProperty> AllEvaluatedProperties { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, System.Collections.Generic.List<string>> ConditionedProperties { get { throw null; } }
        public string DirectoryPath { get { throw null; } }
        public bool DisableMarkDirty { get { throw null; } set { } }
        public int EvaluationCounter { get { throw null; } }
        public string FullPath { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ResolvedImport> Imports { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ResolvedImport> ImportsIncludingDuplicates { get { throw null; } }
        public bool IsBuildEnabled { get { throw null; } set { } }
        public bool IsDirty { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Evaluation.ProjectItemDefinition> ItemDefinitions { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> Items { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> ItemsIgnoringCondition { get { throw null; } }
        public System.Collections.Generic.ICollection<string> ItemTypes { get { throw null; } }
        public int LastEvaluationId { get { throw null; } }
        public Microsoft.Build.Evaluation.ProjectCollection ProjectCollection { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ProjectFileLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectProperty> Properties { get { throw null; } }
        public bool SkipEvaluation { get { throw null; } set { } }
        public string SubToolsetVersion { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectTargetInstance> Targets { get { throw null; } }
        public bool ThrowInsteadOfSplittingItemElement { get { throw null; } set { } }
        public string ToolsVersion { get { throw null; } }
        public Microsoft.Build.Construction.ProjectRootElement Xml { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItem(string itemType, string unevaluatedInclude) { throw null; }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItem(string itemType, string unevaluatedInclude, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata) { throw null; }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItemFast(string itemType, string unevaluatedInclude) { throw null; }
        public System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItemFast(string itemType, string unevaluatedInclude, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata) { throw null; }
        public bool Build() { throw null; }
        public bool Build(Microsoft.Build.Framework.ILogger logger) { throw null; }
        public bool Build(System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string target) { throw null; }
        public bool Build(string target, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(string target, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string[] targets) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public Microsoft.Build.Execution.ProjectInstance CreateProjectInstance() { throw null; }
        public Microsoft.Build.Execution.ProjectInstance CreateProjectInstance(Microsoft.Build.Execution.ProjectInstanceSettings settings) { throw null; }
        public Microsoft.Build.Execution.ProjectInstance CreateProjectInstance(Microsoft.Build.Execution.ProjectInstanceSettings settings, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public string ExpandString(string unexpandedValue) { throw null; }
        public static Microsoft.Build.Evaluation.Project FromFile(string file, Microsoft.Build.Definition.ProjectOptions options) { throw null; }
        public static Microsoft.Build.Evaluation.Project FromProjectRootElement(Microsoft.Build.Construction.ProjectRootElement rootElement, Microsoft.Build.Definition.ProjectOptions options) { throw null; }
        public static Microsoft.Build.Evaluation.Project FromXmlReader(System.Xml.XmlReader reader, Microsoft.Build.Definition.ProjectOptions options) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs() { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs(Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs(string itemType) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs(string itemType, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public static string GetEvaluatedItemIncludeEscaped(Microsoft.Build.Evaluation.ProjectItem item) { throw null; }
        public static string GetEvaluatedItemIncludeEscaped(Microsoft.Build.Evaluation.ProjectItemDefinition item) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(Microsoft.Build.Evaluation.ProjectItem item) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(Microsoft.Build.Evaluation.ProjectItem item, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType) { throw null; }
        public System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { throw null; }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItems(string itemType) { throw null; }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItemsByEvaluatedInclude(string evaluatedInclude) { throw null; }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItemsIgnoringCondition(string itemType) { throw null; }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Construction.ProjectElement> GetLogicalProject() { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Evaluation.ProjectItem item, string name) { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Evaluation.ProjectItemDefinition item, string name) { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Evaluation.ProjectMetadata metadatum) { throw null; }
        public Microsoft.Build.Evaluation.ProjectProperty GetProperty(string name) { throw null; }
        public string GetPropertyValue(string name) { throw null; }
        public static string GetPropertyValueEscaped(Microsoft.Build.Evaluation.ProjectProperty property) { throw null; }
        public void MarkDirty() { }
        public void ReevaluateIfNecessary() { }
        public void ReevaluateIfNecessary(Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext) { }
        public bool RemoveGlobalProperty(string name) { throw null; }
        public bool RemoveItem(Microsoft.Build.Evaluation.ProjectItem item) { throw null; }
        public void RemoveItems(System.Collections.Generic.IEnumerable<Microsoft.Build.Evaluation.ProjectItem> items) { }
        public bool RemoveProperty(Microsoft.Build.Evaluation.ProjectProperty property) { throw null; }
        public void Save() { }
        public void Save(System.IO.TextWriter writer) { }
        public void Save(string path) { }
        public void Save(string path, System.Text.Encoding encoding) { }
        public void Save(System.Text.Encoding encoding) { }
        public void SaveLogicalProject(System.IO.TextWriter writer) { }
        public bool SetGlobalProperty(string name, string escapedValue) { throw null; }
        public Microsoft.Build.Evaluation.ProjectProperty SetProperty(string name, string unevaluatedValue) { throw null; }
    }
    public partial class ProjectChangedEventArgs : System.EventArgs
    {
        internal ProjectChangedEventArgs() { }
        public Microsoft.Build.Evaluation.Project Project { get { throw null; } }
    }
    public partial class ProjectCollection : System.IDisposable
    {
        public ProjectCollection() { }
        public ProjectCollection(Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetLocations) { }
        public ProjectCollection(System.Collections.Generic.IDictionary<string, string> globalProperties) { }
        public ProjectCollection(System.Collections.Generic.IDictionary<string, string> globalProperties, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetDefinitionLocations) { }
        public ProjectCollection(System.Collections.Generic.IDictionary<string, string> globalProperties, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers, Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetDefinitionLocations, int maxNodeCount, bool onlyLogCriticalEvents) { }
        public ProjectCollection(System.Collections.Generic.IDictionary<string, string> globalProperties, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers, Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetDefinitionLocations, int maxNodeCount, bool onlyLogCriticalEvents, bool loadProjectsReadOnly) { }
        public int Count { get { throw null; } }
        public string DefaultToolsVersion { get { throw null; } set { } }
        public bool DisableMarkDirty { get { throw null; } set { } }
        public static string DisplayVersion { get { throw null; } }
        public static Microsoft.Build.Evaluation.ProjectCollection GlobalProjectCollection { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } }
        public Microsoft.Build.Execution.HostServices HostServices { get { throw null; } set { } }
        public bool IsBuildEnabled { get { throw null; } set { } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.Project> LoadedProjects { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Framework.ILogger> Loggers { get { throw null; } }
        public bool OnlyLogCriticalEvents { get { throw null; } set { } }
        public bool SkipEvaluation { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.ToolsetDefinitionLocations ToolsetLocations { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.Toolset> Toolsets { get { throw null; } }
        public static System.Version Version { get { throw null; } }
        public event Microsoft.Build.Evaluation.ProjectCollection.ProjectAddedEventHandler ProjectAdded { add { } remove { } }
        public event System.EventHandler<Microsoft.Build.Evaluation.ProjectChangedEventArgs> ProjectChanged { add { } remove { } }
        public event System.EventHandler<Microsoft.Build.Evaluation.ProjectCollectionChangedEventArgs> ProjectCollectionChanged { add { } remove { } }
        public event System.EventHandler<Microsoft.Build.Evaluation.ProjectXmlChangedEventArgs> ProjectXmlChanged { add { } remove { } }
        public void AddToolset(Microsoft.Build.Evaluation.Toolset toolset) { }
        public bool ContainsToolset(string toolsVersion) { throw null; }
        public void Dispose() { }
        protected virtual void Dispose(bool disposing) { }
        public static string Escape(string unescapedString) { throw null; }
        public string GetEffectiveToolsVersion(string explicitToolsVersion, string toolsVersionFromProject) { throw null; }
        public Microsoft.Build.Execution.ProjectPropertyInstance GetGlobalProperty(string name) { throw null; }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.Project> GetLoadedProjects(string fullPath) { throw null; }
        public Microsoft.Build.Evaluation.Toolset GetToolset(string toolsVersion) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(string fileName) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(string fileName, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(string fileName, string toolsVersion) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(System.Xml.XmlReader xmlReader) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(System.Xml.XmlReader xmlReader, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { throw null; }
        public Microsoft.Build.Evaluation.Project LoadProject(System.Xml.XmlReader xmlReader, string toolsVersion) { throw null; }
        public void RegisterForwardingLoggers(System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { }
        public void RegisterLogger(Microsoft.Build.Framework.ILogger logger) { }
        public void RegisterLoggers(System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { }
        public void RemoveAllToolsets() { }
        public bool RemoveGlobalProperty(string name) { throw null; }
        public bool RemoveToolset(string toolsVersion) { throw null; }
        public void SetGlobalProperty(string name, string value) { }
        public bool TryUnloadProject(Microsoft.Build.Construction.ProjectRootElement projectRootElement) { throw null; }
        public static string Unescape(string escapedString) { throw null; }
        public void UnloadAllProjects() { }
        public void UnloadProject(Microsoft.Build.Construction.ProjectRootElement projectRootElement) { }
        public void UnloadProject(Microsoft.Build.Evaluation.Project project) { }
        public void UnregisterAllLoggers() { }
        public delegate void ProjectAddedEventHandler(object sender, Microsoft.Build.Evaluation.ProjectCollection.ProjectAddedToProjectCollectionEventArgs e);
        public partial class ProjectAddedToProjectCollectionEventArgs : System.EventArgs
        {
            public ProjectAddedToProjectCollectionEventArgs(Microsoft.Build.Construction.ProjectRootElement element) { }
            public Microsoft.Build.Construction.ProjectRootElement ProjectRootElement { get { throw null; } }
        }
    }
    public partial class ProjectCollectionChangedEventArgs : System.EventArgs
    {
        internal ProjectCollectionChangedEventArgs() { }
        public Microsoft.Build.Evaluation.ProjectCollectionChangedState Changed { get { throw null; } }
    }
    public enum ProjectCollectionChangedState
    {
        DefaultToolsVersion = 0,
        Toolsets = 1,
        Loggers = 2,
        GlobalProperties = 3,
        IsBuildEnabled = 4,
        OnlyLogCriticalEvents = 5,
        HostServices = 6,
        DisableMarkDirty = 7,
        SkipEvaluation = 8,
    }
    public partial class ProjectItem
    {
        internal ProjectItem() { }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Evaluation.ProjectMetadata> DirectMetadata { get { throw null; } }
        public int DirectMetadataCount { get { throw null; } }
        public string EvaluatedInclude { get { throw null; } }
        public bool IsImported { get { throw null; } }
        public string ItemType { get { throw null; } set { } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> Metadata { get { throw null; } }
        public int MetadataCount { get { throw null; } }
        public Microsoft.Build.Evaluation.Project Project { get { throw null; } }
        public string UnevaluatedInclude { get { throw null; } set { } }
        public Microsoft.Build.Construction.ProjectItemElement Xml { get { throw null; } }
        public Microsoft.Build.Evaluation.ProjectMetadata GetMetadata(string name) { throw null; }
        public string GetMetadataValue(string name) { throw null; }
        public bool HasMetadata(string name) { throw null; }
        public bool RemoveMetadata(string name) { throw null; }
        public void Rename(string name) { }
        public Microsoft.Build.Evaluation.ProjectMetadata SetMetadataValue(string name, string unevaluatedValue) { throw null; }
        public Microsoft.Build.Evaluation.ProjectMetadata SetMetadataValue(string name, string unevaluatedValue, bool propagateMetadataToSiblingItems) { throw null; }
    }
    public partial class ProjectItemDefinition
    {
        internal ProjectItemDefinition() { }
        public string ItemType { get { throw null; } }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Evaluation.ProjectMetadata> Metadata { get { throw null; } }
        public int MetadataCount { get { throw null; } }
        public Microsoft.Build.Evaluation.Project Project { get { throw null; } }
        public Microsoft.Build.Evaluation.ProjectMetadata GetMetadata(string name) { throw null; }
        public string GetMetadataValue(string name) { throw null; }
        public Microsoft.Build.Evaluation.ProjectMetadata SetMetadataValue(string name, string unevaluatedValue) { throw null; }
    }
    [System.FlagsAttribute]
    public enum ProjectLoadSettings
    {
        Default = 0,
        IgnoreMissingImports = 1,
        RecordDuplicateButNotCircularImports = 2,
        RejectCircularImports = 4,
        RecordEvaluatedItemElements = 8,
        IgnoreEmptyImports = 16,
        DoNotEvaluateElementsWithFalseCondition = 32,
        IgnoreInvalidImports = 64,
        ProfileEvaluation = 128,
        FailOnUnresolvedSdk = 256,
    }
    public partial class ProjectMetadata : System.IEquatable<Microsoft.Build.Evaluation.ProjectMetadata>
    {
        internal ProjectMetadata() { }
        public Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string EvaluatedValue { get { throw null; } }
        public bool IsImported { get { throw null; } }
        public string ItemType { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string Name { get { throw null; } }
        public Microsoft.Build.Evaluation.ProjectMetadata Predecessor { get { throw null; } }
        public Microsoft.Build.Evaluation.Project Project { get { throw null; } }
        public string UnevaluatedValue { get { throw null; } set { } }
        public Microsoft.Build.Construction.ProjectMetadataElement Xml { get { throw null; } }
        bool System.IEquatable<Microsoft.Build.Evaluation.ProjectMetadata>.Equals(Microsoft.Build.Evaluation.ProjectMetadata other) { throw null; }
    }
    public abstract partial class ProjectProperty : System.IEquatable<Microsoft.Build.Evaluation.ProjectProperty>
    {
        internal ProjectProperty() { }
        public string EvaluatedValue { get { throw null; } }
        public abstract bool IsEnvironmentProperty { get; }
        public abstract bool IsGlobalProperty { get; }
        public abstract bool IsImported { get; }
        public abstract bool IsReservedProperty { get; }
        public abstract string Name { get; }
        public abstract Microsoft.Build.Evaluation.ProjectProperty Predecessor { get; }
        public Microsoft.Build.Evaluation.Project Project { get { throw null; } }
        public abstract string UnevaluatedValue { get; set; }
        public abstract Microsoft.Build.Construction.ProjectPropertyElement Xml { get; }
        bool System.IEquatable<Microsoft.Build.Evaluation.ProjectProperty>.Equals(Microsoft.Build.Evaluation.ProjectProperty other) { throw null; }
    }
    public partial class ProjectXmlChangedEventArgs : System.EventArgs
    {
        internal ProjectXmlChangedEventArgs() { }
        public Microsoft.Build.Construction.ProjectRootElement ProjectXml { get { throw null; } }
        public string Reason { get { throw null; } }
    }
    [System.FlagsAttribute]
    public enum Provenance
    {
        Undefined = 0,
        StringLiteral = 1,
        Glob = 2,
        Inconclusive = 4,
    }
    public partial class ProvenanceResult
    {
        public ProvenanceResult(Microsoft.Build.Construction.ProjectItemElement itemElement, Microsoft.Build.Evaluation.Operation operation, Microsoft.Build.Evaluation.Provenance provenance, int occurrences) { }
        public Microsoft.Build.Construction.ProjectItemElement ItemElement { get { throw null; } }
        public int Occurrences { get { throw null; } }
        public Microsoft.Build.Evaluation.Operation Operation { get { throw null; } }
        public Microsoft.Build.Evaluation.Provenance Provenance { get { throw null; } }
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public partial struct ResolvedImport
    {
        private object _dummy;
        private int _dummyPrimitive;
        public Microsoft.Build.Construction.ProjectRootElement ImportedProject { get { throw null; } }
        public Microsoft.Build.Construction.ProjectImportElement ImportingElement { get { throw null; } }
        public bool IsImported { get { throw null; } }
        public Microsoft.Build.Framework.SdkResult SdkResult { get { throw null; } }
    }
    public partial class SubToolset
    {
        internal SubToolset() { }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectPropertyInstance> Properties { get { throw null; } }
        public string SubToolsetVersion { get { throw null; } }
    }
    public partial class Toolset
    {
        public Toolset(string toolsVersion, string toolsPath, Microsoft.Build.Evaluation.ProjectCollection projectCollection, string msbuildOverrideTasksPath) { }
        public Toolset(string toolsVersion, string toolsPath, System.Collections.Generic.IDictionary<string, string> buildProperties, Microsoft.Build.Evaluation.ProjectCollection projectCollection, System.Collections.Generic.IDictionary<string, Microsoft.Build.Evaluation.SubToolset> subToolsets, string msbuildOverrideTasksPath) { }
        public Toolset(string toolsVersion, string toolsPath, System.Collections.Generic.IDictionary<string, string> buildProperties, Microsoft.Build.Evaluation.ProjectCollection projectCollection, string msbuildOverrideTasksPath) { }
        public string DefaultSubToolsetVersion { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectPropertyInstance> Properties { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Evaluation.SubToolset> SubToolsets { get { throw null; } }
        public string ToolsPath { get { throw null; } }
        public string ToolsVersion { get { throw null; } }
        public string GenerateSubToolsetVersion() { throw null; }
        public string GenerateSubToolsetVersion(System.Collections.Generic.IDictionary<string, string> overrideGlobalProperties, int solutionVersion) { throw null; }
        public Microsoft.Build.Execution.ProjectPropertyInstance GetProperty(string propertyName, string subToolsetVersion) { throw null; }
    }
    [System.FlagsAttribute]
    public enum ToolsetDefinitionLocations
    {
        None = 0,
        ConfigurationFile = 1,
        Registry = 2,
        Default = 4,
        Local = 4,
    }
}
namespace Microsoft.Build.Evaluation.Context
{
    public partial class EvaluationContext
    {
        internal EvaluationContext() { }
        public static Microsoft.Build.Evaluation.Context.EvaluationContext Create(Microsoft.Build.Evaluation.Context.EvaluationContext.SharingPolicy policy) { throw null; }
        public static Microsoft.Build.Evaluation.Context.EvaluationContext Create(Microsoft.Build.Evaluation.Context.EvaluationContext.SharingPolicy policy, Microsoft.Build.FileSystem.MSBuildFileSystemBase fileSystem) { throw null; }
        public enum SharingPolicy
        {
            Shared = 0,
            Isolated = 1,
        }
    }
}
namespace Microsoft.Build.Exceptions
{
    public partial class BuildAbortedException : System.Exception
    {
        public BuildAbortedException() { }
        protected BuildAbortedException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
        public BuildAbortedException(string message) { }
        public BuildAbortedException(string message, System.Exception innerException) { }
        public string ErrorCode { get { throw null; } }
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
    public partial class CircularDependencyException : System.Exception
    {
        protected CircularDependencyException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
    public sealed partial class InternalLoggerException : System.Exception
    {
        public InternalLoggerException() { }
        public InternalLoggerException(string message) { }
        public InternalLoggerException(string message, System.Exception innerException) { }
        public Microsoft.Build.Framework.BuildEventArgs BuildEventArgs { get { throw null; } }
        public string ErrorCode { get { throw null; } }
        public string HelpKeyword { get { throw null; } }
        public bool InitializationException { get { throw null; } }
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
    public sealed partial class InvalidProjectFileException : System.Exception
    {
        public InvalidProjectFileException() { }
        public InvalidProjectFileException(string message) { }
        public InvalidProjectFileException(string message, System.Exception innerException) { }
        public InvalidProjectFileException(string projectFile, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber, string message, string errorSubcategory, string errorCode, string helpKeyword) { }
        public string BaseMessage { get { throw null; } }
        public int ColumnNumber { get { throw null; } }
        public int EndColumnNumber { get { throw null; } }
        public int EndLineNumber { get { throw null; } }
        public string ErrorCode { get { throw null; } }
        public string ErrorSubcategory { get { throw null; } }
        public bool HasBeenLogged { get { throw null; } }
        public string HelpKeyword { get { throw null; } }
        public int LineNumber { get { throw null; } }
        public override string Message { get { throw null; } }
        public string ProjectFile { get { throw null; } }
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
    public partial class InvalidToolsetDefinitionException : System.Exception
    {
        public InvalidToolsetDefinitionException() { }
        protected InvalidToolsetDefinitionException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
        public InvalidToolsetDefinitionException(string message) { }
        public InvalidToolsetDefinitionException(string message, System.Exception innerException) { }
        public InvalidToolsetDefinitionException(string message, string errorCode) { }
        public InvalidToolsetDefinitionException(string message, string errorCode, System.Exception innerException) { }
        public string ErrorCode { get { throw null; } }
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
}
namespace Microsoft.Build.Execution
{
    public partial class BuildManager : System.IDisposable
    {
        public BuildManager() { }
        public BuildManager(string hostName) { }
        public static Microsoft.Build.Execution.BuildManager DefaultBuildManager { get { throw null; } }
        public void BeginBuild(Microsoft.Build.Execution.BuildParameters parameters) { }
        public void BeginBuild(Microsoft.Build.Execution.BuildParameters parameters, System.Collections.Generic.IEnumerable<Microsoft.Build.Execution.BuildManager.DeferredBuildMessage> deferredBuildMessages) { }
        public Microsoft.Build.Execution.BuildResult Build(Microsoft.Build.Execution.BuildParameters parameters, Microsoft.Build.Execution.BuildRequestData requestData) { throw null; }
        public Microsoft.Build.Graph.GraphBuildResult Build(Microsoft.Build.Execution.BuildParameters parameters, Microsoft.Build.Graph.GraphBuildRequestData requestData) { throw null; }
        public Microsoft.Build.Execution.BuildResult BuildRequest(Microsoft.Build.Execution.BuildRequestData requestData) { throw null; }
        public Microsoft.Build.Graph.GraphBuildResult BuildRequest(Microsoft.Build.Graph.GraphBuildRequestData requestData) { throw null; }
        public void CancelAllSubmissions() { }
        public void Dispose() { }
        public void EndBuild() { }
        ~BuildManager() { }
        public Microsoft.Build.Execution.ProjectInstance GetProjectInstanceForBuild(Microsoft.Build.Evaluation.Project project) { throw null; }
        public Microsoft.Build.Execution.BuildSubmission PendBuildRequest(Microsoft.Build.Execution.BuildRequestData requestData) { throw null; }
        public Microsoft.Build.Graph.GraphBuildSubmission PendBuildRequest(Microsoft.Build.Graph.GraphBuildRequestData requestData) { throw null; }
        public void ResetCaches() { }
        public void ShutdownAllNodes() { }
        [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public readonly partial struct DeferredBuildMessage
        {
            private readonly object _dummy;
            private readonly int _dummyPrimitive;
            public DeferredBuildMessage(string text, Microsoft.Build.Framework.MessageImportance importance) { throw null; }
            public Microsoft.Build.Framework.MessageImportance Importance { get { throw null; } }
            public string Text { get { throw null; } }
        }
    }
    public partial class BuildParameters
    {
        public BuildParameters() { }
        public BuildParameters(Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public bool AllowFailureWithoutError { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, string> BuildProcessEnvironment { get { throw null; } }
        public System.Globalization.CultureInfo Culture { get { throw null; } set { } }
        public string DefaultToolsVersion { get { throw null; } set { } }
        public bool DetailedSummary { get { throw null; } set { } }
        public bool DisableInProcNode { get { throw null; } set { } }
        public bool DiscardBuildResults { get { throw null; } set { } }
        public bool EnableNodeReuse { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, string> EnvironmentProperties { get { throw null; } }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> ForwardingLoggers { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } set { } }
        public Microsoft.Build.Execution.HostServices HostServices { get { throw null; } set { } }
        public string[] InputResultsCacheFiles { get { throw null; } set { } }
        public bool Interactive { get { throw null; } set { } }
        public bool IsolateProjects { get { throw null; } set { } }
        public bool LegacyThreadingSemantics { get { throw null; } set { } }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> Loggers { get { throw null; } set { } }
        public bool LogInitialPropertiesAndItems { get { throw null; } set { } }
        public bool LogTaskInputs { get { throw null; } set { } }
        public bool LowPriority { get { throw null; } set { } }
        public int MaxNodeCount { get { throw null; } set { } }
        public int MemoryUseLimit { get { throw null; } set { } }
        public string NodeExeLocation { get { throw null; } set { } }
        public bool OnlyLogCriticalEvents { get { throw null; } set { } }
        public string OutputResultsCacheFile { get { throw null; } set { } }
        public Microsoft.Build.Experimental.ProjectCache.ProjectCacheDescriptor ProjectCacheDescriptor { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.ProjectLoadSettings ProjectLoadSettings { get { throw null; } set { } }
        public bool ResetCaches { get { throw null; } set { } }
        public bool SaveOperatingEnvironment { get { throw null; } set { } }
        public bool ShutdownInProcNodeOnBuildFinish { get { throw null; } set { } }
        public Microsoft.Build.Evaluation.ToolsetDefinitionLocations ToolsetDefinitionLocations { get { throw null; } set { } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.Toolset> Toolsets { get { throw null; } }
        public System.Globalization.CultureInfo UICulture { get { throw null; } set { } }
        public bool UseSynchronousLogging { get { throw null; } set { } }
        public System.Collections.Generic.ISet<string> WarningsAsErrors { get { throw null; } set { } }
        public System.Collections.Generic.ISet<string> WarningsAsMessages { get { throw null; } set { } }
        public Microsoft.Build.Execution.BuildParameters Clone() { throw null; }
        public Microsoft.Build.Evaluation.Toolset GetToolset(string toolsVersion) { throw null; }
    }
    public partial class BuildRequestData
    {
        public BuildRequestData(Microsoft.Build.Execution.ProjectInstance projectInstance, string[] targetsToBuild) { }
        public BuildRequestData(Microsoft.Build.Execution.ProjectInstance projectInstance, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public BuildRequestData(Microsoft.Build.Execution.ProjectInstance projectInstance, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public BuildRequestData(Microsoft.Build.Execution.ProjectInstance projectInstance, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags, System.Collections.Generic.IEnumerable<string> propertiesToTransfer) { }
        public BuildRequestData(Microsoft.Build.Execution.ProjectInstance projectInstance, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags, System.Collections.Generic.IEnumerable<string> propertiesToTransfer, Microsoft.Build.Execution.RequestedProjectState requestedProjectState) { }
        public BuildRequestData(string projectFullPath, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public BuildRequestData(string projectFullPath, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public BuildRequestData(string projectFullPath, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string[] targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags, Microsoft.Build.Execution.RequestedProjectState requestedProjectState) { }
        public string ExplicitlySpecifiedToolsVersion { get { throw null; } }
        public Microsoft.Build.Execution.BuildRequestDataFlags Flags { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectPropertyInstance> GlobalProperties { get { throw null; } }
        public Microsoft.Build.Execution.HostServices HostServices { get { throw null; } }
        public string ProjectFullPath { get { throw null; } }
        public Microsoft.Build.Execution.ProjectInstance ProjectInstance { get { throw null; } }
        public System.Collections.Generic.IEnumerable<string> PropertiesToTransfer { get { throw null; } }
        public Microsoft.Build.Execution.RequestedProjectState RequestedProjectState { get { throw null; } }
        public System.Collections.Generic.ICollection<string> TargetNames { get { throw null; } }
    }
    [System.FlagsAttribute]
    public enum BuildRequestDataFlags
    {
        None = 0,
        ReplaceExistingProjectInstance = 1,
        ProvideProjectStateAfterBuild = 2,
        IgnoreExistingProjectState = 4,
        ClearCachesAfterBuild = 8,
        SkipNonexistentTargets = 16,
        ProvideSubsetOfStateAfterBuild = 32,
        IgnoreMissingEmptyAndInvalidImports = 64,
        FailOnUnresolvedSdk = 128,
    }
    public partial class BuildResult
    {
        public BuildResult() { }
        public bool CircularDependency { get { throw null; } }
        public int ConfigurationId { get { throw null; } }
        public System.Exception Exception { get { throw null; } }
        public int GlobalRequestId { get { throw null; } }
        public Microsoft.Build.Execution.ITargetResult this[string target] { get { throw null; } }
        public int NodeRequestId { get { throw null; } }
        public Microsoft.Build.Execution.BuildResultCode OverallResult { get { throw null; } }
        public int ParentGlobalRequestId { get { throw null; } }
        public Microsoft.Build.Execution.ProjectInstance ProjectStateAfterBuild { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.TargetResult> ResultsByTarget { get { throw null; } }
        public int SubmissionId { get { throw null; } }
        public void AddResultsForTarget(string target, Microsoft.Build.Execution.TargetResult result) { }
        public bool HasResultsForTarget(string target) { throw null; }
        public void MergeResults(Microsoft.Build.Execution.BuildResult results) { }
    }
    public enum BuildResultCode
    {
        Success = 0,
        Failure = 1,
    }
    public partial class BuildSubmission
    {
        internal BuildSubmission() { }
        public object AsyncContext { get { throw null; } }
        public Microsoft.Build.Execution.BuildManager BuildManager { get { throw null; } }
        public Microsoft.Build.Execution.BuildResult BuildResult { get { throw null; } set { } }
        public bool IsCompleted { get { throw null; } }
        public int SubmissionId { get { throw null; } }
        public System.Threading.WaitHandle WaitHandle { get { throw null; } }
        public Microsoft.Build.Execution.BuildResult Execute() { throw null; }
        public void ExecuteAsync(Microsoft.Build.Execution.BuildSubmissionCompleteCallback callback, object context) { }
    }
    public delegate void BuildSubmissionCompleteCallback(Microsoft.Build.Execution.BuildSubmission submission);
    public partial class HostServices
    {
        public HostServices() { }
        public Microsoft.Build.Framework.ITaskHost GetHostObject(string projectFile, string targetName, string taskName) { throw null; }
        public Microsoft.Build.Execution.NodeAffinity GetNodeAffinity(string projectFile) { throw null; }
        public void OnRenameProject(string oldFullPath, string newFullPath) { }
        public void RegisterHostObject(string projectFile, string targetName, string taskName, Microsoft.Build.Framework.ITaskHost hostObject) { }
        public void SetNodeAffinity(string projectFile, Microsoft.Build.Execution.NodeAffinity nodeAffinity) { }
        public void UnregisterProject(string projectFullPath) { }
    }
    public partial interface ITargetResult
    {
        System.Exception Exception { get; }
        Microsoft.Build.Framework.ITaskItem[] Items { get; }
        Microsoft.Build.Execution.TargetResultCode ResultCode { get; }
    }
    public enum NodeAffinity
    {
        InProc = 0,
        OutOfProc = 1,
        Any = 2,
    }
    public enum NodeEngineShutdownReason
    {
        BuildComplete = 0,
        BuildCompleteReuse = 1,
        ConnectionFailed = 2,
        Error = 3,
    }
    public partial class OutOfProcNode
    {
        public OutOfProcNode() { }
        public Microsoft.Build.Execution.NodeEngineShutdownReason Run(bool enableReuse, bool lowPriority, out System.Exception shutdownException) { throw null; }
        public Microsoft.Build.Execution.NodeEngineShutdownReason Run(bool enableReuse, out System.Exception shutdownException) { throw null; }
        public Microsoft.Build.Execution.NodeEngineShutdownReason Run(out System.Exception shutdownException) { throw null; }
    }
    public partial class ProjectInstance
    {
        public ProjectInstance(Microsoft.Build.Construction.ProjectRootElement xml) { }
        public ProjectInstance(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectInstance(Microsoft.Build.Construction.ProjectRootElement xml, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectInstance(Microsoft.Build.Evaluation.Project project, Microsoft.Build.Execution.ProjectInstanceSettings settings) { }
        public ProjectInstance(string projectFile) { }
        public ProjectInstance(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion) { }
        public ProjectInstance(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectInstance(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, string toolsVersion, string subToolsetVersion, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public System.Collections.Generic.List<string> DefaultTargets { get { throw null; } }
        public string Directory { get { throw null; } }
        public System.Collections.Generic.List<Microsoft.Build.Construction.ProjectItemElement> EvaluatedItemElements { get { throw null; } }
        public int EvaluationId { get { throw null; } set { } }
        public string FullPath { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } }
        public System.Collections.Generic.IReadOnlyList<string> ImportPaths { get { throw null; } }
        public System.Collections.Generic.IReadOnlyList<string> ImportPathsIncludingDuplicates { get { throw null; } }
        public System.Collections.Generic.List<string> InitialTargets { get { throw null; } }
        public bool IsImmutable { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectItemDefinitionInstance> ItemDefinitions { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectItemInstance> Items { get { throw null; } }
        public System.Collections.Generic.ICollection<string> ItemTypes { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ProjectFileLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectPropertyInstance> Properties { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectTargetInstance> Targets { get { throw null; } }
        public string ToolsVersion { get { throw null; } }
        public bool TranslateEntireState { get { throw null; } set { } }
        public Microsoft.Build.Execution.ProjectItemInstance AddItem(string itemType, string evaluatedInclude) { throw null; }
        public Microsoft.Build.Execution.ProjectItemInstance AddItem(string itemType, string evaluatedInclude, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata) { throw null; }
        public bool Build() { throw null; }
        public bool Build(System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string target, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(string target, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, out System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.TargetResult> targetOutputs) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers) { throw null; }
        public bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers, out System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.TargetResult> targetOutputs) { throw null; }
        public Microsoft.Build.Execution.ProjectInstance DeepCopy() { throw null; }
        public Microsoft.Build.Execution.ProjectInstance DeepCopy(bool isImmutable) { throw null; }
        public bool EvaluateCondition(string condition) { throw null; }
        public string ExpandString(string unexpandedValue) { throw null; }
        public Microsoft.Build.Execution.ProjectInstance FilteredCopy(Microsoft.Build.Execution.RequestedProjectState filter) { throw null; }
        public static Microsoft.Build.Execution.ProjectInstance FromFile(string file, Microsoft.Build.Definition.ProjectOptions options) { throw null; }
        public static Microsoft.Build.Execution.ProjectInstance FromProjectRootElement(Microsoft.Build.Construction.ProjectRootElement rootElement, Microsoft.Build.Definition.ProjectOptions options) { throw null; }
        public static string GetEvaluatedItemIncludeEscaped(Microsoft.Build.Execution.ProjectItemDefinitionInstance item) { throw null; }
        public static string GetEvaluatedItemIncludeEscaped(Microsoft.Build.Execution.ProjectItemInstance item) { throw null; }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectItemInstance> GetItems(string itemType) { throw null; }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Execution.ProjectItemInstance> GetItemsByItemTypeAndEvaluatedInclude(string itemType, string evaluatedInclude) { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Execution.ProjectItemDefinitionInstance item, string name) { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Execution.ProjectItemInstance item, string name) { throw null; }
        public static string GetMetadataValueEscaped(Microsoft.Build.Execution.ProjectMetadataInstance metadatum) { throw null; }
        public Microsoft.Build.Execution.ProjectPropertyInstance GetProperty(string name) { throw null; }
        public string GetPropertyValue(string name) { throw null; }
        public static string GetPropertyValueEscaped(Microsoft.Build.Execution.ProjectPropertyInstance property) { throw null; }
        public bool RemoveItem(Microsoft.Build.Execution.ProjectItemInstance item) { throw null; }
        public bool RemoveProperty(string name) { throw null; }
        public Microsoft.Build.Execution.ProjectPropertyInstance SetProperty(string name, string evaluatedValue) { throw null; }
        public Microsoft.Build.Construction.ProjectRootElement ToProjectRootElement() { throw null; }
        public void UpdateStateFrom(Microsoft.Build.Execution.ProjectInstance projectState) { }
    }
    [System.FlagsAttribute]
    public enum ProjectInstanceSettings
    {
        None = 0,
        Immutable = 1,
        ImmutableWithFastItemLookup = 3,
    }
    public partial class ProjectItemDefinitionInstance
    {
        internal ProjectItemDefinitionInstance() { }
        public string ItemType { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectMetadataInstance> Metadata { get { throw null; } }
        public int MetadataCount { get { throw null; } }
        public System.Collections.Generic.IEnumerable<string> MetadataNames { get { throw null; } }
        public Microsoft.Build.Execution.ProjectMetadataInstance GetMetadata(string name) { throw null; }
    }
    public partial class ProjectItemGroupTaskInstance : Microsoft.Build.Execution.ProjectTargetInstanceChild
    {
        internal ProjectItemGroupTaskInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectItemGroupTaskItemInstance> Items { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
    }
    public partial class ProjectItemGroupTaskItemInstance
    {
        internal ProjectItemGroupTaskItemInstance() { }
        public string Condition { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string Exclude { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ExcludeLocation { get { throw null; } }
        public string Include { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation IncludeLocation { get { throw null; } }
        public string ItemType { get { throw null; } }
        public string KeepDuplicates { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation KeepDuplicatesLocation { get { throw null; } }
        public string KeepMetadata { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation KeepMetadataLocation { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string MatchOnMetadata { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation MatchOnMetadataLocation { get { throw null; } }
        public string MatchOnMetadataOptions { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation MatchOnMetadataOptionsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectItemGroupTaskMetadataInstance> Metadata { get { throw null; } }
        public string Remove { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation RemoveLocation { get { throw null; } }
        public string RemoveMetadata { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation RemoveMetadataLocation { get { throw null; } }
    }
    public partial class ProjectItemGroupTaskMetadataInstance
    {
        internal ProjectItemGroupTaskMetadataInstance() { }
        public string Condition { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string Name { get { throw null; } }
        public string Value { get { throw null; } }
    }
    public partial class ProjectItemInstance : Microsoft.Build.Framework.ITaskItem, Microsoft.Build.Framework.ITaskItem2
    {
        internal ProjectItemInstance() { }
        public int DirectMetadataCount { get { throw null; } }
        public string EvaluatedInclude { get { throw null; } set { } }
        public string ItemType { get { throw null; } }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Execution.ProjectMetadataInstance> Metadata { get { throw null; } }
        public int MetadataCount { get { throw null; } }
        public System.Collections.Generic.ICollection<string> MetadataNames { get { throw null; } }
        string Microsoft.Build.Framework.ITaskItem.ItemSpec { get { throw null; } set { } }
        System.Collections.ICollection Microsoft.Build.Framework.ITaskItem.MetadataNames { get { throw null; } }
        string Microsoft.Build.Framework.ITaskItem2.EvaluatedIncludeEscaped { get { throw null; } set { } }
        public Microsoft.Build.Execution.ProjectInstance Project { get { throw null; } }
        public Microsoft.Build.Execution.ProjectMetadataInstance GetMetadata(string name) { throw null; }
        public string GetMetadataValue(string name) { throw null; }
        public bool HasMetadata(string name) { throw null; }
        System.Collections.IDictionary Microsoft.Build.Framework.ITaskItem.CloneCustomMetadata() { throw null; }
        void Microsoft.Build.Framework.ITaskItem.CopyMetadataTo(Microsoft.Build.Framework.ITaskItem destinationItem) { }
        string Microsoft.Build.Framework.ITaskItem.GetMetadata(string metadataName) { throw null; }
        void Microsoft.Build.Framework.ITaskItem.SetMetadata(string metadataName, string metadataValue) { }
        System.Collections.IDictionary Microsoft.Build.Framework.ITaskItem2.CloneCustomMetadataEscaped() { throw null; }
        string Microsoft.Build.Framework.ITaskItem2.GetMetadataValueEscaped(string name) { throw null; }
        void Microsoft.Build.Framework.ITaskItem2.SetMetadataValueLiteral(string metadataName, string metadataValue) { }
        public void RemoveMetadata(string metadataName) { }
        public void SetMetadata(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadataDictionary) { }
        public Microsoft.Build.Execution.ProjectMetadataInstance SetMetadata(string name, string evaluatedValue) { throw null; }
        public override string ToString() { throw null; }
    }
    public partial class ProjectMetadataInstance : System.IEquatable<Microsoft.Build.Execution.ProjectMetadataInstance>
    {
        internal ProjectMetadataInstance() { }
        public string EvaluatedValue { get { throw null; } }
        public string Name { get { throw null; } }
        public Microsoft.Build.Execution.ProjectMetadataInstance DeepClone() { throw null; }
        bool System.IEquatable<Microsoft.Build.Execution.ProjectMetadataInstance>.Equals(Microsoft.Build.Execution.ProjectMetadataInstance other) { throw null; }
        public override string ToString() { throw null; }
    }
    public sealed partial class ProjectOnErrorInstance : Microsoft.Build.Execution.ProjectTargetInstanceChild
    {
        internal ProjectOnErrorInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string ExecuteTargets { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ExecuteTargetsLocation { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
    }
    public partial class ProjectPropertyGroupTaskInstance : Microsoft.Build.Execution.ProjectTargetInstanceChild
    {
        internal ProjectPropertyGroupTaskInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectPropertyGroupTaskPropertyInstance> Properties { get { throw null; } }
    }
    public partial class ProjectPropertyGroupTaskPropertyInstance
    {
        internal ProjectPropertyGroupTaskPropertyInstance() { }
        public string Condition { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string Name { get { throw null; } }
        public string Value { get { throw null; } }
    }
    public partial class ProjectPropertyInstance : System.IEquatable<Microsoft.Build.Execution.ProjectPropertyInstance>
    {
        internal ProjectPropertyInstance() { }
        public string EvaluatedValue { get { throw null; } set { } }
        public virtual bool IsImmutable { get { throw null; } }
        public string Name { get { throw null; } }
        bool System.IEquatable<Microsoft.Build.Execution.ProjectPropertyInstance>.Equals(Microsoft.Build.Execution.ProjectPropertyInstance other) { throw null; }
        public override string ToString() { throw null; }
    }
    public sealed partial class ProjectTargetInstance
    {
        internal ProjectTargetInstance() { }
        public string AfterTargets { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation AfterTargetsLocation { get { throw null; } }
        public string BeforeTargets { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation BeforeTargetsLocation { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Execution.ProjectTargetInstanceChild> Children { get { throw null; } }
        public string Condition { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string DependsOnTargets { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation DependsOnTargetsLocation { get { throw null; } }
        public string FullPath { get { throw null; } }
        public string Inputs { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation InputsLocation { get { throw null; } }
        public string KeepDuplicateOutputs { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation KeepDuplicateOutputsLocation { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string Name { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Execution.ProjectOnErrorInstance> OnErrorChildren { get { throw null; } }
        public string Outputs { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation OutputsLocation { get { throw null; } }
        public string Returns { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ReturnsLocation { get { throw null; } }
        public System.Collections.Generic.ICollection<Microsoft.Build.Execution.ProjectTaskInstance> Tasks { get { throw null; } }
    }
    public abstract partial class ProjectTargetInstanceChild
    {
        protected ProjectTargetInstanceChild() { }
        public abstract string Condition { get; }
        public abstract Microsoft.Build.Construction.ElementLocation ConditionLocation { get; }
        public string FullPath { get { throw null; } }
        public abstract Microsoft.Build.Construction.ElementLocation Location { get; }
    }
    public sealed partial class ProjectTaskInstance : Microsoft.Build.Execution.ProjectTargetInstanceChild
    {
        internal ProjectTaskInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string ContinueOnError { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ContinueOnErrorLocation { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string MSBuildArchitecture { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation MSBuildArchitectureLocation { get { throw null; } }
        public string MSBuildRuntime { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation MSBuildRuntimeLocation { get { throw null; } }
        public string Name { get { throw null; } }
        public System.Collections.Generic.IList<Microsoft.Build.Execution.ProjectTaskInstanceChild> Outputs { get { throw null; } }
        public System.Collections.Generic.IDictionary<string, string> Parameters { get { throw null; } }
    }
    public abstract partial class ProjectTaskInstanceChild
    {
        protected ProjectTaskInstanceChild() { }
        public abstract string Condition { get; }
        public abstract Microsoft.Build.Construction.ElementLocation ConditionLocation { get; }
        public abstract Microsoft.Build.Construction.ElementLocation Location { get; }
        public abstract Microsoft.Build.Construction.ElementLocation TaskParameterLocation { get; }
    }
    public sealed partial class ProjectTaskOutputItemInstance : Microsoft.Build.Execution.ProjectTaskInstanceChild
    {
        internal ProjectTaskOutputItemInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public string ItemType { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation ItemTypeLocation { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string TaskParameter { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation TaskParameterLocation { get { throw null; } }
    }
    public sealed partial class ProjectTaskOutputPropertyInstance : Microsoft.Build.Execution.ProjectTaskInstanceChild
    {
        internal ProjectTaskOutputPropertyInstance() { }
        public override string Condition { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation ConditionLocation { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation Location { get { throw null; } }
        public string PropertyName { get { throw null; } }
        public Microsoft.Build.Construction.ElementLocation PropertyNameLocation { get { throw null; } }
        public string TaskParameter { get { throw null; } }
        public override Microsoft.Build.Construction.ElementLocation TaskParameterLocation { get { throw null; } }
    }
    public partial class RequestedProjectState
    {
        public RequestedProjectState() { }
        public System.Collections.Generic.IDictionary<string, System.Collections.Generic.List<string>> ItemFilters { get { throw null; } set { } }
        public System.Collections.Generic.List<string> PropertyFilters { get { throw null; } set { } }
    }
    public partial class TargetResult : Microsoft.Build.Execution.ITargetResult
    {
        internal TargetResult() { }
        public System.Exception Exception { get { throw null; } }
        public Microsoft.Build.Framework.ITaskItem[] Items { get { throw null; } }
        public Microsoft.Build.Execution.TargetResultCode ResultCode { get { throw null; } }
    }
    public enum TargetResultCode : byte
    {
        Skipped = (byte)0,
        Success = (byte)1,
        Failure = (byte)2,
    }
}
namespace Microsoft.Build.Experimental.ProjectCache
{
    public partial class CacheContext
    {
        public CacheContext(System.Collections.Generic.IReadOnlyDictionary<string, string> pluginSettings, Microsoft.Build.FileSystem.MSBuildFileSystemBase fileSystem, Microsoft.Build.Graph.ProjectGraph graph = null, System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphEntryPoint> graphEntryPoints = null) { }
        public Microsoft.Build.FileSystem.MSBuildFileSystemBase FileSystem { get { throw null; } }
        public Microsoft.Build.Graph.ProjectGraph Graph { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphEntryPoint> GraphEntryPoints { get { throw null; } }
        public string MSBuildExePath { get { throw null; } }
        public System.Collections.Generic.IReadOnlyDictionary<string, string> PluginSettings { get { throw null; } }
    }
    public partial class CacheResult
    {
        internal CacheResult() { }
        public Microsoft.Build.Execution.BuildResult BuildResult { get { throw null; } }
        public Microsoft.Build.Experimental.ProjectCache.ProxyTargets ProxyTargets { get { throw null; } }
        public Microsoft.Build.Experimental.ProjectCache.CacheResultType ResultType { get { throw null; } }
        public static Microsoft.Build.Experimental.ProjectCache.CacheResult IndicateCacheHit(Microsoft.Build.Execution.BuildResult buildResult) { throw null; }
        public static Microsoft.Build.Experimental.ProjectCache.CacheResult IndicateCacheHit(Microsoft.Build.Experimental.ProjectCache.ProxyTargets proxyTargets) { throw null; }
        public static Microsoft.Build.Experimental.ProjectCache.CacheResult IndicateCacheHit(System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Experimental.ProjectCache.PluginTargetResult> targetResults) { throw null; }
        public static Microsoft.Build.Experimental.ProjectCache.CacheResult IndicateNonCacheHit(Microsoft.Build.Experimental.ProjectCache.CacheResultType resultType) { throw null; }
    }
    public enum CacheResultType
    {
        None = 0,
        CacheHit = 1,
        CacheMiss = 2,
        CacheNotApplicable = 3,
    }
    public abstract partial class PluginLoggerBase
    {
        protected PluginLoggerBase(Microsoft.Build.Framework.LoggerVerbosity verbosity) { }
        public abstract bool HasLoggedErrors { get; protected set; }
        public abstract void LogError(string error);
        public abstract void LogMessage(string message, Microsoft.Build.Framework.MessageImportance? messageImportance = default(Microsoft.Build.Framework.MessageImportance?));
        public abstract void LogWarning(string warning);
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public readonly partial struct PluginTargetResult
    {
        private readonly object _dummy;
        private readonly int _dummyPrimitive;
        public PluginTargetResult(string targetName, System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Framework.ITaskItem2> taskItems, Microsoft.Build.Execution.BuildResultCode resultCode) { throw null; }
        public Microsoft.Build.Execution.BuildResultCode ResultCode { get { throw null; } }
        public string TargetName { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Framework.ITaskItem2> TaskItems { get { throw null; } }
    }
    public partial class ProjectCacheDescriptor
    {
        internal ProjectCacheDescriptor() { }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphEntryPoint> EntryPoints { get { throw null; } }
        public string PluginAssemblyPath { get { throw null; } }
        public Microsoft.Build.Experimental.ProjectCache.ProjectCachePluginBase PluginInstance { get { throw null; } }
        public System.Collections.Generic.IReadOnlyDictionary<string, string> PluginSettings { get { throw null; } }
        public Microsoft.Build.Graph.ProjectGraph ProjectGraph { get { throw null; } }
        public static Microsoft.Build.Experimental.ProjectCache.ProjectCacheDescriptor FromAssemblyPath(string pluginAssemblyPath, System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints, Microsoft.Build.Graph.ProjectGraph projectGraph, System.Collections.Generic.IReadOnlyDictionary<string, string> pluginSettings = null) { throw null; }
        public static Microsoft.Build.Experimental.ProjectCache.ProjectCacheDescriptor FromInstance(Microsoft.Build.Experimental.ProjectCache.ProjectCachePluginBase pluginInstance, System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints, Microsoft.Build.Graph.ProjectGraph projectGraph, System.Collections.Generic.IReadOnlyDictionary<string, string> pluginSettings = null) { throw null; }
        public string GetDetailedDescription() { throw null; }
    }
    public sealed partial class ProjectCacheException : System.Exception
    {
        internal ProjectCacheException() { }
        public string ErrorCode { get { throw null; } }
        public bool HasBeenLoggedByProjectCache { get { throw null; } }
    }
    public abstract partial class ProjectCachePluginBase
    {
        protected ProjectCachePluginBase() { }
        public abstract System.Threading.Tasks.Task BeginBuildAsync(Microsoft.Build.Experimental.ProjectCache.CacheContext context, Microsoft.Build.Experimental.ProjectCache.PluginLoggerBase logger, System.Threading.CancellationToken cancellationToken);
        public abstract System.Threading.Tasks.Task EndBuildAsync(Microsoft.Build.Experimental.ProjectCache.PluginLoggerBase logger, System.Threading.CancellationToken cancellationToken);
        public abstract System.Threading.Tasks.Task<Microsoft.Build.Experimental.ProjectCache.CacheResult> GetCacheResultAsync(Microsoft.Build.Execution.BuildRequestData buildRequest, Microsoft.Build.Experimental.ProjectCache.PluginLoggerBase logger, System.Threading.CancellationToken cancellationToken);
    }
    public partial class ProxyTargets
    {
        public ProxyTargets(System.Collections.Generic.IReadOnlyDictionary<string, string> proxyTargetToRealTargetMap) { }
        public System.Collections.Generic.IReadOnlyDictionary<string, string> ProxyTargetToRealTargetMap { get { throw null; } }
    }
}
namespace Microsoft.Build.FileSystem
{
    public delegate bool FindPredicate(ref System.ReadOnlySpan<char> fileName);
    public delegate TResult FindTransform<TResult>(ref System.ReadOnlySpan<char> fileName);
    public partial interface IDirectoryCache
    {
        bool DirectoryExists(string path);
        System.Collections.Generic.IEnumerable<TResult> EnumerateDirectories<TResult>(string path, string pattern, Microsoft.Build.FileSystem.FindPredicate predicate, Microsoft.Build.FileSystem.FindTransform<TResult> transform);
        System.Collections.Generic.IEnumerable<TResult> EnumerateFiles<TResult>(string path, string pattern, Microsoft.Build.FileSystem.FindPredicate predicate, Microsoft.Build.FileSystem.FindTransform<TResult> transform);
        bool FileExists(string path);
    }
    public partial interface IDirectoryCacheFactory
    {
        Microsoft.Build.FileSystem.IDirectoryCache GetDirectoryCacheForEvaluation(int evaluationId);
    }
    public abstract partial class MSBuildFileSystemBase
    {
        protected MSBuildFileSystemBase() { }
        public virtual bool DirectoryExists(string path) { throw null; }
        public virtual System.Collections.Generic.IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", System.IO.SearchOption searchOption = System.IO.SearchOption.TopDirectoryOnly) { throw null; }
        public virtual System.Collections.Generic.IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", System.IO.SearchOption searchOption = System.IO.SearchOption.TopDirectoryOnly) { throw null; }
        public virtual System.Collections.Generic.IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", System.IO.SearchOption searchOption = System.IO.SearchOption.TopDirectoryOnly) { throw null; }
        public virtual bool FileExists(string path) { throw null; }
        public virtual bool FileOrDirectoryExists(string path) { throw null; }
        public virtual System.IO.FileAttributes GetAttributes(string path) { throw null; }
        public virtual System.IO.Stream GetFileStream(string path, System.IO.FileMode mode, System.IO.FileAccess access, System.IO.FileShare share) { throw null; }
        public virtual System.DateTime GetLastWriteTimeUtc(string path) { throw null; }
        public virtual System.IO.TextReader ReadFile(string path) { throw null; }
        public virtual byte[] ReadFileAllBytes(string path) { throw null; }
        public virtual string ReadFileAllText(string path) { throw null; }
    }
}
namespace Microsoft.Build.Globbing
{
    public partial class CompositeGlob : Microsoft.Build.Globbing.IMSBuildGlob
    {
        public CompositeGlob(params Microsoft.Build.Globbing.IMSBuildGlob[] globs) { }
        public CompositeGlob(System.Collections.Generic.IEnumerable<Microsoft.Build.Globbing.IMSBuildGlob> globs) { }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Globbing.IMSBuildGlob> Globs { get { throw null; } }
        public bool IsMatch(string stringToMatch) { throw null; }
    }
    public partial interface IMSBuildGlob
    {
        bool IsMatch(string stringToMatch);
    }
    public partial class MSBuildGlob : Microsoft.Build.Globbing.IMSBuildGlob
    {
        internal MSBuildGlob() { }
        public string FilenamePart { get { throw null; } }
        public string FixedDirectoryPart { get { throw null; } }
        public bool IsLegal { get { throw null; } }
        public string WildcardDirectoryPart { get { throw null; } }
        public bool IsMatch(string stringToMatch) { throw null; }
        public Microsoft.Build.Globbing.MSBuildGlob.MatchInfoResult MatchInfo(string stringToMatch) { throw null; }
        public static Microsoft.Build.Globbing.MSBuildGlob Parse(string fileSpec) { throw null; }
        public static Microsoft.Build.Globbing.MSBuildGlob Parse(string globRoot, string fileSpec) { throw null; }
        [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public partial struct MatchInfoResult
        {
            private object _dummy;
            private int _dummyPrimitive;
            public string FilenamePartMatchGroup { get { throw null; } }
            public string FixedDirectoryPartMatchGroup { get { throw null; } }
            public bool IsMatch { get { throw null; } }
            public string WildcardDirectoryPartMatchGroup { get { throw null; } }
        }
    }
    public partial class MSBuildGlobWithGaps : Microsoft.Build.Globbing.IMSBuildGlob
    {
        public MSBuildGlobWithGaps(Microsoft.Build.Globbing.IMSBuildGlob mainGlob, params Microsoft.Build.Globbing.IMSBuildGlob[] gaps) { }
        public MSBuildGlobWithGaps(Microsoft.Build.Globbing.IMSBuildGlob mainGlob, System.Collections.Generic.IEnumerable<Microsoft.Build.Globbing.IMSBuildGlob> gaps) { }
        public Microsoft.Build.Globbing.IMSBuildGlob Gaps { get { throw null; } }
        public Microsoft.Build.Globbing.IMSBuildGlob MainGlob { get { throw null; } }
        public bool IsMatch(string stringToMatch) { throw null; }
    }
}
namespace Microsoft.Build.Globbing.Extensions
{
    public static partial class MSBuildGlobExtensions
    {
        public static System.Collections.Generic.IEnumerable<Microsoft.Build.Globbing.MSBuildGlob> GetParsedGlobs(this Microsoft.Build.Globbing.IMSBuildGlob glob) { throw null; }
    }
}
namespace Microsoft.Build.Graph
{
    public partial class GraphBuildOptions : System.IEquatable<Microsoft.Build.Graph.GraphBuildOptions>
    {
        public GraphBuildOptions() { }
        protected GraphBuildOptions(Microsoft.Build.Graph.GraphBuildOptions original) { }
        public bool Build { get { throw null; } set { } }
        protected virtual System.Type EqualityContract { get { throw null; } }
        public virtual bool Equals(Microsoft.Build.Graph.GraphBuildOptions other) { throw null; }
        public override bool Equals(object obj) { throw null; }
        public override int GetHashCode() { throw null; }
        public static bool operator ==(Microsoft.Build.Graph.GraphBuildOptions left, Microsoft.Build.Graph.GraphBuildOptions right) { throw null; }
        public static bool operator !=(Microsoft.Build.Graph.GraphBuildOptions left, Microsoft.Build.Graph.GraphBuildOptions right) { throw null; }
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) { throw null; }
        public override string ToString() { throw null; }
        public virtual Microsoft.Build.Graph.GraphBuildOptions <Clone>$() { throw null; }
    }
    public sealed partial class GraphBuildRequestData
    {
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraph projectGraph, System.Collections.Generic.ICollection<string> targetsToBuild) { }
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraph projectGraph, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraph projectGraph, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraphEntryPoint projectGraphEntryPoint, System.Collections.Generic.ICollection<string> targetsToBuild) { }
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraphEntryPoint projectGraphEntryPoint, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public GraphBuildRequestData(Microsoft.Build.Graph.ProjectGraphEntryPoint projectGraphEntryPoint, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public GraphBuildRequestData(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> projectGraphEntryPoints, System.Collections.Generic.ICollection<string> targetsToBuild) { }
        public GraphBuildRequestData(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> projectGraphEntryPoints, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public GraphBuildRequestData(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> projectGraphEntryPoints, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public GraphBuildRequestData(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> projectGraphEntryPoints, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags, Microsoft.Build.Graph.GraphBuildOptions graphBuildOptions) { }
        public GraphBuildRequestData(string projectFullPath, System.Collections.Generic.IDictionary<string, string> globalProperties, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices) { }
        public GraphBuildRequestData(string projectFullPath, System.Collections.Generic.IDictionary<string, string> globalProperties, System.Collections.Generic.ICollection<string> targetsToBuild, Microsoft.Build.Execution.HostServices hostServices, Microsoft.Build.Execution.BuildRequestDataFlags flags) { }
        public Microsoft.Build.Execution.BuildRequestDataFlags Flags { get { throw null; } }
        public Microsoft.Build.Graph.GraphBuildOptions GraphBuildOptions { get { throw null; } }
        public Microsoft.Build.Execution.HostServices HostServices { get { throw null; } }
        public Microsoft.Build.Graph.ProjectGraph ProjectGraph { get { throw null; } }
        public System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> ProjectGraphEntryPoints { get { throw null; } }
        public System.Collections.Generic.ICollection<string> TargetNames { get { throw null; } }
    }
    public sealed partial class GraphBuildResult
    {
        internal GraphBuildResult() { }
        public bool CircularDependency { get { throw null; } }
        public System.Exception Exception { get { throw null; } }
        public Microsoft.Build.Execution.BuildResult this[Microsoft.Build.Graph.ProjectGraphNode node] { get { throw null; } }
        public Microsoft.Build.Execution.BuildResultCode OverallResult { get { throw null; } }
        public System.Collections.Generic.IReadOnlyDictionary<Microsoft.Build.Graph.ProjectGraphNode, Microsoft.Build.Execution.BuildResult> ResultsByNode { get { throw null; } }
        public int SubmissionId { get { throw null; } }
    }
    public partial class GraphBuildSubmission
    {
        internal GraphBuildSubmission() { }
        public object AsyncContext { get { throw null; } }
        public Microsoft.Build.Execution.BuildManager BuildManager { get { throw null; } }
        public Microsoft.Build.Graph.GraphBuildResult BuildResult { get { throw null; } }
        public bool IsCompleted { get { throw null; } }
        public int SubmissionId { get { throw null; } }
        public System.Threading.WaitHandle WaitHandle { get { throw null; } }
        public Microsoft.Build.Graph.GraphBuildResult Execute() { throw null; }
        public void ExecuteAsync(Microsoft.Build.Graph.GraphBuildSubmissionCompleteCallback callback, object context) { }
    }
    public delegate void GraphBuildSubmissionCompleteCallback(Microsoft.Build.Graph.GraphBuildSubmission submission);
    public sealed partial class ProjectGraph
    {
        public ProjectGraph(Microsoft.Build.Graph.ProjectGraphEntryPoint entryPoint) { }
        public ProjectGraph(Microsoft.Build.Graph.ProjectGraphEntryPoint entryPoint, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Graph.ProjectGraph.ProjectInstanceFactoryFunc projectInstanceFactory) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Graph.ProjectGraph.ProjectInstanceFactoryFunc projectInstanceFactory, int degreeOfParallelism, System.Threading.CancellationToken cancellationToken) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<Microsoft.Build.Graph.ProjectGraphEntryPoint> entryPoints, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Graph.ProjectGraph.ProjectInstanceFactoryFunc projectInstanceFactory, System.Threading.CancellationToken cancellationToken) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<string> entryProjectFiles) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<string> entryProjectFiles, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<string> entryProjectFiles, System.Collections.Generic.IDictionary<string, string> globalProperties) { }
        public ProjectGraph(System.Collections.Generic.IEnumerable<string> entryProjectFiles, System.Collections.Generic.IDictionary<string, string> globalProperties, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectGraph(string entryProjectFile) { }
        public ProjectGraph(string entryProjectFile, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public ProjectGraph(string entryProjectFile, Microsoft.Build.Evaluation.ProjectCollection projectCollection, Microsoft.Build.Graph.ProjectGraph.ProjectInstanceFactoryFunc projectInstanceFactory) { }
        public ProjectGraph(string entryProjectFile, System.Collections.Generic.IDictionary<string, string> globalProperties) { }
        public ProjectGraph(string entryProjectFile, System.Collections.Generic.IDictionary<string, string> globalProperties, Microsoft.Build.Evaluation.ProjectCollection projectCollection) { }
        public Microsoft.Build.Graph.ProjectGraph.GraphConstructionMetrics ConstructionMetrics { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> EntryPointNodes { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> GraphRoots { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> ProjectNodes { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> ProjectNodesTopologicallySorted { get { throw null; } }
        public System.Collections.Generic.IReadOnlyDictionary<Microsoft.Build.Graph.ProjectGraphNode, System.Collections.Immutable.ImmutableList<string>> GetTargetLists(System.Collections.Generic.ICollection<string> entryProjectTargets) { throw null; }
        [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public readonly partial struct GraphConstructionMetrics
        {
            private readonly int _dummyPrimitive;
            public GraphConstructionMetrics(System.TimeSpan constructionTime, int nodeCount, int edgeCount) { throw null; }
            public System.TimeSpan ConstructionTime { get { throw null; } }
            public int EdgeCount { get { throw null; } }
            public int NodeCount { get { throw null; } }
        }
        public delegate Microsoft.Build.Execution.ProjectInstance ProjectInstanceFactoryFunc(string projectPath, System.Collections.Generic.Dictionary<string, string> globalProperties, Microsoft.Build.Evaluation.ProjectCollection projectCollection);
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public partial struct ProjectGraphEntryPoint
    {
        private object _dummy;
        public ProjectGraphEntryPoint(string projectFile) { throw null; }
        public ProjectGraphEntryPoint(string projectFile, System.Collections.Generic.IDictionary<string, string> globalProperties) { throw null; }
        public System.Collections.Generic.IDictionary<string, string> GlobalProperties { get { throw null; } }
        public string ProjectFile { get { throw null; } }
    }
    public sealed partial class ProjectGraphNode
    {
        internal ProjectGraphNode() { }
        public Microsoft.Build.Execution.ProjectInstance ProjectInstance { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> ProjectReferences { get { throw null; } }
        public System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Graph.ProjectGraphNode> ReferencingProjects { get { throw null; } }
    }
}
namespace Microsoft.Build.Logging
{
    public sealed partial class BinaryLogger : Microsoft.Build.Framework.ILogger
    {
        public BinaryLogger() { }
        public Microsoft.Build.Logging.BinaryLogger.ProjectImportsCollectionMode CollectProjectImports { get { throw null; } set { } }
        public string Parameters { get { throw null; } set { } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } set { } }
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public void Shutdown() { }
        public enum ProjectImportsCollectionMode
        {
            None = 0,
            Embed = 1,
            ZipFile = 2,
        }
    }
    public sealed partial class BinaryLogReplayEventSource : Microsoft.Build.Logging.EventArgsDispatcher
    {
        public BinaryLogReplayEventSource() { }
        public void Replay(string sourceFilePath) { }
        public void Replay(string sourceFilePath, System.Threading.CancellationToken cancellationToken) { }
    }
    public partial class BuildEventArgsReader : System.IDisposable
    {
        public BuildEventArgsReader(System.IO.BinaryReader binaryReader, int fileFormatVersion) { }
        public void Dispose() { }
        public Microsoft.Build.Framework.BuildEventArgs Read() { throw null; }
    }
    public delegate void ColorResetter();
    public delegate void ColorSetter(System.ConsoleColor color);
    public partial class ConfigurableForwardingLogger : Microsoft.Build.Framework.IForwardingLogger, Microsoft.Build.Framework.ILogger, Microsoft.Build.Framework.INodeLogger
    {
        public ConfigurableForwardingLogger() { }
        public Microsoft.Build.Framework.IEventRedirector BuildEventRedirector { get { throw null; } set { } }
        public int NodeId { get { throw null; } set { } }
        public string Parameters { get { throw null; } set { } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } set { } }
        protected virtual void ForwardToCentralLogger(Microsoft.Build.Framework.BuildEventArgs e) { }
        public virtual void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource, int nodeCount) { }
        public virtual void Shutdown() { }
    }
    public partial class ConsoleLogger : Microsoft.Build.Framework.ILogger, Microsoft.Build.Framework.INodeLogger
    {
        public ConsoleLogger() { }
        public ConsoleLogger(Microsoft.Build.Framework.LoggerVerbosity verbosity) { }
        public ConsoleLogger(Microsoft.Build.Framework.LoggerVerbosity verbosity, Microsoft.Build.Logging.WriteHandler write, Microsoft.Build.Logging.ColorSetter colorSet, Microsoft.Build.Logging.ColorResetter colorReset) { }
        public string Parameters { get { throw null; } set { } }
        public bool ShowSummary { get { throw null; } set { } }
        public bool SkipProjectStartedText { get { throw null; } set { } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } set { } }
        protected Microsoft.Build.Logging.WriteHandler WriteHandler { get { throw null; } set { } }
        public void ApplyParameter(string parameterName, string parameterValue) { }
        public void BuildFinishedHandler(object sender, Microsoft.Build.Framework.BuildFinishedEventArgs e) { }
        public void BuildStartedHandler(object sender, Microsoft.Build.Framework.BuildStartedEventArgs e) { }
        public void CustomEventHandler(object sender, Microsoft.Build.Framework.CustomBuildEventArgs e) { }
        public void ErrorHandler(object sender, Microsoft.Build.Framework.BuildErrorEventArgs e) { }
        public virtual void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public virtual void Initialize(Microsoft.Build.Framework.IEventSource eventSource, int nodeCount) { }
        public void MessageHandler(object sender, Microsoft.Build.Framework.BuildMessageEventArgs e) { }
        public void ProjectFinishedHandler(object sender, Microsoft.Build.Framework.ProjectFinishedEventArgs e) { }
        public void ProjectStartedHandler(object sender, Microsoft.Build.Framework.ProjectStartedEventArgs e) { }
        public virtual void Shutdown() { }
        public void TargetFinishedHandler(object sender, Microsoft.Build.Framework.TargetFinishedEventArgs e) { }
        public void TargetStartedHandler(object sender, Microsoft.Build.Framework.TargetStartedEventArgs e) { }
        public void TaskFinishedHandler(object sender, Microsoft.Build.Framework.TaskFinishedEventArgs e) { }
        public void TaskStartedHandler(object sender, Microsoft.Build.Framework.TaskStartedEventArgs e) { }
        public void WarningHandler(object sender, Microsoft.Build.Framework.BuildWarningEventArgs e) { }
    }
    public partial class DistributedFileLogger : Microsoft.Build.Framework.IForwardingLogger, Microsoft.Build.Framework.ILogger, Microsoft.Build.Framework.INodeLogger
    {
        public DistributedFileLogger() { }
        public Microsoft.Build.Framework.IEventRedirector BuildEventRedirector { get { throw null; } set { } }
        public int NodeId { get { throw null; } set { } }
        public string Parameters { get { throw null; } set { } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } set { } }
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource, int nodeCount) { }
        public void Shutdown() { }
    }
    public partial class EventArgsDispatcher : Microsoft.Build.Framework.IEventSource
    {
        public EventArgsDispatcher() { }
        public event Microsoft.Build.Framework.AnyEventHandler AnyEventRaised { add { } remove { } }
        public event Microsoft.Build.Framework.BuildFinishedEventHandler BuildFinished { add { } remove { } }
        public event Microsoft.Build.Framework.BuildStartedEventHandler BuildStarted { add { } remove { } }
        public event Microsoft.Build.Framework.CustomBuildEventHandler CustomEventRaised { add { } remove { } }
        public event Microsoft.Build.Framework.BuildErrorEventHandler ErrorRaised { add { } remove { } }
        public event Microsoft.Build.Framework.BuildMessageEventHandler MessageRaised { add { } remove { } }
        public event Microsoft.Build.Framework.ProjectFinishedEventHandler ProjectFinished { add { } remove { } }
        public event Microsoft.Build.Framework.ProjectStartedEventHandler ProjectStarted { add { } remove { } }
        public event Microsoft.Build.Framework.BuildStatusEventHandler StatusEventRaised { add { } remove { } }
        public event Microsoft.Build.Framework.TargetFinishedEventHandler TargetFinished { add { } remove { } }
        public event Microsoft.Build.Framework.TargetStartedEventHandler TargetStarted { add { } remove { } }
        public event Microsoft.Build.Framework.TaskFinishedEventHandler TaskFinished { add { } remove { } }
        public event Microsoft.Build.Framework.TaskStartedEventHandler TaskStarted { add { } remove { } }
        public event Microsoft.Build.Framework.BuildWarningEventHandler WarningRaised { add { } remove { } }
        public void Dispatch(Microsoft.Build.Framework.BuildEventArgs buildEvent) { }
    }
    public partial class FileLogger : Microsoft.Build.Logging.ConsoleLogger
    {
        public FileLogger() { }
        public override void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public override void Initialize(Microsoft.Build.Framework.IEventSource eventSource, int nodeCount) { }
        public override void Shutdown() { }
    }
    public partial class ForwardingLoggerRecord
    {
        public ForwardingLoggerRecord(Microsoft.Build.Framework.ILogger centralLogger, Microsoft.Build.Logging.LoggerDescription forwardingLoggerDescription) { }
        public Microsoft.Build.Framework.ILogger CentralLogger { get { throw null; } }
        public Microsoft.Build.Logging.LoggerDescription ForwardingLoggerDescription { get { throw null; } }
    }
    public partial class LoggerDescription
    {
        public LoggerDescription(string loggerClassName, string loggerAssemblyName, string loggerAssemblyFile, string loggerSwitchParameters, Microsoft.Build.Framework.LoggerVerbosity verbosity) { }
        public LoggerDescription(string loggerClassName, string loggerAssemblyName, string loggerAssemblyFile, string loggerSwitchParameters, Microsoft.Build.Framework.LoggerVerbosity verbosity, bool isOptional) { }
        public bool IsOptional { get { throw null; } }
        public string LoggerSwitchParameters { get { throw null; } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } }
        public Microsoft.Build.Framework.ILogger CreateLogger() { throw null; }
    }
    public sealed partial class ProfilerLogger : Microsoft.Build.Framework.ILogger
    {
        public ProfilerLogger(string fileToLog) { }
        public string FileToLog { get { throw null; } }
        public string Parameters { get { throw null; } set { } }
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get { throw null; } set { } }
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource) { }
        public void Shutdown() { }
    }
    public delegate void WriteHandler(string message);
}
namespace Microsoft.Build.ObjectModelRemoting
{
    public abstract partial class ExternalProjectsProvider
    {
        protected ExternalProjectsProvider() { }
        public virtual void Disconnected(Microsoft.Build.Evaluation.ProjectCollection collection) { }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.Project> GetLoadedProjects(string filePath);
        public static void SetExternalProjectsProvider(Microsoft.Build.Evaluation.ProjectCollection collection, Microsoft.Build.ObjectModelRemoting.ExternalProjectsProvider link) { }
    }
    public partial class LinkedObjectsFactory
    {
        internal LinkedObjectsFactory() { }
        public Microsoft.Build.Evaluation.ProjectCollection Collection { get { throw null; } }
        public Microsoft.Build.Evaluation.ResolvedImport Create(Microsoft.Build.Construction.ProjectImportElement importingElement, Microsoft.Build.Construction.ProjectRootElement importedProject, int versionEvaluated, Microsoft.Build.Framework.SdkResult sdkResult, bool isImported) { throw null; }
        public Microsoft.Build.Construction.ProjectChooseElement Create(Microsoft.Build.ObjectModelRemoting.ProjectChooseElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectExtensionsElement Create(Microsoft.Build.ObjectModelRemoting.ProjectExtensionsElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectImportElement Create(Microsoft.Build.ObjectModelRemoting.ProjectImportElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectImportGroupElement Create(Microsoft.Build.ObjectModelRemoting.ProjectImportGroupElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionElement Create(Microsoft.Build.ObjectModelRemoting.ProjectItemDefinitionElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectItemDefinitionGroupElement Create(Microsoft.Build.ObjectModelRemoting.ProjectItemDefinitionGroupElementLink link) { throw null; }
        public Microsoft.Build.Evaluation.ProjectItemDefinition Create(Microsoft.Build.ObjectModelRemoting.ProjectItemDefinitionLink link, Microsoft.Build.Evaluation.Project project = null) { throw null; }
        public Microsoft.Build.Construction.ProjectItemElement Create(Microsoft.Build.ObjectModelRemoting.ProjectItemElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectItemGroupElement Create(Microsoft.Build.ObjectModelRemoting.ProjectItemGroupElementLink link) { throw null; }
        public Microsoft.Build.Evaluation.ProjectItem Create(Microsoft.Build.ObjectModelRemoting.ProjectItemLink link, Microsoft.Build.Evaluation.Project project = null, Microsoft.Build.Construction.ProjectItemElement xml = null) { throw null; }
        public Microsoft.Build.Evaluation.Project Create(Microsoft.Build.ObjectModelRemoting.ProjectLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectMetadataElement Create(Microsoft.Build.ObjectModelRemoting.ProjectMetadataElementLink link) { throw null; }
        public Microsoft.Build.Evaluation.ProjectMetadata Create(Microsoft.Build.ObjectModelRemoting.ProjectMetadataLink link, object parent = null) { throw null; }
        public Microsoft.Build.Construction.ProjectOnErrorElement Create(Microsoft.Build.ObjectModelRemoting.ProjectOnErrorElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectOtherwiseElement Create(Microsoft.Build.ObjectModelRemoting.ProjectOtherwiseElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectOutputElement Create(Microsoft.Build.ObjectModelRemoting.ProjectOutputElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyElement Create(Microsoft.Build.ObjectModelRemoting.ProjectPropertyElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectPropertyGroupElement Create(Microsoft.Build.ObjectModelRemoting.ProjectPropertyGroupElementLink link) { throw null; }
        public Microsoft.Build.Evaluation.ProjectProperty Create(Microsoft.Build.ObjectModelRemoting.ProjectPropertyLink link, Microsoft.Build.Evaluation.Project project = null) { throw null; }
        public Microsoft.Build.Construction.ProjectRootElement Create(Microsoft.Build.ObjectModelRemoting.ProjectRootElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectSdkElement Create(Microsoft.Build.ObjectModelRemoting.ProjectSdkElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectTargetElement Create(Microsoft.Build.ObjectModelRemoting.ProjectTargetElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectTaskElement Create(Microsoft.Build.ObjectModelRemoting.ProjectTaskElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskBodyElement Create(Microsoft.Build.ObjectModelRemoting.ProjectUsingTaskBodyElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskElement Create(Microsoft.Build.ObjectModelRemoting.ProjectUsingTaskElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectUsingTaskParameterElement Create(Microsoft.Build.ObjectModelRemoting.ProjectUsingTaskParameterElementLink link) { throw null; }
        public Microsoft.Build.Construction.ProjectWhenElement Create(Microsoft.Build.ObjectModelRemoting.ProjectWhenElementLink link) { throw null; }
        public Microsoft.Build.Construction.UsingTaskParameterGroupElement Create(Microsoft.Build.ObjectModelRemoting.UsingTaskParameterGroupElementLink link) { throw null; }
        public static Microsoft.Build.ObjectModelRemoting.LinkedObjectsFactory Get(Microsoft.Build.Evaluation.ProjectCollection collection) { throw null; }
        public static object GetLink(object obj) { throw null; }
        public static System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.Evaluation.Project> GetLocalProjects(Microsoft.Build.Evaluation.ProjectCollection collection, string projectFile = null) { throw null; }
        public static bool IsLocal(object obj) { throw null; }
    }
    public abstract partial class ProjectChooseElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectChooseElementLink() { }
    }
    public abstract partial class ProjectElementContainerLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectElementContainerLink() { }
        public abstract int Count { get; }
        public abstract Microsoft.Build.Construction.ProjectElement FirstChild { get; }
        public abstract Microsoft.Build.Construction.ProjectElement LastChild { get; }
        public abstract void AddInitialChild(Microsoft.Build.Construction.ProjectElement child);
        public static void AddInitialChild(Microsoft.Build.Construction.ProjectElementContainer xml, Microsoft.Build.Construction.ProjectElement child) { }
        public static Microsoft.Build.Construction.ProjectElementContainer DeepClone(Microsoft.Build.Construction.ProjectElementContainer xml, Microsoft.Build.Construction.ProjectRootElement factory, Microsoft.Build.Construction.ProjectElementContainer parent) { throw null; }
        public abstract Microsoft.Build.Construction.ProjectElementContainer DeepClone(Microsoft.Build.Construction.ProjectRootElement factory, Microsoft.Build.Construction.ProjectElementContainer parent);
        public abstract void InsertAfterChild(Microsoft.Build.Construction.ProjectElement child, Microsoft.Build.Construction.ProjectElement reference);
        public abstract void InsertBeforeChild(Microsoft.Build.Construction.ProjectElement child, Microsoft.Build.Construction.ProjectElement reference);
        public abstract void RemoveChild(Microsoft.Build.Construction.ProjectElement child);
    }
    public abstract partial class ProjectElementLink
    {
        protected ProjectElementLink() { }
        public abstract System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.ObjectModelRemoting.XmlAttributeLink> Attributes { get; }
        public abstract Microsoft.Build.Construction.ProjectRootElement ContainingProject { get; }
        public abstract string ElementName { get; }
        public abstract bool ExpressedAsAttribute { get; set; }
        public abstract Microsoft.Build.Construction.ElementLocation Location { get; }
        public abstract Microsoft.Build.Construction.ProjectElement NextSibling { get; }
        public abstract string OuterElement { get; }
        public abstract Microsoft.Build.Construction.ProjectElementContainer Parent { get; }
        public abstract Microsoft.Build.Construction.ProjectElement PreviousSibling { get; }
        public abstract string PureText { get; }
        public abstract void CopyFrom(Microsoft.Build.Construction.ProjectElement element);
        public static Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectElement xml, Microsoft.Build.Construction.ProjectRootElement owner) { throw null; }
        public abstract Microsoft.Build.Construction.ProjectElement CreateNewInstance(Microsoft.Build.Construction.ProjectRootElement owner);
        public static Microsoft.Build.Construction.ElementLocation GetAttributeLocation(Microsoft.Build.Construction.ProjectElement xml, string attributeName) { throw null; }
        public abstract Microsoft.Build.Construction.ElementLocation GetAttributeLocation(string attributeName);
        public static System.Collections.Generic.IReadOnlyCollection<Microsoft.Build.ObjectModelRemoting.XmlAttributeLink> GetAttributes(Microsoft.Build.Construction.ProjectElement xml) { throw null; }
        public static string GetAttributeValue(Microsoft.Build.Construction.ProjectElement xml, string attributeName, bool nullIfNotExists) { throw null; }
        public abstract string GetAttributeValue(string attributeName, bool nullIfNotExists);
        public static bool GetExpressedAsAttribute(Microsoft.Build.Construction.ProjectElement xml) { throw null; }
        public static string GetPureText(Microsoft.Build.Construction.ProjectElement xml) { throw null; }
        public static void MarkDirty(Microsoft.Build.Construction.ProjectElement xml, string reason, string param) { }
        public static void SetExpressedAsAttribute(Microsoft.Build.Construction.ProjectElement xml, bool value) { }
        public static void SetOrRemoveAttribute(Microsoft.Build.Construction.ProjectElement xml, string name, string value, bool clearAttributeCache, string reason, string param) { }
        public abstract void SetOrRemoveAttribute(string name, string value, bool clearAttributeCache, string reason, string param);
    }
    public abstract partial class ProjectExtensionsElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectExtensionsElementLink() { }
        public abstract string Content { get; set; }
        public abstract string GetSubElement(string name);
        public abstract void SetSubElement(string name, string value);
    }
    public abstract partial class ProjectImportElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectImportElementLink() { }
        public abstract Microsoft.Build.Construction.ImplicitImportLocation ImplicitImportLocation { get; }
        public abstract Microsoft.Build.Construction.ProjectElement OriginalElement { get; }
    }
    public abstract partial class ProjectImportGroupElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectImportGroupElementLink() { }
    }
    public abstract partial class ProjectItemDefinitionElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectItemDefinitionElementLink() { }
    }
    public abstract partial class ProjectItemDefinitionGroupElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectItemDefinitionGroupElementLink() { }
    }
    public abstract partial class ProjectItemDefinitionLink
    {
        protected ProjectItemDefinitionLink() { }
        public abstract string ItemType { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> Metadata { get; }
        public abstract Microsoft.Build.Evaluation.Project Project { get; }
        public abstract Microsoft.Build.Evaluation.ProjectMetadata GetMetadata(string name);
        public abstract string GetMetadataValue(string name);
        public abstract Microsoft.Build.Evaluation.ProjectMetadata SetMetadataValue(string name, string unevaluatedValue);
    }
    public abstract partial class ProjectItemElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectItemElementLink() { }
        public abstract void ChangeItemType(string newType);
    }
    public abstract partial class ProjectItemGroupElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectItemGroupElementLink() { }
    }
    public abstract partial class ProjectItemLink
    {
        protected ProjectItemLink() { }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> DirectMetadata { get; }
        public abstract string EvaluatedInclude { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> MetadataCollection { get; }
        public abstract Microsoft.Build.Evaluation.Project Project { get; }
        public abstract Microsoft.Build.Construction.ProjectItemElement Xml { get; }
        public abstract void ChangeItemType(string newItemType);
        public abstract Microsoft.Build.Evaluation.ProjectMetadata GetMetadata(string name);
        public abstract string GetMetadataValue(string name);
        public abstract bool HasMetadata(string name);
        public abstract bool RemoveMetadata(string name);
        public abstract void Rename(string name);
        public abstract Microsoft.Build.Evaluation.ProjectMetadata SetMetadataValue(string name, string unevaluatedValue, bool propagateMetadataToSiblingItems);
    }
    public abstract partial class ProjectLink
    {
        protected ProjectLink() { }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectMetadata> AllEvaluatedItemDefinitionMetadata { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> AllEvaluatedItems { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectProperty> AllEvaluatedProperties { get; }
        public abstract System.Collections.Generic.IDictionary<string, System.Collections.Generic.List<string>> ConditionedProperties { get; }
        public abstract bool DisableMarkDirty { get; set; }
        public abstract System.Collections.Generic.IDictionary<string, string> GlobalProperties { get; }
        public abstract System.Collections.Generic.IList<Microsoft.Build.Evaluation.ResolvedImport> Imports { get; }
        public abstract System.Collections.Generic.IList<Microsoft.Build.Evaluation.ResolvedImport> ImportsIncludingDuplicates { get; }
        public abstract bool IsBuildEnabled { get; set; }
        public abstract bool IsDirty { get; }
        public abstract System.Collections.Generic.IDictionary<string, Microsoft.Build.Evaluation.ProjectItemDefinition> ItemDefinitions { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> Items { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> ItemsIgnoringCondition { get; }
        public abstract System.Collections.Generic.ICollection<string> ItemTypes { get; }
        public abstract int LastEvaluationId { get; }
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectProperty> Properties { get; }
        public abstract bool SkipEvaluation { get; set; }
        public abstract string SubToolsetVersion { get; }
        public abstract System.Collections.Generic.IDictionary<string, Microsoft.Build.Execution.ProjectTargetInstance> Targets { get; }
        public abstract bool ThrowInsteadOfSplittingItemElement { get; set; }
        public abstract string ToolsVersion { get; }
        public abstract Microsoft.Build.Construction.ProjectRootElement Xml { get; }
        public abstract System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItem(string itemType, string unevaluatedInclude, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata);
        public abstract System.Collections.Generic.IList<Microsoft.Build.Evaluation.ProjectItem> AddItemFast(string itemType, string unevaluatedInclude, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> metadata);
        public abstract bool Build(string[] targets, System.Collections.Generic.IEnumerable<Microsoft.Build.Framework.ILogger> loggers, System.Collections.Generic.IEnumerable<Microsoft.Build.Logging.ForwardingLoggerRecord> remoteLoggers, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract Microsoft.Build.Execution.ProjectInstance CreateProjectInstance(Microsoft.Build.Execution.ProjectInstanceSettings settings, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract string ExpandString(string unexpandedValue);
        public abstract System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs(Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract System.Collections.Generic.List<Microsoft.Build.Evaluation.GlobResult> GetAllGlobs(string itemType, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(Microsoft.Build.Evaluation.ProjectItem item, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract System.Collections.Generic.List<Microsoft.Build.Evaluation.ProvenanceResult> GetItemProvenance(string itemToMatch, string itemType, Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItems(string itemType);
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItemsByEvaluatedInclude(string evaluatedInclude);
        public abstract System.Collections.Generic.ICollection<Microsoft.Build.Evaluation.ProjectItem> GetItemsIgnoringCondition(string itemType);
        public abstract System.Collections.Generic.IEnumerable<Microsoft.Build.Construction.ProjectElement> GetLogicalProject();
        public abstract Microsoft.Build.Evaluation.ProjectProperty GetProperty(string name);
        public abstract string GetPropertyValue(string name);
        public abstract void MarkDirty();
        public abstract void ReevaluateIfNecessary(Microsoft.Build.Evaluation.Context.EvaluationContext evaluationContext);
        public abstract bool RemoveGlobalProperty(string name);
        public abstract bool RemoveItem(Microsoft.Build.Evaluation.ProjectItem item);
        public abstract void RemoveItems(System.Collections.Generic.IEnumerable<Microsoft.Build.Evaluation.ProjectItem> items);
        public abstract bool RemoveProperty(Microsoft.Build.Evaluation.ProjectProperty property);
        public abstract void SaveLogicalProject(System.IO.TextWriter writer);
        public abstract bool SetGlobalProperty(string name, string escapedValue);
        public abstract Microsoft.Build.Evaluation.ProjectProperty SetProperty(string name, string unevaluatedValue);
        public abstract void Unload();
    }
    public abstract partial class ProjectMetadataElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectMetadataElementLink() { }
        public abstract string Value { get; set; }
        public abstract void ChangeName(string newName);
    }
    public abstract partial class ProjectMetadataLink
    {
        protected ProjectMetadataLink() { }
        public abstract string EvaluatedValueEscaped { get; }
        public abstract object Parent { get; }
        public abstract Microsoft.Build.Evaluation.ProjectMetadata Predecessor { get; }
        public abstract Microsoft.Build.Construction.ProjectMetadataElement Xml { get; }
        public static string GetEvaluatedValueEscaped(Microsoft.Build.Evaluation.ProjectMetadata metadata) { throw null; }
        public static object GetParent(Microsoft.Build.Evaluation.ProjectMetadata metadata) { throw null; }
    }
    public abstract partial class ProjectOnErrorElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectOnErrorElementLink() { }
    }
    public abstract partial class ProjectOtherwiseElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectOtherwiseElementLink() { }
    }
    public abstract partial class ProjectOutputElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectOutputElementLink() { }
    }
    public abstract partial class ProjectPropertyElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectPropertyElementLink() { }
        public abstract string Value { get; set; }
        public abstract void ChangeName(string newName);
    }
    public abstract partial class ProjectPropertyGroupElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectPropertyGroupElementLink() { }
    }
    public abstract partial class ProjectPropertyLink
    {
        protected ProjectPropertyLink() { }
        public abstract string EvaluatedIncludeEscaped { get; }
        public abstract bool IsEnvironmentProperty { get; }
        public abstract bool IsGlobalProperty { get; }
        public abstract bool IsImported { get; }
        public abstract bool IsReservedProperty { get; }
        public abstract string Name { get; }
        public abstract Microsoft.Build.Evaluation.ProjectProperty Predecessor { get; }
        public abstract Microsoft.Build.Evaluation.Project Project { get; }
        public abstract string UnevaluatedValue { get; set; }
        public abstract Microsoft.Build.Construction.ProjectPropertyElement Xml { get; }
        public static string GetEvaluatedValueEscaped(Microsoft.Build.Evaluation.ProjectProperty property) { throw null; }
    }
    public abstract partial class ProjectRootElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectRootElementLink() { }
        public abstract string DirectoryPath { get; }
        public abstract System.Text.Encoding Encoding { get; }
        public abstract string FullPath { get; set; }
        public abstract bool HasUnsavedChanges { get; }
        public abstract System.DateTime LastWriteTimeWhenRead { get; }
        public abstract bool PreserveFormatting { get; }
        public abstract Microsoft.Build.Construction.ElementLocation ProjectFileLocation { get; }
        public abstract string RawXml { get; }
        public abstract System.DateTime TimeLastChanged { get; }
        public abstract int Version { get; }
        public abstract Microsoft.Build.Construction.ProjectChooseElement CreateChooseElement();
        public abstract Microsoft.Build.Construction.ProjectImportElement CreateImportElement(string project);
        public abstract Microsoft.Build.Construction.ProjectImportGroupElement CreateImportGroupElement();
        public abstract Microsoft.Build.Construction.ProjectItemDefinitionElement CreateItemDefinitionElement(string itemType);
        public abstract Microsoft.Build.Construction.ProjectItemDefinitionGroupElement CreateItemDefinitionGroupElement();
        public abstract Microsoft.Build.Construction.ProjectItemElement CreateItemElement(string itemType);
        public abstract Microsoft.Build.Construction.ProjectItemElement CreateItemElement(string itemType, string include);
        public abstract Microsoft.Build.Construction.ProjectItemGroupElement CreateItemGroupElement();
        public abstract Microsoft.Build.Construction.ProjectMetadataElement CreateMetadataElement(string name);
        public abstract Microsoft.Build.Construction.ProjectMetadataElement CreateMetadataElement(string name, string unevaluatedValue);
        public abstract Microsoft.Build.Construction.ProjectOnErrorElement CreateOnErrorElement(string executeTargets);
        public abstract Microsoft.Build.Construction.ProjectOtherwiseElement CreateOtherwiseElement();
        public abstract Microsoft.Build.Construction.ProjectOutputElement CreateOutputElement(string taskParameter, string itemType, string propertyName);
        public abstract Microsoft.Build.Construction.ProjectExtensionsElement CreateProjectExtensionsElement();
        public abstract Microsoft.Build.Construction.ProjectSdkElement CreateProjectSdkElement(string sdkName, string sdkVersion);
        public abstract Microsoft.Build.Construction.ProjectPropertyElement CreatePropertyElement(string name);
        public abstract Microsoft.Build.Construction.ProjectPropertyGroupElement CreatePropertyGroupElement();
        public abstract Microsoft.Build.Construction.ProjectTargetElement CreateTargetElement(string name);
        public abstract Microsoft.Build.Construction.ProjectTaskElement CreateTaskElement(string name);
        public abstract Microsoft.Build.Construction.ProjectUsingTaskBodyElement CreateUsingTaskBodyElement(string evaluate, string body);
        public abstract Microsoft.Build.Construction.ProjectUsingTaskElement CreateUsingTaskElement(string taskName, string assemblyFile, string assemblyName, string runtime, string architecture);
        public abstract Microsoft.Build.Construction.ProjectUsingTaskParameterElement CreateUsingTaskParameterElement(string name, string output, string required, string parameterType);
        public abstract Microsoft.Build.Construction.UsingTaskParameterGroupElement CreateUsingTaskParameterGroupElement();
        public abstract Microsoft.Build.Construction.ProjectWhenElement CreateWhenElement(string condition);
        public abstract void MarkDirty(string reason, string param);
        public abstract void ReloadFrom(string path, bool throwIfUnsavedChanges, bool preserveFormatting);
        public abstract void ReloadFrom(System.Xml.XmlReader reader, bool throwIfUnsavedChanges, bool preserveFormatting);
        public abstract void Save(System.IO.TextWriter writer);
        public abstract void Save(System.Text.Encoding saveEncoding);
    }
    public abstract partial class ProjectSdkElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectSdkElementLink() { }
    }
    public abstract partial class ProjectTargetElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectTargetElementLink() { }
        public abstract string Name { get; set; }
        public abstract string Returns { set; }
    }
    public abstract partial class ProjectTaskElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectTaskElementLink() { }
        public abstract System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, Microsoft.Build.Construction.ElementLocation>> ParameterLocations { get; }
        public abstract System.Collections.Generic.IDictionary<string, string> Parameters { get; }
        public abstract string GetParameter(string name);
        public abstract void RemoveAllParameters();
        public abstract void RemoveParameter(string name);
        public abstract void SetParameter(string name, string unevaluatedValue);
    }
    public abstract partial class ProjectUsingTaskBodyElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectUsingTaskBodyElementLink() { }
        public abstract string TaskBody { get; set; }
    }
    public abstract partial class ProjectUsingTaskElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectUsingTaskElementLink() { }
    }
    public abstract partial class ProjectUsingTaskParameterElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementLink
    {
        protected ProjectUsingTaskParameterElementLink() { }
        public abstract string Name { get; set; }
    }
    public abstract partial class ProjectWhenElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected ProjectWhenElementLink() { }
    }
    public abstract partial class UsingTaskParameterGroupElementLink : Microsoft.Build.ObjectModelRemoting.ProjectElementContainerLink
    {
        protected UsingTaskParameterGroupElementLink() { }
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public partial struct XmlAttributeLink
    {
        private object _dummy;
        public XmlAttributeLink(string localName, string value, string namespaceUri) { throw null; }
        public string LocalName { get { throw null; } }
        public string NamespaceURI { get { throw null; } }
        public string Value { get { throw null; } }
    }
}
