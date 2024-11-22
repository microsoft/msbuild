// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Build.Framework.Logging;
using System.Globalization;

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

    private static readonly string[] newLineStrings = { "\r\n", "\n" };

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

    internal const string DoubleIndentation = $"{Indentation}{Indentation}";

    internal const string TripleIndentation = $"{Indentation}{Indentation}{Indentation}";

    internal const TerminalColor TargetFrameworkColor = TerminalColor.Cyan;

    internal Func<StopwatchAbstraction>? CreateStopwatch = null;

    /// <summary>
    /// Name of target that identifies the project cache plugin run has just started.
    /// </summary>
    private const string CachePluginStartTarget = "_CachePluginRunStart";

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
    /// <remarks>
    /// There is no locking around access to this data structure despite it being accessed concurrently by multiple threads.
    /// However, reads and writes to locations in an array is atomic, so locking is not required.
    /// </remarks>
    private NodeStatus?[] _nodes = Array.Empty<NodeStatus>();

    /// <summary>
    /// The timestamp of the <see cref="IEventSource.BuildStarted"/> event.
    /// </summary>
    private DateTime _buildStartTime;

    /// <summary>
    /// The working directory when the build starts, to trim relative output paths.
    /// </summary>
    private readonly string _initialWorkingDirectory = Environment.CurrentDirectory;

    /// <summary>
    /// Number of build errors.
    /// </summary>
    private int _buildErrorsCount;

    /// <summary>
    /// Number of build warnings.
    /// </summary>
    private int _buildWarningsCount;

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
    /// Demonstrates whether there exists at least one project which is a cache plugin project.
    /// </summary>
    private bool _hasUsedCache = false;

    /// <summary>
    /// Whether to show TaskCommandLineEventArgs high-priority messages. 
    /// </summary>
    private bool _showCommandLine = false;

    private uint? _originalConsoleMode;

    /// <summary>
    /// Default constructor, used by the MSBuild logger infra.
    /// </summary>
    public TerminalLogger()
    {
        Terminal = new Terminal();
    }

    public TerminalLogger(LoggerVerbosity verbosity) : this()
    {
        Verbosity = verbosity;
    }

    /// <summary>
    /// Internal constructor accepting a custom <see cref="ITerminal"/> for testing.
    /// </summary>
    internal TerminalLogger(ITerminal terminal)
    {
        Terminal = terminal;
        _manualRefresh = true;
    }

    /// <summary>
    /// Private constructor invoked by static factory.
    /// </summary>
    private TerminalLogger(LoggerVerbosity verbosity, uint? originalConsoleMode) : this()
    {
        Verbosity = verbosity;
        _originalConsoleMode = originalConsoleMode;
    }

    #region INodeLogger implementation

    /// <inheritdoc/>
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

    /// <inheritdoc/>
    public string? Parameters { get; set; } = null;

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
        ParseParameters();

        eventSource.BuildStarted += BuildStarted;
        eventSource.BuildFinished += BuildFinished;
        eventSource.ProjectStarted += ProjectStarted;
        eventSource.ProjectFinished += ProjectFinished;
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.TaskStarted += TaskStarted;
        eventSource.StatusEventRaised += StatusEventRaised;

        eventSource.MessageRaised += MessageRaised;
        eventSource.WarningRaised += WarningRaised;
        eventSource.ErrorRaised += ErrorRaised;

        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeEvaluationPropertiesAndItems();
        }
    }


    /// <summary>
    /// Parses out the logger parameters from the Parameters string.
    /// </summary>
    public void ParseParameters()
    {
        foreach (var parameter in LoggerParametersHelper.ParseParameters(Parameters))
        {
            ApplyParameter(parameter.Item1, parameter.Item2);
        }
    }

    /// <summary>
    /// Apply a terminal logger parameter.
    /// parameterValue may be null, if there is no parameter value.
    /// </summary>
    /// <remark>
    /// If verbosity parameter value is not correct, throws an exception. Other incorrect parameter values are disregarded.
    /// </remark>
    private void ApplyParameter(string parameterName, string? parameterValue)
    {
        ErrorUtilities.VerifyThrowArgumentNull(parameterName, nameof(parameterName));

        switch (parameterName.ToUpperInvariant())
        {
            case "V":
            case "VERBOSITY":
                ApplyVerbosityParameter(parameterValue);
                break;
            case "SHOWCOMMANDLINE":
                TryApplyShowCommandLineParameter(parameterValue);
                break;
        }
    }

    /// <summary>
    /// Apply the verbosity value
    /// </summary>
    private void ApplyVerbosityParameter(string? parameterValue)
    {
        if (parameterValue is not null && LoggerParametersHelper.TryParseVerbosityParameter(parameterValue, out LoggerVerbosity? verbosity))
        {
            Verbosity = (LoggerVerbosity)verbosity;
        }
        else
        {
            string errorCode;
            string helpKeyword;
            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out errorCode, out helpKeyword, "InvalidVerbosity", parameterValue);
            throw new LoggerException(message, null, errorCode, helpKeyword);
        }
    }

    /// <summary>
    /// Apply the show command Line value
    /// </summary>
    private bool TryApplyShowCommandLineParameter(string? parameterValue)
    {
        if (String.IsNullOrEmpty(parameterValue))
        {
            _showCommandLine = true;
        }
        else
        {
            return ConversionUtilities.TryConvertStringToBool(parameterValue, out _showCommandLine);
        }

        return true;
    }


    /// <inheritdoc/>
    public void Shutdown()
    {
        NativeMethodsShared.RestoreConsoleMode(_originalConsoleMode);

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
            if (Verbosity > LoggerVerbosity.Quiet)
            {
                string duration = (e.Timestamp - _buildStartTime).TotalSeconds.ToString("F1");
                string buildResult = RenderBuildResult(e.Succeeded, _buildErrorsCount, _buildWarningsCount);

                Terminal.WriteLine("");
                if (_testRunSummaries.Any())
                {
                    var total = _testRunSummaries.Sum(t => t.Total);
                    var failed = _testRunSummaries.Sum(t => t.Failed);
                    var passed = _testRunSummaries.Sum(t => t.Passed);
                    var skipped = _testRunSummaries.Sum(t => t.Skipped);
                    var testDuration = (_testStartTime != null && _testEndTime != null ? (_testEndTime - _testStartTime).Value.TotalSeconds : 0).ToString("F1");

                    var colorizeFailed = failed > 0;
                    var colorizePassed = passed > 0 && _buildErrorsCount == 0 && failed == 0;
                    var colorizeSkipped = skipped > 0 && skipped == total && _buildErrorsCount == 0 && failed == 0;

                    string summaryAndTotalText = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary_BannerAndTotal", total);
                    string failedText = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary_Failed", failed);
                    string passedText = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary_Succeeded", passed);
                    string skippedText = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary_Skipped", skipped);
                    string durationText = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("TestSummary_Duration", testDuration);

                    failedText = colorizeFailed ? AnsiCodes.Colorize(failedText.ToString(), TerminalColor.Red) : failedText;
                    passedText = colorizePassed ? AnsiCodes.Colorize(passedText.ToString(), TerminalColor.Green) : passedText;
                    skippedText = colorizeSkipped ? AnsiCodes.Colorize(skippedText.ToString(), TerminalColor.Yellow) : skippedText;

                    Terminal.WriteLine(string.Join(CultureInfo.CurrentCulture.TextInfo.ListSeparator + " ", summaryAndTotalText, failedText, passedText, skippedText, durationText));
                }

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
        _buildErrorsCount = 0;
        _buildWarningsCount = 0;
        _restoreFailed = false;
        _testStartTime = null;
        _testEndTime = null;
    }

    private void StatusEventRaised(object sender, BuildStatusEventArgs e)
    {
        if (e is BuildCanceledEventArgs buildCanceledEventArgs)
        {
            RenderImmediateMessage(e.Message!);
        }
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
            _projects[c] = new(targetFramework, CreateStopwatch?.Invoke());

            // First ever restore in the build is starting.
            if (e.TargetNames == "Restore" && !_restoreFinished)
            {
                _restoreContext = c;
                int nodeIndex = NodeIndexForContext(buildEventContext);
                _nodes[nodeIndex] = new NodeStatus(e.ProjectFile!, null, "Restore", _projects[c].Stopwatch);
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

        // Continue execution and add project summary to the static part of the Console only if verbosity is higher than Quiet.
        if (Verbosity <= LoggerVerbosity.Quiet)
        {
            return;
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
                    ReadOnlyMemory<char>? outputPath = project.OutputPath;

                    string projectFile = e.ProjectFile is not null ?
                        Path.GetFileNameWithoutExtension(e.ProjectFile) :
                        string.Empty;

                    // Build result. One of 'failed', 'succeeded with warnings', or 'succeeded' depending on the build result and diagnostic messages
                    // reported during build.
                    int countErrors = project.BuildMessages?.Count(m => m.Severity == MessageSeverity.Error) ?? 0;
                    int countWarnings = project.BuildMessages?.Count(m => m.Severity == MessageSeverity.Warning) ?? 0;

                    string buildResult = RenderBuildResult(e.Succeeded, countErrors, countWarnings);

                    bool haveErrors = countErrors > 0;
                    bool haveWarnings = countWarnings > 0;

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
                        if (outputPath is not null)
                        {
                            ReadOnlySpan<char> outputPathSpan = outputPath.Value.Span;
                            ReadOnlySpan<char> url = outputPathSpan;
                            try
                            {
                                // If possible, make the link point to the containing directory of the output.
                                url = Path.GetDirectoryName(url);
                            }
                            catch
                            {
                                // Ignore any GetDirectoryName exceptions.
                            }

                            // Generates file:// schema url string which is better handled by various Terminal clients than raw folder name.
                            string urlString = url.ToString();
                            if (Uri.TryCreate(urlString, UriKind.Absolute, out Uri? uri))
                            {
                                // url.ToString() un-escapes the URL which is needed for our case file://
                                // but not valid for http://
                                urlString = uri.ToString();
                            }

                            // If the output path is under the initial working directory, make the console output relative to that to save space.
                            if (outputPathSpan.StartsWith(_initialWorkingDirectory.AsSpan(), FileUtilities.PathComparison))
                            {
                                if (outputPathSpan.Length > _initialWorkingDirectory.Length
                                    && (outputPathSpan[_initialWorkingDirectory.Length] == Path.DirectorySeparatorChar
                                        || outputPathSpan[_initialWorkingDirectory.Length] == Path.AltDirectorySeparatorChar))
                                {
                                    outputPathSpan = outputPathSpan.Slice(_initialWorkingDirectory.Length + 1);
                                }
                            }

                            Terminal.WriteLine(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("ProjectFinished_OutputPath",
                                $"{AnsiCodes.LinkPrefix}{urlString}{AnsiCodes.LinkInfix}{outputPathSpan.ToString()}{AnsiCodes.LinkSuffix}"));
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
                            Terminal.WriteLine($"{DoubleIndentation}{buildMessage.Message}");
                        }
                    }

                    _buildErrorsCount += countErrors;
                    _buildWarningsCount += countWarnings;

                    DisplayNodes();
                }
                finally
                {
                    Terminal.EndUpdate();
                }
            }
        }
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

            string targetName = e.TargetName;
            if (targetName == CachePluginStartTarget)
            {
                project.IsCachePluginProject = true;
                _hasUsedCache = true;
            }

            if (targetName == _testStartTarget)
            {
                targetName = "Testing";

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
        int nodeIndex = NodeIndexForContext(buildEventContext);
        _nodes[nodeIndex] = nodeStatus;
    }

    /// <summary>
    /// The <see cref="IEventSource.TargetFinished"/> callback. Unused.
    /// </summary>
    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        // For cache plugin projects which result in a cache hit, ensure the output path is set
        // to the item spec corresponding to the GetTargetPath target upon completion.
        var buildEventContext = e.BuildEventContext;
        if (_restoreContext is null
            && buildEventContext is not null
            && _hasUsedCache
            && e.TargetName == "GetTargetPath"
            && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            if (project.IsCachePluginProject)
            {
                foreach (ITaskItem output in e.TargetOutputs)
                {
                    project.OutputPath = output.ItemSpec.AsMemory();
                    break;
                }
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
                    project!.OutputPath = outputPath;
                    return;
                }
            }

            if (Verbosity > LoggerVerbosity.Quiet)
            {
                // Show immediate messages to the user.
                if (IsImmediateMessage(message))
                {
                    RenderImmediateMessage(message);
                    return;
                }
                if (e.Code == "NETSDK1057" && !_loggedPreviewMessage)
                {
                    // The SDK will log the high-pri "not-a-warning" message NETSDK1057
                    // when it's a preview version up to MaxCPUCount times, but that's
                    // an implementation detail--the user cares about at most one.

                    RenderImmediateMessage(message);
                    _loggedPreviewMessage = true;
                    return;
                }
            }

            if (hasProject && project!.IsTestProject)
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
                                // Collect test run summary.
                                if (Verbosity > LoggerVerbosity.Quiet)
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
                                }

                                break;
                            }

                        case "TLTESTOUTPUT":
                            {
                                if (e.Message != null && Verbosity > LoggerVerbosity.Quiet)
                                {
                                    RenderImmediateMessage(e.Message);
                                }
                                break;
                            }
                    }
                    return;
                }
            }

            if (Verbosity > LoggerVerbosity.Normal)
            {
                if (e is TaskCommandLineEventArgs && !_showCommandLine)
                {
                    return;
                }

                if (hasProject)
                {
                    project!.AddBuildMessage(MessageSeverity.Message, message);
                }
                else
                {
                    // Display messages reported by MSBuild, even if it's not tracked in _projects collection.
                    RenderImmediateMessage(message);
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.WarningRaised"/> callback.
    /// </summary>
    private void WarningRaised(object sender, BuildWarningEventArgs e)
    {
        BuildEventContext? buildEventContext = e.BuildEventContext;

        if (buildEventContext is not null
            && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project)
            && Verbosity > LoggerVerbosity.Quiet)
        {
            if ((!String.IsNullOrEmpty(e.Message) && IsImmediateMessage(e.Message!)) ||
                IsImmediateWarning(e.Code))
            {
                RenderImmediateMessage(FormatWarningMessage(e, Indentation));
            }

            project.AddBuildMessage(MessageSeverity.Warning, FormatWarningMessage(e, TripleIndentation));
        }
        else
        {
            // It is necessary to display warning messages reported by MSBuild, even if it's not tracked in _projects collection or the verbosity is Quiet.
            RenderImmediateMessage(FormatWarningMessage(e, Indentation));
            _buildWarningsCount++;
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


    private bool IsImmediateWarning(string code) => code == "MSB3026";

    /// <summary>
    /// The <see cref="IEventSource.ErrorRaised"/> callback.
    /// </summary>
    private void ErrorRaised(object sender, BuildErrorEventArgs e)
    {
        BuildEventContext? buildEventContext = e.BuildEventContext;

        if (buildEventContext is not null
            && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project)
            && Verbosity > LoggerVerbosity.Quiet)
        {
            project.AddBuildMessage(MessageSeverity.Error, FormatErrorMessage(e, TripleIndentation));
        }
        else
        {
            // It is necessary to display error messages reported by MSBuild, even if it's not tracked in _projects collection or the verbosity is Quiet.
            RenderImmediateMessage(FormatErrorMessage(e, Indentation));
            _buildErrorsCount++;
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
    /// Creates a Terminal logger or Console logger based on the environment.
    /// This method is called by reflection from dotnet. Do not modify the name or parameters without adapting the SDK.
    /// </summary>
    public static ILogger CreateTerminalOrConsoleLogger(LoggerVerbosity verbosity)
    {
        bool isDisabled = (Environment.GetEnvironmentVariable("MSBUILDTERMINALLOGGER") ?? string.Empty).Equals("off", StringComparison.InvariantCultureIgnoreCase);
        (bool supportsAnsi, bool outputIsScreen, uint? originalConsoleMode) = NativeMethodsShared.QueryIsScreenAndTryEnableAnsiColorCodes();

        if (isDisabled || !supportsAnsi || !outputIsScreen)
        {
            NativeMethodsShared.RestoreConsoleMode(originalConsoleMode);
            return new ConsoleLogger(verbosity);
        }

        return new TerminalLogger(verbosity, originalConsoleMode);
    }

    /// <summary>
    /// Print a build result summary to the output.
    /// </summary>
    /// <param name="succeeded">True if the build completed with success.</param>
    /// <param name="hasError">True if the build has logged at least one error.</param>
    /// <param name="hasWarning">True if the build has logged at least one warning.</param>
    private string RenderBuildResult(bool succeeded, int countErrors, int countWarnings)
    {
        if (!succeeded)
        {
            // If the build failed, we print one of three red strings.
            string text = (countErrors > 0, countWarnings > 0) switch
            {
                (true, true) => ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("BuildResult_FailedWithErrorsAndWarnings", countErrors, countWarnings),
                (true, _) => ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("BuildResult_FailedWithErrors", countErrors),
                (false, true) => ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("BuildResult_FailedWithWarnings", countWarnings),
                _ => ResourceUtilities.GetResourceString("BuildResult_Failed"),
            };
            return AnsiCodes.Colorize(text, TerminalColor.Red);
        }
        else if (countWarnings > 0)
        {
            return AnsiCodes.Colorize(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("BuildResult_SucceededWithWarnings", countWarnings), TerminalColor.Yellow);
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

    private string FormatWarningMessage(BuildWarningEventArgs e, string indent)
    {
        return FormatEventMessage(
                category: AnsiCodes.Colorize("warning", TerminalColor.Yellow),
                subcategory: e.Subcategory,
                message: e.Message,
                code: AnsiCodes.Colorize(e.Code, TerminalColor.Yellow),
                file: HighlightFileName(e.File),
                lineNumber: e.LineNumber,
                endLineNumber: e.EndLineNumber,
                columnNumber: e.ColumnNumber,
                endColumnNumber: e.EndColumnNumber,
                indent);
    }

    private string FormatErrorMessage(BuildErrorEventArgs e, string indent)
    {
        return FormatEventMessage(
                category: AnsiCodes.Colorize("error", TerminalColor.Red),
                subcategory: e.Subcategory,
                message: e.Message,
                code: AnsiCodes.Colorize(e.Code, TerminalColor.Red),
                file: HighlightFileName(e.File),
                lineNumber: e.LineNumber,
                endLineNumber: e.EndLineNumber,
                columnNumber: e.ColumnNumber,
                endColumnNumber: e.EndColumnNumber,
                indent);
    }

    private string FormatEventMessage(
            string category,
            string subcategory,
            string? message,
            string code,
            string? file,
            int lineNumber,
            int endLineNumber,
            int columnNumber,
            int endColumnNumber,
            string indent)
    {
        message ??= string.Empty;
        StringBuilder builder = new(128);

        if (string.IsNullOrEmpty(file))
        {
            builder.Append("MSBUILD : ");    // Should not be localized.
        }
        else
        {
            builder.Append(file);

            if (lineNumber == 0)
            {
                builder.Append(" : ");
            }
            else
            {
                if (columnNumber == 0)
                {
                    builder.Append(endLineNumber == 0 ?
                        $"({lineNumber}): " :
                        $"({lineNumber}-{endLineNumber}): ");
                }
                else
                {
                    if (endLineNumber == 0)
                    {
                        builder.Append(endColumnNumber == 0 ?
                            $"({lineNumber},{columnNumber}): " :
                            $"({lineNumber},{columnNumber}-{endColumnNumber}): ");
                    }
                    else
                    {
                        builder.Append(endColumnNumber == 0 ?
                            $"({lineNumber}-{endLineNumber},{columnNumber}): " :
                            $"({lineNumber},{columnNumber},{endLineNumber},{endColumnNumber}): ");
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(subcategory))
        {
            builder.Append(subcategory);
            builder.Append(' ');
        }

        builder.Append($"{category} {code}: ");

        // render multi-line message in a special way
        if (message.IndexOf('\n') >= 0)
        {
            // Place the multiline message under the project in case of minimal and higher verbosity.
            string[] lines = message.Split(newLineStrings, StringSplitOptions.None);

            foreach (string line in lines)
            {
                if (indent.Length + line.Length > Terminal.Width) // custom wrapping with indentation
                {
                    WrapText(builder, line, Terminal.Width, indent);
                }
                else
                {
                    builder.AppendLine();
                    builder.Append(indent);
                    builder.Append(line);
                }
            }
        }
        else
        {
            builder.Append(message);
        }

        return builder.ToString();
    }

    private static void WrapText(StringBuilder sb, string text, int maxLength, string indent)
    {
        int start = 0;
        while (start < text.Length)
        {
            int length = Math.Min(maxLength - indent.Length, text.Length - start);
            sb.AppendLine();
            sb.Append(indent);
            sb.Append(text.AsSpan().Slice(start, length));

            start += length;
        }
    }

    #endregion
}
