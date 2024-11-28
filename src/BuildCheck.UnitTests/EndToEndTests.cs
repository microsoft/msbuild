﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Build.BuildCheck.UnitTests;

public class EndToEndTests : IDisposable
{
    private const string EditorConfigFileName = ".editorconfig";

    private readonly TestEnvironment _env;

    public EndToEndTests(ITestOutputHelper output)
    {
        _env = TestEnvironment.Create(output);

        // this is needed to ensure the binary logger does not pollute the environment
        _env.WithEnvironmentInvariant();
    }

    private static string AssemblyLocation { get; } = Path.Combine(Path.GetDirectoryName(typeof(EndToEndTests).Assembly.Location) ?? AppContext.BaseDirectory);

    private static string TestAssetsRootPath { get; } = Path.Combine(AssemblyLocation, "TestAssets");

    public void Dispose() => _env.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PropertiesUsageAnalyzerTest(bool buildInOutOfProcessNode)
    {
        PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode,
            out TransientTestFile projectFile,
            out _,
            "PropsCheckTest.csproj");

        string output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path} -check", out bool success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue(output);

        output.ShouldMatch(@"BC0201: .* Property: 'MyProp11'");
        output.ShouldMatch(@"BC0202: .* Property: 'MyPropT2'");
        output.ShouldMatch(@"BC0203: .* Property: 'MyProp13'");

        // each finding should be found just once - but reported twice, due to summary
        Regex.Matches(output, "BC0201: .* Property").Count.ShouldBe(2);
        Regex.Matches(output, "BC0202: .* Property").Count.ShouldBe(2);
        Regex.Matches(output, "BC0203 .* Property").Count.ShouldBe(2);
    }

    // <EmbeddedResource Update = "Resource1.cs.resx" />

    [Theory]
    [InlineData(
        "cs",
        "cs",
        """<EmbeddedResource Update = "Resource1.cs.resx" />""",
        "warning BC0105: .* 'Resource1\\.cs\\.resx'")]
    // Following tests are prepared after the EmbeddedCulture handling fix is merged: https://github.com/dotnet/msbuild/pull/11000
    ////[InlineData(
    ////    "xyz",
    ////    "xyz",
    ////    """<EmbeddedResource Update = "Resource1.xyz.resx" />""",
    ////    "warning BC0105: .* 'Resource1\\.xyz\\.resx'")]
    ////[InlineData(
    ////    "xyz",
    ////    "zyx",
    ////    """<EmbeddedResource Update = "Resource1.zyx.resx" Culture="xyz" />""",
    ////    "")]
    public void EmbeddedResourceCheckTest(string culture, string resourceExtension, string resourceElement, string expectedDiagnostic)
    {
        EmbedResourceTestOutput output = RunEmbeddedResourceTest(resourceElement, resourceExtension);

        // each finding should be found just once - but reported twice, due to summary
        if (!string.IsNullOrEmpty(expectedDiagnostic))
        {
            Regex.Matches(output.LogOutput, expectedDiagnostic).Count.ShouldBe(2);
        }

        AssertHasResourceForCulture("en");
        AssertHasResourceForCulture(culture);
        output.DepsJsonResources.Count.ShouldBe(2);

        void AssertHasResourceForCulture(string culture)
        {
            KeyValuePair<string, JsonNode?> resource = output.DepsJsonResources.FirstOrDefault(
                o => o.Value?["locale"]?.ToString().Equals(culture, StringComparison.Ordinal) ?? false);
            resource.Equals(default(KeyValuePair<string, JsonNode?>)).ShouldBe(false,
                $"Resource for culture {culture} was not found in deps.json:{Environment.NewLine}{output.DepsJsonResources.ToString()}");

            resource.Key.ShouldBeEquivalentTo($"{culture}/ReferencedProject.resources.dll",
                $"Unexpected resource for culture {culture} was found in deps.json:{Environment.NewLine}{output.DepsJsonResources.ToString()}");
        }
    }

    private readonly record struct EmbedResourceTestOutput(String LogOutput, JsonObject DepsJsonResources);

    private EmbedResourceTestOutput RunEmbeddedResourceTest(string resourceXmlToAdd, string resourceExtension)
    {
        string testAssetsFolderName = "EmbeddedResourceTest";
        const string entryProjectName = "EntryProject";
        const string referencedProjectName = "ReferencedProject";
        const string templateToReplace = "###EmbeddedResourceToAdd";
        TransientTestFolder workFolder = _env.CreateFolder(createFolder: true);

        CopyFilesRecursively(Path.Combine(TestAssetsRootPath, testAssetsFolderName), workFolder.Path);
        ReplaceStringInFile(Path.Combine(workFolder.Path, referencedProjectName, $"{referencedProjectName}.csproj"),
            templateToReplace, resourceXmlToAdd);
        File.Copy(
            Path.Combine(workFolder.Path, referencedProjectName, "Resource1.resx"),
            Path.Combine(workFolder.Path, referencedProjectName, $"Resource1.{resourceExtension}.resx"));

        _env.SetCurrentDirectory(Path.Combine(workFolder.Path, entryProjectName));

        string output = RunnerUtilities.ExecBootstrapedMSBuild("-check -restore", out bool success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue();

        string[] depsFiles = Directory.GetFiles(Path.Combine(workFolder.Path, entryProjectName), $"{entryProjectName}.deps.json", SearchOption.AllDirectories);
        depsFiles.Length.ShouldBe(1);

        JsonNode? depsJson = JsonObject.Parse(File.ReadAllText(depsFiles[0]));

        depsJson.ShouldNotBeNull("Valid deps.json file expected");

        var resources = depsJson!["targets"]?.AsObject().First().Value?[$"{referencedProjectName}/1.0.0"]?["resources"]?.AsObject();

        resources.ShouldNotBeNull("Expected deps.json with 'resources' section");

        return new(output, resources);

        void ReplaceStringInFile(string filePath, string original, string replacement)
        {
            File.Exists(filePath).ShouldBeTrue($"File {filePath} expected to exist.");
            string text = File.ReadAllText(filePath);
            text = text.Replace(original, replacement);
            File.WriteAllText(filePath, text);
        }
    }

    private static void CopyFilesRecursively(string sourcePath, string targetPath)
    {
        // First Create all directories
        foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
        }

        // Then copy all the files & Replaces any files with the same name
        foreach (string newPath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
        }
    }


    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WarningsCountExceedsLimitTest(bool buildInOutOfProcessNode, bool limitReportsCount)
    {
        PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode,
            out TransientTestFile projectFile,
            out _,
            "PropsCheckTestWithLimit.csproj");

        if (limitReportsCount)
        {
            _env.SetEnvironmentVariable("MSBUILDDONOTLIMITBUILDCHECKRESULTSNUMBER", "0");
        }
        else
        {
            _env.SetEnvironmentVariable("MSBUILDDONOTLIMITBUILDCHECKRESULTSNUMBER", "1");
        }

        string output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path} -check", out bool success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue(output);


        // each finding should be found just once - but reported twice, due to summary
        if (limitReportsCount)
        {
            output.ShouldMatch(@"has exceeded the maximum number of results allowed");
            Regex.Matches(output, "BC0202: .* Property").Count.ShouldBe(2);
            Regex.Matches(output, "BC0203: .* Property").Count.ShouldBe(38);
        }
        else
        {
            Regex.Matches(output, "BC0202: .* Property").Count.ShouldBe(2);
            Regex.Matches(output, "BC0203: .* Property").Count.ShouldBe(42);
        }
    }


    [Fact]
    public void ConfigChangeReflectedOnReuse()
    {
        PrepareSampleProjectsAndConfig(
            // we need out of proc build - to test node reuse
            true,
            out TransientTestFile projectFile,
            out TransientTestFile editorconfigFile,
            "PropsCheckTest.csproj");

        // Build without BuildCheck - no findings should be reported
        string output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path}", out bool success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue(output);
        output.ShouldNotContain("BC0201");
        output.ShouldNotContain("BC0202");
        output.ShouldNotContain("BC0203");

        // Build with BuildCheck - findings should be reported
        output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path} -check", out success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue(output);
        output.ShouldContain("warning BC0201");
        output.ShouldContain("warning BC0202");
        output.ShouldContain("warning BC0203");

        // Flip config in editorconfig
        string editorConfigChange = """
                                    
                                    build_check.BC0201.Severity=error
                                    build_check.BC0202.Severity=error
                                    build_check.BC0203.Severity=error
                                    """;

        File.AppendAllText(editorconfigFile.Path, editorConfigChange);

        // Build with BuildCheck - findings with new severity should be reported
        output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path} -check", out success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        // build should fail due to error checks
        success.ShouldBeFalse(output);
        output.ShouldContain("error BC0201");
        output.ShouldContain("error BC0202");
        output.ShouldContain("error BC0203");

        // Build without BuildCheck - no findings should be reported
        output = RunnerUtilities.ExecBootstrapedMSBuild($"{projectFile.Path}", out success);
        _env.Output.WriteLine(output);
        _env.Output.WriteLine("=========================");
        success.ShouldBeTrue(output);
        output.ShouldNotContain("BC0201");
        output.ShouldNotContain("BC0202");
        output.ShouldNotContain("BC0203");
    }


    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SampleCheckIntegrationTest_CheckOnBuild(bool buildInOutOfProcessNode, bool checkRequested)
    {
        PrepareSampleProjectsAndConfig(buildInOutOfProcessNode, out TransientTestFile projectFile, new List<(string, string)>() { ("BC0101", "warning") });

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore" +
            (checkRequested ? " -check" : string.Empty), out bool success, false, _env.Output, timeoutMilliseconds: 120_000);
        _env.Output.WriteLine(output);

        success.ShouldBeTrue();

        // The check warnings should appear - but only if check was requested.
        if (checkRequested)
        {
            output.ShouldContain("BC0101");
            output.ShouldContain("BC0102");
            output.ShouldContain("BC0103");
            output.ShouldContain("BC0104");
        }
        else
        {
            output.ShouldNotContain("BC0101");
            output.ShouldNotContain("BC0102");
            output.ShouldNotContain("BC0103");
            output.ShouldNotContain("BC0104");
        }
    }

    [Theory]
    [InlineData(true, true, "warning")]
    [InlineData(true, true, "error")]
    [InlineData(true, true, "suggestion")]
    [InlineData(false, true, "warning")]
    [InlineData(false, true, "error")]
    [InlineData(false, true, "suggestion")]
    [InlineData(false, false, "warning")]
    public void SampleCheckIntegrationTest_ReplayBinaryLogOfCheckedBuild(bool buildInOutOfProcessNode, bool checkRequested, string BC0101Severity)
    {
        PrepareSampleProjectsAndConfig(buildInOutOfProcessNode, out TransientTestFile projectFile, new List<(string, string)>() { ("BC0101", BC0101Severity) });

        var projectDirectory = Path.GetDirectoryName(projectFile.Path);
        string logFile = _env.ExpectFile(".binlog").Path;

        _ = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore {(checkRequested ? "-check" : string.Empty)} -bl:{logFile}",
            out bool success, false, _env.Output, timeoutMilliseconds: 120_000);

        if (BC0101Severity != "error")
        {
            success.ShouldBeTrue();
        }

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
         $"{logFile} -flp:logfile={Path.Combine(projectDirectory!, "logFile.log")};verbosity=diagnostic",
         out success, false, _env.Output, timeoutMilliseconds: 120_000);

        _env.Output.WriteLine(output);

        if (BC0101Severity != "error")
        {
            success.ShouldBeTrue();
        }

        // The conflicting outputs warning appears - but only if check was requested
        if (checkRequested)
        {
            output.ShouldContain(FormatExpectedDiagOutput("BC0101", BC0101Severity));
            output.ShouldContain("BC0102");
            output.ShouldContain("BC0103");
        }
        else
        {
            output.ShouldNotContain("BC0101");
            output.ShouldNotContain("BC0102");
            output.ShouldNotContain("BC0103");
        }

        string FormatExpectedDiagOutput(string code, string severity)
        {
            string msbuildSeverity = severity.Equals("suggestion") ? "message" : severity;
            return $"{msbuildSeverity} {code}: https://aka.ms/buildcheck/codes#{code}";
        }
    }

    [Theory]
    [InlineData("warning", "warning BC0101", new string[] { "error BC0101" })]
    [InlineData("error", "error BC0101", new string[] { "warning BC0101" })]
    [InlineData("suggestion", "BC0101", new string[] { "error BC0101", "warning BC0101" })]
    [InlineData("default", "warning BC0101", new string[] { "error BC0101" })]
    [InlineData("none", null, new string[] { "BC0101" })]
    public void EditorConfig_SeverityAppliedCorrectly(string BC0101Severity, string? expectedOutputValues, string[] unexpectedOutputValues)
    {
        PrepareSampleProjectsAndConfig(true, out TransientTestFile projectFile, new List<(string, string)>() { ("BC0101", BC0101Severity) });

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore -check",
            out bool success, false, _env.Output, timeoutMilliseconds: 120_000);

        if (BC0101Severity != "error")
        {
            success.ShouldBeTrue();
        }

        if (!string.IsNullOrEmpty(expectedOutputValues))
        {
            output.ShouldContain(expectedOutputValues!);
        }

        foreach (string unexpectedOutputValue in unexpectedOutputValues)
        {
            output.ShouldNotContain(unexpectedOutputValue);
        }
    }

    [Fact(Skip = "https://github.com/dotnet/msbuild/issues/10702")]
    public void CheckHasAccessToAllConfigs()
    {
        using (var env = TestEnvironment.Create())
        {
            string checkCandidatePath = Path.Combine(TestAssetsRootPath, "CheckCandidate");
            string message = ": An extra message for the analyzer";
            string severity = "warning";

            // Can't use Transitive environment due to the need to dogfood local nuget packages.
            AddCustomDataSourceToNugetConfig(checkCandidatePath);
            string editorConfigName = Path.Combine(checkCandidatePath, EditorConfigFileName);
            File.WriteAllText(editorConfigName, ReadEditorConfig(
                new List<(string, string)>() { ("X01234", severity) },
                new List<(string, (string, string))>
                {
                    ("X01234",("setMessage", message))
                },
                checkCandidatePath));

            string projectCheckBuildLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.Combine(checkCandidatePath, $"CheckCandidate.csproj")} /m:1 -nr:False -restore -check -verbosity:n", out bool success, timeoutMilliseconds: 1200_0000);
            success.ShouldBeTrue();

            projectCheckBuildLog.ShouldContain("warning X01234");
            projectCheckBuildLog.ShouldContain(severity + message);

            // Cleanup
            File.Delete(editorConfigName);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SampleCheckIntegrationTest_CheckOnBinaryLogReplay(bool buildInOutOfProcessNode, bool checkRequested)
    {
        PrepareSampleProjectsAndConfig(buildInOutOfProcessNode, out TransientTestFile projectFile, new List<(string, string)>() { ("BC0101", "warning") });

        string? projectDirectory = Path.GetDirectoryName(projectFile.Path);
        string logFile = _env.ExpectFile(".binlog").Path;

        _ = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore -bl:{logFile}",
            out bool success, false, _env.Output, timeoutMilliseconds: 120_000);

        success.ShouldBeTrue();

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
         $"{logFile} -flp:logfile={Path.Combine(projectDirectory!, "logFile.log")};verbosity=diagnostic {(checkRequested ? "-check" : string.Empty)}",
         out success, false, _env.Output, timeoutMilliseconds: 120_000);

        _env.Output.WriteLine(output);

        success.ShouldBeTrue();

        // The conflicting outputs warning appears - but only if check was requested
        if (checkRequested)
        {
            output.ShouldContain("BC0101");
            output.ShouldContain("BC0102");
            output.ShouldContain("BC0103");
        }
        else
        {
            output.ShouldNotContain("BC0101");
            output.ShouldNotContain("BC0102");
            output.ShouldNotContain("BC0103");
        }
    }

    [Theory]
    [InlineData(null, new[] { "Property is derived from environment variable: 'TestFromTarget'.", "Property is derived from environment variable: 'TestFromEvaluation'." })]
    [InlineData(true, new[] { "Property is derived from environment variable: 'TestFromTarget' with value: 'FromTarget'.", "Property is derived from environment variable: 'TestFromEvaluation' with value: 'FromEvaluation'." })]
    [InlineData(false, new[] { "Property is derived from environment variable: 'TestFromTarget'.", "Property is derived from environment variable: 'TestFromEvaluation'." })]
    public void NoEnvironmentVariableProperty_Test(bool? customConfigEnabled, string[] expectedMessages)
    {
        List<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? customConfigData = null;

        if (customConfigEnabled.HasValue)
        {
            customConfigData = new List<(string, (string, string))>()
            {
                ("BC0103", ("allow_displaying_environment_variable_value", customConfigEnabled.Value ? "true" : "false")),
            };
        }

        PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode: true,
            out TransientTestFile projectFile,
            new List<(string, string)>() { ("BC0103", "error") },
            customConfigData);

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore -check", out bool success, false, _env.Output);

        foreach (string expectedMessage in expectedMessages)
        {
            output.ShouldContain(expectedMessage);
        }
    }

    [Theory]
    [InlineData(EvaluationCheckScope.ProjectFileOnly)]
    [InlineData(EvaluationCheckScope.WorkTreeImports)]
    [InlineData(EvaluationCheckScope.All)]
    public void NoEnvironmentVariableProperty_Scoping(EvaluationCheckScope scope)
    {
        List<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? customConfigData = null;

        string editorconfigScope = scope switch
        {
            EvaluationCheckScope.ProjectFileOnly => "project_file",
            EvaluationCheckScope.WorkTreeImports => "work_tree_imports",
            EvaluationCheckScope.All => "all",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        customConfigData = new List<(string, (string, string))>()
        {
            ("BC0103", ("scope", editorconfigScope)),
        };

        PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode: true,
            out TransientTestFile projectFile,
            new List<(string, string)>() { ("BC0103", "error") },
            customConfigData);

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore -check", out bool success, false, _env.Output);

        if (scope == EvaluationCheckScope.ProjectFileOnly)
        {
            output.ShouldNotContain("Property is derived from environment variable: 'TestImported'. Properties should be passed explicitly using the /p option.");
        }
        else
        {
            output.ShouldContain("Property is derived from environment variable: 'TestImported'. Properties should be passed explicitly using the /p option.");
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void NoEnvironmentVariableProperty_DeferredProcessing(bool warnAsError, bool warnAsMessage)
    {
        PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode: true,
            out TransientTestFile projectFile,
            new List<(string, string)>() { ("BC0103", "warning") });

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore -check" +
            (warnAsError ? " /p:warn2err=BC0103" : "") + (warnAsMessage ? " /p:warn2msg=BC0103" : ""), out bool success,
            false, _env.Output);

        success.ShouldBe(!warnAsError);

        if (warnAsMessage)
        {
            output.ShouldNotContain("warning BC0103");
            output.ShouldNotContain("error BC0103");
        }
        else if (warnAsError)
        {
            output.ShouldNotContain("warning BC0103");
            output.ShouldContain("error BC0103");
        }
        else
        {
            output.ShouldContain("warning BC0103");
            output.ShouldNotContain("error BC0103");
        }
    }

    [Theory(Skip = "https://github.com/dotnet/msbuild/issues/10702")]
    [InlineData("CheckCandidate", new[] { "CustomRule1", "CustomRule2" })]
    [InlineData("CheckCandidateWithMultipleChecksInjected", new[] { "CustomRule1", "CustomRule2", "CustomRule3" }, true)]
    public void CustomCheckTest_NoEditorConfig(string checkCandidate, string[] expectedRegisteredRules, bool expectedRejectedChecks = false)
    {
        using (var env = TestEnvironment.Create())
        {
            var checkCandidatePath = Path.Combine(TestAssetsRootPath, checkCandidate);
            AddCustomDataSourceToNugetConfig(checkCandidatePath);

            string projectCheckBuildLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.Combine(checkCandidatePath, $"{checkCandidate}.csproj")} /m:1 -nr:False -restore -check -verbosity:n",
                out bool successBuild);

            foreach (string registeredRule in expectedRegisteredRules)
            {
                projectCheckBuildLog.ShouldContain(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("CustomCheckSuccessfulAcquisition", registeredRule));
            }

            if (!expectedRejectedChecks)
            {
                successBuild.ShouldBeTrue(projectCheckBuildLog);
            }
            else
            {
                projectCheckBuildLog.ShouldContain(ResourceUtilities.FormatResourceStringStripCodeAndKeyword(
                    "CustomCheckBaseTypeNotAssignable",
                    "InvalidCheck",
                    "InvalidCustomCheck, Version=15.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));
            }
        }
    }

    [Theory]
    [InlineData("CheckCandidate", "X01234", "error", "error X01234: http://samplelink.com/X01234")]
    [InlineData("CheckCandidateWithMultipleChecksInjected", "X01234", "warning", "warning X01234: http://samplelink.com/X01234")]
    public void CustomCheckTest_WithEditorConfig(string checkCandidate, string ruleId, string severity, string expectedMessage)
    {
        using (var env = TestEnvironment.Create())
        {
            string checkCandidatePath = Path.Combine(TestAssetsRootPath, checkCandidate);

            // Can't use Transitive environment due to the need to dogfood local nuget packages.
            AddCustomDataSourceToNugetConfig(checkCandidatePath);
            string editorConfigName = Path.Combine(checkCandidatePath, EditorConfigFileName);
            File.WriteAllText(editorConfigName, ReadEditorConfig(
                new List<(string, string)>() { (ruleId, severity) },
                ruleToCustomConfig: null,
                checkCandidatePath));

            string projectCheckBuildLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.Combine(checkCandidatePath, $"{checkCandidate}.csproj")} /m:1 -nr:False -restore -check -verbosity:n", out bool _);

            projectCheckBuildLog.ShouldContain(expectedMessage);

            // Cleanup
            File.Delete(editorConfigName);
        }
    }

    [Theory]
    [InlineData("X01236", "ErrorOnInitializeCheck", "Something went wrong initializing")]
    [InlineData("X01237", "ErrorOnRegisteredAction", "something went wrong when executing registered action")]
    [InlineData("X01238", "ErrorWhenRegisteringActions", "something went wrong when registering actions")]
    public void CustomChecksFailGracefully(string ruleId, string friendlyName, string expectedMessage)
    {
        using (var env = TestEnvironment.Create())
        {
            string checkCandidate = "CheckCandidateWithMultipleChecksInjected";
            string checkCandidatePath = Path.Combine(TestAssetsRootPath, checkCandidate);

            // Can't use Transitive environment due to the need to dogfood local nuget packages.
            AddCustomDataSourceToNugetConfig(checkCandidatePath);
            string editorConfigName = Path.Combine(checkCandidatePath, EditorConfigFileName);
            File.WriteAllText(editorConfigName, ReadEditorConfig(
                new List<(string, string)>() { (ruleId, "warning") },
                ruleToCustomConfig: null,
                checkCandidatePath));

            string projectCheckBuildLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.Combine(checkCandidatePath, $"{checkCandidate}.csproj")} /m:1 -nr:False -restore -check -verbosity:n", out bool success);

            success.ShouldBeTrue();
            projectCheckBuildLog.ShouldContain(expectedMessage);
            projectCheckBuildLog.ShouldNotContain("This check should have been disabled");
            projectCheckBuildLog.ShouldContain($"Dismounting check '{friendlyName}'");

            // Cleanup
            File.Delete(editorConfigName);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoesNotRunOnRestore(bool buildInOutOfProcessNode)
    {
        PrepareSampleProjectsAndConfig(buildInOutOfProcessNode, out TransientTestFile projectFile, new List<(string, string)>() { ("BC0101", "warning") });

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -t:restore -check",
            out bool success);

        success.ShouldBeTrue();
        output.ShouldNotContain("BC0101");
        output.ShouldNotContain("BC0102");
        output.ShouldNotContain("BC0103");
    }

