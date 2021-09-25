// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Unittest;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

using static Microsoft.Build.Engine.UnitTests.BackEnd.SdkResolverService_Tests;
using static Microsoft.Build.UnitTests.ObjectModelHelpers;

namespace Microsoft.Build.UnitTests.BackEnd
{
    public class SdkResultOutOfProc_Tests : IDisposable
    {
        /// <summary>
        /// The mock logger for testing.
        /// </summary>
        private readonly MockLogger _logger;

        /// <summary>
        /// The standard build manager for each test.
        /// </summary>
        private readonly BuildManager _buildManager;

        /// <summary>
        /// The build parameters.
        /// </summary>
        private readonly BuildParameters _parameters;

        /// <summary>
        /// The project collection used.
        /// </summary>
        private readonly ProjectCollection _projectCollection;

        private readonly TestEnvironment _env;
        private readonly ITestOutputHelper _output;

        public SdkResultOutOfProc_Tests(ITestOutputHelper output)
        {
            _output = output;
            // Ensure that any previous tests which may have been using the default BuildManager do not conflict with us.
            BuildManager.DefaultBuildManager.Dispose();

            _logger = new MockLogger(output);
            _parameters = new BuildParameters
            {
                ShutdownInProcNodeOnBuildFinish = true,
                Loggers = new ILogger[] { _logger },
                EnableNodeReuse = false
            };
            _buildManager = new BuildManager();
            _projectCollection = new ProjectCollection();

            _env = TestEnvironment.Create(output);
            _env.SetEnvironmentVariable("MSBUILDINPROCENVCHECK", "1");
            _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");

            // Need to set this env variable to enable Process.GetCurrentProcess().Id in the project file.
            _env.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", "1");

            //  Set this if you need to debug the out of process build
            //_env.SetEnvironmentVariable("MSBUILDDEBUGONSTART", "1");
        }

        public void Dispose()
        {
            _buildManager.Dispose();
            _projectCollection.Dispose();
            _env.Dispose();
            EvaluationContext.TestOnlyHookOnCreate = null;
        }

        private const string GetCurrentProcessIdTarget = @"<Target Name='GetCurrentProcessId' Returns='@(CurrentProcessId)'>
    <ItemGroup>
       <CurrentProcessId Include='$([System.Diagnostics.Process]::GetCurrentProcess().Id)'/>
    </ItemGroup>
    <Message Text='[success]'/>
 </Target>";

        private const string GetResolverResultsTarget = @"<Target Name='GetResolverResults' Returns='@(ResolverResult)'>
    <ItemGroup>
       <ResolverResult Include='$(PropertyNameFromResolver)' Type='PropertyNameFromResolver' />
       <ResolverResult Include='@(ItemFromResolver)' Type='ItemFromResolver' />
       <ResolverResult Include='@(SdksImported)' Type='SdksImported' />
    </ItemGroup>
 </Target>";

        [Fact]
        public void RunOutOfProcBuild()
        {
            string contents = $@"
<Project>
<Import Project='Sdk.props' Sdk='TestSdk' />
{GetCurrentProcessIdTarget}
{GetResolverResultsTarget}
</Project>
";

            string projectFolder = _env.CreateFolder().Path;

            string projectPath = Path.Combine(projectFolder, "TestProject.proj");
            File.WriteAllText(projectPath, CleanupFileContents(contents));

            using var state = CreateProjectInstance(projectPath, MSBuildDefaultToolsVersion, _projectCollection, out ProjectInstance projectInstance);

            var data = new BuildRequestData(projectInstance, new[] { "GetCurrentProcessId", "GetResolverResults" }, _projectCollection.HostServices);
            var customparameters = new BuildParameters { EnableNodeReuse = false, Loggers = new ILogger[] { _logger } };

            BuildResult result = _buildManager.Build(customparameters, data);

            result.OverallResult.ShouldBe(BuildResultCode.Success);

            ValidateRanInSeparateProcess(result);
            ValidateResolverResults(result);
        }

        //  Test scenario where using an SdkResolver in a project that hasn't been evaluated
        //  in the main node (which is where the SdkResolver runs).  This validates that
        //  the SdkResult is correctly transferred between nodes.
        [Fact]
        public void RunOutOfProcBuildWithTwoProjects()
        {
            string entryProjectContents = $@"
<Project>
 {GetCurrentProcessIdTarget}
<Target Name='GetResolverResults' Returns='@(ResolverResults)'>
    <MSBuild Projects='ProjectWithSdkImport.proj'
             Targets='GetResolverResults'>
        <Output TaskParameter='TargetOutputs' ItemName='ResolverResults' />
    </MSBuild>
 </Target>
</Project>
";
            string projectFolder = _env.CreateFolder().Path;

            string entryProjectPath = Path.Combine(projectFolder, "EntryProject.proj");
            File.WriteAllText(entryProjectPath, CleanupFileContents(entryProjectContents));

            string projectWithSdkImportContents = $@"
<Project>
<Import Project='Sdk.props' Sdk='TestSdk' />
{GetResolverResultsTarget}
</Project>
";

            string projectWithSdkImportPath = Path.Combine(projectFolder, "ProjectWithSdkImport.proj");
            File.WriteAllText(projectWithSdkImportPath, CleanupFileContents(projectWithSdkImportContents));

            using var state = CreateProjectInstance(entryProjectPath, MSBuildDefaultToolsVersion, _projectCollection, out ProjectInstance projectInstance);

            var data = new BuildRequestData(projectInstance, new[] { "GetCurrentProcessId", "GetResolverResults" }, _projectCollection.HostServices);
            var customparameters = new BuildParameters { EnableNodeReuse = false, Loggers = new ILogger[] { _logger } };

            BuildResult result = _buildManager.Build(customparameters, data);


            result.OverallResult.ShouldBe(BuildResultCode.Success);

            ValidateRanInSeparateProcess(result);
            ValidateResolverResults(result);
        }


