// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

namespace Microsoft.Build.Engine.UnitTests
{
    public class TelemetryTests
    {
        public TelemetryTests()
        {
            ProjectBuildStats.DurationThresholdForTopN = TimeSpan.Zero;
        }

        private sealed class ProjectFinishedCapturingLogger : ILogger
        {
            private readonly List<ProjectFinishedEventArgs> _projectFinishedEventArgs = [];
            public LoggerVerbosity Verbosity { get; set; }
            public string? Parameters { get; set; }

            public IReadOnlyList<ProjectFinishedEventArgs> ProjectFinishedEventArgsReceived =>
                _projectFinishedEventArgs;

            public void Initialize(IEventSource eventSource)
            {
                eventSource.ProjectFinished += EventSource_ProjectFinished;
            }

            private void EventSource_ProjectFinished(object sender, ProjectFinishedEventArgs e)
            {
                _projectFinishedEventArgs.Add(e);
            }

            public void Shutdown()
            { }
        }

        [Fact]
        public void WorkerNodeTelemetryCollection_BasicTarget()
        {
            var tstLogger = new ProjectFinishedCapturingLogger();
            var testProject = """
                        <Project>
                            <Target Name="Build">
                                <Message Text="Hello World"/>
                                <CreateItem Include="foo.bar">
                                    <Output TaskParameter="Include" ItemName="I" />
                                </CreateItem>
                                <Message Text="Bye World"/>
                            </Target>
                        </Project>
                """;
            Helpers.BuildProjectContentUsingBuildManager(testProject, tstLogger,
                new BuildParameters() { IsTelemetryEnabled = true }).OverallResult.ShouldBe(BuildResultCode.Success);

            tstLogger.ProjectFinishedEventArgsReceived.Count.ShouldBe(1);
            ProjectBuildStats? stats = tstLogger.ProjectFinishedEventArgsReceived[0].ProjectBuildStats;
            stats.ShouldNotBeNull();
            ((int)stats.CustomTargetsCount).ShouldBe(1);
            ((int)stats.ExecutedCustomTargetsCount).ShouldBe(1);
            ((int)stats.TotalTargetsCount).ShouldBe(1);
            ((int)stats.ExecutedCustomTargetsCount).ShouldBe(1);

            ((int)stats.TotalTasksCount).ShouldBeGreaterThan(2);
            ((int)stats.TotalTasksExecutionsCount).ShouldBe(3);
            ((int)stats.TotalExecutedTasksCount).ShouldBe(2);
            ((int)stats.CustomTasksCount).ShouldBe(0);
            ((int)stats.CustomTasksExecutionsCount).ShouldBe(0);
            ((int)stats.ExecutedCustomTasksCount).ShouldBe(0);
            stats.TotalTasksExecution.ShouldBeGreaterThan(TimeSpan.Zero);
            stats.TotalCustomTasksExecution.ShouldBe(TimeSpan.Zero);

            stats.TopTasksByCumulativeExecution.Count.ShouldNotBe(0);
            foreach (var st in stats.TopTasksByCumulativeExecution)
            {
                st.Key.ShouldBeGreaterThan(TimeSpan.Zero);
                (st.Value.EndsWith("Message") || st.Value.EndsWith("CreateItem")).ShouldBeTrue($"Only specified tasks expected. Encountered: {st.Value}");
            }
        }

        [Fact]
        public void WorkerNodeTelemetryCollection_CustomTargetsAndTasks()
        {
            var tstLogger = new ProjectFinishedCapturingLogger();
            var testProject = """
                                      <Project>
                                      <UsingTask
                                          TaskName="Task01"
                                          TaskFactory="RoslynCodeTaskFactory"
                                          AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll" >
                                          <ParameterGroup />
                                          <Task>
                                            <Code Type="Fragment" Language="cs">
                                              Log.LogMessage(MessageImportance.Low, "Hello, world!");
                                            </Code>
                                          </Task>
                                       </UsingTask>
                                       
                                       <UsingTask
                                         TaskName="Task02"
                                         TaskFactory="RoslynCodeTaskFactory"
                                         AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll" >
                                         <ParameterGroup />
                                         <Task>
                                           <Code Type="Fragment" Language="cs">
                                             Log.LogMessage(MessageImportance.High, "Hello, world!");
                                           </Code>
                                         </Task>
                                      </UsingTask>
                                      
                                          <Target Name="Build" DependsOnTargets="BeforeBuild">
                                              <Message Text="Hello World"/>
                                              <CreateItem Include="foo.bar">
                                                  <Output TaskParameter="Include" ItemName="I" />
                                              </CreateItem>
                                              <Task01 />
                                              <Message Text="Bye World"/>
                                          </Target>
                                          
                                          <Target Name="BeforeBuild">
                                              <Message Text="Hello World"/>
                                              <Task01 />
                                          </Target>
                                          
                                          <Target Name="NotExecuted">
                                              <Message Text="Hello World"/>
                                          </Target>
                                      </Project>
                              """;
            Helpers.BuildProjectContentUsingBuildManager(testProject, tstLogger,
                new BuildParameters() { IsTelemetryEnabled = true }).OverallResult.ShouldBe(BuildResultCode.Success);

            tstLogger.ProjectFinishedEventArgsReceived.Count.ShouldBe(1);
            ProjectBuildStats? stats = tstLogger.ProjectFinishedEventArgsReceived[0].ProjectBuildStats;
            stats.ShouldNotBeNull();
            ((int)stats.CustomTargetsCount).ShouldBe(3);
            ((int)stats.ExecutedCustomTargetsCount).ShouldBe(2);
            ((int)stats.TotalTargetsCount).ShouldBe(3);
            ((int)stats.ExecutedCustomTargetsCount).ShouldBe(2);

            ((int)stats.TotalTasksCount).ShouldBeGreaterThan(2);
            ((int)stats.TotalTasksExecutionsCount).ShouldBe(6);
            ((int)stats.TotalExecutedTasksCount).ShouldBe(3);
            ((int)stats.CustomTasksCount).ShouldBe(2);
            ((int)stats.CustomTasksExecutionsCount).ShouldBe(2);
            ((int)stats.ExecutedCustomTasksCount).ShouldBe(1);
            stats.TotalTasksExecution.ShouldBeGreaterThan(TimeSpan.Zero);
            stats.TotalCustomTasksExecution.ShouldBeGreaterThan(TimeSpan.Zero);

            stats.TopTasksByCumulativeExecution.Count.ShouldNotBe(0);
            foreach (var st in stats.TopTasksByCumulativeExecution)
            {
                st.Key.ShouldBeGreaterThan(TimeSpan.Zero);
                (st.Value.EndsWith("Message") || st.Value.EndsWith("CreateItem") || st.Value.EndsWith("Task01")).ShouldBeTrue($"Only specified tasks expected. Encountered: {st.Value}");
            }
            stats.TopTasksByCumulativeExecution.Any(t => t.Value.Equals("Custom:Task01")).ShouldBeTrue($"Expected to encounter custom task. Tasks: {stats.TopTasksByCumulativeExecution.Select(t => t.Value).ToCsvString()}");
        }
    }
}
