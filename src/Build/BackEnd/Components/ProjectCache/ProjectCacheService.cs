// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Eventing;
using Microsoft.Build.Execution;
using Microsoft.Build.FileAccesses;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using ExceptionHandling = Microsoft.Build.Shared.ExceptionHandling;

namespace Microsoft.Build.Experimental.ProjectCache
{
    internal record CacheRequest(BuildSubmission Submission, BuildRequestConfiguration Configuration);

    internal sealed class ProjectCacheService : IAsyncDisposable
    {
        private static readonly ParallelOptions s_parallelOptions = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

        private static HashSet<string> s_projectSpecificPropertyNames = new(StringComparer.OrdinalIgnoreCase) { "TargetFramework", "Configuration", "Platform", "TargetPlatform", "OutputType" };

        private readonly BuildManager _buildManager;
        private readonly IBuildComponentHost _componentHost;
        private readonly ILoggingService _loggingService;
#if FEATURE_REPORTFILEACCESSES
        private readonly IFileAccessManager _fileAccessManager;
#endif
        private readonly IConfigCache _configCache;

        private readonly ProjectCacheDescriptor? _globalProjectCacheDescriptor;

        private readonly ConcurrentDictionary<ProjectCacheDescriptor, Lazy<Task<ProjectCachePlugin>>> _projectCachePlugins = new(ProjectCacheDescriptorEqualityComparer.Instance);

        // Helps to avoid excessive allocation since BuildRequestConfiguration doesn't expose global properties in a way the plugins can consume (PropertyDictionary<ProjectPropertyInstance> vs IReadOnlyDictionary<string, string>).
        private readonly ConcurrentDictionary<BuildRequestConfiguration, IReadOnlyDictionary<string, string>> _globalPropertiesPerConfiguration = new();

        private bool _isVsScenario;

        private bool _isDisposed;

        private record struct ProjectCachePlugin(
            string Name,
            ProjectCachePluginBase? Instance,
#if FEATURE_REPORTFILEACCESSES
            FileAccessManager.HandlerRegistration? HandlerRegistration,
#endif
            ExceptionDispatchInfo? InitializationException = null);

        /// <summary>
        /// An instanatiable version of MSBuildFileSystemBase not overriding any methods,
        /// i.e. falling back to FileSystem.Default.
        /// </summary>
        private sealed class DefaultMSBuildFileSystem : MSBuildFileSystemBase
        {
            private DefaultMSBuildFileSystem()
            {
            }

            public static DefaultMSBuildFileSystem Instance { get; } = new();
        }

        public ProjectCacheService(
            BuildManager buildManager,
            ILoggingService loggingService,
#if FEATURE_REPORTFILEACCESSES
            IFileAccessManager fileAccessManager,
#endif
            IConfigCache configCache,
            ProjectCacheDescriptor? globalProjectCacheDescriptor)
        {
            _buildManager = buildManager;
            _componentHost = buildManager;
            _loggingService = loggingService;
#if FEATURE_REPORTFILEACCESSES
            _fileAccessManager = fileAccessManager;
#endif
            _configCache = configCache;
            _globalProjectCacheDescriptor = globalProjectCacheDescriptor;
        }

        /// <summary>
        /// Optimization which frontloads plugin initialization since we have an entire graph.
        /// </summary>
        public void InitializePluginsForGraph(
            ProjectGraph projectGraph,
            ICollection<string> requestedTargets,
            CancellationToken cancellationToken)
        {
            EnsureNotDisposed();

            // Performing this in a Task.Run to break away from the main thread and prevent hangs
            Task.Run(
                () =>
                {
                    Parallel.ForEach(
                        projectGraph.ProjectNodes,
                        s_parallelOptions,
                        node =>
                        {
                            foreach (ProjectCacheDescriptor projectCacheDescriptor in GetProjectCacheDescriptors(node.ProjectInstance))
                            {
                                // Intentionally fire-and-forget to asynchronously initialize the plugin. Any exceptions will bubble up later when querying.
                                _ = GetProjectCachePluginAsync(projectCacheDescriptor, projectGraph, buildRequestConfiguration: null, requestedTargets, cancellationToken)
                                    .ContinueWith(t => { }, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted);
                            }
                        });
                },
                cancellationToken);
        }

