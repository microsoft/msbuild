// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Engine.UnitTests;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Shouldly;
using Xunit;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;

#nullable disable

namespace Microsoft.Build.UnitTests.Evaluation
{
    /// <summary>
    /// Tests mainly for project evaluation
    /// </summary>
    public class Evaluator_Tests : IDisposable
    {
        /// <summary>
        /// Cleanup
        /// </summary>
        public Evaluator_Tests()
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            GC.Collect();
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void Dispose()
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            GC.Collect();
        }

        [Fact]
        public void EnsureProjectEvaluationFinishedIsLogged()
        {
            using TestEnvironment env = TestEnvironment.Create();
            TransientTestFile projectFile = env.CreateFile("project.proj", $@"
<Project Sdk=""Microsoft.NETT.Sdk"">
  <Target Name=""DefaultTarget"">
  </Target>
</Project>
");

            MockLogger logger = new();
            using ProjectCollection collection = new(new Dictionary<string, string>(), [logger], ToolsetDefinitionLocations.Default);
            Assert.Throws<InvalidProjectFileException>(() => collection.LoadProject(projectFile.Path));
            logger.EvaluationFinishedEvents.ShouldNotBeEmpty();
        }

        [Theory]
        [MemberData(nameof(ImportLoadingScenarioTestData))]
        public void VerifyLoadingImportScenarios(string importParameter, bool shouldSucceed)
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                TransientTestFolder existentDirectory = env.CreateFolder(createFolder: true);
                TransientTestFile realFile = env.CreateFile(existentDirectory, "realFile.csproj", @"<Project> </Project>");
                TransientTestFile projectFile = env.CreateFile("project.proj", @$"
<Project>
  <Import {importParameter.Replace("realFolder", existentDirectory.Path)} />

  <Target Name=""MyTarget"">
    <Message Text=""Target working!"" />
  </Target>
</Project>
");
                bool result = false;
                try
                {
                    Project project = new(projectFile.Path);
                    MockLogger logger = new();
                    result = project.Build(logger);
                }
                catch (InvalidProjectFileException) { }
                result.ShouldBe(shouldSucceed);
            }
        }

