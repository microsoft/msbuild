// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests.BackEnd;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public class BuildEventArgsSerializationTests
    {
        public BuildEventArgsSerializationTests()
        {
            // touch the type so that static constructor runs
            _ = ItemGroupLoggingHelper.ItemGroupIncludeLogMessagePrefix;
        }

        [Fact]
        public void WriteBlobFromStream()
        {
            byte[] bytes = new byte[] { 1, 2, 3, 4, 5 };
            MemoryStream inputStream = new MemoryStream(bytes);

            MemoryStream outputStream = new MemoryStream();
            using BinaryWriter binaryWriter = new BinaryWriter(outputStream);
            BuildEventArgsWriter writer = new BuildEventArgsWriter(binaryWriter);

            writer.WriteBlob(BinaryLogRecordKind.ProjectImportArchive, inputStream);
            binaryWriter.Flush();

            outputStream.Position = 0;
            using BinaryReader binaryReader = new BinaryReader(outputStream);
            Assert.Equal(BinaryLogRecordKind.ProjectImportArchive, (BinaryLogRecordKind)binaryReader.Read7BitEncodedInt());
            Assert.Equal(bytes.Length, binaryReader.Read7BitEncodedInt());
            Assert.Equal(bytes, binaryReader.ReadBytes(bytes.Length));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripBuildStartedEventArgs(bool serializeAllEnvironmentVariables)
        {
            Traits.LogAllEnvironmentVariables = serializeAllEnvironmentVariables;
            var args = new BuildStartedEventArgs(
                "Message",
                "HelpKeyword",
                DateTime.Parse("3/1/2017 11:11:56 AM"));
            Roundtrip(args,
                e => e.Message,
                e => e.HelpKeyword,
                e => e.Timestamp.ToString());

            args = new BuildStartedEventArgs(
                "M",
                null,
                new Dictionary<string, string>
                {
                { "SampleName", "SampleValue" }
                });
            Roundtrip(args,
                e => serializeAllEnvironmentVariables ? TranslationHelpers.ToString(e.BuildEnvironment) : null,
                e => e.HelpKeyword,
                e => e.ThreadId.ToString(),
                e => e.SenderName);

            Traits.LogAllEnvironmentVariables = false;
        }

        [Fact]
        public void RoundtripBuildFinishedEventArgs()
        {
            var args = new BuildFinishedEventArgs(
                "Message",
                null,
                succeeded: true,
                eventTimestamp: DateTime.Parse("12/12/2015 06:11:56 PM"));
            args.BuildEventContext = new BuildEventContext(1, 2, 3, 4, 5, 6);

            Roundtrip(args,
                e => ToString(e.BuildEventContext),
                e => e.Succeeded.ToString());
        }

        [Fact]
        public void RoundtripBuildCanceledEventArgs()
        {
            var args = new BuildCanceledEventArgs(
                "Message",
                eventTimestamp: DateTime.Parse("12/12/2015 06:11:56 PM"));

            Roundtrip(args,
                e => e.Message,
                e => e.Timestamp.ToString());
        }

        [Fact]
        public void RoundtripBuildSubmissionStartedEventArgs()
        {
            var globalVariables = new Dictionary<string, string>
            {
                {"Variable1", "Value1" },
                {"Variable2", "" },
                {"Variable3", null },
            };
            var entryPointProjects = new List<string>()
            {
                "project1",
                "project2",
                "",
            };
            var targetNames = new List<string>()
            {
                "target1",
                "target2",
                "",
            };
            var flag = Execution.BuildRequestDataFlags.FailOnUnresolvedSdk;
            var submissionId = 1234;

            BuildSubmissionStartedEventArgs args = new(
                globalVariables,
                entryPointProjects,
                targetNames,
                flag,
                submissionId);

            Roundtrip<BuildSubmissionStartedEventArgs>(args,
                e => TranslationHelpers.GetPropertiesString(e.GlobalProperties),
                e => TranslationHelpers.GetPropertiesString(e.EntryProjectsFullPath),
                e => TranslationHelpers.GetPropertiesString(e.TargetNames),
                e => e.Flags.ToString(),
                e => e.SubmissionId.ToString());
        }

        [Fact]
        public void RoundtripProjectStartedEventArgs()
        {
            var args = new ProjectStartedEventArgs(
                projectId: 42,
                message: "Project \"test.proj\" (Build target(s)):",
                helpKeyword: "help",
                projectFile: Path.Combine("a", "test.proj"),
                targetNames: "Build",
                properties: new List<DictionaryEntry>() { new DictionaryEntry("Key", "Value") },
                items: new List<DictionaryEntry>() { new DictionaryEntry("Key", new MyTaskItem() { ItemSpec = "TestItemSpec" }) },
                parentBuildEventContext: new BuildEventContext(7, 8, 9, 10, 11, 12),
                globalProperties: new Dictionary<string, string>() { { "GlobalKey", "GlobalValue" } },
                toolsVersion: "Current");
            args.BuildEventContext = new BuildEventContext(1, 2, 3, 4, 5, 6);

            Roundtrip<ProjectStartedEventArgs>(args,
                e => ToString(e.BuildEventContext),
                e => TranslationHelpers.GetPropertiesString(e.GlobalProperties),
                e => TranslationHelpers.GetMultiItemsString(e.Items),
                e => e.Message,
                e => ToString(e.ParentProjectBuildEventContext),
                e => e.ProjectFile,
                e => e.ProjectId.ToString(),
                e => TranslationHelpers.GetPropertiesString(e.Properties),
                e => e.TargetNames,
                e => e.ThreadId.ToString(),
                e => e.Timestamp.ToString(),
                e => e.ToolsVersion);
        }

        [Fact]
        public void RoundtripProjectFinishedEventArgs()
        {
            var args = new ProjectFinishedEventArgs(
                "Message",
                null,
                "C:\\projectfile.proj",
                true,
                DateTime.Parse("12/12/2015 06:11:56 PM"));

            Roundtrip(args,
                e => e.ProjectFile,
                e => e.Succeeded.ToString());
        }

        [Fact]
        public void RoundtripTargetStartedEventArgs()
        {
            var args = new TargetStartedEventArgs(
                "Message",
                "Help",
                "Build",
                "C:\\projectfile.proj",
                "C:\\Common.targets",
                "ParentTarget",
                TargetBuiltReason.AfterTargets,
                DateTime.Parse("12/12/2015 06:11:56 PM"));

            Roundtrip(args,
                e => e.ParentTarget,
                e => e.ProjectFile,
                e => e.TargetFile,
                e => e.TargetName,
                e => e.BuildReason.ToString(),
                e => e.Timestamp.ToString());
        }

        [Fact]
        public void RoundtripTargetFinishedEventArgs()
        {
            var args = new TargetFinishedEventArgs(
                "Message",
                null,
                "TargetName",
                "C:\\Project.proj",
                "C:\\TargetFile.targets",
                true,
                new List<ITaskItem> { new MyTaskItem() });

            Roundtrip(args,
                e => e.ProjectFile,
                e => e.Succeeded.ToString(),
                e => e.TargetFile,
                e => e.TargetName,
                e => ToString(e.TargetOutputs.OfType<ITaskItem>()));
        }

        [Fact]
        public void RoundtripTaskStartedEventArgs()
        {
            var args = new TaskStartedEventArgs(
                "Message",
                null,
                projectFile: "C:\\project.proj",
                taskFile: "C:\\common.targets",
                taskName: "Csc",
                DateTime.Now,
                "TaskAssemblyLocation");
            args.LineNumber = 42;
            args.ColumnNumber = 999;

            Roundtrip(args,
                e => e.ProjectFile,
                e => e.TaskFile,
                e => e.TaskName,
                e => e.LineNumber.ToString(),
                e => e.ColumnNumber.ToString(),
                e => e.TaskAssemblyLocation);
        }

        [Fact]
        public void RoundtripEnvironmentVariableReadEventArgs()
        {
            EnvironmentVariableReadEventArgs args = new("VarName", "VarValue", "file", 10, 20);
            args.BuildEventContext = new BuildEventContext(4, 5, 6, 7);
            Roundtrip(args,
                e => e.Message,
                e => e.EnvironmentVariableName,
                e => e.BuildEventContext.ToString());
        }

        [Fact]
        public void RoundtripTaskFinishedEventArgs()
        {
            var args = new TaskFinishedEventArgs(
                "Message",
                null,
                "C:\\project.proj",
                "C:\\common.tasks",
                "Csc",
                succeeded: false);

            Roundtrip(args,
                e => e.ProjectFile,
                e => e.TaskFile,
                e => e.TaskName,
                e => e.ThreadId.ToString());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripBuildErrorEventArgs(bool useArguments)
        {
            var args = new BuildErrorEventArgs(
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message with arguments: '{0}'",
                "Help",
                "SenderName",
                DateTime.Parse("9/1/2021 12:02:07 PM"),
                useArguments ? new object[] { "argument0" } : null);

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }


        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripExtendedErrorEventArgs_SerializedAsError(bool withOptionalData)
        {
            var args = new ExtendedBuildErrorEventArgs(
                "extendedDataType",
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message with arguments: '{0}'",
                "Help",
                "SenderName",
                DateTime.Parse("9/1/2021 12:02:07 PM"),
                withOptionalData ? new object[] { "argument0" } : null)
            {
                ExtendedData = withOptionalData ? /*lang=json*/ "{'long-json':'mostly-strings'}" : null,
                ExtendedMetadata = withOptionalData ? new Dictionary<string, string> { { "m1", "v1" }, { "m2", "v2" } } : null,
                BuildEventContext = withOptionalData ? new BuildEventContext(1, 2, 3, 4, 5, 6, 7) : null,
            };

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory,
                e => e.ExtendedType,
                e => TranslationHelpers.ToString(e.ExtendedMetadata),
                e => e.ExtendedData,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripBuildWarningEventArgs(bool useArguments)
        {
            var args = new BuildWarningEventArgs(
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message with arguments: '{0}'",
                "Help",
                "SenderName",
                DateTime.Parse("9/1/2021 12:02:07 PM"),
                useArguments ? new object[] { "argument0" } : null);

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripExtendedWarningEventArgs_SerializedAsWarning(bool withOptionalData)
        {
            var args = new ExtendedBuildWarningEventArgs(
                "extendedDataType",
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message with arguments: '{0}'",
                "Help",
                "SenderName",
                DateTime.Parse("9/1/2021 12:02:07 PM"),
                withOptionalData ? new object[] { "argument0" } : null)
            {
                ExtendedData = withOptionalData ? /*lang=json*/ "{'long-json':'mostly-strings'}" : null,
                ExtendedMetadata = withOptionalData ? new Dictionary<string, string> { { "m1", "v1" }, { "m2", "v2" } } : null,
                BuildEventContext = withOptionalData ? new BuildEventContext(1, 2, 3, 4, 5, 6, 7) : null,
            };

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory,
                e => e.ExtendedType,
                e => TranslationHelpers.ToString(e.ExtendedMetadata),
                e => e.ExtendedData,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripBuildMessageEventArgs(bool useArguments)
        {
            var args = new BuildMessageEventArgs(
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message",
                "Help",
                "SenderName",
                MessageImportance.High,
                DateTime.Parse("12/12/2015 06:11:56 PM"),
                useArguments ? new object[] { "argument0" } : null);

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.Importance.ToString(),
                e => e.ProjectFile,
                e => e.Subcategory,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripExtendedBuildMessageEventArgs_SerializedAsMessage(bool withOptionalData)
        {
            var args = new ExtendedBuildMessageEventArgs(
                "extendedDataType",
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message",
                "Help",
                "SenderName",
                MessageImportance.High,
                DateTime.Parse("12/12/2015 06:11:56 PM"),
                withOptionalData ? new object[] { "argument0" } : null)
            {
                ExtendedData = withOptionalData ? /*lang=json*/ "{'long-json':'mostly-strings'}" : null,
                ExtendedMetadata = withOptionalData ? new Dictionary<string, string> { { "m1", "v1" }, { "m2", "v2" } } : null,
                BuildEventContext = withOptionalData ? new BuildEventContext(1, 2, 3, 4, 5, 6, 7) : null,
            };

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.Importance.ToString(),
                e => e.ProjectFile,
                e => e.Subcategory,
                e => e.ExtendedType,
                e => TranslationHelpers.ToString(e.ExtendedMetadata),
                e => e.ExtendedData,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Fact]
        public void RoundtripAssemblyLoadBuild()
        {
            string assemblyName = Guid.NewGuid().ToString();
            string assemblyPath = Guid.NewGuid().ToString();
            Guid mvid = Guid.NewGuid();
            string loadingInitiator = Guid.NewGuid().ToString();
            string appDomainName = Guid.NewGuid().ToString();
            AssemblyLoadingContext context =
                (AssemblyLoadingContext)(new Random().Next(Enum.GetNames(typeof(AssemblyLoadingContext)).Length));

            AssemblyLoadBuildEventArgs args = new(context, loadingInitiator, assemblyName, assemblyPath, mvid, appDomainName);

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.Importance.ToString(),
                e => e.ProjectFile,
                e => e.Subcategory,
                e => e.LoadingContext.ToString(),
                e => e.AssemblyName,
                e => e.AssemblyPath,
                e => e.MVID.ToString(),
                e => e.AppDomainDescriptor,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Fact]
        public void RoundtripBuildCheckTracingEventArgs()
        {
            string key1 = "AA";
            TimeSpan span1 = TimeSpan.FromSeconds(5);
            string key2 = "b";
            TimeSpan span2 = TimeSpan.FromSeconds(15);
            string key3 = "cCc";
            TimeSpan span3 = TimeSpan.FromSeconds(50);

            Dictionary<string, TimeSpan> stats = new() { { key1, span1 }, { key2, span2 }, { key3, span3 } };

            BuildCheckTracingEventArgs args = new BuildCheckTracingEventArgs(stats);

            Roundtrip(args,
                e => e.TracingData.InfrastructureTracingData.Keys.Count.ToString(),
                e => e.TracingData.InfrastructureTracingData.Keys.ToCsvString(false),
                e => e.TracingData.InfrastructureTracingData.Values
                    .Select(v => v.TotalSeconds.ToString(CultureInfo.InvariantCulture)).ToCsvString(false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExtendedCustomBuildEventArgs_SerializedAsMessage(bool withOptionalData)
        {
            ExtendedCustomBuildEventArgs args = new(
                type: "TypeOfExtendedCustom",
                message: withOptionalData ? "a message with args {0} {1}" : null,
                helpKeyword: withOptionalData ? "MSBT123" : null,
                senderName: withOptionalData ? $"UnitTest {Guid.NewGuid()}" : null,
                eventTimestamp: withOptionalData ? DateTime.Parse("3/1/2017 11:11:56 AM") : DateTime.Now,
                messageArgs: withOptionalData ? new object[] { "arg0val", "arg1val" } : null)
            {
                ExtendedData = withOptionalData ? /*lang=json*/ "{'long-json':'mostly-strings'}" : null,
                ExtendedMetadata = withOptionalData ? new Dictionary<string, string> { { "m1", "v1" }, { "m2", "v2" } } : null,
                BuildEventContext = withOptionalData ? new BuildEventContext(1, 2, 3, 4, 5, 6, 7) : null,
            };


            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);
            buildEventArgsWriter.Write(args);

            memoryStream.Position = 0;
            using var binaryReader = new BinaryReader(memoryStream);

            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion);
            var deserialized = buildEventArgsReader.Read();
            ExtendedBuildMessageEventArgs desArgs = (ExtendedBuildMessageEventArgs)deserialized;

            desArgs.ShouldBeOfType(typeof(ExtendedBuildMessageEventArgs));
            desArgs.Message.ShouldBe(args.Message);
            desArgs.HelpKeyword.ShouldBe(args.HelpKeyword);
            desArgs.SenderName.ShouldBe(args.SenderName);
            desArgs.Importance.ShouldBe(MessageImportance.Normal);
            desArgs.Timestamp.ShouldBe(args.Timestamp);
            desArgs.ExtendedType.ShouldBe(args.ExtendedType);

            if (withOptionalData)
            {
                desArgs.BuildEventContext.ShouldBe(args.BuildEventContext);
                desArgs.ExtendedData.ShouldBe(args.ExtendedData);
                TranslationHelpers.ToString(desArgs.ExtendedMetadata).ShouldBe(TranslationHelpers.ToString(args.ExtendedMetadata));
            }
            else
            {
                desArgs.BuildEventContext.ShouldBe(BuildEventContext.Invalid);
            }
        }

        [Fact]
        public void RoundtripResponseFileUsedEventArgs()
        {
            var args = new ResponseFileUsedEventArgs("MSBuild.rsp");
            Roundtrip(args,
                e => e.ResponseFilePath);
        }


        [Fact]
        public void RoundtripCriticalBuildMessageEventArgs()
        {
            var args = new CriticalBuildMessageEventArgs(
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message",
                "Help",
                "SenderName",
                DateTime.Parse("12/12/2015 06:11:56 PM"));

            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundtripExtendedCriticalBuildMessageEventArgs(bool withOptionalData)
        {
            var args = new ExtendedCriticalBuildMessageEventArgs(
                "extCrit",
                "Subcategory",
                "Code",
                "File",
                1,
                2,
                3,
                4,
                "Message",
                "Help",
                "SenderName",
                DateTime.Parse("12/12/2015 06:11:56 PM"),
                withOptionalData ? new object[] { "argument0" } : null)
            {
                ExtendedData = withOptionalData ? /*lang=json*/ "{'long-json':'mostly-strings'}" : null,
                ExtendedMetadata = withOptionalData ? new Dictionary<string, string> { { "m1", "v1" }, { "m2", "v2" } } : null,
                BuildEventContext = withOptionalData ? new BuildEventContext(1, 2, 3, 4, 5, 6, 7) : null,
            };


            Roundtrip(args,
                e => e.Code,
                e => e.ColumnNumber.ToString(),
                e => e.EndColumnNumber.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory,
                e => e.ExtendedType,
                e => TranslationHelpers.ToString(e.ExtendedMetadata),
                e => e.ExtendedData,
                e => string.Join(", ", e.RawArguments ?? Array.Empty<object>()));
        }

        [Fact]
        public void RoundtripTaskCommandLineEventArgs()
        {
            var args = new TaskCommandLineEventArgs(
                "/bl /noconlog /v:diag",
                "Csc",
                MessageImportance.Low,
                DateTime.Parse("12/12/2015 06:11:56 PM"));

            Roundtrip(args,
                e => e.CommandLine,
                e => e.TaskName,
                e => e.Importance.ToString(),
                e => e.EndLineNumber.ToString(),
                e => e.File,
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.Subcategory);
        }

        [Fact]
        public void RoundtripTaskParameterEventArgs()
        {
            var items = new ITaskItem[]
            {
                new TaskItemData("ItemSpec1", null),
                new TaskItemData("ItemSpec2", Enumerable.Range(1,3).ToDictionary(i => i.ToString(), i => i.ToString() + "value"))
            };
            var args = new TaskParameterEventArgs(TaskParameterMessageKind.TaskOutput, "ParameterName1", "PropertyName1", "ItemName1", items, true, DateTime.MinValue);
            args.LineNumber = 265;
            args.ColumnNumber = 6;

            Roundtrip(args,
                e => e.Kind.ToString(),
                e => e.ParameterName,
                e => e.PropertyName,
                e => e.ItemType,
                e => e.LogItemMetadata.ToString(),
                e => e.LineNumber.ToString(),
                e => e.ColumnNumber.ToString(),
                e => TranslationHelpers.GetItemsString(e.Items));
        }

        [Fact]
        public void RoundtripProjectEvaluationStartedEventArgs()
        {
            var projectFile = @"C:\foo\bar.proj";
            var args = new ProjectEvaluationStartedEventArgs(
                ResourceUtilities.GetResourceString("EvaluationStarted"),
                projectFile)
            {
                BuildEventContext = BuildEventContext.Invalid,
                ProjectFile = projectFile,
            };

            Roundtrip(args,
                e => e.Message,
                e => e.ProjectFile);
        }

        [Fact]
        public void RoundtripProjectEvaluationFinishedEventArgs()
        {
            var projectFile = @"C:\foo\bar.proj";
            var args = new ProjectEvaluationFinishedEventArgs(
                ResourceUtilities.GetResourceString("EvaluationFinished"),
                projectFile)
            {
                BuildEventContext = BuildEventContext.Invalid,
                ProjectFile = @"C:\foo\bar.proj",
                GlobalProperties = new Dictionary<string, string>() { { "GlobalKey", "GlobalValue" } },
                Properties = new List<DictionaryEntry>() { new DictionaryEntry("Key", "Value") },
                Items = new List<DictionaryEntry>() { new DictionaryEntry("Key", new MyTaskItem() { ItemSpec = "TestItemSpec" }) }
            };

            Roundtrip(args,
                e => e.Message,
                e => e.ProjectFile,
                e => TranslationHelpers.GetPropertiesString(e.GlobalProperties),
                e => TranslationHelpers.GetPropertiesString(e.Properties),
                e => TranslationHelpers.GetMultiItemsString(e.Items));
        }

        [Fact]
        public void RoundtripProjectEvaluationFinishedEventArgsWithProfileData()
        {
            var projectFile = @"C:\foo\bar.proj";
            var args = new ProjectEvaluationFinishedEventArgs(
                ResourceUtilities.GetResourceString("EvaluationFinished"),
                projectFile)
            {
                BuildEventContext = BuildEventContext.Invalid,
                ProjectFile = @"C:\foo\bar.proj",
                ProfilerResult = new ProfilerResult(new Dictionary<EvaluationLocation, ProfiledLocation>
                {
                    {
                        new EvaluationLocation(1, 0, EvaluationPass.InitialProperties, "desc1", "file1", 7, "element1", "description", EvaluationLocationKind.Condition),
                        new ProfiledLocation(TimeSpan.FromSeconds(1), TimeSpan.FromHours(2), 1)
                    },
                    {
                        new EvaluationLocation(0, null, EvaluationPass.LazyItems, "desc2", "file1", null, "element2", "description2", EvaluationLocationKind.Glob),
                        new ProfiledLocation(TimeSpan.FromSeconds(1), TimeSpan.FromHours(2), 2)
                    },
                    {
                        new EvaluationLocation(2, 0, EvaluationPass.Properties, "desc2", "file1", null, "element2", "description2", EvaluationLocationKind.Element),
                        new ProfiledLocation(TimeSpan.FromSeconds(1), TimeSpan.FromHours(2), 2)
                    }
                })
            };

            Roundtrip(args,
                e => e.Message,
                e => e.ProjectFile,
                e => ToString(e.ProfilerResult.Value.ProfiledLocations));
        }

        [Fact]
        public void RoundtripProjectImportedEventArgs()
        {
            var args = new ProjectImportedEventArgs(
                1,
                2,
                "Message")
            {
                BuildEventContext = BuildEventContext.Invalid,
                ImportedProjectFile = "foo.props",
                ProjectFile = "foo.csproj",
                UnexpandedProject = "$(Something)"
            };

            Roundtrip(args,
                e => e.ImportedProjectFile,
                e => e.UnexpandedProject,
                e => e.Importance.ToString(),
                e => e.LineNumber.ToString(),
                e => e.ColumnNumber.ToString(),
                e => e.LineNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile);
        }

        [Fact]
        public void RoundtripTargetSkippedEventArgs()
        {
            var args = new TargetSkippedEventArgs(
                "Target \"target\" skipped. Previously built unsuccessfully.")
            {
                BuildEventContext = BuildEventContext.Invalid,
                ProjectFile = "foo.csproj",
                TargetName = "target",
                ParentTarget = "bar",
                BuildReason = TargetBuiltReason.DependsOn,
                SkipReason = TargetSkipReason.PreviouslyBuiltSuccessfully,
                Condition = "$(condition) == true",
                EvaluatedCondition = "true == true",
                OriginalBuildEventContext = new BuildEventContext(1, 2, 3, 4, 5, 6, 7),
                OriginallySucceeded = false,
                TargetFile = "foo.csproj"
            };

            Roundtrip(args,
                e => e.BuildEventContext.ToString(),
                e => e.ParentTarget,
                e => e.Importance.ToString(),
                e => e.LineNumber.ToString(),
                e => e.ColumnNumber.ToString(),
                e => e.Message,
                e => e.ProjectFile,
                e => e.TargetFile,
                e => e.TargetName,
                e => e.BuildReason.ToString(),
                e => e.SkipReason.ToString(),
                e => e.Condition,
                e => e.EvaluatedCondition,
                e => e.OriginalBuildEventContext.ToString(),
                e => e.OriginallySucceeded.ToString());
        }

        [Fact]
        public void RoundTripPropertyReassignmentEventArgs()
        {
            var args = new PropertyReassignmentEventArgs(
                propertyName: "a",
                previousValue: "b",
                newValue: "c",
                file: "file.cs",
                line: 10,
                column: 20,
                message: "Property reassignment: $(a)=\"c\" (previous value: \"b\") at file.cs (10,20)",
                helpKeyword: "e",
                senderName: "f");

            Roundtrip(args,
                e => e.PropertyName,
                e => e.PreviousValue,
                e => e.NewValue,
                e => e.Location,
                e => e.Message,
                e => e.HelpKeyword,
                e => e.SenderName);
        }

        [Fact]
        public void UninitializedPropertyReadEventArgs()
        {
            var args = new UninitializedPropertyReadEventArgs(
                propertyName: Guid.NewGuid().ToString(),
                message: Guid.NewGuid().ToString(),
                helpKeyword: Guid.NewGuid().ToString(),
                senderName: Guid.NewGuid().ToString());

            Roundtrip(args,
                e => e.PropertyName,
                e => e.Message,
                e => e.HelpKeyword,
                e => e.SenderName);
        }

        [Fact]
        public void PropertyInitialValueEventArgs()
        {
            var args = new PropertyInitialValueSetEventArgs(
                propertyName: Guid.NewGuid().ToString(),
                propertyValue: Guid.NewGuid().ToString(),
                propertySource: Guid.NewGuid().ToString(),
                message: Guid.NewGuid().ToString(),
                helpKeyword: Guid.NewGuid().ToString(),
                senderName: Guid.NewGuid().ToString());

            Roundtrip(args,
                e => e.PropertyName,
                e => e.PropertyValue,
                e => e.PropertySource,
                e => e.Message,
                e => e.HelpKeyword,
                e => e.SenderName);
        }

        [Fact]
        public void ReadingCorruptedStreamThrows()
        {
            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            var args = new BuildStartedEventArgs(
                "Message",
                "HelpKeyword",
                DateTime.Parse("3/1/2017 11:11:56 AM"));

            buildEventArgsWriter.Write(args);

            long length = memoryStream.Length;

            for (long i = length - 1; i >= 0; i--) // try all possible places where a stream might end abruptly
            {
                memoryStream.SetLength(i); // pretend that the stream abruptly ends
                memoryStream.Position = 0;

#pragma warning disable CA2000 // The memory stream needs to keep the binary reader open.
                var binaryReader = new BinaryReader(memoryStream);
                var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion);
#pragma warning restore CA2000 // The memory stream needs to keep the binary reader open.

                Assert.Throws<EndOfStreamException>(() => buildEventArgsReader.Read());
            }
        }

        [Fact]
        public void ForwardCompatibleRead_HandleAppendOnlyChanges()
        {
            // Let's not write any strings prior the first event - to make locating (and overwriting) the size byte(s) easier.
            BuildErrorEventArgs error = new(null, null, null, 1, 2, 3, 4, null, null, null);
            BuildFinishedEventArgs finished = new("Message", "HelpKeyword", true);

            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            using var binaryReader = new BinaryReader(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            buildEventArgsWriter.Write(error);

            // Some future data that are not known in current version
            binaryWriter.Write(new byte[] { 1, 2, 3, 4 });


            int positionAfterFirstEvent = (int)memoryStream.Position;
            memoryStream.Position = 0;
            // event type
            Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int eventSizePos = (int)memoryStream.Position;
            int eventSize = Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int positionAfterFirstEventSize = (int)memoryStream.Position;
            memoryStream.Position = eventSizePos;
            // the extra 4 bytes
            Microsoft.Build.Shared.BinaryWriterExtensions.Write7BitEncodedInt(binaryWriter, eventSize + 4);
            memoryStream.Position.ShouldBe(positionAfterFirstEventSize, "The event size need to be overwritten in place - without overwriting any bytes after the size info");
            memoryStream.Position = positionAfterFirstEvent;

            buildEventArgsWriter.Write(finished);

            // Remember num of bytes written - we should read them all.
            long length = memoryStream.Length;
            // Now move back to the beginning of the stream and start reading.
            memoryStream.Position = 0;

            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion)
            {
                SkipUnknownEventParts = true
            };

            List<BinaryLogReaderErrorEventArgs> readerErrors = new();
            buildEventArgsReader.RecoverableReadError += readerErrors.Add;

            var deserializedError = (BuildErrorEventArgs)buildEventArgsReader.Read();

            readerErrors.Count.ShouldBe(1);
            readerErrors[0].ErrorType.ShouldBe(ReaderErrorType.UnknownEventData);
            readerErrors[0].RecordKind.ShouldBe(BinaryLogRecordKind.Error);

            deserializedError.Should().BeEquivalentTo(error);

            var deserializedFinished = (BuildFinishedEventArgs)buildEventArgsReader.Read();

            readerErrors.Count.ShouldBe(1);

            deserializedFinished.Should().BeEquivalentTo(finished);

            // There is nothing else in the stream
            memoryStream.Position.Should().Be(length);
        }

        [Fact]
        public void ForwardCompatibleRead_HandleUnknownEvent()
        {
            // Let's not write any strings prior the first event - to make locating (and overwriting) the event type byte(s) easier.
            BuildErrorEventArgs error = new(null, null, null, 1, 2, 3, 4, null, null, null);
            BuildFinishedEventArgs finished = new("Message", "HelpKeyword", true);

            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            using var binaryReader = new BinaryReader(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            buildEventArgsWriter.Write(error);

            int positionAfterFirstEvent = (int)memoryStream.Position;
            memoryStream.Position = 0;
            // event type
            Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int eventSizePos = (int)memoryStream.Position;
            memoryStream.Position = 0;

            // some future type that is not known in current version
            BinaryLogRecordKind unknownType = (BinaryLogRecordKind)Enum.GetValues(typeof(BinaryLogRecordKind)).Cast<BinaryLogRecordKind>().Select(e => (int)e).Max() + 2;
            Microsoft.Build.Shared.BinaryWriterExtensions.Write7BitEncodedInt(binaryWriter, (int)unknownType);
            memoryStream.Position.ShouldBe(eventSizePos, "The event type need to be overwritten in place - without overwriting any bytes after the type info");
            memoryStream.Position = positionAfterFirstEvent;

            buildEventArgsWriter.Write(finished);

            // Remember num of bytes written - we should read them all.
            long length = memoryStream.Length;
            // Now move back to the beginning of the stream and start reading.
            memoryStream.Position = 0;

            List<BinaryLogReaderErrorEventArgs> readerErrors = new();
            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion)
            {
                SkipUnknownEvents = true
            };

            buildEventArgsReader.RecoverableReadError += readerErrors.Add;

            var deserializedEvent = buildEventArgsReader.Read();

            readerErrors.Count.ShouldBe(1);
            readerErrors[0].ErrorType.ShouldBe(ReaderErrorType.UnknownEventType);
            readerErrors[0].RecordKind.ShouldBe(unknownType);

            deserializedEvent.Should().BeEquivalentTo(finished);

            // There is nothing else in the stream
            memoryStream.Position.Should().Be(length);
        }

        [Fact]
        public void ForwardCompatibleRead_HandleMismatchedFormatOfEvent()
        {
            // BuildErrorEventArgs error = new("Subcategory", "Code", "File", 1, 2, 3, 4, "Message", "HelpKeyword", "SenderName");
            BuildErrorEventArgs error = new(null, null, null, 1, 2, 3, 4, null, null, null);
            BuildFinishedEventArgs finished = new("Message", "HelpKeyword", true);

            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            using var binaryReader = new BinaryReader(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            buildEventArgsWriter.Write(error);

            int positionAfterFirstEvent = (int)memoryStream.Position;
            memoryStream.Position = 0;
            // event type
            Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int eventSize = Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            // overwrite the entire event with garbage
            binaryWriter.Write(Enumerable.Repeat(byte.MaxValue, eventSize).ToArray());

            memoryStream.Position.ShouldBe(positionAfterFirstEvent, "The event need to be overwritten in place - without overwriting any bytes after the size info");

            buildEventArgsWriter.Write(finished);

            // Remember num of bytes written - we should read them all.
            long length = memoryStream.Length;
            // Now move back to the beginning of the stream and start reading.
            memoryStream.Position = 0;

            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion)
            {
                SkipUnknownEvents = true
            };

            List<BinaryLogReaderErrorEventArgs> readerErrors = new();
            buildEventArgsReader.RecoverableReadError += readerErrors.Add;

            var deserializedEvent = buildEventArgsReader.Read();

            readerErrors.Count.ShouldBe(1);
            readerErrors[0].ErrorType.ShouldBe(ReaderErrorType.UnknownFormatOfEventData);
            readerErrors[0].RecordKind.ShouldBe(BinaryLogRecordKind.Error);
            readerErrors[0].GetFormattedMessage().ShouldContain("FormatException");

            deserializedEvent.Should().BeEquivalentTo(finished);

            // There is nothing else in the stream
            memoryStream.Position.Should().Be(length);
        }

        [Fact]
        public void ForwardCompatibleRead_HandleRemovalOfDataFromEventDefinition()
        {
            // BuildErrorEventArgs error = new("Subcategory", "Code", "File", 1, 2, 3, 4, "Message", "HelpKeyword", "SenderName");
            BuildErrorEventArgs error = new(null, null, null, 1, 2, 3, 4, null, null, null);
            BuildFinishedEventArgs finished = new("Message", "HelpKeyword", true);

            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            using var binaryReader = new BinaryReader(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            buildEventArgsWriter.Write(error);

            int positionAfterFirstEvent = (int)memoryStream.Position;
            memoryStream.Position = 0;
            // event type
            Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int eventSizePos = (int)memoryStream.Position;
            int eventSize = Microsoft.Build.Shared.BinaryReaderExtensions.Read7BitEncodedInt(binaryReader);
            int positionAfterFirstEventSize = (int)memoryStream.Position;
            memoryStream.Position = eventSizePos;
            // simulate there are 4 bytes less in the future version of the event - while our reader expects those
            Microsoft.Build.Shared.BinaryWriterExtensions.Write7BitEncodedInt(binaryWriter, eventSize - 4);
            memoryStream.Position.ShouldBe(positionAfterFirstEventSize, "The event size need to be overwritten in place - without overwriting any bytes after the size info");
            // remove the 4 bytes - so that actual size of event is inline with it's written size.
            memoryStream.Position = positionAfterFirstEvent - 4;

            buildEventArgsWriter.Write(finished);

            // Remember num of bytes written - we should read them all.
            long length = memoryStream.Length;
            // Now move back to the beginning of the stream and start reading.
            memoryStream.Position = 0;

            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion)
            {
                SkipUnknownEvents = true
            };

            List<BinaryLogReaderErrorEventArgs> readerErrors = new();
            buildEventArgsReader.RecoverableReadError += readerErrors.Add;

            var deserializedEvent = buildEventArgsReader.Read();

            readerErrors.Count.ShouldBe(1);
            readerErrors[0].ErrorType.ShouldBe(ReaderErrorType.UnknownFormatOfEventData);
            readerErrors[0].RecordKind.ShouldBe(BinaryLogRecordKind.Error);
            readerErrors[0].GetFormattedMessage().ShouldContain("EndOfStreamException");

            deserializedEvent.Should().BeEquivalentTo(finished);

            // There is nothing else in the stream
            memoryStream.Position.Should().Be(length);
        }

        private string ToString(BuildEventContext context)
        {
            return $"{context.BuildRequestId} {context.NodeId} {context.ProjectContextId} {context.ProjectInstanceId} {context.SubmissionId} {context.TargetId} {context.TaskId}";
        }

        private string ToString(IEnumerable<ITaskItem> items)
        {
            return string.Join(";", items.Select(i => ToString(i)));
        }

        private string ToString(ITaskItem i)
        {
            return i.ItemSpec + string.Join(";", i.CloneCustomMetadata().Keys.OfType<string>().Select(k => i.GetMetadata(k)));
        }

        private string ToString(IReadOnlyDictionary<EvaluationLocation, ProfiledLocation> items)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.AppendLine(item.Key.ToString());
                sb.AppendLine(item.Value.ToString());
            }

            return sb.ToString();
        }

        private void Roundtrip<T>(T args, params Func<T, string>[] fieldsToCompare)
            where T : BuildEventArgs
        {
            var memoryStream = new MemoryStream();
            using var binaryWriter = new BinaryWriter(memoryStream);
            var buildEventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            buildEventArgsWriter.Write(args);

            long length = memoryStream.Length;

            memoryStream.Position = 0;

            using var binaryReader = new BinaryReader(memoryStream);
            using var buildEventArgsReader = new BuildEventArgsReader(binaryReader, BinaryLogger.FileFormatVersion);
            var deserializedArgs = (T)buildEventArgsReader.Read();

            Assert.Equal(length, memoryStream.Position);

            Assert.NotNull(deserializedArgs);
            Assert.Equal(typeof(T), deserializedArgs.GetType());

            foreach (var field in fieldsToCompare)
            {
                var expected = field(args);
                var actual = field(deserializedArgs);
                Assert.Equal(expected, actual);
            }
        }
    }
}