        public void InitializePluginsForVsScenario(
            IEnumerable<ProjectCacheDescriptor> projectCacheDescriptors,
            BuildRequestConfiguration buildRequestConfiguration,
            ICollection<string> requestedTargets,
            CancellationToken cancellationToken)
        {
            EnsureNotDisposed();

            _isVsScenario = true;

            // Bail out for design-time builds
            if (IsDesignTimeBuild(buildRequestConfiguration))
            {
                return;
            }

            // Performing this in a Task.Run to break away from the main thread and prevent hangs
            Task.Run(
                () =>
                {
                    Parallel.ForEach(
                        projectCacheDescriptors,
                        s_parallelOptions,
                        projectCacheDescriptor =>
                        {
                            // Intentionally fire-and-forget to asynchronously initialize the plugin. Any exceptions will bubble up later when querying.
                            _ = GetProjectCachePluginAsync(projectCacheDescriptor, projectGraph: null, buildRequestConfiguration, requestedTargets, cancellationToken)
                                .ContinueWith(t => { }, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted);
                        });
                },
                cancellationToken);
        }

        private Task<ProjectCachePlugin> GetProjectCachePluginAsync(
            ProjectCacheDescriptor projectCacheDescriptor,
            ProjectGraph? projectGraph,
            BuildRequestConfiguration? buildRequestConfiguration,
            ICollection<string> requestedTargets,
            CancellationToken cancellationToken)
            => _projectCachePlugins.GetOrAdd(
                projectCacheDescriptor,
                // The use of Lazy is because ConcurrentDictionary doesn't guarantee the value factory executes only once if there are multiple simultaneous callers,
                // so this ensures that CreateAndInitializePluginAsync is only called exactly once.
                descriptor => new Lazy<Task<ProjectCachePlugin>>(() => CreateAndInitializePluginAsync(descriptor, projectGraph, buildRequestConfiguration, requestedTargets, cancellationToken)))
               .Value;

        private IEnumerable<ProjectCacheDescriptor> GetProjectCacheDescriptors(ProjectInstance projectInstance)
        {
            if (_globalProjectCacheDescriptor != null)
            {
                yield return _globalProjectCacheDescriptor;
            }

            ICollection<ProjectItemInstance> items = projectInstance.GetItems(ItemTypeNames.ProjectCachePlugin);
            foreach (ProjectItemInstance item in items)
            {
                string pluginPath = FileUtilities.NormalizePath(Path.Combine(item.Project.Directory, item.EvaluatedInclude));

                var pluginSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ProjectMetadataInstance metadatum in item.Metadata)
                {
                    pluginSettings.Add(metadatum.Name, metadatum.EvaluatedValue);
                }

                yield return ProjectCacheDescriptor.FromAssemblyPath(pluginPath, pluginSettings);
            }
        }

