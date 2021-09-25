// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Unittest;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Build.UnitTests.Evaluation
{
    public class SdkResultEvaluation_Tests : IDisposable
    {
        private TestEnvironment _env;
        private readonly string _testFolder;
        private MockLogger _logger;
        private ProjectCollection _projectCollection;
        private ITestOutputHelper _log;
        private bool _originalWarnOnUnitializedProperty;

        public SdkResultEvaluation_Tests(ITestOutputHelper log)
        {
            _log = log;
    
            _env = TestEnvironment.Create();

            _originalWarnOnUnitializedProperty = BuildParameters.WarnOnUninitializedProperty;
            BuildParameters.WarnOnUninitializedProperty = false;

            _testFolder = _env.CreateFolder().Path;
            _logger = new MockLogger();
            _projectCollection = _env.CreateProjectCollection().Collection;
            _projectCollection.RegisterLogger(_logger);
        }

        private Project CreateProject(string projectPath, ProjectOptions projectOptions)
        {
            ProjectRootElement projectRootElement = ProjectRootElement.Open(projectPath, _projectCollection);

            projectOptions.ProjectCollection = _projectCollection;

            var project = Project.FromProjectRootElement(projectRootElement, projectOptions);

            return project;
        }

        private void CreateMockSdkResultPropertiesAndItems(out Dictionary<string, string> propertiesToAdd, out Dictionary<string, SdkResultItem> itemsToAdd)
        {
            propertiesToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    {"PropertyFromSdkResolver", "ValueFromSdkResolver" }
                };

            itemsToAdd = new Dictionary<string, SdkResultItem>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ItemNameFromSdkResolver", new SdkResultItem( "ItemValueFromSdkResolver",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "MetadataName", "MetadataValue" }
                        })
                    }
                };
        }

        private void ValidateExpectedPropertiesAndItems(bool includePropertiesAndItems, Project project, int expectedItemCount = 1)
        {
            if (includePropertiesAndItems)
            {
                project.GetPropertyValue("PropertyFromSdkResolver").ShouldBe("ValueFromSdkResolver");

                var itemsFromResolver = project.GetItems("ItemNameFromSdkResolver");
                itemsFromResolver.Count.ShouldBe(expectedItemCount);
                foreach (var item in itemsFromResolver)
                {
                    ValidateItemFromResolver(item);
                }
            }
            else
            {
                project.GetProperty("PropertyFromSdkResolver").ShouldBeNull();
                project.GetItems("ItemNameFromSdkResolver").ShouldBeEmpty();
            }
        }

        private void ValidateItemFromResolver(ProjectItem item)
        {
            item.EvaluatedInclude.ShouldBe("ItemValueFromSdkResolver");
            item.Metadata.Select(m => (m.Name, m.EvaluatedValue))
                .ShouldBeSameIgnoringOrder(new[] { (Name: "MetadataName", EvaluatedValue: "MetadataValue") });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SdkResolverCanReturnNoPaths(bool includePropertiesAndItems)
        {
            Dictionary<string, string> propertiesToAdd = null;
            Dictionary<string, SdkResultItem> itemsToAdd = null;

            if (includePropertiesAndItems)
            {
                CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);
            }

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(
                new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                        Enumerable.Empty<string>(),
                        version: null,
                        propertiesToAdd,
                        itemsToAdd,
                        warnings: null
                    )),
                out var projectOptions
                );

            string projectContent = @"
                    <Project>
                        <Import Project=""Sdk.props"" Sdk=""TestPropsAndItemsFromResolverSdk""/>
                    </Project>";

            string projectPath = Path.Combine(_testFolder, "project.proj");
            File.WriteAllText(projectPath, projectContent);

            var project = CreateProject(projectPath, projectOptions);

            ValidateExpectedPropertiesAndItems(includePropertiesAndItems, project);

            _logger.ErrorCount.ShouldBe(0);
            _logger.WarningCount.ShouldBe(0);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void SdkResolverCanReturnSinglePath(bool includePropertiesAndItems, bool useSinglePathResult)
        {
            Dictionary<string, string> propertiesToAdd = null;
            Dictionary<string, SdkResultItem> itemsToAdd = null;

            if (includePropertiesAndItems)
            {
                CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);
            }

            var sdkResult = useSinglePathResult ?
                new Build.BackEnd.SdkResolution.SdkResult(
                    new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                    Path.Combine(_testFolder, "Sdk"),
                    version: null,
                    warnings: null,
                    propertiesToAdd,
                    itemsToAdd) :
                new Build.BackEnd.SdkResolution.SdkResult(
                    new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                    new[] { Path.Combine(_testFolder, "Sdk") },
                    version: null,
                    propertiesToAdd,
                    itemsToAdd,
                    warnings: null);

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(sdkResult),
                out var projectOptions);

            string projectContent = @"
                    <Project>
                        <PropertyGroup>
                            <ValueFromResolverBefore>Value=$(PropertyFromSdkResolver)</ValueFromResolverBefore>
                        </PropertyGroup>
                        <ItemGroup>
                            <ItemsFromSdkResolverBefore Include=""@(ItemNameFromSdkResolver)"" />
                        </ItemGroup>
                        <Import Project=""Sdk.props"" Sdk=""TestPropsAndItemsFromResolverSdk""/>
                        <PropertyGroup>
                            <ValueFromResolverAfter>Value=$(PropertyFromSdkResolver)</ValueFromResolverAfter>
                        </PropertyGroup>
                        <ItemGroup>
                            <ItemsFromSdkResolverAfter Include=""@(ItemNameFromSdkResolver)"" />
                        </ItemGroup>
                    </Project>";

            string projectPath = Path.Combine(_testFolder, "project.proj");
            File.WriteAllText(projectPath, projectContent);

            string sdkImportContents = @"
                    <Project>
                        <PropertyGroup>
                            <PropertyFromImportedSdk>ValueFromImportedSdk</PropertyFromImportedSdk>
                        </PropertyGroup>
                    </Project>";

            string sdkPropsPath = Path.Combine(_testFolder, "Sdk", "Sdk.props");
            Directory.CreateDirectory(Path.Combine(_testFolder, "Sdk"));
            File.WriteAllText(sdkPropsPath, sdkImportContents);

            var project = CreateProject(projectPath, projectOptions);

            ValidateExpectedPropertiesAndItems(includePropertiesAndItems, project);

            project.GetPropertyValue("ValueFromResolverBefore").ShouldBe("Value=");
            if (includePropertiesAndItems)
            {
                project.GetPropertyValue("ValueFromResolverAfter").ShouldBe("Value=ValueFromSdkResolver");
            }
            else
            {
                project.GetPropertyValue("ValueFromResolverAfter").ShouldBe("Value=");
            }

            project.GetPropertyValue("PropertyFromImportedSdk").ShouldBe("ValueFromImportedSdk");

            project.GetItems("ItemsFromSdkResolverBefore").ShouldBeEmpty();
            if (includePropertiesAndItems)
            {
                var items = project.GetItems("ItemsFromSdkResolverAfter");
                items.Count.ShouldBe(1);
                ValidateItemFromResolver(items.Single());
            }
            else
            {
                project.GetItems("ItemsFromSdkResolverAfter").ShouldBeEmpty();
            }

            _logger.ErrorCount.ShouldBe(0);
            _logger.WarningCount.ShouldBe(0);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SdkResolverCanReturnMultiplePaths(bool includePropertiesAndItems)
        {
            Dictionary<string, string> propertiesToAdd = null;
            Dictionary<string, SdkResultItem> itemsToAdd = null;

            if (includePropertiesAndItems)
            {
                CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);
            }

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(
                new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                        new[] {
                            Path.Combine(_testFolder, "Sdk1"),
                            Path.Combine(_testFolder, "Sdk2")
                        },
                        version: null,
                        propertiesToAdd,
                        itemsToAdd,
                        warnings: null
                    )),
                out var projectOptions
                );

            string projectContent = @"
                    <Project>
                        <PropertyGroup>
                            <ValueFromResolverBefore>Value=$(PropertyFromSdkResolver)</ValueFromResolverBefore>
                        </PropertyGroup>
                        <ItemGroup>
                            <ItemsFromSdkResolverBefore Include=""@(ItemNameFromSdkResolver)"" />
                        </ItemGroup>
                        <Import Project=""Sdk.props"" Sdk=""TestPropsAndItemsFromResolverSdk""/>
                        <PropertyGroup>
                            <ValueFromResolverAfter>Value=$(PropertyFromSdkResolver)</ValueFromResolverAfter>
                        </PropertyGroup>
                        <ItemGroup>
                            <ItemsFromSdkResolverAfter Include=""@(ItemNameFromSdkResolver)"" />
                        </ItemGroup>
                    </Project>";

            string projectPath = Path.Combine(_testFolder, "project.proj");
            File.WriteAllText(projectPath, projectContent);

            string sdk1ImportContents = @"
                    <Project>
                        <PropertyGroup>
                            <PropertyFromImportedSdk1>ValueFromImportedSdk1</PropertyFromImportedSdk1>
                        </PropertyGroup>
                    </Project>";

            string sdk1PropsPath = Path.Combine(_testFolder, "Sdk1", "Sdk.props");
            Directory.CreateDirectory(Path.Combine(_testFolder, "Sdk1"));
            File.WriteAllText(sdk1PropsPath, sdk1ImportContents);

            string sdk2ImportContents = @"
                    <Project>
                        <PropertyGroup>
                            <PropertyFromImportedSdk2>ValueFromImportedSdk2</PropertyFromImportedSdk2>
                        </PropertyGroup>
                    </Project>";

            string sdk2PropsPath = Path.Combine(_testFolder, "Sdk2", "Sdk.props");
            Directory.CreateDirectory(Path.Combine(_testFolder, "Sdk2"));
            File.WriteAllText(sdk2PropsPath, sdk2ImportContents);

            var project = CreateProject(projectPath, projectOptions);

            ValidateExpectedPropertiesAndItems(includePropertiesAndItems, project);

            project.GetPropertyValue("ValueFromResolverBefore").ShouldBe("Value=");
            if (includePropertiesAndItems)
            {
                project.GetPropertyValue("ValueFromResolverAfter").ShouldBe("Value=ValueFromSdkResolver");
            }
            else
            {
                project.GetPropertyValue("ValueFromResolverAfter").ShouldBe("Value=");
            }

            project.GetPropertyValue("PropertyFromImportedSdk1").ShouldBe("ValueFromImportedSdk1");
            project.GetPropertyValue("PropertyFromImportedSdk2").ShouldBe("ValueFromImportedSdk2");

            project.GetItems("ItemsFromSdkResolverBefore").ShouldBeEmpty();
            if (includePropertiesAndItems)
            {
                var items = project.GetItems("ItemsFromSdkResolverAfter");
                items.Count.ShouldBe(1);
                ValidateItemFromResolver(items.Single());
            }
            else
            {
                project.GetItems("ItemsFromSdkResolverAfter").ShouldBeEmpty();
            }

            if (_logger.ErrorCount > 0 || _logger.WarningCount > 0)
            {
                _log.WriteLine(_logger.FullLog);
            }

            _logger.ErrorCount.ShouldBe(0);
            _logger.WarningCount.ShouldBe(0);
        }

        //  When two different SdkResults (ie from the Sdk.props and Sdk.targets imports) return the same combination of items / properties:
        //  - Test that there aren't warnings for duplicate imports
        //  - Test that items from resolver are duplicated in final evaluation result
        [Fact]
        public void SdkResolverCanReturnTheSamePropertiesAndItemsMultipleTimes()
        {
            Dictionary<string, string> propertiesToAdd;
            Dictionary<string, SdkResultItem> itemsToAdd;
            CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(
                new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestPropsAndItemsFromResolverSdk", null, null),
                        new[] { Path.Combine(_testFolder, "Sdk") },
                        version: null,
                        propertiesToAdd,
                        itemsToAdd,
                        warnings: null
                    )),
                out var projectOptions
                );

            string projectContent = @"
                    <Project Sdk=""TestPropsAndItemsFromResolverSdk"">
                        <PropertyGroup>
                            <ValueFromResolverInProjectBody>Value=$(PropertyFromSdkResolver)</ValueFromResolverInProjectBody>
                        </PropertyGroup>
                        <ItemGroup>
                            <ItemsFromSdkResolverInProjectBody Include=""@(ItemNameFromSdkResolver)"" />
                        </ItemGroup>
                    </Project>";

            string projectPath = Path.Combine(_testFolder, "project.proj");
            File.WriteAllText(projectPath, projectContent);

            string sdkPropsContents = @"
                    <Project>
                        <PropertyGroup>
                            <PropertyFromSdkProps>PropertyFromSdkPropsValue</PropertyFromSdkProps>
                        </PropertyGroup>
                    </Project>";

            string sdkPropsPath = Path.Combine(_testFolder, "Sdk", "Sdk.props");
            Directory.CreateDirectory(Path.Combine(_testFolder, "Sdk"));
            File.WriteAllText(sdkPropsPath, sdkPropsContents);

            string sdkTargetsContents = @"
                    <Project>
                        <PropertyGroup>
                            <PropertyFromSdkTargets>PropertyFromSdkTargetsValue</PropertyFromSdkTargets>
                        </PropertyGroup>
                    </Project>";

            string sdkTargetsPath = Path.Combine(_testFolder, "Sdk", "Sdk.targets");
            File.WriteAllText(sdkTargetsPath, sdkTargetsContents);

            var project = CreateProject(projectPath, projectOptions);

            ValidateExpectedPropertiesAndItems(true, project, expectedItemCount: 2);

            project.GetPropertyValue("ValueFromResolverInProjectBody").ShouldBe("Value=ValueFromSdkResolver");
            project.GetPropertyValue("PropertyFromSdkProps").ShouldBe("PropertyFromSdkPropsValue");
            project.GetPropertyValue("PropertyFromSdkTargets").ShouldBe("PropertyFromSdkTargetsValue");

            var itemsFromBody = project.GetItems("ItemsFromSdkResolverInProjectBody");
            itemsFromBody.Count.ShouldBe(1);
            ValidateItemFromResolver(itemsFromBody.Single());

            _logger.ErrorCount.ShouldBe(0);
            _logger.WarningCount.ShouldBe(0);
        }

        [Fact]
        public void SdkResolverCanReturnSpecialCharacters()
        {
            //  %3B - semicolon
            //  %24 - $
            //  %0A - LF

            string specialString = "%3B;%24$%0A\\\"'";

            Dictionary<string, string> propertiesToAdd = new Dictionary<string, string>()
            {
                { "PropertyName", "PropertyValue" + specialString }
            };

            Dictionary<string, SdkResultItem> itemsToAdd = new Dictionary<string, SdkResultItem>()
            {
                {
                    "ItemName",
                    new SdkResultItem(itemSpec: "ItemValue" + specialString, new Dictionary<string, string>()
                        { { "MetadataName", "MetadataValue" + specialString } })
                }
            };

            using var state = SdkUtilities.CreateProjectOptionsWithResolver(new SdkUtilities.ConfigurableMockSdkResolver(
                new Build.BackEnd.SdkResolution.SdkResult(
                        new SdkReference("TestSpecialCharactersFromSdkResolver", null, null),
                        Enumerable.Empty<string>(),
                        version: null,
                        propertiesToAdd,
                        itemsToAdd,
                        warnings: null
                    )),
                out var projectOptions
                );

            string projectContent = @"
                    <Project>
                        <Import Project=""Sdk.props"" Sdk=""TestSpecialCharactersFromSdkResolver""/>
                    </Project>";

            string projectPath = Path.Combine(_testFolder, "project.proj");
            File.WriteAllText(projectPath, projectContent);

            var project = CreateProject(projectPath, projectOptions);

            project.GetPropertyValue("PropertyName").ShouldBe("PropertyValue" + specialString);

            var itemsFromResolver = project.GetItems("ItemName");
            var item = itemsFromResolver.ShouldHaveSingleItem();
            item.EvaluatedInclude.ShouldBe("ItemValue" + specialString);
            item.Metadata.Select(m => (m.Name, m.EvaluatedValue))
                .ShouldBeSameIgnoringOrder(new[] { (Name: "MetadataName", EvaluatedValue: "MetadataValue" + specialString) });

            _logger.ErrorCount.ShouldBe(0);
            _logger.WarningCount.ShouldBe(0);
        }

        public void Dispose()
        {
            _env.Dispose();
            BuildParameters.WarnOnUninitializedProperty = _originalWarnOnUnitializedProperty;
        }
    }
}
