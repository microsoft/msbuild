// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Shared;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System;
using Microsoft.Build.Unittest;
using Xunit;
using Microsoft.Build.Framework;
using System.Linq;

namespace Microsoft.Build.UnitTests.Preprocessor
{
    /// <summary>
    /// Tests mainly for project preprocessing
    /// </summary>
    public class Preprocessor_Tests : IDisposable
    {
        private static string CurrentDirectoryXmlCommentFriendly => Directory.GetCurrentDirectory().Replace("--", "__");

        public Preprocessor_Tests()
        {
            Setup();
        }

        /// <summary>
        /// Clear out the cache
        /// </summary>
        private void Setup()
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            GC.Collect();
        }

        /// <summary>
        /// Clear out the cache
        /// </summary>
        public void Dispose()
        {
            Setup();
        }

        /// <summary>
        /// Basic project
        /// </summary>
        [Fact]
        public void Single()
        {
            Project project = new Project();
            project.SetProperty("p", "v1");
            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// InitialTargets are concatenated, outermost to innermost
        /// </summary>
        [Fact]
        public void InitialTargetsOuterAndInner()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.InitialTargets = "i1";
            xml1.AddImport("p2");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.InitialTargets = "i2";

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"" InitialTargets=""i1;i2"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// InitialTargets are concatenated, outermost to innermost
        /// </summary>
        [Fact]
        public void InitialTargetsInnerOnly()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddImport("p2");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.InitialTargets = "i2";

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"" InitialTargets=""i2"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// InitialTargets are concatenated, outermost to innermost
        /// </summary>
        [Fact]
        public void InitialTargetsOuterOnly()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.InitialTargets = "i1";
            xml1.AddImport("p2");
            ProjectRootElement.Create("p2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"" InitialTargets=""i1"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic empty project importing another
        /// </summary>
        [Fact]
        public void TwoFirstEmpty()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddImport("p2");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.AddProperty("p", "v2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// False import should not be followed
        /// </summary>
        [Fact]
        public void FalseImport()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddProperty("p", "v1");
            xml1.AddImport("p2").Condition = "false";
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.AddProperty("p", "v2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--<Import Project=""p2"" Condition=""false"" />-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic project importing another empty one
        /// </summary>
        [Fact]
        public void TwoSecondEmpty()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddProperty("p", "v");
            xml1.AddImport("p2");
            ProjectRootElement.Create("p2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic project importing another
        /// </summary>
        [Fact]
        public void TwoWithContent()
        {
            string one = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v0</p>
  </PropertyGroup>
  <Import Project=""p2""/>
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
</Project>");

            string two = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
</Project>");
            ProjectRootElement twoXml = ProjectRootElement.Create(XmlReader.Create(new StringReader(two)));
            twoXml.FullPath = "p2";

            Project project;
            using (StringReader sr = new StringReader(one))
            {
                using (XmlReader xr = XmlTextReader.Create(sr))
                {
                    project = new Project(xr);
                }
            }

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v0</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic project importing another one via an ImportGroup
        /// </summary>
        [Fact]
        public void ImportGroup()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddProperty("p", "v1");
            xml1.AddImportGroup().AddImport("p2");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.AddProperty("p", "v2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--<ImportGroup>-->
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
  <!--</ImportGroup>-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic project importing another one via an ImportGroup with two imports inside it, and a condition on it
        /// </summary>
        [Fact]
        public void ImportGroupDoubleChildPlusCondition()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddProperty("p", "v1");
            ProjectImportGroupElement group = xml1.AddImportGroup();
            group.Condition = "true";
            group.AddImport("p2");
            group.AddImport("p3");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.AddProperty("p", "v2");
            ProjectRootElement xml3 = ProjectRootElement.Create("p3");
            xml3.AddProperty("p", "v3");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--<ImportGroup Condition=""true"">-->
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""p3"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p3
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v3</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
  <!--</ImportGroup>-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// First DefaultTargets encountered is used
        /// </summary>
        [Fact]
        public void DefaultTargetsOuterAndInner()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddImport("p2");
            xml1.AddImport("p3");
            xml1.DefaultTargets = "d1";
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.DefaultTargets = "d2";
            ProjectRootElement xml3 = ProjectRootElement.Create("p3");
            xml3.DefaultTargets = "d3";

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
     @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"" DefaultTargets=""d1"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""p3"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p3
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// First DefaultTargets encountered is used
        /// </summary>
        [Fact]
        public void DefaultTargetsInnerOnly()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddImport("p2");
            xml1.AddImport("p3");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.DefaultTargets = "d2";
            ProjectRootElement xml3 = ProjectRootElement.Create("p3");
            xml3.DefaultTargets = "d3";

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
     @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"" DefaultTargets=""d2"">
  <!--
============================================================================================================================================
  <Import Project=""p2"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p2
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""p3"">

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p3
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Basic project importing another one via an ImportGroup, but the ImportGroup condition is false
        /// </summary>
        [Fact]
        public void ImportGroupFalseCondition()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            xml1.AddProperty("p", "v1");
            xml1.AddImportGroup().AddImport("p2");
            xml1.LastChild.Condition = "false";
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            xml2.AddProperty("p", "v2");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--<ImportGroup Condition=""false"">-->
  <!--<Import Project=""p2"" />-->
  <!--</ImportGroup>-->
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Import has a wildcard expression
        /// </summary>
        [Fact]
        public void ImportWildcard()
        {
            string directory = null;
            ProjectRootElement xml0, xml1 = null, xml2 = null, xml3 = null;

            try
            {
                directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(directory);

                xml0 = ProjectRootElement.Create("p1");
                xml0.AddImport(directory + Path.DirectorySeparatorChar + "*.targets");

                xml1 = ProjectRootElement.Create(directory + Path.DirectorySeparatorChar + "1.targets");
                xml1.AddProperty("p", "v1");
                xml1.Save();

                xml2 = ProjectRootElement.Create(directory + Path.DirectorySeparatorChar + "2.targets");
                xml2.AddProperty("p", "v2");
                xml2.Save();

                xml3 = ProjectRootElement.Create(directory + Path.DirectorySeparatorChar + "3.xxxxxx");
                xml3.AddProperty("p", "v3");
                xml3.Save();

                Project project = new Project(xml0);

                StringWriter writer = new StringWriter();

                project.SaveLogicalProject(writer);

                string directoryXmlCommentFriendly = directory.Replace("--", "__");

                string expected = ObjectModelHelpers.CleanupFileContents(
        @"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <!--
============================================================================================================================================
  <Import Project=""" + Path.Combine(directoryXmlCommentFriendly, "*.targets") + @""">

" + Path.Combine(directoryXmlCommentFriendly, "1.targets") + @"
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""" + Path.Combine(directoryXmlCommentFriendly, "*.targets") + @""">

" + Path.Combine(directoryXmlCommentFriendly, "2.targets") + @"
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v2</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

" + CurrentDirectoryXmlCommentFriendly + Path.DirectorySeparatorChar + @"p1
============================================================================================================================================
-->
</Project>");

                Helpers.VerifyAssertLineByLine(expected, writer.ToString());
            }
            finally
            {
                File.Delete(xml1.FullPath);
                File.Delete(xml2.FullPath);
                File.Delete(xml3.FullPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// CDATA node type cloned correctly
        /// </summary>
        [Fact]
        public void CData()
        {
            Project project = new Project();
            project.SetProperty("p", "<![CDATA[<sender>John Smith</sender>]]>");
            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <PropertyGroup>
    <p><![CDATA[<sender>John Smith</sender>]]></p>
  </PropertyGroup>
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        /// <summary>
        /// Metadata named "Project" should not confuse it..
        /// </summary>
        [Fact]
        public void ProjectMetadata()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
<Project DefaultTargets='Build' ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'>
  <ItemGroup>
    <ProjectReference Include='..\CLREXE\CLREXE.vcxproj'>
      <Project>{3699f81b-2d03-46c5-abd7-e88a4c946f28}</Project>
    </ProjectReference>
  </ItemGroup>
</Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));
            Project project = new Project(xml);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);

            string expected = ObjectModelHelpers.CleanupFileContents(
    @"<?xml version=""1.0"" encoding=""utf-16""?>
<Project DefaultTargets=""Build"" ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
  <ItemGroup>
    <ProjectReference Include=""..\CLREXE\CLREXE.vcxproj"">
      <Project>{3699f81b-2d03-46c5-abd7-e88a4c946f28}</Project>
    </ProjectReference>
  </ItemGroup>
</Project>");

            Helpers.VerifyAssertLineByLine(expected, writer.ToString());
        }

        [Fact]
        public void SdkImportsAreInPreprocessedOutput()
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                string testSdkDirectory = env.CreateFolder().Path;

                using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.FileBasedMockSdkResolver(new Dictionary<string, string>
                {
                    {"MSBuildUnitTestSdk", testSdkDirectory}
                }),
                out var projectOptions);


                string sdkPropsPath = Path.Combine(testSdkDirectory, "Sdk.props");
                string sdkTargetsPath = Path.Combine(testSdkDirectory, "Sdk.targets");

                File.WriteAllText(sdkPropsPath, @"<Project>
    <PropertyGroup>
        <SdkPropsImported>true</SdkPropsImported>
    </PropertyGroup>
</Project>");
                File.WriteAllText(sdkTargetsPath, @"<Project>
    <PropertyGroup>
        <SdkTargetsImported>true</SdkTargetsImported>
    </PropertyGroup>
</Project>");


                string content = @"<Project Sdk='MSBuildUnitTestSdk'>
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
</Project>";

                Project project = Project.FromProjectRootElement(
                    ProjectRootElement.Create(XmlReader.Create(new StringReader(content))),
                    projectOptions);

                StringWriter writer = new StringWriter();

                project.SaveLogicalProject(writer);

                string expected = ObjectModelHelpers.CleanupFileContents(
                    $@"<?xml version=""1.0"" encoding=""utf-16""?>
<Project>
  <!--
============================================================================================================================================
  <Import Project=""Sdk.props"" Sdk=""MSBuildUnitTestSdk"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk"".

{sdkPropsPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkPropsImported>true</SdkPropsImported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project=""Sdk.targets"" Sdk=""MSBuildUnitTestSdk"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk"".

{sdkTargetsPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkTargetsImported>true</SdkTargetsImported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
</Project>");
                Helpers.VerifyAssertLineByLine(expected, writer.ToString());
            }
        }

        [Fact]
        public void SdkResolverItemsAndPropertiesAreInPreprocessedOutput()
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                string testDirectory = env.CreateFolder().Path;

                var propertiesToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    {"PropertyFromSdkResolver", "ValueFromSdkResolver" }
                };

                var itemsToAdd = new Dictionary<string, SdkResultItem>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ItemNameFromSdkResolver", new SdkResultItem( "ItemValueFromSdkResolver",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "MetadataName", "MetadataValue" }
                        })
                    }
                };

                using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(
                    new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                        new [] { testDirectory},
                        version: null,
                        propertiesToAdd,
                        itemsToAdd,
                        warnings: null
                        )),
                    out var projectOptions);

                string content = @"<Project>
<Import Project='Import.props' Sdk='TestPropsAndItemsFromResolverSdk' />
<PropertyGroup>
<p>v1</p>
</PropertyGroup>
</Project>";

                string importedPropsPath = Path.Combine(testDirectory, "Import.props");
                File.WriteAllText(importedPropsPath, @"<Project>
    <PropertyGroup>
        <SdkPropsImported>true</SdkPropsImported>
    </PropertyGroup>
</Project>");

                string projectPath = Path.Combine(testDirectory, "TestProject.csproj");
                File.WriteAllText(projectPath, content);

                var project = Project.FromFile(projectPath, projectOptions);

                StringWriter writer = new StringWriter();

                project.SaveLogicalProject(writer);

                string actual = writer.ToString();

                //  File names for the projects including the properties and items from the SDK resolvers are based on a hash of
                //  the values, so look up the filename here.
                //  Sample filename: projectPath + ".SdkResolver.-171948414.proj"
                var virtualImport = project.Imports.First(i => i.ImportedProject.FullPath.StartsWith(projectPath + ".SdkResolver"));
                string virtualProjectPath = virtualImport.ImportedProject.FullPath;

                string expected = ObjectModelHelpers.CleanupFileContents(
                    $@"<?xml version=""1.0"" encoding=""utf-16""?>
<!--
============================================================================================================================================
{projectPath.Replace("--", "__")}
============================================================================================================================================
-->
<Project>
  <!--
============================================================================================================================================
  <Import Project=""Import.props"" Sdk=""TestPropsAndItemsFromResolverSdk"">

{virtualProjectPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
    <PropertyFromSdkResolver>ValueFromSdkResolver</PropertyFromSdkResolver>
  </PropertyGroup>
  <ItemGroup xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
    <ItemNameFromSdkResolver Include=""ItemValueFromSdkResolver"">
      <MetadataName>MetadataValue</MetadataName>
    </ItemNameFromSdkResolver>
  </ItemGroup>
  <!--
============================================================================================================================================
  </Import>
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""Import.props"" Sdk=""TestPropsAndItemsFromResolverSdk"">

{importedPropsPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkPropsImported>true</SdkPropsImported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

{projectPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
</Project>");
                Helpers.VerifyAssertLineByLine(expected, actual);
            }
        }

        [Fact]
        public void ImportedProjectsSdkImportsAreInPreprocessedOutput()
        {
            using (TestEnvironment env = TestEnvironment.Create())
            {
                string sdk1 = env.CreateFolder().Path;
                string sdk2 = env.CreateFolder().Path;

                var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.FileBasedMockSdkResolver(new Dictionary<string, string>
                {
                    {"MSBuildUnitTestSdk1", sdk1},
                    {"MSBuildUnitTestSdk2", sdk2},
                }),
                out var projectOptions);

                string sdkPropsPath1 = Path.Combine(sdk1, "Sdk.props");
                string sdkTargetsPath1 = Path.Combine(sdk1, "Sdk.targets");

                File.WriteAllText(sdkPropsPath1, @"<Project>
    <PropertyGroup>
        <SdkProps1Imported>true</SdkProps1Imported>
    </PropertyGroup>
</Project>");
                File.WriteAllText(sdkTargetsPath1, @"<Project>
    <PropertyGroup>
        <SdkTargets1Imported>true</SdkTargets1Imported>
    </PropertyGroup>
</Project>");

                string sdkPropsPath2 = Path.Combine(sdk2, "Sdk.props");
                string sdkTargetsPath2 = Path.Combine(sdk2, "Sdk.targets");

                File.WriteAllText(sdkPropsPath2, @"<Project>
    <PropertyGroup>
        <SdkProps2Imported>true</SdkProps2Imported>
    </PropertyGroup>
</Project>");
                File.WriteAllText(sdkTargetsPath2, @"<Project>
    <PropertyGroup>
        <SdkTargets2Imported>true</SdkTargets2Imported>
    </PropertyGroup>
</Project>");


                TransientTestProjectWithFiles import = env.CreateTestProjectWithFiles(@"<Project Sdk='MSBuildUnitTestSdk2'>
    <PropertyGroup>
        <MyImportWasImported>true</MyImportWasImported>
    </PropertyGroup>
</Project>");
                string importPath = Path.GetFullPath(import.ProjectFile);
                string content = $@"<Project Sdk='MSBuildUnitTestSdk1'>
  <Import Project='{importPath}' />
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
</Project>";

                Project project = Project.FromProjectRootElement(
                    ProjectRootElement.Create(XmlReader.Create(new StringReader(content))),
                    projectOptions);

                StringWriter writer = new StringWriter();

                project.SaveLogicalProject(writer);

                string expected = ObjectModelHelpers.CleanupFileContents(
                    $@"<?xml version=""1.0"" encoding=""utf-16""?>
<Project>
  <!--
============================================================================================================================================
  <Import Project=""Sdk.props"" Sdk=""MSBuildUnitTestSdk1"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk1"".

{sdkPropsPath1.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkProps1Imported>true</SdkProps1Imported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""{importPath.Replace("--", "__")}"">

{importPath.Replace("--", "__")}
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project=""Sdk.props"" Sdk=""MSBuildUnitTestSdk2"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk2"".

{sdkPropsPath2.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkProps2Imported>true</SdkProps2Imported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

{importPath.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <MyImportWasImported>true</MyImportWasImported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project=""Sdk.targets"" Sdk=""MSBuildUnitTestSdk2"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk2"".

{sdkTargetsPath2.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkTargets2Imported>true</SdkTargets2Imported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>

{importPath.Replace("--", "__")}
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
  <PropertyGroup>
    <p>v1</p>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project=""Sdk.targets"" Sdk=""MSBuildUnitTestSdk1"">
  This import was added implicitly because the Project element's Sdk attribute specified ""MSBuildUnitTestSdk1"".

{sdkTargetsPath1.Replace("--", "__")}
============================================================================================================================================
-->
  <PropertyGroup>
    <SdkTargets1Imported>true</SdkTargets1Imported>
  </PropertyGroup>
  <!--
============================================================================================================================================
  </Import>


============================================================================================================================================
-->
</Project>");
                Helpers.VerifyAssertLineByLine(expected, writer.ToString());
            }
        }



        /// <summary>
        /// Verifies that the Preprocessor works when the import graph contains unevaluated duplicates.  This can occur if two projects in 
        /// two different folders both import "..\dir.props" or "$(Property)".  Those values will evaluate to different paths at run time
        /// but the preprocessor builds a map of the imports.
        /// </summary>
        [Fact]
        public void DuplicateUnevaluatedImports()
        {
            ProjectRootElement xml1 = ProjectRootElement.Create("p1");
            ProjectRootElement xml2 = ProjectRootElement.Create("p2");
            ProjectRootElement.Create("p3");

            xml1.AddProperty("Import", "p2");
            xml2.AddProperty("Import", "p3");

            // These imports are duplicates but for each project will evaluate to separate projects.  We expect that to NOT break
            // the preprocessor's internal mapping.
            //
            xml1.AddImport("$(Import)");
            xml2.AddImport("$(Import)");

            Project project = new Project(xml1);

            StringWriter writer = new StringWriter();

            project.SaveLogicalProject(writer);
        }
    }
}
