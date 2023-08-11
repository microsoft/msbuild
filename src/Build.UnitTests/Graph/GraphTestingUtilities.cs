﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Shouldly;

#nullable disable

namespace Microsoft.Build.Graph.UnitTests
{
    internal static class GraphTestingUtilities
    {
        public static readonly ImmutableDictionary<string, string> EmptyGlobalProperties = new Dictionary<string, string> { { PropertyNames.IsGraphBuild, "true" } }.ToImmutableDictionary();

        public const string InnerBuildPropertyName = "InnerBuild";
        public const string InnerBuildPropertiesName = "InnerBuildProperties";

        public const string MultitargetingSpecificationPropertyGroup = $@"<PropertyGroup>
                                                                        <InnerBuildProperty>{InnerBuildPropertyName}</InnerBuildProperty>
                                                                        <InnerBuildPropertyValues>{InnerBuildPropertiesName}</InnerBuildPropertyValues>
                                                                        <{InnerBuildPropertiesName}>a;b</{InnerBuildPropertiesName}>
                                                                     </PropertyGroup>";
        public const string HardCodedInnerBuildWithMultitargetingSpecification = $@"<PropertyGroup>
                                                                        <InnerBuildProperty>{InnerBuildPropertyName}</InnerBuildProperty>
                                                                        <InnerBuildPropertyValues>{InnerBuildPropertiesName}</InnerBuildPropertyValues>
                                                                        <{InnerBuildPropertyName}>a</{InnerBuildPropertyName}>
                                                                     </PropertyGroup>";

        public const string EnableTransitiveProjectReferencesPropertyGroup = @"<PropertyGroup>
                                                                                            <AddTransitiveProjectReferencesInStaticGraph>true</AddTransitiveProjectReferencesInStaticGraph>
                                                                                         </PropertyGroup>";

        public static void AssertOuterBuild(
            ProjectGraphNode outerBuild,
            ProjectGraph graph,
            Dictionary<string, string> additionalGlobalProperties = null,
            int expectedInnerBuildCount = 2)
        {
            additionalGlobalProperties ??= new Dictionary<string, string>(0);

            AssertOuterBuildEvaluation(outerBuild, additionalGlobalProperties);

            outerBuild.ProjectReferences.Count.ShouldBe(expectedInnerBuildCount);

            // Outer -> Inner build edges
            foreach (ProjectGraphNode innerBuild in outerBuild.ProjectReferences)
            {
                AssertInnerBuildEvaluation(innerBuild, true, additionalGlobalProperties);

                var edge = graph.TestOnly_Edges[(outerBuild, innerBuild)];
                edge.DirectMetadataCount.ShouldBe(1);

                string expectedPropertiesMetadata = $"{InnerBuildPropertyName}={innerBuild.ProjectInstanceSnapshot.GlobalProperties[InnerBuildPropertyName]}";
                edge.GetMetadataValue("Properties").ShouldBe(expectedPropertiesMetadata);
            }

            // Ensure edges were added directly to the inner builds
            foreach (ProjectGraphNode outerBuildReferencer in outerBuild.ReferencingProjects)
            {
                ProjectGraphNode[] innerBuilds = outerBuildReferencer.ProjectReferences
                    .Where(p => IsInnerBuild(p) && p.ProjectInstanceSnapshot.FullPath == outerBuild.ProjectInstanceSnapshot.FullPath)
                    .ToArray();

                innerBuilds.Length.ShouldBe(expectedInnerBuildCount);

                foreach (ProjectGraphNode innerBuild in innerBuilds)
                {
                    AssertInnerBuildEvaluation(innerBuild, true, additionalGlobalProperties);

                    innerBuild.ReferencingProjects.ShouldContain(outerBuildReferencer);
                    innerBuild.ReferencingProjects.ShouldContain(outerBuild);

                    graph.TestOnly_Edges.HasEdge((outerBuild, innerBuild)).ShouldBeTrue();

                    var edgeToOuterBuild = graph.TestOnly_Edges[(outerBuildReferencer, outerBuild)];
                    var edgeToInnerBuild = graph.TestOnly_Edges[(outerBuildReferencer, innerBuild)];

                    edgeToOuterBuild.ShouldBe(edgeToInnerBuild);
                }
            }
        }

        public static void AssertNonMultitargetingNode(ProjectGraphNode node, Dictionary<string, string> additionalGlobalProperties = null)
        {
            additionalGlobalProperties ??= new Dictionary<string, string>();

            IsNotMultitargeting(node).ShouldBeTrue();
            node.ProjectInstanceSnapshot.GlobalProperties.ShouldBeSameIgnoringOrder(EmptyGlobalProperties.AddRange(additionalGlobalProperties));
            node.ProjectInstanceSnapshot.GetPropertyValue(InnerBuildPropertyName).ShouldBeNullOrEmpty();
        }

        public static void AssertOuterBuildEvaluation(ProjectGraphNode outerBuild, Dictionary<string, string> additionalGlobalProperties)
        {
            additionalGlobalProperties.ShouldNotBeNull();

            IsOuterBuild(outerBuild).ShouldBeTrue();
            IsInnerBuild(outerBuild).ShouldBeFalse();

            outerBuild.ProjectInstanceSnapshot.GetPropertyValue(InnerBuildPropertyName).ShouldBeNullOrEmpty();
            outerBuild.ProjectInstanceSnapshot.GlobalProperties.ShouldBeSameIgnoringOrder(EmptyGlobalProperties.AddRange(additionalGlobalProperties));
        }