        private async Task<ProjectCachePlugin> CreateAndInitializePluginAsync(
            ProjectCacheDescriptor projectCacheDescriptor,
            ProjectGraph? projectGraph,
            BuildRequestConfiguration? buildRequestConfiguration,
            ICollection<string> requestedTargets,
            CancellationToken cancellationToken)
        {
            BuildEventContext buildEventContext = BuildEventContext.Invalid;
            BuildEventFileInfo buildEventFileInfo = BuildEventFileInfo.Empty;
            var pluginLogger = new LoggingServiceToPluginLoggerAdapter(
                _loggingService,
                buildEventContext,
                buildEventFileInfo);

            ProjectCachePluginBase pluginInstance;
            string pluginTypeName;

            if (projectCacheDescriptor.PluginInstance != null)
            {
                pluginInstance = projectCacheDescriptor.PluginInstance;
                pluginTypeName = projectCacheDescriptor.PluginInstance.GetType().Name;
            }
            else
            {
                ErrorUtilities.VerifyThrowArgumentNull(projectCacheDescriptor.PluginAssemblyPath, nameof(projectCacheDescriptor.PluginAssemblyPath));

                string pluginAssemblyPath = projectCacheDescriptor.PluginAssemblyPath!;
                pluginTypeName = pluginAssemblyPath; // Just in case the assembly can't be loaded, the path would be helpful to help identify the problem.
                try
                {
                    MSBuildEventSource.Log.ProjectCacheCreatePluginInstanceStart(pluginAssemblyPath);

                    Type pluginType = GetTypeFromAssemblyPath(pluginAssemblyPath);
                    pluginTypeName = pluginType.Name;

                    pluginInstance = GetPluginInstanceFromType(pluginType);
                }
                catch (Exception e)
                {
                    return new ProjectCachePlugin(
                        pluginTypeName,
                        Instance: null,
#if FEATURE_REPORTFILEACCESSES
                        HandlerRegistration: null,
#endif
                        ExceptionDispatchInfo.Capture(e));
                }
                finally
                {
                    MSBuildEventSource.Log.ProjectCacheCreatePluginInstanceStop(pluginAssemblyPath, pluginTypeName);
                }
            }

            IReadOnlyCollection<ProjectGraphEntryPoint>? graphEntryPoints = buildRequestConfiguration != null
                ? GetGraphEntryPoints(buildRequestConfiguration)
                : null;

            // In practice, the underlying type of the ICollection is a List<string> so attempt to cast first
            IReadOnlyList<string> requestedTargetsList = requestedTargets as List<string> ?? requestedTargets.ToList();

            _loggingService.LogComment(buildEventContext, MessageImportance.High, "LoadingProjectCachePlugin", pluginTypeName);
            MSBuildEventSource.Log.ProjectCacheBeginBuildStart(pluginTypeName);

            try
            {
                await pluginInstance.BeginBuildAsync(
                    new CacheContext(
                        projectCacheDescriptor.PluginSettings,
                        DefaultMSBuildFileSystem.Instance,
                        requestedTargetsList,
                        projectGraph,
                        graphEntryPoints),
                    pluginLogger,
                    cancellationToken);

                if (pluginLogger.HasLoggedErrors)
                {
                    ProjectCacheException.ThrowForErrorLoggedInsideTheProjectCache("ProjectCacheInitializationFailed");
                }

#if FEATURE_REPORTFILEACCESSES
                FileAccessManager.HandlerRegistration? handlerRegistration = null;
                if (_componentHost.BuildParameters.ReportFileAccesses)
                {
                    handlerRegistration = _fileAccessManager.RegisterHandlers(
                        (buildRequest, fileAccessData) =>
                        {
                            // TODO: Filter out projects which do not configure this plugin
                            FileAccessContext fileAccessContext = GetFileAccessContext(buildRequest);
                            pluginInstance.HandleFileAccess(fileAccessContext, fileAccessData);
                        },
                        (buildRequest, processData) =>
                        {
                            // TODO: Filter out projects which do not configure this plugin
                            FileAccessContext fileAccessContext = GetFileAccessContext(buildRequest);
                            pluginInstance.HandleProcess(fileAccessContext, processData);
                        });
                }
#endif

                return new ProjectCachePlugin(
                    pluginTypeName,
                    pluginInstance,
#if FEATURE_REPORTFILEACCESSES
                    handlerRegistration,
#endif
                    InitializationException: null);
            }
            catch (Exception e)
            {
                return new ProjectCachePlugin(
                    pluginTypeName,
                    Instance: null,
#if FEATURE_REPORTFILEACCESSES
                    HandlerRegistration: null,
#endif
                    ExceptionDispatchInfo.Capture(e));
            }
            finally
            {
                MSBuildEventSource.Log.ProjectCacheBeginBuildStop(pluginTypeName);
            }
        }

        private FileAccessContext GetFileAccessContext(BuildRequest buildRequest)
        {
            BuildRequestConfiguration configuration = _configCache[buildRequest.ConfigurationId];
            IReadOnlyDictionary<string, string> globalProperties = GetGlobalProperties(configuration);
            return new FileAccessContext(configuration.ProjectFullPath, globalProperties, buildRequest.Targets);
        }

