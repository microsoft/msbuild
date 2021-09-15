// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace Microsoft.Build.Tasks
{
    public partial class AssignCulture : Microsoft.Build.Tasks.TaskExtension
    {
        public AssignCulture() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedFilesWithCulture { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedFilesWithNoCulture { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] CultureNeutralAssignedFiles { get { throw null; } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class AssignLinkMetadata : Microsoft.Build.Tasks.TaskExtension
    {
        public AssignLinkMetadata() { }
        public Microsoft.Build.Framework.ITaskItem[] Items { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutputItems { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class AssignProjectConfiguration : Microsoft.Build.Tasks.ResolveProjectBase
    {
        public AssignProjectConfiguration() { }
        public bool AddSyntheticProjectReferencesForSolutionDependencies { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedProjects { get { throw null; } set { } }
        public string CurrentProject { get { throw null; } set { } }
        public string CurrentProjectConfiguration { get { throw null; } set { } }
        public string CurrentProjectPlatform { get { throw null; } set { } }
        public string DefaultToVcxPlatformMapping { get { throw null; } set { } }
        public bool OnlyReferenceAndBuildProjectsEnabledInSolutionConfiguration { get { throw null; } set { } }
        public string OutputType { get { throw null; } set { } }
        public bool ResolveConfigurationPlatformUsingMappings { get { throw null; } set { } }
        public bool ShouldUnsetParentConfigurationAndPlatform { get { throw null; } set { } }
        public string SolutionConfigurationContents { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] UnassignedProjects { get { throw null; } set { } }
        public string VcxToDefaultPlatformMapping { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class AssignTargetPath : Microsoft.Build.Tasks.TaskExtension
    {
        public AssignTargetPath() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedFiles { get { throw null; } }
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string RootFolder { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    [Microsoft.Build.Framework.RunInMTAAttribute]
    public partial class CallTarget : Microsoft.Build.Tasks.TaskExtension
    {
        public CallTarget() { }
        public bool RunEachTargetSeparately { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] TargetOutputs { get { throw null; } }
        public string[] Targets { get { throw null; } set { } }
        public bool UseResultsCache { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    [System.ObsoleteAttribute("The CodeTaskFactory is not supported on .NET Core.  This class is included so that users receive run-time errors and should not be used for any other purpose.", true)]
    public sealed partial class CodeTaskFactory : Microsoft.Build.Framework.ITaskFactory
    {
        public CodeTaskFactory() { }
        public string FactoryName { get { throw null; } }
        public System.Type TaskType { get { throw null; } }
        public void CleanupTask(Microsoft.Build.Framework.ITask task) { }
        public Microsoft.Build.Framework.ITask CreateTask(Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
        public Microsoft.Build.Framework.TaskPropertyInfo[] GetTaskParameters() { throw null; }
        public bool Initialize(string taskName, System.Collections.Generic.IDictionary<string, Microsoft.Build.Framework.TaskPropertyInfo> parameterGroup, string taskBody, Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
    }
    public partial class CombinePath : Microsoft.Build.Tasks.TaskExtension
    {
        public CombinePath() { }
        public string BasePath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] CombinedPaths { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Paths { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class CombineTargetFrameworkInfoProperties : Microsoft.Build.Tasks.TaskExtension
    {
        public CombineTargetFrameworkInfoProperties() { }
        public Microsoft.Build.Framework.ITaskItem[] PropertiesAndValues { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string Result { get { throw null; } set { } }
        public string RootElementName { get { throw null; } set { } }
        public bool UseAttributeForTargetFrameworkInfoPropertyNames { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class CombineXmlElements : Microsoft.Build.Tasks.TaskExtension
    {
        public CombineXmlElements() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public string Result { get { throw null; } set { } }
        public string RootElementName { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] XmlElements { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class CommandLineBuilderExtension : Microsoft.Build.Utilities.CommandLineBuilder
    {
        public CommandLineBuilderExtension() { }
        public CommandLineBuilderExtension(bool quoteHyphensOnCommandLine, bool useNewLineSeparator) { }
        protected string GetQuotedText(string unquotedText) { throw null; }
    }
    public partial class ConvertToAbsolutePath : Microsoft.Build.Tasks.TaskExtension
    {
        public ConvertToAbsolutePath() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AbsolutePaths { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Paths { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class Copy : Microsoft.Build.Tasks.TaskExtension, Microsoft.Build.Framework.ICancelableTask, Microsoft.Build.Framework.ITask
    {
        public Copy() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] CopiedFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] DestinationFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem DestinationFolder { get { throw null; } set { } }
        public bool ErrorIfLinkFails { get { throw null; } set { } }
        public bool OverwriteReadOnlyFiles { get { throw null; } set { } }
        public int Retries { get { throw null; } set { } }
        public int RetryDelayMilliseconds { get { throw null; } set { } }
        public bool SkipUnchangedFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SourceFiles { get { throw null; } set { } }
        public bool UseHardlinksIfPossible { get { throw null; } set { } }
        public bool UseSymboliclinksIfPossible { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public bool WroteAtLeastOneFile { get { throw null; } }
        public void Cancel() { }
        public override bool Execute() { throw null; }
    }
    public partial class CreateCSharpManifestResourceName : Microsoft.Build.Tasks.CreateManifestResourceName
    {
        public CreateCSharpManifestResourceName() { }
        protected override string SourceFileExtension { get { throw null; } }
        protected override string CreateManifestName(string fileName, string linkFileName, string rootNamespace, string dependentUponFileName, System.IO.Stream binaryStream) { throw null; }
        protected override bool IsSourceFile(string fileName) { throw null; }
    }
    public partial class CreateItem : Microsoft.Build.Tasks.TaskExtension
    {
        public CreateItem() { }
        public string[] AdditionalMetadata { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Exclude { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Include { get { throw null; } set { } }
        public bool PreserveExistingMetadata { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public abstract partial class CreateManifestResourceName : Microsoft.Build.Tasks.TaskExtension
    {
        protected System.Collections.Generic.Dictionary<string, Microsoft.Build.Framework.ITaskItem> itemSpecToTaskitem;
        protected CreateManifestResourceName() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ManifestResourceNames { get { throw null; } }
        public bool PrependCultureAsDirectory { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResourceFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResourceFilesWithManifestResourceNames { get { throw null; } set { } }
        public string RootNamespace { get { throw null; } set { } }
        protected abstract string SourceFileExtension { get; }
        public bool UseDependentUponConvention { get { throw null; } set { } }
        protected abstract string CreateManifestName(string fileName, string linkFileName, string rootNamespaceName, string dependentUponFileName, System.IO.Stream binaryStream);
        public override bool Execute() { throw null; }
        protected abstract bool IsSourceFile(string fileName);
        public static string MakeValidEverettIdentifier(string name) { throw null; }
    }
    public partial class CreateProperty : Microsoft.Build.Tasks.TaskExtension
    {
        public CreateProperty() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public string[] Value { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string[] ValueSetByTask { get { throw null; } }
        public override bool Execute() { throw null; }
    }
    public partial class CreateVisualBasicManifestResourceName : Microsoft.Build.Tasks.CreateManifestResourceName
    {
        public CreateVisualBasicManifestResourceName() { }
        protected override string SourceFileExtension { get { throw null; } }
        protected override string CreateManifestName(string fileName, string linkFileName, string rootNamespace, string dependentUponFileName, System.IO.Stream binaryStream) { throw null; }
        protected override bool IsSourceFile(string fileName) { throw null; }
    }
    public partial class Delete : Microsoft.Build.Tasks.TaskExtension, Microsoft.Build.Framework.ICancelableTask, Microsoft.Build.Framework.ITask
    {
        public Delete() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] DeletedFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        public bool TreatErrorsAsWarnings { get { throw null; } set { } }
        public void Cancel() { }
        public override bool Execute() { throw null; }
    }
    public sealed partial class DownloadFile : Microsoft.Build.Tasks.TaskExtension, Microsoft.Build.Framework.ICancelableTask, Microsoft.Build.Framework.ITask
    {
        public DownloadFile() { }
        public Microsoft.Build.Framework.ITaskItem DestinationFileName { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem DestinationFolder { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem DownloadedFile { get { throw null; } set { } }
        public int Retries { get { throw null; } set { } }
        public int RetryDelayMilliseconds { get { throw null; } set { } }
        public bool SkipUnchangedFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string SourceUrl { get { throw null; } set { } }
        public void Cancel() { }
        public override bool Execute() { throw null; }
    }
    public sealed partial class Error : Microsoft.Build.Tasks.TaskExtension
    {
        public Error() { }
        public string Code { get { throw null; } set { } }
        public string File { get { throw null; } set { } }
        public string HelpKeyword { get { throw null; } set { } }
        public string HelpLink { get { throw null; } set { } }
        public string Text { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class ErrorFromResources : Microsoft.Build.Tasks.TaskExtension
    {
        public ErrorFromResources() { }
        public string[] Arguments { get { throw null; } set { } }
        public string Code { get { throw null; } set { } }
        public string File { get { throw null; } set { } }
        public string HelpKeyword { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string Resource { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class Exec : Microsoft.Build.Tasks.ToolTaskExtension
    {
        public Exec() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string Command { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ConsoleOutput { get { throw null; } }
        public bool ConsoleToMSBuild { get { throw null; } set { } }
        public string CustomErrorRegularExpression { get { throw null; } set { } }
        public string CustomWarningRegularExpression { get { throw null; } set { } }
        public bool IgnoreExitCode { get { throw null; } set { } }
        public bool IgnoreStandardErrorWarningFormat { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Outputs { get { throw null; } set { } }
        protected override System.Text.Encoding StandardErrorEncoding { get { throw null; } }
        protected override Microsoft.Build.Framework.MessageImportance StandardErrorLoggingImportance { get { throw null; } }
        protected override System.Text.Encoding StandardOutputEncoding { get { throw null; } }
        protected override Microsoft.Build.Framework.MessageImportance StandardOutputLoggingImportance { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string StdErrEncoding { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string StdOutEncoding { get { throw null; } set { } }
        protected override string ToolName { get { throw null; } }
        public string WorkingDirectory { get { throw null; } set { } }
        protected internal override void AddCommandLineCommands(Microsoft.Build.Tasks.CommandLineBuilderExtension commandLine) { }
        protected override int ExecuteTool(string pathToTool, string responseFileCommands, string commandLineCommands) { throw null; }
        protected override string GenerateFullPathToTool() { throw null; }
        protected override string GetWorkingDirectory() { throw null; }
        protected override bool HandleTaskExecutionErrors() { throw null; }
        protected override void LogEventsFromTextOutput(string singleLine, Microsoft.Build.Framework.MessageImportance messageImportance) { }
        protected override void LogPathToTool(string toolName, string pathToTool) { }
        protected override void LogToolCommand(string message) { }
        protected override bool ValidateParameters() { throw null; }
    }
    [System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public partial struct ExtractedClassName
    {
        private object _dummy;
        private int _dummyPrimitive;
        public bool IsInsideConditionalBlock { get { throw null; } set { } }
        public string Name { get { throw null; } set { } }
    }
    public partial class FindAppConfigFile : Microsoft.Build.Tasks.TaskExtension
    {
        public FindAppConfigFile() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem AppConfigFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] PrimaryList { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SecondaryList { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string TargetPath { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class FindInList : Microsoft.Build.Tasks.TaskExtension
    {
        public FindInList() { }
        public bool CaseSensitive { get { throw null; } set { } }
        public bool FindLastMatch { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem ItemFound { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string ItemSpecToFind { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] List { get { throw null; } set { } }
        public bool MatchFileNameOnly { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class FindInvalidProjectReferences : Microsoft.Build.Tasks.TaskExtension
    {
        public FindInvalidProjectReferences() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] InvalidReferences { get { throw null; } }
        public Microsoft.Build.Framework.ITaskItem[] ProjectReferences { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string TargetPlatformIdentifier { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string TargetPlatformVersion { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class FindUnderPath : Microsoft.Build.Tasks.TaskExtension
    {
        public FindUnderPath() { }
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] InPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutOfPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem Path { get { throw null; } set { } }
        public bool UpdateToAbsolutePaths { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class FormatUrl : Microsoft.Build.Tasks.TaskExtension
    {
        public FormatUrl() { }
        public string InputUrl { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string OutputUrl { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class FormatVersion : Microsoft.Build.Tasks.TaskExtension
    {
        public FormatVersion() { }
        public string FormatType { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string OutputVersion { get { throw null; } set { } }
        public int Revision { get { throw null; } set { } }
        public string Version { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class GenerateApplicationManifest : Microsoft.Build.Tasks.GenerateManifestBase
    {
        public GenerateApplicationManifest() { }
        public string ClrVersion { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem ConfigFile { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Dependencies { get { throw null; } set { } }
        public string ErrorReportUrl { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] FileAssociations { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        public bool HostInBrowser { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem IconFile { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] IsolatedComReferences { get { throw null; } set { } }
        public string ManifestType { get { throw null; } set { } }
        public string OSVersion { get { throw null; } set { } }
        public string Product { get { throw null; } set { } }
        public string Publisher { get { throw null; } set { } }
        public bool RequiresMinimumFramework35SP1 { get { throw null; } set { } }
        public string SuiteName { get { throw null; } set { } }
        public string SupportUrl { get { throw null; } set { } }
        public string TargetFrameworkProfile { get { throw null; } set { } }
        public string TargetFrameworkSubset { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem TrustInfoFile { get { throw null; } set { } }
        public bool UseApplicationTrust { get { throw null; } set { } }
        protected override System.Type GetObjectType() { throw null; }
        protected override bool OnManifestLoaded(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest) { throw null; }
        protected override bool OnManifestResolved(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest) { throw null; }
        protected internal override bool ValidateInputs() { throw null; }
    }
    public partial class GenerateBindingRedirects : Microsoft.Build.Tasks.TaskExtension
    {
        public GenerateBindingRedirects() { }
        public Microsoft.Build.Framework.ITaskItem AppConfigFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputAppConfigFile { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] SuggestedRedirects { get { throw null; } set { } }
        public string TargetName { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class GenerateDeploymentManifest : Microsoft.Build.Tasks.GenerateManifestBase
    {
        public GenerateDeploymentManifest() { }
        public bool CreateDesktopShortcut { get { throw null; } set { } }
        public string DeploymentUrl { get { throw null; } set { } }
        public bool DisallowUrlActivation { get { throw null; } set { } }
        public string ErrorReportUrl { get { throw null; } set { } }
        public bool Install { get { throw null; } set { } }
        public bool MapFileExtensions { get { throw null; } set { } }
        public string MinimumRequiredVersion { get { throw null; } set { } }
        public string Product { get { throw null; } set { } }
        public string Publisher { get { throw null; } set { } }
        public string SuiteName { get { throw null; } set { } }
        public string SupportUrl { get { throw null; } set { } }
        public bool TrustUrlParameters { get { throw null; } set { } }
        public bool UpdateEnabled { get { throw null; } set { } }
        public int UpdateInterval { get { throw null; } set { } }
        public string UpdateMode { get { throw null; } set { } }
        public string UpdateUnit { get { throw null; } set { } }
        protected override System.Type GetObjectType() { throw null; }
        protected override bool OnManifestLoaded(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest) { throw null; }
        protected override bool OnManifestResolved(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest) { throw null; }
        protected internal override bool ValidateInputs() { throw null; }
    }
    public sealed partial class GenerateLauncher : Microsoft.Build.Tasks.TaskExtension
    {
        public GenerateLauncher() { }
        public string AssemblyName { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem EntryPoint { get { throw null; } set { } }
        public string LauncherPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputEntryPoint { get { throw null; } set { } }
        public string OutputPath { get { throw null; } set { } }
        public string VisualStudioVersion { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public abstract partial class GenerateManifestBase : Microsoft.Build.Utilities.Task
    {
        protected GenerateManifestBase() { }
        public string AssemblyName { get { throw null; } set { } }
        public string AssemblyVersion { get { throw null; } set { } }
        public string Description { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem EntryPoint { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem InputManifest { get { throw null; } set { } }
        public bool LauncherBasedDeployment { get { throw null; } set { } }
        public int MaxTargetPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputManifest { get { throw null; } set { } }
        public string Platform { get { throw null; } set { } }
        public string TargetCulture { get { throw null; } set { } }
        public string TargetFrameworkMoniker { get { throw null; } set { } }
        public string TargetFrameworkVersion { get { throw null; } set { } }
        protected internal Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference AddAssemblyFromItem(Microsoft.Build.Framework.ITaskItem item) { throw null; }
        protected internal Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference AddAssemblyNameFromItem(Microsoft.Build.Framework.ITaskItem item, Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReferenceType referenceType) { throw null; }
        protected internal Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference AddEntryPointFromItem(Microsoft.Build.Framework.ITaskItem item, Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReferenceType referenceType) { throw null; }
        protected internal Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference AddFileFromItem(Microsoft.Build.Framework.ITaskItem item) { throw null; }
        public override bool Execute() { throw null; }
        protected internal Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference FindFileFromItem(Microsoft.Build.Framework.ITaskItem item) { throw null; }
        protected abstract System.Type GetObjectType();
        protected abstract bool OnManifestLoaded(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest);
        protected abstract bool OnManifestResolved(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest);
        protected internal virtual bool ValidateInputs() { throw null; }
        protected internal virtual bool ValidateOutput() { throw null; }
    }
    [Microsoft.Build.Framework.RequiredRuntimeAttribute("v2.0")]
    public sealed partial class GenerateResource : Microsoft.Build.Tasks.TaskExtension
    {
        public GenerateResource() { }
        public Microsoft.Build.Framework.ITaskItem[] AdditionalInputs { get { throw null; } set { } }
        public string[] EnvironmentVariables { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] ExcludedInputPaths { get { throw null; } set { } }
        public bool ExecuteAsTool { get { throw null; } set { } }
        public bool ExtractResWFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] FilesWritten { get { throw null; } }
        public bool MinimalRebuildFromTracking { get { throw null; } set { } }
        public bool NeverLockTypeAssemblies { get { throw null; } set { } }
        public string OutputDirectory { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutputResources { get { throw null; } set { } }
        public bool PublicClass { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] References { get { throw null; } set { } }
        public string SdkToolsPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Sources { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem StateFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string StronglyTypedClassName { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string StronglyTypedFileName { get { throw null; } set { } }
        public string StronglyTypedLanguage { get { throw null; } set { } }
        public string StronglyTypedManifestPrefix { get { throw null; } set { } }
        public string StronglyTypedNamespace { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] TLogReadFiles { get { throw null; } }
        public Microsoft.Build.Framework.ITaskItem[] TLogWriteFiles { get { throw null; } }
        public string ToolArchitecture { get { throw null; } set { } }
        public string TrackerFrameworkPath { get { throw null; } set { } }
        public string TrackerLogDirectory { get { throw null; } set { } }
        public string TrackerSdkPath { get { throw null; } set { } }
        public bool TrackFileAccess { get { throw null; } set { } }
        public bool UsePreserializedResources { get { throw null; } set { } }
        public bool UseSourcePath { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class GetAssemblyIdentity : Microsoft.Build.Tasks.TaskExtension
    {
        public GetAssemblyIdentity() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Assemblies { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssemblyFiles { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class GetCompatiblePlatform : Microsoft.Build.Tasks.TaskExtension
    {
        public GetCompatiblePlatform() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AnnotatedProjects { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] AssignedProjectsWithPlatform { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string CurrentProjectPlatform { get { throw null; } set { } }
        public string PlatformLookupTable { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class GetFileHash : Microsoft.Build.Tasks.TaskExtension
    {
        public GetFileHash() { }
        public string Algorithm { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string Hash { get { throw null; } set { } }
        public string HashEncoding { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Items { get { throw null; } set { } }
        public string MetadataName { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class GetFrameworkPath : Microsoft.Build.Tasks.TaskExtension
    {
        public GetFrameworkPath() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion11Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion20Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion30Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion35Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion40Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion451Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion452Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion45Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion461Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion462Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion46Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion471Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion472Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion47Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string FrameworkVersion48Path { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string Path { get { throw null; } }
        public override bool Execute() { throw null; }
    }
    public partial class GetReferenceAssemblyPaths : Microsoft.Build.Tasks.TaskExtension
    {
        public GetReferenceAssemblyPaths() { }
        public bool BypassFrameworkInstallChecks { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string[] FullFrameworkReferenceAssemblyPaths { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string[] ReferenceAssemblyPaths { get { throw null; } }
        public string RootPath { get { throw null; } set { } }
        public bool SuppressNotFoundError { get { throw null; } set { } }
        public string TargetFrameworkFallbackSearchPaths { get { throw null; } set { } }
        public string TargetFrameworkMoniker { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string TargetFrameworkMonikerDisplayName { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class Hash : Microsoft.Build.Tasks.TaskExtension
    {
        public Hash() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public string HashResult { get { throw null; } set { } }
        public bool IgnoreCase { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ItemsToHash { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class MakeDir : Microsoft.Build.Tasks.TaskExtension
    {
        public MakeDir() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Directories { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] DirectoriesCreated { get { throw null; } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class Message : Microsoft.Build.Tasks.TaskExtension
    {
        public Message() { }
        public string Code { get { throw null; } set { } }
        public string File { get { throw null; } set { } }
        public string HelpKeyword { get { throw null; } set { } }
        public string Importance { get { throw null; } set { } }
        public bool IsCritical { get { throw null; } set { } }
        public string Text { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class Move : Microsoft.Build.Tasks.TaskExtension, Microsoft.Build.Framework.ICancelableTask, Microsoft.Build.Framework.ITask
    {
        public Move() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] DestinationFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem DestinationFolder { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] MovedFiles { get { throw null; } }
        public bool OverwriteReadOnlyFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SourceFiles { get { throw null; } set { } }
        public void Cancel() { }
        public override bool Execute() { throw null; }
    }
    [Microsoft.Build.Framework.RunInMTAAttribute]
    public partial class MSBuild : Microsoft.Build.Tasks.TaskExtension
    {
        public MSBuild() { }
        public bool BuildInParallel { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Projects { get { throw null; } set { } }
        public string[] Properties { get { throw null; } set { } }
        public bool RebaseOutputs { get { throw null; } set { } }
        public string RemoveProperties { get { throw null; } set { } }
        public bool RunEachTargetSeparately { get { throw null; } set { } }
        public string SkipNonexistentProjects { get { throw null; } set { } }
        public bool StopOnFirstFailure { get { throw null; } set { } }
        public string[] TargetAndPropertyListSeparators { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] TargetOutputs { get { throw null; } }
        public string[] Targets { get { throw null; } set { } }
        public string ToolsVersion { get { throw null; } set { } }
        public bool UnloadProjectsOnCompletion { get { throw null; } set { } }
        public bool UseResultsCache { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class ReadLinesFromFile : Microsoft.Build.Tasks.TaskExtension
    {
        public ReadLinesFromFile() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem File { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Lines { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class RemoveDir : Microsoft.Build.Tasks.TaskExtension
    {
        public RemoveDir() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Directories { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] RemovedDirectories { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class RemoveDuplicates : Microsoft.Build.Tasks.TaskExtension
    {
        public RemoveDuplicates() { }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Filtered { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public bool HadAnyDuplicates { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class ResolveAssemblyReference : Microsoft.Build.Tasks.TaskExtension
    {
        public ResolveAssemblyReference() { }
        public string[] AllowedAssemblyExtensions { get { throw null; } set { } }
        public string[] AllowedRelatedFileExtensions { get { throw null; } set { } }
        public string AppConfigFile { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Assemblies { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] AssemblyFiles { get { throw null; } set { } }
        public string AssemblyInformationCacheOutputPath { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] AssemblyInformationCachePaths { get { throw null; } set { } }
        public bool AutoUnify { get { throw null; } set { } }
        public string[] CandidateAssemblyFiles { get { throw null; } set { } }
        public bool CopyLocalDependenciesWhenParentReferenceInGac { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] CopyLocalFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string DependsOnNETStandard { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string DependsOnSystemRuntime { get { throw null; } }
        public bool DoNotCopyLocalIfInGac { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] FilesWritten { get { throw null; } set { } }
        public bool FindDependencies { get { throw null; } set { } }
        public bool FindDependenciesOfExternallyResolvedReferences { get { throw null; } set { } }
        public bool FindRelatedFiles { get { throw null; } set { } }
        public bool FindSatellites { get { throw null; } set { } }
        public bool FindSerializationAssemblies { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] FullFrameworkAssemblyTables { get { throw null; } set { } }
        public string[] FullFrameworkFolders { get { throw null; } set { } }
        public string[] FullTargetFrameworkSubsetNames { get { throw null; } set { } }
        public bool IgnoreDefaultInstalledAssemblySubsetTables { get { throw null; } set { } }
        public bool IgnoreDefaultInstalledAssemblyTables { get { throw null; } set { } }
        public bool IgnoreTargetFrameworkAttributeVersionMismatch { get { throw null; } set { } }
        public bool IgnoreVersionForFrameworkReferences { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] InstalledAssemblySubsetTables { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] InstalledAssemblyTables { get { throw null; } set { } }
        public string[] LatestTargetFrameworkDirectories { get { throw null; } set { } }
        public bool OutputUnresolvedAssemblyConflicts { get { throw null; } set { } }
        public string ProfileName { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] RelatedFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResolvedDependencyFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResolvedFiles { get { throw null; } }
        public Microsoft.Build.Framework.ITaskItem[] ResolvedSDKReferences { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SatelliteFiles { get { throw null; } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ScatterFiles { get { throw null; } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string[] SearchPaths { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SerializationAssemblyFiles { get { throw null; } }
        public bool Silent { get { throw null; } set { } }
        public string StateFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SuggestedRedirects { get { throw null; } }
        public bool SupportsBindingRedirectGeneration { get { throw null; } set { } }
        public string TargetedRuntimeVersion { get { throw null; } set { } }
        public string[] TargetFrameworkDirectories { get { throw null; } set { } }
        public string TargetFrameworkMoniker { get { throw null; } set { } }
        public string TargetFrameworkMonikerDisplayName { get { throw null; } set { } }
        public string[] TargetFrameworkSubsets { get { throw null; } set { } }
        public string TargetFrameworkVersion { get { throw null; } set { } }
        public string TargetProcessorArchitecture { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] UnresolvedAssemblyConflicts { get { throw null; } }
        public bool UnresolveFrameworkAssembliesFromHigherFrameworks { get { throw null; } set { } }
        public string WarnOrErrorOnTargetArchitectureMismatch { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class ResolveCodeAnalysisRuleSet : Microsoft.Build.Tasks.TaskExtension
    {
        public ResolveCodeAnalysisRuleSet() { }
        public string CodeAnalysisRuleSet { get { throw null; } set { } }
        public string[] CodeAnalysisRuleSetDirectories { get { throw null; } set { } }
        public string MSBuildProjectDirectory { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string ResolvedCodeAnalysisRuleSet { get { throw null; } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class ResolveComReference : Microsoft.Build.Tasks.TaskExtension
    {
        public ResolveComReference() { }
        public bool DelaySign { get { throw null; } set { } }
        public string[] EnvironmentVariables { get { throw null; } set { } }
        public bool ExecuteAsTool { get { throw null; } set { } }
        public bool IncludeVersionInInteropName { get { throw null; } set { } }
        public string KeyContainer { get { throw null; } set { } }
        public string KeyFile { get { throw null; } set { } }
        public bool NoClassMembers { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] ResolvedAssemblyReferences { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResolvedFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResolvedModules { get { throw null; } set { } }
        public string SdkToolsPath { get { throw null; } set { } }
        public bool Silent { get { throw null; } set { } }
        public string StateFile { get { throw null; } set { } }
        public string TargetFrameworkVersion { get { throw null; } set { } }
        public string TargetProcessorArchitecture { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] TypeLibFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] TypeLibNames { get { throw null; } set { } }
        public string WrapperOutputDirectory { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class ResolveKeySource : Microsoft.Build.Tasks.TaskExtension
    {
        public ResolveKeySource() { }
        public int AutoClosePasswordPromptShow { get { throw null; } set { } }
        public int AutoClosePasswordPromptTimeout { get { throw null; } set { } }
        public string CertificateFile { get { throw null; } set { } }
        public string CertificateThumbprint { get { throw null; } set { } }
        public string KeyFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string ResolvedKeyContainer { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string ResolvedKeyFile { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public string ResolvedThumbprint { get { throw null; } set { } }
        public bool ShowImportDialogDespitePreviousFailures { get { throw null; } set { } }
        public bool SuppressAutoClosePasswordPrompt { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class ResolveManifestFiles : Microsoft.Build.Tasks.TaskExtension
    {
        public ResolveManifestFiles() { }
        public string AssemblyName { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem DeploymentManifestEntryPoint { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem EntryPoint { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] ExtraFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        public bool IsSelfContainedPublish { get { throw null; } set { } }
        public bool IsSingleFilePublish { get { throw null; } set { } }
        public bool LauncherBasedDeployment { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] ManagedAssemblies { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] NativeAssemblies { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutputAssemblies { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputDeploymentManifestEntryPoint { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputEntryPoint { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutputFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] PublishFiles { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] RuntimePackAssets { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] SatelliteAssemblies { get { throw null; } set { } }
        public bool SigningManifests { get { throw null; } set { } }
        public string TargetCulture { get { throw null; } set { } }
        public string TargetFrameworkIdentifier { get { throw null; } set { } }
        public string TargetFrameworkVersion { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class ResolveNonMSBuildProjectOutput : Microsoft.Build.Tasks.ResolveProjectBase
    {
        public ResolveNonMSBuildProjectOutput() { }
        public string PreresolvedProjectOutputs { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ResolvedOutputPaths { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] UnresolvedProjectReferences { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public abstract partial class ResolveProjectBase : Microsoft.Build.Tasks.TaskExtension
    {
        protected ResolveProjectBase() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] ProjectReferences { get { throw null; } set { } }
        protected void AddSyntheticProjectReferences(string currentProjectAbsolutePath) { }
        protected System.Xml.XmlElement GetProjectElement(Microsoft.Build.Framework.ITaskItem projectRef) { throw null; }
        protected string GetProjectItem(Microsoft.Build.Framework.ITaskItem projectRef) { throw null; }
    }
    public sealed partial class RoslynCodeTaskFactory : Microsoft.Build.Framework.ITaskFactory
    {
        public RoslynCodeTaskFactory() { }
        public string FactoryName { get { throw null; } }
        public System.Type TaskType { get { throw null; } }
        public void CleanupTask(Microsoft.Build.Framework.ITask task) { }
        public Microsoft.Build.Framework.ITask CreateTask(Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
        public Microsoft.Build.Framework.TaskPropertyInfo[] GetTaskParameters() { throw null; }
        public bool Initialize(string taskName, System.Collections.Generic.IDictionary<string, Microsoft.Build.Framework.TaskPropertyInfo> parameterGroup, string taskBody, Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
    }
    public partial class SGen : Microsoft.Build.Tasks.ToolTaskExtension
    {
        public SGen() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string BuildAssemblyName { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string BuildAssemblyPath { get { throw null; } set { } }
        public bool DelaySign { get { throw null; } set { } }
        public string KeyContainer { get { throw null; } set { } }
        public string KeyFile { get { throw null; } set { } }
        public string Platform { get { throw null; } set { } }
        public string[] References { get { throw null; } set { } }
        public string SdkToolsPath { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SerializationAssembly { get { throw null; } set { } }
        public string SerializationAssemblyName { get { throw null; } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public bool ShouldGenerateSerializer { get { throw null; } set { } }
        protected override string ToolName { get { throw null; } }
        public string[] Types { get { throw null; } set { } }
        public bool UseKeep { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public bool UseProxyTypes { get { throw null; } set { } }
        public override bool Execute() { throw null; }
        protected override string GenerateFullPathToTool() { throw null; }
    }
    public sealed partial class SignFile : Microsoft.Build.Utilities.Task
    {
        public SignFile() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string CertificateThumbprint { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem SigningTarget { get { throw null; } set { } }
        public string TargetFrameworkIdentifier { get { throw null; } set { } }
        public string TargetFrameworkVersion { get { throw null; } set { } }
        public string TimestampUrl { get { throw null; } set { } }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public override bool Execute() { throw null; }
    }
    public abstract partial class TaskExtension : Microsoft.Build.Utilities.Task
    {
        internal TaskExtension() { }
        public new Microsoft.Build.Utilities.TaskLoggingHelper Log { get { throw null; } }
    }
    public partial class TaskLoggingHelperExtension : Microsoft.Build.Utilities.TaskLoggingHelper
    {
        public TaskLoggingHelperExtension(Microsoft.Build.Framework.ITask taskInstance, System.Resources.ResourceManager primaryResources, System.Resources.ResourceManager sharedResources, string helpKeywordPrefix) : base (default(Microsoft.Build.Framework.ITask)) { }
        public System.Resources.ResourceManager TaskSharedResources { get { throw null; } set { } }
        public override string FormatResourceString(string resourceName, params object[] args) { throw null; }
    }
    public sealed partial class Telemetry : Microsoft.Build.Tasks.TaskExtension
    {
        public Telemetry() { }
        public string EventData { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string EventName { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public abstract partial class ToolTaskExtension : Microsoft.Build.Utilities.ToolTask
    {
        internal ToolTaskExtension() { }
        protected internal System.Collections.Hashtable Bag { get { throw null; } }
        protected override bool HasLoggedErrors { get { throw null; } }
        public new Microsoft.Build.Utilities.TaskLoggingHelper Log { get { throw null; } }
        protected virtual bool UseNewLineSeparatorInResponseFile { get { throw null; } }
        protected internal virtual void AddCommandLineCommands(Microsoft.Build.Tasks.CommandLineBuilderExtension commandLine) { }
        protected internal virtual void AddResponseFileCommands(Microsoft.Build.Tasks.CommandLineBuilderExtension commandLine) { }
        protected override string GenerateCommandLineCommands() { throw null; }
        protected override string GenerateResponseFileCommands() { throw null; }
        protected internal bool GetBoolParameterWithDefault(string parameterName, bool defaultValue) { throw null; }
        protected internal int GetIntParameterWithDefault(string parameterName, int defaultValue) { throw null; }
    }
    public partial class Touch : Microsoft.Build.Tasks.TaskExtension
    {
        public Touch() { }
        public bool AlwaysCreate { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Files { get { throw null; } set { } }
        public bool ForceTouch { get { throw null; } set { } }
        public string Time { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] TouchedFiles { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class Unzip : Microsoft.Build.Tasks.TaskExtension, Microsoft.Build.Framework.ICancelableTask, Microsoft.Build.Framework.ITask
    {
        public Unzip() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem DestinationFolder { get { throw null; } set { } }
        public string Exclude { get { throw null; } set { } }
        public string Include { get { throw null; } set { } }
        public bool OverwriteReadOnlyFiles { get { throw null; } set { } }
        public bool SkipUnchangedFiles { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] SourceFiles { get { throw null; } set { } }
        public void Cancel() { }
        public override bool Execute() { throw null; }
    }
    public sealed partial class VerifyFileHash : Microsoft.Build.Tasks.TaskExtension
    {
        public VerifyFileHash() { }
        public string Algorithm { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string File { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string Hash { get { throw null; } set { } }
        public string HashEncoding { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class Warning : Microsoft.Build.Tasks.TaskExtension
    {
        public Warning() { }
        public string Code { get { throw null; } set { } }
        public string File { get { throw null; } set { } }
        public string HelpKeyword { get { throw null; } set { } }
        public string HelpLink { get { throw null; } set { } }
        public string Text { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class WriteCodeFragment : Microsoft.Build.Tasks.TaskExtension
    {
        public WriteCodeFragment() { }
        public Microsoft.Build.Framework.ITaskItem[] AssemblyAttributes { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public string Language { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem OutputDirectory { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem OutputFile { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class WriteLinesToFile : Microsoft.Build.Tasks.TaskExtension
    {
        public WriteLinesToFile() { }
        public string Encoding { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem File { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] Lines { get { throw null; } set { } }
        public bool Overwrite { get { throw null; } set { } }
        public bool WriteOnlyWhenDifferent { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    [System.ObsoleteAttribute("The XamlTaskFactory is not supported on .NET Core.  This class is included so that users receive run-time errors and should not be used for any other purpose.", true)]
    public sealed partial class XamlTaskFactory : Microsoft.Build.Framework.ITaskFactory
    {
        public XamlTaskFactory() { }
        public string FactoryName { get { throw null; } }
        public System.Type TaskType { get { throw null; } }
        public void CleanupTask(Microsoft.Build.Framework.ITask task) { }
        public Microsoft.Build.Framework.ITask CreateTask(Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
        public Microsoft.Build.Framework.TaskPropertyInfo[] GetTaskParameters() { throw null; }
        public bool Initialize(string taskName, System.Collections.Generic.IDictionary<string, Microsoft.Build.Framework.TaskPropertyInfo> parameterGroup, string taskBody, Microsoft.Build.Framework.IBuildEngine taskFactoryLoggingHost) { throw null; }
    }
    public partial class XmlPeek : Microsoft.Build.Tasks.TaskExtension
    {
        public XmlPeek() { }
        public string Namespaces { get { throw null; } set { } }
        public bool ProhibitDtd { get { throw null; } set { } }
        public string Query { get { throw null; } set { } }
        [Microsoft.Build.Framework.OutputAttribute]
        public Microsoft.Build.Framework.ITaskItem[] Result { get { throw null; } }
        public string XmlContent { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem XmlInputPath { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class XmlPoke : Microsoft.Build.Tasks.TaskExtension
    {
        public XmlPoke() { }
        public string Namespaces { get { throw null; } set { } }
        public string Query { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem Value { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem XmlInputPath { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public partial class XslTransformation : Microsoft.Build.Tasks.TaskExtension
    {
        public XslTransformation() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem[] OutputPaths { get { throw null; } set { } }
        public string Parameters { get { throw null; } set { } }
        public bool UseTrustedSettings { get { throw null; } set { } }
        public string XmlContent { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem[] XmlInputPaths { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem XslCompiledDllPath { get { throw null; } set { } }
        public string XslContent { get { throw null; } set { } }
        public Microsoft.Build.Framework.ITaskItem XslInputPath { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
    public sealed partial class ZipDirectory : Microsoft.Build.Tasks.TaskExtension
    {
        public ZipDirectory() { }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem DestinationFile { get { throw null; } set { } }
        public bool Overwrite { get { throw null; } set { } }
        [Microsoft.Build.Framework.RequiredAttribute]
        public Microsoft.Build.Framework.ITaskItem SourceDirectory { get { throw null; } set { } }
        public override bool Execute() { throw null; }
    }
}
namespace Microsoft.Build.Tasks.Deployment.Bootstrapper
{
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("1D9FE38A-0226-4b95-9C6B-6DFFA2236270")]
    public partial class BootstrapperBuilder : Microsoft.Build.Tasks.Deployment.Bootstrapper.IBootstrapperBuilder
    {
        public BootstrapperBuilder() { }
        public BootstrapperBuilder(string visualStudioVersion) { }
        public string Path { get { throw null; } set { } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductCollection Products { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildResults Build(Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildSettings settings) { throw null; }
        public string[] GetOutputFolders(string[] productCodes, string culture, string fallbackCulture, Microsoft.Build.Tasks.Deployment.Bootstrapper.ComponentsLocation componentsLocation) { throw null; }
        public static string XmlToConfigurationFile(System.Xml.XmlNode input) { throw null; }
    }
    public partial class BuildMessage : Microsoft.Build.Tasks.Deployment.Bootstrapper.IBuildMessage
    {
        internal BuildMessage() { }
        public int HelpId { get { throw null; } }
        public string HelpKeyword { get { throw null; } }
        public string Message { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildMessageSeverity Severity { get { throw null; } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("936D32F9-1A68-4d5e-98EA-044AC9A1AADA")]
    public enum BuildMessageSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("FAD7BA7C-CA00-41e0-A5EF-2DA9A74E58E6")]
    public partial class BuildResults : Microsoft.Build.Tasks.Deployment.Bootstrapper.IBuildResults
    {
        internal BuildResults() { }
        public string[] ComponentFiles { get { throw null; } }
        public string KeyFile { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildMessage[] Messages { get { throw null; } }
        public bool Succeeded { get { throw null; } }
    }
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("5D13802C-C830-4b41-8E7A-F69D9DD6A095")]
    public partial class BuildSettings : Microsoft.Build.Tasks.Deployment.Bootstrapper.IBuildSettings
    {
        public BuildSettings() { }
        public string ApplicationFile { get { throw null; } set { } }
        public string ApplicationName { get { throw null; } set { } }
        public bool ApplicationRequiresElevation { get { throw null; } set { } }
        public string ApplicationUrl { get { throw null; } set { } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.ComponentsLocation ComponentsLocation { get { throw null; } set { } }
        public string ComponentsUrl { get { throw null; } set { } }
        public bool CopyComponents { get { throw null; } set { } }
        public int FallbackLCID { get { throw null; } set { } }
        public int LCID { get { throw null; } set { } }
        public string OutputPath { get { throw null; } set { } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilderCollection ProductBuilders { get { throw null; } }
        public string SupportUrl { get { throw null; } set { } }
        public bool Validate { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("12F49949-7B60-49CD-B6A0-2B5E4A638AAF")]
    public enum ComponentsLocation
    {
        HomeSite = 0,
        Relative = 1,
        Absolute = 2,
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("1D202366-5EEA-4379-9255-6F8CDB8587C9")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IBootstrapperBuilder
    {
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        string Path { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(4)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductCollection Products { get; }
        [System.Runtime.InteropServices.DispIdAttribute(5)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildResults Build(Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildSettings settings);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("E3C981EA-99E6-4f48-8955-1AAFDFB5ACE4")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IBuildMessage
    {
        [System.Runtime.InteropServices.DispIdAttribute(4)]
        int HelpId { get; }
        [System.Runtime.InteropServices.DispIdAttribute(3)]
        string HelpKeyword { get; }
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        string Message { get; }
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildMessageSeverity Severity { get; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("586B842C-D9C7-43b8-84E4-9CFC3AF9F13B")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IBuildResults
    {
        [System.Runtime.InteropServices.DispIdAttribute(3)]
        string[] ComponentFiles { get; }
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        string KeyFile { get; }
        [System.Runtime.InteropServices.DispIdAttribute(4)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildMessage[] Messages { get; }
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        bool Succeeded { get; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("87EEBC69-0948-4ce6-A2DE-819162B87CC6")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IBuildSettings
    {
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        string ApplicationFile { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        string ApplicationName { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(13)]
        bool ApplicationRequiresElevation { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(3)]
        string ApplicationUrl { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(11)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.ComponentsLocation ComponentsLocation { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(4)]
        string ComponentsUrl { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(5)]
        bool CopyComponents { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(7)]
        int FallbackLCID { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(6)]
        int LCID { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(8)]
        string OutputPath { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(9)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilderCollection ProductBuilders { get; }
        [System.Runtime.InteropServices.DispIdAttribute(12)]
        string SupportUrl { get; set; }
        [System.Runtime.InteropServices.DispIdAttribute(10)]
        bool Validate { get; set; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("9E81BE3D-530F-4a10-8349-5D5947BA59AD")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IProduct
    {
        [System.Runtime.InteropServices.DispIdAttribute(4)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductCollection Includes { get; }
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        string Name { get; }
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilder ProductBuilder { get; }
        [System.Runtime.InteropServices.DispIdAttribute(3)]
        string ProductCode { get; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("0777432F-A60D-48b3-83DB-90326FE8C96E")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IProductBuilder
    {
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Product { get; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("0D593FC0-E3F1-4dad-A674-7EA4D327F79B")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IProductBuilderCollection
    {
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        void Add(Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilder builder);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("63F63663-8503-4875-814C-09168E595367")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual)]
    public partial interface IProductCollection
    {
        [System.Runtime.InteropServices.DispIdAttribute(1)]
        int Count { get; }
        [System.Runtime.InteropServices.DispIdAttribute(2)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Item(int index);
        [System.Runtime.InteropServices.DispIdAttribute(3)]
        Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Product(string productCode);
    }
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("532BF563-A85D-4088-8048-41F51AC5239F")]
    public partial class Product : Microsoft.Build.Tasks.Deployment.Bootstrapper.IProduct
    {
        public Product() { }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductCollection Includes { get { throw null; } }
        public string Name { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilder ProductBuilder { get { throw null; } }
        public string ProductCode { get { throw null; } }
    }
    public partial class ProductBuilder : Microsoft.Build.Tasks.Deployment.Bootstrapper.IProductBuilder
    {
        internal ProductBuilder() { }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Product { get { throw null; } }
    }
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("D25C0741-99CA-49f7-9460-95E5F25EEF43")]
    public partial class ProductBuilderCollection : Microsoft.Build.Tasks.Deployment.Bootstrapper.IProductBuilderCollection, System.Collections.IEnumerable
    {
        internal ProductBuilderCollection() { }
        public void Add(Microsoft.Build.Tasks.Deployment.Bootstrapper.ProductBuilder builder) { }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
    }
    [System.Runtime.InteropServices.ClassInterfaceAttribute(System.Runtime.InteropServices.ClassInterfaceType.None)]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("EFFA164B-3E87-4195-88DB-8AC004DDFE2A")]
    public partial class ProductCollection : Microsoft.Build.Tasks.Deployment.Bootstrapper.IProductCollection, System.Collections.IEnumerable
    {
        internal ProductCollection() { }
        public int Count { get { throw null; } }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Item(int index) { throw null; }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.Product Product(string productCode) { throw null; }
    }
}
namespace Microsoft.Build.Tasks.Deployment.ManifestUtilities
{
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class ApplicationIdentity
    {
        public ApplicationIdentity(string url, Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity deployManifestIdentity, Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity applicationManifestIdentity) { }
        public ApplicationIdentity(string url, string deployManifestPath, string applicationManifestPath) { }
        public override string ToString() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    [System.Xml.Serialization.XmlRootAttribute("ApplicationManifest")]
    public sealed partial class ApplicationManifest : Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyManifest
    {
        public ApplicationManifest() { }
        public ApplicationManifest(string targetFrameworkVersion) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ConfigFile { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public override Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference EntryPoint { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ErrorReportUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileAssociationCollection FileAssociations { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool HostInBrowser { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string IconFile { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsClickOnceManifest { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public int MaxTargetPath { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string OSDescription { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string OSSupportUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string OSVersion { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Product { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Publisher { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SuiteName { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SupportUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TargetFrameworkVersion { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.TrustInfo TrustInfo { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool UseApplicationTrust { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ConfigFile")]
        public string XmlConfigFile { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlElementAttribute("EntryPointIdentity")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity XmlEntryPointIdentity { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("EntryPointParameters")]
        public string XmlEntryPointParameters { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("EntryPointPath")]
        public string XmlEntryPointPath { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ErrorReportUrl")]
        public string XmlErrorReportUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("FileAssociations")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileAssociation[] XmlFileAssociations { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("HostInBrowser")]
        public string XmlHostInBrowser { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("IconFile")]
        public string XmlIconFile { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("IsClickOnceManifest")]
        public string XmlIsClickOnceManifest { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSBuild")]
        public string XmlOSBuild { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSDescription")]
        public string XmlOSDescription { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSMajor")]
        public string XmlOSMajor { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSMinor")]
        public string XmlOSMinor { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSRevision")]
        public string XmlOSRevision { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("OSSupportUrl")]
        public string XmlOSSupportUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Product")]
        public string XmlProduct { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Publisher")]
        public string XmlPublisher { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("SuiteName")]
        public string XmlSuiteName { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("SupportUrl")]
        public string XmlSupportUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("UseApplicationTrust")]
        public string XmlUseApplicationTrust { get { throw null; } set { } }
        public override void Validate() { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    [System.Xml.Serialization.XmlRootAttribute("AssemblyIdentity")]
    public sealed partial class AssemblyIdentity
    {
        public AssemblyIdentity() { }
        public AssemblyIdentity(Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity identity) { }
        public AssemblyIdentity(string name) { }
        public AssemblyIdentity(string name, string version) { }
        public AssemblyIdentity(string name, string version, string publicKeyToken, string culture) { }
        public AssemblyIdentity(string name, string version, string publicKeyToken, string culture, string processorArchitecture) { }
        public AssemblyIdentity(string name, string version, string publicKeyToken, string culture, string processorArchitecture, string type) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Culture { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsFrameworkAssembly { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsNeutralPlatform { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsStrongName { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Name { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ProcessorArchitecture { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string PublicKeyToken { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Type { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Version { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Culture")]
        public string XmlCulture { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Name")]
        public string XmlName { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ProcessorArchitecture")]
        public string XmlProcessorArchitecture { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("PublicKeyToken")]
        public string XmlPublicKeyToken { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Type")]
        public string XmlType { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Version")]
        public string XmlVersion { get { throw null; } set { } }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity FromAssemblyName(string assemblyName) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity FromFile(string path) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity FromManagedAssembly(string path) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity FromManifest(string path) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity FromNativeAssembly(string path) { throw null; }
        public string GetFullName(Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity.FullNameFlags flags) { throw null; }
        public bool IsInFramework(string frameworkIdentifier, string frameworkVersion) { throw null; }
        public override string ToString() { throw null; }
        [System.FlagsAttribute]
        public enum FullNameFlags
        {
            Default = 0,
            ProcessorArchitecture = 1,
            Type = 2,
            All = 3,
        }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    [System.Xml.Serialization.XmlRootAttribute("AssemblyManifest")]
    public partial class AssemblyManifest : Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest
    {
        public AssemblyManifest() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ProxyStub[] ExternalProxyStubs { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("ExternalProxyStubs")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ProxyStub[] XmlExternalProxyStubs { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class AssemblyReference : Microsoft.Build.Tasks.Deployment.ManifestUtilities.BaseReference
    {
        public AssemblyReference() { }
        public AssemblyReference(string path) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity AssemblyIdentity { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsPrerequisite { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReferenceType ReferenceType { get { throw null; } set { } }
        protected internal override string SortName { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlElementAttribute("AssemblyIdentity")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity XmlAssemblyIdentity { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("IsNative")]
        public string XmlIsNative { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("IsPrerequisite")]
        public string XmlIsPrerequisite { get { throw null; } set { } }
        public override string ToString() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class AssemblyReferenceCollection : System.Collections.IEnumerable
    {
        internal AssemblyReferenceCollection() { }
        public int Count { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference this[int index] { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference Add(Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference assembly) { throw null; }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference Add(string path) { throw null; }
        public void Clear() { }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference Find(Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity identity) { throw null; }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference Find(string name) { throw null; }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference FindTargetPath(string targetPath) { throw null; }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
        public void Remove(Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference assemblyReference) { }
    }
    public enum AssemblyReferenceType
    {
        Unspecified = 0,
        ClickOnceManifest = 1,
        ManagedAssembly = 2,
        NativeAssembly = 3,
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public abstract partial class BaseReference
    {
        protected internal BaseReference() { }
        protected internal BaseReference(string path) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Group { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Hash { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsOptional { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ResolvedPath { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public long Size { get { throw null; } set { } }
        protected internal abstract string SortName { get; }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SourcePath { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TargetPath { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Group")]
        public string XmlGroup { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Hash")]
        public string XmlHash { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("HashAlg")]
        public string XmlHashAlgorithm { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("IsOptional")]
        public string XmlIsOptional { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Path")]
        public string XmlPath { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Size")]
        public string XmlSize { get { throw null; } set { } }
        public override string ToString() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public partial class ComClass
    {
        public ComClass() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ClsId { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Description { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ProgId { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ThreadingModel { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TlbId { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Clsid")]
        public string XmlClsId { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Description")]
        public string XmlDescription { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Progid")]
        public string XmlProgId { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ThreadingModel")]
        public string XmlThreadingModel { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Tlbid")]
        public string XmlTlbId { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class CompatibleFramework
    {
        public CompatibleFramework() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Profile { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SupportedRuntime { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Version { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Profile")]
        public string XmlProfile { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("SupportedRuntime")]
        public string XmlSupportedRuntime { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Version")]
        public string XmlVersion { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class CompatibleFrameworkCollection : System.Collections.IEnumerable
    {
        internal CompatibleFrameworkCollection() { }
        public int Count { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.CompatibleFramework this[int index] { get { throw null; } }
        public void Add(Microsoft.Build.Tasks.Deployment.ManifestUtilities.CompatibleFramework compatibleFramework) { }
        public void Clear() { }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    [System.Xml.Serialization.XmlRootAttribute("DeployManifest")]
    public sealed partial class DeployManifest : Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest
    {
        public DeployManifest() { }
        public DeployManifest(string targetFrameworkMoniker) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.CompatibleFrameworkCollection CompatibleFrameworks { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool CreateDesktopShortcut { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string DeploymentUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool DisallowUrlActivation { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public override Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference EntryPoint { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ErrorReportUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool Install { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool MapFileExtensions { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string MinimumRequiredVersion { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Product { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Publisher { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SuiteName { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SupportUrl { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TargetFrameworkMoniker { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool TrustUrlParameters { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool UpdateEnabled { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public int UpdateInterval { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.UpdateMode UpdateMode { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.UpdateUnit UpdateUnit { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("CompatibleFrameworks")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.CompatibleFramework[] XmlCompatibleFrameworks { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("CreateDesktopShortcut")]
        public string XmlCreateDesktopShortcut { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("DeploymentUrl")]
        public string XmlDeploymentUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("DisallowUrlActivation")]
        public string XmlDisallowUrlActivation { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ErrorReportUrl")]
        public string XmlErrorReportUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Install")]
        public string XmlInstall { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("MapFileExtensions")]
        public string XmlMapFileExtensions { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("MinimumRequiredVersion")]
        public string XmlMinimumRequiredVersion { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Product")]
        public string XmlProduct { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Publisher")]
        public string XmlPublisher { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("SuiteName")]
        public string XmlSuiteName { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("SupportUrl")]
        public string XmlSupportUrl { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("TrustUrlParameters")]
        public string XmlTrustUrlParameters { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("UpdateEnabled")]
        public string XmlUpdateEnabled { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("UpdateInterval")]
        public string XmlUpdateInterval { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("UpdateMode")]
        public string XmlUpdateMode { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("UpdateUnit")]
        public string XmlUpdateUnit { get { throw null; } set { } }
        public override void Validate() { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class FileAssociation
    {
        public FileAssociation() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string DefaultIcon { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Description { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Extension { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ProgId { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("DefaultIcon")]
        public string XmlDefaultIcon { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Description")]
        public string XmlDescription { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Extension")]
        public string XmlExtension { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Progid")]
        public string XmlProgId { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class FileAssociationCollection : System.Collections.IEnumerable
    {
        internal FileAssociationCollection() { }
        public int Count { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileAssociation this[int index] { get { throw null; } }
        public void Add(Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileAssociation fileAssociation) { }
        public void Clear() { }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class FileReference : Microsoft.Build.Tasks.Deployment.ManifestUtilities.BaseReference
    {
        public FileReference() { }
        public FileReference(string path) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ComClass[] ComClasses { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool IsDataFile { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ProxyStub[] ProxyStubs { get { throw null; } }
        protected internal override string SortName { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.TypeLib[] TypeLibs { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("ComClasses")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ComClass[] XmlComClasses { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("ProxyStubs")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.ProxyStub[] XmlProxyStubs { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("TypeLibs")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.TypeLib[] XmlTypeLibs { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("WriteableType")]
        public string XmlWriteableType { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class FileReferenceCollection : System.Collections.IEnumerable
    {
        internal FileReferenceCollection() { }
        public int Count { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference this[int index] { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference Add(Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference file) { throw null; }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference Add(string path) { throw null; }
        public void Clear() { }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference FindTargetPath(string targetPath) { throw null; }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
        public void Remove(Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference file) { }
    }
    public partial class LauncherBuilder
    {
        public LauncherBuilder(string launcherPath) { }
        public string LauncherPath { get { throw null; } set { } }
        public Microsoft.Build.Tasks.Deployment.Bootstrapper.BuildResults Build(string filename, string outputPath) { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public abstract partial class Manifest
    {
        protected internal Manifest() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity AssemblyIdentity { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string AssemblyName { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReferenceCollection AssemblyReferences { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Description { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public virtual Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference EntryPoint { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReferenceCollection FileReferences { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public System.IO.Stream InputStream { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool LauncherBasedDeployment { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.OutputMessageCollection OutputMessages { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool ReadOnly { get { throw null; } set { } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string SourcePath { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlElementAttribute("AssemblyIdentity")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyIdentity XmlAssemblyIdentity { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("AssemblyReferences")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.AssemblyReference[] XmlAssemblyReferences { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Description")]
        public string XmlDescription { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlArrayAttribute("FileReferences")]
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.FileReference[] XmlFileReferences { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Schema")]
        public string XmlSchema { get { throw null; } set { } }
        public void ResolveFiles() { }
        public void ResolveFiles(string[] searchPaths) { }
        public override string ToString() { throw null; }
        public void UpdateFileInfo() { }
        public void UpdateFileInfo(string targetFrameworkVersion) { }
        public virtual void Validate() { }
        protected void ValidatePlatform() { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public static partial class ManifestReader
    {
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest ReadManifest(System.IO.Stream input, bool preserveStream) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest ReadManifest(string path, bool preserveStream) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest ReadManifest(string manifestType, System.IO.Stream input, bool preserveStream) { throw null; }
        public static Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest ReadManifest(string manifestType, string path, bool preserveStream) { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public static partial class ManifestWriter
    {
        public static void WriteManifest(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest) { }
        public static void WriteManifest(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest, System.IO.Stream output) { }
        public static void WriteManifest(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest, string path) { }
        public static void WriteManifest(Microsoft.Build.Tasks.Deployment.ManifestUtilities.Manifest manifest, string path, string targetframeWorkVersion) { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class OutputMessage
    {
        internal OutputMessage() { }
        public string Name { get { throw null; } }
        public string Text { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.OutputMessageType Type { get { throw null; } }
        public string[] GetArguments() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class OutputMessageCollection : System.Collections.IEnumerable
    {
        internal OutputMessageCollection() { }
        public int ErrorCount { get { throw null; } }
        public Microsoft.Build.Tasks.Deployment.ManifestUtilities.OutputMessage this[int index] { get { throw null; } }
        public int WarningCount { get { throw null; } }
        public void Clear() { }
        public System.Collections.IEnumerator GetEnumerator() { throw null; }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public enum OutputMessageType
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public partial class ProxyStub
    {
        public ProxyStub() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string BaseInterface { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string IID { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Name { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string NumMethods { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TlbId { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("BaseInterface")]
        public string XmlBaseInterface { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Iid")]
        public string XmlIID { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Name")]
        public string XmlName { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("NumMethods")]
        public string XmlNumMethods { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Tlbid")]
        public string XmlTlbId { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public static partial class SecurityUtilities
    {
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static void SignFile(System.Security.Cryptography.X509Certificates.X509Certificate2 cert, System.Uri timestampUrl, string path) { }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static void SignFile(string certPath, System.Security.SecureString certPassword, System.Uri timestampUrl, string path) { }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static void SignFile(string certThumbprint, System.Uri timestampUrl, string path) { }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static void SignFile(string certThumbprint, System.Uri timestampUrl, string path, string targetFrameworkVersion) { }
        [System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
        public static void SignFile(string certThumbprint, System.Uri timestampUrl, string path, string targetFrameworkVersion, string targetFrameworkIdentifier) { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public sealed partial class TrustInfo
    {
        public TrustInfo() { }
        public bool HasUnmanagedCodePermission { get { throw null; } }
        public bool IsFullTrust { get { throw null; } }
        public bool PreserveFullTrustPermissionSet { get { throw null; } set { } }
        public string SameSiteAccess { get { throw null; } set { } }
        public void Clear() { }
        public void Read(System.IO.Stream input) { }
        public void Read(string path) { }
        public void ReadManifest(System.IO.Stream input) { }
        public void ReadManifest(string path) { }
        public override string ToString() { throw null; }
        public void Write(System.IO.Stream output) { }
        public void Write(string path) { }
        public void WriteManifest(System.IO.Stream output) { }
        public void WriteManifest(System.IO.Stream input, System.IO.Stream output) { }
        public void WriteManifest(string path) { }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public partial class TypeLib
    {
        public TypeLib() { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Flags { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string HelpDirectory { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string ResourceId { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string TlbId { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Version { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Flags")]
        public string XmlFlags { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("HelpDir")]
        public string XmlHelpDirectory { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("ResourceId")]
        public string XmlResourceId { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Tlbid")]
        public string XmlTlbId { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Version")]
        public string XmlVersion { get { throw null; } set { } }
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public enum UpdateMode
    {
        Background = 0,
        Foreground = 1,
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public enum UpdateUnit
    {
        Hours = 0,
        Days = 1,
        Weeks = 2,
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(false)]
    public partial class WindowClass
    {
        public WindowClass() { }
        public WindowClass(string name, bool versioned) { }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public string Name { get { throw null; } }
        [System.Xml.Serialization.XmlIgnoreAttribute]
        public bool Versioned { get { throw null; } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Name")]
        public string XmlName { get { throw null; } set { } }
        [System.ComponentModel.BrowsableAttribute(false)]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Xml.Serialization.XmlAttributeAttribute("Versioned")]
        public string XmlVersioned { get { throw null; } set { } }
    }
}
namespace Microsoft.Build.Tasks.Hosting
{
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("B5A95716-2053-4B70-9FBF-E4148EBA96BC")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IAnalyzerHostObject
    {
        bool SetAdditionalFiles(Microsoft.Build.Framework.ITaskItem[] additionalFiles);
        bool SetAnalyzers(Microsoft.Build.Framework.ITaskItem[] analyzers);
        bool SetRuleSet(string ruleSetFile);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("8520CC4D-64DC-4855-BE3F-4C28CCE048EE")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface ICscHostObject : Microsoft.Build.Framework.ITaskHost
    {
        void BeginInitialization();
        bool Compile();
        bool EndInitialization(out string errorMessage, out int errorCode);
        bool IsDesignTime();
        bool IsUpToDate();
        bool SetAdditionalLibPaths(string[] additionalLibPaths);
        bool SetAddModules(string[] addModules);
        bool SetAllowUnsafeBlocks(bool allowUnsafeBlocks);
        bool SetBaseAddress(string baseAddress);
        bool SetCheckForOverflowUnderflow(bool checkForOverflowUnderflow);
        bool SetCodePage(int codePage);
        bool SetDebugType(string debugType);
        bool SetDefineConstants(string defineConstants);
        bool SetDelaySign(bool delaySignExplicitlySet, bool delaySign);
        bool SetDisabledWarnings(string disabledWarnings);
        bool SetDocumentationFile(string documentationFile);
        bool SetEmitDebugInformation(bool emitDebugInformation);
        bool SetErrorReport(string errorReport);
        bool SetFileAlignment(int fileAlignment);
        bool SetGenerateFullPaths(bool generateFullPaths);
        bool SetKeyContainer(string keyContainer);
        bool SetKeyFile(string keyFile);
        bool SetLangVersion(string langVersion);
        bool SetLinkResources(Microsoft.Build.Framework.ITaskItem[] linkResources);
        bool SetMainEntryPoint(string targetType, string mainEntryPoint);
        bool SetModuleAssemblyName(string moduleAssemblyName);
        bool SetNoConfig(bool noConfig);
        bool SetNoStandardLib(bool noStandardLib);
        bool SetOptimize(bool optimize);
        bool SetOutputAssembly(string outputAssembly);
        bool SetPdbFile(string pdbFile);
        bool SetPlatform(string platform);
        bool SetReferences(Microsoft.Build.Framework.ITaskItem[] references);
        bool SetResources(Microsoft.Build.Framework.ITaskItem[] resources);
        bool SetResponseFiles(Microsoft.Build.Framework.ITaskItem[] responseFiles);
        bool SetSources(Microsoft.Build.Framework.ITaskItem[] sources);
        bool SetTargetType(string targetType);
        bool SetTreatWarningsAsErrors(bool treatWarningsAsErrors);
        bool SetWarningLevel(int warningLevel);
        bool SetWarningsAsErrors(string warningsAsErrors);
        bool SetWarningsNotAsErrors(string warningsNotAsErrors);
        bool SetWin32Icon(string win32Icon);
        bool SetWin32Resource(string win32Resource);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("D6D4E228-259A-4076-B5D0-0627338BCC10")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface ICscHostObject2 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.ICscHostObject
    {
        bool SetWin32Manifest(string win32Manifest);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("F9353662-F1ED-4a23-A323-5F5047E85F5D")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface ICscHostObject3 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.ICscHostObject, Microsoft.Build.Tasks.Hosting.ICscHostObject2
    {
        bool SetApplicationConfiguration(string applicationConfiguration);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("0DDB496F-C93C-492C-87F1-90B6FDBAA833")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface ICscHostObject4 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.ICscHostObject, Microsoft.Build.Tasks.Hosting.ICscHostObject2, Microsoft.Build.Tasks.Hosting.ICscHostObject3
    {
        bool SetHighEntropyVA(bool highEntropyVA);
        bool SetPlatformWith32BitPreference(string platformWith32BitPreference);
        bool SetSubsystemVersion(string subsystemVersion);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("7D7AC3BE-253A-40e8-A3FF-357D0DA7C47A")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObject : Microsoft.Build.Framework.ITaskHost
    {
        void BeginInitialization();
        bool Compile();
        void EndInitialization();
        bool IsDesignTime();
        bool IsUpToDate();
        bool SetAdditionalLibPaths(string[] additionalLibPaths);
        bool SetAddModules(string[] addModules);
        bool SetBaseAddress(string targetType, string baseAddress);
        bool SetCodePage(int codePage);
        bool SetDebugType(bool emitDebugInformation, string debugType);
        bool SetDefineConstants(string defineConstants);
        bool SetDelaySign(bool delaySign);
        bool SetDisabledWarnings(string disabledWarnings);
        bool SetDocumentationFile(string documentationFile);
        bool SetErrorReport(string errorReport);
        bool SetFileAlignment(int fileAlignment);
        bool SetGenerateDocumentation(bool generateDocumentation);
        bool SetImports(Microsoft.Build.Framework.ITaskItem[] importsList);
        bool SetKeyContainer(string keyContainer);
        bool SetKeyFile(string keyFile);
        bool SetLinkResources(Microsoft.Build.Framework.ITaskItem[] linkResources);
        bool SetMainEntryPoint(string mainEntryPoint);
        bool SetNoConfig(bool noConfig);
        bool SetNoStandardLib(bool noStandardLib);
        bool SetNoWarnings(bool noWarnings);
        bool SetOptimize(bool optimize);
        bool SetOptionCompare(string optionCompare);
        bool SetOptionExplicit(bool optionExplicit);
        bool SetOptionStrict(bool optionStrict);
        bool SetOptionStrictType(string optionStrictType);
        bool SetOutputAssembly(string outputAssembly);
        bool SetPlatform(string platform);
        bool SetReferences(Microsoft.Build.Framework.ITaskItem[] references);
        bool SetRemoveIntegerChecks(bool removeIntegerChecks);
        bool SetResources(Microsoft.Build.Framework.ITaskItem[] resources);
        bool SetResponseFiles(Microsoft.Build.Framework.ITaskItem[] responseFiles);
        bool SetRootNamespace(string rootNamespace);
        bool SetSdkPath(string sdkPath);
        bool SetSources(Microsoft.Build.Framework.ITaskItem[] sources);
        bool SetTargetCompactFramework(bool targetCompactFramework);
        bool SetTargetType(string targetType);
        bool SetTreatWarningsAsErrors(bool treatWarningsAsErrors);
        bool SetWarningsAsErrors(string warningsAsErrors);
        bool SetWarningsNotAsErrors(string warningsNotAsErrors);
        bool SetWin32Icon(string win32Icon);
        bool SetWin32Resource(string win32Resource);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("f59afc84-d102-48b1-a090-1b90c79d3e09")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObject2 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.IVbcHostObject
    {
        bool SetModuleAssemblyName(string moduleAssemblyName);
        bool SetOptionInfer(bool optionInfer);
        bool SetWin32Manifest(string win32Manifest);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("1186fe8f-8aba-48d6-8ce3-32ca42f53728")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObject3 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.IVbcHostObject, Microsoft.Build.Tasks.Hosting.IVbcHostObject2
    {
        bool SetLanguageVersion(string languageVersion);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("2AE3233C-8AB3-48A0-9ED9-6E3545B3C566")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObject4 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.IVbcHostObject, Microsoft.Build.Tasks.Hosting.IVbcHostObject2, Microsoft.Build.Tasks.Hosting.IVbcHostObject3
    {
        bool SetVBRuntime(string VBRuntime);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("5ACF41FF-6F2B-4623-8146-740C89212B21")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObject5 : Microsoft.Build.Framework.ITaskHost, Microsoft.Build.Tasks.Hosting.IVbcHostObject, Microsoft.Build.Tasks.Hosting.IVbcHostObject2, Microsoft.Build.Tasks.Hosting.IVbcHostObject3, Microsoft.Build.Tasks.Hosting.IVbcHostObject4
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.PreserveSig)]int CompileAsync(out System.IntPtr buildSucceededEvent, out System.IntPtr buildFailedEvent);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.PreserveSig)]int EndCompile(bool buildSuccess);
        Microsoft.Build.Tasks.Hosting.IVbcHostObjectFreeThreaded GetFreeThreadedHostObject();
        bool SetHighEntropyVA(bool highEntropyVA);
        bool SetPlatformWith32BitPreference(string platformWith32BitPreference);
        bool SetSubsystemVersion(string subsystemVersion);
    }
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    [System.Runtime.InteropServices.GuidAttribute("ECCF972F-8C2D-4F51-9746-9288661DE2CB")]
    [System.Runtime.InteropServices.InterfaceTypeAttribute(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    public partial interface IVbcHostObjectFreeThreaded
    {
        bool Compile();
    }
}
namespace System.Deployment.Internal.CodeSigning
{
    public sealed partial class RSAPKCS1SHA256SignatureDescription : System.Security.Cryptography.SignatureDescription
    {
        public RSAPKCS1SHA256SignatureDescription() { }
        public override System.Security.Cryptography.AsymmetricSignatureDeformatter CreateDeformatter(System.Security.Cryptography.AsymmetricAlgorithm key) { throw null; }
        public override System.Security.Cryptography.AsymmetricSignatureFormatter CreateFormatter(System.Security.Cryptography.AsymmetricAlgorithm key) { throw null; }
    }
}
