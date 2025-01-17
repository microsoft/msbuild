// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BuildCheck.Infrastructure;
using Microsoft.Build.Construction;
using Microsoft.Build.Experimental.BuildCheck.Acquisition;
using Microsoft.Build.Experimental.BuildCheck.Checks;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck.Infrastructure;

internal delegate Check CheckFactory();
internal delegate CheckWrapper CheckWrapperFactory(ConfigurationContext configurationContext);

/// <summary>
/// The central manager for the BuildCheck - this is the integration point with MSBuild infrastructure.
/// </summary>
internal sealed class BuildCheckManagerProvider : IBuildCheckManagerProvider
{
    private IBuildCheckManager? _instance;

    public IBuildCheckManager Instance => _instance ?? new NullBuildCheckManager();

    public IBuildEngineDataRouter BuildEngineDataRouter => (IBuildEngineDataRouter)Instance;

    internal static IBuildComponent CreateComponent(BuildComponentType type)
    {
        ErrorUtilities.VerifyThrow(type == BuildComponentType.BuildCheckManagerProvider, "Cannot create components of type {0}", type);
        return new BuildCheckManagerProvider();
    }

    public void InitializeComponent(IBuildComponentHost host)
    {
        ErrorUtilities.VerifyThrow(host != null, "BuildComponentHost was null");

        if (_instance == null)
        {
            if (host!.BuildParameters.IsBuildCheckEnabled)
            {
                _instance = new BuildCheckManager();
            }
            else
            {
                _instance = new NullBuildCheckManager();
            }
        }
    }

    public void ShutdownComponent()
    {
        _instance?.Shutdown();
        _instance = null;
    }

    internal sealed class BuildCheckManager : IBuildCheckManager, IBuildEngineDataRouter, IResultReporter
    {
        private readonly TracingReporter _tracingReporter = new TracingReporter();
        private readonly IConfigurationProvider _configurationProvider = new ConfigurationProvider();
        private readonly BuildCheckCentralContext _buildCheckCentralContext;
        private readonly List<CheckFactoryContext> _checkRegistry;
        private readonly bool[] _enabledDataSources = new bool[(int)BuildCheckDataSource.ValuesCount];
        private readonly BuildEventsProcessor _buildEventsProcessor;
        private readonly IBuildCheckAcquisitionModule _acquisitionModule;

        internal BuildCheckManager()
        {
            _checkRegistry = new List<CheckFactoryContext>();
            _acquisitionModule = new BuildCheckAcquisitionModule();
            _buildCheckCentralContext = new(_configurationProvider, RemoveChecksAfterExecutedActions);
            _buildEventsProcessor = new(_buildCheckCentralContext);
        }

        private bool IsInProcNode => _enabledDataSources[(int)BuildCheckDataSource.EventArgs] &&
                                     _enabledDataSources[(int)BuildCheckDataSource.BuildExecution];

