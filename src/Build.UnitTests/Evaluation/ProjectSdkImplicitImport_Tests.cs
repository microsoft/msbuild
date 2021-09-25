// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;
using SdkResolverContext = Microsoft.Build.Framework.SdkResolverContext;
using SdkResult = Microsoft.Build.Framework.SdkResult;
using SdkResultFactory = Microsoft.Build.Framework.SdkResultFactory;
using SdkReferencePropertyExpansionMode = Microsoft.Build.Utilities.EscapeHatches.SdkReferencePropertyExpansionMode;

namespace Microsoft.Build.UnitTests.OM.Construction
{
    /// <summary>
    /// Tests for the ProjectImportElement class when imports are implicit through an Sdk specification.
    /// </summary>
    public class ProjectSdkImplicitImport_Tests : IDisposable
    {
        private const string ProjectTemplateSdkAsAttribute = @"
<Project Sdk=""{0}"">
  {1}
</Project>";

        private const string ProjectTemplateSdkAsAttributeWithVersion = @"
<Project Sdk=""{0}/{2}"">
  {1}
</Project>";

        private const string ProjectTemplateSdkAsElement = @"
<Project>
  <Sdk Name=""{0}"" />
  {1}
</Project>";

        private const string ProjectTemplateSdkAsElementWithVersion = @"
<Project>
  <Sdk Name=""{0}"" Version=""{2}"" MinimumVersion=""{3}""/>
  {1}
</Project>";

        private const string ProjectTemplateSdkAsExplicitImport = @"
<Project>
  <Import Project=""Sdk.props"" Sdk=""{0}"" />
  {1}
  <Import Project=""Sdk.targets"" Sdk=""{0}"" />
</Project>";

        private const string ProjectTemplateSdkAsExplicitImportWithVersion = @"
<Project>
  <Import Project=""Sdk.props"" Sdk=""{0}"" Version=""{2}"" MinimumVersion=""{3}"" />
  {1}
  <Import Project=""Sdk.targets"" Sdk=""{0}"" Version=""{2}"" MinimumVersion=""{3}"" />
</Project>";

        private const string SdkName = "MSBuildUnitTestSdk";
        private TestEnvironment _env;
        private readonly string _testSdkRoot;
        private readonly string _testSdkDirectory;
        private readonly string _sdkPropsPath;
        private readonly string _sdkTargetsPath;
        private string _sdkPropsContent = "<Project><PropertyGroup><InitialImportProperty>Hello</InitialImportProperty></PropertyGroup></Project>";
        private string _sdkTargetsContent = "<Project><PropertyGroup><FinalImportProperty>World</FinalImportProperty></PropertyGroup></Project>";
        private string _projectInnerContents = @"<PropertyGroup><UsedToTestIfImplicitImportsAreInTheCorrectLocation>null</UsedToTestIfImplicitImportsAreInTheCorrectLocation></PropertyGroup>";
        private const string SdkNamePropertyName = "MyTestSdkName";
        private const string SdkNameProperty = "$(" + SdkNamePropertyName + ")";
        private const string SdkVersionPropertyName = "MyTestSdkVersion";
        private const string SdkVersionProperty = "$(" + SdkVersionPropertyName + ")";
        private const string SdkExpectedVersion = "42.42.42-local";

