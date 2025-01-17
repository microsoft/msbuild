// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck.Checks;
internal class TargetFrameworkUnexpectedCheck : Check
{
    private const string RuleId = "BC0108";
    public static CheckRule SupportedRule = new CheckRule(RuleId, "TargetFrameworkUnexpected",
        ResourceUtilities.GetResourceString("BuildCheck_BC0108_Title")!,
        ResourceUtilities.GetResourceString("BuildCheck_BC0108_MessageFmt")!,
        new CheckConfiguration() { RuleId = RuleId, Severity = CheckResultSeverity.Warning });

    public override string FriendlyName => "MSBuild.TargetFrameworkUnexpected";

    public override IReadOnlyList<CheckRule> SupportedRules { get; } = [SupportedRule];

    public override void Initialize(ConfigurationContext configurationContext)
    {
        /* This is it - no custom configuration */
    }

    public override void RegisterActions(IBuildCheckRegistrationContext registrationContext)
    {
        registrationContext.RegisterEvaluatedPropertiesAction(EvaluatedPropertiesAction);
        registrationContext.RegisterEvaluatedItemsAction(EvaluatedItemsAction);
    }

    internal override bool IsBuiltIn => true;

    private readonly HashSet<string> _projectsSeen = new(MSBuildNameIgnoreCaseComparer.Default);
    private string? _tfm;

    private void EvaluatedPropertiesAction(BuildCheckDataContext<EvaluatedPropertiesCheckData> context)
    {
        // Resetting state for the next project.
        _tfm = null;

        // See CopyAlwaysCheck.EvaluatedPropertiesAction for explanation. 
        if (_projectsSeen.Contains(context.Data.ProjectFilePath))
        {
            return;
        }

        string? frameworks = null;
        string? framework = null;
        // TargetFramework(s) is specified
        if ((context.Data.EvaluatedProperties.TryGetValue(PropertyNames.TargetFrameworks, out frameworks) ||
             context.Data.EvaluatedProperties.TryGetValue(PropertyNames.TargetFramework, out framework)) &&
            !string.IsNullOrEmpty(framework ?? frameworks)
            &&
            !IsSdkStyleProject(context.Data.EvaluatedProperties) && !IsCppCliProject(context.Data.EvaluatedProperties)
            )
        {
            // Indicating that to the EvaluatedItemsAction, that if this project is recognized as manged - we should emit diagnostics.
            _tfm = framework ?? frameworks;
        }

        bool IsSdkStyleProject(IReadOnlyDictionary<string, string> evaluatedProperties)
            => evaluatedProperties.TryGetValue(PropertyNames.UsingMicrosoftNETSdk, out string? usingSdkStr) &&
               usingSdkStr.IsMSBuildTrueString();

        bool IsCppCliProject(IReadOnlyDictionary<string, string> evaluatedProperties)
            => evaluatedProperties.TryGetValue("CLRSupport", out string? clrSupportStr) &&
               MSBuildNameIgnoreCaseComparer.Default.Equals(clrSupportStr, "NetCore");
    }

    private void EvaluatedItemsAction(BuildCheckDataContext<EvaluatedItemsCheckData> context)
    {
        // Neither TargetFrameworks nor TargetFramework is specified, or the project is not Sdk-style nor C++/CLI project.
        if (_tfm == null)
        {
            return;
        }

        // We want to avoid repeated checking of a same project (as it might be evaluated multiple times)
        //  for this reason we use a hashset with already seen projects.
        if (!_projectsSeen.Add(context.Data.ProjectFilePath))
        {
            return;
        }

        foreach (ItemData itemData in context.Data.EnumerateItemsOfType(ItemNames.ProjectCapability))
        {
            if (MSBuildNameIgnoreCaseComparer.Default.Equals(itemData.EvaluatedInclude, ItemMetadataNames.managed))
            {
                // {0} specifies 'TargetFramework(s)' property value
                context.ReportResult(BuildCheckResult.Create(
                    SupportedRule,
                    // Populating precise location tracked via https://github.com/dotnet/msbuild/issues/10383
                    ElementLocation.EmptyLocation,
                    Path.GetFileName(context.Data.ProjectFilePath),
                    _tfm));

                break;
            }
        }
    }
}