        // Some of these are also tested elsewhere, but this consolidates related tests in one spot.
        public static IEnumerable<object[]> ImportLoadingScenarioTestData
        {
            get
            {
                // This first section tests our behavior if a folder does not exist. Conditions and whether there are wildcards should affect whether it fails if it fails to find a file.
                yield return new object[] { $@"Project=""{Path.Combine("nonexistentDirectory", "projectThatDoesNotExist.csproj")}"" Condition=""Exists('{Path.Combine("nonexistentDirectory", "projectThatDoesNotExist.csproj")}')""", true };
                yield return new object[] { $@"Project=""{Path.Combine("nonexistentDirectory", "projectThatDoesNotExist.csproj")}"" Condition=""'true'""", false };
                yield return new object[] { $@"Project=""{Path.Combine("nonexistentDirectory", "projectThatDoesNotExist.csproj")}""", false };
                yield return new object[] { $@"Project=""{Path.Combine("nonexistentDirectory", "*.*proj")}""", true };

                // This section tests if the folder does exist, but the project does not.
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "projectThatDoesNotExist.csproj")}"" Condition=""Exists('{Path.Combine("realFolder", "projectThatDoesNotExist.csproj")}')""", true };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "projectThatDoesNotExist.csproj")}"" Condition=""'true'""", false };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "projectThatDoesNotExist.csproj")}""", false };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "*.*proj")}""", true };

                // This tests if the folder and the file exist.
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "realFile.csproj")}"" Condition=""Exists('{Path.Combine("realFolder", "realFile.csproj")}')""", true };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "realFile.csproj")}"" Condition=""'true'""", true };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "realFile.csproj")}""", true };
                yield return new object[] { $@"Project=""{Path.Combine("realFolder", "*.*proj")}""", true };

                // If we fail to find a particular import along one project path, we have a few properties that can be expanded in different ways, including VSToolsPath. In other words,
                // if the property isn't defined to somewhere that exists, we may still find it in a fallback path. Error behavior in this case is more complicated, as the file may
                // exist along one search path but not another, in which case we should not error.
                yield return new object[] { $@"Project=""{Path.Combine("$(VSToolsPath)", "nonexistentDirectory", "projectThatDoesNotExist.csproj")}"" Condition=""Exists('{Path.Combine("$(VSToolsPath)", "nonexistentDirectory", "projectThatDoesNotExist.csproj")}')""", true };
                yield return new object[] { $@"Project=""{Path.Combine("$(VSToolsPath)", "nonexistentDirectory", "projectThatDoesNotExist.csproj")}"" Condition=""'true'""", false };
                yield return new object[] { $@"Project=""{Path.Combine("$(VSToolsPath)", "nonexistentDirectory", "projectThatDoesNotExist.csproj")}""", false };
                yield return new object[] { $@"Project=""{Path.Combine("$(VSToolsPath)", "nonexistentDirectory", "*.*proj")}""", true };
                yield return new object[] { $@"Project=""{Path.Combine("$(VSToolsPath)", "*.*proj")}""", true };
            }
        }

        /// <summary>
        /// Verify Exist condition used in Import or ImportGroup elements will succeed when in-memory project is available inside projectCollection.
        /// </summary>
        [Fact]
        public void VerifyExistsInMemoryProjects()
        {
            string fooPath = NativeMethodsShared.IsWindows ? @"c:\temp\foo.import" : "/temp/foo.import";
            string barPath = NativeMethodsShared.IsWindows ? @"c:\temp\bar.import" : "/temp/bar.import";
            string projXml = ObjectModelHelpers.CleanupFileContents(@"
                                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                                    <Import Project=""" + fooPath + @""" Condition=""Exists('" + fooPath + @"')""/>
                                    <ImportGroup Condition=""Exists('" + barPath + @"')"">
                                          <Import Project=""" + barPath + @""" />
                                    </ImportGroup>
                                </Project>");

            string fooXml = ObjectModelHelpers.CleanupFileContents(@"
                              <Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                     <foo>yes</foo>
                                  </PropertyGroup>
                                </Project>");
            string barXml = ObjectModelHelpers.CleanupFileContents(@"
                                <Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                     <bar>yes</bar>
                                  </PropertyGroup>
                                </Project>");

            // no imports should be loaded
            using ProjectFromString projectFromString = new(projXml);
            Project project = projectFromString.Project;
            project.ReevaluateIfNecessary();

            Assert.Null(project.GetProperty("foo"));
            Assert.Null(project.GetProperty("bar"));

            // add in-memory project c:\temp\foo.import
            using ProjectFromString projectFromStringFoo = new(fooXml);
            Project fooImport = projectFromStringFoo.Project;
            fooImport.FullPath = fooPath;

            // force reevaluation
            project.MarkDirty();
            project.ReevaluateIfNecessary();

            // foo should be available now via fooImport
            Assert.NotNull(project.GetProperty("foo"));
            Assert.Null(project.GetProperty("bar"));

            // add in-memory project c:\temp\bar.import
            using ProjectFromString projectFromStringBar = new(barXml);
            Project barImport = projectFromStringBar.Project;
            barImport.FullPath = barPath;

            // force reevaluation
            project.MarkDirty();
            project.ReevaluateIfNecessary();

            // both foo and bar should be available
            Assert.NotNull(project.GetProperty("foo"));
            Assert.NotNull(project.GetProperty("bar"));

            // remove the imports from PRE
            fooImport.FullPath = @"c:\temp\alteredfoo.import";
            barImport.FullPath = @"c:\temp\alteredbar.import";

            // force reevaluation
            project.MarkDirty();
            project.ReevaluateIfNecessary();

            // both foo and bar should be gone
            Assert.Null(project.GetProperty("foo"));
            Assert.Null(project.GetProperty("bar"));
        }

        /// <summary>
        /// Verify when the conditions are evaluated outside of a target that they are evaluated relative to the file they are physically contained in,
        /// in the case of Imports, and ImportGroups, and PropertyGroups, but that property conditions are evaluated relative to the project file.
        /// When conditions are evaluated inside of a target they are evaluated relative to the project file.
        ///
        /// File Structure
        /// test.targets
        /// test.tx, file to check for existence
        /// subdir\test.proj
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void VerifyConditionsInsideOutsideTargets()
        {
            string testtargets = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                  <PropertyGroup>
                                      <PropertyOutsideTarget Condition=""Exists('test.txt')"">test.txt</PropertyOutsideTarget>
                                      <PropertyOutsideTarget Condition=""Exists('..\test.txt')"">..\test.txt</PropertyOutsideTarget>
                                  </PropertyGroup>
                                  <PropertyGroup Condition=""Exists('test.txt')"">
                                      <PropertyGroupOutsideTarget>test.txt</PropertyGroupOutsideTarget>
                                  </PropertyGroup>
                                  <PropertyGroup Condition=""Exists('..\test.txt')"">
                                      <PropertyGroupOutsideTarget>..\test.txt</PropertyGroupOutsideTarget>
                                  </PropertyGroup>
                                  <Target Name=""Test"">
                                      <PropertyGroup>
                                          <PropertyInsideTarget Condition=""Exists('test.txt')"">test.txt</PropertyInsideTarget>
                                          <PropertyInsideTarget Condition=""Exists('..\test.txt')"">..\test.txt</PropertyInsideTarget>
                                      </PropertyGroup>
                                      <PropertyGroup Condition=""Exists('test.txt')"">
                                          <PropertyGroupInsideTarget>test.txt</PropertyGroupInsideTarget>
                                      </PropertyGroup>
                                      <PropertyGroup Condition=""Exists('..\test.txt')"">
                                          <PropertyGroupInsideTarget>..\test.txt</PropertyGroupInsideTarget>
                                      </PropertyGroup>
                                      <Message Text=""PropertyOutsideTarget: $(PropertyOutsideTarget)"" />
                                      <Message Text=""PropertyGroupOutsideTarget: $(PropertyGroupOutsideTarget)"" />
                                      <Message Text=""PropertyInsideTarget: $(PropertyInsideTarget)"" />
                                      <Message Text=""PropertyGroupInsideTarget: $(PropertyGroupInsideTarget)"" />
                                   </Target>
                                      <Import Project=""projdir.targets"" Condition=""Exists('projdir.targets')"" />
                                      <Import Project=""targetdir.targets"" Condition=""Exists('targetdir.targets')"" />
                                      <ImportGroup Condition=""Exists('projdir2.targets')"">
                                          <Import Project=""projdir2.targets"" />
                                      </ImportGroup>
                                      <ImportGroup Condition=""Exists('targetdir2.targets')"">
                                          <Import Project=""targetdir2.targets"" />
                                      </ImportGroup>
                                   </Project>";

            string projDirTargets = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest"" AfterTargets=""Test"">
                                           <Message Text=""[ProjectDirectoryTargetsImport]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string projDirTargets2 = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest2"" AfterTargets=""Test"">
                                           <Message Text=""[ProjectDirectoryTargetsImportGroup]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string targetDirTargets = @"
                             <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest3"" AfterTargets=""Test"">
                                           <Message Text=""[TargetDirectoryTargetsImport]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string targetDirTargets2 = @"
                             <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest4"" AfterTargets=""Test"">
                                           <Message Text=""[TargetDirectoryTargetsImportGroup]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string subdirTestProj = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Import Project=""..\test.targets"" />
                                </Project>";

            string testTxt = @"Hello";

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "VerifyConditionsInsideOutsideTargets");
            string subDirectory = Path.Combine(targetDirectory, "subdir");

            string testTargetPath = Path.Combine(targetDirectory, "test.targets");
            string targetDirectoryTargetsPath = Path.Combine(targetDirectory, "targetdir.targets");
            string targetDirectoryTargetsPath2 = Path.Combine(targetDirectory, "targetdir2.targets");
            string subdirProjPath = Path.Combine(subDirectory, "test.proj");
            string projectDirectoryTargetsPath = Path.Combine(subDirectory, "projdir.targets");
            string projectDirectoryTargetsPath2 = Path.Combine(subDirectory, "projdir2.targets");
            string textTextPath = Path.Combine(targetDirectory, "test.txt");

            try
            {
                Directory.CreateDirectory(subDirectory);
                File.WriteAllText(testTargetPath, ObjectModelHelpers.CleanupFileContents(testtargets));
                File.WriteAllText(subdirProjPath, ObjectModelHelpers.CleanupFileContents(subdirTestProj));
                File.WriteAllText(textTextPath, testTxt);
                File.WriteAllText(targetDirectoryTargetsPath, ObjectModelHelpers.CleanupFileContents(targetDirTargets));
                File.WriteAllText(targetDirectoryTargetsPath2, ObjectModelHelpers.CleanupFileContents(targetDirTargets2));
                File.WriteAllText(projectDirectoryTargetsPath, ObjectModelHelpers.CleanupFileContents(projDirTargets));
                File.WriteAllText(projectDirectoryTargetsPath2, ObjectModelHelpers.CleanupFileContents(projDirTargets2));

                Project project = new Project(subdirProjPath);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains("PropertyOutsideTarget: ..\\test.txt");
                logger.AssertLogContains("PropertyGroupOutsideTarget: test.txt");
                logger.AssertLogContains("PropertyInsideTarget: ..\\test.txt");
                logger.AssertLogContains("PropertyGroupInsideTarget: ..\\test.txt");
                logger.AssertLogContains("[TargetDirectoryTargetsImport]");
                logger.AssertLogDoesntContain("[ProjectDirectoryTargetsImport]");
                logger.AssertLogContains("[TargetDirectoryTargetsImportGroup]");
                logger.AssertLogDoesntContain("[ProjectDirectoryTargetsImportGroup]");
            }
            finally
            {
                FileUtilities.DeleteDirectoryNoThrow(targetDirectory, true);
            }
        }

        /// <summary>
        /// Verify when the conditions are evaluated outside of a target that they are evaluated relative to the file they are physically contained in,
        /// in the case of Imports, and ImportGroups, and PropertyGroups, but that property conditions are evaluated relative to the project file.
        /// When conditions are evaluated inside of a target they are evaluated relative to the project file.
        ///
        /// File Structure
        /// test.targets
        /// test.tx, file to check for existence
        /// subdir\test.proj
        /// </summary>
        [Fact]
        public void VerifyConditionsInsideOutsideTargets_ProjectInstance()
        {
            string testtargets = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                  <PropertyGroup>
                                      <PropertyOutsideTarget Condition=""Exists('test.txt')"">test.txt</PropertyOutsideTarget>
                                      <PropertyOutsideTarget Condition=""Exists('..\test.txt')"">..\test.txt</PropertyOutsideTarget>
                                  </PropertyGroup>
                                  <PropertyGroup Condition=""Exists('test.txt')"">
                                      <PropertyGroupOutsideTarget>test.txt</PropertyGroupOutsideTarget>
                                  </PropertyGroup>
                                  <PropertyGroup Condition=""Exists('..\test.txt')"">
                                      <PropertyGroupOutsideTarget>..\test.txt</PropertyGroupOutsideTarget>
                                  </PropertyGroup>
                                  <Target Name=""Test"">
                                      <PropertyGroup>
                                          <PropertyInsideTarget Condition=""Exists('test.txt')"">test.txt</PropertyInsideTarget>
                                          <PropertyInsideTarget Condition=""Exists('..\test.txt')"">..\test.txt</PropertyInsideTarget>
                                      </PropertyGroup>
                                      <PropertyGroup Condition=""Exists('test.txt')"">
                                          <PropertyGroupInsideTarget>test.txt</PropertyGroupInsideTarget>
                                      </PropertyGroup>
                                      <PropertyGroup Condition=""Exists('..\test.txt')"">
                                          <PropertyGroupInsideTarget>..\test.txt</PropertyGroupInsideTarget>
                                      </PropertyGroup>
                                      <Message Text=""PropertyOutsideTarget: $(PropertyOutsideTarget)"" />
                                      <Message Text=""PropertyGroupOutsideTarget: $(PropertyGroupOutsideTarget)"" />
                                      <Message Text=""PropertyInsideTarget: $(PropertyInsideTarget)"" />
                                      <Message Text=""PropertyGroupInsideTarget: $(PropertyGroupInsideTarget)"" />
                                   </Target>
                                      <Import Project=""projdir.targets"" Condition=""Exists('projdir.targets')"" />
                                      <Import Project=""targetdir.targets"" Condition=""Exists('targetdir.targets')"" />
                                      <ImportGroup Condition=""Exists('projdir2.targets')"">
                                          <Import Project=""projdir2.targets"" />
                                      </ImportGroup>
                                      <ImportGroup Condition=""Exists('targetdir2.targets')"">
                                          <Import Project=""targetdir2.targets"" />
                                      </ImportGroup>
                                   </Project>";

            string projDirTargets = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest"" AfterTargets=""Test"">
                                           <Message Text=""[ProjectDirectoryTargetsImport]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string projDirTargets2 = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest2"" AfterTargets=""Test"">
                                           <Message Text=""[ProjectDirectoryTargetsImportGroup]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string targetDirTargets = @"
                             <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest3"" AfterTargets=""Test"">
                                           <Message Text=""[TargetDirectoryTargetsImport]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string targetDirTargets2 = @"
                             <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Target Name=""AfterTest4"" AfterTargets=""Test"">
                                           <Message Text=""[TargetDirectoryTargetsImportGroup]"" Importance=""High""/>
                                    </Target>
                                </Project>";

            string subdirTestProj = @"
                                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
                                    <Import Project=""..\test.targets"" />
                                </Project>";

            string testTxt = @"Hello";

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "VerifyConditionsInsideOutsideTargets");
            string subDirectory = Path.Combine(targetDirectory, "subdir");

            string testTargetPath = Path.Combine(targetDirectory, "test.targets");
            string targetDirectoryTargetsPath = Path.Combine(targetDirectory, "targetdir.targets");
            string targetDirectoryTargetsPath2 = Path.Combine(targetDirectory, "targetdir2.targets");
            string subdirProjPath = Path.Combine(subDirectory, "test.proj");
            string projectDirectoryTargetsPath = Path.Combine(subDirectory, "projdir.targets");
            string projectDirectoryTargetsPath2 = Path.Combine(subDirectory, "projdir2.targets");
            string textTextPath = Path.Combine(targetDirectory, "test.txt");

            try
            {
                Directory.CreateDirectory(subDirectory);
                File.WriteAllText(testTargetPath, ObjectModelHelpers.CleanupFileContents(testtargets));
                File.WriteAllText(subdirProjPath, ObjectModelHelpers.CleanupFileContents(subdirTestProj));
                File.WriteAllText(textTextPath, testTxt);
                File.WriteAllText(targetDirectoryTargetsPath, ObjectModelHelpers.CleanupFileContents(targetDirTargets));
                File.WriteAllText(targetDirectoryTargetsPath2, ObjectModelHelpers.CleanupFileContents(targetDirTargets2));
                File.WriteAllText(projectDirectoryTargetsPath, ObjectModelHelpers.CleanupFileContents(projDirTargets));
                File.WriteAllText(projectDirectoryTargetsPath2, ObjectModelHelpers.CleanupFileContents(projDirTargets2));

                ProjectInstance project = new ProjectInstance(subdirProjPath);

                MockLogger logger = new MockLogger();
                bool result = project.Build(new ILogger[] { logger });
                Assert.True(result);
                logger.AssertLogContains("PropertyOutsideTarget: " + Path.Combine("..", "test.txt"));
                logger.AssertLogContains("PropertyGroupOutsideTarget: test.txt");
                logger.AssertLogContains("PropertyInsideTarget: " + Path.Combine("..", "test.txt"));
                logger.AssertLogContains("PropertyGroupInsideTarget: " + Path.Combine("..", "test.txt"));
                logger.AssertLogContains("[TargetDirectoryTargetsImport]");
                logger.AssertLogDoesntContain("[ProjectDirectoryTargetsImport]");
                logger.AssertLogContains("[TargetDirectoryTargetsImportGroup]");
                logger.AssertLogDoesntContain("[ProjectDirectoryTargetsImportGroup]");
            }
            finally
            {
                FileUtilities.DeleteDirectoryNoThrow(targetDirectory, true);
            }
        }

        /// <summary>
        /// When properties are consumed and set in imports make sure that we get the correct warnings.
        /// </summary>
        [Fact]
        public void VerifyUsedUnInitializedPropertyInImports()
        {
            string targetA = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                     <Foo>$(bar)</Foo>
                                  </PropertyGroup>
                                </Project>
                            ");

            string targetB = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                     <bar>Something</bar>
                                  </PropertyGroup>
                                </Project>
                               ");

            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns=""msbuildnamespace"">
                                    <Import Project=""targetA.targets"" />
                                    <Import Project=""targetB.targets"" />
                                    <Target Name=""Build""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "VerifyUsedUnInitializedPropertyInImports");

            string targetAPath = Path.Combine(targetDirectory, "targetA.targets");
            string targetBPath = Path.Combine(targetDirectory, "targetB.targets");
            string projectPath = Path.Combine(targetDirectory, "test.proj");
            bool originalValue = BuildParameters.WarnOnUninitializedProperty;
            try
            {
                BuildParameters.WarnOnUninitializedProperty = true;
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(targetAPath, targetA);
                File.WriteAllText(targetBPath, targetB);
                File.WriteAllText(projectPath, projectContents);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(projectPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogContains("MSB4211");
            }
            finally
            {
                BuildParameters.WarnOnUninitializedProperty = originalValue;
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If a property is set to an empty value and then set to a non empty value we do not expect a warning.
        /// </summary>
        [Fact]
        public void EmptyPropertyIsThenSet()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                      <bar></bar>
                                      <baz>$(bar)</baz>
                                      <bar>Something</bar>
                                  </PropertyGroup>
                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "EmptyPropertyIsThenSet");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            string originalValue = Environment.GetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY");
            try
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", "true");
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogDoesntContain("MSB4211");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", originalValue);
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If a property is set to an empty value and the environment variable is not set then we do not expect an error
        /// </summary>
        [Fact]
        public void EmptyPropertyIsThenSetEnvironmentVariableNotSet()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                      <baz>$(bar)</baz>
                                      <bar>Something</bar>
                                  </PropertyGroup>
                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "EmptyPropertyIsThenSet");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            string originalValue = Environment.GetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY");
            bool originalValue2 = BuildParameters.WarnOnUninitializedProperty;

            try
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", null);
                BuildParameters.WarnOnUninitializedProperty = false;
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogDoesntContain("MSB4211");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", originalValue);
                BuildParameters.WarnOnUninitializedProperty = originalValue2;
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If a property has not been set yet and we consume the property we are setting in order to set it, do not warn
        /// </summary>
        [Fact]
        public void SetPropertyToItself()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                  <PropertyGroup>
                                      <baz>$(baz);I am some text</baz>
                                      <bar>STUFF $(baz) STUFF</bar>
                                  </PropertyGroup>
                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "SetPropertyToItself");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            string originalValue = Environment.GetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY");
            try
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", "true");
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogDoesntContain("MSB4211");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", originalValue);
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If we consume a property which has not been initialized in a property we do not expect a warning because we are explicitly ignoring conditions.
        /// This is done because it is a very common scenario to use uninitialized properties in conditions to set default values.
        /// </summary>
        [Fact]
        public void UsePropertyInCondition()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                    <PropertyGroup Condition=""'$(Maz)' == ''"">
                                       <Maz>Something</Maz>
                                     </PropertyGroup>
                                     <PropertyGroup>
                                         <Maz Condition=""'$(Maz)' == ''"">Something</Maz>
                                     </PropertyGroup>

                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "UsePropertyInCondition");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            string originalValue = Environment.GetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY");
            try
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", "true");
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogDoesntContain("MSB4211");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDWARNONUNINITIALIZEDPROPERTY", originalValue);
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If a property is consumed before it is initialized for the first time log a warning.
        /// </summary>
        [Fact]
        public void UsePropertyBeforeSet()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                     <PropertyGroup>
                                         <Foo>$(baz) $(bar)</Foo>
                                         <bar>Something</bar>
                                         <baz>Something</baz>
                                     </PropertyGroup>

                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "UsePropertyBeforeSet");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            bool originalValue = BuildParameters.WarnOnUninitializedProperty;
            try
            {
                BuildParameters.WarnOnUninitializedProperty = true;
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogContains("MSB4211");
                Assert.Equal(2, logger.WarningCount); // "Expected two warnings"
            }
            finally
            {
                BuildParameters.WarnOnUninitializedProperty = originalValue;
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// If we use a property twice make sure we warn and don't crash due to the dictionary which is holding the used but uninitialized variables..
        /// </summary>
        [Fact]
        public void UsePropertyBeforeSetDuplicates()
        {
            string testtargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project xmlns='msbuildnamespace'>
                                     <PropertyGroup>
                                         <Foo1>$(baz) $(bar)</Foo1>
                                         <Foo>$(baz) $(bar)</Foo>
                                         <bar>Something</bar>
                                         <baz>Something</baz>
                                     </PropertyGroup>

                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "UsePropertyBeforeSetDuplicates");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            bool originalValue = BuildParameters.WarnOnUninitializedProperty;
            try
            {
                BuildParameters.WarnOnUninitializedProperty = true;
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(testTargetPath, testtargets);

                MockLogger logger = new MockLogger();
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                Assert.True(result);
                logger.AssertLogContains("MSB4211");
                Assert.Equal(2, logger.WarningCount); // "Expected two warnings"
            }
            finally
            {
                BuildParameters.WarnOnUninitializedProperty = originalValue;
                FileUtilities.DeleteWithoutTrailingBackslash(targetDirectory, true);
            }
        }

        /// <summary>
        /// Imports should only be included once.
        /// A second import should give a warning, and then be ignored.
        /// If it is imported twice, subtle problems will occur: because typically the 2nd import will
        /// have no effect, but occasionally it won't.
        /// </summary>
        [Fact]
        public void ImportsOnlyIncludedOnce()
        {
            string importPath = null;

            try
            {
                importPath = FileUtilities.GetTemporaryFileName();

                string import = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>$(q)</p>
                        </PropertyGroup>
                    </Project>
                ");

                File.WriteAllText(importPath, import);

                // If the import is pulled in again, the property 'q' will be defined when it is
                // assigned to 'p' in the second inclusion
                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <q>foo</q>
                        </PropertyGroup>

                        <Import Project='" + importPath + @"'/>

                        <PropertyGroup>
                          <q>foo_bar</q>
                        </PropertyGroup>

                        <Import Project='" + importPath + @"'/>

                        <Target Name='t'>
                          <Message Text='$(p)'/>
                        </Target>
                    </Project>
                ");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains("foo");
                logger.AssertLogDoesntContain("foo_bar");
            }
            finally
            {
                File.Delete(importPath);
            }
        }

        /// <summary>
        /// Imports should only be included once. However we should be able to see them in the ImportsIncludingDuplicates property
        /// A second import should give a warning, and still not be added to the Imports property.
        /// </summary>
        [Fact]
        public void MultipleImportsVerifyImportsIncludingDuplicates()
        {
            string importPath = null;
            string importPath2 = null;
            string importPath3 = null;

            try
            {
                importPath = FileUtilities.GetTemporaryFileName();
                importPath2 = FileUtilities.GetTemporaryFileName();
                importPath3 = FileUtilities.GetTemporaryFileName();

                string import = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>Hello</p>
                        </PropertyGroup>
                    </Project>
                ");

                string import2 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>Hello</p>
                        </PropertyGroup>
                      <Import Project='" + importPath3 + @"'/>
                    </Project>
                ");

                File.WriteAllText(importPath, import2);
                File.WriteAllText(importPath2, import2);
                File.WriteAllText(importPath3, import);

                // If the import is pulled in again, the property 'q' will be defined when it is
                // assigned to 'p' in the second inclusion
                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <q>foo</q>
                        </PropertyGroup>

                        <Import Project='" + importPath + @"'/>

                        <PropertyGroup>
                          <q>foo_bar</q>
                        </PropertyGroup>

                        <Import Project='" + importPath + @"'/>
                        <Import Project='" + importPath2 + @"'/>
                        <Target Name='t'>
                          <Message Text='$(p)'/>
                        </Target>
                    </Project>
                ");

                using ProjectCollection pc = new ProjectCollection();
                using ProjectFromString projectFromString = new(content, null, null, pc, ProjectLoadSettings.RecordDuplicateButNotCircularImports);
                Project project = projectFromString.Project;
                IList<ResolvedImport> imports = project.Imports;
                IList<ResolvedImport> importsIncludingDuplicates = project.ImportsIncludingDuplicates;
                Assert.Equal(3, imports.Count);
                Assert.Equal(5, importsIncludingDuplicates.Count);
                Assert.False(imports[0].IsImported);
                Assert.True(imports[1].IsImported);
                Assert.False(imports[2].IsImported);
            }
            finally
            {
                File.Delete(importPath);
                File.Delete(importPath2);
                File.Delete(importPath3);
            }
        }

        [DotNetOnlyFact("Tests .NET SDK-only error")]
        public void ImportWithVSPathThrowsCorrectError()
        {
            string importPath = null;

            using (TestEnvironment env = TestEnvironment.Create())
            {
                // Does not matter that the file or folder does not exist, we are checking for the VS pathing here
                importPath = "path\\that\\does\\not\\exist\\Microsoft\\VisualStudio\\FileName.txt";

                var content = env.CreateTestProjectWithFiles(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <Import Project='" + importPath + @"'/>
                    </Project>
                ");

                InvalidProjectFileException ex = Assert.Throws<InvalidProjectFileException>(() =>
                {
                    Project project = new Project(content.ProjectFile, null, null);
                });

                Assert.Contains("MSB4278", ex.ErrorCode);
            }
        }

        /// <summary>
        /// RecordDuplicateButNotCircularImports should not record circular imports (which do come under the category of "duplicate imports".
        /// </summary>
        [Fact]
        public void RecordDuplicateButNotCircularImportsWithCircularImports()
        {
            string importPath1 = null;
            string importPath2 = null;

            try
            {
                importPath1 = FileUtilities.GetTemporaryFileName();
                importPath2 = FileUtilities.GetTemporaryFileName();

                // "import1" imports "import2" and vice versa.
                string import1 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                      <Import Project='" + importPath2 + @"'/>
                    </Project>
                ");

                // "import1" imports "import2" and vice versa.
                string import2 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>Hello</p>
                        </PropertyGroup>
                      <Import Project='" + importPath1 + @"'/>
                    </Project>
                ");

                File.WriteAllText(importPath1, import1);
                File.WriteAllText(importPath2, import2);

                // The project file contents.
                string manifest = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <Import Project='" + importPath1 + @"'/>
                    </Project>
                ");

                using ProjectCollection pc = new ProjectCollection();
                using ProjectFromString projectFromString = new(manifest, null, null, pc, ProjectLoadSettings.RecordDuplicateButNotCircularImports);
                Project project = projectFromString.Project;


                // In the list returned by ImportsIncludingDuplicates, check if there are any imports that are imported by importPath2.
                bool circularImportsAreRecorded = project.ImportsIncludingDuplicates.Any(resolvedImport => string.Equals(resolvedImport.ImportingElement.ContainingProject.FullPath, importPath2, StringComparison.OrdinalIgnoreCase));

                // Even though, the text in importPath2 contains exactly one import, namely importPath1, it should not be recorded since
                // importPath1 introduces a circular dependency when traversing depth-first from the project.
                Assert.False(circularImportsAreRecorded);
            }
            finally
            {
                File.Delete(importPath1);
                File.Delete(importPath2);
            }
        }

        /// <summary>
        /// RecordDuplicateButNotCircularImports should not record circular imports (which do come under the category of "duplicate imports".
        /// </summary>
        [Fact]
        public void RejectCircularImportsWithCircularImports()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                string importPath1 = null;
                string importPath2 = null;

                try
                {
                    importPath1 = FileUtilities.GetTemporaryFileName();
                    importPath2 = FileUtilities.GetTemporaryFileName();

                    // "import1" imports "import2" and vice versa.
                    string import1 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                      <Import Project='" + importPath2 + @"'/>
                    </Project>
                ");

                    // "import1" imports "import2" and vice versa.
                    string import2 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>Hello</p>
                        </PropertyGroup>
                      <Import Project='" + importPath1 + @"'/>
                    </Project>
                ");

                    File.WriteAllText(importPath1, import1);
                    File.WriteAllText(importPath2, import2);

                    // The project file contents.
                    string manifest = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <Import Project='" + importPath1 + @"'/>
                    </Project>
                ");

                    ProjectCollection pc = new ProjectCollection();

                    // Because import1 and import2 import each other, there is a circular dependency. And since RejectCircularImports flag is set, the below call
                    // should throw.
                    Project project = new Project(XmlReader.Create(new StringReader(manifest)), null, null, pc, ProjectLoadSettings.RejectCircularImports);
                }
                finally
                {
                    File.Delete(importPath1);
                    File.Delete(importPath2);
                }
            });
        }
        /// <summary>
        /// MSBuildDefaultTargets was not getting cleared out between reevaluations.
        /// </summary>
        [Fact]
        public void MSBuildDefaultTargets()
        {
            Project project = new Project();
            project.Xml.DefaultTargets = "dt";
            project.ReevaluateIfNecessary();

            Assert.Equal("dt", project.GetPropertyValue("msbuildprojectdefaulttargets"));

            project.ReevaluateIfNecessary();

            Assert.Equal("dt", project.GetPropertyValue("msbuildprojectdefaulttargets"));

            project.MarkDirty();
            project.ReevaluateIfNecessary();

            Assert.Equal("dt", project.GetPropertyValue("msbuildprojectdefaulttargets"));

            project.Xml.DefaultTargets = "dt2";
            project.ReevaluateIfNecessary();

            Assert.Equal("dt2", project.GetPropertyValue("msbuildprojectdefaulttargets"));
        }

        /// <summary>
        /// Something like a $ in an import's path should work.
        /// </summary>
        [Fact]
        public void EscapableCharactersInImportPath()
        {
            string importPath1 = null;
            string importPath2 = null;
            string projectPath = null;
            string directory = null;
            string directory2 = null;

            try
            {
                directory = Path.Combine(Path.GetTempPath(), "fol$der");
                directory2 = Path.Combine(Path.GetTempPath(), "fol$der" + Path.DirectorySeparatorChar + "fol$der2");
                Directory.CreateDirectory(directory2);

                string importPathRelativeEscaped = Path.Combine("fol$(x)$der2", "Escap%3beab$(x)leChar$ac%3BtersInI*tPa?h");
                string importRelative1 = Path.Combine("fol$der2", "Escap;eableChar$ac;tersInImportPath");
                string importRelative2 = Path.Combine("fol$der2", "Escap;eableChar$ac;tersInI_XXXX_tPath");
                importPath1 = Path.Combine(directory, importRelative1);
                importPath2 = Path.Combine(directory, importRelative2);

                string import1 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <Target Name='t'>
                          <Message Text='[imported1]'/>
                        </Target>
                    </Project>
                ");

                string import2 = ObjectModelHelpers.CleanupFileContents(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <Target Name='t2' DependsOnTargets='t'>
                          <Message Text='[imported2]'/>
                        </Target>
                    </Project>
                ");

                File.WriteAllText(importPath1, import1);
                File.WriteAllText(importPath2, import2);

                projectPath = Path.Combine(directory, "my.proj"); // project path has $ in too
                Project project = new Project();
                project.Save(projectPath);
                project.Xml.AddImport(importPathRelativeEscaped);
                Console.WriteLine(project.Xml.RawXml);
                MockLogger logger = new MockLogger();
                bool result = project.Build("t2", new ILogger[] { logger });

                Assert.True(result);

                logger.AssertLogContains("[imported1]");
                logger.AssertLogContains("[imported2]");
            }
            finally
            {
                File.Delete(importPath1);
                File.Delete(importPath2);
                File.Delete(projectPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory2);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        [Fact]
        public void ImportListOfItems()
        {
            string[] importPaths = {
                ObjectModelHelpers.CreateTempFileOnDisk(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                            <Property1>15F11509E2E047EF9B337807ACEE4448</Property1>
                            <Property2>01CB8D8A6E454918B17496468B3D74AA</Property2>
                        </PropertyGroup>
                    </Project>"),
                ObjectModelHelpers.CreateTempFileOnDisk(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                            <Property1>$(Property1);1C5C388AD1AB46F8A95BDF5894E95B8B</Property1>
                            <Property2>5D64384AAB7A45FEA105EDA9959F5A41</Property2>
                        </PropertyGroup>
                    </Project>")
            };

            string content = ObjectModelHelpers.CleanupFileContents($@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                            <Imports>{String.Join(";", importPaths)}</Imports>
                        </PropertyGroup>

                        <Import Project='$(Imports)' Condition='Exists($(Imports))'/>

                        <Target Name='t'>
                          <Message Text='$(Property1)'/>
                          <Message Text='$(Property2)'/>
                        </Target>
                    </Project>
                ");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);

            logger.AssertLogContains("15F11509E2E047EF9B337807ACEE4448;1C5C388AD1AB46F8A95BDF5894E95B8B");
            logger.AssertLogContains("5D64384AAB7A45FEA105EDA9959F5A41");
            logger.AssertLogDoesntContain("01CB8D8A6E454918B17496468B3D74AA");
        }

        [Fact]
        public void ImportListOfItemsWithWildcards()
        {
            string filename1 = Guid.NewGuid().ToString("N");
            string filename2 = Guid.NewGuid().ToString("N");

            ObjectModelHelpers.CreateFileInTempProjectDirectory($"{filename1}-1.props", @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Property1>43FEAE1F861742549766443A31C581F9</Property1>
                    </PropertyGroup>
                </Project>");
            ObjectModelHelpers.CreateFileInTempProjectDirectory($"{filename1}-2.props", @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Property1>$(Property1);14F2D19468E24EEE86F7DD6D6E81BB20</Property1>
                    </PropertyGroup>
                </Project>");

            ObjectModelHelpers.CreateFileInTempProjectDirectory($"{filename2}-1.props", @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Property1>$(Property1);DCBE5C70A6EC41288AEA2259F0BFEEB4</Property1>
                    </PropertyGroup>
                </Project>");
            ObjectModelHelpers.CreateFileInTempProjectDirectory($"{filename2}-2.props", @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Property1>$(Property1);74960FBBB84C46F5B7CAAF9113F955FC</Property1>
                    </PropertyGroup>
                </Project>");
            ObjectModelHelpers.CreateFileInTempProjectDirectory($"{filename2}-3.props", @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Property1>$(Property1);67EFAD6EF5584EC2BD651119E6489424</Property1>
                    </PropertyGroup>
                </Project>");

            string content = ObjectModelHelpers.CleanupFileContents($@"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                    <PropertyGroup>
                        <Imports>{ObjectModelHelpers.TempProjectDir}\{filename1}-*.props;{ObjectModelHelpers.TempProjectDir}\{filename2}-*.props</Imports>
                    </PropertyGroup>

                    <Import Project='$(Imports)'/>

                    <Target Name='t'>
                        <Message Text='$(Property1)'/>
                    </Target>
                </Project>
            ");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);

            logger.AssertLogContains("43FEAE1F861742549766443A31C581F9;14F2D19468E24EEE86F7DD6D6E81BB20;DCBE5C70A6EC41288AEA2259F0BFEEB4;74960FBBB84C46F5B7CAAF9113F955FC;67EFAD6EF5584EC2BD651119E6489424");
        }

        [Fact]
        public void ImportListOfItemsOneFileDoesNotExist()
        {
            string[] importPaths = {
                ObjectModelHelpers.CreateTempFileOnDisk(@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                            <Property1>15F11509E2E047EF9B337807ACEE4448</Property1>
                        </PropertyGroup>
                    </Project>"),
                "filethatdoesnotexist"
            };

            string content = ObjectModelHelpers.CleanupFileContents($@"
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace' >
                        <PropertyGroup>
                            <Imports>{String.Join(";", importPaths)}</Imports>
                        </PropertyGroup>

                        <Import Project='$(Imports)' Condition='Exists($(Imports))'/>

                        <Target Name='t'>
                          <Message Text='$(Property1)'/>
                        </Target>
                    </Project>
                ");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);

            logger.AssertLogDoesntContain("15F11509E2E047EF9B337807ACEE4448");
        }

        /// <summary>
        /// There are several built-in MSBuildProjectXXXX properties like MSBuildProjectFile.
        /// These always refer to the outer project wherever they are evaluated.
        /// We also want MSBuildFileXXXX properties that are similar but have special behavior:
        /// their values vary according to the file they are evaluated in.
        /// </summary>
        [Fact]
        public void MSBuildThisFileProperties()
        {
            string targets1FileName = NativeMethodsShared.IsWindows ? @"c:\a\t1.targets" : "/a/t1.targets";
            string aDirName = NativeMethodsShared.IsWindows ? @"c:\a\" : "/a/";
            string targets2FileName = NativeMethodsShared.IsWindows ? @"c:\a\b\t2.targets" : "/a/b/t2.targets";
            string bDirName = NativeMethodsShared.IsWindows ? @"c:\a\b\" : "/a/b/";
            string targets3FileName = NativeMethodsShared.IsWindows ? @"c:\t3.targets" : "/t3.targets";
            string rootDirName = NativeMethodsShared.IsWindows ? @"c:\" : "/";
            string aProjName = NativeMethodsShared.IsWindows ? @"c:\a\p.proj" : "/a/p.proj";
            ProjectRootElement main = ProjectRootElement.Create(aProjName);
            main.AddImport(targets1FileName);
            main.AddImport(targets2FileName);
            main.AddImport(targets3FileName);
            ProjectTargetElement target0 = main.AddTarget("t0");
            AddPropertyDumpTasks(aProjName, target0);
            main.InitialTargets = "t0";

            ProjectRootElement import1 = ProjectRootElement.Create(targets1FileName);
            ProjectTargetElement target1 = import1.AddTarget("t1");
            AddPropertyDumpTasks(targets1FileName, target1);
            import1.InitialTargets = "t1";

            ProjectRootElement import2 = ProjectRootElement.Create(targets2FileName);
            ProjectTargetElement target2 = import2.AddTarget("t2");
            AddPropertyDumpTasks(targets2FileName, target2);
            import2.InitialTargets = "t2";

            ProjectRootElement import3 = ProjectRootElement.Create(targets3FileName);
            ProjectTargetElement target3 = import3.AddTarget("t3");
            AddPropertyDumpTasks(targets3FileName, target3);
            import3.InitialTargets = "t3";

            Project project = new Project(main);
            MockLogger logger = new MockLogger();
            project.Build(logger);

            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();

            // For comparison:
            //
            // Project "C:\a\p.proj" on node 1 (default targets).
            //  MSBuildProjectDirectory=           C:\a
            //  MSBuildProjectDirectoryNoRoot=     a
            //  MSBuildProjectFile=                p.proj
            //  MSBuildProjectExtension=           .proj
            //  MSBuildProjectFullPath=            C:\a\p.proj
            //  MSBuildProjectName=                p
            //
            // and at the root, c:\p.proj:
            //
            // MSBuildProjectDirectory=           C:\
            // MSBuildProjectDirectoryNoRoot=
            // MSBuildProjectFile=                a.proj
            // MSBuildProjectExtension=           .proj
            // MSBuildProjectFullPath=            C:\a.proj
            // MSBuildProjectName=                a
            logger.AssertLogContains(aProjName + @": MSBuildThisFileDirectory=" + aDirName);
            logger.AssertLogContains(aProjName + @": MSBuildThisFileDirectoryNoRoot=a" + Path.DirectorySeparatorChar);
            logger.AssertLogContains(aProjName + @": MSBuildThisFile=p.proj");
            logger.AssertLogContains(aProjName + @": MSBuildThisFileExtension=.proj");
            logger.AssertLogContains(aProjName + @": MSBuildThisFileFullPath=" + aProjName);
            logger.AssertLogContains(aProjName + @": MSBuildThisFileName=p");

            logger.AssertLogContains(targets1FileName + @": MSBuildThisFileDirectory=" + aDirName);
            logger.AssertLogContains(targets1FileName + @": MSBuildThisFileDirectoryNoRoot=a" + Path.DirectorySeparatorChar);
            logger.AssertLogContains(targets1FileName + @": MSBuildThisFile=t1.targets");
            logger.AssertLogContains(targets1FileName + @": MSBuildThisFileExtension=.targets");
            logger.AssertLogContains(targets1FileName + @": MSBuildThisFileFullPath=" + targets1FileName);
            logger.AssertLogContains(targets1FileName + @": MSBuildThisFileName=t1");

            logger.AssertLogContains(targets2FileName + @": MSBuildThisFileDirectory=" + bDirName);
            logger.AssertLogContains(targets2FileName + @": MSBuildThisFileDirectoryNoRoot=a" + Path.DirectorySeparatorChar + "b" + Path.DirectorySeparatorChar);
            logger.AssertLogContains(targets2FileName + @": MSBuildThisFile=t2.targets");
            logger.AssertLogContains(targets2FileName + @": MSBuildThisFileExtension=.targets");
            logger.AssertLogContains(targets2FileName + @": MSBuildThisFileFullPath=" + targets2FileName);
            logger.AssertLogContains(targets2FileName + @": MSBuildThisFileName=t2");

            logger.AssertLogContains(targets3FileName + @": MSBuildThisFileDirectory=" + rootDirName);
            logger.AssertLogContains(targets3FileName + @": MSBuildThisFileDirectoryNoRoot=");
            logger.AssertLogContains(targets3FileName + @": MSBuildThisFile=t3.targets");
            logger.AssertLogContains(targets3FileName + @": MSBuildThisFileExtension=.targets");
            logger.AssertLogContains(targets3FileName + @": MSBuildThisFileFullPath=" + targets3FileName);
            logger.AssertLogContains(targets3FileName + @": MSBuildThisFileName=t3");
        }

        /// <summary>
        /// Per Orcas/Whidbey, if there are several task parameters that only differ
        /// by case, we just silently take the last one.
        /// </summary>
        [Fact]
        public void RepeatedTaskParameters()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Target Name='t'>
                            <Message Text='1' text='2' TEXT='3'/>
                        </Target>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;
            ProjectInstance instance = project.CreateProjectInstance();

            Assert.Equal("3", (Helpers.GetFirst(instance.Targets["t"].Tasks)).GetParameter("Text"));
        }

        /// <summary>
        /// Simple override
        /// </summary>
        [Fact]
        public void PropertyPredecessors()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>1</p>
                          <p>2</p>
                          <p Condition='false'>3</p>
                          <p>$(p);2</p>
                        </PropertyGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectProperty property = project.GetProperty("p");

            ProjectPropertyElement xml1 = project.Xml.Properties.First();
            Assert.Equal("2;2", property.EvaluatedValue);
            Assert.Equal("1", property.Predecessor.Predecessor.EvaluatedValue);
            Assert.True(Object.ReferenceEquals(xml1, property.Predecessor.Predecessor.Xml));
            Assert.Null(property.Predecessor.Predecessor.Predecessor);
        }

        /// <summary>
        /// Predecessors and imports
        /// </summary>
        [Fact]
        public void PropertyPredecessorsAndImports()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion' >
                        <Import Project='$(MSBuildToolsPath)\Microsoft.Common.targets'/>
                        <PropertyGroup>
                          <!-- Case insensitive -->
                          <OUTdir>1</OUTdir>
                        </PropertyGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            // Verify the predecessor is the one in the import document
            ProjectRootElement importXml = ProjectRootElement.Open(project.Items.ElementAt(0).Xml.ContainingProject.FullPath);
            ProjectRootElement predecessorXmlRoot = project.GetProperty("outdir").Predecessor.Xml.ContainingProject;

            Assert.True(Object.ReferenceEquals(importXml, predecessorXmlRoot));
        }

        /// <summary>
        /// New properties get a null predecessor until reevaluation
        /// </summary>
        [Fact]
        public void PropertyPredecessorsSetProperty()
        {
            // Need an existing property with the same name in an import
            // so there's a potential predecessor but it's not just overwritten
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion' >
                        <Import Project='$(MSBuildToolsPath)\Microsoft.Common.targets'/>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectProperty property = project.SetProperty("outdir", "x"); // Outdir is set in microsoft.common.targets

            Assert.Null(property.Predecessor);
        }

        /// <summary>
        /// Predecessor of item definition is item definition
        /// </summary>
        [Fact]
        public void ItemDefinitionPredecessorToItemDefinition()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemDefinitionGroup>
                          <i>
                            <m>1</m>
                            <m>2</m>
                            <m Condition='false'>3</m>
                            <m>%(m);2</m>
                          </i>
                        </ItemDefinitionGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadata metadatum = project.ItemDefinitions["i"].GetMetadata("m");

            Assert.Equal("2;2", metadatum.EvaluatedValue);
            Assert.Equal("1", metadatum.Predecessor.Predecessor.EvaluatedValue);

            ProjectMetadataElement xml1 = project.Xml.ItemDefinitions.ElementAt(0).Metadata.ElementAt(0);
            Assert.True(Object.ReferenceEquals(xml1, metadatum.Predecessor.Predecessor.Xml));
            Assert.Null(metadatum.Predecessor.Predecessor.Predecessor);
        }

        /// <summary>
        /// Newly added item's metadata always has null predecessor until reevaluation
        /// </summary>
        [Fact]
        public void NewItemPredecessor()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemDefinitionGroup>
                          <i>
                            <m>m1</m>
                          </i>
                        </ItemDefinitionGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectItem item = project.AddItem("i", "i1")[0];
            ProjectMetadata metadatum = item.SetMetadataValue("m", "m2");

            Assert.Null(metadatum.Predecessor);
        }

        /// <summary>
        /// Predecessor of item is item definition
        /// </summary>
        [Fact]
        public void ItemDefinitionPredecessorToItem()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemDefinitionGroup>
                          <i>
                            <m>1</m>
                          </i>
                        </ItemDefinitionGroup>
                        <ItemGroup>
                          <i Include='i1'>
                            <m>2;%(m)</m>
                            <x>x</x>
                            <m>3;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadata metadatum = project.GetItems("i").ElementAt(0).GetMetadata("m");

            Assert.Equal("3;2;1", metadatum.EvaluatedValue);
            Assert.Equal("2;1", metadatum.Predecessor.EvaluatedValue);
            Assert.Equal("1", metadatum.Predecessor.Predecessor.EvaluatedValue);

            ProjectMetadataElement xml1 = project.Xml.ItemDefinitions.ElementAt(0).Metadata.ElementAt(0);
            Assert.True(Object.ReferenceEquals(xml1, metadatum.Predecessor.Predecessor.Xml));

            ProjectMetadataElement xml2 = project.Xml.Items.ElementAt(0).Metadata.ElementAt(0);
            Assert.True(Object.ReferenceEquals(xml2, metadatum.Predecessor.Xml));

            Assert.Null(metadatum.Predecessor.Predecessor.Predecessor);
        }

        /// <summary>
        /// Predecessor of item is on the same item.
        /// </summary>
        [Fact]
        public void PredecessorOnSameItem()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <i Include='i0'>
                            <!-- should not be relevant to i1-->
                            <m>0</m>
                          </i>
                          <i Include='i1'>
                            <m>1</m>
                            <m Condition='false'>2</m>
                            <m>3</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadata metadatum = project.GetItems("i").ElementAt(1).GetMetadata("m");

            Assert.Equal("3", metadatum.EvaluatedValue);
            Assert.Equal("1", metadatum.Predecessor.EvaluatedValue);

            ProjectMetadataElement xml1 = project.Xml.Items.ElementAt(1).Metadata.ElementAt(0);
            Assert.True(Object.ReferenceEquals(xml1, metadatum.Predecessor.Xml));

            Assert.Null(metadatum.Predecessor.Predecessor);
        }

        /// <summary>
        /// Predecessor of item is item
        /// </summary>
        [Fact]
        public void ItemPredecessorToItem()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <h Include='h1'>
                            <m>1</m>
                          </h>
                          <i Include='@(h)'>
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadata metadatum = project.GetItems("i").ElementAt(0).GetMetadata("m");

            Assert.Equal("2;1", metadatum.EvaluatedValue);
            Assert.Equal("1", metadatum.Predecessor.EvaluatedValue);

            ProjectMetadataElement xml1 = project.Xml.Items.ElementAt(0).Metadata.ElementAt(0);
            Assert.True(Object.ReferenceEquals(xml1, metadatum.Predecessor.Xml));

            Assert.Null(metadatum.Predecessor.Predecessor);
        }

        [Fact]
        public void ItemPredecessorToItemWithCaseChange()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <item_with_lowercase_name Include='h1'>
                            <m>1</m>
                          </item_with_lowercase_name>
                          <i Include='@(ITEM_WITH_LOWERCASE_NAME)'>
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadataElement metadataElementFromProjectRootElement =
                project.Xml.Items.First().Metadata.First();

            Assert.Collection(project.GetItems("i"), item =>
            {
                ProjectMetadata metadatum = item.GetMetadata("m");

                Assert.Equal("2;1", metadatum.EvaluatedValue);
                Assert.Equal("1", metadatum.Predecessor.EvaluatedValue);
                Assert.Same(metadataElementFromProjectRootElement, metadatum.Predecessor.Xml);

                Assert.Null(metadatum.Predecessor.Predecessor);
            });
        }

        /// <summary>
        /// Should be removed when escape hatch for #1751 is removed
        /// </summary>
        [Fact]
        public void ItemPredecessorToItemWithCaseChangeAndEscapeHatch()
        {
            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDUSECASESENSITIVEITEMNAMES", "1");
                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <item_with_lowercase_name Include='h1'>
                            <m>1</m>
                          </item_with_lowercase_name>
                          <i Include='@(ITEM_WITH_LOWERCASE_NAME)'>
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                ProjectMetadataElement metadataElementFromProjectRootElement =
                    project.Xml.Items.First().Metadata.First();

                // empty because of the case mismatch
                Assert.Empty(project.GetItems("i"));
            }
        }

        /// <summary>
        /// Predecessor of item is item via transform
        /// </summary>
        [Fact]
        public void ItemPredecessorToItemViaTransform()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <h Include='h1'>
                            <m>1</m>
                          </h>
                          <i Include=""@(h->'%(identity)')"">
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            ProjectMetadata metadatum = project.GetItems("i").ElementAt(0).GetMetadata("m");

            Assert.Equal("2;1", metadatum.EvaluatedValue);
            Assert.Equal("1", metadatum.Predecessor.EvaluatedValue);
        }

        [Fact]
        public void ItemPredecessorToItemViaTransformWithCaseChange()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <ITEM_WITH_UPPERCASE_NAME Include='h1'>
                            <m>1</m>
                          </ITEM_WITH_UPPERCASE_NAME>
                          <i Include=""@(item_with_uppercase_name->'%(identity)')"">
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");


            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            Assert.Collection(project.GetItems("i"), item =>
            {
                Assert.Equal("h1", item.EvaluatedInclude);
            });
        }

        /// <summary>
        /// Should be removed when escape hatch for #1751 is removed
        /// </summary>
        [Fact]
        public void ItemPredecessorToItemViaTransformWithCaseChangeWithEscapeHatch()
        {
            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDUSECASESENSITIVEITEMNAMES", "1");
                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <ITEM_WITH_UPPERCASE_NAME Include='h1'>
                            <m>1</m>
                          </ITEM_WITH_UPPERCASE_NAME>
                          <i Include=""@(item_with_uppercase_name->'%(identity)')"">
                            <m>2;%(m)</m>
                          </i>
                        </ItemGroup>
                    </Project>");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                // Should be empty because of the case mismatch
                Assert.Empty(project.GetItems("i"));
            }
        }

        /// <summary>
        /// Item predecessors and imports
        /// </summary>
        [Fact]
        public void ItemPredecessorsAndImports()
        {
            string file = null;

            try
            {
                ProjectRootElement import = ProjectRootElement.Create();
                import.AddItemDefinition("i").AddMetadata("m", "%(m);m1");

                file = FileUtilities.GetTemporaryFileName();
                import.Save(file);

                string content = ObjectModelHelpers.CleanupFileContents(@"
                        <Project xmlns='msbuildnamespace' >
                            <ItemDefinitionGroup>
                              <i>
                                <m>m0</m>
                              </i>
                            </ItemDefinitionGroup>
                            <Import Project='" + file + @"'/>
                            <ItemGroup>
                              <i Include='i1'>
                                <m>%(m);m2</m>
                              </i>
                            </ItemGroup>
                        </Project>");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                ProjectMetadata predecessor = project.GetItems("i").ElementAt(0).GetMetadata("m").Predecessor;

                Assert.True(Object.ReferenceEquals(import, predecessor.Xml.ContainingProject));
                Assert.True(Object.ReferenceEquals(project.Xml, predecessor.Predecessor.Xml.ContainingProject));
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Cases where there are no predecessors at all
        /// </summary>
        [Fact]
        public void NoPredecessors()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>1</p>
                        </PropertyGroup>
                        <ItemDefinitionGroup>
                          <i>
                            <m>m1</m>
                          </i>
                        </ItemDefinitionGroup>
                        <ItemGroup>
                          <j Include='j1'>
                            <m>$(p)</m>
                          </j>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            Assert.Null(project.GetProperty("p").Predecessor);
            Assert.Null(project.ItemDefinitions["i"].GetMetadata("m").Predecessor);
            Assert.Null(project.GetItems("j").ElementAt(0).GetMetadata("m").Predecessor);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Simple override
        /// </summary>
        [Fact]
        public void AllEvaluatedProperties()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <PropertyGroup>
                          <p>1</p>
                          <p>2</p>
                          <p Condition='false'>3</p>
                          <p>$(p);2</p>
                        </PropertyGroup>
                        <PropertyGroup Condition='false'>
                          <p>3</p>
                        </PropertyGroup>
                        <PropertyGroup>
                          <r>4</r>
                        </PropertyGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            IDictionary<string, ProjectProperty> allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates = new Dictionary<string, ProjectProperty>(StringComparer.OrdinalIgnoreCase);

            // Get all those properties from project.AllEvaluatedProperties which don't have a backing xml. We have to dump it into a dictionary
            // because AllEvaluatedProperties contains duplicates, but we're preparing to Properties, which doesn't, so we need to make sure
            // that the final value in AllEvaluatedProperties is the one that matches.
            foreach (ProjectProperty property in project.AllEvaluatedProperties.TakeWhile(property => property.Xml == null))
            {
                allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates[property.Name] = property;
            }

            // All those properties which aren't defined in any file. Examples are global properties, environment properties, etc.
            IEnumerable<ProjectProperty> nonImportedProperties = project.Properties.Where(property => property.Xml == null);

            Assert.Equal(allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates.Count, nonImportedProperties.Count());

            // Now check and make sure they all match.  If we get through the entire foreach without triggering an Assert.Fail(), then
            // they do.
            foreach (ProjectProperty property in nonImportedProperties)
            {
                ProjectProperty propertyFromAllEvaluated = null;

                if (!allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates.TryGetValue(property.Name, out propertyFromAllEvaluated))
                {
                    Assert.Fail(String.Format("project.Properties contained property {0}, but AllEvaluatedProperties did not.", property.Name));
                }
                else if (!property.Equals(propertyFromAllEvaluated))
                {
                    Assert.Fail(String.Format("The properties in project.Properties and AllEvaluatedProperties for property {0} were different.", property.Name));
                }
            }

            // These are the properties which are defined in some file.
            IEnumerable<ProjectProperty> restOfAllEvaluatedProperties = project.AllEvaluatedProperties.SkipWhile(property => property.Xml == null);

            Assert.Equal(4, restOfAllEvaluatedProperties.Count());
            Assert.Equal("1", restOfAllEvaluatedProperties.ElementAt(0).EvaluatedValue);
            Assert.Equal("2", restOfAllEvaluatedProperties.ElementAt(1).EvaluatedValue);
            Assert.Equal("2;2", restOfAllEvaluatedProperties.ElementAt(2).EvaluatedValue);
            Assert.Equal("4", restOfAllEvaluatedProperties.ElementAt(3).EvaluatedValue);

            // Verify lists reset on reevaluation
            project.MarkDirty();
            project.ReevaluateIfNecessary();

            restOfAllEvaluatedProperties = project.AllEvaluatedProperties.SkipWhile(property => property.Xml == null);
            Assert.Equal(4, restOfAllEvaluatedProperties.Count());
        }

        /// <summary>
        /// All evaluated items
        /// </summary>
        [Fact]
        public void AllEvaluatedItems()
        {
            string file = null;

            try
            {
                // Should include imported items
                file = FileUtilities.GetTemporaryFileName();
                ProjectRootElement import = ProjectRootElement.Create(file);
                import.AddItem("i", "i10");
                import.Save();

                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <i Include='i1'/>
                          <j Include='j1'>
                            <m>m1</m>
                          </j>
                          <i Include='i2' Condition='false'/>
                          <i Include='i3'/>
                        </ItemGroup>

                        <Choose>
                          <When Condition='false'>
                            <ItemGroup>
                              <i Include='i4'/>
                            </ItemGroup>
                          </When>
                          <When Condition='true'>
                            <ItemGroup>
                              <i Include='i1'>
                                <m>m2</m>
                              </i>
                            </ItemGroup>
                          </When>
                        </Choose>

                        <Choose>
                          <When Condition='false'/>
                          <Otherwise>
                            <ItemGroup>
                              <i Include='i5'/>
                            </ItemGroup>
                          </Otherwise>
                        </Choose>

                        <Import Project='" + file + @"'/>
                    </Project>");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                Assert.Equal(6, project.AllEvaluatedItems.Count);
                Assert.Equal("i1", project.AllEvaluatedItems.ElementAt(0).EvaluatedInclude);
                Assert.Equal(String.Empty, project.AllEvaluatedItems.ElementAt(0).GetMetadataValue("m"));
                Assert.Equal("j1", project.AllEvaluatedItems.ElementAt(1).EvaluatedInclude);
                Assert.Equal("m1", project.AllEvaluatedItems.ElementAt(1).GetMetadataValue("m"));
                Assert.Equal("i3", project.AllEvaluatedItems.ElementAt(2).EvaluatedInclude);
                Assert.Equal("i1", project.AllEvaluatedItems.ElementAt(3).EvaluatedInclude);
                Assert.Equal("m2", project.AllEvaluatedItems.ElementAt(3).GetMetadataValue("m"));
                Assert.Equal("i5", project.AllEvaluatedItems.ElementAt(4).EvaluatedInclude);
                Assert.Equal("i10", project.AllEvaluatedItems.ElementAt(5).EvaluatedInclude);

                // Adds aren't applied until reevaluation
                project.AddItem("i", "i6");
                project.AddItem("i", "i7");
                project.RemoveItem(project.AllEvaluatedItems.ElementAt(1));

                Assert.Equal(6, project.AllEvaluatedItems.Count);

                project.MarkDirty();
                project.ReevaluateIfNecessary();

                Assert.Equal(7, project.AllEvaluatedItems.Count);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Evaluated properties list and imports
        /// </summary>
        [Fact]
        public void AllEvaluatedPropertiesAndImports()
        {
            string file = null;

            try
            {
                file = FileUtilities.GetTemporaryFileName();
                ProjectRootElement import = ProjectRootElement.Create(file);
                import.AddProperty("p", "0").Condition = "false";
                import.AddProperty("p", "1");
                import.AddProperty("q", "2");
                import.Save();

                string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Import Project='" + file + @"'/>
                        <PropertyGroup>
                          <p>3</p>
                        </PropertyGroup>
                    </Project>");

                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                IDictionary<string, ProjectProperty> allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates = new Dictionary<string, ProjectProperty>(StringComparer.OrdinalIgnoreCase);

                // Get all those properties from project.AllEvaluatedProperties which don't have a backing xml. As project.AllEvaluatedProperties
                // is an ordered collection and since such properties necessarily should occur before other properties, we don't need to scan
                // the whole list.
                // We have to dump it into a dictionary because AllEvaluatedProperties contains duplicates, but we're preparing to Properties,
                // which doesn't, so we need to make sure that the final value in AllEvaluatedProperties is the one that matches.
                foreach (ProjectProperty property in project.AllEvaluatedProperties.Where(property => property.Xml == null))
                {
                    allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates[property.Name] = property;
                }

                // All those properties which aren't defined in any file. Examples are global properties, environment properties, etc.
                IEnumerable<ProjectProperty> nonImportedProperties = project.Properties.Where(property => property.Xml == null);

                Assert.Equal(allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates.Count, nonImportedProperties.Count());

                // Now check and make sure they all match.  If we get through the entire foreach without triggering an Assert.Fail(), then
                // they do.
                foreach (ProjectProperty property in nonImportedProperties)
                {
                    ProjectProperty propertyFromAllEvaluated = null;

                    if (!allEvaluatedPropertiesWithNoBackingXmlAndNoDuplicates.TryGetValue(property.Name, out propertyFromAllEvaluated))
                    {
                        Assert.Fail(String.Format("project.Properties contained property {0}, but AllEvaluatedProperties did not.", property.Name));
                    }
                    else if (!property.Equals(propertyFromAllEvaluated))
                    {
                        Assert.Fail(String.Format("The properties in project.Properties and AllEvaluatedProperties for property {0} were different.", property.Name));
                    }
                }

                // These are the properties which are defined in some file.
                IEnumerable<ProjectProperty> restOfAllEvaluatedProperties = project.AllEvaluatedProperties.Where(property => property.Xml != null);

                Assert.Equal(3, restOfAllEvaluatedProperties.Count());
                Assert.Equal("1", restOfAllEvaluatedProperties.ElementAt(0).EvaluatedValue);
                Assert.Equal("2", restOfAllEvaluatedProperties.ElementAt(1).EvaluatedValue);
                Assert.Equal("3", restOfAllEvaluatedProperties.ElementAt(2).EvaluatedValue);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// New properties do not appear in the evaluated properties list until reevaluation
        /// </summary>
        [Fact]
        public void AllEvaluatedPropertiesSetProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion'>
                        <Import Project='$(MSBuildToolsPath)\Microsoft.Common.targets'/>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            int initial = project.AllEvaluatedProperties.Count;

            project.SetProperty("p", "1");

            Assert.Equal(initial, project.AllEvaluatedProperties.Count);

            project.ReevaluateIfNecessary();

            Assert.Equal(initial + 1, project.AllEvaluatedProperties.Count);
        }

        /// <summary>
        /// Two item definitions
        /// </summary>
        [Fact]
        public void AllEvaluatedItemDefinitionMetadata()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' ToolsVersion='msbuilddefaulttoolsversion'>
                        <ItemDefinitionGroup>
                          <i>
                            <m>1</m>
                            <n>2</n>
                          </i>
                        </ItemDefinitionGroup>
                        <ItemDefinitionGroup>
                          <i>
                            <m>1</m>
                            <m Condition='false'>3</m>
                            <m>%(m);2</m>
                          </i>
                        </ItemDefinitionGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            Assert.Equal(4, project.AllEvaluatedItemDefinitionMetadata.Count);

            Assert.Equal("2", project.AllEvaluatedItemDefinitionMetadata.ElementAt(1).EvaluatedValue);
            Assert.Equal("1;2", project.AllEvaluatedItemDefinitionMetadata.ElementAt(3).EvaluatedValue);

            // Verify lists are cleared on reevaluation
            Assert.Equal(4, project.AllEvaluatedItemDefinitionMetadata.Count);
        }

        /// <summary>
        /// Item's metadata does not appear in AllEvaluatedItemDefinitionMetadata
        /// </summary>
        [Fact]
        public void AllEvaluatedItemDefinitionItem()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <ItemGroup>
                          <i Include='i1'>
                            <m>m1</m>
                          </i>
                        </ItemGroup>
                    </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            Assert.Empty(project.AllEvaluatedItemDefinitionMetadata);
        }

        /// <summary>
        /// Cases where there are no AllEvaluated* at all
        /// </summary>
        [Fact]
        public void AllEvaluatedListsExceptPropertiesAreEmpty()
        {
            Project project = new Project();

            // All those properties which aren't defined in any file. Examples are global properties, environment properties, etc.
            IEnumerable<ProjectProperty> nonImportedProperties = project.Properties.Where(property => property.Xml == null);

            // AllEvaluatedProperties intentionally includes duplicates; but if there are any among the non-imported properties, then
            // our count won't match the above.
            HashSet<string> allProjectPropertiesNoDuplicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectProperty property in project.AllEvaluatedProperties)
            {
                allProjectPropertiesNoDuplicateNames.Add(property.Name);
            }

            Assert.Equal(nonImportedProperties.Count(), allProjectPropertiesNoDuplicateNames.Count);
            Assert.Empty(project.AllEvaluatedItemDefinitionMetadata);
            Assert.Empty(project.AllEvaluatedItems);
        }

        /// <summary>
        /// Test imports with wildcards and a relative path.
        /// </summary>
        [Fact]
        public void ImportWildcardsRelative()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ImportWildcardsRelative");
            string directory2 = Path.Combine(directory, "sub");
            Directory.CreateDirectory(directory2);
            VerifyImportTargetRelativePath(directory, directory2, new string[] { Path.Combine("**", "*.targets") });
        }

        /// <summary>
        /// Test imports with wildcards and a relative path.
        /// </summary>
        [Fact]
        public void ImportWildcardsRelative2()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ImportWildcardsRelative2");
            string directory2 = Path.Combine(directory, "sub");
            Directory.CreateDirectory(directory2);
            VerifyImportTargetRelativePath(
                directory,
                directory2,
                new string[] { Path.Combine(directory2, "*.targets"), Path.Combine(directory, "*.targets") });
        }

        /// <summary>
        /// Test imports with wildcards and a relative path.
        /// </summary>
        [Fact]
        public void ImportWildcardsRelative3()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ImportWildcardsRelative3");
            string directory2 = Path.Combine(directory, "sub");
            Directory.CreateDirectory(directory2);
            VerifyImportTargetRelativePath(
                directory,
                directory2,
                new string[]
                    {
                        Path.Combine(directory2, "..", "*.targets"), Path.Combine(
                            directory,
                            ".",
                            "sub",
                            "*.targets")
                    });
        }

        /// <summary>
        /// Test imports with wildcards and a full path
        /// </summary>
        [Fact]
        public void ImportWildcardsFullPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ImportWildcardsFullPath");
            string directory2 = Path.Combine(directory, "sub");
            Directory.CreateDirectory(directory2);

            string file1 = Path.Combine(directory, "1.targets");
            string file2 = Path.Combine(directory2, "2.targets");
            string file3 = Path.Combine(directory2, "3.cpp.targets");

            VerifyImportTargetRelativePath(directory, directory2, new string[] { file1, file2, file3 });
        }

        /// <summary>
        /// Don't crash on a particular bad conditional.
        /// </summary>
        [Fact]
        public void BadConditional()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Target Name='t' Condition='()'/>
                    </Project>");

            MockLogger mockLogger = new MockLogger();
            List<ILogger> loggerList = new List<ILogger>();
            loggerList.Add(mockLogger);

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;
            ProjectInstance instance = project.CreateProjectInstance();
            instance.Build(loggerList);

            // Expect an error from the bad condition
            Assert.Equal(1, mockLogger.ErrorCount);
            mockLogger.AssertLogContains("MSB4092");
        }

        /// <summary>
        /// Default targets with empty entries doesn't break
        /// </summary>
        [Fact]
        public void DefaultTargetsWithBlanks()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' DefaultTargets='t; ;q; ' >
                        <Target Name='t' Condition='()'/>
                    </Project>");

            MockLogger mockLogger = new MockLogger();
            List<ILogger> loggerList = new List<ILogger>();
            loggerList.Add(mockLogger);

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;
            ProjectInstance instance = project.CreateProjectInstance();
            Assert.Equal(2, instance.DefaultTargets.Count);
            Assert.Equal("t", instance.DefaultTargets[0]);
            Assert.Equal("q", instance.DefaultTargets[1]);
        }

        /// <summary>
        /// Initial targets with empty entries doesn't break
        /// </summary>
        [Fact]
        public void InitialTargetsWithBlanks()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' InitialTargets='t; ;q; ' >
                        <Target Name='t' />
                        <Target Name='q' />
                    </Project>");

            MockLogger mockLogger = new MockLogger();
            List<ILogger> loggerList = new List<ILogger>();
            loggerList.Add(mockLogger);

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;
            ProjectInstance instance = project.CreateProjectInstance();
            Assert.Equal(2, instance.InitialTargets.Count);
            Assert.Equal("t", instance.InitialTargets[0]);
            Assert.Equal("q", instance.InitialTargets[1]);
        }