        private void ValidateRanInSeparateProcess(BuildResult result)
        {
            TargetResult targetresult = result.ResultsByTarget["GetCurrentProcessId"];
            ITaskItem[] item = targetresult.Items;

            item.ShouldHaveSingleItem();

            int.TryParse(item[0].ItemSpec, out int processId)
                .ShouldBeTrue($"Process ID passed from the 'test' target is not a valid integer (actual is '{item[0].ItemSpec}')");
            processId.ShouldNotBe(Process.GetCurrentProcess().Id);
        }

        private void ValidateResolverResults(BuildResult result)
        {
            TargetResult targetresult = result.ResultsByTarget["GetResolverResults"];

            IEnumerable<string> GetResolverResults(string type)
            {
                return targetresult.Items.Where(i => i.GetMetadata("Type").Equals(type, StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.ItemSpec)
                    .ToList();
            }

            GetResolverResults("PropertyNameFromResolver").ShouldBeSameIgnoringOrder(new[] { "PropertyValueFromResolver" });
            GetResolverResults("ItemFromResolver").ShouldBeSameIgnoringOrder(new[] { "ItemValueFromResolver" });
            GetResolverResults("SdksImported").ShouldBeSameIgnoringOrder(new[] { "Sdk1", "Sdk2" });
        }

        private ResettableSdkResolverServiceState CreateProjectInstance(string projectPath, string toolsVersion, ProjectCollection projectCollection, out ProjectInstance instance)
        {
            var (sdkResolver, state) = SetupSdkResolver(Path.GetDirectoryName(projectPath));

            // No need to store the state object here: it would restore to the state of the one above.
            SdkUtilities.CreateProjectOptionsWithResolver(sdkResolver, out var projectOptions);

            projectOptions.ProjectCollection = projectCollection;
            projectOptions.ToolsVersion = toolsVersion;

            ProjectRootElement projectRootElement = ProjectRootElement.Open(projectPath, _projectCollection);

            Project project = Project.FromProjectRootElement(projectRootElement, projectOptions);

            instance= project.CreateProjectInstance(ProjectInstanceSettings.None, projectOptions.EvaluationContext);

            return state;
        }

        private (SdkResolver, ResettableSdkResolverServiceState) SetupSdkResolver(string projectFolder)
        {
            Directory.CreateDirectory(Path.Combine(projectFolder, "Sdk1"));
            Directory.CreateDirectory(Path.Combine(projectFolder, "Sdk2"));

            string sdk1propsContents = @"
<Project>
    <ItemGroup>
        <SdksImported Include='Sdk1' />
    </ItemGroup>
</Project>";

            string sdk2propsContents = @"
<Project>
    <ItemGroup>
        <SdksImported Include='Sdk2' />
    </ItemGroup>
</Project>";

            File.WriteAllText(Path.Combine(projectFolder, "Sdk1", "Sdk.props"), CleanupFileContents(sdk1propsContents));
            File.WriteAllText(Path.Combine(projectFolder, "Sdk2", "Sdk.props"), CleanupFileContents(sdk2propsContents));

            var sdkResolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestSdk", null, null),
                        new[]
                        {
                            Path.Combine(projectFolder, "Sdk1"),
                            Path.Combine(projectFolder, "Sdk2")
                        },
                        version: null,
                        propertiesToAdd: new Dictionary<string, string>()
                            { {"PropertyNameFromResolver","PropertyValueFromResolver" } },
                        itemsToAdd: new Dictionary<string, SdkResultItem>()
                            {
                                { "ItemFromResolver", new SdkResultItem("ItemValueFromResolver", null) }
                            },
                        warnings: null
                    ));

            ResettableSdkResolverServiceState state = new();

            EvaluationContext.TestOnlyHookOnCreate = context =>
            {
                var sdkService = (SdkResolverService)context.SdkResolverService;

                state.Initialize(sdkService, null, new List<SdkResolver> { sdkResolver });
            };

            ((IBuildComponentHost)_buildManager).RegisterFactory(BuildComponentType.SdkResolverService, type =>
            {
                var resolverService = new MainNodeSdkResolverService();
                state.Initialize(resolverService, null, new List<SdkResolver> { sdkResolver });
                return resolverService;
            });

            return (sdkResolver, state);
        }
    }
}