        public static void AssertInnerBuildEvaluation(
            ProjectGraphNode innerBuild,
            bool InnerBuildPropertyIsSetViaGlobalProperty,
            Dictionary<string, string> additionalGlobalProperties)
        {
            additionalGlobalProperties.ShouldNotBeNull();

            IsOuterBuild(innerBuild).ShouldBeFalse();
            IsInnerBuild(innerBuild).ShouldBeTrue();

            var innerBuildPropertyValue = innerBuild.ProjectInstanceSnapshot.GetPropertyValue(InnerBuildPropertyName);

            innerBuildPropertyValue.ShouldNotBeNullOrEmpty();

            if (InnerBuildPropertyIsSetViaGlobalProperty)
            {
                innerBuild.ProjectInstanceSnapshot.GlobalProperties.ShouldBeSameIgnoringOrder(
                    EmptyGlobalProperties
                        .Add(InnerBuildPropertyName, innerBuildPropertyValue)
                        .AddRange(additionalGlobalProperties));
            }
        }

        internal static bool IsOuterBuild(ProjectGraphNode project)
        {
            return project.ProjectInstanceSnapshot.ProjectType == ProjectInterpretation.ProjectType.OuterBuild;
        }

        internal static bool IsInnerBuild(ProjectGraphNode project)
        {
            return project.ProjectInstanceSnapshot.ProjectType == ProjectInterpretation.ProjectType.InnerBuild;
        }

        internal static bool IsNotMultitargeting(ProjectGraphNode project)
        {
            return project.ProjectInstanceSnapshot.ProjectType == ProjectInterpretation.ProjectType.NonMultitargeting;
        }

        internal static ProjectGraphNode GetFirstNodeWithProjectNumber(ProjectGraph graph, int projectNum)
        {
            return GetNodesWithProjectNumber(graph, projectNum).First();
        }

        internal static IEnumerable<ProjectGraphNode> GetNodesWithProjectNumber(ProjectGraph graph, int projectNum)
        {
            return graph.ProjectNodes.Where(node => node.ProjectInstanceSnapshot.FullPath.EndsWith(projectNum + ".proj"));
        }

        internal static ProjectGraphNode GetOuterBuild(ProjectGraph graph, int projectNumber)
        {
            return GetNodesWithProjectNumber(graph, projectNumber).FirstOrDefault(IsOuterBuild);
        }

        internal static IReadOnlyCollection<ProjectGraphNode> GetInnerBuilds(ProjectGraph graph, int projectNumber)
        {
            var outerBuild = GetOuterBuild(graph, projectNumber);

            if (outerBuild == null)
            {
                return ImmutableArray<ProjectGraphNode>.Empty;
            }
            else
            {
                var innerBuilds = GetNodesWithProjectNumber(graph, projectNumber)
                    .Where(p => IsInnerBuild(p) && p.ProjectInstanceSnapshot.FullPath.Equals(outerBuild.ProjectInstanceSnapshot.FullPath))
                    .ToArray();

                innerBuilds.ShouldNotBeEmpty();

                return innerBuilds;
            }
        }

        internal static string GetProjectFileName(ProjectGraphNode node)
        {
            node.ShouldNotBeNull();
            return Path.GetFileNameWithoutExtension(node.ProjectInstanceSnapshot.FullPath);
        }

        internal static string GetProjectFileName(ConfigurationMetadata config)
        {
            config.ShouldNotBeNull();
            return Path.GetFileNameWithoutExtension(config.ProjectFullPath);
        }

        internal static int GetProjectNumber(ProjectGraphNode node)
        {
            node.ShouldNotBeNull();
            return int.Parse(GetProjectFileName(node));
        }

        internal static int GetProjectNumber(ConfigurationMetadata config)
        {
            config.ShouldNotBeNull();
            return int.Parse(GetProjectFileName(config));
        }

        internal static string GetProjectPath(ProjectGraphNode node)
        {
            node.ShouldNotBeNull();
            return node.ProjectInstanceSnapshot.FullPath;
        }

        internal static TransientTestFile CreateProjectFile(
            TestEnvironment env,
            int projectNumber,
            int[] projectReferences = null,
            Dictionary<string, string[]> projectReferenceTargets = null,
            string defaultTargets = null,
            string extraContent = null)
        {
            return Helpers.CreateProjectFile(
                env,
                projectNumber,
                projectReferences,
                projectReferenceTargets,
                // Use "Build" when the default target is unspecified since in practice that is usually the default target.
                defaultTargets ?? "Build",
                extraContent);
        }

        internal static IEnumerable<ProjectGraphNode> ComputeClosure(ProjectGraphNode node)
        {
            return ComputeClosureRecursive(node).ToHashSet();

            IEnumerable<ProjectGraphNode> ComputeClosureRecursive(ProjectGraphNode projectGraphNode)
            {
                foreach (var reference in projectGraphNode.ProjectReferences)
                {
                    yield return reference;

                    foreach (var closureReference in ComputeClosureRecursive(reference))
                    {
                        yield return closureReference;
                    }
                }
            }
        }

        internal static void AssertReferencesIgnoringOrder(this ProjectGraph graph, Dictionary<int, int[]> expectedReferencesForNode)
        {
            foreach (var kvp in expectedReferencesForNode)
            {
                var node = GetFirstNodeWithProjectNumber(graph, kvp.Key);
                node.AssertReferencesIgnoringOrder(kvp.Value);
            }
        }

        internal static void AssertReferencesIgnoringOrder(this ProjectGraphNode node, int[] expectedReferences)
        {
            node.ProjectReferences.Select(GetProjectNumber).ShouldBeSameIgnoringOrder(expectedReferences);
        }
    }
}
