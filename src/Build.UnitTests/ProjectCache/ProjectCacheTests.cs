// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.CommandLine;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Shared;
using Microsoft.Build.Unittest;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Build.Engine.UnitTests.ProjectCache
{
    public class ProjectCacheTests : IDisposable
    {
        public ProjectCacheTests(ITestOutputHelper output)
        {
            _output = output;
            _env = TestEnvironment.Create(output);

            BuildManager.ProjectCacheDescriptors.ShouldBeEmpty();
            _env.WithInvariant(new CustomConditionInvariant(() => BuildManager.ProjectCacheDescriptors.IsEmpty));
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        private const string AssemblyMockCache = nameof(AssemblyMockCache);

        private static readonly Lazy<string> SamplePluginAssemblyPath =
            new Lazy<string>(
                () =>
                {
                    return Directory.EnumerateFiles(
                        Path.GetFullPath(
                            Path.Combine(
                                BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory,
                                "..",
                                "..",
                                "..",
                                "Samples",
                                "ProjectCachePlugin")),
                        "ProjectCachePlugin.dll",
                        SearchOption.AllDirectories).First();
                });

        public class GraphCacheResponse
        {
            private readonly IDictionary<int, string>? _extraContentPerProjectNumber;
            public const string CacheHitByProxy = nameof(CacheHitByProxy);
            public const string CacheHitByTargetResult = nameof(CacheHitByTargetResult);

            private static readonly string P2PTargets =
                @$"
                    <ItemGroup>
                        <ProjectReferenceTargets Include=`Build` Targets=`Build` />
                        <{ItemTypeNames.ProjectCachePlugin} Include=`{SamplePluginAssemblyPath.Value}` />
                    </ItemGroup>

                    <Target Name=`Build` Returns=`@(ReturnValue)`>
                        <MSBuild Projects=`@(ProjectReference)` Targets=`Build`>
                            <Output TaskParameter=`TargetOutputs` ItemName=`ReferenceReturns` />
                        </MSBuild>

                        <Message Text=`Reference: %(ReferenceReturns.Identity) : %(ReferenceReturns.File)` Importance=`High` />
                        <Error Text=`Reference file [%(ReferenceReturns.File)] does not exist` Condition=`@(ReferenceReturns->Count()) != 0 and !Exists(%(ReferenceReturns.File))` />

                        <ItemGroup>
                            <ReturnValue Include=`$(MSBuildProjectName)` File=`$(MSBuildProjectFile)` />
                        </ItemGroup>
                    </Target>

                    <Target Name=`ProxyBuild` Returns=`@(ReturnValue)`>
                        <ItemGroup>
                            <ReturnValue Include=`$(MSBuildProjectName)` File=`$(MSBuildProjectFile)` {CacheHitByProxy}=`true`/>
                        </ItemGroup>
                    </Target>";

            private Dictionary<int, int[]?> GraphEdges { get; }

            public Dictionary<int, CacheResult> NonCacheMissResults { get; }

            public GraphCacheResponse(Dictionary<int, int[]?> graphEdges, Dictionary<int, CacheResult>? nonCacheMissResults = null, IDictionary<int, string>? extraContentPerProjectNumber = null)
            {
                _extraContentPerProjectNumber = extraContentPerProjectNumber;
                GraphEdges = graphEdges;
                NonCacheMissResults = nonCacheMissResults ?? new Dictionary<int, CacheResult>();
            }

            public ProjectGraph CreateGraph(TestEnvironment env)
                => Helpers.CreateProjectGraph(
                    env,
                    GraphEdges,
                    extraContentPerProjectNumber: _extraContentPerProjectNumber,
                    extraContentForAllNodes: P2PTargets);

            public static CacheResult SuccessfulProxyTargetResult()
            {
                return CacheResult.IndicateCacheHit(
                    new ProxyTargets(
                        new Dictionary<string, string>
                        {
                            {"ProxyBuild", "Build"}
                        }));
            }

            public static CacheResult SuccessfulTargetResult(int projectNumber, string projectPath)
            {
                return CacheResult.IndicateCacheHit(
                    new[]
                    {
                        new PluginTargetResult(
                            "Build",
                            new ITaskItem2[]
                            {
                                new TaskItem(
                                    projectNumber.ToString(),
                                    new Dictionary<string, string>
                                    {
                                        {"File", projectPath},
                                        {CacheHitByTargetResult, "true"}
                                    })
                            },
                            BuildResultCode.Success)
                    });
            }

            public CacheResult GetExpectedCacheResultForNode(ProjectGraphNode node)
            {
                return GetExpectedCacheResultForProjectNumber(GetProjectNumber(node));
            }

            public CacheResult GetExpectedCacheResultForProjectNumber(int projectNumber)
            {
                return NonCacheMissResults.TryGetValue(projectNumber, out var cacheResult)
                    ? cacheResult
                    : CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
            }

            public override string ToString()
            {
                return string.Join(
                    ", ",
                    GraphEdges.Select(e => $"{Node(e.Key)}->{FormatChildren(e.Value)}"));

                string FormatChildren(int[]? children)
                {
                    return children == null
                        ? "Null"
                        : string.Join(",", children.Select(c => Node(c)));
                }

                string Node(int projectNumber)
                {
                    return $"{projectNumber}({Chr(projectNumber)})";
                }

                char Chr(int projectNumber)
                {
                    var cacheResult = GetExpectedCacheResultForProjectNumber(projectNumber);
                    return cacheResult.ResultType switch
                    {

                        CacheResultType.CacheHit => cacheResult.ProxyTargets != null
                            ? 'P'
                            : 'T',
                        CacheResultType.CacheMiss => 'M',
                        CacheResultType.CacheNotApplicable => 'N',
                        CacheResultType.None => 'E',
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }
            }
        }

        public class DelegatingMockCache : ProjectCachePluginBase
        {
            private readonly Func<BuildRequestData, PluginLoggerBase, CancellationToken, Task<CacheResult>> _getCacheResultDelegate;

            public DelegatingMockCache(Func<BuildRequestData, PluginLoggerBase, CancellationToken, Task<CacheResult>> getCacheResultDelegate)
            {
                _getCacheResultDelegate = getCacheResultDelegate;
            }

            public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override async Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                return await _getCacheResultDelegate(buildRequest, logger, cancellationToken);
            }

            public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        [Flags]
        public enum ErrorLocations
        {
            Constructor = 1 << 0,
            BeginBuildAsync = 1 << 1,
            GetCacheResultAsync = 1 << 2,
            EndBuildAsync = 1 << 3
        }

        public enum ErrorKind
        {
            Exception,
            LoggedError
        }

        public class ConfigurableMockCache : ProjectCachePluginBase
        {
            public Func<CacheContext, PluginLoggerBase, CancellationToken, Task>? BeginBuildImplementation { get; set; }
            public Func<BuildRequestData, PluginLoggerBase, CancellationToken, Task<CacheResult>>? GetCacheResultImplementation { get; set; }

            public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                return BeginBuildImplementation != null
                    ? BeginBuildImplementation(context, logger, cancellationToken)
                    : Task.CompletedTask;
            }

            public override Task<CacheResult> GetCacheResultAsync(
                BuildRequestData buildRequest,
                PluginLoggerBase logger,
                CancellationToken cancellationToken)
            {
                return GetCacheResultImplementation != null
                    ? GetCacheResultImplementation(buildRequest, logger, cancellationToken)
                    : Task.FromResult(CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable));
            }

            public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        public class InstanceMockCache : ProjectCachePluginBase
        {
            private readonly GraphCacheResponse? _testData;
            private readonly TimeSpan? _projectQuerySleepTime;
            public ConcurrentQueue<BuildRequestData> Requests { get; } = new();

            public bool BeginBuildCalled { get; set; }
            public bool EndBuildCalled { get; set; }

            private int _nextId;
            public ConcurrentQueue<int> QueryStartStops = new();

            public InstanceMockCache(GraphCacheResponse? testData = null, TimeSpan? projectQuerySleepTime = null)
            {
                _testData = testData;
                _projectQuerySleepTime = projectQuerySleepTime;
            }

            public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                logger.LogMessage("MockCache: BeginBuildAsync", MessageImportance.High);

                BeginBuildCalled = true;

                return Task.CompletedTask;
            }

            public override async Task<CacheResult> GetCacheResultAsync(
                BuildRequestData buildRequest,
                PluginLoggerBase logger,
                CancellationToken cancellationToken)
            {
                var queryId = Interlocked.Increment(ref _nextId);

                Requests.Enqueue(buildRequest);
                QueryStartStops.Enqueue(queryId);

                logger.LogMessage($"MockCache: GetCacheResultAsync for {buildRequest.ProjectFullPath}", MessageImportance.High);

                buildRequest.ProjectInstance.ShouldNotBeNull("The cache plugin expects evaluated projects.");

                if (_projectQuerySleepTime is not null)
                {
                    await Task.Delay(_projectQuerySleepTime.Value, cancellationToken);
                }

                QueryStartStops.Enqueue(queryId);

                return _testData?.GetExpectedCacheResultForProjectNumber(GetProjectNumber(buildRequest.ProjectFullPath))
                        ?? CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
            }

            public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
            {
                logger.LogMessage("MockCache: EndBuildAsync", MessageImportance.High);

                EndBuildCalled = true;

                return Task.CompletedTask;
            }

            public CacheResult GetCacheResultForNode(ProjectGraphNode node)
            {
                throw new NotImplementedException();
            }
        }

        private readonly TestEnvironment _env;

        private readonly ITestOutputHelper _output;

        public static IEnumerable<GraphCacheResponse> SuccessfulGraphs
        {
            get
            {
                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, null}
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, null}
                    },
                    new Dictionary<int, CacheResult>
                    {
                        {1, GraphCacheResponse.SuccessfulProxyTargetResult() }
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, null}
                    },
                    new Dictionary<int, CacheResult>
                    {
                        {1, GraphCacheResponse.SuccessfulTargetResult(1, "1.proj") }
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, new[] {2}}
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, new[] {2}}
                    },
                    new Dictionary<int, CacheResult>
                    {
                        {2, GraphCacheResponse.SuccessfulProxyTargetResult() }
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, new[] {2}}
                    },
                    new Dictionary<int, CacheResult>
                    {
                        {2, GraphCacheResponse.SuccessfulTargetResult(2, "2.proj") }
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, new[] {2}}
                    },
                    new Dictionary<int, CacheResult>
                    {
                        {1, GraphCacheResponse.SuccessfulProxyTargetResult() },
                        {2, GraphCacheResponse.SuccessfulTargetResult(2, "2.proj") }
                    });

                yield return new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, new[] {2, 3, 7}},
                        {2, new[] {4}},
                        {3, new[] {4}},
                        {4, new[] {5, 6, 7}}
                    });
            }
        }

        public static IEnumerable<object[]> MultiProcWithAndWithoutInProcNode
        {
            get
            {
                yield return new object[]
                {
                    new BuildParameters
                    {
                        DisableInProcNode = false,
                        MaxNodeCount = Environment.ProcessorCount
                    }
                };

                yield return new object[]
                {
                    new BuildParameters
                    {
                        DisableInProcNode = true,
                        MaxNodeCount = Environment.ProcessorCount
                    }
                };
            }
        }

        public static IEnumerable<object[]> SuccessfulGraphsWithBuildParameters
        {
            get
            {
                foreach (var graph in SuccessfulGraphs)
                {
                    foreach (var buildParameters in MultiProcWithAndWithoutInProcNode)
                    {
                        yield return new object[]
                        {
                            graph,
                            buildParameters[0]
                        };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(SuccessfulGraphsWithBuildParameters))]
        public void ProjectCacheByBuildParametersAndGraphBuildWorks(GraphCacheResponse testData, BuildParameters buildParameters)
        {
            _output.WriteLine(testData.ToString());
            var graph = testData.CreateGraph(_env);
            var mockCache = new InstanceMockCache(testData);

            // Reset the environment variables stored in the build params to take into account TestEnvironmentChanges.
            buildParameters = new BuildParameters(buildParameters, resetEnvironment: true)
            {
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache)
            };

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();

            AssertCacheBuild(graph, testData, mockCache, logger, graphResult.ResultsByNode, targets: "Build");
        }

        [Theory]
        [MemberData(nameof(SuccessfulGraphsWithBuildParameters))]
        public void ProjectCacheByBuildParametersAndBottomUpBuildWorks(GraphCacheResponse testData, BuildParameters buildParameters)
        {
            var graph = testData.CreateGraph(_env);
            var mockCache = new InstanceMockCache(testData);

            var projectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache);

            // Reset the environment variables stored in the build params to take into account TestEnvironmentChanges.
            buildParameters = new BuildParameters(buildParameters, resetEnvironment: true)
            {
                ProjectCacheDescriptor = projectCacheDescriptor
            };

            MockLogger logger;
            var nodesToBuildResults = new Dictionary<ProjectGraphNode, BuildResult>();
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;

                foreach (var node in graph.ProjectNodesTopologicallySorted)
                {
                    var buildResult = buildSession.BuildProjectFile(node.ProjectInstance.FullPath);

                    buildResult.ShouldHaveSucceeded();

                    nodesToBuildResults[node] = buildResult;
                }
            }


            AssertCacheBuild(graph, testData, mockCache, logger, nodesToBuildResults, targets: null);
        }

        [Theory]
        [MemberData(nameof(SuccessfulGraphsWithBuildParameters))]
        public void ProjectCacheByVsScenarioWorks(GraphCacheResponse testData, BuildParameters buildParameters)
        {
            (MockLogger logger, ProjectGraph graph, Dictionary<ProjectGraphNode, BuildResult> nodesToBuildResults) = BuildGraphVsScenario(testData, buildParameters);

            AssertCacheBuild(graph, testData, null, logger, nodesToBuildResults, targets: null);
        }

        [Fact]
        public void ProjectCacheByVsScenarioIgnoresSlnDisabledProjects()
        {
            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    { 1, new[] { 2 } },
                },
                extraContentPerProjectNumber: new Dictionary<int, string>()
                {
                    { 1, "<PropertyGroup> <BuildProjectInSolution>false</BuildProjectInSolution> </PropertyGroup>" },
                });

            (MockLogger logger, ProjectGraph graph, _) = BuildGraphVsScenario(testData, assertBuildResults: false);

            logger.FullLog.ShouldNotContain($"EntryPoint: {graph.GraphRoots.First().ProjectInstance.FullPath}");
            logger.FullLog.ShouldContain($"EntryPoint: {graph.GraphRoots.First().ProjectReferences.First().ProjectInstance.FullPath}");
        }

        private (MockLogger logger, ProjectGraph projectGraph, Dictionary<ProjectGraphNode, BuildResult> nodesToBuildResults) BuildGraphVsScenario(
            GraphCacheResponse testData,
            BuildParameters? buildParameters = null,
            bool assertBuildResults = true)
        {
            var nodesToBuildResults = new Dictionary<ProjectGraphNode, BuildResult>();
            MockLogger logger;
            ProjectGraph graph;

            var currentBuildEnvironment = BuildEnvironmentHelper.Instance;

            try
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(
                    new BuildEnvironment(
                        currentBuildEnvironment.Mode,
                        currentBuildEnvironment.CurrentMSBuildExePath,
                        currentBuildEnvironment.RunningTests,
                        currentBuildEnvironment.RunningInMSBuildExe,
                        runningInVisualStudio: true,
                        visualStudioPath: currentBuildEnvironment.VisualStudioInstallRootDirectory));

                // Reset the environment variables stored in the build params to take into account TestEnvironmentChanges.
                buildParameters = buildParameters is null
                    ? new BuildParameters()
                    : new BuildParameters(buildParameters, resetEnvironment: true);

                BuildManager.ProjectCacheDescriptors.ShouldBeEmpty();

                graph = testData.CreateGraph(_env);

                BuildManager.ProjectCacheDescriptors.ShouldHaveSingleItem();

                // VS sets this global property on every project it builds.
                string solutionConfigurationGlobalProperty = CreateSolutionConfigurationProperty(graph.ProjectNodes);

                using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
                {
                    logger = buildSession.Logger;

                    foreach (var node in graph.ProjectNodesTopologicallySorted)
                    {
                        BuildResult buildResult = buildSession.BuildProjectFile(
                            node.ProjectInstance.FullPath,
                            globalProperties:
                                new Dictionary<string, string>
                                {
                                    { SolutionProjectGenerator.CurrentSolutionConfigurationContents, solutionConfigurationGlobalProperty },
                                    { "TargetFramework", "net472"},
                                });

                        if (assertBuildResults)
                        {
                            buildResult.ShouldHaveSucceeded();
                        }

                        nodesToBuildResults[node] = buildResult;
                    }
                }

                if (assertBuildResults)
                {
                    foreach (var node in graph.ProjectNodes)
                    {
                        var projectPath = node.ProjectInstance.FullPath;
                        var projectName = Path.GetFileNameWithoutExtension(projectPath);

                        // Ensure MSBuild passes config / platform information set by VS.
                        logger.FullLog.ShouldContain($"EntryPoint: {projectPath}");
                        logger.FullLog.ShouldContain($"Configuration:{projectName}Debug");
                        logger.FullLog.ShouldContain($"Platform:{projectName}x64");

                        // Ensure MSBuild removes the target framework if present.
                        logger.FullLog.ShouldNotContain("TargetFramework:net472");
                    }
                }
            }
            finally
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(currentBuildEnvironment);
                BuildManager.ProjectCacheDescriptors.Clear();
            }

            return (logger, graph, nodesToBuildResults);
        }

        private static string CreateSolutionConfigurationProperty(IReadOnlyCollection<ProjectGraphNode> projectNodes)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<SolutionConfiguration>");

            foreach (var node in projectNodes)
            {
                var projectPath = node.ProjectInstance.FullPath;
                var projectName = Path.GetFileNameWithoutExtension(projectPath);

                var buildProjectInSolutionValue = node.ProjectInstance.GetPropertyValue("BuildProjectInSolution");
                var buildProjectInSolutionAttribute = string.IsNullOrWhiteSpace(buildProjectInSolutionValue)
                    ? string.Empty
                    : $"BuildProjectInSolution=\"{buildProjectInSolutionValue}\"";

                var projectDependencyValue = node.ProjectInstance.GetPropertyValue("ProjectDependency");
                var projectDependencyElement = string.IsNullOrWhiteSpace(projectDependencyValue)
                    ? string.Empty
                    : $"<ProjectDependency Project=\"{projectDependencyValue}\" />";

                sb.AppendLine($"<ProjectConfiguration Project=\"{Guid.NewGuid()}\" AbsolutePath=\"{projectPath}\" {buildProjectInSolutionAttribute}>{projectName}Debug|{projectName}x64{projectDependencyElement}</ProjectConfiguration>");
            }

            sb.AppendLine("</SolutionConfiguration>");

            return sb.ToString();
        }

        [Fact]
        public void DesignTimeBuildsDuringVsScenarioShouldDisableTheCache()
        {
            var currentBuildEnvironment = BuildEnvironmentHelper.Instance;

            // Use a few references to stress test the design time build workaround logic.
            var referenceNumbers = Enumerable.Range(2, NativeMethodsShared.GetLogicalCoreCount()).ToArray();

            var testData = new GraphCacheResponse(
                graphEdges: new Dictionary<int, int[]?>
                {
                    {1, referenceNumbers}
                });

            try
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(
                    new BuildEnvironment(
                        currentBuildEnvironment.Mode,
                        currentBuildEnvironment.CurrentMSBuildExePath,
                        currentBuildEnvironment.RunningTests,
                        currentBuildEnvironment.RunningInMSBuildExe,
                        runningInVisualStudio: true,
                        visualStudioPath: currentBuildEnvironment.VisualStudioInstallRootDirectory));

                var graph = testData.CreateGraph(_env);

                var rootNode = graph.GraphRoots.First();

                var globalProperties = new Dictionary<string, string>
                {
                    { DesignTimeProperties.DesignTimeBuild, "true" },
                };

                MockLogger logger;
                using (var buildSession = new Helpers.BuildManagerSession(_env))
                {
                    logger = buildSession.Logger;

                    // Build references in parallel.
                    var referenceBuildTasks = rootNode.ProjectReferences.Select(
                        r => buildSession.BuildProjectFileAsync(r.ProjectInstance.FullPath, globalProperties: globalProperties));

                    foreach (var task in referenceBuildTasks)
                    {
                        var buildResult = task.Result;
                        buildResult.ShouldHaveSucceeded();
                    }

                    buildSession
                        .BuildProjectFile(rootNode.ProjectInstance.FullPath, globalProperties: globalProperties)
                        .ShouldHaveSucceeded();
                }

                // Cache doesn't get initialized, queried, or disposed.
                logger.FullLog.ShouldNotContain("BeginBuildAsync");
                logger.FullLog.ShouldNotContain("GetCacheResultAsync for");
                logger.FullLog.ShouldNotContain("Querying project cache for project");
                logger.FullLog.ShouldNotContain("EndBuildAsync");
            }
            finally
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(currentBuildEnvironment);
                BuildManager.ProjectCacheDescriptors.Clear();
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RunningProxyBuildsOnOutOfProcNodesShouldIssueWarning(bool disableInprocNodeViaEnvironmentVariable)
        {
            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, new[] {2}}
                },
                new Dictionary<int, CacheResult>
                {
                    {1, GraphCacheResponse.SuccessfulProxyTargetResult() },
                    {2, GraphCacheResponse.SuccessfulProxyTargetResult() }
                });

            var graph = testData.CreateGraph(_env);
            var mockCache = new InstanceMockCache(testData);

            var buildParameters = new BuildParameters
            {
                MaxNodeCount = Environment.ProcessorCount,
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache)
            };

            if (disableInprocNodeViaEnvironmentVariable)
            {
                _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");
            }
            else
            {
                buildParameters.DisableInProcNode = true;
            }

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();

            logger.AssertMessageCount("MSB4274", 1);
        }

        private void AssertCacheBuild(
            ProjectGraph graph,
            GraphCacheResponse testData,
            InstanceMockCache? instanceMockCache,
            MockLogger mockLogger,
            IReadOnlyDictionary<ProjectGraphNode, BuildResult> projectPathToBuildResults,
            string? targets)
        {
            if (instanceMockCache != null)
            {
                instanceMockCache.BeginBuildCalled.ShouldBeTrue();
                instanceMockCache.Requests.Count.ShouldBe(graph.ProjectNodes.Count);
                instanceMockCache.EndBuildCalled.ShouldBeTrue();
            }
            else
            {
                mockLogger.FullLog.ShouldContain($"{AssemblyMockCache}: BeginBuildAsync");
                Regex.Matches(mockLogger.FullLog, $"{AssemblyMockCache}: GetCacheResultAsync for").Count.ShouldBe(graph.ProjectNodes.Count);
                mockLogger.FullLog.ShouldContain($"{AssemblyMockCache}: EndBuildAsync");
            }

            foreach (var node in graph.ProjectNodes)
            {
                if (string.IsNullOrEmpty(targets))
                {
                    mockLogger.FullLog.ShouldContain(string.Format(ResourceUtilities.GetResourceString("ProjectCacheQueryStartedWithDefaultTargets"), node.ProjectInstance.FullPath));
                }
                else
                {
                    mockLogger.FullLog.ShouldContain(string.Format(ResourceUtilities.GetResourceString("ProjectCacheQueryStartedWithTargetNames"), node.ProjectInstance.FullPath, targets));
                }

                if (instanceMockCache != null)
                {
                    instanceMockCache.Requests.ShouldContain(r => r.ProjectFullPath.Equals(node.ProjectInstance.FullPath));

                    var expectedCacheResponse = testData.GetExpectedCacheResultForNode(node);
                    switch (expectedCacheResponse.ResultType)
                    {
                        case CacheResultType.CacheHit:
                            AssertBuildResultForCacheHit(node.ProjectInstance.FullPath, projectPathToBuildResults[node], expectedCacheResponse);
                            break;
                        case CacheResultType.CacheMiss:
                            break;
                        case CacheResultType.CacheNotApplicable:
                            break;
                        case CacheResultType.None:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    mockLogger.FullLog.ShouldContain($"{AssemblyMockCache}: GetCacheResultAsync for {node.ProjectInstance.FullPath}");

                    // Too complicated, not worth it to send expected results to the assembly plugin, so skip checking the build results.
                }
            }
        }

        private static int GetProjectNumber(ProjectGraphNode node) => GetProjectNumber(node.ProjectInstance.FullPath);

        private static int GetProjectNumber(string projectPath) => int.Parse(Path.GetFileNameWithoutExtension(projectPath));

        private void AssertBuildResultForCacheHit(
            string projectPath,
            BuildResult buildResult,
            CacheResult expectedCacheResponse)
        {
            // If the cache hit is via proxy targets then the build result should contain entry for both the real target
            // and the proxy target. Both target results should be the same.
            // If it's not a cache result by proxy targets then the cache constructed the target results by hand and only the real target result
            // exists in the BuildResult.

            var targetResult = buildResult.ResultsByTarget!["Build"];

            targetResult.Items.ShouldHaveSingleItem();
            var itemResult = targetResult.Items.First();
            string expectedMetadata;

            if (expectedCacheResponse.ProxyTargets != null)
            {
                var proxyTargetResult = buildResult.ResultsByTarget["ProxyBuild"];
                SdkUtilities.EngineHelpers.AssertTargetResultsEqual(targetResult, proxyTargetResult);

                expectedMetadata = GraphCacheResponse.CacheHitByProxy;
            }
            else
            {
                expectedMetadata = GraphCacheResponse.CacheHitByTargetResult;
            }

            itemResult.ItemSpec.ShouldBe(GetProjectNumber(projectPath).ToString());
            itemResult.GetMetadata("File").ShouldBe(Path.GetFileName(projectPath));
            itemResult.GetMetadata(expectedMetadata).ShouldBe("true");
        }

        [Theory]
        [MemberData(nameof(MultiProcWithAndWithoutInProcNode))]
        public void CacheShouldNotGetQueriedForNestedBuildRequests(BuildParameters buildParameters)
        {
            var project1 = _env.CreateFile("1.proj", @"
                    <Project>
                        <Target Name=`Build`>
                            <MSBuild Projects=`2.proj` />
                        </Target>
                    </Project>".Cleanup());

            _env.CreateFile("2.proj", @"
                    <Project>
                        <Target Name=`Build`>
                            <Message Text=`Hello` Importance=`High` />
                        </Target>
                    </Project>".Cleanup());

            var mockCache = new InstanceMockCache();
            buildParameters.ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache);

            MockLogger logger;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;

                BuildResult buildResult = buildSession.BuildProjectFile(project1.Path);

                buildResult.ShouldHaveSucceeded();
            }

            logger.ProjectStartedEvents.Count.ShouldBe(2);

            mockCache.Requests.Count.ShouldBe(1);
            mockCache.Requests.First().ProjectFullPath.ShouldEndWith("1.proj");
        }

        [Fact]
        public void CacheViaBuildParametersCanDiscoverAndLoadPluginFromAssembly()
        {
            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, new[] {2, 3}}
                });

            var graph = testData.CreateGraph(_env);

            var buildParameters = new BuildParameters
            {
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromAssemblyPath(SamplePluginAssemblyPath.Value)
            };

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();

            logger.FullLog.ShouldContain($"Loading the following project cache plugin: {AssemblyMockCache}");

            AssertCacheBuild(graph, testData, null, logger, graphResult.ResultsByNode, targets: "Build");
        }

        [Fact]
        public void GraphBuildCanDiscoverAndLoadPluginFromAssembly()
        {
            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, new[] {2, 3}}
                });

            var graph = testData.CreateGraph(_env);

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();

            AssertCacheBuild(graph, testData, null, logger, graphResult.ResultsByNode, targets: "Build");
        }

        [Fact]
        public void BuildFailsWhenCacheBuildResultIsWrong()
        {
            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, new[] {2}}
                },
                new Dictionary<int, CacheResult>
                {
                    {
                        2, CacheResult.IndicateCacheHit(
                            new[]
                            {
                                new PluginTargetResult(
                                    "Build",
                                    new ITaskItem2[]
                                    {
                                        new TaskItem(
                                            "NA",
                                            new Dictionary<string, string>
                                            {
                                                {"File", "Invalid file"}
                                            })
                                    },
                                    BuildResultCode.Success)
                            })
                    }
                });

            var graph = testData.CreateGraph(_env);
            var mockCache = new InstanceMockCache(testData);

            var buildParameters = new BuildParameters
            {
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache)
            };

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            mockCache.Requests.Count.ShouldBe(2);

            graphResult.ResultsByNode.First(r => GetProjectNumber(r.Key) == 2).Value.ShouldHaveSucceeded();
            graphResult.ResultsByNode.First(r => GetProjectNumber(r.Key) == 1).Value.ShouldHaveFailed();

            graphResult.ShouldHaveFailed();

            logger.FullLog.ShouldContain("Reference file [Invalid file] does not exist");
        }

        [Fact]
        public void MultiplePlugins()
        {
            // One from the project, one from BuildParameters.
            var graph = Helpers.CreateProjectGraph(
                _env,
                new Dictionary<int, int[]?>
                {
                    { 1, new[] { 2 } },
                },
                extraContentForAllNodes: @$"
<ItemGroup>
   <{ItemTypeNames.ProjectCachePlugin} Include='{SamplePluginAssemblyPath.Value}' />
</ItemGroup>
");
            var mockCache = new InstanceMockCache();

            var buildParameters = new BuildParameters
            {
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(mockCache),
            };

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();
        }

        [Fact]
        public void NotAllNodesDefineAPlugin()
        {
            var graph = Helpers.CreateProjectGraph(
                _env,
                dependencyEdges: new Dictionary<int, int[]?>
                {
                    { 1, new[] { 2 } },
                },
                extraContentPerProjectNumber: new Dictionary<int, string>
                {
                    {
                        2,
                        @$"
<ItemGroup>
   <{ItemTypeNames.ProjectCachePlugin} Include='{SamplePluginAssemblyPath.Value}' />
</ItemGroup>
"
                    }
                });

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();
        }

        public static IEnumerable<object[]> CacheExceptionLocationsTestData
        {
            get
            {
                // Plugin constructors cannot log errors, they can only throw exceptions.
                yield return new object[] { ErrorLocations.Constructor, ErrorKind.Exception };

                foreach (var errorKind in new[] { ErrorKind.Exception, ErrorKind.LoggedError })
                {
                    yield return new object[] { ErrorLocations.BeginBuildAsync, errorKind };
                    yield return new object[] { ErrorLocations.BeginBuildAsync | ErrorLocations.GetCacheResultAsync, errorKind };
                    yield return new object[] { ErrorLocations.BeginBuildAsync | ErrorLocations.GetCacheResultAsync | ErrorLocations.EndBuildAsync, errorKind };
                    yield return new object[] { ErrorLocations.BeginBuildAsync | ErrorLocations.EndBuildAsync, errorKind };

                    yield return new object[] { ErrorLocations.GetCacheResultAsync, errorKind };
                    yield return new object[] { ErrorLocations.GetCacheResultAsync | ErrorLocations.EndBuildAsync, errorKind };

                    yield return new object[] { ErrorLocations.EndBuildAsync, errorKind };
                }
            }
        }

        [Theory]
        [MemberData(nameof(CacheExceptionLocationsTestData))]
        public void EngineShouldHandleExceptionsFromCachePluginViaBuildParameters(ErrorLocations errorLocations, ErrorKind errorKind)
        {
            SetEnvironmentForErrorLocations(errorLocations, errorKind);

            var project = _env.CreateFile("1.proj", @$"
                    <Project>
                        <Target Name=`Build`>
                            <Message Text=`Hello World` Importance=`High` />
                        </Target>
                    </Project>".Cleanup());

            Helpers.BuildManagerSession? buildSession = null;
            MockLogger logger;

            try
            {
                buildSession = new Helpers.BuildManagerSession(
                    _env,
                    new BuildParameters
                    {
                        UseSynchronousLogging = true,
                        ProjectCacheDescriptor = ProjectCacheDescriptor.FromAssemblyPath(SamplePluginAssemblyPath.Value)
                    });

                logger = buildSession.Logger;
                var buildResult = buildSession.BuildProjectFile(project.Path);

                // Plugin construction, initialization, and query all end up throwing in BuildManager.ExecuteSubmission and thus
                // mark the submission as failed with exception.
                var exceptionsThatEndUpInBuildResult =
                    ErrorLocations.Constructor | ErrorLocations.BeginBuildAsync | ErrorLocations.GetCacheResultAsync;

                if ((exceptionsThatEndUpInBuildResult & errorLocations) != 0)
                {
                    buildResult.Exception.ShouldNotBeNull();
                    buildResult.Exception.ShouldBeOfType<ProjectCacheException>();

                    if (errorKind == ErrorKind.Exception)
                    {
                        buildResult.Exception.InnerException!.ShouldNotBeNull();
                        buildResult.Exception.InnerException!.Message.ShouldContain("Cache plugin exception from");
                    }
                    else
                    {
                        buildResult.Exception.InnerException.ShouldBeNull();
                    }
                }

                // BuildManager.EndBuild calls plugin.EndBuild, so if only plugin.EndBuild fails it means everything else passed,
                // so the build submission should be successful.
                if (errorLocations == ErrorLocations.EndBuildAsync)
                {
                    buildResult.ShouldHaveSucceeded();
                }
                else
                {
                    buildResult.ShouldHaveFailed();
                }
            }
            finally
            {
                // These exceptions prevent the creation of a plugin so there's no plugin to shutdown.
                var exceptionsThatPreventEndBuildFromThrowing = ErrorLocations.Constructor |
                                                                ErrorLocations.BeginBuildAsync;

                if ((errorLocations & exceptionsThatPreventEndBuildFromThrowing) != 0 ||
                    !errorLocations.HasFlag(ErrorLocations.EndBuildAsync))
                {
                    Should.NotThrow(() => buildSession!.Dispose());
                }
                else if (errorLocations.HasFlag(ErrorLocations.EndBuildAsync))
                {
                    var e = Should.Throw<ProjectCacheException>(() => buildSession!.Dispose());

                    if (errorKind == ErrorKind.Exception)
                    {
                        e.InnerException!.ShouldNotBeNull();
                        e.InnerException!.Message.ShouldContain("Cache plugin exception from EndBuildAsync");
                    }
                    else
                    {
                        e.InnerException.ShouldBeNull();
                    }
                }
                else
                {
                    throw new NotImplementedException();
                }
                buildSession?.Dispose();
            }

            logger.BuildFinishedEvents.First().Succeeded.ShouldBeFalse();

            // Plugin query must happen after plugin init. So if plugin init fails, then the plugin should not get queried.
            var exceptionsThatShouldPreventCacheQueryAndEndBuildAsync = ErrorLocations.Constructor | ErrorLocations.BeginBuildAsync;

            if ((exceptionsThatShouldPreventCacheQueryAndEndBuildAsync & errorLocations) != 0)
            {
                logger.FullLog.ShouldNotContain($"{AssemblyMockCache}: GetCacheResultAsync for");
                logger.FullLog.ShouldNotContain($"{AssemblyMockCache}: EndBuildAsync");
            }
            else
            {
                StringShouldContainSubstring(logger.FullLog, $"{AssemblyMockCache}: GetCacheResultAsync for", expectedOccurrences: 1);
                StringShouldContainSubstring(logger.FullLog, $"{AssemblyMockCache}: EndBuildAsync", expectedOccurrences: 1);
            }

            logger.FullLog.ShouldNotContain("Cache plugin exception from");

            if (errorKind == ErrorKind.LoggedError)
            {
                logger.FullLog.ShouldContain("Cache plugin logged error from");
            }
        }

        [Theory]
        [MemberData(nameof(CacheExceptionLocationsTestData))]
        public void EngineShouldHandleExceptionsFromCachePluginViaGraphBuild(ErrorLocations errorLocations, ErrorKind errorKind)
        {
            const ErrorLocations exceptionsThatShouldPreventCacheQueryAndEndBuildAsync = ErrorLocations.Constructor | ErrorLocations.BeginBuildAsync;

            SetEnvironmentForErrorLocations(errorLocations, errorKind);

            var graph = Helpers.CreateProjectGraph(
                _env,
                new Dictionary<int, int[]?>
                {
                    {1, new []{2}}
                },
                extraContentForAllNodes: @$"
<ItemGroup>
    <{ItemTypeNames.ProjectCachePlugin} Include=`{SamplePluginAssemblyPath.Value}` />
    <{ItemTypeNames.ProjectReferenceTargets} Include=`Build` Targets=`Build` />
</ItemGroup>
<Target Name=`Build`>
    <Message Text=`Hello World` Importance=`High` />
</Target>
");

            using var buildSession = new Helpers.BuildManagerSession(
                _env,
                new BuildParameters
                {
                    UseSynchronousLogging = true,
                    MaxNodeCount = 1
                });

            var logger = buildSession.Logger;

            GraphBuildResult? buildResult = null;

            try
            {
                buildResult = buildSession.BuildGraph(graph);

                if (!errorLocations.HasFlag(ErrorLocations.Constructor))
                {
                    logger.FullLog.ShouldContain("Loading the following project cache plugin:");
                }

                // EndBuildAsync isn't until the build manager is shut down, so the build result itself is successful if that's the only error.
                if (errorLocations == ErrorLocations.EndBuildAsync)
                {
                    buildResult.ShouldHaveSucceeded();
                }
                else
                {
                    buildResult.ShouldHaveFailed();

                    buildResult.Exception.ShouldBeOfType<ProjectCacheException>();

                    if (errorKind == ErrorKind.Exception)
                    {
                        buildResult.Exception.InnerException!.ShouldNotBeNull();
                        buildResult.Exception.InnerException!.Message.ShouldContain("Cache plugin exception from");
                    }

                    logger.FullLog.ShouldNotContain("Cache plugin exception from");

                    if (errorKind == ErrorKind.LoggedError)
                    {
                        logger.FullLog.ShouldContain("Cache plugin logged error from");
                    }
                }
            }
            finally
            {
                if (errorLocations.HasFlag(ErrorLocations.EndBuildAsync)
                    && (exceptionsThatShouldPreventCacheQueryAndEndBuildAsync & errorLocations) == 0)
                {
                    Should.Throw<ProjectCacheException>(() => buildSession.Dispose());
                }
                else
                {
                    Should.NotThrow(() => buildSession.Dispose());
                }
            }

            logger.BuildFinishedEvents.First().Succeeded.ShouldBeFalse();

            if ((exceptionsThatShouldPreventCacheQueryAndEndBuildAsync & errorLocations) != 0)
            {
                logger.FullLog.ShouldNotContain($"{AssemblyMockCache}: GetCacheResultAsync for");
                logger.FullLog.ShouldNotContain($"{AssemblyMockCache}: EndBuildAsync");
            }
            else
            {
                // There's two projects, so there should be two cache queries logged ... unless a cache queries throws an exception. That ends the build.
                var expectedQueryOccurrences = errorLocations.HasFlag(ErrorLocations.GetCacheResultAsync)
                    ? 1
                    : 2;

                StringShouldContainSubstring(logger.FullLog, $"{AssemblyMockCache}: GetCacheResultAsync for", expectedQueryOccurrences);

                StringShouldContainSubstring(logger.FullLog, $"{AssemblyMockCache}: EndBuildAsync", expectedOccurrences: 1);
            }
        }

        [Fact]
        public void EndBuildShouldGetCalledOnceWhenItThrowsExceptionsFromGraphBuilds()
        {
            var project = _env.CreateFile(
                "1.proj",
                @$"
                    <Project>
                        <ItemGroup>
                            <{ItemTypeNames.ProjectCachePlugin} Include=`{SamplePluginAssemblyPath.Value}` />
                        </ItemGroup>
                        <Target Name=`Build`>
                            <Message Text=`Hello EngineShouldHandleExceptionsFromCachePlugin` Importance=`High` />
                        </Target>
                    </Project>".Cleanup());

            SetEnvironmentForErrorLocations(ErrorLocations.EndBuildAsync, ErrorKind.Exception);

            var buildParameters = new BuildParameters
            {
                UseSynchronousLogging = true
            };

            using var buildSession = new Helpers.BuildManagerSession(_env, buildParameters);
            GraphBuildResult graphResult = buildSession.BuildGraph(new ProjectGraph(project.Path));

            Should.Throw<ProjectCacheException>(() => buildSession.Dispose()).InnerException!.Message.ShouldContain("Cache plugin exception from EndBuildAsync");

            StringShouldContainSubstring(buildSession.Logger.FullLog, $"{nameof(AssemblyMockCache)}: EndBuildAsync", expectedOccurrences: 1);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void CacheShouldBeQueriedInParallelDuringGraphBuilds(bool useSynchronousLogging, bool disableInprocNode)
        {
            var referenceNumbers = new[] { 2, 3, 4 };

            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, referenceNumbers}
                },
                referenceNumbers.ToDictionary(k => k, k => GraphCacheResponse.SuccessfulProxyTargetResult()));

            var graph = testData.CreateGraph(_env);

            var completedCacheRequests = new ConcurrentBag<int>();
            var task2Completion = new TaskCompletionSource<bool>();
            task2Completion.Task.IsCompleted.ShouldBeFalse();

            var cache = new DelegatingMockCache(
                async (buildRequest, _, _) =>
                {
                    var projectNumber = GetProjectNumber(buildRequest.ProjectFullPath);

                    try
                    {
                        if (projectNumber == 2)
                        {
                            await task2Completion.Task;
                        }

                        return testData.GetExpectedCacheResultForProjectNumber(projectNumber);
                    }
                    finally
                    {
                        completedCacheRequests.Add(projectNumber);
                    }
                });

            var buildParameters = new BuildParameters()
            {
                MaxNodeCount = NativeMethodsShared.GetLogicalCoreCount(),
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(cache),
                UseSynchronousLogging = useSynchronousLogging,
                DisableInProcNode = disableInprocNode
            };

            using var buildSession = new Helpers.BuildManagerSession(_env, buildParameters);

            var task2 = BuildProjectFileAsync(2);
            var task3 = BuildProjectFileAsync(3);
            var task4 = BuildProjectFileAsync(4);

            task3.Result.ShouldHaveSucceeded();
            completedCacheRequests.ShouldContain(3);
            task4.Result.ShouldHaveSucceeded();
            completedCacheRequests.ShouldContain(4);

            // task 2 hasn't been instructed to finish yet
            task2.IsCompleted.ShouldBeFalse();
            completedCacheRequests.ShouldNotContain(2);

            task2Completion.SetResult(true);

            task2.Result.ShouldHaveSucceeded();
            completedCacheRequests.ShouldContain(2);

            var task1 = BuildProjectFileAsync(1);
            task1.Result.ShouldHaveSucceeded();
            completedCacheRequests.ShouldContain(1);

            Task<BuildResult> BuildProjectFileAsync(int projectNumber)
            {
                return buildSession.BuildProjectFileAsync(graph.ProjectNodes.First(n => GetProjectNumber(n) == projectNumber).ProjectInstance.FullPath);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void ParallelStressTestForVsScenario(bool useSynchronousLogging, bool disableInprocNode)
        {
            var currentBuildEnvironment = BuildEnvironmentHelper.Instance;

            try
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(
                    new BuildEnvironment(
                        currentBuildEnvironment.Mode,
                        currentBuildEnvironment.CurrentMSBuildExePath,
                        currentBuildEnvironment.RunningTests,
                        currentBuildEnvironment.RunningInMSBuildExe,
                        runningInVisualStudio: true,
                        visualStudioPath: currentBuildEnvironment.VisualStudioInstallRootDirectory));

                BuildManager.ProjectCacheDescriptors.ShouldBeEmpty();

                var referenceNumbers = Enumerable.Range(2, NativeMethodsShared.GetLogicalCoreCount() * 2).ToArray();

                var testData = new GraphCacheResponse(
                    new Dictionary<int, int[]?>
                    {
                        {1, referenceNumbers}
                    },
                    referenceNumbers.ToDictionary(k => k, k => GraphCacheResponse.SuccessfulProxyTargetResult()));

                var graph = testData.CreateGraph(_env);

                BuildManager.ProjectCacheDescriptors.ShouldHaveSingleItem();

                var solutionConfigurationGlobalProperty = CreateSolutionConfigurationProperty(graph.ProjectNodes);

                var buildParameters = new BuildParameters
                {
                    MaxNodeCount = NativeMethodsShared.GetLogicalCoreCount(),
                    UseSynchronousLogging = useSynchronousLogging,
                    DisableInProcNode = disableInprocNode
                };

                MockLogger logger;
                var buildResultTasks = new List<Task<BuildResult>>();
                using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
                {
                    logger = buildSession.Logger;

                    foreach (var node in graph.ProjectNodes.Where(n => referenceNumbers.Contains(GetProjectNumber(n))))
                    {
                        Task<BuildResult> buildResultTask = buildSession.BuildProjectFileAsync(
                            node.ProjectInstance.FullPath,
                            globalProperties:
                            new Dictionary<string, string>
                            {
                                { SolutionProjectGenerator.CurrentSolutionConfigurationContents, solutionConfigurationGlobalProperty }
                            });

                        buildResultTasks.Add(buildResultTask);
                    }

                    foreach (var buildResultTask in buildResultTasks)
                    {
                        buildResultTask.Result.ShouldHaveSucceeded();
                    }

                    buildSession.BuildProjectFile(graph.GraphRoots.First().ProjectInstance.FullPath).ShouldHaveSucceeded();
                }

                StringShouldContainSubstring(logger.FullLog, $"{AssemblyMockCache}: GetCacheResultAsync for", graph.ProjectNodes.Count);
            }
            finally
            {
                BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly(currentBuildEnvironment);
                BuildManager.ProjectCacheDescriptors.Clear();
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void ParallelStressTest(bool useSynchronousLogging, bool disableInprocNode)
        {
            var referenceNumbers = Enumerable.Range(2, NativeMethodsShared.GetLogicalCoreCount() * 2).ToArray();

            var testData = new GraphCacheResponse(
                new Dictionary<int, int[]?>
                {
                    {1, referenceNumbers}
                },
                referenceNumbers.ToDictionary(k => k, k => GraphCacheResponse.SuccessfulProxyTargetResult()));

            var graph = testData.CreateGraph(_env);
            var cache = new InstanceMockCache(testData, TimeSpan.FromMilliseconds(50));

            var buildParameters = new BuildParameters()
            {
                MaxNodeCount = NativeMethodsShared.GetLogicalCoreCount(),
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(cache),
                UseSynchronousLogging = useSynchronousLogging,
                DisableInProcNode = disableInprocNode
            };

            MockLogger logger;
            GraphBuildResult graphResult;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;
                graphResult = buildSession.BuildGraph(graph);
            }

            graphResult.ShouldHaveSucceeded();
            cache.QueryStartStops.Count.ShouldBe(graph.ProjectNodes.Count * 2);
        }

        [Fact]
        // Schedules different requests for the same BuildRequestConfiguration in parallel.
        // The first batch of the requests are cache misses, the second batch are cache hits via proxy builds.
        // The first batch is delayed so it starts intermingling with the second batch.
        // This test ensures that scheduling proxy builds on the inproc node works nicely within the Scheduler
        // if the BuildRequestConfigurations for those proxy builds have built before (or are still building) on
        // the out of proc node.
        // More details: https://github.com/dotnet/msbuild/pull/6635
        public void ProxyCacheHitsOnPreviousCacheMissesShouldWork()
        {
            var cacheNotApplicableTarget = "NATarget";
            var cacheHitTarget = "CacheHitTarget";
            var proxyTarget = "ProxyTarget";

            var project =
@$"
<Project>
    <Target Name='{cacheNotApplicableTarget}'>
        <Exec Command=`{Helpers.GetSleepCommand(TimeSpan.FromMilliseconds(200))}` />
        <Message Text='{cacheNotApplicableTarget} in $(MSBuildThisFile)' />
    </Target>

    <Target Name='{cacheHitTarget}'>
        <Message Text='{cacheHitTarget} in $(MSBuildThisFile)' />
    </Target>

    <Target Name='{proxyTarget}'>
        <Message Text='{proxyTarget} in $(MSBuildThisFile)' />
    </Target>
</Project>
".Cleanup();

            var projectPaths = Enumerable.Range(0, NativeMethodsShared.GetLogicalCoreCount())
                .Select(i => _env.CreateFile($"project{i}.proj", project).Path)
                .ToArray();

            var cacheHitCount = 0;
            var nonCacheHitCount = 0;

            var buildParameters = new BuildParameters
            {
                ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(
                    new ConfigurableMockCache
                    {
                        GetCacheResultImplementation = (request, _, _) =>
                        {
                            var projectFile = request.ProjectFullPath;

                            if (request.TargetNames.Contains(cacheNotApplicableTarget))
                            {
                                Interlocked.Increment(ref nonCacheHitCount);
                                return Task.FromResult(CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable));
                            }
                            else
                            {
                                Interlocked.Increment(ref cacheHitCount);
                                return Task.FromResult(
                                    CacheResult.IndicateCacheHit(
                                        new ProxyTargets(new Dictionary<string, string> { { proxyTarget, cacheHitTarget } })));
                            }
                        }
                    }),
                MaxNodeCount = NativeMethodsShared.GetLogicalCoreCount()
            };

            MockLogger logger;
            using (var buildSession = new Helpers.BuildManagerSession(_env, buildParameters))
            {
                logger = buildSession.Logger;

                var buildRequests = new List<(string, string)>();
                buildRequests.AddRange(projectPaths.Select(r => (r, cacheNotApplicableTarget)));
                buildRequests.AddRange(projectPaths.Select(r => (r, cacheHitTarget)));

                var buildTasks = new List<Task<BuildResult>>();
                foreach (var (projectPath, target) in buildRequests)
                {
                    buildTasks.Add(buildSession.BuildProjectFileAsync(projectPath, new[] { target }));
                }

                foreach (var buildResult in buildTasks.Select(buildTask => buildTask.Result))
                {
                    buildResult.Exception.ShouldBeNull();
                    buildResult.ShouldHaveSucceeded();
                }
            }

            logger.ProjectStartedEvents.Count.ShouldBe(2 * projectPaths.Length);

            cacheHitCount.ShouldBe(projectPaths.Length);
            nonCacheHitCount.ShouldBe(projectPaths.Length);
        }

        private static void StringShouldContainSubstring(string aString, string substring, int expectedOccurrences)
        {
            aString.ShouldContain(substring);
            Regex.Matches(aString, substring).Count.ShouldBe(expectedOccurrences);
        }

        private void SetEnvironmentForErrorLocations(ErrorLocations errorLocations, ErrorKind errorKind)
        {
            foreach (var enumValue in Enum.GetValues(typeof(ErrorLocations)))
            {
                var typedValue = (ErrorLocations)enumValue;
                if (errorLocations.HasFlag(typedValue))
                {
                    var exceptionLocation = typedValue.ToString();
                    _env.SetEnvironmentVariable(exceptionLocation, errorKind.ToString());
                    _output.WriteLine($"Set exception location: {exceptionLocation}");
                }
            }
        }

        [DotNetOnlyFact("The netfx bootstrap layout created with 'dotnet build' is incomplete")]
        /// <summary>
        /// https://github.com/dotnet/msbuild/issues/5334
        /// </summary>
        public void EmbeddedResourcesFileCompileCache()
        {
            var directory = _env.CreateFolder();
            string content = ObjectModelHelpers.CleanupFileContents(
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <OutputPath>bin/</OutputPath>
                </PropertyGroup>
                <ItemGroup>
                    <EmbeddedResource Include="*.txt"/>
                </ItemGroup>
            </Project>
            """);
            var projectPath = directory.CreateFile("app.csproj", content).Path;
            directory.CreateFile("Program.cs",
            """
            using System;
            using System.IO;
            using System.Reflection;

            class Program
            {
                static void Main()
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceNames = assembly.GetManifestResourceNames();

                    foreach (var resourceName in resourceNames)
                    {
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        using (var reader = new StreamReader(stream))
                        {
                            var content = reader.ReadToEnd();
                            Console.WriteLine($"Content of {resourceName}:");
                            Console.WriteLine(content);
                        }
                    }
                }
            }
            """);

            // Create EmbeddedResources file
            var file1 = directory.CreateFile("File1.txt", "A=1");
            var file2 = directory.CreateFile("File2.txt", "B=1");

            // Build and run the project
            string output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectPath} -restore", out bool success);
            success.ShouldBeTrue(output);
            output = RunnerUtilities.RunProcessAndGetOutput(Path.Combine(directory.Path, "bin/net8.0/app"), "", out success, false, _output);
            output.ShouldContain("A=1");
            output.ShouldContain("B=1");

            // Delete a file and build
            FileUtilities.DeleteNoThrow(file1.Path);
            output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectPath}", out success);
            success.ShouldBeTrue(output);
            output = RunnerUtilities.RunProcessAndGetOutput(Path.Combine(directory.Path, "bin/net8.0/app"), "", out success, false, _output);
            output.ShouldNotContain("A=1");
            output.ShouldContain("B=1");
        }
    }
}
