﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.BuildCheck.Infrastructure;
using Microsoft.Build.Construction;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck.Checks;

internal sealed class NoEnvironmentVariablePropertyCheck : Check, IDisposable
{
    public static CheckRule SupportedRule = new CheckRule(
                "BC0103",
                "NoEnvironmentVariablePropertyCheck",
                "No implicit property derived from an environment variable should be used during the build",
                "Property is derived from environment variable: {0}. Properties should be passed explicitly using the /p option.",
                new CheckConfiguration() { Severity = CheckResultSeverity.Suggestion });

    private const string RuleId = "BC0103";

    private const string VerboseEnvVariableOutputKey = "allow_displaying_environment_variable_value";

    private readonly List<BuildCheckResult> _buildCheckResults = new List<BuildCheckResult>();

    private BuildCheckDataContext<EnvironmentVariableCheckData>? _dataContext;

    /// <summary>
    /// Contains the list of reported environment variables.
    /// </summary>
    private readonly HashSet<EnvironmentVariableIdentityKey> _environmentVariablesReported = new HashSet<EnvironmentVariableIdentityKey>();

    private bool _isVerboseEnvVarOutput;
    private EvaluationCheckScope _scope;

    public override string FriendlyName => "MSBuild.NoEnvironmentVariablePropertyCheck";

    public override IReadOnlyList<CheckRule> SupportedRules { get; } = [SupportedRule];

    public override void Initialize(ConfigurationContext configurationContext)
    {
        _scope = configurationContext.CheckConfig[0].EvaluationCheckScope;
        foreach (CustomConfigurationData customConfigurationData in configurationContext.CustomConfigurationData)
        {
            bool? isVerboseEnvVarOutput = GetVerboseEnvVarOutputConfig(customConfigurationData, RuleId);
            _isVerboseEnvVarOutput = isVerboseEnvVarOutput.HasValue && isVerboseEnvVarOutput.Value;
        }

        CheckScopeClassifier.NotifyOnScopingReadiness += (string? projectFilePath) =>
        {
            foreach (BuildCheckResult result in _buildCheckResults)
            {
                if (!CheckScopeClassifier.IsActionInObservedScope(_scope, result.Location.File, projectFilePath ?? string.Empty))
                {
                    continue;
                }

                _dataContext?.ReportResult(result);
            }
        };
    }

    public override void RegisterActions(IBuildCheckRegistrationContext registrationContext) => registrationContext.RegisterEnvironmentVariableReadAction(ProcessEnvironmentVariableReadAction);

    private void ProcessEnvironmentVariableReadAction(BuildCheckDataContext<EnvironmentVariableCheckData> context)
    {
        if (context.Data.EvaluatedEnvironmentVariables.Count != 0)
        {
            foreach (var envVariableData in context.Data.EvaluatedEnvironmentVariables)
            {
                EnvironmentVariableIdentityKey identityKey = new(envVariableData.Key, envVariableData.Value.Location);
                if (!_environmentVariablesReported.Contains(identityKey))
                {
                    if (_isVerboseEnvVarOutput)
                    {
                        _buildCheckResults.Add(BuildCheckResult.Create(
                            SupportedRule,
                            ElementLocation.Create(envVariableData.Value.Location.File, envVariableData.Value.Location.Line, envVariableData.Value.Location.Column),
                            $"'{envVariableData.Key}' with value: '{envVariableData.Value.EnvVarValue}'"));
                    }
                    else
                    {
                        _buildCheckResults.Add(BuildCheckResult.Create(
                            SupportedRule,
                            ElementLocation.Create(envVariableData.Value.Location.File, envVariableData.Value.Location.Line, envVariableData.Value.Location.Column),
                            $"'{envVariableData.Key}'"));
                    }

                    _environmentVariablesReported.Add(identityKey);
                }
            }

            _dataContext = context;
        }
    }

    private static bool? GetVerboseEnvVarOutputConfig(CustomConfigurationData customConfigurationData, string ruleId) => customConfigurationData.RuleId.Equals(ruleId, StringComparison.InvariantCultureIgnoreCase)
            && (customConfigurationData.ConfigurationData?.TryGetValue(VerboseEnvVariableOutputKey, out string? configVal) ?? false)
            ? bool.Parse(configVal)
            : null;

    internal class EnvironmentVariableIdentityKey(string environmentVariableName, IMSBuildElementLocation location) : IEquatable<EnvironmentVariableIdentityKey>
    {
        public string EnvironmentVariableName { get; } = environmentVariableName;

        public IMSBuildElementLocation Location { get; } = location;

        public override bool Equals(object? obj) => Equals(obj as EnvironmentVariableIdentityKey);

        public bool Equals(EnvironmentVariableIdentityKey? other) =>
            other != null &&
            EnvironmentVariableName == other.EnvironmentVariableName &&
            Location.File == other.Location.File &&
            Location.Line == other.Location.Line &&
            Location.Column == other.Location.Column;

        public override int GetHashCode()
        {
            int hashCode = 17;
            hashCode = hashCode * 31 + (Location.File != null ? Location.File.GetHashCode() : 0);
            hashCode = hashCode * 31 + Location.Line.GetHashCode();
            hashCode = hashCode * 31 + Location.Column.GetHashCode();

            return hashCode;
        }
    }
}
