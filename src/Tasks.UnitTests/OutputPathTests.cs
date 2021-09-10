// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Build.Tasks.UnitTests
{
    public sealed class OutputPathTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _projectRelativePath = Path.Combine("src", "test", "test.csproj");

        public OutputPathTests(ITestOutputHelper output)
        {
            _output = output;
            ObjectModelHelpers.DeleteTempProjectDirectory();
        }

        public void Dispose()
        {
            ObjectModelHelpers.DeleteTempProjectDirectory();
        }

        /// <summary>
        /// Test when both BaseOutputPath and OutputPath are not specified.
        /// </summary>
        [Fact]
        public void BothBaseOutputPathAndOutputPathWereNotSpecified()
        {
            // Arrange
            var baseOutputPath = "bin";

            var projectFilePath = ObjectModelHelpers.CreateFileInTempProjectDirectory(_projectRelativePath,
$@"<Project DefaultTargets=`Build` xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.props`/>

    <PropertyGroup>
        <Platform>AnyCPU</Platform>
        <Configuration>Debug</Configuration>
    </PropertyGroup>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.targets`/>
    <Target Name=`Build`/>

</Project>");

            // Act
            Project project = ObjectModelHelpers.LoadProjectFileInTempProjectDirectory(projectFilePath, touchProject: false);

            project.Build(new MockLogger(_output)).ShouldBeFalse();

            // Assert
            project.GetPropertyValue("BaseOutputPath").ShouldBe(baseOutputPath.WithTrailingSlash());
            project.GetPropertyValue("BaseOutputPathWasSpecified").ShouldBe(string.Empty);
            project.GetPropertyValue("_OutputPathWasMissing").ShouldBe("true");
        }

        /// <summary>
        /// Test when BaseOutputPath is specified without the OutputPath.
        /// </summary>
        [Fact]
        public void BaseOutputPathWasSpecifiedAndIsOverridable()
        {
            // Arrange
            var baseOutputPath = Path.Combine("build", "bin");

            var projectFilePath = ObjectModelHelpers.CreateFileInTempProjectDirectory(_projectRelativePath,
$@"<Project DefaultTargets=`Build` xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.props`/>

    <PropertyGroup>
        <Platform>AnyCPU</Platform>
        <Configuration>Debug</Configuration>
        <BaseOutputPath>{baseOutputPath}</BaseOutputPath>
    </PropertyGroup>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.targets`/>
    <Target Name=`Build`/>

</Project>");

            // Act
            Project project = ObjectModelHelpers.LoadProjectFileInTempProjectDirectory(projectFilePath, touchProject: false);

            project.Build(new MockLogger(_output)).ShouldBeTrue();

            // Assert
            project.GetPropertyValue("BaseOutputPath").ShouldBe(baseOutputPath.WithTrailingSlash());
            project.GetPropertyValue("BaseOutputPathWasSpecified").ShouldBe("true");
            project.GetPropertyValue("_OutputPathWasMissing").ShouldBe("true");
        }

        /// <summary>
        /// Test when both BaseOutputPath and OutputPath are specified.
        /// </summary>
        [Fact]
        public void BothBaseOutputPathAndOutputPathWereSpecified()
        {
            // Arrange
            var baseOutputPath = Path.Combine("build", "bin");
            var outputPath = Path.Combine("bin", "Debug");
            var outputPathAlt = Path.Combine("bin", "Release");

            var projectFilePath = ObjectModelHelpers.CreateFileInTempProjectDirectory(_projectRelativePath,
$@"<Project DefaultTargets=`Build` xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.props`/>

    <PropertyGroup>
        <Platform>AnyCPU</Platform>
        <Configuration>Debug</Configuration>
    </PropertyGroup>

    <PropertyGroup>
        <BaseOutputPath>{baseOutputPath}</BaseOutputPath>
        <OutputPath Condition=`'$(Platform)|$(Configuration)' == 'AnyCPU|Debug'`>{outputPath}</OutputPath>
        <OutputPath Condition=`'$(Platform)|$(Configuration)' == 'AnyCPU|Release'`>{outputPathAlt}</OutputPath>
    </PropertyGroup>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.targets`/>
    <Target Name=`Build`/>

</Project>");

            // Act
            Project project = ObjectModelHelpers.LoadProjectFileInTempProjectDirectory(projectFilePath, touchProject: false);

            project.Build(new MockLogger(_output)).ShouldBeTrue();

            // Assert
            project.GetPropertyValue("BaseOutputPath").ShouldBe(baseOutputPath.WithTrailingSlash());
            project.GetPropertyValue("OutputPath").ShouldBe(outputPath.WithTrailingSlash());
            project.GetPropertyValue("BaseOutputPathWasSpecified").ShouldBe("true");
            project.GetPropertyValue("_OutputPathWasMissing").ShouldBe(string.Empty);
        }

        /// <summary>
        /// Test for [MSBuild]::NormalizePath and [MSBuild]::NormalizeDirectory returning current directory instead of current Project directory.
        /// </summary>
        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsWindows), Skip = "Skipping this test for now until we have a consensus about this issue.")]
        public void MSBuildNormalizePathShouldReturnProjectDirectory()
        {
            // Arrange
            var configuration = "Debug";
            var baseOutputPath = "bin";

            var projectFilePath = ObjectModelHelpers.CreateFileInTempProjectDirectory(_projectRelativePath,
$@"<Project DefaultTargets=`Build` xmlns=`msbuildnamespace` ToolsVersion=`msbuilddefaulttoolsversion`>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.props`/>

    <PropertyGroup Condition=`'$(OutputPath)' == ''`>
        <OutputPath>$([MSBuild]::NormalizeDirectory('{baseOutputPath}', '{configuration}'))</OutputPath>
    </PropertyGroup>

    <Import Project=`$(MSBuildToolsPath)\Microsoft.Common.targets`/>
    <Target Name=`Build`/>

</Project>");

            // Act
            Project project = ObjectModelHelpers.LoadProjectFileInTempProjectDirectory(projectFilePath, touchProject: false);

            project.Build(new MockLogger(_output)).ShouldBeTrue();

            // Assert
            project.GetPropertyValue("Configuration").ShouldBe(configuration);
            project.GetPropertyValue("BaseOutputPath").ShouldBe(baseOutputPath.WithTrailingSlash());

            var expectedOutputPath = FileUtilities.CombinePaths(project.DirectoryPath, baseOutputPath, configuration).WithTrailingSlash();
            project.GetPropertyValue("OutputPath").ShouldBe(expectedOutputPath);
        }
    }
}