        private IReadOnlyDictionary<string, string> GetGlobalProperties(BuildRequestConfiguration configuration)
            => _globalPropertiesPerConfiguration.GetOrAdd(
                    configuration,
                    static configuration =>
                    {
                        Dictionary<string, string> globalProperties = new(configuration.GlobalProperties.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (ProjectPropertyInstance property in configuration.GlobalProperties)
                        {
                            globalProperties.Add(property.Name, property.EvaluatedValue);
                        }

                        return globalProperties;
                    });

        private static ProjectCachePluginBase GetPluginInstanceFromType(Type pluginType)
        {
            try
            {
                return (ProjectCachePluginBase)Activator.CreateInstance(pluginType)!;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                HandlePluginException(e.InnerException, "Constructor");
                return null!; // Unreachable
            }
        }

        private static Type GetTypeFromAssemblyPath(string pluginAssemblyPath)
        {
            var assembly = LoadAssembly(pluginAssemblyPath);

            var type = GetTypes<ProjectCachePluginBase>(assembly).FirstOrDefault();

            if (type == null)
            {
                ProjectCacheException.ThrowForMSBuildIssueWithTheProjectCache("NoProjectCachePluginFoundInAssembly", pluginAssemblyPath);
            }

            return type!;

            Assembly LoadAssembly(string resolverPath)
            {
#if !FEATURE_ASSEMBLYLOADCONTEXT
                return Assembly.LoadFrom(resolverPath);
#else
                return s_loader.LoadFromPath(resolverPath);
#endif
            }

            IEnumerable<Type> GetTypes<T>(Assembly assembly)
            {
                return assembly.ExportedTypes
                    .Select(type => new { type, info = type.GetTypeInfo() })
                    .Where(
                        t => t.info.IsClass &&
                             t.info.IsPublic &&
                             !t.info.IsAbstract &&
                             typeof(T).IsAssignableFrom(t.type))
                    .Select(t => t.type);
            }
        }

#if FEATURE_ASSEMBLYLOADCONTEXT
        private static readonly CoreClrAssemblyLoader s_loader = new CoreClrAssemblyLoader();
#endif

        public bool ShouldUseCache(BuildRequestConfiguration buildRequestConfiguration)
        {
            if (IsDesignTimeBuild(buildRequestConfiguration))
            {
                return false;
            }

            if (_globalProjectCacheDescriptor != null)
            {
                return true;
            }

            // We've determined it's the VS scenario and know that there are project cache plugins.
            if (_isVsScenario)
            {
                return true;
            }

            // If the project isn't loaded, don't force it to be just to check if it's cacheable as this may not be very performant.
            if (!buildRequestConfiguration.IsLoaded)
            {
                return false;
            }

            // We need to retrieve the configuration if it's already loaded in order to access the Project property below.
            if (buildRequestConfiguration.IsCached)
            {
                buildRequestConfiguration.RetrieveFromCache();
            }

            // Check if there are any project cache items defined in the project
            return GetProjectCacheDescriptors(buildRequestConfiguration.Project).Any();
        }

        private bool IsDesignTimeBuild(BuildRequestConfiguration buildRequestConfiguration)
        {
            string? designTimeBuild = buildRequestConfiguration.GlobalProperties[DesignTimeProperties.DesignTimeBuild]?.EvaluatedValue;
            string? buildingProject = buildRequestConfiguration.GlobalProperties[DesignTimeProperties.BuildingProject]?.EvaluatedValue;
            return ConversionUtilities.ConvertStringToBool(designTimeBuild, nullOrWhitespaceIsFalse: true)
                || (buildingProject != null && !ConversionUtilities.ConvertStringToBool(buildingProject, nullOrWhitespaceIsFalse: true));
        }

        public void PostCacheRequest(CacheRequest cacheRequest, CancellationToken cancellationToken)
        {
            EnsureNotDisposed();

            Task.Run(
                async () =>
                {
                    try
                    {
                        (CacheResult cacheResult, int projectContextId) = await ProcessCacheRequestAsync();
                        _buildManager.PostCacheResult(cacheRequest, cacheResult, projectContextId);
                    }
                    catch (Exception e)
                    {
                        _buildManager.PostCacheResult(cacheRequest, CacheResult.IndicateException(e), BuildEventContext.InvalidProjectContextId);
                    }
                },
                cancellationToken);

            async Task<(CacheResult Result, int ProjectContextId)> ProcessCacheRequestAsync()
            {
                EvaluateProjectIfNecessary(cacheRequest.Submission, cacheRequest.Configuration);

                BuildRequestData buildRequest = new BuildRequestData(
                    cacheRequest.Configuration.Project,
                    cacheRequest.Submission.BuildRequestData?.TargetNames.ToArray() ?? []);
                BuildEventContext buildEventContext = _loggingService.CreateProjectCacheBuildEventContext(
                    cacheRequest.Submission.SubmissionId,
                    evaluationId: cacheRequest.Configuration.Project.EvaluationId,
                    projectInstanceId: cacheRequest.Configuration.ConfigurationId,
                    projectFile: cacheRequest.Configuration.Project.FullPath);

                CacheResult cacheResult;
                try
                {
                    cacheResult = await GetCacheResultAsync(buildRequest, cacheRequest.Configuration, buildEventContext, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Wrap the exception here so we can preserve the ProjectContextId
                    cacheResult = CacheResult.IndicateException(ex);
                }

                return (cacheResult, buildEventContext.ProjectContextId);
            }

            void EvaluateProjectIfNecessary(BuildSubmission submission, BuildRequestConfiguration configuration)
            {
                ErrorUtilities.VerifyThrow(submission.BuildRequestData != null,
                    "Submission BuildRequestData is not populated.");

                lock (configuration)
                {
                    if (!configuration.IsLoaded)
                    {
                        configuration.LoadProjectIntoConfiguration(
                            _buildManager,
                            submission.BuildRequestData!.Flags,
                            submission.SubmissionId,
                            Scheduler.InProcNodeId);

                        // If we're taking the time to evaluate, avoid having other nodes to repeat the same evaluation.
                        // Based on the assumption that ProjectInstance serialization is faster than evaluating from scratch.
                        configuration.Project.TranslateEntireState = true;
                    }
                }
            }
        }

        private async Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, BuildRequestConfiguration buildRequestConfiguration, BuildEventContext buildEventContext, CancellationToken cancellationToken)
        {
            ErrorUtilities.VerifyThrowInternalNull(buildRequest.ProjectInstance, nameof(buildRequest.ProjectInstance));

            var buildEventFileInfo = new BuildEventFileInfo(buildRequest.ProjectFullPath);
            var pluginLogger = new LoggingServiceToPluginLoggerAdapter(
                _loggingService,
                buildEventContext,
                buildEventFileInfo);

            string? targetNames = buildRequest.TargetNames != null && buildRequest.TargetNames.Count > 0
                ? string.Join(", ", buildRequest.TargetNames)
                : null;
            if (string.IsNullOrEmpty(targetNames))
            {
                _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheQueryStartedWithDefaultTargets", buildRequest.ProjectFullPath);
            }
            else
            {
                _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheQueryStartedWithTargetNames", buildRequest.ProjectFullPath, targetNames);
            }

            HashSet<ProjectCacheDescriptor> queriedCaches = new(ProjectCacheDescriptorEqualityComparer.Instance);
            CacheResult? cacheResult = null;
            foreach (ProjectCacheDescriptor projectCacheDescriptor in GetProjectCacheDescriptors(buildRequest.ProjectInstance!))
            {
                // Ensure each unique plugin is only queried once
                if (!queriedCaches.Add(projectCacheDescriptor))
                {
                    continue;
                }

                ICollection<string> requestedTargetsList = buildRequestConfiguration.RequestedTargets as ICollection<string> ?? buildRequestConfiguration.RequestedTargets.ToList();
                ProjectCachePlugin plugin = await GetProjectCachePluginAsync(projectCacheDescriptor, projectGraph: null, buildRequestConfiguration, requestedTargetsList, cancellationToken);
                try
                {
                    // Rethrow any initialization exception.
                    plugin.InitializationException?.Throw();

                    ErrorUtilities.VerifyThrow(plugin.Instance != null, "Plugin '{0}' instance is null", plugin.Name);

                    MSBuildEventSource.Log.ProjectCacheGetCacheResultStart(plugin.Name, buildRequest.ProjectFullPath, targetNames ?? MSBuildConstants.DefaultTargetsMarker);
                    cacheResult = await plugin.Instance!.GetCacheResultAsync(buildRequest, pluginLogger, cancellationToken);

                    if (pluginLogger.HasLoggedErrors || cacheResult.ResultType == CacheResultType.None)
                    {
                        ProjectCacheException.ThrowForErrorLoggedInsideTheProjectCache("ProjectCacheQueryFailed", buildRequest.ProjectFullPath);
                    }

                    if (cacheResult.ResultType == CacheResultType.CacheHit)
                    {
                        break;
                    }
                }
                catch (Exception e) when (e is not ProjectCacheException)
                {
                    HandlePluginException(e, nameof(ProjectCachePluginBase.GetCacheResultAsync));
                    return null!; // Unreachable
                }
                finally
                {
                    if (MSBuildEventSource.Log.IsEnabled())
                    {
                        string cacheResultType = cacheResult?.ResultType.ToString() ?? nameof(CacheResultType.None);
                        MSBuildEventSource.Log.ProjectCacheGetCacheResultStop(plugin.Name, buildRequest.ProjectFullPath, targetNames ?? "<default>", cacheResultType);
                    }
                }
            }

            // Handle the case of no configured plugins.
            cacheResult ??= CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);

            switch (cacheResult.ResultType)
            {
                case CacheResultType.CacheHit:
                    if (string.IsNullOrEmpty(targetNames))
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheHitWithDefaultTargets", buildRequest.ProjectFullPath);
                    }
                    else
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheHitWithTargetNames", buildRequest.ProjectFullPath, targetNames);
                    }