#if NET
    [Fact]
    public void TestBuildCheckTemplate()
    {
        TransientTestFolder workFolder = _env.CreateFolder(createFolder: true);
        var nugetTemplateName = "nugetTemplate.config";
        var nugetTemplatePath = Path.Combine(TestAssetsRootPath, "CheckCandidate", nugetTemplateName);
        File.Copy(nugetTemplatePath, Path.Combine(workFolder.Path, nugetTemplateName));
        AddCustomDataSourceToNugetConfig(workFolder.Path);

        var ExecuteDotnetCommand = (string parameters) =>
        {
            string output = RunnerUtilities.RunProcessAndGetOutput("dotnet", parameters, out bool success);
            return output;
        };

        var buildCheckTemplatePath = Path.Combine(BuildCheckUnitTestsConstants.RepoRoot, "template_feed", "content", "Microsoft.CheckTemplate");
        var templateShortName = "msbuildcheck";
        var projectName = "BuildCheck";
        var installLog = ExecuteDotnetCommand($"new install {buildCheckTemplatePath}");
        installLog.ShouldContain($"Success: {buildCheckTemplatePath} installed the following templates:");
        var creationLog = ExecuteDotnetCommand($"new {templateShortName} -n {projectName} --MicrosoftBuildVersion {BuildCheckUnitTestsConstants.MicrosoftBuildPackageVersion} -o {workFolder.Path} ");
        creationLog.ShouldContain("The template \"MSBuild custom check skeleton project.\" was created successfully.");
        var buildLog = ExecuteDotnetCommand($"build {workFolder.Path}");
        buildLog.ShouldContain("Build succeeded.");
        ExecuteDotnetCommand($"new -u {buildCheckTemplatePath}");
    }