        public ProjectSdkImplicitImport_Tests()
        {
            _env = TestEnvironment.Create();

            _testSdkRoot = _env.CreateFolder().Path;
            _testSdkDirectory = Path.Combine(_testSdkRoot, SdkName, "Sdk");
            _sdkPropsPath = Path.Combine(_testSdkDirectory, "Sdk.props");
            _sdkTargetsPath = Path.Combine(_testSdkDirectory, "Sdk.targets");

            Directory.CreateDirectory(_testSdkDirectory);
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute, false)]
        [InlineData(ProjectTemplateSdkAsElement, true)]
        [InlineData(ProjectTemplateSdkAsExplicitImport, false)]
        public void SdkImportsAreInLogicalProject(string projectFormatString, bool expectImportInLogicalProject)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);
            string projectInnerContents = _projectInnerContents;
            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);

            string content = string.Format(projectFormatString, SdkName, projectInnerContents);

            ProjectRootElement projectRootElement = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));

            var project = new Project(projectRootElement);

            IList<ProjectElement> children = project.GetLogicalProject().ToList();

            // <Sdk> style will have an extra ProjectElment.
            Assert.Equal(expectImportInLogicalProject ? 7 : 6, children.Count);
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute, false)]
        [InlineData(ProjectTemplateSdkAsElement, false)]
        [InlineData(ProjectTemplateSdkAsExplicitImport, true)]
        public void SdkImportsAreInImportList(string projectFormatString, bool expectImportInLogicalProject)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);
            string projectInnerContents = _projectInnerContents;
            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);
            string content = string.Format(projectFormatString, SdkName, projectInnerContents);

            ProjectRootElement projectRootElement = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));

            var project = new Project(projectRootElement);

            // The XML representation of the project should only indicate an import if they are not implicit.
            Assert.Equal(expectImportInLogicalProject ? 2 : 0, projectRootElement.Imports.Count);

            // The project representation should have imports
            Assert.Equal(2, project.Imports.Count);

            ResolvedImport initialResolvedImport = project.Imports[0];
            Assert.Equal(_sdkPropsPath, initialResolvedImport.ImportedProject.FullPath);


            ResolvedImport finalResolvedImport = project.Imports[1];
            Assert.Equal(_sdkTargetsPath, finalResolvedImport.ImportedProject.FullPath);

            VerifyPropertyFromImplicitImport(project, "InitialImportProperty", _sdkPropsPath, "Hello");
            VerifyPropertyFromImplicitImport(project, "FinalImportProperty", _sdkTargetsPath, "World");

            // TODO: Check the location of the import, maybe it should point to the location of the SDK attribute?
        }

        /// <summary>
        /// Verifies that when a user specifies more than one SDK that everything works as expected
        /// </summary>
        [Theory]
        [InlineData(@"
<Project Sdk=""{0};{1};{2}"">
</Project >", false)]
        [InlineData(@"
<Project>
  <Sdk Name=""{0}"" />
  <Sdk Name=""{1}"" />
  <Sdk Name=""{2}"" />
</Project>", false)]
        [InlineData(@"
<Project>
  <Import Project=""Sdk.props"" Sdk=""{0}"" />
  <Import Project=""Sdk.props"" Sdk=""{1}"" />
  <Import Project=""Sdk.props"" Sdk=""{2}"" />

  <Import Project=""Sdk.targets"" Sdk=""{0}"" />
  <Import Project=""Sdk.targets"" Sdk=""{1}"" />
  <Import Project=""Sdk.targets"" Sdk=""{2}"" />
</Project>", true)]
        public void SdkSupportsMultiple(string projectFormatString, bool expectImportInLogicalProject)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            IList<string> sdkNames = new List<string>
            {
                "MSBuild.SDK.One",
                "MSBuild.SDK.Two",
                "MSBuild.SDK.Three",
            };

            foreach (string sdkName in sdkNames)
            {
                string testSdkDirectory = Directory.CreateDirectory(Path.Combine(_testSdkRoot, sdkName, "Sdk")).FullName;

                File.WriteAllText(Path.Combine(testSdkDirectory, "Sdk.props"), $"<Project><PropertyGroup><InitialImportProperty>{sdkName}</InitialImportProperty></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(testSdkDirectory, "Sdk.targets"), $"<Project><PropertyGroup><FinalImportProperty>{sdkName}</FinalImportProperty></PropertyGroup></Project>");
            }
            string content = string.Format(projectFormatString, sdkNames[0], sdkNames[1], sdkNames[2]);

            ProjectRootElement projectRootElement = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));

            Project project = new Project(projectRootElement);

            // The XML representation of the project should indicate there are no imports
            Assert.Equal(expectImportInLogicalProject ? 6 : 0, projectRootElement.Imports.Count);

            // The project representation should have twice as many imports as SDKs
            Assert.Equal(sdkNames.Count * 2, project.Imports.Count);

            // Last imported SDK should set the value
            VerifyPropertyFromImplicitImport(project, "InitialImportProperty", Path.Combine(_testSdkRoot, sdkNames.Last(), "Sdk", "Sdk.props"), sdkNames.Last());
            VerifyPropertyFromImplicitImport(project, "FinalImportProperty", Path.Combine(_testSdkRoot, sdkNames.Last(), "Sdk", "Sdk.targets"), sdkNames.Last());
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute)]
        [InlineData(ProjectTemplateSdkAsElement)]
        [InlineData(ProjectTemplateSdkAsExplicitImport)]
        public void ProjectWithSdkImportsIsCloneable(string projectFormatString)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            string projectInnerContents = @"
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>netcoreapp1.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include=""**\*.cs"" />
    <EmbeddedResource Include=""**\*.resx"" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.NETCore.App"" Version=""1.0.1"" />
  </ItemGroup>";
            File.WriteAllText(_sdkPropsPath, "<Project />");
            File.WriteAllText(_sdkTargetsPath, "<Project />");

            // Based on the new-console-project CLI template (but not matching exactly
            // should not be a deal-breaker).
            string content = string.Format(projectFormatString, SdkName, projectInnerContents);
            ProjectRootElement project = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));

            project.DeepClone();
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute)]
        [InlineData(ProjectTemplateSdkAsElement)]
        [InlineData(ProjectTemplateSdkAsExplicitImport)]
        public void ProjectWithSdkImportsIsRemoveable(string projectFormatString)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            string projectInnerContents = @"
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>netcoreapp1.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include=""**\*.cs"" />
    <EmbeddedResource Include=""**\*.resx"" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.NETCore.App"" Version=""1.0.1"" />
  </ItemGroup>";
            File.WriteAllText(_sdkPropsPath, " <Project />");
            File.WriteAllText(_sdkTargetsPath, "<Project />");

            // Based on the new-console-project CLI template (but not matching exactly
            // should not be a deal-breaker).
            string content = string.Format(projectFormatString, SdkName, projectInnerContents);
            ProjectRootElement project = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));
            ProjectRootElement clone = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));

            clone.DeepCopyFrom(project);

            clone.RemoveAllChildren();
        }

        /// <summary>
        /// Verifies that an error occurs when an SDK name is not in the correct format.
        /// </summary>
        [Fact]
        public void ProjectWithInvalidSdkName()
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            const string invalidSdkName = "SdkWithExtra/Slash/1.0.0";

            InvalidProjectFileException exception = Assert.Throws<InvalidProjectFileException>(() =>
            {
                string content = $@"
                    <Project Sdk=""{invalidSdkName}"">
                        <PropertyGroup>
                            <UsedToTestIfImplicitImportsAreInTheCorrectLocation>null</UsedToTestIfImplicitImportsAreInTheCorrectLocation>
                        </PropertyGroup>
                    </Project>";

                Project project = new Project(ProjectRootElement.Create(XmlReader.Create(new StringReader(content))));
            });
            
            Assert.Equal("MSB4229", exception.ErrorCode);
        }

        /// <summary>
        /// Verifies that an empty SDK attribute works and nothing is imported.
        /// </summary>
        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute, false)]
        [InlineData(ProjectTemplateSdkAsElement, true)]
        [InlineData(ProjectTemplateSdkAsExplicitImport, true)]
        public void ProjectWithEmptySdkName(string projectFormatString, bool throwsOnEvaluate)
        {
            string projectInnerContents =
                _projectInnerContents;

            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            string content = string.Format(projectFormatString, string.Empty, projectInnerContents);
            if (throwsOnEvaluate)
            {
                Assert.Throws<InvalidProjectFileException>(
                    () => new Project(ProjectRootElement.Create(XmlReader.Create(new StringReader(content)))));
            }
            else
            {
                var project = new Project(ProjectRootElement.Create(XmlReader.Create(new StringReader(content))));
                Assert.Empty(project.Imports);
            }
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttribute)]
        [InlineData(ProjectTemplateSdkAsElement)]
        [InlineData(ProjectTemplateSdkAsExplicitImport)]
        public void ProjectResolverContextRefersToBuildingProject(string projectFormatString)
        {
            string projectInnerContents = _projectInnerContents;
            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);

            // Use custom SDK resolution to ensure resolver context is logged.
            var mapping = new Dictionary<string, string> { { SdkName, _testSdkDirectory } };
            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.FileBasedMockSdkResolver(mapping),
                out var projectOptions);

            // Create a normal project (p1) which imports an SDK style project (p2).
            var projectFolder = _env.CreateFolder().Path;

            var p1 = @"<Project> <Import Project=""p2.proj"" /> </Project>";
            var p2 = string.Format(projectFormatString, SdkName, projectInnerContents);

            var p1Path = Path.Combine(projectFolder, "p1.proj");
            var p2Path = Path.Combine(projectFolder, "p2.proj");

            File.WriteAllText(p1Path, p1);
            File.WriteAllText(p2Path, p2);

            var logger = new MockLogger();
            var pc = _env.CreateProjectCollection().Collection;
            pc.RegisterLogger(logger);
            ProjectRootElement projectRootElement = ProjectRootElement.Open(p1Path, pc);

            projectOptions.ProjectCollection = pc;

            var project = Project.FromProjectRootElement(projectRootElement, projectOptions);

            // ProjectFilePath should be logged with the path to p1 and not the path to p2.
            logger.AssertLogContains($"ProjectFilePath = {p1Path}");
            logger.AssertLogDoesntContain($"ProjectFilePath = {p2Path}");
        }

        /// <summary>
        /// Verifies that an empty SDK attribute works and nothing is imported.
        /// </summary>
        [Fact]
        public void ProjectWithEmptySdkNameElementThrows()
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            string content = @"
                    <Project>
                        <Sdk Name="""" />
                        <PropertyGroup>
                            <UsedToTestIfImplicitImportsAreInTheCorrectLocation>null</UsedToTestIfImplicitImportsAreInTheCorrectLocation>
                        </PropertyGroup>
                    </Project>";

            var e =
                Assert.Throws<InvalidProjectFileException>(() => new Project(
                    ProjectRootElement.Create(XmlReader.Create(new StringReader(content)))));

            Assert.Equal("MSB4238", e.ErrorCode);
        }

        /// <summary>
        /// Verifies that an error occurs when one or more SDK names are empty.
        /// </summary>
        [Fact]
        public void ProjectWithEmptySdkNameInValidList()
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);

            const string invalidSdkName = "foo;  ;bar";

            InvalidProjectFileException exception = Assert.Throws<InvalidProjectFileException>(() =>
            {
                string content = $@"
                    <Project Sdk=""{invalidSdkName}"">
                        <PropertyGroup>
                            <UsedToTestIfImplicitImportsAreInTheCorrectLocation>null</UsedToTestIfImplicitImportsAreInTheCorrectLocation>
                        </PropertyGroup>
                    </Project>";

                Project project = new Project(ProjectRootElement.Create(XmlReader.Create(new StringReader(content))));
            });

            Assert.Equal("MSB4229", exception.ErrorCode);
        }

        [Theory]
        // MinimumVersion & Version not supported in SDK attribute at the same time
        [InlineData(ProjectTemplateSdkAsAttributeWithVersion, "1.0.0", null)]
        [InlineData(ProjectTemplateSdkAsAttributeWithVersion, "min=1.0.0", "1.0.0")]

        [InlineData(ProjectTemplateSdkAsElementWithVersion, "1.0.0", "1.0.0")]
        [InlineData(ProjectTemplateSdkAsExplicitImportWithVersion, "1.0.0", "1.0.0")]
        public void SdkImportsSupportVersion(string projectFormatString, string sdkVersion, string minimumSdkVersion)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);
            string projectInnerContents = _projectInnerContents;
            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);

            string content = string.Format(projectFormatString, SdkName, projectInnerContents, sdkVersion, minimumSdkVersion);

            ProjectRootElement projectRootElement = ProjectRootElement.Create(XmlReader.Create(new StringReader(content)));
            var project = new Project(projectRootElement);
            project.Imports.Count.ShouldBe(2);
            var importElement = project.Imports[0].ImportingElement;
            var sdk = GetParsedSdk(importElement);

            if (sdkVersion.StartsWith("min="))
            {
                // Ignore version when min= string is specified
                sdkVersion = null;
            }

            sdk.Name.ShouldBe(SdkName);
            sdk.Version.ShouldBe(sdkVersion);
            sdk.MinimumVersion.ShouldBe(minimumSdkVersion);
        }

        /// <summary>
        /// Verifies that when <see cref="ProjectLoadSettings.IgnoreMissingImports"/> is set that we don't throw an <see cref="InvalidProjectFileException"/> when an SDK cannot be found.
        /// </summary>
        [Fact]
        public void IgnoreMissingImportsSdkNotFoundDoesNotThrow()
        {
            const string projectContents = @"
<Project Sdk=""Does.Not.Exist"">
  <PropertyGroup>
    <Success>true</Success>
  </PropertyGroup>
</Project>
";
            MockLogger logger = new MockLogger();
            ProjectCollection projectCollection = _env.CreateProjectCollection().Collection;
            projectCollection.RegisterLogger(logger);

            ProjectRootElement rootElement = ObjectModelHelpers.CreateInMemoryProjectRootElement(projectContents);

            Project project = new Project(rootElement,
                globalProperties: null,
                toolsVersion: null,
                projectCollection: projectCollection,
                loadSettings: ProjectLoadSettings.IgnoreMissingImports);

            project.GetPropertyValue("Success").ShouldBe("true");
            
            ProjectImportedEventArgs[] events = logger.BuildMessageEvents.OfType<ProjectImportedEventArgs>().ToArray();

            // There are two implicit imports so there should be two logged ProjectImportedEventArgs
            events.Length.ShouldBe(2);

            events[0].Message.ShouldStartWith("MSB4236");
            events[0].ImportIgnored.ShouldBeTrue();
            events[0].ImportedProjectFile.ShouldBeNull();

            events[1].Message.ShouldStartWith("MSB4236");
            events[1].ImportIgnored.ShouldBeTrue();
            events[1].ImportedProjectFile.ShouldBeNull();
        }

        [Theory]
        [InlineData(ProjectTemplateSdkAsAttributeWithVersion, "min=1.0.0", null, null, "1.0.0", typeof(ProjectRootElement))]
        [InlineData(ProjectTemplateSdkAsAttributeWithVersion, "1.0.0", null, "1.0.0", null, typeof(ProjectRootElement))]
        [InlineData(ProjectTemplateSdkAsElementWithVersion, "2.0.0", "1.0.0", "2.0.0", "1.0.0", typeof(ProjectSdkElement))]
        public void ImplicitImportsShouldHaveParsedSdkInfo(
            string projectTemplate,
            string version,
            string minimumVersion,
            string expectedVersion,
            string expectedMinimumVersion,
            Type expectedOriginalElementType)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);
            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);
            string projectContents = string.Format(projectTemplate, SdkName, _projectInnerContents, version, minimumVersion);

            var project = Project.FromXmlReader(XmlReader.Create(new StringReader(projectContents)), new ProjectOptions());

            project.Imports.Count.ShouldBe(2);
            var imports = project.Imports;

            for (var i = 0; i < 2; i++)
            {
                var import = imports[i];
                var importingElement = import.ImportingElement;
                importingElement.Sdk.ShouldBe(SdkName + $"/{version}");
                importingElement.SdkReference.Name.ShouldBe(SdkName);
                importingElement.SdkReference.Version.ShouldBe(expectedVersion);
                importingElement.SdkReference.MinimumVersion.ShouldBe(expectedMinimumVersion);
                importingElement.SdkLocation.ShouldBe(ElementLocation.EmptyLocation);
                importingElement.OriginalElement.ShouldBeOfType(expectedOriginalElementType);

                var implicitLocation = i == 0
                    ? ImplicitImportLocation.Top
                    : ImplicitImportLocation.Bottom;

                importingElement.ImplicitImportLocation.ShouldBe(implicitLocation);

                import.SdkResult.SdkReference.ShouldBeSameAs(importingElement.SdkReference);

                var expectedSdkPath = i == 0
                    ? _sdkPropsPath
                    : _sdkTargetsPath;

                import.SdkResult.Path.ShouldBe(Path.GetDirectoryName(expectedSdkPath));
                import.SdkResult.Version.ShouldBeEmpty();
            }
        }

        internal class SdkPropertiesAreExpandedDataTemplate
        {
            public SdkPropertiesAreExpandedDataTemplate(string template, bool expectedMinimumVersionIsNull)
            {
                Template = template ?? throw new ArgumentNullException(nameof(template));
                ExpectedMinimumVersion = expectedMinimumVersionIsNull ? null : string.Empty;
            }

            public string Template { get; }
            public string ExpectedMinimumVersion { get; }

            public override string ToString()
            {
                return Template switch
                {
                    ProjectTemplateSdkAsAttributeWithVersion => nameof(ProjectTemplateSdkAsAttributeWithVersion),
                    ProjectTemplateSdkAsElementWithVersion => nameof(ProjectTemplateSdkAsElementWithVersion),
                    ProjectTemplateSdkAsExplicitImportWithVersion => nameof(ProjectTemplateSdkAsExplicitImportWithVersion),
                    _ => "<unknown>"
                };
            }
        }

        internal class SdkPropertiesAreExpandedCase
        {
            public SdkPropertiesAreExpandedCase(SdkReferencePropertyExpansionMode? mode,
                SdkPropertiesAreExpandedDataTemplate template, bool setName, bool setVersion, bool expectedSuccess)
            {
                Mode = mode;
                Template = template ?? throw new ArgumentNullException(nameof(template));
                ExpectedSuccess = expectedSuccess;
                SetNameProperty = setName;
                SetVersionProperty = setVersion;
            }

            public SdkReferencePropertyExpansionMode? Mode { get; }
            public SdkPropertiesAreExpandedDataTemplate Template { get; }
            public string TemplateName { get; set; } = SdkName;
            public string TemplateVersion { get; set; } = SdkExpectedVersion;
            public bool SetNameProperty { get; }
            public bool SetVersionProperty { get; }
            public bool ExpectedSuccess { get; }

            public override string ToString()
            {
                var result = new StringBuilder(256);
                if (Mode.HasValue)
                    result.Append(Mode);
                else
                    result.Append($"{nameof(Mode)}: <null>");

                result.Append($", {Template}, {nameof(TemplateName)}: {TemplateName}, {nameof(TemplateVersion)}: {TemplateVersion}");

                if (SetNameProperty)
                    result.Append(", SetName");
                if (SetVersionProperty)
                    result.Append(", SetVersion");
                if (ExpectedSuccess)
                    result.Append(", ExpectedSuccess");

                return result.ToString();
            }
        }

        [Theory]
        [MemberData(nameof(SdkPropertiesAreExpandedData))]
        internal void SdkPropertiesAreExpanded(SdkPropertiesAreExpandedCase data)
        {
            _env.SetEnvironmentVariable("MSBuildSDKsPath", _testSdkRoot);
            _env.SetEnvironmentVariable("MSBUILD_SDKREFERENCE_PROPERTY_EXPANSION_MODE", data.Mode.ToString());

            File.WriteAllText(_sdkPropsPath, _sdkPropsContent);
            File.WriteAllText(_sdkTargetsPath, _sdkTargetsContent);

            var projectContents = string.Format(
                data.Template.Template,
                data.TemplateName,
                _projectInnerContents,
                data.TemplateVersion,
                null
            );

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(
                new MockExpandedSdkResolver(_testSdkDirectory),
                out var projectOptions
            );

            void AddProperty(string name, string value) =>
                (projectOptions.GlobalProperties ??= new Dictionary<string, string>()).Add(name, value);

            if (data.SetNameProperty)
                AddProperty(SdkNamePropertyName, SdkName);

            if (data.SetVersionProperty)
                AddProperty(SdkVersionPropertyName, SdkExpectedVersion);

            using var xmlReader = XmlReader.Create(new StringReader(projectContents));

            if (!data.ExpectedSuccess)
                projectOptions.LoadSettings |= ProjectLoadSettings.IgnoreMissingImports;

            var project = Project.FromXmlReader(xmlReader, projectOptions);

            if (data.ExpectedSuccess)
            {
                var expectedSdkPath = Path.GetDirectoryName(_sdkPropsPath);

                // self-consistency check
                expectedSdkPath.ShouldBe(Path.GetDirectoryName(_sdkTargetsPath));

                var expectedSdkReferenceRaw = new SdkReference(
                    data.TemplateName,
                    data.TemplateVersion,
                    data.Template.ExpectedMinimumVersion
                );

                var expectedSdkReference = new SdkReference(
                    SdkName,
                    SdkExpectedVersion,
                    data.Template.ExpectedMinimumVersion
                );

                project.Imports.Count.ShouldBe(2);

                foreach (var import in project.Imports)
                {
                    import.ImportingElement.SdkReference.ShouldBe(expectedSdkReferenceRaw);
                    import.SdkResult.Success.ShouldBeTrue();
                    import.SdkResult.SdkReference.ShouldBe(expectedSdkReference);
                    import.SdkResult.Path.ShouldBe(expectedSdkPath);
                    import.SdkResult.Version.ShouldBe(expectedSdkReference.Version);
                }
            }
            else
            {
                project.Imports.Count.ShouldBe(0);
            }
        }

        public static IEnumerable<object[]> SdkPropertiesAreExpandedData
        {
            get
            {
                static IEnumerable<SdkReferencePropertyExpansionMode?> Modes()
                {
                    yield return null;
                    yield return SdkReferencePropertyExpansionMode.NoExpansion;
                    yield return SdkReferencePropertyExpansionMode.DefaultExpand;
                    yield return SdkReferencePropertyExpansionMode.ExpandUnescape;
                    yield return SdkReferencePropertyExpansionMode.ExpandLeaveEscaped;
                }

                static IEnumerable<(SdkPropertiesAreExpandedDataTemplate, bool setName, bool setVersion)> Templates()
                {
                    var templates = new[]
                    {
                        new SdkPropertiesAreExpandedDataTemplate(
                            ProjectTemplateSdkAsAttributeWithVersion, true
                        ),
                        new SdkPropertiesAreExpandedDataTemplate(
                            ProjectTemplateSdkAsElementWithVersion, false
                        ),
                        new SdkPropertiesAreExpandedDataTemplate(
                            ProjectTemplateSdkAsExplicitImportWithVersion, false
                        )
                    };

                    foreach (var template in templates)
                    {
                        yield return (template, false, false);
                        yield return (template, false, true);
                        yield return (template, true, false);
                        yield return (template, true, true);
                    }
                }

                foreach (var mode in Modes())
                {
                    var shouldExpand = mode != SdkReferencePropertyExpansionMode.NoExpansion;

                    foreach (var (template, setName, setVersion) in Templates())
                    {
                        yield return new object[]
                        {
                            new SdkPropertiesAreExpandedCase(mode, template, setName, setVersion, true)
                        };

                        yield return new object[]
                        {
                            new SdkPropertiesAreExpandedCase(
                                mode, template, setName, setVersion, shouldExpand && setName
                            )
                            {
                                TemplateName = SdkNameProperty
                            }
                        };

                        yield return new object[]
                        {
                            new SdkPropertiesAreExpandedCase(
                                mode, template, setName, setVersion, shouldExpand && setVersion
                            )
                            {
                                TemplateVersion = SdkVersionProperty
                            }
                        };

                        yield return new object[]
                        {
                            new SdkPropertiesAreExpandedCase(
                                mode, template, setName, setVersion, shouldExpand && setName && setVersion
                            )
                            {
                                TemplateName = SdkNameProperty,
                                TemplateVersion = SdkVersionProperty
                            }
                        };
                    }
                }
            }
        }

        private sealed class MockExpandedSdkResolver : SdkResolver
        {
            private const string ResolverName = nameof(MockExpandedSdkResolver);
            private const string ErrorName = ResolverName + "/Error/" + nameof(SdkReference.Name);
            private const string ErrorVersion = ResolverName + "/Error/" + nameof(SdkReference.Version);

            public MockExpandedSdkResolver(string resolvedPath)
            {
                ResolvedPath = resolvedPath;
            }

            public override string Name => ResolverName;

            public override int Priority => 1;

            public string ResolvedPath { get; }

            public override SdkResult Resolve(SdkReference sdk, SdkResolverContext resolverContext,
                                              SdkResultFactory factory)
            {
                return sdk.Name switch
                {
                    SdkName when sdk.Version == SdkExpectedVersion =>
                    factory.IndicateSuccess(ResolvedPath, SdkExpectedVersion),
                    SdkName => factory.IndicateFailure(new[] {ErrorVersion}),
                    _ => factory.IndicateFailure(new[] {ErrorName})
                };
            }
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        private void VerifyPropertyFromImplicitImport(Project project, string propertyName, string expectedContainingProjectPath, string expectedValue)
        {
            ProjectProperty property = project.GetProperty(propertyName);

            Assert.NotNull(property?.Xml?.ContainingProject?.FullPath);

            Assert.Equal(expectedContainingProjectPath, property.Xml.ContainingProject.FullPath);

            Assert.True(property.IsImported);

            Assert.Equal(expectedValue, property.EvaluatedValue);
        }

        private SdkReference GetParsedSdk(ProjectImportElement element)
        {
            PropertyInfo parsedSdkInfo = typeof(ProjectImportElement).GetProperty("SdkReference", BindingFlags.Instance | BindingFlags.NonPublic);
            return (SdkReference)parsedSdkInfo.GetValue(element);
        }
    }
}