#if FEATURE_INSTALLED_MSBUILD
        /// <summary>
        /// Test that the default value for $(MSBuildExtensionsPath) points to "c:\program files\msbuild" in a 64-bit process
        /// or on a 32-bit machine and "c:\program files (x86)\msbuild" in a 32-bit process on a 64-bit machine.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPathDefault_Legacy()
        {
            string specialPropertyName = "MSBuildExtensionsPath";

            // Save the old copy of the MSBuildExtensionsPath, so we can restore it when the unit test is done.
            string backupMSBuildExtensionsPath = Environment.GetEnvironmentVariable(specialPropertyName);
            string backupMagicSwitch = Environment.GetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH");
            string targetVar = Environment.GetEnvironmentVariable("Target");
            string numberVar = Environment.GetEnvironmentVariable("0env");
            string msbuildVar = Environment.GetEnvironmentVariable("msbuildtoolsversion");

            try
            {
                // Set an environment variable called MSBuildExtensionsPath to some value, for the purpose
                // of seeing whether our value wins.
                Environment.SetEnvironmentVariable(specialPropertyName, null);
                Environment.SetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH", "1");

                // Need to create a new project collection object in order to pick up the new environment variables.
                using var collection = new ProjectCollection();
                Project project = new Project(collection);

                string msbuildPath = NativeMethodsShared.IsWindows ?
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar + "MSBuild" :
                    "MSBuild";
                Assert.Equal(msbuildPath, project.GetPropertyValue(specialPropertyName));
            }
            finally
            {
                // Restore the original value of the MSBuildExtensionsPath environment variable.
                Environment.SetEnvironmentVariable(specialPropertyName, backupMSBuildExtensionsPath);
                Environment.SetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH", backupMagicSwitch);
                Environment.SetEnvironmentVariable("Target", targetVar);
                Environment.SetEnvironmentVariable("0env", numberVar);
                Environment.SetEnvironmentVariable("msbuildtoolsversion", msbuildVar);
            }
        }