                    // Similar to CopyFilesToOutputDirectory from Microsoft.Common.CurrentVersion.targets, so that progress can be seen.
                    // TODO: This should be indented by the console logger. That requires making these log events structured.
                    if (!buildRequestConfiguration.IsTraversal)
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.High, "ProjectCacheHitWithOutputs", buildRequest.ProjectInstance!.GetPropertyValue(ReservedPropertyNames.projectName));
                    }

                    break;
                case CacheResultType.CacheMiss:
                    if (string.IsNullOrEmpty(targetNames))
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheMissWithDefaultTargets", buildRequest.ProjectFullPath);
                    }
                    else
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheMissWithTargetNames", buildRequest.ProjectFullPath, targetNames);
                    }

                    break;
                case CacheResultType.CacheNotApplicable:
                    if (string.IsNullOrEmpty(targetNames))
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheNotApplicableWithDefaultTargets", buildRequest.ProjectFullPath);
                    }
                    else
                    {
                        _loggingService.LogComment(buildEventContext, MessageImportance.Normal, "ProjectCacheNotApplicableWithTargetNames", buildRequest.ProjectFullPath, targetNames);
                    }

                    break;
                case CacheResultType.None: // Should not get here based on the throw above
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return cacheResult;
        }

        private IReadOnlyCollection<ProjectGraphEntryPoint> GetGraphEntryPoints(BuildRequestConfiguration configuration)
        {
            var globalProperties = new Dictionary<string, string>(configuration.GlobalProperties.Count, StringComparer.OrdinalIgnoreCase);
            foreach (ProjectPropertyInstance property in configuration.GlobalProperties)
            {
                // If any project specific property is set, it will propagate down the project graph and force all nodes to that property's specific side effects, which is incorrect.
                if (!s_projectSpecificPropertyNames.Contains(property.Name))
                {
                    globalProperties.Add(property.Name, property.EvaluatedValue);
                }
            }

            if (globalProperties.TryGetValue(SolutionProjectGenerator.CurrentSolutionConfigurationContents, out string? solutionConfigurationXml)
                && !string.IsNullOrWhiteSpace(solutionConfigurationXml))
            {
                // A solution supports multiple solution configurations (different values for Configuration and Platform).
                // Each solution configuration generates a different static graph.
                // Therefore, plugin implementations that rely on creating static graphs in their BeginBuild methods need access to the
                // currently solution configuration used by VS.
                //
                // In this VS workaround, however, we do not have access to VS' solution configuration. The only information we have is a global property
                // named "CurrentSolutionConfigurationContents" that VS sets on each built project. It does not contain the solution configuration itself, but
                // instead it contains information on how the solution configuration maps to each project's configuration.
                //
                // So instead of using the solution file as the entry point, we parse this VS property and extract graph entry points from it, for every project
                // mentioned in the "CurrentSolutionConfigurationContents" global property.
                return GenerateGraphEntryPointsFromSolutionConfigurationXml(solutionConfigurationXml!, configuration.ProjectFullPath, globalProperties);
            }
            else
            {
                return [new ProjectGraphEntryPoint(configuration.ProjectFullPath, globalProperties)];
            }

            static IReadOnlyCollection<ProjectGraphEntryPoint> GenerateGraphEntryPointsFromSolutionConfigurationXml(
                string solutionConfigurationXml,
                string definingProjectPath,
                Dictionary<string, string> templateGlobalProperties)
            {
                XmlNodeList? projectConfigurations = SolutionConfiguration.GetProjectConfigurations(solutionConfigurationXml);
                if (projectConfigurations == null || projectConfigurations.Count == 0)
                {
                    return Array.Empty<ProjectGraphEntryPoint>();
                }

                var graphEntryPoints = new List<ProjectGraphEntryPoint>(projectConfigurations.Count);

                foreach (XmlElement projectConfiguration in projectConfigurations)
                {
                    ErrorUtilities.VerifyThrowInternalNull(projectConfiguration.Attributes, nameof(projectConfiguration.Attributes));

                    var buildProjectInSolution = projectConfiguration.Attributes![SolutionConfiguration.BuildProjectInSolutionAttribute];
                    if (buildProjectInSolution is not null &&
                        string.IsNullOrWhiteSpace(buildProjectInSolution.Value) is false &&
                        bool.TryParse(buildProjectInSolution.Value, out var buildProject) &&
                        buildProject is false)
                    {
                        continue;
                    }

                    XmlAttribute? projectPathAttribute = projectConfiguration.Attributes![SolutionConfiguration.AbsolutePathAttribute];
                    ErrorUtilities.VerifyThrow(projectPathAttribute is not null, "Expected VS to set the project path on each ProjectConfiguration element.");

                    string projectPath = projectPathAttribute!.Value;

                    (string configuration, string platform) = SolutionFile.ParseConfigurationName(projectConfiguration.InnerText, definingProjectPath, 0, solutionConfigurationXml);

                    // Take the defining project global properties and override the configuration and platform.
                    // It's sufficient to only set Configuration and Platform.
                    // But we send everything to maximize the plugins' potential to quickly workaround potential MSBuild issues while the MSBuild fixes flow into VS.
                    var globalProperties = new Dictionary<string, string>(templateGlobalProperties, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Configuration"] = configuration,
                        ["Platform"] = platform
                    };

                    graphEntryPoints.Add(new ProjectGraphEntryPoint(projectPath, globalProperties));
                }

                return graphEntryPoints;
            }
        }

        public async Task HandleBuildResultAsync(
            BuildRequestConfiguration requestConfiguration,
            BuildResult buildResult,
            BuildEventContext buildEventContext,
            CancellationToken cancellationToken)
        {
            ErrorUtilities.VerifyThrowInternalNull(requestConfiguration.Project, nameof(requestConfiguration.Project));

            if (_projectCachePlugins.IsEmpty)
            {
                return;
            }

            // We need to retrieve the configuration if it's already loaded in order to access the Project property below.
            if (requestConfiguration.IsCached)
            {
                requestConfiguration.RetrieveFromCache();
            }

            // Filter to plugins which apply to the project, if any
            List<ProjectCacheDescriptor> projectCacheDescriptors = GetProjectCacheDescriptors(requestConfiguration.Project).ToList();
            if (projectCacheDescriptors.Count == 0)
            {
                return;
            }

#if FEATURE_REPORTFILEACCESSES
            if (_componentHost.BuildParameters.ReportFileAccesses)
            {
                _fileAccessManager.WaitForFileAccessReportCompletion(buildResult.GlobalRequestId, cancellationToken);
            }
#endif

            IReadOnlyDictionary<string, string> globalProperties = GetGlobalProperties(requestConfiguration);

            List<string> targets = buildResult.ResultsByTarget?.Keys.ToList() ?? new();
            string? targetNames = string.Join(", ", targets);

            FileAccessContext fileAccessContext = new(requestConfiguration.ProjectFullPath, globalProperties, targets);

            var buildEventFileInfo = new BuildEventFileInfo(requestConfiguration.ProjectFullPath);
            var pluginLogger = new LoggingServiceToPluginLoggerAdapter(
                _loggingService,
                buildEventContext,
                buildEventFileInfo);

            Task[] tasks = new Task[projectCacheDescriptors.Count];
            int idx = 0;
            foreach (ProjectCacheDescriptor projectCacheDescriptor in projectCacheDescriptors)
            {
                tasks[idx++] = Task.Run(
                    async () =>
                    {
                        if (!_projectCachePlugins.TryGetValue(projectCacheDescriptor, out Lazy<Task<ProjectCachePlugin>>? pluginLazyTask))
                        {
                            // The plugin might not be in the collection if it was never initialized, which can happen if there are multiple plugins
                            // and the first one(s) always handles the cache request so the subsequent one(s) never get lazy initialized.
                            return;
                        }

                        ProjectCachePlugin plugin = await pluginLazyTask.Value;

                        // Rethrow any initialization exception.
                        plugin.InitializationException?.Throw();

                        ErrorUtilities.VerifyThrow(plugin.Instance != null, "Plugin '{0}' instance is null", plugin.Name);

                        MSBuildEventSource.Log.ProjectCacheHandleBuildResultStart(plugin.Name, fileAccessContext.ProjectFullPath, targetNames);
                        try
                        {
                            await plugin.Instance!.HandleProjectFinishedAsync(fileAccessContext, buildResult, pluginLogger, cancellationToken);
                        }
                        catch (Exception e) when (e is not ProjectCacheException)
                        {
                            HandlePluginException(e, nameof(ProjectCachePluginBase.HandleProjectFinishedAsync));
                        }
                        finally
                        {
                            MSBuildEventSource.Log.ProjectCacheHandleBuildResultStop(plugin.Name, fileAccessContext.ProjectFullPath, targetNames);
                        }
                    },
                    cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (pluginLogger.HasLoggedErrors)
            {
                ProjectCacheException.ThrowForErrorLoggedInsideTheProjectCache("ProjectCacheHandleBuildResultFailed", fileAccessContext.ProjectFullPath);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_projectCachePlugins.IsEmpty)
            {
                return;
            }

            BuildEventContext buildEventContext = BuildEventContext.Invalid;
            BuildEventFileInfo buildEventFileInfo = BuildEventFileInfo.Empty;
            var pluginLogger = new LoggingServiceToPluginLoggerAdapter(
                _loggingService,
                buildEventContext,
                buildEventFileInfo);

            _loggingService.LogComment(buildEventContext, MessageImportance.Low, "ProjectCacheEndBuild");

            Task[] cleanupTasks = new Task[_projectCachePlugins.Count];
            int idx = 0;
            foreach (KeyValuePair<ProjectCacheDescriptor, Lazy<Task<ProjectCachePlugin>>> kvp in _projectCachePlugins)
            {
                cleanupTasks[idx++] = Task.Run(async () =>
                {
                    ProjectCachePlugin plugin = await kvp.Value.Value;

                    // If there is no instance, the exceptions would have bubbled up already, so skip cleanup for this one.
                    if (plugin.Instance == null)
                    {
                        return;
                    }

#if FEATURE_REPORTFILEACCESSES
                    if (plugin.HandlerRegistration.HasValue)
                    {
                        plugin.HandlerRegistration.Value.Dispose();
                    }
#endif

                    MSBuildEventSource.Log.ProjectCacheEndBuildStart(plugin.Name);
                    try
                    {
                        await plugin.Instance.EndBuildAsync(pluginLogger, CancellationToken.None);
                    }
                    catch (Exception e) when (e is not ProjectCacheException)
                    {
                        HandlePluginException(e, nameof(ProjectCachePluginBase.EndBuildAsync));
                    }
                    finally
                    {
                        MSBuildEventSource.Log.ProjectCacheEndBuildStop(plugin.Name);
                    }
                });
            }

            await Task.WhenAll(cleanupTasks).ConfigureAwait(false);

            if (pluginLogger.HasLoggedErrors)
            {
                ProjectCacheException.ThrowForErrorLoggedInsideTheProjectCache("ProjectCacheShutdownFailed");
            }
        }

        private void EnsureNotDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ProjectCacheService));
            }
        }

        private static void HandlePluginException(Exception e, string apiExceptionWasThrownFrom)
        {
            if (ExceptionHandling.IsCriticalException(e))
            {
                throw e;
            }

            ProjectCacheException.ThrowAsUnhandledException(
                e,
                "ProjectCacheException",
                apiExceptionWasThrownFrom);
        }

        private class LoggingServiceToPluginLoggerAdapter : PluginLoggerBase
        {
            private readonly ILoggingService _loggingService;

            private readonly BuildEventContext _buildEventContext;

            private readonly BuildEventFileInfo _buildEventFileInfo;

            public override bool HasLoggedErrors { get; protected set; }

            public LoggingServiceToPluginLoggerAdapter(
                ILoggingService loggingService,
                BuildEventContext buildEventContext,
                BuildEventFileInfo buildEventFileInfo)
            {
                _loggingService = loggingService;
                _buildEventContext = buildEventContext;
                _buildEventFileInfo = buildEventFileInfo;
            }

            public override void LogMessage(string message, MessageImportance? messageImportance = null)
            {
                _loggingService.LogCommentFromText(
                    _buildEventContext,
                    messageImportance ?? MessageImportance.Normal,
                    message);
            }

            public override void LogWarning(string warning)
            {
                _loggingService.LogWarningFromText(
                    _buildEventContext,
                    subcategoryResourceName: null,
                    warningCode: null,
                    helpKeyword: null,
                    _buildEventFileInfo,
                    warning);
            }

            public override void LogError(string error)
            {
                HasLoggedErrors = true;

                _loggingService.LogErrorFromText(
                    _buildEventContext,
                    subcategoryResourceName: null,
                    errorCode: null,
                    helpKeyword: null,
                    _buildEventFileInfo,
                    error);
            }
        }
    }
}
