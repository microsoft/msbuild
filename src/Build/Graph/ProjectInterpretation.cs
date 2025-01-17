// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Graph
{
    internal sealed class ProjectInterpretation
    {
        private const string FullPathMetadataName = "FullPath";
        private const string ToolsVersionMetadataName = "ToolsVersion";
        private const string SetConfigurationMetadataName = "SetConfiguration";
        private const string SetPlatformMetadataName = "SetPlatform";
        private const string SetTargetFrameworkMetadataName = "SetTargetFramework";
        private const string GlobalPropertiesToRemoveMetadataName = "GlobalPropertiesToRemove";
        private const string ProjectReferenceTargetIsOuterBuildMetadataName = "OuterBuild";
        private const string InnerBuildReferenceItemName = "_ProjectSelfReference";
        internal static string TransitiveReferenceItemName = "_TransitiveProjectReference";
        internal const string AddTransitiveProjectReferencesInStaticGraphPropertyName = "AddTransitiveProjectReferencesInStaticGraph";
        private const string PlatformLookupTableMetadataName = "PlatformLookupTable";
        private const string PlatformMetadataName = "Platform";
        private const string PlatformsMetadataName = "Platforms";
        private const string EnableDynamicPlatformResolutionPropertyName = "EnableDynamicPlatformResolution";
        private const string OverridePlatformNegotiationValue = "OverridePlatformNegotiationValue";
        private const string ShouldUnsetParentConfigurationAndPlatformPropertyName = "ShouldUnsetParentConfigurationAndPlatform";
        private const string ProjectMetadataName = "Project";
        private const string ConfigurationMetadataName = "Configuration";

        private static readonly char[] PropertySeparator = MSBuildConstants.SemicolonChar;

        public static ProjectInterpretation Instance = new ProjectInterpretation();

        private static readonly ImmutableList<GlobalPropertiesModifier> ModifierForNonMultitargetingNodes = [(GlobalPropertiesModifier)ProjectReferenceGlobalPropertiesModifier];

        internal enum ProjectType
        {
            OuterBuild,
            InnerBuild,
            NonMultitargeting,
        }

        internal readonly record struct ReferenceInfo(ConfigurationMetadata ReferenceConfiguration, ProjectItemInstance ProjectReferenceItem);

        private readonly struct TargetSpecification
        {
            public TargetSpecification(string target, bool skipIfNonexistent)
            {
                // Verify that if this target is skippable then it equals neither
                // ".default" nor ".projectReferenceTargetsOrDefaultTargets".
                ErrorUtilities.VerifyThrow(
                    !skipIfNonexistent || (!target.Equals(MSBuildConstants.DefaultTargetsMarker)
                    && !target.Equals(MSBuildConstants.ProjectReferenceTargetsOrDefaultTargetsMarker)),
                    target + " cannot be marked as SkipNonexistentTargets");
                Target = target;
                SkipIfNonexistent = skipIfNonexistent;
            }

            public string Target { get; }

            public bool SkipIfNonexistent { get; }
        }

        public IEnumerable<ReferenceInfo> GetReferences(ProjectGraphNode projectGraphNode, ProjectCollection projectCollection, ProjectGraph.ProjectInstanceFactoryFunc projectInstanceFactory)
        {
            IEnumerable<ProjectItemInstance> projectReferenceItems;
            IEnumerable<GlobalPropertiesModifier> globalPropertiesModifiers = null;

            ProjectInstance requesterInstance = projectGraphNode.ProjectInstance;

            switch (projectGraphNode.ProjectType)
            {
                case ProjectType.OuterBuild:
                    projectReferenceItems = ConstructInnerBuildReferences(requesterInstance);
                    break;
                case ProjectType.InnerBuild:
                    globalPropertiesModifiers = ModifierForNonMultitargetingNodes.Add((parts, reference) => parts.AddPropertyToUndefine(GetInnerBuildPropertyName(requesterInstance)));
                    projectReferenceItems = requesterInstance.GetItems(ItemTypeNames.ProjectReference);
                    break;
                case ProjectType.NonMultitargeting:
                    globalPropertiesModifiers = ModifierForNonMultitargetingNodes;
                    projectReferenceItems = requesterInstance.GetItems(ItemTypeNames.ProjectReference);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            SolutionConfiguration solutionConfiguration = null;
            string solutionConfigurationXml = requesterInstance.GetEngineRequiredPropertyValue(SolutionProjectGenerator.CurrentSolutionConfigurationContents);
            if (!string.IsNullOrWhiteSpace(solutionConfigurationXml))
            {
                solutionConfiguration = new SolutionConfiguration(solutionConfigurationXml);
            }

            foreach (ProjectItemInstance projectReferenceItem in projectReferenceItems)
            {
                if (!String.IsNullOrEmpty(projectReferenceItem.GetMetadataValue(ToolsVersionMetadataName)))
                {
                    throw new InvalidOperationException(
                        String.Format(
                            CultureInfo.InvariantCulture,
                            ResourceUtilities.GetResourceString(
                                "ProjectGraphDoesNotSupportProjectReferenceWithToolset"),
                            projectReferenceItem.EvaluatedInclude,
                            requesterInstance.FullPath));
                }

                string projectReferenceFullPath = projectReferenceItem.GetMetadataValue(FullPathMetadataName);
                bool enableDynamicPlatformResolution = ConversionUtilities.ValidBooleanTrue(requesterInstance.GetEngineRequiredPropertyValue(EnableDynamicPlatformResolutionPropertyName));

                PropertyDictionary<ProjectPropertyInstance> referenceGlobalProperties = GetGlobalPropertiesForItem(
                    projectReferenceItem,
                    requesterInstance.GlobalPropertiesDictionary,
                    // Only allow reuse in scenarios where we will not mutate the collection.
                    // TODO: Should these mutations be moved to globalPropertiesModifiers in the future?
                    allowCollectionReuse: solutionConfiguration == null && !enableDynamicPlatformResolution,
                    globalPropertiesModifiers);

                bool configurationDefined = false;

                // Match what AssignProjectConfiguration does to resolve project references.
                if (solutionConfiguration != null)
                {
                    string projectGuid = projectReferenceItem.GetMetadataValue(ProjectMetadataName);
                    if (solutionConfiguration.TryGetProjectByGuid(projectGuid, out XmlElement projectElement)
                        || solutionConfiguration.TryGetProjectByAbsolutePath(projectReferenceFullPath, out projectElement))
                    {
                        // Note: AssignProjectConfiguration sets various metadata on the ProjectReference item, but ultimately it just translates to the Configuration and Platform global properties on the MSBuild task.
                        string projectConfiguration = projectElement.InnerText;
                        string[] configurationPlatformParts = projectConfiguration.Split(SolutionConfiguration.ConfigPlatformSeparator[0]);
                        SetProperty(referenceGlobalProperties, ConfigurationMetadataName, configurationPlatformParts[0]);

                        if (configurationPlatformParts.Length > 1)
                        {
                            SetProperty(referenceGlobalProperties, PlatformMetadataName, configurationPlatformParts[1]);
                        }
                        else
                        {
                            referenceGlobalProperties.Remove(PlatformMetadataName);
                        }

                        configurationDefined = true;
                    }
                    else
                    {
                        // Note: ShouldUnsetParentConfigurationAndPlatform defaults to true in the AssignProjectConfiguration target when building a solution, so check that it's not false instead of checking that it's true.
                        bool shouldUnsetParentConfigurationAndPlatform = !ConversionUtilities.ValidBooleanFalse(requesterInstance.GetEngineRequiredPropertyValue(ShouldUnsetParentConfigurationAndPlatformPropertyName));
                        if (shouldUnsetParentConfigurationAndPlatform)
                        {
                            referenceGlobalProperties.Remove(ConfigurationMetadataName);
                            referenceGlobalProperties.Remove(PlatformMetadataName);
                        }
                        else
                        {
                            configurationDefined = true;
                        }
                    }
                }

                // Note: Dynamic platform resolution is not enabled for sln-based builds,
                // unless the project isn't known to the solution.
                if (enableDynamicPlatformResolution && !configurationDefined && !projectReferenceItem.HasMetadata(SetPlatformMetadataName))
                {
                    string requesterPlatform = requesterInstance.GetEngineRequiredPropertyValue("Platform");
                    string requesterPlatformLookupTable = requesterInstance.GetEngineRequiredPropertyValue("PlatformLookupTable");

                    var projectInstance = projectInstanceFactory(
                        projectReferenceFullPath,
                        null, // Platform negotiation requires an evaluation with no global properties first
                        projectCollection);

                    string overridePlatformNegotiationMetadataValue = projectReferenceItem.GetMetadataValue(OverridePlatformNegotiationValue);

                    var selectedPlatform = PlatformNegotiation.GetNearestPlatform(overridePlatformNegotiationMetadataValue, projectInstance.GetEngineRequiredPropertyValue(PlatformMetadataName), projectInstance.GetEngineRequiredPropertyValue(PlatformsMetadataName), projectInstance.GetEngineRequiredPropertyValue(PlatformLookupTableMetadataName), requesterInstance.GetEngineRequiredPropertyValue(PlatformLookupTableMetadataName), projectInstance.FullPath, requesterInstance.GetEngineRequiredPropertyValue(PlatformMetadataName));

                    if (selectedPlatform.Equals(String.Empty))
                    {
                        referenceGlobalProperties.Remove(PlatformMetadataName);
                    }
                    else
                    {
                        SetProperty(referenceGlobalProperties, PlatformMetadataName, selectedPlatform);
                    }
                }

                var referenceConfig = new ConfigurationMetadata(projectReferenceFullPath, referenceGlobalProperties);

                yield return new ReferenceInfo(referenceConfig, projectReferenceItem);

                static void SetProperty(PropertyDictionary<ProjectPropertyInstance> properties, string propertyName, string propertyValue)
                {
                    ProjectPropertyInstance propertyInstance = ProjectPropertyInstance.Create(propertyName, propertyValue);
                    properties[propertyName] = propertyInstance;
                }
            }
        }

        internal static string GetInnerBuildPropertyValue(ProjectInstance project)
        {
            return project.GetPropertyValue(GetInnerBuildPropertyName(project));
        }

        internal static string GetInnerBuildPropertyName(ProjectInstance project)
        {
            return project.GetPropertyValue(PropertyNames.InnerBuildProperty);
        }

        internal static string GetInnerBuildPropertyValues(ProjectInstance project)
        {
            return project.GetPropertyValue(project.GetPropertyValue(PropertyNames.InnerBuildPropertyValues));
        }

        internal static ProjectType GetProjectType(ProjectInstance project)
        {
            var isOuterBuild = String.IsNullOrWhiteSpace(GetInnerBuildPropertyValue(project)) && !String.IsNullOrWhiteSpace(GetInnerBuildPropertyValues(project));
            var isInnerBuild = !String.IsNullOrWhiteSpace(GetInnerBuildPropertyValue(project));

            ErrorUtilities.VerifyThrow(!(isOuterBuild && isInnerBuild), $"A project cannot be an outer and inner build at the same time: ${project.FullPath}");

            return isOuterBuild
                ? ProjectType.OuterBuild
                : isInnerBuild
                    ? ProjectType.InnerBuild
                    : ProjectType.NonMultitargeting;
        }

        /// <summary>
        /// To avoid calling nuget at graph construction time, the graph is initially constructed with nodes referencing outer build nodes which in turn
        /// reference inner build nodes. However at build time, the inner builds are referenced directly by the nodes referencing the outer build.
        /// Change the graph to mimic this behaviour.
        /// Example: Node -> Outer -> Inner go to: Node -> Outer; Node->Inner; Outer -> Inner. Inner build edges get added to Node.
        /// </summary>
        public void AddInnerBuildEdges(Dictionary<ConfigurationMetadata, ParsedProject> allNodes, GraphBuilder graphBuilder)
        {
            foreach (KeyValuePair<ConfigurationMetadata, ParsedProject> node in allNodes)
            {
                ProjectGraphNode outerBuild = node.Value.GraphNode;

                if (outerBuild.ProjectType == ProjectType.OuterBuild && outerBuild.ReferencingProjects.Count != 0)
                {
                    foreach (ProjectGraphNode innerBuild in outerBuild.ProjectReferences)
                    {
                        foreach (ProjectGraphNode outerBuildReferencingProject in outerBuild.ReferencingProjects)
                        {
                            // Which edge should be used to connect the outerBuildReferencingProject to the inner builds?
                            // Decided to use the outerBuildBuildReferencingProject -> outerBuild edge in order to preserve any extra metadata
                            // information that may be present on the edge, like the "Targets" metadata which specifies what
                            // targets to call on the references.
                            ProjectItemInstance newInnerBuildEdge = graphBuilder.Edges[(outerBuildReferencingProject, outerBuild)];

                            if (outerBuildReferencingProject.ProjectReferences.Contains(innerBuild))
                            {
                                ErrorUtilities.VerifyThrow(
                                    graphBuilder.Edges[(outerBuildReferencingProject, innerBuild)]
                                        .ItemType.Equals(
                                            TransitiveReferenceItemName,
                                            StringComparison.OrdinalIgnoreCase),
                                    "Only transitive references may reference inner builds that got generated by outer builds");

                                outerBuildReferencingProject.RemoveReference(innerBuild, graphBuilder.Edges);
                            }

                            outerBuildReferencingProject.AddProjectReference(innerBuild, newInnerBuildEdge, graphBuilder.Edges);
                        }
                    }
                }
            }
        }

        private static IEnumerable<ProjectItemInstance> ConstructInnerBuildReferences(ProjectInstance outerBuild)
        {
            var globalPropertyName = GetInnerBuildPropertyName(outerBuild);
            var globalPropertyValues = GetInnerBuildPropertyValues(outerBuild);

            ErrorUtilities.VerifyThrow(!String.IsNullOrWhiteSpace(globalPropertyName), "Must have an inner build property");
            ErrorUtilities.VerifyThrow(!String.IsNullOrWhiteSpace(globalPropertyValues), "Must have values for the inner build property");

            foreach (var globalPropertyValue in ExpressionShredder.SplitSemiColonSeparatedList(globalPropertyValues))
            {
                yield return new ProjectItemInstance(
                    project: outerBuild,
                    itemType: InnerBuildReferenceItemName,
                    includeEscaped: outerBuild.FullPath,
                    directMetadata: [new KeyValuePair<string, string>(ItemMetadataNames.PropertiesMetadataName, $"{globalPropertyName}={globalPropertyValue}")],
                    definingFileEscaped: outerBuild.FullPath);
            }
        }

        /// <summary>
        ///     Gets the effective global properties for a project reference item.
        /// </summary>
        /// <remarks>
        ///     The behavior of this method should match the logic in the SDK
        /// </remarks>
        private static GlobalPropertyPartsForMSBuildTask ProjectReferenceGlobalPropertiesModifier(
            GlobalPropertyPartsForMSBuildTask defaultParts,
            ProjectItemInstance projectReference)
        {
            // ProjectReference defines yet another metadata name containing properties to undefine. Merge it in if non empty.
            var globalPropertiesToRemove = SplitPropertyNames(projectReference.GetMetadataValue(GlobalPropertiesToRemoveMetadataName));

            var newUndefineProperties = defaultParts.UndefineProperties;

            newUndefineProperties = newUndefineProperties.AddRange(defaultParts.UndefineProperties);
            newUndefineProperties = newUndefineProperties.AddRange(globalPropertiesToRemove);

            newUndefineProperties.Add("InnerBuildProperty");

            var newProperties = defaultParts.Properties;

            // The properties on the project reference supersede the ones from the MSBuild task instead of appending.
            if (newProperties.Count == 0)
            {
                // TODO: Mimic AssignProjectConfiguration's behavior for determining the values for these.
                var setConfigurationString = projectReference.GetMetadataValue(SetConfigurationMetadataName);
                var setPlatformString = projectReference.GetMetadataValue(SetPlatformMetadataName);
                var setTargetFrameworkString = projectReference.GetMetadataValue(SetTargetFrameworkMetadataName);

                if (!String.IsNullOrEmpty(setConfigurationString) || !String.IsNullOrEmpty(setPlatformString) || !String.IsNullOrEmpty(setTargetFrameworkString))
                {
                    newProperties = SplitPropertyNameValuePairs(
                        ItemMetadataNames.PropertiesMetadataName,
                        $"{setConfigurationString};{setPlatformString};{setTargetFrameworkString}").ToImmutableDictionary();
                }
            }

            return new GlobalPropertyPartsForMSBuildTask(newProperties, defaultParts.AdditionalProperties, newUndefineProperties);
        }

        private readonly struct GlobalPropertyPartsForMSBuildTask
        {
            public ImmutableDictionary<string, string> Properties { get; }
            public ImmutableDictionary<string, string> AdditionalProperties { get; }
            public ImmutableList<string> UndefineProperties { get; }

            public GlobalPropertyPartsForMSBuildTask(
                ImmutableDictionary<string, string> properties,
                ImmutableDictionary<string, string> additionalProperties,
                ImmutableList<string> undefineProperties)
            {
                Properties = properties;
                AdditionalProperties = additionalProperties;
                UndefineProperties = undefineProperties;
            }

            public bool AllEmpty()
            {
                return Properties.Count == 0 && AdditionalProperties.Count == 0 && UndefineProperties.Count == 0;
            }

            public GlobalPropertyPartsForMSBuildTask AddPropertyToUndefine(string propertyToUndefine)
            {
                return new GlobalPropertyPartsForMSBuildTask(Properties, AdditionalProperties, UndefineProperties.Add(propertyToUndefine));
            }
        }

        private delegate GlobalPropertyPartsForMSBuildTask GlobalPropertiesModifier(GlobalPropertyPartsForMSBuildTask defaultParts, ProjectItemInstance projectReference);

        /// <summary>
        ///     Gets the effective global properties for an item that will get passed to <see cref="MSBuild.Projects"/>.
        /// </summary>
        /// <remarks>
        ///     The behavior of this method matches the hardcoded behaviour of the msbuild task
        ///     and the <paramref name="globalPropertyModifiers"/> parameter can contain other mutations done at build time in targets / tasks
        /// </remarks>
        private static PropertyDictionary<ProjectPropertyInstance> GetGlobalPropertiesForItem(
            ProjectItemInstance projectReference,
            PropertyDictionary<ProjectPropertyInstance> requesterGlobalProperties,
            bool allowCollectionReuse,
            IEnumerable<GlobalPropertiesModifier> globalPropertyModifiers)
        {
            ErrorUtilities.VerifyThrowInternalNull(projectReference);
            ErrorUtilities.VerifyThrowArgumentNull(requesterGlobalProperties);

            var properties = SplitPropertyNameValuePairs(ItemMetadataNames.PropertiesMetadataName, projectReference.GetMetadataValue(ItemMetadataNames.PropertiesMetadataName));
            var additionalProperties = SplitPropertyNameValuePairs(ItemMetadataNames.AdditionalPropertiesMetadataName, projectReference.GetMetadataValue(ItemMetadataNames.AdditionalPropertiesMetadataName));
            var undefineProperties = SplitPropertyNames(projectReference.GetMetadataValue(ItemMetadataNames.UndefinePropertiesMetadataName));

            var defaultParts = new GlobalPropertyPartsForMSBuildTask(properties.ToImmutableDictionary(), additionalProperties.ToImmutableDictionary(), undefineProperties.ToImmutableList());

            var globalPropertyParts = globalPropertyModifiers?.Aggregate(defaultParts, (currentProperties, modifier) => modifier(currentProperties, projectReference)) ?? defaultParts;

            if (globalPropertyParts.AllEmpty() && allowCollectionReuse)
            {
                return requesterGlobalProperties;
            }

            // Make a copy to avoid mutating the requester
            var globalProperties = new PropertyDictionary<ProjectPropertyInstance>(requesterGlobalProperties);

            // Append and remove properties as specified by the various metadata
            MergeIntoPropertyDictionary(globalProperties, globalPropertyParts.Properties);
            MergeIntoPropertyDictionary(globalProperties, globalPropertyParts.AdditionalProperties);
            RemoveFromPropertyDictionary(globalProperties, globalPropertyParts.UndefineProperties);

            return globalProperties;
        }

        private static void MergeIntoPropertyDictionary(
            PropertyDictionary<ProjectPropertyInstance> destination,
            IReadOnlyDictionary<string, string> source)
        {
            foreach (var pair in source)
            {
                destination[pair.Key] = ProjectPropertyInstance.Create(pair.Key, pair.Value);
            }
        }

        private static IReadOnlyDictionary<string, string> SplitPropertyNameValuePairs(string syntaxName, string propertyNameAndValuesString)
        {
            if (String.IsNullOrEmpty(propertyNameAndValuesString))
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            if (PropertyParser.GetTableWithEscaping(
                null,
                null,
                null,
                propertyNameAndValuesString.Split(PropertySeparator, StringSplitOptions.RemoveEmptyEntries),
                out var propertiesTable))
            {
                return propertiesTable;
            }

            throw new InvalidProjectFileException(
                String.Format(
                    CultureInfo.InvariantCulture,
                    ResourceUtilities.GetResourceString("General.InvalidPropertyError"),
                    syntaxName,
                    propertyNameAndValuesString));
        }

        private static IReadOnlyCollection<string> SplitPropertyNames(string propertyNamesString)
        {
            if (String.IsNullOrEmpty(propertyNamesString))
            {
                return ImmutableArray<string>.Empty;
            }

            return propertyNamesString.Split(PropertySeparator, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void RemoveFromPropertyDictionary(
            PropertyDictionary<ProjectPropertyInstance> properties,
            IReadOnlyCollection<string> propertyNamesToRemove)
        {
            foreach (var propertyName in propertyNamesToRemove)
            {
                properties.Remove(propertyName);
            }
        }

        public readonly struct TargetsToPropagate
        {
            private readonly ImmutableList<TargetSpecification> _outerBuildTargets;
            private readonly ImmutableList<TargetSpecification> _allTargets;

            private TargetsToPropagate(ImmutableList<TargetSpecification> outerBuildTargets, ImmutableList<TargetSpecification> nonOuterBuildTargets)
            {
                _outerBuildTargets = outerBuildTargets;

                // This is used as the list of entry targets for both inner builds and non-multitargeting projects.
                // It represents the concatenation of outer build targets and non outer build targets, in this order.
                // Non-multitargeting projects use these targets because they act as both outer and inner builds.
                _allTargets = outerBuildTargets.AddRange(nonOuterBuildTargets);
            }

            /// <summary>
            /// Given a project and a set of entry targets the project would get called with,
            /// parse the project's project reference target specification and compute how the target would call its references.
            ///
            /// The calling code should then call <see cref="GetApplicableTargetsForReference"/> for each of the project's references
            /// to get the concrete targets for each reference.
            /// </summary>
            /// <param name="project">Project containing the PRT protocol</param>
            /// <param name="entryTargets">Targets with which <paramref name="project"/> will get called</param>
            /// <returns></returns>
            public static TargetsToPropagate FromProjectAndEntryTargets(ProjectInstance project, ImmutableList<string> entryTargets)
            {
                ImmutableList<TargetSpecification>.Builder targetsForOuterBuild = ImmutableList.CreateBuilder<TargetSpecification>();
                ImmutableList<TargetSpecification>.Builder targetsForInnerBuild = ImmutableList.CreateBuilder<TargetSpecification>();

                ICollection<ProjectItemInstance> projectReferenceTargets = project.GetItems(ItemTypeNames.ProjectReferenceTargets);

                foreach (string entryTarget in entryTargets)
                {
                    foreach (ProjectItemInstance projectReferenceTarget in projectReferenceTargets)
                    {
                        if (projectReferenceTarget.EvaluatedInclude.Equals(entryTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            string targetsMetadataValue = projectReferenceTarget.GetMetadataValue(ItemMetadataNames.ProjectReferenceTargetsMetadataName);
                            bool skipNonexistentTargets = MSBuildStringIsTrue(projectReferenceTarget.GetMetadataValue("SkipNonexistentTargets"));
                            bool targetsAreForOuterBuild = MSBuildStringIsTrue(projectReferenceTarget.GetMetadataValue(ProjectReferenceTargetIsOuterBuildMetadataName));
                            TargetSpecification[] targets = ExpressionShredder.SplitSemiColonSeparatedList(targetsMetadataValue)
                                .Select(t => new TargetSpecification(t, skipNonexistentTargets)).ToArray();
                            if (targetsAreForOuterBuild)
                            {
                                targetsForOuterBuild.AddRange(targets);
                            }
                            else
                            {
                                targetsForInnerBuild.AddRange(targets);
                            }
                        }
                    }
                }

                return new TargetsToPropagate(targetsForOuterBuild.ToImmutable(), targetsForInnerBuild.ToImmutable());
            }

            public ImmutableList<string> GetApplicableTargetsForReference(ProjectGraphNode projectGraphNode)
            {
                ImmutableList<string> RemoveNonexistentTargetsIfSkippable(ImmutableList<TargetSpecification> targets)
                {
                    // Keep targets that are non-skippable or that exist but are skippable.
                    return targets
                        .Where(t => !t.SkipIfNonexistent || projectGraphNode.ProjectInstance.Targets.ContainsKey(t.Target))
                        .Select(t => t.Target)
                        .ToImmutableList();
                }

                return projectGraphNode.ProjectType switch
                {
                    ProjectType.InnerBuild => RemoveNonexistentTargetsIfSkippable(_allTargets),
                    ProjectType.OuterBuild => RemoveNonexistentTargetsIfSkippable(_outerBuildTargets),
                    ProjectType.NonMultitargeting => RemoveNonexistentTargetsIfSkippable(_allTargets),
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
        }

        public bool RequiresTransitiveProjectReferences(ProjectGraphNode projectGraphNode)
        {
            // Outer builds do not get edges based on ProjectReference or their transitive closure, only inner builds do.
            if (projectGraphNode.ProjectType == ProjectType.OuterBuild)
            {
                return false;
            }

            ProjectInstance projectInstance = projectGraphNode.ProjectInstance;

            // special case for Quickbuild which updates msbuild binaries independent of props/targets. Remove this when all QB repos will have
            // migrated to new enough Visual Studio versions whose Microsoft.Managed.After.Targets enable transitive references.
            if (string.IsNullOrWhiteSpace(projectInstance.GetEngineRequiredPropertyValue(AddTransitiveProjectReferencesInStaticGraphPropertyName)) &&
                MSBuildStringIsTrue(projectInstance.GetEngineRequiredPropertyValue(PropertyNames.UsingMicrosoftNETSdk)) &&
                MSBuildStringIsFalse(projectInstance.GetEngineRequiredPropertyValue("DisableTransitiveProjectReferences")))
            {
                return true;
            }

            return MSBuildStringIsTrue(
                projectInstance.GetEngineRequiredPropertyValue(AddTransitiveProjectReferencesInStaticGraphPropertyName));
        }

        private static bool MSBuildStringIsTrue(string msbuildString) =>
            ConversionUtilities.ConvertStringToBool(msbuildString, nullOrWhitespaceIsFalse: true);

        private static bool MSBuildStringIsFalse(string msbuildString) => !MSBuildStringIsTrue(msbuildString);
    }
}