#endif

        /// <summary>
        /// Test that the default value for $(MSBuildExtensionsPath) points to the 32-bit Program Files always
        /// (ie. it should have the same value as MSBuildExtensionsPath32).
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPathDefault()
        {
            string specialPropertyName = "MSBuildExtensionsPath";
            string specialPropertyName32 = "MSBuildExtensionsPath32";

            // Save the old copy of the MSBuildExtensionsPath, so we can restore it when the unit test is done.
            string backupMSBuildExtensionsPath = Environment.GetEnvironmentVariable(specialPropertyName);
            string backupMSBuildExtensionsPath32 = Environment.GetEnvironmentVariable(specialPropertyName32);
            string backupMagicSwitch = Environment.GetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH");
            string targetVar = Environment.GetEnvironmentVariable("Target");
            string numberVar = Environment.GetEnvironmentVariable("0env");
            string msbuildVar = Environment.GetEnvironmentVariable("msbuildtoolsversion");

            try
            {
                // Set any pre-existing environment variables to null, just in case someone had set
                // MSBuildExtensionsPath or MSBuildExtensionsPath32 explicitly in the environment.
                Environment.SetEnvironmentVariable(specialPropertyName, null);
                Environment.SetEnvironmentVariable(specialPropertyName32, null);
                Environment.SetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH", null);

                // Need to create a new project collection object in order to pick up the new environment variables.
                using var collection = new ProjectCollection();
                Project project = new Project(collection);

                Assert.Equal(project.GetPropertyValue(specialPropertyName32), project.GetPropertyValue(specialPropertyName));
            }
            finally
            {
                // Restore the original value of the MSBuildExtensionsPath environment variable.
                Environment.SetEnvironmentVariable(specialPropertyName, backupMSBuildExtensionsPath);
                Environment.SetEnvironmentVariable(specialPropertyName32, backupMSBuildExtensionsPath32);
                Environment.SetEnvironmentVariable("MSBUILDLEGACYEXTENSIONSPATH", backupMagicSwitch);
                Environment.SetEnvironmentVariable("Target", targetVar);
                Environment.SetEnvironmentVariable("0env", numberVar);
                Environment.SetEnvironmentVariable("msbuildtoolsversion", msbuildVar);
            }
        }

        /// <summary>
        /// Test that if I set an environment variable called "MSBuildExtensionPath", that my env var
        /// should win over whatever MSBuild thinks the default is.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPathWithEnvironmentOverride()
        {
            // Save the old copy of the MSBuildExtensionsPath, so we can restore it when the unit test is done.
            string backupMSBuildExtensionsPath = Environment.GetEnvironmentVariable("MSBuildExtensionsPath");

            try
            {
                // Set an environment variable called MSBuildExtensionsPath to some value, for the purpose
                // of seeing whether our value wins.
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath", @"c:\foo\bar");

                // Need to create a new project collection object in order to pick up the new environment variables.
                using var collection = new ProjectCollection();
                Project project = new Project(collection);

                Assert.Equal(@"c:\foo\bar", project.GetPropertyValue("MSBuildExtensionsPath"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath", backupMSBuildExtensionsPath);
            }
        }

        /// <summary>
        /// Test that if I set a global property called "MSBuildExtensionPath", that my global property
        /// should win over whatever MSBuild thinks the default is.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPathWithGlobalOverride()
        {
            using var collection = new ProjectCollection();
            Project project = new Project(collection);

            // Set a global property called MSBuildExtensionsPath to some value, for the purpose
            // of seeing whether our value wins.
            project.SetGlobalProperty("MSBuildExtensionsPath", @"c:\devdiv\vscore\msbuild");
            project.ReevaluateIfNecessary();

            Assert.Equal(@"c:\devdiv\vscore\msbuild", project.GetPropertyValue("MSBuildExtensionsPath"));
        }

#if FEATURE_INSTALLED_MSBUILD
        /// <summary>
        /// The default value for $(MSBuildExtensionsPath32) should point to "c:\program files (x86)\msbuild" on a 64 bit machine.
        /// We can't test that unless we are on a 64 bit box, but this test will work on either
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath32Default()
        {
            // On a 64 bit machine we always want to use the program files x86.  If we are running as a 64 bit process then this variable will be set correctly
            // If we are on a 32 bit machine or running as a 32 bit process then this variable will be null and the programFiles variable will be correct.
            string expected = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (String.IsNullOrEmpty(expected))
            {
                // 32 bit box
                expected = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }

            string extensionsPath32Env = Environment.GetEnvironmentVariable("MSBuildExtensionsPath32");

            try
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", null);
                using var collection = new ProjectCollection();
                Project project = new Project(collection);

                string msbuildPath = NativeMethodsShared.IsWindows ? Path.Combine(expected, "MSBuild") : "MSBuild";
                Assert.Equal(msbuildPath, project.GetPropertyValue("MSBuildExtensionsPath32"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", extensionsPath32Env);
            }
        }
#endif

        /// <summary>
        /// Set an env var called MSBuildExtensionsPath32 to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath32WithEnvironmentOverride()
        {
            string originalMSBuildExtensionsPath32Value = Environment.GetEnvironmentVariable("MSBuildExtensionsPath32");

            try
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", @"c:\devdiv\vscore\msbuild");
                using var collection = new ProjectCollection();
                Project project = new Project(collection);
                string msbuildExtensionsPath32Value = project.GetPropertyValue("MSBuildExtensionsPath32");
                Assert.Equal(@"c:\devdiv\vscore\msbuild", msbuildExtensionsPath32Value);
            }
            finally
            {
                // And restore the old value
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath32", originalMSBuildExtensionsPath32Value);
            }
        }

        /// <summary>
        /// Set a global property called MSBuildExtensionsPath32 to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath32WithGlobalOverride()
        {
            using var collection = new ProjectCollection();
            Project project = new Project(collection);

            project.SetGlobalProperty("MSBuildExtensionsPath32", @"c:\devdiv\vscore\msbuild");
            string msbuildExtensionsPath32Value = project.GetPropertyValue("MSBuildExtensionsPath32");
            Assert.Equal(@"c:\devdiv\vscore\msbuild", msbuildExtensionsPath32Value);
        }

#if FEATURE_INSTALLED_MSBUILD
        /// <summary>
        /// The default value for $(MSBuildExtensionsPath64) should point to "c:\program files\msbuild" on a 64 bit machine,
        /// and should be empty on a 32-bit machine.
        /// We can't test that unless we are on a 64 bit box, but this test will work on either
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath64Default()
        {
            string expected = string.Empty;

            // If we are on a 32 bit machine then this variable will be null.
            string programFiles32 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!String.IsNullOrEmpty(programFiles32))
            {
                // only set in 32-bit windows on 64-bit machines
                expected = Environment.GetEnvironmentVariable("ProgramW6432");

                if (string.IsNullOrEmpty(expected))
                {
                    // 64-bit window on a 64-bit machine -- ProgramFiles is correct
                    expected = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                }
            }

            if (!string.IsNullOrEmpty(expected))
            {
                expected += @"\MSBuild";
            }

            Project project = new Project();

            Assert.Equal(expected, project.GetPropertyValue("MSBuildExtensionsPath64"));
        }
#endif

        /// <summary>
        /// Set an env var called MSBuildExtensionsPath64 to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath64WithEnvironmentOverride()
        {
            string originalMSBuildExtensionsPath64Value = Environment.GetEnvironmentVariable("MSBuildExtensionsPath64");

            try
            {
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath64", @"c:\devdiv\vscore\msbuild");
                using var collection = new ProjectCollection();
                Project project = new Project(collection);
                string msbuildExtensionsPath64Value = project.GetPropertyValue("MSBuildExtensionsPath64");
                Assert.Equal(@"c:\devdiv\vscore\msbuild", msbuildExtensionsPath64Value);
            }
            finally
            {
                // And restore the old value
                Environment.SetEnvironmentVariable("MSBuildExtensionsPath64", originalMSBuildExtensionsPath64Value);
            }
        }

        /// <summary>
        /// Set a global property called MSBuildExtensionsPath64 to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void MSBuildExtensionsPath64WithGlobalOverride()
        {
            using var collection = new ProjectCollection();
            Project project = new Project(collection);

            project.SetGlobalProperty("MSBuildExtensionsPath64", @"c:\devdiv\vscore\msbuild");
            string msbuildExtensionsPath64Value = project.GetPropertyValue("MSBuildExtensionsPath64");
            Assert.Equal(@"c:\devdiv\vscore\msbuild", msbuildExtensionsPath64Value);
        }

        /// <summary>
        /// Verify whether LocalAppData property is set by default in msbuild
        /// with the path of the OS special LocalApplicationData or ApplicationData folders.
        /// </summary>
        [Fact]
        public void LocalAppDataDefault()
        {
            string expected = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(expected))
            {
                expected = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            Project project = new Project();

            Assert.Equal(expected, project.GetPropertyValue("LocalAppData"));
        }

        /// <summary>
        /// Set an env var called LocalAppData to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void LocalAppDataWithEnvironmentOverride()
        {
            string originalLocalAppDataValue = Environment.GetEnvironmentVariable("LocalAppData");

            try
            {
                Environment.SetEnvironmentVariable("LocalAppData", @"c:\AppData\Local");
                using var collection = new ProjectCollection();
                Project project = new Project(collection);
                string localAppDataValue = project.GetPropertyValue("LocalAppData");
                Assert.Equal(@"c:\AppData\Local", localAppDataValue);
            }
            finally
            {
                // And restore the old value
                Environment.SetEnvironmentVariable("LocalAppData", originalLocalAppDataValue);
            }
        }

        /// <summary>
        /// Set a global property called LocalAppData to some value, for the purpose
        /// of seeing whether our value wins.
        /// </summary>
        [Fact]
        public void LocalAppDataWithGlobalOverride()
        {
            using var collection = new ProjectCollection();
            Project project = new Project(collection);

            project.SetGlobalProperty("LocalAppData", @"c:\AppData\Local");
            string localAppDataValue = project.GetPropertyValue("LocalAppData");
            Assert.Equal(@"c:\AppData\Local", localAppDataValue);
        }

        [Fact]
        public void MSBuildAssemblyVersion()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.DefaultTargets = "Build";
            Project project = new Project(xml);

            Version.TryParse(project.GetPropertyValue("MSBuildAssemblyVersion"), out Version assemblyVersionAsVersion).ShouldBeTrue();

            // This version has historically not incremented for patch releases
            assemblyVersionAsVersion.Minor.ShouldBe(0);

            // Version parses missing elements into -1, and this property should be Major.0 only
            assemblyVersionAsVersion.Build.ShouldBe(-1);
            assemblyVersionAsVersion.Revision.ShouldBe(-1);
        }

        [Fact]
        public void MSBuildVersion()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.DefaultTargets = "Build";
            Project project = new Project(xml);

            string msbuildVersionProperty = project.GetPropertyValue("MSBuildVersion");
            string msbuildFileVersionProperty = project.GetPropertyValue("MSBuildFileVersion");
            string msbuildSemanticVersionProperty = project.GetPropertyValue("MSBuildSemanticVersion");

            Version.TryParse(msbuildVersionProperty, out Version msbuildVersionAsVersion).ShouldBeTrue();

            msbuildVersionAsVersion.Minor.ShouldBeInRange(0, 20,
                customMessage: $"minor version {msbuildVersionProperty} looks fishy. If we're really in x.20.0, go ahead and change the constant. This is to guard against being nonsensical like 16.200.19");

            // Version parses missing elements into -1, and this property should be Major.Minor.Patch only
            msbuildVersionAsVersion.Revision.ShouldBe(-1);

            msbuildFileVersionProperty.ShouldBe(ProjectCollection.Version.ToString());
            ProjectCollection.Version.ToString().ShouldStartWith(msbuildVersionProperty,
                customMessage: "ProjectCollection.Version should match the property MSBuildVersion, but can contain another version part");

            msbuildSemanticVersionProperty.ShouldBe(ProjectCollection.DisplayVersion);
            ProjectCollection.DisplayVersion.ShouldStartWith(msbuildVersionProperty,
                customMessage: "DisplayVersion is semver2 while MSBuildVersion is Major.Minor.Build but should be a prefix match");
        }


        /// <summary>
        /// Test standard reserved properties
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void ReservedProjectProperties()
        {
            string file = NativeMethodsShared.IsWindows ? @"c:\foo\bar.csproj" : "/foo/bar.csproj";
            ProjectRootElement xml = ProjectRootElement.Create(file);
            xml.DefaultTargets = "Build";
            Project project = new Project(xml);

            Assert.Equal(@"c:\foo", project.GetPropertyValue("MSBuildProjectDirectory"));
            Assert.Equal(@"foo", project.GetPropertyValue("MSBuildProjectDirectoryNoRoot"));
            Assert.Equal("bar.csproj", project.GetPropertyValue("MSBuildProjectFile"));
            Assert.Equal(".csproj", project.GetPropertyValue("MSBuildProjectExtension"));
            Assert.Equal(@"c:\foo\bar.csproj", project.GetPropertyValue("MSBuildProjectFullPath"));
            Assert.Equal("bar", project.GetPropertyValue("MSBuildProjectName"));
        }

        /// <summary>
        /// Test standard reserved properties
        /// </summary>
        [Fact]
        public void ReservedProjectPropertiesAtRoot()
        {
            string file = NativeMethodsShared.IsWindows ? @"c:\bar.csproj" : "/bar.csproj";
            string dir = NativeMethodsShared.IsWindows ? @"c:\" : "/";
            ProjectRootElement xml = ProjectRootElement.Create(file);
            Project project = new Project(xml);

            Assert.Equal(dir, project.GetPropertyValue("MSBuildProjectDirectory"));
            Assert.Equal(string.Empty, project.GetPropertyValue("MSBuildProjectDirectoryNoRoot"));
            Assert.Equal("bar.csproj", project.GetPropertyValue("MSBuildProjectFile"));
            Assert.Equal(".csproj", project.GetPropertyValue("MSBuildProjectExtension"));
            Assert.Equal(file, project.GetPropertyValue("MSBuildProjectFullPath"));
            Assert.Equal("bar", project.GetPropertyValue("MSBuildProjectName"));
        }

        /// <summary>
        /// Test standard reserved properties on UNC at root
        /// </summary>
        [WindowsOnlyFact("UNC is only available under Windows.")]
        public void ReservedProjectPropertiesOnUNCRoot()
        {
            string uncFile = @"\\foo\bar\baz.csproj";
            ProjectRootElement xml = ProjectRootElement.Create(uncFile);
            Project project = new Project(xml);

            Assert.Equal(@"\\foo\bar", project.GetPropertyValue("MSBuildProjectDirectory"));
            Assert.Equal(string.Empty, project.GetPropertyValue("MSBuildProjectDirectoryNoRoot"));
            Assert.Equal("baz.csproj", project.GetPropertyValue("MSBuildProjectFile"));
            Assert.Equal(".csproj", project.GetPropertyValue("MSBuildProjectExtension"));
            Assert.Equal(@"\\foo\bar\baz.csproj", project.GetPropertyValue("MSBuildProjectFullPath"));
            Assert.Equal("baz", project.GetPropertyValue("MSBuildProjectName"));
        }

        /// <summary>
        /// Test standard reserved properties on UNC
        /// </summary>
        [WindowsOnlyFact("UNC is only available under Windows.")]
        public void ReservedProjectPropertiesOnUNC()
        {
            string uncFile = @"\\foo\bar\baz\biz.csproj";
            ProjectRootElement xml = ProjectRootElement.Create(uncFile);
            Project project = new Project(xml);

            Assert.Equal(@"\\foo\bar\baz", project.GetPropertyValue("MSBuildProjectDirectory"));
            Assert.Equal(@"baz", project.GetPropertyValue("MSBuildProjectDirectoryNoRoot"));
            Assert.Equal("biz.csproj", project.GetPropertyValue("MSBuildProjectFile"));
            Assert.Equal(".csproj", project.GetPropertyValue("MSBuildProjectExtension"));
            Assert.Equal(@"\\foo\bar\baz\biz.csproj", project.GetPropertyValue("MSBuildProjectFullPath"));
            Assert.Equal("biz", project.GetPropertyValue("MSBuildProjectName"));
        }