#endif

    private void AddCustomDataSourceToNugetConfig(string checkCandidatePath)
    {
        var nugetTemplatePath = Path.Combine(checkCandidatePath, "nugetTemplate.config");

        var doc = new XmlDocument();
        doc.LoadXml(File.ReadAllText(nugetTemplatePath));
        if (doc.DocumentElement != null)
        {
            XmlNode? packageSourcesNode = doc.SelectSingleNode("//packageSources");

            // The test packages are generated during the test project build and saved in CustomChecks folder.
            string checksPackagesPath = Path.Combine(Directory.GetParent(AssemblyLocation)?.Parent?.FullName ?? string.Empty, "CustomChecks");
            AddPackageSource(doc, packageSourcesNode, "Key", checksPackagesPath);

            doc.Save(Path.Combine(checkCandidatePath, "nuget.config"));
        }
    }

    private void AddPackageSource(XmlDocument doc, XmlNode? packageSourcesNode, string key, string value)
    {
        if (packageSourcesNode != null)
        {
            XmlElement addNode = doc.CreateElement("add");

            PopulateXmlAttribute(doc, addNode, "key", key);
            PopulateXmlAttribute(doc, addNode, "value", value);

            packageSourcesNode.AppendChild(addNode);
        }
    }

    private void PopulateXmlAttribute(XmlDocument doc, XmlNode node, string attributeName, string attributeValue)
    {
        node.ShouldNotBeNull($"The attribute {attributeName} can not be populated with {attributeValue}. Xml node is null.");
        var attribute = doc.CreateAttribute(attributeName);
        attribute.Value = attributeValue;
        node.Attributes!.Append(attribute);
    }

    private void PrepareSampleProjectsAndConfig(
        bool buildInOutOfProcessNode,
        out TransientTestFile projectFile,
        out TransientTestFile editorconfigFile,
        string entryProjectAssetName,
        IEnumerable<string>? supplementalAssetNames = null,
        IEnumerable<(string RuleId, string Severity)>? ruleToSeverity = null,
        IEnumerable<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? ruleToCustomConfig = null)
    {
        string testAssetsFolderName = "SampleCheckIntegrationTest";
        TransientTestFolder workFolder = _env.CreateFolder(createFolder: true);
        TransientTestFile testFile = _env.CreateFile(workFolder, "somefile");

        string contents = ReadAndAdjustProjectContent(entryProjectAssetName);
        projectFile = _env.CreateFile(workFolder, entryProjectAssetName, contents);

        foreach (string supplementalAssetName in supplementalAssetNames ?? Enumerable.Empty<string>())
        {
            string supplementalContent = ReadAndAdjustProjectContent(supplementalAssetName);
            TransientTestFile supplementalFile = _env.CreateFile(workFolder, supplementalAssetName, supplementalContent);
        }

        editorconfigFile = _env.CreateFile(workFolder, ".editorconfig", ReadEditorConfig(ruleToSeverity, ruleToCustomConfig, testAssetsFolderName));

        // OSX links /var into /private, which makes Path.GetTempPath() return "/var..." but Directory.GetCurrentDirectory return "/private/var...".
        // This discrepancy breaks path equality checks in MSBuild checks if we pass to MSBuild full path to the initial project.
        // See if there is a way of fixing it in the engine - tracked: https://github.com/orgs/dotnet/projects/373/views/1?pane=issue&itemId=55702688.
        _env.SetCurrentDirectory(Path.GetDirectoryName(projectFile.Path));

        _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", buildInOutOfProcessNode ? "1" : "0");

        // Needed for testing check BC0103
        _env.SetEnvironmentVariable("TestFromTarget", "FromTarget");
        _env.SetEnvironmentVariable("TestFromEvaluation", "FromEvaluation");
        _env.SetEnvironmentVariable("TestImported", "FromEnv");

        string ReadAndAdjustProjectContent(string fileName) =>
            File.ReadAllText(Path.Combine(TestAssetsRootPath, testAssetsFolderName, fileName))
                .Replace("TestFilePath", testFile.Path)
                .Replace("WorkFolderPath", workFolder.Path);
    }

    private void PrepareSampleProjectsAndConfig(
        bool buildInOutOfProcessNode,
        out TransientTestFile projectFile,
        IEnumerable<(string RuleId, string Severity)>? ruleToSeverity,
        IEnumerable<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? ruleToCustomConfig = null)
        => PrepareSampleProjectsAndConfig(
            buildInOutOfProcessNode,
            out projectFile,
            out _,
            "Project1.csproj",
            new[] { "Project2.csproj", "ImportedFile1.props" },
            ruleToSeverity,
            ruleToCustomConfig);

    private string ReadEditorConfig(
        IEnumerable<(string RuleId, string Severity)>? ruleToSeverity,
        IEnumerable<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? ruleToCustomConfig,
        string testAssetsFolderName)
    {
        string configContent = File.ReadAllText(Path.Combine(TestAssetsRootPath, testAssetsFolderName, $"{EditorConfigFileName}test"));

        PopulateRuleToSeverity(ruleToSeverity, ref configContent);
        PopulateRuleToCustomConfig(ruleToCustomConfig, ref configContent);

        return configContent;
    }

    private void PopulateRuleToSeverity(IEnumerable<(string RuleId, string Severity)>? ruleToSeverity, ref string configContent)
    {
        if (ruleToSeverity != null && ruleToSeverity.Any())
        {
            foreach (var rule in ruleToSeverity)
            {
                configContent = configContent.Replace($"build_check.{rule.RuleId}.Severity={rule.RuleId}Severity", $"build_check.{rule.RuleId}.Severity={rule.Severity}");
            }
        }
    }

    private void PopulateRuleToCustomConfig(IEnumerable<(string RuleId, (string ConfigKey, string Value) CustomConfig)>? ruleToCustomConfig, ref string configContent)
    {
        if (ruleToCustomConfig != null && ruleToCustomConfig.Any())
        {
            foreach (var rule in ruleToCustomConfig)
            {
                configContent = configContent.Replace($"build_check.{rule.RuleId}.CustomConfig=dummy", $"build_check.{rule.RuleId}.{rule.CustomConfig.ConfigKey}={rule.CustomConfig.Value}");
            }
        }
    }
}
