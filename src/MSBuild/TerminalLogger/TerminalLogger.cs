// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Utilities;
using DictionaryEntry = System.Collections.DictionaryEntry;


#if NET7_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
#if NETFRAMEWORK
using Microsoft.IO;
#else
using System.IO;
#endif

namespace Microsoft.Build.Logging.TerminalLogger;

/// <summary>
/// A logger which updates the console output "live" during the build.
/// </summary>
/// <remarks>
/// Uses ANSI/VT100 control codes to erase and overwrite lines as the build is progressing.
/// </remarks>
internal sealed partial class TerminalLogger : INodeLogger
{
    private const string FilePathPattern = " -> ";

#if NET7_0_OR_GREATER
    [StringSyntax(StringSyntaxAttribute.Regex)]
    private const string ImmediateMessagePattern = @"\[CredentialProvider\]|--interactive";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    [GeneratedRegex(ImmediateMessagePattern, Options)]
    private static partial Regex ImmediateMessageRegex();
#else
    private static readonly string[] _immediateMessageKeywords = { "[CredentialProvider]", "--interactive" };
#endif

    /// <summary>
    /// A wrapper over the project context ID passed to us in <see cref="IEventSource"/> logger events.
    /// </summary>
    internal record struct ProjectContext(int Id)
    {
        public ProjectContext(BuildEventContext context)
            : this(context.ProjectContextId)
        { }
    }

    /// <summary>
    /// The indentation to use for all build output.
    /// </summary>
    internal const string Indentation = "  ";

    internal const TerminalColor TargetFrameworkColor = TerminalColor.Cyan;

    internal Func<StopwatchAbstraction>? CreateStopwatch = null;

    /// <summary>
    /// Protects access to state shared between the logger callbacks and the rendering thread.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// A cancellation token to signal the rendering thread that it should exit.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Tracks the status of all relevant projects seen so far.
    /// </summary>
    /// <remarks>
    /// Keyed by an ID that gets passed to logger callbacks, this allows us to quickly look up the corresponding project.
    /// </remarks>
    private readonly Dictionary<ProjectContext, Project> _projects = new();

    /// <summary>
    /// Tracks the work currently being done by build nodes. Null means the node is not doing any work worth reporting.
    /// </summary>
    private NodeStatus?[] _nodes = Array.Empty<NodeStatus>();

    /// <summary>
    /// The timestamp of the <see cref="IEventSource.BuildStarted"/> event.
    /// </summary>
    private DateTime _buildStartTime;

    /// <summary>
    /// The working directory when the build starts, to trim relative output paths.
    /// </summary>
    private readonly DirectoryInfo _initialWorkingDirectory = new(Environment.CurrentDirectory);

    /// <summary>
    /// True if the build has encountered at least one error.
    /// </summary>
    private bool _buildHasErrors;

    /// <summary>
    /// True if the build has encountered at least one warning.
    /// </summary>
    private bool _buildHasWarnings;

    /// <summary>
    /// True if restore failed and this failure has already been reported.
    /// </summary>
    private bool _restoreFailed;

    /// <summary>
    /// True if restore happened and finished.
    /// </summary>
    private bool _restoreFinished = false;

    /// <summary>
    /// The project build context corresponding to the <c>Restore</c> initial target, or null if the build is currently
    /// not restoring.
    /// </summary>
    private ProjectContext? _restoreContext;

    /// <summary>
    /// The thread that performs periodic refresh of the console output.
    /// </summary>
    private Thread? _refresher;

    /// <summary>
    /// What is currently displaying in Nodes section as strings representing per-node console output.
    /// </summary>
    private NodesFrame _currentFrame = new(Array.Empty<NodeStatus>(), 0, 0);

    /// <summary>
    /// The <see cref="Terminal"/> to write console output to.
    /// </summary>
    private ITerminal Terminal { get; }

    /// <summary>
    /// Should the logger's test environment refresh the console output manually instead of using a background thread?
    /// </summary>
    private bool _manualRefresh;

    /// <summary>
    /// True if we've logged the ".NET SDK is preview" message.
    /// </summary>
    private bool _loggedPreviewMessage;