#if FEATURE_APPDOMAIN
        /// <summary>
        /// Verify when a node count is passed through on the project collection that the correct number is used to evaluate the msbuildNodeCount
        /// </summary>
        [Fact]
        public void VerifyMsBuildNodeCountReservedProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <abcdef>$(MSBuildNodeCount)</abcdef>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(abcdef)]' />
                                </Target>
                              </Project>");

            // Setup a project collection which asks for 4 nodes
            using ProjectCollection collection =
                new ProjectCollection(
                                   ProjectCollection.GlobalProjectCollection.GlobalProperties,
                                   ProjectCollection.GlobalProjectCollection.Loggers,
                                   null,
                                   ProjectCollection.GlobalProjectCollection.ToolsetLocations,
                                   4,
                    false);

            using ProjectFromString projectFromString = new(content, new Dictionary<string, string>(), ObjectModelHelpers.MSBuildDefaultToolsVersion, collection);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(String.Format("[{0}]", 4));
        }
#endif

        /// <summary>
        /// Verify when no node count is passed that we evaluate MsBuildNodeCount to 1
        /// </summary>
        [Fact]
        public void VerifyMsBuildNodeCountReservedPropertyDefault()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <abcdef>$(MSBuildNodeCount)</abcdef>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(abcdef)]' />
                                </Target>
                              </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(String.Format("[{0}]", 1));
        }

        /// <summary>
        /// Verify that the programfiles32 property points to the correct location
        /// </summary>
        [Fact]
        public void VerifyMsbuildProgramFiles32ReservedProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <abcdef>$(MsBuildProgramFiles32)</abcdef>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(abcdef)]' />
                                </Target>
                              </Project>");
            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(String.Format("[{0}]", FrameworkLocationHelper.programFiles32));
        }

        /// <summary>
        /// Basic verification -- adding the tag to the ProjectRootElement on its own does nothing.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyTagDoesNothingIfNoGlobalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");
            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains("[Bar]");
        }

        /// <summary>
        /// Basic verification -- with no TreatAsLocalProperty, but with a global property specified, the global property
        /// overrides the local property.
        /// </summary>
        [Fact]
        public void VerifyGlobalPropertyOverridesIfNoTreatAsLocalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains("[Baz]");
        }

        /// <summary>
        /// Basic verification -- with TreatAsLocalProperty, and with a global property specified, the local property
        /// overrides the global property.
        /// </summary>
        [Fact]
        public void VerifyLocalPropertyOverridesIfTreatAsLocalPropertySet()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains("[Bar]");
        }

        /// <summary>
        /// Basic verification -- with TreatAsLocalProperty set, but to a different property than is being passed as a global, the
        /// global property overrides the local property.
        /// </summary>
        [Fact]
        public void VerifyGlobalPropertyOverridesNonSpecifiedLocalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo2"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains("[Baz]");
        }

        /// <summary>
        /// Basic verification -- with TreatAsLocalProperty set, but to a different property than is being passed as a global, the
        /// global property overrides the local property.
        /// </summary>
        [Fact]
        public void VerifyLocalPropertyInheritsFromOverriddenGlobalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains("[BazBar]");
        }

        /// <summary>
        /// Basic verification -- with TreatAsLocalProperty set, but to a different property than is being passed as a global, the
        /// global property overrides the local property.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertySpecificationWorksIfSpecificationIsItselfAProperty()
        {
            string oldEnvironmentValue = Environment.GetEnvironmentVariable("EnvironmentProperty");

            try
            {
                Environment.SetEnvironmentVariable("EnvironmentProperty", "Bar;Baz");

                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""$(GlobalProperty);$(EnvironmentProperty)"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo2</Foo>
                                    <Bar>$(Bar)Bar2</Bar>
                                    <Baz>$(Baz)Baz2</Baz>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                    <Message Text=' .[[$(Bar)]].' />
                                    <Message Text='.[[[$(Baz)]]].' />
                                </Target>
                              </Project>");

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Foo1");
                globalProperties.Add("Bar", "Bar1");
                globalProperties.Add("Baz", "Baz1");
                globalProperties.Add("GlobalProperty", "Foo");

                using var collection = new ProjectCollection();
                using ProjectFromString projectFromString = new(content, globalProperties, null, collection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains(".[Foo1Foo2].");
                logger.AssertLogContains(".[[Bar1Bar2]].");
                logger.AssertLogContains(".[[[Baz1Baz2]]].");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EnvironmentProperty", oldEnvironmentValue);
            }
        }

        /// <summary>
        /// Basic verification -- setting an invalid TreatAsLocalProperty should be an evaluation error.
        /// </summary>
        [Fact]
        public void VerifyInvalidTreatAsLocalProperty()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""||Bar;Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Baz");

                Project project = new Project(XmlReader.Create(new StringReader(content)), globalProperties, null);

                MockLogger logger = new MockLogger();
                project.Build(logger);

                // Should not reach this point.
                Assert.Fail();
            });
        }
        /// <summary>
        /// Basic verification -- whitespace in the TreatAsLocalProperty definition should be trimmed.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyTrimmed()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""   Foo;
                                                                                     Goo
                                "" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo</Foo>
                                    <Bar>$(Goo)Goo</Bar>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text=' .[$(Foo)].' />
                                    <Message Text='.[[$(Bar)]].' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");
            globalProperties.Add("Goo", "Foo");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(".[BazFoo].");
            logger.AssertLogContains(".[[FooGoo]].");
        }

        /// <summary>
        /// Basic verification -- if there are empty entries in the split of the properties for TreatAsLocalProperty,
        /// they should be ignored.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyEmptySplits()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo;;;Goo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Goo</Foo>
                                    <Goo>$(Goo)Goo</Goo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text=' .[$(Foo)].' />
                                    <Message Text='.[[$(Goo)]].' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");
            globalProperties.Add("Goo", "Foo");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(".[BazGoo].");
            logger.AssertLogContains(".[[FooGoo]].");
        }

        /// <summary>
        /// Basic verification -- if looking at the project in the OM, verify that while looking at the property
        /// value returns the mutable version, looking explicitly at the global properties dictionary still returns
        /// the original global property value.
        /// </summary>
        [Fact]
        public void VerifyGlobalPropertyRetainsOriginalValue()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text=' .[$(Foo)].' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            Assert.Equal("BazBar", project.GetPropertyValue("Foo"));
            Assert.Equal("Baz", project.GlobalProperties["Foo"]);
        }

        /// <summary>
        /// Basic verification -- if TreatAsLocalProperty is modified on the project XML and then the project is
        /// re-evaluated, it should be re-evaluated in the context of that modified value.
        /// </summary>
        [Fact]
        public void VerifyModificationsToTreatAsLocalPropertyRespected()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo;Goo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Goo</Foo>
                                    <Goo>$(Goo)Goo</Goo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text=' .[$(Foo)].' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Baz");
            globalProperties.Add("Goo", "Foo");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            Assert.Equal("Foo;Goo", project.Xml.TreatAsLocalProperty);

            Assert.Equal("BazGoo", project.GetPropertyValue("Foo"));
            Assert.Equal("Baz", project.GlobalProperties["Foo"]);
            Assert.Equal("FooGoo", project.GetPropertyValue("Goo"));
            Assert.Equal("Foo", project.GlobalProperties["Goo"]);

            project.Xml.TreatAsLocalProperty = "Foo";
            project.ReevaluateIfNecessary();

            Assert.Equal("BazGoo", project.GetPropertyValue("Foo"));
            Assert.Equal("Baz", project.GlobalProperties["Foo"]);
            Assert.Equal("Foo", project.GetPropertyValue("Goo"));
            Assert.Equal("Foo", project.GlobalProperties["Goo"]);
        }

        /// <summary>
        /// Basic verification -- if TreatAsLocalProperty is modified on the project XML and then the project is
        /// re-evaluated, it should be re-evaluated in the context of that modified value.
        /// </summary>
        [Fact]
        public void VerifyModificationsToGlobalPropertiesRespected()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text=' .[$(Foo)].' />
                                </Target>
                              </Project>");

            using ProjectFromString projectFromString = new(content);
            Project project = projectFromString.Project;

            Assert.Equal("Bar", project.GetPropertyValue("Foo"));
            Assert.False(project.GlobalProperties.ContainsKey("Foo"));

            project.SetGlobalProperty("Foo", "Baz");
            project.ReevaluateIfNecessary();

            Assert.Equal("BazBar", project.GetPropertyValue("Foo"));
            Assert.Equal("Baz", project.GlobalProperties["Foo"]);

            project.RemoveGlobalProperty("Foo");
            project.ReevaluateIfNecessary();

            Assert.Equal("Bar", project.GetPropertyValue("Foo"));
            Assert.False(project.GlobalProperties.ContainsKey("Foo"));
        }

        /// <summary>
        /// Basic verification -- with TreatAsLocalProperty set to multiple global properties, and with multiple global properties
        /// passed in, only the ones that are marked TALP are overridable.
        /// </summary>
        [Fact]
        public void VerifyOnlySpecifiedPropertiesOverridden()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo;Bar"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Foo2</Foo>
                                    <Bar>Bar2</Bar>
                                    <Baz>Baz2</Baz>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                    <Message Text=' .[[$(Bar)]].' />
                                    <Message Text='.[[[$(Baz)]]].' />
                                </Target>
                              </Project>");

            IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Foo", "Foo1");
            globalProperties.Add("Bar", "Bar1");
            globalProperties.Add("Baz", "Baz1");

            using ProjectFromString projectFromString = new(content, globalProperties, null);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);
            logger.AssertLogContains(".[Foo2].");
            logger.AssertLogContains(".[[Bar2]].");
            logger.AssertLogContains(".[[[Baz1]]].");
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in a parent project, that property is still treated as overridable
        /// when defined in an imported project.
        /// </summary>
        [Fact]
        public void VerifyPropertySetInImportStillOverrides()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>

                                <Import Project=""import.proj"" />
                              </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Bar</Foo>
                                </PropertyGroup>
                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyPropertySetInImportStillOverrides");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Baz");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains("[BazBar]");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in an imported project, any instances of that property in the parent
        /// project before the import are ignored and the global property value is used instead.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyInImportDoesntAffectParentProjectAboveIt()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>

                                <Import Project=""import.proj"" />
                              </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>

                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyTreatAsLocalPropertyInImportDoesntAffectParentProjectAboveIt");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Baz");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains("[Baz]");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in an imported project, any instances of that property in the parent
        /// project after the import recognize the TreatAsLocalProperty flag and override the global property value.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyInImportAffectsParentProjectBelowIt()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Import Project=""import.proj"" />
                                <PropertyGroup>
                                    <Foo>Bar</Foo>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='[$(Foo)]' />
                                </Target>
                              </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>

                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyTreatAsLocalPropertyInImportAffectsParentProjectBelowIt");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Baz");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains("[Bar]");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in both the parent and imported project, the end result
        /// set of overridable properties is the union of the two sets, though of course you cannot
        /// override a property until you reach the import that mentions it in its TreatAsLocalProperty
        /// parameter.
        /// </summary>
        [Fact]
        public void VerifyTreatAsLocalPropertyUnionBetweenImports()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo1</Foo>
                                    <Bar>$(Bar)Bar1</Bar>
                                    <Baz>$(Baz)Baz1</Baz>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                    <Message Text=' .[[$(Bar)]].' />
                                    <Message Text='.[[[$(Baz)]]].' />
                                </Target>

                                <Import Project=""import.proj"" />
                            </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Bar;Baz"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo2</Foo>
                                    <Bar>$(Bar)Bar2</Bar>
                                    <Baz>$(Baz)Baz2</Baz>
                                </PropertyGroup>

                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyTreatAsLocalPropertyUnionBetweenImports");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Foo3");
                globalProperties.Add("Bar", "Bar3");
                globalProperties.Add("Baz", "Baz3");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains(".[Foo3Foo1Foo2].");
                logger.AssertLogContains(".[[Bar3Bar2]].");
                logger.AssertLogContains(".[[[Baz3Baz2]]].");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If a property is set to TreatAsLocalProperty in both the parent project and the import, this is
        /// silently acknowledged.
        /// </summary>
        [Fact]
        public void VerifyDuplicateTreatAsLocalProperty()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo1</Foo>
                                    <Bar>$(Bar)Bar1</Bar>
                                    <Baz>$(Baz)Baz1</Baz>
                                </PropertyGroup>

                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                    <Message Text=' .[[$(Bar)]].' />
                                    <Message Text='.[[[$(Baz)]]].' />
                                </Target>
                                <Import Project=""import.proj"" />
                              </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo;Bar;Baz"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo2</Foo>
                                    <Bar>$(Bar)Bar2</Bar>
                                    <Baz>$(Baz)Baz2</Baz>
                                </PropertyGroup>

                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyDuplicateTreatAsLocalProperty");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Foo3");
                globalProperties.Add("Bar", "Bar3");
                globalProperties.Add("Baz", "Baz3");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains(".[Foo3Foo1Foo2].");
                logger.AssertLogContains(".[[Bar3Bar2]].");
                logger.AssertLogContains(".[[[Baz3Baz2]]].");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in a parent project, a project that is P2P'ed to will
        /// still receive the original value of the global property.
        /// </summary>
        [Fact]
        public void VerifyGlobalPropertyPassedToP2P()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo2</Foo>
                                </PropertyGroup>

                                <Target Name='t0'>
                                       <MSBuild Projects=""project2.proj"" Targets=""t"" />
                                </Target>
                              </Project>");

            string project2Contents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                </Target>
                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyGlobalPropertyPassedToP2P");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string project2 = Path.Combine(projectDirectory, "project2.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(project2, project2Contents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Foo1");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains(".[Foo1].");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// If TreatAsLocalProperty is set in a parent project, a project that is P2P'ed who is explicitly
        /// passed the property, will get the mutable local value rather than the original value of the
        /// global property.
        /// </summary>
        [Fact]
        public void VerifyLocalPropertyPropagatesIfExplicitlyPassedToP2P()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" TreatAsLocalProperty=""Foo"" xmlns='msbuildnamespace'>
                                <PropertyGroup>
                                    <Foo>$(Foo)Foo2</Foo>
                                </PropertyGroup>

                                <Target Name='t0'>
                                       <MSBuild Projects=""project2.proj"" Targets=""t"" Properties=""Foo=$(Foo)"" />
                                </Target>
                              </Project>");

            string project2Contents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='  .[$(Foo)].' />
                                </Target>
                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyLocalPropertyPropagatesIfExplicitlyPassedToP2P");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string project2 = Path.Combine(projectDirectory, "project2.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(project2, project2Contents);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("Foo", "Foo1");

                Project project = new Project(primaryProject, globalProperties, null);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);
                logger.AssertLogContains(".[Foo1Foo2].");
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// Verify that when we don't specify the sub-toolset version, we get the correct sub-toolset properties
        /// based on the default sub-toolset version -- base toolset if Dev10 is installed, or lowest (numerically
        /// sorted) toolset if it's not.
        /// </summary>
        [Fact(Skip = "https://github.com/dotnet/msbuild/issues/4363")]
        public void VerifyDefaultSubToolsetPropertiesAreEvaluated()
        {
            if (NativeMethodsShared.IsUnixLike)
            {
                return; // "TODO: Under Unix this fails unexpectedly"
            }

            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");
            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", null);
                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no global properties */);
                using ProjectFromString projectFromString = new(content, null, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                if (Toolset.Dev10IsInstalled)
                {
                    // if Dev10 is installed, the default sub-toolset is nothing == base toolset.
                    logger.AssertLogContains(".[a1].");
                    logger.AssertLogContains(".[[b1]].");
                    logger.AssertLogContains(".[[[]]].");
                    logger.AssertLogContains(".[[[[10.0]]]].");
                }
                else
                {
                    // if Dev10 is not installed, the default sub-toolset is the numerical least -- in our case, "11.0" --
                    // so the toolset properties are a combination of that + the base toolset.
                    logger.AssertLogContains(".[a1].");
                    logger.AssertLogContains(".[[b2]].");
                    logger.AssertLogContains(".[[[c2]]].");
                    logger.AssertLogContains(".[[[[11.0]]]].");
                }

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a1||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that when we specify an invalid sub-toolset version, we just get the properties from the base
        /// toolset ... but that invalid version is still reflected as a project property.
        /// </summary>
        [Fact]
        public void VerifyNoSubToolsetPropertiesAreEvaluatedWithInvalidSubToolset()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");
            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "ABCDE");
                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no global properties */);
                using ProjectFromString projectFromString = new(content, null, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a1].");
                logger.AssertLogContains(".[[b1]].");
                logger.AssertLogContains(".[[[]]].");
                logger.AssertLogContains(".[[[[ABCDE]]]].");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a1||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that if a sub-toolset is explicitly specified, its properties are evaluated into the project properly.
        /// </summary>
        [Fact]
        public void VerifyExplicitSubToolsetPropertiesAreEvaluated()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");
            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "FakeSubToolset");
                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no global properties */);
                using ProjectFromString projectFromString = new(content, null, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a3].");
                logger.AssertLogContains(".[[b1]].");
                logger.AssertLogContains(".[[[c3]]].");
                logger.AssertLogContains(".[[[[FakeSubToolset]]]].");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a3||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that if a non-existent sub-toolset is specified, we simply ignore it and just use the base toolset properties.
        /// </summary>
        [Fact]
        public void VerifyExplicitNonExistentSubToolsetPropertiesAreEvaluated()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");
            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "abcdef");
                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no global properties */);
                using ProjectFromString projectFromString = new(content, null, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a1].");
                logger.AssertLogContains(".[[b1]].");
                logger.AssertLogContains(".[[[]]].");
                logger.AssertLogContains(".[[[[abcdef]]]].");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a1||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that if there is a conflict between sub-toolset and environment properties, the sub-toolset properties win.
        /// </summary>
        [Fact]
        public void VerifySubToolsetPropertiesOverrideEnvironment()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");
            string originalC = Environment.GetEnvironmentVariable("C");
            string originalD = Environment.GetEnvironmentVariable("D");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "FakeSubToolset");
                Environment.SetEnvironmentVariable("C", "c4"); // not explosive :)
                Environment.SetEnvironmentVariable("D", "d4");

                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='    .[$(a)].' />
                                    <Message Text='   .[[$(b)]].' />
                                    <Message Text='  .[[[$(c)]]].' />
                                    <Message Text=' .[[[[$(d)]]]].' />
                                    <Message Text='.[[[[[$(VisualStudioVersion)]]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no global properties */);
                using ProjectFromString projectFromString = new(content, null, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a3].");
                logger.AssertLogContains(".[[b1]].");
                logger.AssertLogContains(".[[[c3]]].");
                logger.AssertLogContains(".[[[[d4]]]].");
                logger.AssertLogContains(".[[[[[FakeSubToolset]]]]]");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a3||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
                Environment.SetEnvironmentVariable("C", originalC);
                Environment.SetEnvironmentVariable("D", originalD);
            }
        }

        /// <summary>
        /// Verify that if there is a conflict between sub-toolset and global properties, the global properties win.
        /// </summary>
        [Fact]
        public void VerifyGlobalPropertiesOverrideSubToolset()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "FakeSubToolset");

                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='    .[$(a)].' />
                                    <Message Text='   .[[$(b)]].' />
                                    <Message Text='  .[[[$(c)]]].' />
                                    <Message Text=' .[[[[$(d)]]]].' />
                                    <Message Text='.[[[[[$(VisualStudioVersion)]]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no project collection global properties */);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>();
                globalProperties.Add("c", "c5");
                globalProperties.Add("d", "d5");

                using ProjectFromString projectFromString = new(content, globalProperties, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a3].");
                logger.AssertLogContains(".[[b1]].");
                logger.AssertLogContains(".[[[c5]]].");
                logger.AssertLogContains(".[[[[d5]]]].");
                logger.AssertLogContains(".[[[[[FakeSubToolset]]]]].");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a3||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that even if the sub-toolset was set by a global property, it can be overridden from within the project
        /// </summary>
        [Fact]
        public void VerifySubToolsetVersionSetByGlobalPropertyStillOverridable()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "FakeSubToolset");

                string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

                using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no project collection global properties */);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>();
                globalProperties.Add("VisualStudioVersion", "11.0");

                using ProjectFromString projectFromString = new(content, globalProperties, "Fake", fakeProjectCollection);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);
                Assert.True(result);

                logger.AssertLogContains(".[a1].");
                logger.AssertLogContains(".[[b2]].");
                logger.AssertLogContains(".[[[c2]]].");
                logger.AssertLogContains(".[[[[11.0]]]].");

                // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
                // the value of any of the sub-toolset properties.
                logger.AssertLogContains("|changed|");
                logger.AssertLogContains("||a1||");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that if the sub-toolset was set by a global property, it cannot be overridden from within the project
        /// </summary>
        [Fact]
        public void VerifySubToolsetVersionSetByConstructorOverridable_OverridesGlobalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

            using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no project collection global properties */);

            IDictionary<string, string> globalProperties = new Dictionary<string, string>();
            globalProperties.Add("VisualStudioVersion", "11.0");

            using ProjectFromString projectFromString = new(content, globalProperties, "Fake", "FakeSubToolset", fakeProjectCollection, ProjectLoadSettings.Default);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);

            logger.AssertLogContains(".[a3].");
            logger.AssertLogContains(".[[b1]].");
            logger.AssertLogContains(".[[[c3]]].");
            logger.AssertLogContains(".[[[[FakeSubToolset]]]].");

            // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
            // the value of any of the sub-toolset properties.
            logger.AssertLogContains("|changed|");
            logger.AssertLogContains("||a3||");
        }

        /// <summary>
        /// Verify that if the sub-toolset was set by a global property, it cannot be overridden from within the project
        /// </summary>
        [Fact]
        public void VerifySubToolsetVersionSetByConstructorOverridable()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                <Target Name='t'>
                                    <Message Text='   .[$(a)].' />
                                    <Message Text='  .[[$(b)]].' />
                                    <Message Text=' .[[[$(c)]]].' />
                                    <Message Text='.[[[[$(VisualStudioVersion)]]]].' />
                                </Target>
                                <Target Name='t2' AfterTargets='t'>
                                    <PropertyGroup>
                                        <VisualStudioVersion>changed</VisualStudioVersion>
                                    </PropertyGroup>

                                    <Message Text='|$(VisualStudioVersion)|' />
                                    <Message Text='||$(a)||' />
                                </Target>
                              </Project>");

            using ProjectCollection fakeProjectCollection = GetProjectCollectionWithFakeToolset(null /* no project collection global properties */);

            using ProjectFromString projectFromString = new(content, null, "Fake", "FakeSubToolset", fakeProjectCollection, ProjectLoadSettings.Default);
            Project project = projectFromString.Project;

            MockLogger logger = new MockLogger();
            bool result = project.Build(logger);
            Assert.True(result);

            logger.AssertLogContains(".[a3].");
            logger.AssertLogContains(".[[b1]].");
            logger.AssertLogContains(".[[[c3]]].");
            logger.AssertLogContains(".[[[[FakeSubToolset]]]].");

            // whatever the initial value of VisualStudioVersion, we should be able to change it, but it doesn't affect
            // the value of any of the sub-toolset properties.
            logger.AssertLogContains("|changed|");
            logger.AssertLogContains("||a3||");
        }

        /// <summary>
        /// Verifies that if no VisualStudioVersion is set that the toolset with set a value.
        /// </summary>
        [Fact]
        public void VerifyVisualStudioVersionSetByToolset()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVerson");

            try
            {
                // Ensure that VisualStudioVersion is not set as an environment variable
                //
                Environment.SetEnvironmentVariable("VisualStudioVersion", null);

                // No global properties are passed to the ProjectCollection so VisualStudioVersion should not be set
                //
                using var collection = new ProjectCollection();
                Project project = new Project(null, ObjectModelHelpers.MSBuildDefaultToolsVersion, collection);

                string actual = project.GetPropertyValue(Constants.VisualStudioVersionPropertyName);

                Assert.Equal(MSBuildConstants.CurrentVisualStudioVersion, actual);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that DTD processing is disabled when loading a project
        /// We add some invalid DTD code to a MSBuild project, if such code is ever parsed a XmlException will be thrown
        /// If DTD parsing is disabled (desired behavior), no XmlException should be caught
        /// </summary>
        [Fact]
        public void VerifyDTDProcessingIsDisabled()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(
                                @"<?xml version=""1.0"" encoding=""utf-8""?>
                                <!DOCTYPE Project [
                                <!ELEMENT DUMMYELEMENT (SOMETHING+)>
                                <INVALID_DTD>
                                ]>
                                <Project ToolsVersion=""msbuilddefaulttoolsversion"" DefaultTargets=""SimpleMessage"" xmlns=""msbuildnamespace"">
                                    <Target Name=""SimpleMessage"">
                                        <Message Text=""Dummy project""/>
                                    </Target>
                                </Project>");
            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyDTDProcessingIsDisabled");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string projectFilename = Path.Combine(projectDirectory, "project.proj");

                File.WriteAllText(projectFilename, projectContents);

                Project project = new Project(projectFilename);

                MockLogger logger = new MockLogger();
                project.Build(logger);
            }
            catch (XmlException)
            {
                // XmlException thrown when invalid DTD statement is parsed: it means DTD processing was enabled
                Assert.Fail();
            }
        }