        /// <summary>
        /// Notifies the manager that the data source will be used -
        ///   so it should register the built-in checks for the source if it hasn't been done yet.
        /// </summary>
        /// <param name="buildCheckDataSource"></param>
        public void SetDataSource(BuildCheckDataSource buildCheckDataSource)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!_enabledDataSources[(int)buildCheckDataSource])
            {
                _enabledDataSources[(int)buildCheckDataSource] = true;
                RegisterBuiltInChecks(buildCheckDataSource);
            }
            stopwatch.Stop();
            _tracingReporter.AddSetDataSourceStats(stopwatch.Elapsed);
        }

        public void ProcessCheckAcquisition(
            CheckAcquisitionData acquisitionData,
            ICheckContext checkContext)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (IsInProcNode)
            {
                var checksFactories = _acquisitionModule.CreateCheckFactories(acquisitionData, checkContext);
                if (checksFactories.Count != 0)
                {
                    RegisterCustomCheck(acquisitionData.ProjectPath, BuildCheckDataSource.EventArgs, checksFactories, checkContext);
                }
                else
                {
                    checkContext.DispatchAsComment(MessageImportance.Normal, "CustomCheckFailedAcquisition", acquisitionData.AssemblyPath);
                }
            }
            else
            {
                BuildCheckAcquisitionEventArgs eventArgs = acquisitionData.ToBuildEventArgs();
                eventArgs.BuildEventContext = checkContext.BuildEventContext!;

                checkContext.DispatchBuildEvent(eventArgs);
            }

            stopwatch.Stop();
            _tracingReporter.AddAcquisitionStats(stopwatch.Elapsed);
        }

        private static T Construct<T>() where T : new() => new();

        /// <summary>
        /// The builtin check factory definition
        /// </summary>
        /// <param name="RuleIds">The rule ids that the check is able to emit.</param>
        /// <param name="DefaultEnablement">Is it enabled by default?</param>
        /// <param name="Factory">Factory method to create the check.</param>
        internal readonly record struct BuiltInCheckFactory(
            string[] RuleIds,
            bool DefaultEnablement,
            CheckFactory Factory);

        private static readonly BuiltInCheckFactory[][] s_builtInFactoriesPerDataSource =
        [

            // BuildCheckDataSource.EventArgs
            [
                new BuiltInCheckFactory([SharedOutputPathCheck.SupportedRule.Id], SharedOutputPathCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<SharedOutputPathCheck>),
                new BuiltInCheckFactory([PreferProjectReferenceCheck.SupportedRule.Id], PreferProjectReferenceCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<PreferProjectReferenceCheck>),
                new BuiltInCheckFactory([CopyAlwaysCheck.SupportedRule.Id], CopyAlwaysCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<CopyAlwaysCheck>),
                new BuiltInCheckFactory([DoubleWritesCheck.SupportedRule.Id], DoubleWritesCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<DoubleWritesCheck>),
                new BuiltInCheckFactory([NoEnvironmentVariablePropertyCheck.SupportedRule.Id], NoEnvironmentVariablePropertyCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<NoEnvironmentVariablePropertyCheck>),
                new BuiltInCheckFactory([EmbeddedResourceCheck.SupportedRule.Id], EmbeddedResourceCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<EmbeddedResourceCheck>),
                new BuiltInCheckFactory([TargetFrameworkConfusionCheck.SupportedRule.Id], TargetFrameworkConfusionCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<TargetFrameworkConfusionCheck>),
                new BuiltInCheckFactory([TargetFrameworkUnexpectedCheck.SupportedRule.Id], TargetFrameworkUnexpectedCheck.SupportedRule.DefaultConfiguration.IsEnabled ?? false, Construct<TargetFrameworkUnexpectedCheck>),
            ],

            // BuildCheckDataSource.Execution
            [
                new BuiltInCheckFactory(PropertiesUsageCheck.SupportedRulesList.Select(r => r.Id).ToArray(),
                    PropertiesUsageCheck.SupportedRulesList.Any(r => r.DefaultConfiguration.IsEnabled ?? false),
                    Construct<PropertiesUsageCheck>)
            ]
        ];

        /// <summary>
        /// For tests only. TODO: Remove when check acquisition is done.
        /// </summary>
        internal static BuiltInCheckFactory[][]? s_testFactoriesPerDataSource;

        private void RegisterBuiltInChecks(BuildCheckDataSource buildCheckDataSource)
        {
            _checkRegistry.AddRange(
                s_builtInFactoriesPerDataSource[(int)buildCheckDataSource]
                    .Select(v => new CheckFactoryContext(v.Factory, v.RuleIds, v.DefaultEnablement)));

            if (s_testFactoriesPerDataSource is not null)
            {
                _checkRegistry.AddRange(
                    s_testFactoriesPerDataSource[(int)buildCheckDataSource]
                        .Select(v => new CheckFactoryContext(v.Factory, v.RuleIds, v.DefaultEnablement)));
            }
        }

        /// <summary>
        /// To be used by acquisition module
        /// Registers the custom check, the construction of check is needed during registration.
        /// </summary>
        /// <param name="projectPath">The project path is used for the correct .editorconfig resolution.</param>
        /// <param name="buildCheckDataSource">Represents different data sources used in build check operations.</param>
        /// <param name="factories">A collection of build check factories for rules instantiation.</param>
        /// <param name="checkContext">The logging context of the build event.</param>
        internal void RegisterCustomCheck(
            string projectPath,
            BuildCheckDataSource buildCheckDataSource,
            IEnumerable<CheckFactory> factories,
            ICheckContext checkContext)
        {
            if (_enabledDataSources[(int)buildCheckDataSource])
            {
                List<CheckFactoryContext> invalidChecksToRemove = new();
                foreach (var factory in factories)
                {
                    var instance = factory();
                    if (instance != null && instance.SupportedRules.Any())
                    {
                        var checkFactoryContext = new CheckFactoryContext(
                            factory,
                            instance.SupportedRules.Select(r => r.Id).ToArray(),
                            instance.SupportedRules.Any(r => r.DefaultConfiguration.IsEnabled == true));

                        if (checkFactoryContext != null)
                        {
                            _checkRegistry.Add(checkFactoryContext);
                            try
                            {
                                SetupSingleCheck(checkFactoryContext, projectPath);
                                checkContext.DispatchAsComment(MessageImportance.Normal, "CustomCheckSuccessfulAcquisition", instance.FriendlyName);
                            }
                            catch (BuildCheckConfigurationException e)
                            {
                                checkContext.DispatchAsWarningFromText(
                                    null,
                                    null,
                                    null,
                                    new BuildEventFileInfo(projectPath),
                                    e.Message);
                                invalidChecksToRemove.Add(checkFactoryContext);
                            }
                        }
                    }
                    RemoveInvalidChecks(invalidChecksToRemove, checkContext);
                }
            }
        }

        private void SetupSingleCheck(CheckFactoryContext checkFactoryContext, string projectFullPath)
        {
            // For custom checks - it should run only on projects where referenced
            // (otherwise error out - https://github.com/orgs/dotnet/projects/373/views/1?pane=issue&itemId=57849480)
            // on others it should work similarly as disabling them.
            // Disabled check should not only post-filter results - it shouldn't even see the data
            CheckWrapper wrapper;
            CheckConfigurationEffective[] configurations;
            if (checkFactoryContext.MaterializedCheck == null)
            {
                CheckConfiguration[] userEditorConfigs =
                    _configurationProvider.GetUserConfigurations(projectFullPath, checkFactoryContext.RuleIds);

                if (userEditorConfigs.All(c => !(c.IsEnabled ?? checkFactoryContext.IsEnabledByDefault)))
                {
                    // the check was not yet instantiated nor mounted - so nothing to do here now.
                    return;
                }

                CustomConfigurationData[] customConfigData =
                    _configurationProvider.GetCustomConfigurations(projectFullPath, checkFactoryContext.RuleIds);

                Check uninitializedCheck = checkFactoryContext.Factory();
                configurations = _configurationProvider.GetMergedConfigurations(userEditorConfigs, uninitializedCheck);

                ConfigurationContext configurationContext = ConfigurationContext.FromDataEnumeration(customConfigData, configurations);

                wrapper = checkFactoryContext.Initialize(uninitializedCheck, this, configurationContext);
                checkFactoryContext.MaterializedCheck = wrapper;
                Check check = wrapper.Check;

                // This is to facilitate possible perf improvement for custom checks - as we might want to
                //  avoid loading the assembly and type just to check if it's supported.
                // If we expose a way to declare the enablement status and rule ids during registration (e.g. via
                //  optional arguments of the intrinsic property function) - we can then avoid loading it.
                // But once loaded - we should verify that the declared enablement status and rule ids match the actual ones.
                if (
                    check.SupportedRules.Count != checkFactoryContext.RuleIds.Length
                    ||
                    !check.SupportedRules.Select(r => r.Id)
                        .SequenceEqual(checkFactoryContext.RuleIds, StringComparer.CurrentCultureIgnoreCase)
                )
                {
                    throw new BuildCheckConfigurationException(
                        $"The check '{check.FriendlyName}' exposes rules '{check.SupportedRules.Select(r => r.Id).ToCsvString()}', but different rules were declared during registration: '{checkFactoryContext.RuleIds.ToCsvString()}'");
                }

                // technically all checks rules could be disabled, but that would mean
                // that the provided 'IsEnabledByDefault' value wasn't correct - the only
                // price to be paid in that case is slight performance cost.

                // Create the wrapper and register to central context
                wrapper.StartNewProject(projectFullPath, configurations, userEditorConfigs);
                var wrappedContext = new CheckRegistrationContext(wrapper, _buildCheckCentralContext);
                try
                {
                    check.RegisterActions(wrappedContext);
                }
                catch (Exception e)
                {
                    string message = $"The check '{check.FriendlyName}' failed to register actions with the following message: '{e.Message}'";
                    throw new BuildCheckConfigurationException(message, e);
                }
            }
            else
            {
                wrapper = checkFactoryContext.MaterializedCheck;

                CheckConfiguration[] userEditorConfigs =
                    _configurationProvider.GetUserConfigurations(projectFullPath, checkFactoryContext.RuleIds);
                configurations = _configurationProvider.GetMergedConfigurations(userEditorConfigs, wrapper.Check);

                _configurationProvider.CheckCustomConfigurationDataValidity(projectFullPath,
                    checkFactoryContext.RuleIds[0]);

                // Update the wrapper
                wrapper.StartNewProject(projectFullPath, configurations, userEditorConfigs);
            }
        }

        private void SetupChecksForNewProject(string projectFullPath, ICheckContext checkContext)
        {
            // Only add checks here
            // On an execution node - we might remove and dispose the checks once project is done

            // If it's already constructed - just control the custom settings do not differ
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<CheckFactoryContext> invalidChecksToRemove = new();
            foreach (CheckFactoryContext checkFactoryContext in _checkRegistry)
            {
                try
                {
                    SetupSingleCheck(checkFactoryContext, projectFullPath);
                }
                catch (BuildCheckConfigurationException e)
                {
                    checkContext.DispatchAsWarningFromText(
                        null,
                        null,
                        null,
                        new BuildEventFileInfo(projectFullPath),
                        e.Message);
                    invalidChecksToRemove.Add(checkFactoryContext);
                }
            }

            RemoveInvalidChecks(invalidChecksToRemove, checkContext);

            stopwatch.Stop();
            _tracingReporter.AddNewProjectStats(stopwatch.Elapsed);
        }

        private void RemoveInvalidChecks(List<CheckFactoryContext> checksToRemove, ICheckContext checkContext)
        {
            foreach (var checkToRemove in checksToRemove)
            {
                checkContext.DispatchAsCommentFromText(MessageImportance.High, $"Dismounting check '{checkToRemove.FriendlyName}'");
                RemoveCheck(checkToRemove);
            }
        }

        public void RemoveChecksAfterExecutedActions(List<CheckWrapper>? checksToRemove, ICheckContext checkContext)
        {
            if (checksToRemove is not null)
            {
                foreach (CheckWrapper check in checksToRemove)
                {
                    var checkFactory = _checkRegistry.Find(c => c.MaterializedCheck == check);
                    if (checkFactory is not null)
                    {
                        checkContext.DispatchAsCommentFromText(MessageImportance.High, $"Dismounting check '{check.Check.FriendlyName}'. The check has thrown an unhandled exception while executing registered actions.");
                        RemoveCheck(checkFactory);
                    }
                }
            }

            foreach (var throttledCheck in _checkRegistry.FindAll(c => c.MaterializedCheck?.IsThrottled ?? false))
            {
                checkContext.DispatchAsCommentFromText(MessageImportance.Normal, $"Dismounting check '{throttledCheck.FriendlyName}'. The check has exceeded the maximum number of results allowed. Any additional results will not be displayed.");
                RemoveCheck(throttledCheck);
            }
        }

        private void RemoveCheck(CheckFactoryContext checkToRemove)
        {
            _checkRegistry.Remove(checkToRemove);

            if (checkToRemove.MaterializedCheck is not null)
            {
                _buildCheckCentralContext.DeregisterCheck(checkToRemove.MaterializedCheck);
                _ruleTelemetryData.AddRange(checkToRemove.MaterializedCheck.GetRuleTelemetryData());
                checkToRemove.MaterializedCheck.Check.Dispose();
            }
        }

        public void ProcessEvaluationFinishedEventArgs(
            ICheckContext checkContext,
            ProjectEvaluationFinishedEventArgs evaluationFinishedEventArgs)
        {
            Dictionary<string, string>? propertiesLookup = null;

            // The FileClassifier is normally initialized by executing build requests.
            // However, if we are running in a main node that has no execution nodes - we need to initialize it here (from events).
            if (!IsInProcNode)
            {
                propertiesLookup =
                    BuildEventsProcessor.ExtractEvaluatedPropertiesLookup(evaluationFinishedEventArgs);
                Func<string, string?> getPropertyValue = p =>
                    propertiesLookup.TryGetValue(p, out string? value) ? value : null;

                FileClassifier.Shared.RegisterFrameworkLocations(getPropertyValue);
                FileClassifier.Shared.RegisterKnownImmutableLocations(getPropertyValue);
            }

            // run it here to avoid the missed imports that can be reported before the checks registration
            if (_deferredProjectEvalIdToImportedProjects.TryGetValue(checkContext.BuildEventContext.EvaluationId, out HashSet<string>? importedProjects))
            {
                if (importedProjects != null && TryGetProjectFullPath(checkContext.BuildEventContext, out string projectPath))
                {
                    foreach (string importedProject in importedProjects)
                    {
                        _buildEventsProcessor.ProcessProjectImportedEventArgs(checkContext, projectPath,
                            importedProject);
                    }
                }
            }

            _buildEventsProcessor
                .ProcessEvaluationFinishedEventArgs(checkContext, evaluationFinishedEventArgs, propertiesLookup);
        }

        public void ProcessEnvironmentVariableReadEventArgs(ICheckContext checkContext, EnvironmentVariableReadEventArgs projectEvaluationEventArgs)
        {
            if (projectEvaluationEventArgs is EnvironmentVariableReadEventArgs evr)
            {
                if (TryGetProjectFullPath(checkContext.BuildEventContext, out string projectPath))
                {
                    _buildEventsProcessor.ProcessEnvironmentVariableReadEventArgs(
                        checkContext,
                        projectPath,
                        evr.EnvironmentVariableName,
                        evr.Message ?? string.Empty,
                        ElementLocation.Create(evr.File, evr.LineNumber, evr.ColumnNumber));
                }
            }
        }

        public void ProcessProjectImportedEventArgs(ICheckContext checkContext, ProjectImportedEventArgs projectImportedEventArgs)
        {
            if (string.IsNullOrEmpty(projectImportedEventArgs.ImportedProjectFile))
            {
                return;
            }

            PropagateImport(checkContext.BuildEventContext.EvaluationId, projectImportedEventArgs.ProjectFile, projectImportedEventArgs.ImportedProjectFile);
        }

        public void ProcessTaskStartedEventArgs(
            ICheckContext checkContext,
            TaskStartedEventArgs taskStartedEventArgs)
            => _buildEventsProcessor
                .ProcessTaskStartedEventArgs(checkContext, taskStartedEventArgs);

        public void ProcessBuildFinished(ICheckContext checkContext)
            => _buildEventsProcessor.ProcessBuildDone(checkContext);

        public void ProcessTaskFinishedEventArgs(
            ICheckContext checkContext,
            TaskFinishedEventArgs taskFinishedEventArgs)
            => _buildEventsProcessor
                .ProcessTaskFinishedEventArgs(checkContext, taskFinishedEventArgs);

        public void ProcessTaskParameterEventArgs(
            ICheckContext checkContext,
            TaskParameterEventArgs taskParameterEventArgs)
            => _buildEventsProcessor
                .ProcessTaskParameterEventArgs(checkContext, taskParameterEventArgs);

        private readonly List<BuildCheckRuleTelemetryData> _ruleTelemetryData = [];

        public BuildCheckTracingData CreateCheckTracingStats()
        {
            foreach (CheckFactoryContext checkFactoryContext in _checkRegistry)
            {
                if (checkFactoryContext.MaterializedCheck != null)
                {
                    _ruleTelemetryData.AddRange(checkFactoryContext.MaterializedCheck.GetRuleTelemetryData());
                }
            }

            return new BuildCheckTracingData(_ruleTelemetryData, _tracingReporter.GetInfrastructureTracingStats());
        }

        public void FinalizeProcessing(LoggingContext loggingContext)
        {
            if (IsInProcNode)
            {
                // We do not want to send tracing stats from in-proc node
                return;
            }

            var checkEventStats = CreateCheckTracingStats();

            BuildCheckTracingEventArgs checkEventArg =
                new(checkEventStats) { BuildEventContext = loggingContext.BuildEventContext };
            loggingContext.LogBuildEvent(checkEventArg);
        }

        private readonly ConcurrentDictionary<int, string> _projectsByInstanceId = new();
        private readonly ConcurrentDictionary<int, string> _projectsByEvaluationId = new();
        // We are receiving project imported data only from the logger events - hence always in a single threaded context
        //  (https://github.com/dotnet/msbuild/blob/main/documentation/wiki/Logging-Internals.md)
        private readonly Dictionary<int, HashSet<string>> _deferredProjectEvalIdToImportedProjects = new();

        /// <summary>
        /// This method fetches the project full path from the context id.
        /// This is needed because the full path is needed for configuration and later for fetching configured checks
        ///  (future version might optimize by using the ProjectContextId directly for fetching the checks).
        /// </summary>
        /// <param name="buildEventContext"></param>
        /// <param name="projectFullPath"></param>
        /// <returns></returns>
        private bool TryGetProjectFullPath(BuildEventContext buildEventContext, out string projectFullPath)
        {
            if (buildEventContext.EvaluationId >= 0)
            {
                if (_projectsByEvaluationId.TryGetValue(buildEventContext.EvaluationId, out string? val))
                {
                    projectFullPath = val;
                    return true;
                }
            }
            else if (buildEventContext.ProjectInstanceId >= 0)
            {
                if (_projectsByInstanceId.TryGetValue(buildEventContext.ProjectInstanceId, out string? val))
                {
                    projectFullPath = val;
                    return true;
                }
            }
            else if (_projectsByInstanceId.Count == 1)
            {
                projectFullPath = _projectsByInstanceId.FirstOrDefault().Value;
                // This is for a rare possibility of a race where other thread removed the item (between the if check and fetch here).
                // We currently do not support multiple projects in parallel in a single node anyway.
                if (!string.IsNullOrEmpty(projectFullPath))
                {
                    return true;
                }
            }
            else if (_projectsByEvaluationId.Count == 1)
            {
                projectFullPath = _projectsByEvaluationId.FirstOrDefault().Value;
                if (!string.IsNullOrEmpty(projectFullPath))
                {
                    return true;
                }
            }

            projectFullPath = string.Empty;
            return false;
        }

        public void ProjectFirstEncountered(
            BuildCheckDataSource buildCheckDataSource,
            ICheckContext checkContext,
            string projectFullPath)
        {
            if (buildCheckDataSource == BuildCheckDataSource.EventArgs && IsInProcNode)
            {
                // Skipping this event - as it was already handled by the in-proc node.
                // This is because in-proc node has the BuildEventArgs source and check source
                //  both in a single manager. The project started is first encountered by the execution before the EventArg is sent
                return;
            }

            SetupChecksForNewProject(projectFullPath, checkContext);
        }

        public void ProcessProjectEvaluationStarted(ICheckContext checkContext, string projectFullPath)
            => ProcessProjectEvaluationStarted(BuildCheckDataSource.BuildExecution, checkContext, projectFullPath);

        public void ProcessProjectEvaluationStarted(
            BuildCheckDataSource buildCheckDataSource,
            ICheckContext checkContext,
            string projectFullPath)
        {
            _projectsByEvaluationId[checkContext.BuildEventContext.EvaluationId] = projectFullPath;
            // We are receiving project imported data only from the logger events
            if (buildCheckDataSource == BuildCheckDataSource.EventArgs &&
                !_deferredProjectEvalIdToImportedProjects.ContainsKey(checkContext.BuildEventContext.EvaluationId))
            {
                _deferredProjectEvalIdToImportedProjects.Add(checkContext.BuildEventContext.EvaluationId,
                    [projectFullPath]);
            }
        }

        /*
         *
         * Following methods are for future use (should we decide to approach in-execution check)
         *
         */

        public void EndProjectEvaluation(BuildEventContext buildEventContext)
        {
        }

        public void StartProjectRequest(ICheckContext checkContext, string projectFullPath)
        {
            BuildEventContext buildEventContext = checkContext.BuildEventContext;

            // There can be multiple ProjectStarted-ProjectFinished per single configuration project build (each request for different target)
            _projectsByInstanceId[buildEventContext.ProjectInstanceId] = projectFullPath;

            if (_deferredEvalDiagnostics.TryGetValue(buildEventContext.EvaluationId, out var list))
            {
                foreach (BuildEventArgs deferredArgs in list)
                {
                    deferredArgs.BuildEventContext = deferredArgs.BuildEventContext!.WithInstanceIdAndContextId(buildEventContext);
                    checkContext.DispatchBuildEvent(deferredArgs);
                }
                list.Clear();
                _deferredEvalDiagnostics.Remove(buildEventContext.EvaluationId);
            }
        }

        private readonly Dictionary<int, List<BuildEventArgs>> _deferredEvalDiagnostics = new();

        /// <summary>
        /// Registers the logic import by a project file.
        /// </summary>
        /// <param name="evaluationId">The evaluation id is associated with the root project path.</param>
        /// <param name="importingProjectFile">The path of the project file that is importing a new project.</param>
        /// <param name="importedFile">The path of the imported project file.</param>
        private void PropagateImport(int evaluationId, string importingProjectFile, string importedFile)
        {
            if (_deferredProjectEvalIdToImportedProjects.TryGetValue(evaluationId,
                    out HashSet<string>? importedProjects))
            {
                importedProjects.Add(importedFile);
            }
        }

        void IResultReporter.ReportResult(BuildEventArgs eventArgs, ICheckContext checkContext)
        {
            // If we do not need to decide on promotability/demotability of warnings or we are ready to decide on those
            //  - we can just dispatch the event.
            if (
                // no context - we cannot defer as we'd need eval id to queue it
                eventArgs.BuildEventContext == null ||
                // no eval id - we cannot defer as we'd need eval id to queue it
                eventArgs.BuildEventContext.EvaluationId == BuildEventContext.InvalidEvaluationId ||
                // instance id known - no need to defer
                eventArgs.BuildEventContext.ProjectInstanceId != BuildEventContext.InvalidProjectInstanceId ||
                // it's not a warning - no need to defer
                eventArgs is not BuildWarningEventArgs)
            {
                checkContext.DispatchBuildEvent(eventArgs);
                return;
            }

            // This is evaluation - so we need to defer it until we know the instance id and context id

            if (!_deferredEvalDiagnostics.TryGetValue(eventArgs.BuildEventContext.EvaluationId, out var list))
            {
                list = [];
                _deferredEvalDiagnostics[eventArgs.BuildEventContext.EvaluationId] = list;
            }

            list.Add(eventArgs);
        }

        public void EndProjectRequest(
            ICheckContext checkContext,
            string projectFullPath)
        {
            _buildEventsProcessor.ProcessProjectDone(checkContext, projectFullPath);
        }

        public void ProcessPropertyRead(PropertyReadInfo propertyReadInfo, CheckLoggingContext checkContext)
        {
            if (!_buildCheckCentralContext.HasPropertyReadActions)
            {
                return;
            }

            if (TryGetProjectFullPath(checkContext.BuildEventContext, out string projectFullPath))
            {
                PropertyReadData propertyReadData = new(
                    projectFullPath,
                    checkContext.BuildEventContext.ProjectInstanceId,
                    propertyReadInfo);
                _buildEventsProcessor.ProcessPropertyRead(propertyReadData, checkContext);
            }
        }

        public void ProcessPropertyWrite(PropertyWriteInfo propertyWriteInfo, CheckLoggingContext checkContext)
        {
            if (!_buildCheckCentralContext.HasPropertyWriteActions)
            {
                return;
            }

            if (TryGetProjectFullPath(checkContext.BuildEventContext, out string projectFullPath))
            {
                PropertyWriteData propertyWriteData = new(
                    projectFullPath,
                    checkContext.BuildEventContext.ProjectInstanceId,
                    propertyWriteInfo);
                _buildEventsProcessor.ProcessPropertyWrite(propertyWriteData, checkContext);
            }
        }

        public void Shutdown()
        { /* Too late here for any communication to the main node or for logging anything */ }

        private class CheckFactoryContext(
            CheckFactory factory,
            string[] ruleIds,
            bool isEnabledByDefault)
        {
            public Check Factory()
            {
                Check ba = factory();
                return ba;
            }

            public CheckWrapper Initialize(Check ba, IResultReporter resultReporter, ConfigurationContext configContext)
            {
                try
                {
                    ba.Initialize(configContext);
                }
                catch (BuildCheckConfigurationException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new BuildCheckConfigurationException(
                        $"The Check '{ba.FriendlyName}' failed to initialize: {e.Message}", e);
                }
                return new CheckWrapper(ba, resultReporter);
            }

            public CheckWrapper? MaterializedCheck { get; set; }

            public string[] RuleIds { get; init; } = ruleIds;

            public bool IsEnabledByDefault { get; init; } = isEnabledByDefault;

            public string FriendlyName => MaterializedCheck?.Check.FriendlyName ?? factory().FriendlyName;
        }
    }
}

internal interface IResultReporter
{
    void ReportResult(BuildEventArgs result, ICheckContext checkContext);
}