    /// <summary>
    /// List of events the logger needs as parameters to the <see cref="ConfigurableForwardingLogger"/>.
    /// </summary>
    /// <remarks>
    /// If TerminalLogger runs as a distributed logger, MSBuild out-of-proc nodes might filter the events that will go to the main
    /// node using an instance of <see cref="ConfigurableForwardingLogger"/> with the following parameters.
    /// Important: Note that TerminalLogger is special-cased in <see cref="BackEnd.Logging.LoggingService.UpdateMinimumMessageImportance"/>
    /// so changing this list may impact the minimum message importance logging optimization.
    /// </remarks>
    public static readonly string[] ConfigurableForwardingLoggerParameters =
    {
            "BUILDSTARTEDEVENT",
            "BUILDFINISHEDEVENT",
            "PROJECTSTARTEDEVENT",
            "PROJECTFINISHEDEVENT",
            "TARGETSTARTEDEVENT",
            "TARGETFINISHEDEVENT",
            "TASKSTARTEDEVENT",
            "HIGHMESSAGEEVENT",
            "WARNINGEVENT",
            "ERROREVENT"
    };

    /// <summary>
    /// The two directory separator characters to be passed to methods like <see cref="String.IndexOfAny(char[])"/>.
    /// </summary>
    private static readonly char[] PathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>
    /// One summary per finished project test run.
    /// </summary>
    private List<TestSummary> _testRunSummaries = new();

    /// <summary>
    /// Name of target that identifies a project that has tests, and that they just started.
    /// </summary>
    private static string _testStartTarget = "_TestRunStart";

    /// <summary>
    /// Time of the oldest observed test target start.
    /// </summary>
    private DateTime? _testStartTime;

    /// <summary>
    /// Time of the most recently observed test target finished.
    /// </summary>
    private DateTime? _testEndTime;

    /// <summary>
    /// Default constructor, used by the MSBuild logger infra.
    /// </summary>
    public TerminalLogger()
    {
        Terminal = new Terminal();
    }

    /// <summary>
    /// Internal constructor accepting a custom <see cref="ITerminal"/> for testing.
    /// </summary>
    internal TerminalLogger(ITerminal terminal)
    {
        Terminal = terminal;
        _manualRefresh = true;
    }

    #region INodeLogger implementation

    /// <inheritdoc/>
    public LoggerVerbosity Verbosity { get => LoggerVerbosity.Minimal; set { } }

    /// <inheritdoc/>
    public string Parameters
    {
        get => ""; set { }
    }

    /// <inheritdoc/>
    public void Initialize(IEventSource eventSource, int nodeCount)
    {
        // When MSBUILDNOINPROCNODE enabled, NodeId's reported by build start with 2. We need to reserve an extra spot for this case.
        _nodes = new NodeStatus[nodeCount + 1];

        Initialize(eventSource);
    }