#if FEATURE_HTTP_LISTENER
        private Exception _httpListenerThreadException = null;

        /// <summary>
        /// Verify that DTD processing is disabled when loading a project
        /// We create an HTTP server that waits for a request and load a project containing DTD code making reference to a fictitious file in the server.
        /// This test emulates a scenario where some malicious DTD code could upload user data to a malicious website
        /// If DTD processing is disabled, the server should not receive any connection request.
        /// </summary>
        [Fact]
        [ActiveIssue("https://github.com/dotnet/msbuild/issues/7623")]
        public async Task VerifyDTDProcessingIsDisabled2()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"<?xml version=""1.0"" encoding=""utf-8""?>
                                <!DOCTYPE Project [
                                    <!ENTITY % external SYSTEM ""http://localhost:51111/ext.xml"">
                                    %external;
                                    %param1;
                                    %test;
                                ]>
                                <Project ToolsVersion=""msbuilddefaulttoolsversion"" DefaultTargets=""SimpleMessage"" xmlns=""msbuildnamespace"">
                                    <Target Name=""SimpleMessage"">
                                        <Message Text=""Dummy project""/>
                                    </Target>
                                </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "VerifyDTDProcessingIsDisabled2");

            Thread t = new(HttpServerThread);
            t.IsBackground = true;
            t.Start();

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, recursive: true);
                }

                Directory.CreateDirectory(projectDirectory);

                string projectFilename = Path.Combine(projectDirectory, "project.proj");

                File.WriteAllText(projectFilename, projectContents);

                Project project = new(projectFilename);

                MockLogger logger = new();
                project.Build(logger);
            }
            finally
            {
                await Task.Delay(500);
                t.IsAlive.ShouldBeTrue();
                t.Abort();
                await Task.Delay(500);

                // Expect server to be alive and hung up unless a request originating from DTD processing was sent
                _httpListenerThreadException.ShouldBeNull();
            }
        }
