// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Microsoft.Build.Utilities;
#if NETFRAMEWORK
using Microsoft.IO;
#else
using System.IO;
#endif
using Shouldly;
using VerifyTests;
using VerifyXunit;
using Xunit;
using Xunit.Abstractions;
using static VerifyXunit.Verifier;

#nullable disable

namespace Microsoft.Build.Tasks.UnitTests
{
    [UsesVerify]
    public class RoslynCodeTaskFactory_Tests
    {
        private const string TaskName = "MyInlineTask";

        private readonly VerifySettings _verifySettings;

        private readonly ITestOutputHelper _testOutputHelper;

        public RoslynCodeTaskFactory_Tests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            UseProjectRelativeDirectory("TaskFactorySource");

            _verifySettings = new();
            _verifySettings.UniqueForRuntime();
            _verifySettings.ScrubLinesContaining("Runtime Version:");
        }

        [Fact]
        public void InlineTaskWithAssemblyPlatformAgnostic()
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                TransientTestFolder folder = env.CreateFolder(createFolder: true);
                string location = Assembly.GetExecutingAssembly().Location;
                TransientTestFile inlineTask = env.CreateFile(folder, "5106.proj", @$"
<Project>

  <UsingTask TaskName=""MyInlineTask"" TaskFactory=""RoslynCodeTaskFactory"" AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"">
    <Task>
      <Reference Include=""" + Path.Combine(Path.GetDirectoryName(location), "..", "..", "..", "Samples", "Dependency",
#if DEBUG
      "Debug"
#else
      "Release"
#endif
      , "net472", "Dependency.dll") + @""" />
      <Using Namespace=""Dependency"" />
      <Code Type=""Fragment"" Language=""cs"" >
<![CDATA[
Log.LogError(Alpha.GetString());
]]>
      </Code>
    </Task>
  </UsingTask>

<Target Name=""ToRun"">
  <MyInlineTask/>
</Target>

</Project>
");

                string output = RunnerUtilities.ExecMSBuild(inlineTask.Path, out bool success);
                success.ShouldBeTrue(output);
                output.ShouldContain("Alpha.GetString");
            }
        }

        [Fact]
        [SkipOnPlatform(TestPlatforms.AnyUnix, ".NETFramework 4.0 isn't on unix machines.")]
        public void InlineTaskWithAssembly()
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                TransientTestFolder folder = env.CreateFolder(createFolder: true);
                TransientTestFile assemblyProj = env.CreateFile(folder, "5106.csproj", @$"
                    <Project DefaultTargets=""Build"">
                        <PropertyGroup>
                            <TargetFrameworkVersion>{MSBuildConstants.StandardTestTargetFrameworkVersion}</TargetFrameworkVersion>
                            <OutputType>Library</OutputType>
                        </PropertyGroup>
                        <ItemGroup>
                            <Reference Include=""System""/>
                            <Compile Include=""Class1.cs""/>
                        </ItemGroup>
                        <Import Project=""$(MSBuildBinPath)\Microsoft.CSharp.targets"" />
                    </Project>
");
                
                _testOutputHelper.WriteLine($"AssemblyProj: {assemblyProj.Path}, {assemblyProj.ToString()}");
                TransientTestFile csFile = env.CreateFile(folder, "Class1.cs", @"
using System;

namespace _5106 {
    public class Class1 {
        public static string ToPrint() {
            return ""Hello!"";
        }
    }
}
");
                string output = RunnerUtilities.ExecMSBuild(assemblyProj.Path + $" /p:OutDir={Path.Combine(folder.Path, "subFolder")} /restore", out bool success, _testOutputHelper);
                success.ShouldBeTrue(output);

                TransientTestFile inlineTask = env.CreateFile(folder, "5106.proj", @$"
<Project>

  <UsingTask TaskName=""MyInlineTask"" TaskFactory=""RoslynCodeTaskFactory"" AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"">
    <Task>
      <Reference Include=""{Path.Combine(folder.Path, "subFolder", "5106.dll")}"" />
      <Reference Include=""netstandard"" />
      <Using Namespace=""_5106"" />
      <Code Type=""Fragment"" Language=""cs"" >
<![CDATA[
Log.LogError(Class1.ToPrint());
]]>
      </Code>
    </Task>
  </UsingTask>

<Target Name=""ToRun"">
  <MyInlineTask/>
</Target>

</Project>
");
                output = RunnerUtilities.ExecMSBuild(inlineTask.Path, out success);
                success.ShouldBeTrue();
                output.ShouldContain("Hello!");
            }
        }

        [Fact]
        public void RoslynCodeTaskFactory_ReuseCompilation()
        {
            string text1 = $@"
<Project>

  <UsingTask
    TaskName=""Custom1""
    TaskFactory=""RoslynCodeTaskFactory""
    AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"" >
    <ParameterGroup>
      <SayHi ParameterType=""System.String"" Required=""true"" />
    </ParameterGroup>
    <Task>
      <Reference Include=""{typeof(Enumerable).Assembly.Location}"" />
      <Code Type=""Fragment"" Language=""cs"">
        Log.LogMessage(SayHi);
      </Code>
    </Task>
  </UsingTask>

    <Target Name=""Build"">
        <MSBuild Projects=""p2.proj"" Targets=""Build"" />
        <Custom1 SayHi=""hello1"" />
        <Custom1 SayHi=""hello2"" />
    </Target>

</Project>";

            var text2 = $@"
<Project>

  <UsingTask
    TaskName=""Custom1""
    TaskFactory=""RoslynCodeTaskFactory""
    AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"" >
    <ParameterGroup>
      <SayHi ParameterType=""System.String"" Required=""true"" />
    </ParameterGroup>
    <Task>
      <Reference Include=""{typeof(Enumerable).Assembly.Location}"" />
      <Code Type=""Fragment"" Language=""cs"">
        Log.LogMessage(SayHi);
      </Code>
    </Task>
  </UsingTask>

    <Target Name=""Build"">
        <Custom1 SayHi=""hello1"" />
        <Custom1 SayHi=""hello2"" />
    </Target>

</Project>";

            using var env = TestEnvironment.Create();

            var p2 = env.CreateTestProjectWithFiles("p2.proj", text2);
            text1 = text1.Replace("p2.proj", p2.ProjectFile);
            var p1 = env.CreateTestProjectWithFiles("p1.proj", text1);

            var logger = p1.BuildProjectExpectSuccess();
            var messages = logger
                .BuildMessageEvents
                .Where(m => m.Message == "Compiling task source code")
                .ToArray();

            // with broken cache we get two Compiling messages
            // as we fail to reuse the first assembly
            messages.Length.ShouldBe(1);
        }

        [Fact]
        public void VisualBasicFragment()
        {
            const string fragment = "Dim x = 0";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody: $"<Code Language=\"VB\">{fragment}</Code>",
                expectedCodeLanguage: "VB",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Fragment);
        }

        [Fact]
        public void VisualBasicFragmentWithProperties()
        {
            ICollection<TaskPropertyInfo> parameters = new List<TaskPropertyInfo>
            {
                new TaskPropertyInfo("Parameter1", typeof(string), output: false, required: true),
                new TaskPropertyInfo("Parameter2", typeof(string), output: true, required: false),
                new TaskPropertyInfo("Parameter3", typeof(string), output: true, required: true),
                new TaskPropertyInfo("Parameter4", typeof(ITaskItem), output: false, required: false),
                new TaskPropertyInfo("Parameter5", typeof(ITaskItem[]), output: false, required: false),
            };

            const string fragment = @"Dim x = 0";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody: $"<Code Language=\"VB\">{fragment}</Code>",
                expectedCodeLanguage: "VB",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Fragment,
                parameters: parameters);
        }

        [Fact]
        public void VisualBasicMethod()
        {
            const string method = @"Public Overrides Function Execute() As Boolean
            Dim x = 0
            Return True
        End Function";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody: $"<Code Language=\"VB\" Type=\"Method\">{method}</Code>",
                expectedCodeLanguage: "VB",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Method);
        }

        [Fact]
        public void CodeLanguageFromTaskBody()
        {
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"CS\">code</Code>", expectedCodeLanguage: "CS");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"cs\">code</Code>", expectedCodeLanguage: "CS");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"csharp\">code</Code>", expectedCodeLanguage: "CS");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"c#\">code</Code>", expectedCodeLanguage: "CS");

            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"VB\">code</Code>", expectedCodeLanguage: "VB");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"vb\">code</Code>", expectedCodeLanguage: "VB");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"visualbasic\">code</Code>", expectedCodeLanguage: "VB");
            TryLoadTaskBodyAndExpectSuccess("<Code Language=\"ViSuAl BaSic\">code</Code>", expectedCodeLanguage: "VB");

            // Default when the Language attribute is not present.
            TryLoadTaskBodyAndExpectSuccess("<Code>code</Code>", expectedCodeLanguage: "CS");
        }

        [Fact]
        public void CodeTypeFromTaskBody()
        {
            foreach (RoslynCodeTaskFactoryCodeType codeType in Enum.GetValues(typeof(RoslynCodeTaskFactoryCodeType)).Cast<RoslynCodeTaskFactoryCodeType>())
            {
                TryLoadTaskBodyAndExpectSuccess($"<Code Type=\"{codeType}\">code</Code>", expectedCodeType: codeType);
            }

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFile file = testEnvironment.CreateFile(fileName: "236D48CE30064161B31B55DBF088C8B2", contents: "6159BD98607A460AA4F11D2FA92E5436");

                // When Source is provided and Type is not provided, Type is expected to default to Type="Class".
                TryLoadTaskBodyAndExpectSuccess($"<Code Source=\"{file.Path}\"/>", expectedCodeType: RoslynCodeTaskFactoryCodeType.Class);

                foreach (RoslynCodeTaskFactoryCodeType codeType in Enum.GetValues(typeof(RoslynCodeTaskFactoryCodeType)).Cast<RoslynCodeTaskFactoryCodeType>())
                {
                    TryLoadTaskBodyAndExpectSuccess($"<Code Source=\"{file.Path}\" Type=\"{codeType}\">code</Code>", expectedCodeType: codeType);
                }
            }
        }

        [Fact]
        public void CSharpClass()
        {
            const string taskClassSourceCode = @"namespace InlineTask
{
    using Microsoft.Build.Utilities;

    public class HelloWorld : Task
    {
        public override bool Execute()
        {
            Log.LogMessage(""Hello, world!"");
            return !Log.HasLoggedErrors;
        }
    }
}
";

            TryLoadTaskBodyAndExpectSuccess(
                $"<Code Type=\"Class\">{taskClassSourceCode}</Code>",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Class,
                expectedCodeLanguage: "CS");
        }

        [Fact]
        public void CSharpFragment()
        {
            const string fragment = "int x = 0;";

            TryLoadTaskBodyAndExpectSuccess(taskBody: $"<Code>{fragment}</Code>", verifySource: true);
        }

        [Fact]
        public void CSharpFragmentWithProperties()
        {
            ICollection<TaskPropertyInfo> parameters = new List<TaskPropertyInfo>
            {
                new TaskPropertyInfo("Parameter1", typeof(string), output: false, required: true),
                new TaskPropertyInfo("Parameter2", typeof(string), output: true, required: false),
                new TaskPropertyInfo("Parameter3", typeof(string), output: true, required: true),
                new TaskPropertyInfo("Parameter4", typeof(ITaskItem), output: false, required: false),
                new TaskPropertyInfo("Parameter5", typeof(ITaskItem[]), output: false, required: false),
            };

            const string fragment = @"int x = 0;";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody: $"<Code>{fragment}</Code>",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Fragment,
                parameters: parameters);
        }

        [Fact]
        public void CSharpMethod()
        {
            const string method = @"public override bool Execute() { int x = 0; return true; }";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody: $"<Code Type=\"Method\">{method}</Code>",
                verifySource: true,
                expectedCodeType: RoslynCodeTaskFactoryCodeType.Method);
        }

        [Fact]
        public void CSharpClassSourceCodeFromFile()
        {
            const string taskClassSourceCode = @"namespace InlineTask
{
    using Microsoft.Build.Utilities;

    public class HelloWorld : Task
    {
        public override bool Execute()
        {
            Log.LogMessage(""Hello, world!"");
            return !Log.HasLoggedErrors;
        }
    }
}
";

            using (TestEnvironment env = TestEnvironment.Create())
            {
                TransientTestFile file = env.CreateFile(fileName: "CSharpClassSourceCodeFromFile.tmp", contents: taskClassSourceCode);

                TryLoadTaskBodyAndExpectSuccess(
                    $"<Code Source=\"{file.Path}\" />",
                    verifySource: true,
                    expectedCodeType: RoslynCodeTaskFactoryCodeType.Class,
                    expectedCodeLanguage: "CS");
            }
        }

        [Fact]
        public void CSharpFragmentSourceCodeFromFile()
        {
            const string sourceCodeFileContents = "int x = 0;";

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFile file = testEnvironment.CreateFile(fileName: "CSharpFragmentSourceCodeFromFile.tmp", contents: sourceCodeFileContents);

                TryLoadTaskBodyAndExpectSuccess(
                    $"<Code Source=\"{file.Path}\" Type=\"Fragment\"/>",
                    verifySource: true,
                    expectedCodeType: RoslynCodeTaskFactoryCodeType.Fragment);
            }
        }

        [Fact]
        public void CSharpMethodSourceCodeFromFile()
        {
            const string sourceCodeFileContents = @"public override bool Execute() { int x = 0; return true; }";

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFile file = testEnvironment.CreateFile(fileName: "CSharpMethodSourceCodeFromFile.tmp", contents: sourceCodeFileContents);

                TryLoadTaskBodyAndExpectSuccess(
                    $"<Code Source=\"{file.Path}\" Type=\"Method\"/>",
                    verifySource: true,
                    expectedCodeType: RoslynCodeTaskFactoryCodeType.Method);
            }
        }

        [Fact]
        public void EmptyCodeElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code />",
                expectedErrorMessage: "You must specify source code within the Code element or a path to a file containing source code.");
        }

        [Theory]
        [InlineData("")]
        [InlineData("Include=\"\"")]
        [InlineData("Include=\" \"")]
        public void EmptyIncludeAttributeOnReferenceElement(string includeSetting)
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: $"<Reference {includeSetting} />",
                expectedErrorMessage: $"The \"Include\" attribute of the <Reference> element in the task \"{TaskName}\" has been set but is empty. Make sure the attribute has a proper value.");
        }

        [Fact]
        public void EmptyLanguageAttributeOnCodeElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Language=\"\" />",
                expectedErrorMessage: "The \"Language\" attribute of the <Code> element has been set but is empty. If the \"Language\" attribute is set it must not be empty.");
        }

        [Fact]
        public void EmptyNamespaceAttributeOnUsingElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Using Namespace=\"\" />",
                expectedErrorMessage: "The \"Namespace\" attribute of the <Using> element has been set but is empty. If the \"Namespace\" attribute is set it must not be empty.");
        }

        [Fact]
        public void EmptySourceAttributeOnCodeElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Source=\"\" />",
                expectedErrorMessage: "The \"Source\" attribute of the <Code> element has been set but is empty. If the \"Source\" attribute is set it must not be empty.");
        }

        [Fact]
        public void EmptyTypeAttributeOnCodeElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Type=\"\" />",
                expectedErrorMessage: "The \"Type\" attribute of the <Code> element has been set but is empty. If the \"Type\" attribute is set it must not be empty.");
        }

        [Fact]
        public void IgnoreTaskCommentsAndWhiteSpace()
        {
            TryLoadTaskBodyAndExpectSuccess("<!-- Comment --><Code>code</Code>");
            TryLoadTaskBodyAndExpectSuccess("                <Code>code</Code>");
        }

        [Fact]
        public void InvalidCodeElementAttribute()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Invalid=\"Attribute\" />",
                expectedErrorMessage: "The attribute \"Invalid\" is not valid for the <Code> element.  Valid attributes are \"Language\", \"Source\", and \"Type\".");
        }

        [Fact]
        public void InvalidCodeLanguage()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Language=\"Invalid\" />",
                expectedErrorMessage: "The specified code language \"Invalid\" is invalid.  The supported code languages are \"CS, VB\".");
        }

        [Fact]
        public void InvalidCodeType()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code Type=\"Invalid\" />",
                expectedErrorMessage: "The specified code type \"Invalid\" is invalid.  The supported code types are \"Fragment, Method, Class\".");
        }

        [Fact]
        public void InvalidTaskChildElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Invalid />",
                expectedErrorMessage: "The element <Invalid> is not a valid child of the <Task> element.  Valid child elements are <Code>, <Reference>, and <Using>.");

            TryLoadTaskBodyAndExpectFailure(
                taskBody: "invalid<Code>code</Code>",
                expectedErrorMessage: "The element <Text> is not a valid child of the <Task> element.  Valid child elements are <Code>, <Reference>, and <Using>.");
        }

        [Fact]
        public void InvalidTaskXml()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<invalid xml",
                expectedErrorMessage: "The specified task XML is invalid.  '<' is an unexpected token. The expected token is '='. Line 1, position 19.");
        }

        [Fact]
        public void MissingCodeElement()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "",
                expectedErrorMessage: $"The <Code> element is missing for the \"{TaskName}\" task. This element is required.");
        }

        [Fact]
        public void MultipleCodeNodes()
        {
            TryLoadTaskBodyAndExpectFailure(
                taskBody: "<Code><![CDATA[]]></Code><Code></Code>",
                expectedErrorMessage: "Only one <Code> element can be specified.");
        }

        [Fact]
        public void NamespacesFromTaskBody()
        {
            const string taskBody = @"
                <Using Namespace=""namespace.A"" />
                <Using Namespace=""   namespace.B   "" />
                <Using Namespace=""namespace.C""></Using>
                <Code>code</Code>";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody,
                expectedNamespaces: new HashSet<string>
                {
                    "namespace.A",
                    "namespace.B",
                    "namespace.C",
                });
        }

        [Fact]
        public void ReferencesFromTaskBody()
        {
            const string taskBody = @"
                <Reference Include=""AssemblyA"" />
                <Reference Include=""   AssemblyB   "" />
                <Reference Include=""AssemblyC""></Reference>
                <Reference Include=""C:\Program Files(x86)\Common Files\Microsoft\AssemblyD.dll"" />
                <Code>code</Code>";

            TryLoadTaskBodyAndExpectSuccess(
                taskBody,
                expectedReferences: new HashSet<string>
                {
                    "AssemblyA",
                    "AssemblyB",
                    "AssemblyC",
                    @"C:\Program Files(x86)\Common Files\Microsoft\AssemblyD.dll"
                });
        }

        [Fact]
        public void SourceCodeFromFile()
        {
            const string sourceCodeFileContents = @"
1F214E27A13F432B9397F1733BC55929
9111DC29B0064E6994A68CFE465404D4";

            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                TransientTestFile file = testEnvironment.CreateFile(fileName: "CB3096DA4A454768AA9C0C4D422FC188.tmp", contents: sourceCodeFileContents);

                TryLoadTaskBodyAndExpectSuccess(
                    $"<Code Source=\"{file.Path}\"/>",
                    verifySource: true,
                    expectedCodeType: RoslynCodeTaskFactoryCodeType.Class);
            }
        }

        [Fact]
        public void MismatchedTaskNameAndTaskClassName()
        {
            const string taskName = "SayHello";
            const string className = "HelloWorld";
            taskName.ShouldNotBe(className, "The test is misconfigured.");
            string errorMessage = string.Format(ResourceUtilities.GetResourceString("CodeTaskFactory.CouldNotFindTaskInAssembly"), taskName);

            const string projectContent = @"<Project>
  <UsingTask TaskName=""" + taskName + @""" TaskFactory=""RoslynCodeTaskFactory"" AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"">
    <Task>
      <Code Type=""Class"">
namespace InlineTask
{
    using Microsoft.Build.Utilities;

    public class " + className + @" : Task
    {
        public override bool Execute()
        {
            Log.LogMessage(""Hello, world!"");
            return !Log.HasLoggedErrors;
        }
    }
}
      </Code>
    </Task>
  </UsingTask>
  <Target Name=""Build"">
    <" + taskName + @" />
  </Target>
</Project>";

            using (TestEnvironment env = TestEnvironment.Create())
            {
                TransientTestProjectWithFiles proj = env.CreateTestProjectWithFiles(projectContent);
                var logger = proj.BuildProjectExpectFailure();
                logger.AssertLogContains(errorMessage);
            }
        }

        [Fact]
        public void EmbedsGeneratedFromSourceFileInBinlog()
        {
            string taskName = "HelloTask";

            string sourceContent = $$"""
                namespace InlineTask
                {
                    using Microsoft.Build.Utilities;

                    public class {{taskName}} : Task
                    {
                        public override bool Execute()
                        {
                            Log.LogMessage("Hello, world!");
                            return !Log.HasLoggedErrors;
                        }
                    }
                }
                """;

            CodeTaskFactoryEmbeddedFileInBinlogTestHelper.BuildFromSourceAndCheckForEmbeddedFileInBinlog(
                FactoryType.RoslynCodeTaskFactory, taskName, sourceContent, true);
        }

        [Fact]
        public void EmbedsGeneratedFromSourceFileInBinlogWhenFailsToCompile()
        {
            string taskName = "HelloTask";

            string sourceContent = $$"""
                namespace InlineTask
                {
                    using Microsoft.Build.Utilities;

                    public class {{taskName}} : Task
                    {
                """;

            CodeTaskFactoryEmbeddedFileInBinlogTestHelper.BuildFromSourceAndCheckForEmbeddedFileInBinlog(
                FactoryType.RoslynCodeTaskFactory, taskName, sourceContent, false);
        }

        [Fact]
        public void EmbedsGeneratedFileInBinlog()
        {
            string taskXml = @"
                <Task>
                    <Code Type=""Fragment"" Language=""cs"">
                        <![CDATA[
                              Log.LogMessage(""Hello, World!"");
                		   ]]>
                    </Code>
                </Task>";

            CodeTaskFactoryEmbeddedFileInBinlogTestHelper.BuildAndCheckForEmbeddedFileInBinlog(
                FactoryType.RoslynCodeTaskFactory, "HelloTask", taskXml, true);
        }

        [Fact]
        public void EmbedsGeneratedFileInBinlogWhenFailsToCompile()
        {
            string taskXml = @"
                <Task>
                    <Code Type=""Fragment"" Language=""cs"">
                        <![CDATA[
                              Log.LogMessage(""Hello, World!
                		   ]]>
                    </Code>
                </Task>";

            CodeTaskFactoryEmbeddedFileInBinlogTestHelper.BuildAndCheckForEmbeddedFileInBinlog(
                FactoryType.RoslynCodeTaskFactory, "HelloTask", taskXml, false);
        }

#if !FEATURE_RUN_EXE_IN_TESTS
        [Fact]
        public void RoslynCodeTaskFactory_UsingAPI()
        {
            string text = $@"
<Project>

  <UsingTask
    TaskName=""Custom1""
    TaskFactory=""RoslynCodeTaskFactory""
    AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"" >
    <ParameterGroup>
      <SayHi ParameterType=""System.String"" Required=""true"" />
    </ParameterGroup>
    <Task>
      <Code Type=""Fragment"" Language=""cs"">
        <![CDATA[
        string sayHi = ""Hello "" + SayHi;
        Log.LogMessage(sayHi);
        ]]>
      </Code>
    </Task>
  </UsingTask>

    <Target Name=""Build"">
        <Custom1 SayHi=""World"" />
    </Target>

</Project>";

            using var env = TestEnvironment.Create();
            RunnerUtilities.ApplyDotnetHostPathEnvironmentVariable(env);
            var dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

            var project = env.CreateTestProjectWithFiles("p1.proj", text);
            var logger = project.BuildProjectExpectSuccess();
            var logLines = logger.AllBuildEvents.Select(a => a.Message);
            var log = string.Join("\n", logLines);
            logLines.Where(l => l.Contains(dotnetPath)).Count().ShouldBe(1, log);
        }
#endif

        private void TryLoadTaskBodyAndExpectFailure(string taskBody, string expectedErrorMessage)
        {
            if (expectedErrorMessage == null)
            {
                throw new ArgumentNullException(nameof(expectedErrorMessage));
            }

            MockEngine buildEngine = new MockEngine();

            TaskLoggingHelper log = new TaskLoggingHelper(buildEngine, TaskName)
            {
                TaskResources = Shared.AssemblyResources.PrimaryResources
            };

            bool success = RoslynCodeTaskFactory.TryLoadTaskBody(log, TaskName, taskBody, new List<TaskPropertyInfo>(), out RoslynCodeTaskFactoryTaskInfo _);

            success.ShouldBeFalse();

            buildEngine.Errors.ShouldBe(1);

            buildEngine.Log.ShouldContain(expectedErrorMessage, customMessage: buildEngine.Log);
        }

        private void TryLoadTaskBodyAndExpectSuccess(
            string taskBody,
            ICollection<TaskPropertyInfo> parameters = null,
            ISet<string> expectedReferences = null,
            ISet<string> expectedNamespaces = null,
            string expectedCodeLanguage = null,
            RoslynCodeTaskFactoryCodeType? expectedCodeType = null,
            bool verifySource = false,
            IReadOnlyList<string> expectedWarningMessages = null)
        {
            MockEngine buildEngine = new MockEngine();

            TaskLoggingHelper log = new TaskLoggingHelper(buildEngine, TaskName)
            {
                TaskResources = Shared.AssemblyResources.PrimaryResources
            };

            bool success = RoslynCodeTaskFactory.TryLoadTaskBody(log, TaskName, taskBody, parameters ?? new List<TaskPropertyInfo>(), out RoslynCodeTaskFactoryTaskInfo taskInfo);

            buildEngine.Errors.ShouldBe(0, buildEngine.Log);

            if (expectedWarningMessages == null)
            {
                buildEngine.Warnings.ShouldBe(0);
            }
            else
            {
                string output = buildEngine.Log;

                foreach (string expectedWarningMessage in expectedWarningMessages)
                {
                    output.ShouldContain(expectedWarningMessage, customMessage: output);
                }
            }

            success.ShouldBeTrue();

            if (expectedReferences != null)
            {
                taskInfo.References.ShouldBe(expectedReferences);
            }

            if (expectedNamespaces != null)
            {
                taskInfo.Namespaces.ShouldBe(expectedNamespaces);
            }

            if (expectedCodeLanguage != null)
            {
                taskInfo.CodeLanguage.ShouldBe(expectedCodeLanguage);
            }

            if (expectedCodeType != null)
            {
                taskInfo.CodeType.ShouldBe(expectedCodeType.Value);
            }

            if (verifySource)
            {
                Verify(taskInfo.SourceCode, _verifySettings).GetAwaiter().GetResult();
            }
        }
    }
}