    /// <inheritdoc/>
    public void Initialize(IEventSource eventSource)
    {
        eventSource.BuildStarted += BuildStarted;
        eventSource.BuildFinished += BuildFinished;
        eventSource.ProjectStarted += ProjectStarted;
        eventSource.ProjectFinished += ProjectFinished;
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.TaskStarted += TaskStarted;

        eventSource.MessageRaised += MessageRaised;
        eventSource.WarningRaised += WarningRaised;
        eventSource.ErrorRaised += ErrorRaised;

        if (eventSource is IEventSource3 eventSource3)
        {
            eventSource3.IncludeTaskInputs();
        }

        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeEvaluationPropertiesAndItems();
        }
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        _cts.Cancel();
        _refresher?.Join();
        Terminal.Dispose();
        _cts.Dispose();
    }

    #endregion

    #region Logger callbacks

    /// <summary>
    /// The <see cref="IEventSource.BuildStarted"/> callback.
    /// </summary>
    private void BuildStarted(object sender, BuildStartedEventArgs e)
    {
        if (!_manualRefresh)
        {
            _refresher = new Thread(ThreadProc);
            _refresher.Start();
        }

        _buildStartTime = e.Timestamp;

        if (Terminal.SupportsProgressReporting)
        {
            Terminal.Write(AnsiCodes.SetProgressIndeterminate);
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.BuildFinished"/> callback.
    /// </summary>
    private void BuildFinished(object sender, BuildFinishedEventArgs e)
    {
        _cts.Cancel();
        _refresher?.Join();

        _projects.Clear();

        Terminal.BeginUpdate();
        try
        {
            string duration = (e.Timestamp - _buildStartTime).TotalSeconds.ToString("F1");
            string buildResult = RenderBuildResult(e.Succeeded, _buildHasErrors, _buildHasWarnings);

            Terminal.WriteLine("");
            if (_restoreFailed)
            {
                Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("RestoreCompleteWithMessage",
                    buildResult,
                    duration));
            }
            else
            {
                Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("BuildFinished",
                    buildResult,
                    duration));
            }

            if (_testRunSummaries.Any())
            {
                var total = _testRunSummaries.Sum(t => t.Total);
                var failed = _testRunSummaries.Sum(t => t.Failed);
                var passed = _testRunSummaries.Sum(t => t.Passed);
                var skipped = _testRunSummaries.Sum(t => t.Skipped);
                var testDuration = (_testStartTime != null && _testEndTime != null ? (_testEndTime - _testStartTime).Value.TotalSeconds : 0).ToString("F1");

                var colorizedResult = _testRunSummaries.Any(t => t.Failed > 0) || _buildHasErrors
                    ? AnsiCodes.Colorize(ResourceUtilities.GetResourceString("BuildResult_Failed"), TerminalColor.Red)
                    : AnsiCodes.Colorize(ResourceUtilities.GetResourceString("BuildResult_Succeeded"), TerminalColor.Green);

                Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary",
                    colorizedResult,
                    total,
                    failed,
                    passed,
                    skipped,
                    testDuration));
            }
        }
        finally
        {
            if (Terminal.SupportsProgressReporting)
            {
                Terminal.Write(AnsiCodes.RemoveProgress);
            }

            Terminal.EndUpdate();
        }

        _testRunSummaries.Clear();
        _buildHasErrors = false;
        _buildHasWarnings = false;
        _restoreFailed = false;
        _testStartTime = null;
        _testEndTime = null;
    }

    /// <summary>
    /// The <see cref="IEventSource.ProjectStarted"/> callback.
    /// </summary>
    private void ProjectStarted(object sender, ProjectStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        ProjectContext c = new ProjectContext(buildEventContext);

        if (_restoreContext is null)
        {
            if (e.GlobalProperties?.TryGetValue("TargetFramework", out string? targetFramework) != true)
            {
                targetFramework = null;
            }
            Project project = new(targetFramework, CreateStopwatch?.Invoke());
            _projects[c] = project;

            // First ever restore in the build is starting.
            if (e.TargetNames == "Restore" && !_restoreFinished)
            {
                _restoreContext = c;
                int nodeIndex = NodeIndexForContext(buildEventContext);
                _nodes[nodeIndex] = new NodeStatus(e.ProjectFile!, null, "Restore", _projects[c].Stopwatch);
            }

            TryDetectGenerateFullPaths(e, project);
        }
    }

    private void TryDetectGenerateFullPaths(ProjectStartedEventArgs e, Project project)
    {
        if (e.GlobalProperties is not null
            && e.GlobalProperties.TryGetValue("GenerateFullPaths", out string? generateFullPaths)
            && bool.TryParse(generateFullPaths, out bool generateFullPathsValue))
        {
            project.GenerateFullPaths = generateFullPathsValue;
        }
        else if (e.Properties is not null)
        {
            foreach (DictionaryEntry property in e.Properties)
            {
                if (property.Key is "GenerateFullPaths" &&
                    property.Value is string generateFullPathsString
                    && bool.TryParse(generateFullPathsString, out bool generateFullPathsPropertyValue))
                {
                    project.GenerateFullPaths = generateFullPathsPropertyValue;
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.ProjectFinished"/> callback.
    /// </summary>
    private void ProjectFinished(object sender, ProjectFinishedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        // Mark node idle until something uses it again
        if (_restoreContext is null)
        {
            UpdateNodeStatus(buildEventContext, null);
        }

        ProjectContext c = new(buildEventContext);

        if (_projects.TryGetValue(c, out Project? project))
        {
            lock (_lock)
            {
                Terminal.BeginUpdate();
                try
                {
                    EraseNodes();

                    string duration = project.Stopwatch.ElapsedSeconds.ToString("F1");

                    string projectFile = e.ProjectFile is not null ?
                        Path.GetFileNameWithoutExtension(e.ProjectFile) :
                        string.Empty;

                    // Build result. One of 'failed', 'succeeded with warnings', or 'succeeded' depending on the build result and diagnostic messages
                    // reported during build.
                    bool haveErrors = project.BuildMessages?.Exists(m => m.Severity == MessageSeverity.Error) == true;
                    bool haveWarnings = project.BuildMessages?.Exists(m => m.Severity == MessageSeverity.Warning) == true;

                    string buildResult = RenderBuildResult(e.Succeeded, haveErrors, haveWarnings);

                    // Check if we're done restoring.
                    if (c == _restoreContext)
                    {
                        if (e.Succeeded)
                        {
                            if (haveErrors || haveWarnings)
                            {
                                Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("RestoreCompleteWithMessage",
                                    buildResult,
                                    duration));
                            }
                            else
                            {
                                Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("RestoreComplete",
                                    duration));
                            }
                        }
                        else
                        {
                            // It will be reported after build finishes.
                            _restoreFailed = true;
                        }

                        _restoreContext = null;
                        _restoreFinished = true;
                    }
                    // If this was a notable project build, we print it as completed only if it's produced an output or warnings/error.
                    // If this is a test project, print it always, so user can see either a success or failure, otherwise success is hidden
                    // and it is hard to see if project finished, or did not run at all.
                    else if (project.OutputPath is not null || project.BuildMessages is not null || project.IsTestProject)
                    {
                        // Show project build complete and its output
                        if (project.IsTestProject)
                        {
                            if (string.IsNullOrEmpty(project.TargetFramework))
                            {
                                Terminal.Write(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestProjectFinished_NoTF",
                                    Indentation,
                                    projectFile,
                                    buildResult,
                                    duration));
                            }
                            else
                            {
                                Terminal.Write(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestProjectFinished_WithTF",
                                    Indentation,
                                    projectFile,
                                    AnsiCodes.Colorize(project.TargetFramework, TargetFrameworkColor),
                                    buildResult,
                                    duration));
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(project.TargetFramework))
                            {
                                Terminal.Write(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("ProjectFinished_NoTF",
                                    Indentation,
                                    projectFile,
                                    buildResult,
                                    duration));
                            }
                            else
                            {
                                Terminal.Write(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("ProjectFinished_WithTF",
                                    Indentation,
                                    projectFile,
                                    AnsiCodes.Colorize(project.TargetFramework, TargetFrameworkColor),
                                    buildResult,
                                    duration));
                            }
                        }

                        // Print the output path as a link if we have it.
                        if (project.OutputPath is FileInfo outputFile)
                        {
                            ReadOnlySpan<char> outputPathSpan = outputFile.FullName.AsSpan();
                            ReadOnlySpan<char> url = outputPathSpan;
                            try
                            {
                                // If possible, make the link point to the containing directory of the output.
                                url = outputFile.DirectoryName.AsSpan();
                            }
                            catch
                            {
                                // Ignore any GetDirectoryName exceptions.
                            }

                            // Generates file:// schema url string which is better handled by various Terminal clients than raw folder name.
                            string urlString = url.ToString();
                            if (Uri.TryCreate(urlString, UriKind.Absolute, out Uri? uri))
                            {
                                urlString = uri.AbsoluteUri;
                            }

                            string? resolvedPathToOutput = null;
                            if (project.GenerateFullPaths)
                            {
                                resolvedPathToOutput = outputPathSpan.ToString();
                            }
                            else
                            {
                                var outputPathString = outputPathSpan.ToString();
                                var workingDirectory = _initialWorkingDirectory;

                                // If the output path is under the initial working directory, make the console output relative to that to save space.
                                if (IsChildOf(outputFile, workingDirectory))
                                {
                                    resolvedPathToOutput = Path.GetRelativePath(workingDirectory.FullName, outputPathString);
                                }

                                // if the output path isn't under the working directory, but is under the source root, make the output relative to that to save space
                                else if (project.SourceRoot is DirectoryInfo sourceRoot
                                            && project.OutputPath is FileInfo outputFileInfo
                                            && IsChildOf(outputFileInfo, sourceRoot))
                                {
                                    resolvedPathToOutput = Path.GetRelativePath(sourceRoot.FullName, outputPathString);
                                }
                                else if (project.SourceRoot is DirectoryInfo sourceRootDir)
                                {
                                    var relativePathFromOutputToRoot = Path.GetRelativePath(sourceRootDir.FullName, outputPathString);
                                    // we have the portion from sourceRoot to outputPath, now we need to get the portion from workingDirectory to sourceRoot
                                    var relativePathFromWorkingDirToSourceRoot = Path.GetRelativePath(workingDirectory.FullName, sourceRootDir.FullName);
                                    resolvedPathToOutput = Path.Join(relativePathFromWorkingDirToSourceRoot, relativePathFromOutputToRoot);
                                }
                                else
                                {
                                    // in this case, with no reasonable working directory and no reasonable sourceroot,
                                    // we just emit the full path.
                                    resolvedPathToOutput = outputPathString;
                                }
                            }

                            Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("ProjectFinished_OutputPath",
                                $"{AnsiCodes.LinkPrefix}{urlString}{AnsiCodes.LinkInfix}{resolvedPathToOutput}{AnsiCodes.LinkSuffix}"));
                        }
                        else
                        {
                            Terminal.WriteLine(string.Empty);
                        }
                    }

                    // Print diagnostic output under the Project -> Output line.
                    if (project.BuildMessages is not null)
                    {
                        foreach (BuildMessage buildMessage in project.BuildMessages)
                        {
                            Terminal.WriteLine($"{Indentation}{Indentation}{buildMessage.Message}");
                        }
                    }

                    _buildHasErrors |= haveErrors;
                    _buildHasWarnings |= haveWarnings;

                    DisplayNodes();
                }
                finally
                {
                    Terminal.EndUpdate();
                }
            }
        }
    }

    private static bool IsChildOf(FileInfo file, DirectoryInfo parent)
    {
        DirectoryInfo? current = file.Directory;
        if (current is null)
        {
            return false;
        }
        if (current == parent)
        {
            return true;
        }

        while (current?.Parent is not null)
        {
            if (current == parent)
            {
                return true;
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// The <see cref="IEventSource.TargetStarted"/> callback.
    /// </summary>
    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (_restoreContext is null && buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            project.Stopwatch.Start();

            string projectFile = Path.GetFileNameWithoutExtension(e.ProjectFile);

            var isTestTarget = e.TargetName == _testStartTarget;

            var targetName = isTestTarget ? "Testing" : e.TargetName;
            if (isTestTarget)
            {
                // Use the minimal start time, so if we run tests in parallel, we can calculate duration
                // as this start time, minus time when tests finished.
                _testStartTime = _testStartTime == null
                    ? e.Timestamp
                    : e.Timestamp < _testStartTime
                        ? e.Timestamp : _testStartTime;
                project.IsTestProject = true;
            }

            NodeStatus nodeStatus = new(projectFile, project.TargetFramework, targetName, project.Stopwatch);
            UpdateNodeStatus(buildEventContext, nodeStatus);
        }
    }

    private void UpdateNodeStatus(BuildEventContext buildEventContext, NodeStatus? nodeStatus)
    {
        lock (_lock)
        {
            int nodeIndex = NodeIndexForContext(buildEventContext);
            _nodes[nodeIndex] = nodeStatus;
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.TargetFinished"/> callback. Unused.
    /// </summary>
    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
    }

    private void TryReadSourceControlInformationForProject(BuildEventContext? context, IEnumerable<ITaskItem>? sourceRoots)
    {
        if (context is null || sourceRoots is null)
        {
            return;
        }

        var projectContext = new ProjectContext(context);
        if (_projects.TryGetValue(projectContext, out Project? project))
        {
            if (project.SourceRoot is not null)
            {
                return;
            }
            var sourceControlSourceRoot = sourceRoots.FirstOrDefault(root => !string.IsNullOrEmpty(root.GetMetadata("SourceControl")));
            if (sourceControlSourceRoot is not null)
            {
                // This takes the first root from source control the first time it's added to the build.
                // This seems to be the Target InitializeSourceControlInformationFromSourceControlManager.
                // So far this has been acceptable, but if a SourceRoot would be modified by a task later on
                // (e.g. TranslateGitHubUrlsInSourceControlInformation) we would lose that modification.
                try
                {
                    project.SourceRoot = new(sourceControlSourceRoot.ItemSpec);
                }
                catch { } // ignore exceptions from trying to make the SourceRoot a DirectoryInfo, if this is invalid then we just won't use it.
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.TaskStarted"/> callback.
    /// </summary>
    private void TaskStarted(object sender, TaskStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (_restoreContext is null && buildEventContext is not null && e.TaskName == "MSBuild")
        {
            // This will yield the node, so preemptively mark it idle
            UpdateNodeStatus(buildEventContext, null);

            if (_projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
            {
                project.Stopwatch.Stop();
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.MessageRaised"/> callback.
    /// </summary>
    private void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        string? message = e.Message;
        if (e is TaskParameterEventArgs taskArgs)
        {
            if (taskArgs.Kind == TaskParameterMessageKind.AddItem)
            {
                if (taskArgs.ItemType.Equals("SourceRoot", StringComparison.OrdinalIgnoreCase))
                {
                    TryReadSourceControlInformationForProject(taskArgs.BuildEventContext, taskArgs.Items as IList<ProjectItemInstance>);
                }
            }
        }
        if (message is not null && e.Importance == MessageImportance.High)
        {
            var hasProject = _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project);
            // Detect project output path by matching high-importance messages against the "$(MSBuildProjectName) -> ..."
            // pattern used by the CopyFilesToOutputDirectory target.
            int index = message.IndexOf(FilePathPattern, StringComparison.Ordinal);
            if (index > 0)
            {
                var projectFileName = Path.GetFileName(e.ProjectFile.AsSpan());
                if (!projectFileName.IsEmpty &&
                    message.AsSpan().StartsWith(Path.GetFileNameWithoutExtension(projectFileName)) && hasProject)
                {
                    ReadOnlyMemory<char> outputPath = e.Message.AsMemory().Slice(index + 4);
                    try
                    {
                        project!.OutputPath = new(outputPath.ToString());
                    }
                    catch { } // ignore exceptions from trying to make the OutputPath a FileInfo, if this is invalid then we just won't use it.
                }
            }

            if (IsImmediateMessage(message))
            {
                RenderImmediateMessage(message);
            }
            else if (hasProject && project!.IsTestProject)
            {
                var node = _nodes[NodeIndexForContext(buildEventContext)];

                // Consumes test update messages produced by VSTest and MSTest runner.
                if (node != null && e is IExtendedBuildEventArgs extendedMessage)
                {
                    switch (extendedMessage.ExtendedType)
                    {
                        case "TLTESTPASSED":
                            {
                                var indicator = extendedMessage.ExtendedMetadata!["localizedResult"]!;
                                var displayName = extendedMessage.ExtendedMetadata!["displayName"]!;

                                var status = new NodeStatus(node.Project, node.TargetFramework, TerminalColor.Green, indicator, displayName, project.Stopwatch);
                                UpdateNodeStatus(buildEventContext, status);
                                break;
                            }

                        case "TLTESTSKIPPED":
                            {
                                var indicator = extendedMessage.ExtendedMetadata!["localizedResult"]!;
                                var displayName = extendedMessage.ExtendedMetadata!["displayName"]!;

                                var status = new NodeStatus(node.Project, node.TargetFramework, TerminalColor.Yellow, indicator, displayName, project.Stopwatch);
                                UpdateNodeStatus(buildEventContext, status);
                                break;
                            }

                        case "TLTESTFINISH":
                            {
                                _ = int.TryParse(extendedMessage.ExtendedMetadata!["total"]!, out int total);
                                _ = int.TryParse(extendedMessage.ExtendedMetadata!["passed"]!, out int passed);
                                _ = int.TryParse(extendedMessage.ExtendedMetadata!["skipped"]!, out int skipped);
                                _ = int.TryParse(extendedMessage.ExtendedMetadata!["failed"]!, out int failed);

                                _testRunSummaries.Add(new TestSummary(total, passed, skipped, failed));

                                _testEndTime = _testEndTime == null
                                        ? e.Timestamp
                                        : e.Timestamp > _testEndTime
                                            ? e.Timestamp : _testEndTime;
                                break;
                            }
                    }
                }
            }
            else if (e.Code == "NETSDK1057" && !_loggedPreviewMessage)
            {
                // The SDK will log the high-pri "not-a-warning" message NETSDK1057
                // when it's a preview version up to MaxCPUCount times, but that's
                // an implementation detail--the user cares about at most one.

                RenderImmediateMessage(message);
                _loggedPreviewMessage = true;
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.WarningRaised"/> callback.
    /// </summary>
    private void WarningRaised(object sender, BuildWarningEventArgs e)
    {
        BuildEventContext? buildEventContext = e.BuildEventContext;
        string message = EventArgsFormatting.FormatEventMessage(
                category: AnsiCodes.Colorize("warning", TerminalColor.Yellow),
                subcategory: e.Subcategory,
                message: e.Message,
                code: AnsiCodes.Colorize(e.Code, TerminalColor.Yellow),
                file: HighlightFileName(e.File),
                projectFile: e.ProjectFile ?? null,
                lineNumber: e.LineNumber,
                endLineNumber: e.EndLineNumber,
                columnNumber: e.ColumnNumber,
                endColumnNumber: e.EndColumnNumber,
                threadId: e.ThreadId,
                logOutputProperties: null);

        if (buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            if (IsImmediateMessage(message))
            {
                RenderImmediateMessage(message);
            }

            project.AddBuildMessage(MessageSeverity.Warning, message);
        }
        else
        {
            // It is necessary to display warning messages reported by MSBuild, even if it's not tracked in _projects collection.
            RenderImmediateMessage(message);
            _buildHasWarnings = true;
        }
    }

    /// <summary>
    /// Detect markers that require special attention from a customer.
    /// </summary>
    /// <param name="message">Raised event.</param>
    /// <returns>true if marker is detected.</returns>
    private bool IsImmediateMessage(string message) =>
#if NET7_0_OR_GREATER
        ImmediateMessageRegex().IsMatch(message);
#else
        _immediateMessageKeywords.Any(imk => message.IndexOf(imk, StringComparison.OrdinalIgnoreCase) >= 0);
#endif

    /// <summary>
    /// The <see cref="IEventSource.ErrorRaised"/> callback.
    /// </summary>
    private void ErrorRaised(object sender, BuildErrorEventArgs e)
    {
        BuildEventContext? buildEventContext = e.BuildEventContext;
        string message = EventArgsFormatting.FormatEventMessage(
                category: AnsiCodes.Colorize("error", TerminalColor.Red),
                subcategory: e.Subcategory,
                message: e.Message,
                code: AnsiCodes.Colorize(e.Code, TerminalColor.Red),
                file: HighlightFileName(e.File),
                projectFile: e.ProjectFile ?? null,
                lineNumber: e.LineNumber,
                endLineNumber: e.EndLineNumber,
                columnNumber: e.ColumnNumber,
                endColumnNumber: e.EndColumnNumber,
                threadId: e.ThreadId,
                logOutputProperties: null);

        if (buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            project.AddBuildMessage(MessageSeverity.Error, message);
        }
        else
        {
            // It is necessary to display error messages reported by MSBuild, even if it's not tracked in _projects collection.
            RenderImmediateMessage(message);
            _buildHasErrors = true;
        }
    }

    #endregion

    #region Refresher thread implementation

    /// <summary>
    /// The <see cref="_refresher"/> thread proc.
    /// </summary>
    private void ThreadProc()
    {
        // 1_000 / 30 is a poor approx of 30Hz
        while (!_cts.Token.WaitHandle.WaitOne(1_000 / 30))
        {
            lock (_lock)
            {
                DisplayNodes();
            }
        }

        EraseNodes();
    }

    /// <summary>
    /// Render Nodes section.
    /// It shows what all build nodes do.
    /// </summary>
    internal void DisplayNodes()
    {
        NodesFrame newFrame = new NodesFrame(_nodes, width: Terminal.Width, height: Terminal.Height);

        // Do not render delta but clear everything if Terminal width or height have changed.
        if (newFrame.Width != _currentFrame.Width || newFrame.Height != _currentFrame.Height)
        {
            EraseNodes();
        }

        string rendered = newFrame.Render(_currentFrame);

        // Hide the cursor to prevent it from jumping around as we overwrite the live lines.
        Terminal.Write(AnsiCodes.HideCursor);
        try
        {
            Terminal.Write(rendered);
        }
        finally
        {
            Terminal.Write(AnsiCodes.ShowCursor);
        }

        _currentFrame = newFrame;
    }

    /// <summary>
    /// Erases the previously printed live node output.
    /// </summary>
    private void EraseNodes()
    {
        if (_currentFrame.NodesCount == 0)
        {
            return;
        }
        Terminal.WriteLine($"{AnsiCodes.CSI}{_currentFrame.NodesCount + 1}{AnsiCodes.MoveUpToLineStart}");
        Terminal.Write($"{AnsiCodes.CSI}{AnsiCodes.EraseInDisplay}");
        _currentFrame.Clear();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Print a build result summary to the output.
    /// </summary>
    /// <param name="succeeded">True if the build completed with success.</param>
    /// <param name="hasError">True if the build has logged at least one error.</param>
    /// <param name="hasWarning">True if the build has logged at least one warning.</param>
    private string RenderBuildResult(bool succeeded, bool hasError, bool hasWarning)
    {
        if (!succeeded)
        {
            // If the build failed, we print one of three red strings.
            string text = (hasError, hasWarning) switch
            {
                (true, _) => ResourceUtilities.GetResourceString("BuildResult_FailedWithErrors"),
                (false, true) => ResourceUtilities.GetResourceString("BuildResult_FailedWithWarnings"),
                _ => ResourceUtilities.GetResourceString("BuildResult_Failed"),
            };
            return AnsiCodes.Colorize(text, TerminalColor.Red);
        }
        else if (hasWarning)
        {
            return AnsiCodes.Colorize(ResourceUtilities.GetResourceString("BuildResult_SucceededWithWarnings"), TerminalColor.Yellow);
        }
        else
        {
            return AnsiCodes.Colorize(ResourceUtilities.GetResourceString("BuildResult_Succeeded"), TerminalColor.Green);
        }
    }

    /// <summary>
    /// Print a build messages to the output that require special customer's attention.
    /// </summary>
    /// <param name="message">Build message needed to be shown immediately.</param>
    private void RenderImmediateMessage(string message)
    {
        lock (_lock)
        {
            // Calling erase helps to clear the screen before printing the message
            // The immediate output will not overlap with node status reporting
            EraseNodes();
            Terminal.WriteLine(message);
        }
    }

    /// <summary>
    /// Returns the <see cref="_nodes"/> index corresponding to the given <see cref="BuildEventContext"/>.
    /// </summary>
    private int NodeIndexForContext(BuildEventContext context)
    {
        // Node IDs reported by the build are 1-based.
        return context.NodeId - 1;
    }

    /// <summary>
    /// Colorizes the filename part of the given path.
    /// </summary>
    private static string? HighlightFileName(string? path)
    {
        if (path == null)
        {
            return null;
        }

        int index = path.LastIndexOfAny(PathSeparators);
        return index >= 0
            ? $"{path.Substring(0, index + 1)}{AnsiCodes.MakeBold(path.Substring(index + 1))}"
            : path;
    }

    #endregion
}

internal sealed class TerminalLoggerNodeForwardingLogger : IForwardingLogger
{
    public IEventRedirector? BuildEventRedirector { get; set; }
    public int NodeId { get; set; }
    public LoggerVerbosity Verbosity { get => LoggerVerbosity.Diagnostic; set { return; } }
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource, int nodeCount) => Initialize(eventSource);
    public void Initialize(IEventSource eventSource)
    {
        eventSource.BuildStarted += ForwardEventUnconditionally;
        eventSource.BuildFinished += ForwardEventUnconditionally;
        eventSource.ProjectStarted += ForwardEventUnconditionally;
        eventSource.ProjectFinished += ForwardEventUnconditionally;
        eventSource.TargetStarted += ForwardEventUnconditionally;
        eventSource.TargetFinished += ForwardEventUnconditionally;
        eventSource.TaskStarted += TaskStarted;

        eventSource.MessageRaised += MessageRaised;
        eventSource.WarningRaised += ForwardEventUnconditionally;
        eventSource.ErrorRaised += ForwardEventUnconditionally;

        if (eventSource is IEventSource3 eventSource3)
        {
            eventSource3.IncludeTaskInputs();
        }

        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeEvaluationPropertiesAndItems();
        }
    }

    public void ForwardEventUnconditionally(object sender, BuildEventArgs e)
    {
        BuildEventRedirector?.ForwardEvent(e);
    }

    public void TaskStarted(object sender, TaskStartedEventArgs e)
    {
        // MSBuild tasks yield the build node, so forward this to the central node so it can update status
        if (e.TaskName.Equals("MSBuild", StringComparison.OrdinalIgnoreCase))
        {
            BuildEventRedirector?.ForwardEvent(e);
        }
    }

    public void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        if (e.BuildEventContext is null)
        {
            return;
        }

        // SourceRoot additions are used in output reporting, so forward those along
        if (e is TaskParameterEventArgs taskArgs)
        {
            if (taskArgs.Kind == TaskParameterMessageKind.AddItem)
            {
                if (taskArgs.ItemType.Equals("SourceRoot", StringComparison.OrdinalIgnoreCase))
                {
                    BuildEventRedirector?.ForwardEvent(taskArgs);
                }
            }
        }

        // High-priority messages are rendered for each project, so forward those along
        if (e.Message is not null && e.Importance == MessageImportance.High)
        {
            BuildEventRedirector?.ForwardEvent(e);
        }
    }

    public void Shutdown()
    {
    }
}