#endif

        /// <summary>
        /// Verify that Condition Evaluator does reset the cached state when the evaluation throws an exception.
        /// </summary>
        [Fact]
        public void VerifyConditionEvaluatorResetStateOnFailure()
        {
            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, FileSystems.Default);
            string condition = " '$(TargetOSFamily)' >= '3' ";

            // Give an incorrect value for the property "TargetOSFamily", and then the evaluation should throw an exception.
            propertyBag.Set(ProjectPropertyInstance.Create("TargetOSFamily", "*"));
            try
            {
                ConditionEvaluator.EvaluateCondition(
                    condition,
                    ParserOptions.AllowAll,
                    expander,
                    ExpanderOptions.ExpandProperties,
                    Directory.GetCurrentDirectory(),
                    MockElementLocation.Instance,
                    FileSystems.Default,
                    new TestLoggingContext(null!, new BuildEventContext(1, 2, 3, 4)));
                Assert.Fail("Expect exception due to the value of property \"TargetOSFamily\" is not a number.");
            }
            catch (InvalidProjectFileException e)
            {
                Console.WriteLine("Expect exception: " + e.Message);
            }

            // Correct the property "TargetOSFamily", and then the evaluation should succeed.
            propertyBag.Set(ProjectPropertyInstance.Create("TargetOSFamily", "3"));
            Assert.True(ConditionEvaluator.EvaluateCondition(
                condition,
                ParserOptions.AllowAll,
                expander,
                ExpanderOptions.ExpandProperties,
                Directory.GetCurrentDirectory(),
                MockElementLocation.Instance,
                FileSystems.Default,
                new TestLoggingContext(null!, new BuildEventContext(1, 2, 3, 4))));
        }

        /// <summary>
        /// Test regression reported at https://github.com/dotnet/msbuild/issues/2228
        /// </summary>
        [Fact]
        public void ThrownInvalidProjectExceptionProperlyHandled()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>

                                <Import Project=""import.proj"" />

                            </Project>");

            string importContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>

                                <AnInvalidTopLevelElement />

                              </Project>");

            string projectDirectory = Path.Combine(Path.GetTempPath(), "ThrownInvalidProjectExceptionProperlyHandled");

            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }

                Directory.CreateDirectory(projectDirectory);

                string primaryProject = Path.Combine(projectDirectory, "project.proj");
                string import = Path.Combine(projectDirectory, "import.proj");

                File.WriteAllText(primaryProject, projectContents);
                File.WriteAllText(import, importContents);

                InvalidProjectFileException ex = Assert.Throws<InvalidProjectFileException>(() =>
                    {
                        Project unused = new Project(primaryProject, null, null);
                    })
                ;

                Assert.Contains("<AnInvalidTopLevelElement>", ex.Message);
                Assert.Equal("MSB4067", ex.ErrorCode);
                Assert.Equal(4, ex.LineNumber);
                Assert.Equal(33, ex.ColumnNumber);
            }
            finally
            {
                if (Directory.Exists(projectDirectory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(projectDirectory, true /* recursive delete */);
                }
            }
        }

        /// <summary>
        /// Tests that an import, target, or task with a condition that contains an error but is short-circuited does not fail the build.  This can happen when you have a condition like:
        /// 'true' == 'false' AND '$([MSBuild]::GetDirectoryNameOfFileAbove($(NonExistentProperty), init.props))' != ''
        ///
        /// The first condition is false so the second condition is not evaluated.  But in some cases we double evaluate the condition to log it.  The second evaluation will fail because it evaluates the whole string.
        ///
        /// https://github.com/dotnet/msbuild/issues/2259
        /// </summary>
        [Theory]
        [InlineData("<Target Name=\"Build\" /><Import Project=\"$(NonExistentProperty)\" Condition=\"\'true\' == \'false\' And \'$([MSBuild]::GetDirectoryNameOfFileAbove($(NonExistentProperty), init.props))\' != \'\'\" />")]
        [InlineData("<Target Name=\"Build\" Condition=\"\'true\' == \'false\' And \'$([MSBuild]::GetDirectoryNameOfFileAbove($(NonExistentProperty), init.props))\' != \'\'\" />")]
        [InlineData("<Target Name=\"Build\"><Message Text=\"Build executed\" Condition=\"\'true\' == \'false\' And \'$([MSBuild]::GetDirectoryNameOfFileAbove($(NonExistentProperty), init.props))\' != \'\'\" /></Target>")]
        public void ConditionWithShortCircuitAndErrorDoesNotFailBuild(string projectInnerXml)
        {
            string content = ObjectModelHelpers.CleanupFileContents($@"
                             <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns='msbuildnamespace'>
                                {projectInnerXml}
                             </Project>");

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDLOGIMPORTS", "1");
                using ProjectFromString projectFromString = new(content);
                Project project = projectFromString.Project;

                MockLogger logger = new MockLogger();

                bool result = project.Build(logger);

                Assert.True(result);
            }
        }

        [Theory]
        [InlineData("$(Hello)", 0, 8, "Hello")]
        [InlineData("$(Hello)|$(World)", 9, 8, "World")]
        [InlineData("$(He()o)", 0, 8, null)]
        [InlineData("$)Hello(", 0, 8, null)]
        [InlineData("$(Helloo", 0, 8, null)]
        [InlineData("$Heello)", 0, 8, null)]
        [InlineData("$(He$$o)", 0, 8, null)]
        [InlineData(" $(Helo)", 0, 8, null)]
        [InlineData("$(Helo) ", 0, 8, null)]
        [InlineData("$()", 0, 3, "")]
        [InlineData("$( Hello )", 0, 10, " Hello ")]
        [InlineData("$(He-ll-o)", 0, 10, "He-ll-o")]
        [InlineData("$(He ll o)", 0, 10, "He ll o")]
        [InlineData("aaa$(Hello)", 3, 8, "Hello")]
        [InlineData("aaa$(Hello)bbb", 3, 8, "Hello")]
        public void TryGetSingleProperty(string input, int start, int length, string expected)
        {
            bool result = ConditionEvaluator.TryGetSingleProperty(input.AsSpan(), start, length, out ReadOnlySpan<char> actual);

            if (expected is null)
            {
                Assert.False(result);
                Assert.True(actual.IsEmpty);
            }
            else
            {
                Assert.True(result);
                Assert.Equal(expected, actual.ToString());
            }
        }

        [Fact]
        public void VerifyMSBuildLastModifiedProjectForImport()
        {
            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                var project1 = testEnvironment.CreateTestProjectWithFiles("<Project />");
                var project2 = testEnvironment.CreateTestProjectWithFiles("<Project />");

                var primaryProject = testEnvironment.CreateTestProjectWithFiles($@"<Project>
<Import Project=""{project1.ProjectFile}"" />
<Import Project=""{project2.ProjectFile}"" />
</Project>");

                // Project1 and primary project last modified an hour ago, project2 is the newest
                File.SetLastWriteTime(project1.ProjectFile, DateTime.Now.AddHours(-1));
                File.SetLastWriteTime(project2.ProjectFile, DateTime.Now);
                File.SetLastWriteTime(primaryProject.ProjectFile, DateTime.Now.AddHours(-1));

                Project project = new Project(primaryProject.ProjectFile, null, null);

                string propertyValue = project.GetPropertyValue(Constants.MSBuildAllProjectsPropertyName);

                propertyValue.ShouldStartWith(project2.ProjectFile);

                propertyValue.ShouldNotContain(primaryProject.ProjectFile, Case.Insensitive);
                propertyValue.ShouldNotContain(project1.ProjectFile, Case.Insensitive);
            }
        }

        [Fact]
        public void VerifyMSBuildLastModifiedProjectIsProject()
        {
            using (TestEnvironment testEnvironment = TestEnvironment.Create())
            {
                var project1 = testEnvironment.CreateTestProjectWithFiles("<Project />");
                var project2 = testEnvironment.CreateTestProjectWithFiles("<Project />");

                var primaryProject = testEnvironment.CreateTestProjectWithFiles($@"<Project>
<Import Project=""{project1.ProjectFile}"" />
<Import Project=""{project2.ProjectFile}"" />
</Project>");

                // Project1 and project2 last modified an hour ago, primaryProject is the newest
                File.SetLastWriteTime(project1.ProjectFile, DateTime.Now.AddHours(-1));
                File.SetLastWriteTime(project2.ProjectFile, DateTime.Now.AddHours(-1));
                File.SetLastWriteTime(primaryProject.ProjectFile, DateTime.Now);


                Project project = new Project(primaryProject.ProjectFile, null, null);

                project.GetPropertyValue(Constants.MSBuildAllProjectsPropertyName).ShouldStartWith(primaryProject.ProjectFile);
            }
        }

        [Fact]
        public void VerifyMSBuildLogsAMessageWhenLocalPropertyCannotOverrideValueOfGlobalProperty()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                             <Project>
                               <PropertyGroup>
                                 <Foo>Bar</Foo>
                               </PropertyGroup>

                               <Target Name='t'>
                                 <Message Text='[$(Foo)]' />
                               </Target>
                             </Project>");
            IDictionary<string, string> globalProperties =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Foo", "Baz" }
                };

            MockLogger logger = new MockLogger();

            using var collection = new ProjectCollection(
                        globalProperties, new List<ILogger> { logger }, ToolsetDefinitionLocations.Default);
            using ProjectFromString projectFromString = new(content, globalProperties, null, collection);
            Project project = projectFromString.Project;

            project.Build(logger);
            logger.AssertLogContains(
                ResourceUtilities.FormatResourceStringStripCodeAndKeyword("OM_GlobalProperty", "Foo"));
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingDefault()
        {
            // Having just environment variables defined should default to nothing being logged except one environment variable read.
            VerifyPropertyTrackingLoggingScenario(
                null,
                 (logger, _) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldHaveSingleItem()
                        .EnvironmentVariableName
                        .ShouldBe("DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .Count()
                        .ShouldBe(2);

                    logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ShouldBeEmpty();
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingPropertyReassignment()
        {
            VerifyPropertyTrackingLoggingScenario(
                "1",
                (logger, _) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldHaveSingleItem()
                        .EnvironmentVariableName
                        .ShouldBe("DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .ShouldContain(r => r.PropertyName == "Prop2" && r.PreviousValue == "Value1" && r.NewValue == "Value2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ShouldBeEmpty();
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingNone()
        {
            this.VerifyPropertyTrackingLoggingScenario(
                "0",
                 (logger, _) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldHaveSingleItem()
                        .EnvironmentVariableName
                        .ShouldBe("DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ShouldBeEmpty();

                    logger
                       .AllBuildEvents
                       .OfType<PropertyReassignmentEventArgs>()
                       .Count()
                       .ShouldBe(2);
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingPropertyInitialValue()
        {
            this.VerifyPropertyTrackingLoggingScenario(
                "2",
                (logger, projectPath) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldHaveSingleItem()
                        .EnvironmentVariableName
                        .ShouldBe("DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .ShouldBeEmpty();

                    IDictionary<string, PropertyInitialValueSetEventArgs> propertyInitialValueMap = logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ToDictionary(piv => piv.PropertyName);

                    // Verify logging of property initial values.
                    propertyInitialValueMap.ShouldContainKey("Prop");
                    propertyInitialValueMap["Prop"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["Prop"].PropertyValue.ShouldBe(string.Empty);

                    propertyInitialValueMap.ShouldContainKey("EnvVar");
                    propertyInitialValueMap["EnvVar"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["EnvVar"].PropertyValue.ShouldBe("It's also Defined!");

                    propertyInitialValueMap.ShouldContainKey("DEFINED_ENVIRONMENT_VARIABLE");
                    propertyInitialValueMap["DEFINED_ENVIRONMENT_VARIABLE"].PropertySource.ShouldBe("EnvironmentVariable");
                    propertyInitialValueMap["DEFINED_ENVIRONMENT_VARIABLE"].PropertyValue.ShouldBe("It's Defined!");

                    propertyInitialValueMap.ShouldContainKey("NotEnvVarRead");
                    propertyInitialValueMap["NotEnvVarRead"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["NotEnvVarRead"].PropertyValue.ShouldBe("Overwritten!");

                    propertyInitialValueMap.ShouldContainKey("Prop2");
                    propertyInitialValueMap["Prop2"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["Prop2"].PropertyValue.ShouldBe("Value1");
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingEnvironmentVariableRead()
        {
            this.VerifyPropertyTrackingLoggingScenario(
                "4",
                (logger, _) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldContain(ev => ev.EnvironmentVariableName == "DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldNotContain(ev => ev.EnvironmentVariableName == "DEFINED_ENVIRONMENT_VARIABLE");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ShouldBeEmpty();
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingUninitializedPropertyRead()
        {
            this.VerifyPropertyTrackingLoggingScenario(
                "8",
                (logger, _) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldContain(p => p.PropertyName == "DOES_NOT_EXIST");

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldHaveSingleItem()
                        .EnvironmentVariableName
                        .ShouldBe("DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .ShouldBeEmpty();

                    logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ShouldBeEmpty();
                });
        }

        [Fact]
        public void VerifyPropertyTrackingLoggingAll()
        {
            this.VerifyPropertyTrackingLoggingScenario(
                "15",
                (logger, projectPath) =>
                {
                    logger
                        .AllBuildEvents
                        .OfType<UninitializedPropertyReadEventArgs>()
                        .ShouldContain(p => p.PropertyName == "DOES_NOT_EXIST");

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldContain(ev => ev.EnvironmentVariableName == "DEFINED_ENVIRONMENT_VARIABLE2");

                    logger
                        .AllBuildEvents
                        .OfType<EnvironmentVariableReadEventArgs>()
                        .ShouldNotContain(ev => ev.EnvironmentVariableName == "DEFINED_ENVIRONMENT_VARIABLE");

                    logger
                        .AllBuildEvents
                        .OfType<PropertyReassignmentEventArgs>()
                        .ShouldContain(r => r.PropertyName == "Prop2" && r.PreviousValue == "Value1" && r.NewValue == "Value2");

                    IDictionary<string, PropertyInitialValueSetEventArgs> propertyInitialValueMap = logger
                        .AllBuildEvents
                        .OfType<PropertyInitialValueSetEventArgs>()
                        .ToDictionary(piv => piv.PropertyName);

                    // Verify logging of property initial values.
                    propertyInitialValueMap.ShouldContainKey("Prop");
                    propertyInitialValueMap["Prop"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["Prop"].PropertyValue.ShouldBe(string.Empty);

                    propertyInitialValueMap.ShouldContainKey("EnvVar");
                    propertyInitialValueMap["EnvVar"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["EnvVar"].PropertyValue.ShouldBe("It's also Defined!");

                    propertyInitialValueMap.ShouldContainKey("DEFINED_ENVIRONMENT_VARIABLE");
                    propertyInitialValueMap["DEFINED_ENVIRONMENT_VARIABLE"].PropertySource.ShouldBe("EnvironmentVariable");
                    propertyInitialValueMap["DEFINED_ENVIRONMENT_VARIABLE"].PropertyValue.ShouldBe("It's Defined!");

                    propertyInitialValueMap.ShouldContainKey("NotEnvVarRead");
                    propertyInitialValueMap["NotEnvVarRead"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["NotEnvVarRead"].PropertyValue.ShouldBe("Overwritten!");

                    propertyInitialValueMap.ShouldContainKey("Prop2");
                    propertyInitialValueMap["Prop2"].File.ShouldBe(projectPath);
                    propertyInitialValueMap["Prop2"].PropertyValue.ShouldBe("Value1");
                });
        }

        [Fact]
        public void VerifyGetTypeEvaluationBlocked()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"
                             <Project>
                               <PropertyGroup>
                                 <TestProp>$(MSBuildRuntimeType.GetType())</TestProp>
                               </PropertyGroup>
                             </Project>");

            using ProjectCollection fakeProjectCollection =
                GetProjectCollectionWithFakeToolset(null /* no global properties */);

            Should.Throw<InvalidProjectFileException>(() =>
                new Project(XmlReader.Create(new StringReader(projectContents)), null, "Fake", fakeProjectCollection));
        }

        private void VerifyPropertyTrackingLoggingScenario(string envVarValue, Action<MockLogger, string> loggerEvaluatorAction)
        {
            // The default is that only reassignments are logged.

            string testTargets = ObjectModelHelpers.CleanupFileContents(@"
                                <Project>
                                     <PropertyGroup>
                                         <Prop>$(DOES_NOT_EXIST)</Prop>
                                         <EnvVar>$(DEFINED_ENVIRONMENT_VARIABLE2)</EnvVar>
                                         <DEFINED_ENVIRONMENT_VARIABLE>Overwritten!</DEFINED_ENVIRONMENT_VARIABLE>
                                         <NotEnvVarRead>$(DEFINED_ENVIRONMENT_VARIABLE)</NotEnvVarRead>
                                         <Prop2>Value1</Prop2>
                                         <Prop2>Value2</Prop2>
                                     </PropertyGroup>
                                     <Target Name='Build' />
                                </Project>");

            using (TestEnvironment env = TestEnvironment.Create())
            {
                if (!string.IsNullOrWhiteSpace(envVarValue))
                {
                    env.SetEnvironmentVariable("MsBuildLogPropertyTracking", envVarValue);
                }

                env.SetEnvironmentVariable("DEFINED_ENVIRONMENT_VARIABLE", "It's Defined!");
                env.SetEnvironmentVariable("DEFINED_ENVIRONMENT_VARIABLE2", "It's also Defined!");

                var tempPath = env.CreateFile(Guid.NewGuid().ToString(), testTargets);

                BuildParameters.WarnOnUninitializedProperty = true;

                MockLogger logger = new MockLogger();
                logger.Verbosity = LoggerVerbosity.Diagnostic;
                using ProjectCollection pc = new ProjectCollection();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(tempPath.Path);

                project.Build().ShouldBeTrue();

                loggerEvaluatorAction?.Invoke(logger, tempPath.Path);
            }
        }

        /// <summary>
        /// Log when a property is being assigned a new value.
        /// </summary>
        [Fact]
        public void VerifyLogPropertyReassignment()
        {
            string propertyName = "Prop";
            string propertyOldValue = "OldValue";
            string propertyNewValue = "NewValue";
            string testtargets = ObjectModelHelpers.CleanupFileContents(@$"
                                <Project xmlns='msbuildnamespace'>
                                     <PropertyGroup>
                                         <{propertyName}>{propertyOldValue}</{propertyName}>
                                         <{propertyName}>{propertyNewValue}</{propertyName}>
                                     </PropertyGroup>
                                  <Target Name=""Test""/>
                                </Project>");

            string tempPath = Path.GetTempPath();
            string targetDirectory = Path.Combine(tempPath, "LogPropertyAssignments");
            string testTargetPath = Path.Combine(targetDirectory, "test.proj");

            using (TestEnvironment env = TestEnvironment.Create())
            {
                env.CreateFolder(targetDirectory);
                env.CreateFile(testTargetPath, testtargets);

                MockLogger logger = new()
                {
                    Verbosity = LoggerVerbosity.Diagnostic,
                };
                using ProjectCollection pc = new();
                pc.RegisterLogger(logger);
                Project project = pc.LoadProject(testTargetPath);

                bool result = project.Build();
                result.ShouldBeTrue();
                logger.BuildMessageEvents
                      .OfType<PropertyReassignmentEventArgs>()
                      .ShouldContain(r => r.PropertyName == propertyName
                      && r.PreviousValue == propertyOldValue
                      && r.NewValue == propertyNewValue
                      && r.Message.StartsWith($"{ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                              "PropertyReassignment", propertyName, propertyNewValue, propertyOldValue, string.Empty)}"));
            }
        }

#if FEATURE_HTTP_LISTENER
        /// <summary>
        /// HTTP server code running on a separate thread that expects a connection request
        /// The test "VerifyDTDProcessingIsDisabled" creates a project with a url reference to this server from a DTD tag
        /// If a connection request is received, this thread will terminate, if not, the server will remain alive until
        /// "VerifyDTDProcessingIsDisabled" returns.
        /// </summary>
        private void HttpServerThread()
        {
            try
            {
                using HttpListener listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:51111/");
                listener.Start();

                HttpListenerContext context = listener.GetContext();

                // if reached this point it means the server answered a request triggered during DTD processing
                listener.Stop();
            }
            catch (Exception e)
            {
                // don't crash the test process; save the exception and check for it in the test
                _httpListenerThreadException = e;
            }
        }
#endif

        /// <summary>
        /// Creates a standard ProjectCollection and adds a fake toolset with the following contents to it:
        ///
        /// ToolsVersion = Fake
        /// Base Properties:
        /// a = a1
        /// b = b1
        ///
        /// SubToolset "11.0":
        /// b = b2
        /// c = c2
        ///
        /// SubToolset "FakeSubToolset":
        /// a = a3
        /// c = c3
        /// </summary>
        private ProjectCollection GetProjectCollectionWithFakeToolset(IDictionary<string, string> globalProperties)
        {
            ProjectCollection projectCollection = new ProjectCollection(globalProperties);

            IDictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            properties.Add("a", "a1");
            properties.Add("b", "b1");

            Dictionary<string, SubToolset> subToolsets = new Dictionary<string, SubToolset>(StringComparer.OrdinalIgnoreCase);

            // SubToolset 11.0 properties
            PropertyDictionary<ProjectPropertyInstance> subToolset11Properties = new PropertyDictionary<ProjectPropertyInstance>();
            subToolset11Properties.Set(ProjectPropertyInstance.Create("b", "b2"));
            subToolset11Properties.Set(ProjectPropertyInstance.Create("c", "c2"));

            // FakeSubToolset properties
            PropertyDictionary<ProjectPropertyInstance> fakeSubToolsetProperties = new PropertyDictionary<ProjectPropertyInstance>();
            fakeSubToolsetProperties.Set(ProjectPropertyInstance.Create("a", "a3"));
            fakeSubToolsetProperties.Set(ProjectPropertyInstance.Create("c", "c3"));

            subToolsets.Add("FakeSubToolset", new SubToolset("FakeSubToolset", fakeSubToolsetProperties));
            subToolsets.Add("11.0", new SubToolset("11.0", subToolset11Properties));

            Toolset parentToolset = projectCollection.GetToolset(ObjectModelHelpers.MSBuildDefaultToolsVersion);

            Toolset fakeToolset = new Toolset("Fake", parentToolset.ToolsPath, properties, projectCollection, subToolsets, parentToolset.OverrideTasksPath);

            projectCollection.AddToolset(fakeToolset);

            return projectCollection;
        }

        /// <summary>
        /// To the target provided add messages to dump all the MSBuildThisFileXXXX properties.
        /// </summary>
        private void AddPropertyDumpTasks(string prefix, ProjectTargetElement target)
        {
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFileDirectory=$(MSBuildThisFileDirectory)");
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFileDirectoryNoRoot=$(MSBuildThisFileDirectoryNoRoot)");
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFile=$(MSBuildThisFile)");
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFileExtension=$(MSBuildThisFileExtension)");
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFileFullPath=$(MSBuildThisFileFullPath)");
            target.AddTask("Message").SetParameter("Text", prefix + ": MSBuildThisFileName=$(MSBuildThisFileName)");
        }

        /// <summary>
        /// Creates a file on disk that logs [$(MSBuildThisFile)]
        /// </summary>
        private void CreateTargetsFileWithMessage(string path, string targetName, string dependsOn)
        {
            ProjectRootElement import = ProjectRootElement.Create(path);
            ProjectTargetElement target = import.AddTarget(targetName);
            target.AddTask("Message").SetParameter("Text", "[$(MSBuildThisFile)]");
            target.DependsOnTargets = dependsOn;
            import.Save();
        }

        /// <summary>
        /// Verifies that the import path.
        /// </summary>
        private void VerifyImportTargetRelativePath(string directory, string directory2, string[] imports)
        {
            string file1 = null;
            string file2 = null;
            string file3 = null;
            string file4 = null;

            try
            {
                if (File.Exists(directory))
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(directory);
                }

                string file0 = Path.Combine(directory, "my.proj");
                file1 = Path.Combine(directory, "1.targets");
                file2 = Path.Combine(directory2, "2.targets");
                file3 = Path.Combine(directory2, "3.cpp.targets");
                file4 = Path.Combine(directory2, "4.nottargets");

                ProjectRootElement projectXml = ProjectRootElement.Create(file0);
                projectXml.DefaultTargets = "t1";
                foreach (string import in imports)
                {
                    projectXml.AddImport(import);
                }

                CreateTargetsFileWithMessage(file1, "t1", "t3");
                CreateTargetsFileWithMessage(file2, "t2", "");
                CreateTargetsFileWithMessage(file3, "t3", "t2");
                CreateTargetsFileWithMessage(file4, "t4", "t3");

                Project project = new Project(projectXml);

                MockLogger logger = new MockLogger();
                bool result = project.Build(logger);

                Assert.True(result);

                logger.AssertLogContains(new string[] { "[2.targets]", "[3.cpp.targets]", "[1.targets]" });
                logger.AssertLogDoesntContain("4.nottargets");

                logger.ClearLog();

                result = project.Build("t4");

                Assert.False(result);
            }
            finally
            {
                File.Delete(file1);
                File.Delete(file2);
                File.Delete(file3);
                File.Delete(file4);
                FileUtilities.DeleteWithoutTrailingBackslash(directory, true);
            }
        }
    }
}
