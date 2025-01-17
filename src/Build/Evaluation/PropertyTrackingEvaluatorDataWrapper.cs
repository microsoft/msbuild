// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Components.Logging;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Wraps an existing <see cref="IEvaluatorData{P,I,M,D}"/> allowing the property usage to be tracked.
    /// </summary>
    /// <typeparam name="P">The type of properties to be produced.</typeparam>
    /// <typeparam name="I">The type of items to be produced.</typeparam>
    /// <typeparam name="M">The type of metadata on those items.</typeparam>
    /// <typeparam name="D">The type of item definitions to be produced.</typeparam>
    internal class PropertyTrackingEvaluatorDataWrapper<P, I, M, D> : IEvaluatorData<P, I, M, D>
        where P : class, IProperty, IEquatable<P>, IValued
        where I : class, IItem
        where M : class, IMetadatum
        where D : class, IItemDefinition<M>
    {
        private readonly IEvaluatorData<P, I, M, D> _wrapped;
        private readonly HashSet<string> _overwrittenEnvironmentVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly EvaluationLoggingContext _evaluationLoggingContext;
        private readonly PropertyTrackingSetting _settings;

        /// <summary>
        /// Creates an instance of the PropertyTrackingEvaluatorDataWrapper class.
        /// </summary>
        /// <param name="dataToWrap">The underlying <see cref="IEvaluatorData{P,I,M,D}"/> to wrap for property tracking.</param>
        /// <param name="evaluationLoggingContext">The <see cref="EvaluationLoggingContext"/> used to log relevant events.</param>
        /// <param name="settingValue">Property tracking setting value</param>
        public PropertyTrackingEvaluatorDataWrapper(IEvaluatorData<P, I, M, D> dataToWrap, EvaluationLoggingContext evaluationLoggingContext, int settingValue)
        {
            ErrorUtilities.VerifyThrowInternalNull(dataToWrap);
            ErrorUtilities.VerifyThrowInternalNull(evaluationLoggingContext);

            _wrapped = dataToWrap;
            _evaluationLoggingContext = evaluationLoggingContext;
            _settings = (PropertyTrackingSetting)settingValue;
        }

        #region IEvaluatorData<> members with tracking-related code in them.

        /// <summary>
        /// Returns a property with the specified name, or null if it was not found.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The property.</returns>
        public P GetProperty(string name)
        {
            P prop = _wrapped.GetProperty(name);
            if (IsPropertyReadTrackingRequested)
            {
                this.TrackPropertyRead(name, prop);
            }

            return prop;
        }

        /// <summary>
        /// Returns a property with the specified name, or null if it was not found.
        /// Name is the segment of the provided string with the provided start and end indexes.
        /// </summary>
        public P GetProperty(string name, int startIndex, int endIndex)
        {
            P prop = _wrapped.GetProperty(name, startIndex, endIndex);
            if (IsPropertyReadTrackingRequested)
            {
                this.TrackPropertyRead(name.Substring(startIndex, endIndex - startIndex + 1), prop);
            }

            return prop;
        }

        /// <summary>
        /// Sets a property which does not come from the Xml.
        /// </summary>
        public P SetProperty(
            string name,
            string evaluatedValueEscaped,
            bool isGlobalProperty,
            bool mayBeReserved,
            LoggingContext loggingContext,
            bool isEnvironmentVariable = false,
            bool isCommandLineProperty = false)
        {
            P? originalProperty = _wrapped.GetProperty(name);
            P newProperty = _wrapped.SetProperty(name, evaluatedValueEscaped, isGlobalProperty, mayBeReserved, _evaluationLoggingContext, isEnvironmentVariable, isCommandLineProperty);

            this.TrackPropertyWrite(
                originalProperty,
                newProperty,
                null,
                this.DeterminePropertySource(isGlobalProperty, mayBeReserved, isEnvironmentVariable, isCommandLineProperty),
                loggingContext);

            return newProperty;
        }

        /// <summary>
        /// Sets a property which comes from the Xml.
        /// Predecessor is any immediately previous property that was overridden by this one during evaluation.
        /// This would include all properties with the same name that lie above in the logical
        /// project file, and whose conditions evaluated to true.
        /// If there are none above this is null.
        /// </summary>
        public P SetProperty(ProjectPropertyElement propertyElement, string evaluatedValueEscaped, LoggingContext loggingContext)
        {
            P? originalProperty = _wrapped.GetProperty(propertyElement.Name);
            P newProperty = _wrapped.SetProperty(propertyElement, evaluatedValueEscaped, loggingContext);

            this.TrackPropertyWrite(
                originalProperty,
                newProperty,
                propertyElement.Location,
                PropertySource.Xml,
                loggingContext);

            return newProperty;
        }

        #endregion

        #region IEvaluatorData<> members that are forwarded directly to wrapped object.
        public ICollection<I> GetItems(string itemType) => _wrapped.GetItems(itemType);
        public int EvaluationId { get => _wrapped.EvaluationId; set => _wrapped.EvaluationId = value; }
        public string Directory => _wrapped.Directory;
        public TaskRegistry TaskRegistry { get => _wrapped.TaskRegistry; set => _wrapped.TaskRegistry = value; }
        public Toolset Toolset => _wrapped.Toolset;
        public string SubToolsetVersion => _wrapped.SubToolsetVersion;
        public string ExplicitToolsVersion => _wrapped.ExplicitToolsVersion;
        public PropertyDictionary<ProjectPropertyInstance> GlobalPropertiesDictionary => _wrapped.GlobalPropertiesDictionary;
        public ISet<string> GlobalPropertiesToTreatAsLocal => _wrapped.GlobalPropertiesToTreatAsLocal;
        public List<string> InitialTargets { get => _wrapped.InitialTargets; set => _wrapped.InitialTargets = value; }
        public List<string> DefaultTargets { get => _wrapped.DefaultTargets; set => _wrapped.DefaultTargets = value; }
        public IDictionary<string, List<TargetSpecification>> BeforeTargets { get => _wrapped.BeforeTargets; set => _wrapped.BeforeTargets = value; }
        public IDictionary<string, List<TargetSpecification>> AfterTargets { get => _wrapped.AfterTargets; set => _wrapped.AfterTargets = value; }
        public Dictionary<string, List<string>> ConditionedProperties => _wrapped.ConditionedProperties;
        public bool ShouldEvaluateForDesignTime => _wrapped.ShouldEvaluateForDesignTime;
        public bool CanEvaluateElementsWithFalseConditions => _wrapped.CanEvaluateElementsWithFalseConditions;
        public PropertyDictionary<P> Properties => _wrapped.Properties;
        public IEnumerable<D> ItemDefinitionsEnumerable => _wrapped.ItemDefinitionsEnumerable;
        public IItemDictionary<I> Items => _wrapped.Items;
        public List<ProjectItemElement> EvaluatedItemElements => _wrapped.EvaluatedItemElements;
        public PropertyDictionary<ProjectPropertyInstance> EnvironmentVariablePropertiesDictionary => _wrapped.EnvironmentVariablePropertiesDictionary;
        public void InitializeForEvaluation(IToolsetProvider toolsetProvider, EvaluationContext evaluationContext, LoggingContext loggingContext) => _wrapped.InitializeForEvaluation(toolsetProvider, evaluationContext, loggingContext);
        public void FinishEvaluation() => _wrapped.FinishEvaluation();
        public void AddItem(I item) => _wrapped.AddItem(item);
        public void AddItemIgnoringCondition(I item) => _wrapped.AddItemIgnoringCondition(item);
        public IItemDefinition<M> AddItemDefinition(string itemType) => _wrapped.AddItemDefinition(itemType);
        public void AddToAllEvaluatedPropertiesList(P property) => _wrapped.AddToAllEvaluatedPropertiesList(property);
        public void AddToAllEvaluatedItemDefinitionMetadataList(M itemDefinitionMetadatum) => _wrapped.AddToAllEvaluatedItemDefinitionMetadataList(itemDefinitionMetadatum);
        public void AddToAllEvaluatedItemsList(I item) => _wrapped.AddToAllEvaluatedItemsList(item);
        public IItemDefinition<M> GetItemDefinition(string itemType) => _wrapped.GetItemDefinition(itemType);
        public ProjectTargetInstance GetTarget(string targetName) => _wrapped.GetTarget(targetName);
        public void AddTarget(ProjectTargetInstance target) => _wrapped.AddTarget(target);
        public void RecordImport(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated, SdkResult sdkResult) => _wrapped.RecordImport(importElement, import, versionEvaluated, sdkResult);
        public void RecordImportWithDuplicates(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated) => _wrapped.RecordImportWithDuplicates(importElement, import, versionEvaluated);
        public string ExpandString(string unexpandedValue) => _wrapped.ExpandString(unexpandedValue);
        public bool EvaluateCondition(string condition) => _wrapped.EvaluateCondition(condition);
        #endregion

        #region Private Methods...

        private bool IsPropertyReadTrackingRequested
            => IsEnvironmentVariableReadTrackingRequested ||
               (_settings & PropertyTrackingSetting.UninitializedPropertyRead) ==
               PropertyTrackingSetting.UninitializedPropertyRead;

        private bool IsEnvironmentVariableReadTrackingRequested
            => (_settings & PropertyTrackingSetting.EnvironmentVariableRead) ==
               PropertyTrackingSetting.EnvironmentVariableRead;

        /// <summary>
        /// Logic containing what to do when a property is read.
        /// </summary>
        /// <param name="name">The name of the property.</param>
        /// <param name="property">The value of the property that was read (null if there is no value).</param>
        private void TrackPropertyRead(string name, P property)
        {
            // MSBuild looks up a property called "InnerBuildProperty". If that isn't present,
            // an empty string is returned and it then attempts to look up the value for that property
            // (which is an empty string). Thus this check.
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // If a property matches the name of an environment variable, but has NOT been overwritten by a non-environment-variable property
            // track it as an environment variable read.
            if (IsEnvironmentVariableReadTrackingRequested && _wrapped.EnvironmentVariablePropertiesDictionary.Contains(name) && !_overwrittenEnvironmentVariables.Contains(name))
            {
                this.TrackEnvironmentVariableRead(name);
            }
            else if (property == null)
            {
                this.TrackUninitializedPropertyRead(name);
            }
        }

        /// <summary>
        /// Logs an EnvironmentVariableRead event.
        /// </summary>
        /// <param name="name">The name of the environment variable read.</param>
        private void TrackEnvironmentVariableRead(string name)
        {
            if ((_settings & PropertyTrackingSetting.EnvironmentVariableRead) != PropertyTrackingSetting.EnvironmentVariableRead)
            {
                return;
            }

            var args = new EnvironmentVariableReadEventArgs(
                name,
                ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("EnvironmentVariableRead", name),
                string.Empty,
                0,
                0);
            args.BuildEventContext = _evaluationLoggingContext.BuildEventContext;

            _evaluationLoggingContext.LogBuildEvent(args);
        }

        /// <summary>
        /// Logs an UninitializedPropertyRead event.
        /// </summary>
        /// <param name="name">The name of the uninitialized property read.</param>
        private void TrackUninitializedPropertyRead(string name)
        {
            if ((_settings & PropertyTrackingSetting.UninitializedPropertyRead) != PropertyTrackingSetting.UninitializedPropertyRead)
            {
                return;
            }

            var args = new UninitializedPropertyReadEventArgs(
                name,
                ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("UninitializedPropertyRead", name));
            args.BuildEventContext = _evaluationLoggingContext.BuildEventContext;

            _evaluationLoggingContext.LogBuildEvent(args);
        }

        private void TrackPropertyWrite(
            P? predecessor,
            P property,
            IElementLocation? location,
            PropertySource source,
            LoggingContext loggingContext)
        {
            string name = property.Name;

            loggingContext.ProcessPropertyWrite(new PropertyWriteInfo(property.Name, string.IsNullOrEmpty(property.EscapedValue), location));

            if (predecessor == null)
            {
                // If this property had no previous value, then track an initial value.
                TrackPropertyInitialValueSet(property, source, location);
            }
            else
            {
                // There was a previous value, and it might have been changed. Track that.
                TrackPropertyReassignment(predecessor, property, location);
            }

            // If this property was an environment variable but no longer is, track it.
            if (IsEnvironmentVariableReadTrackingRequested && _wrapped.EnvironmentVariablePropertiesDictionary.Contains(name) && source != PropertySource.EnvironmentVariable)
            {
                _overwrittenEnvironmentVariables.Add(name);
            }
        }

        /// <summary>
        /// If the property's initial value is set, it logs a PropertyInitialValueSet event.
        /// </summary>
        /// <param name="property">The property being set.</param>
        /// <param name="source">The source of the property.</param>
        /// <param name="location">The exact location of the property. Can be null if comes not form xml.</param>
        private void TrackPropertyInitialValueSet(P property, PropertySource source, IElementLocation? location)
        {
            if ((_settings & PropertyTrackingSetting.PropertyInitialValueSet) != PropertyTrackingSetting.PropertyInitialValueSet)
            {
                return;
            }

            var args = new PropertyInitialValueSetEventArgs(
                property.Name,
                property.EvaluatedValue,

                // If the property is from XML, we don't need property source since a full location is available.
                location == null ? EnumUtilities.GetEnumString(source) : string.Empty,
                location?.File,
                location?.Line ?? 0,
                location?.Column ?? 0,
                ResourceUtilities.GetResourceString("PropertyAssignment"))
            { BuildEventContext = _evaluationLoggingContext.BuildEventContext, };

            _evaluationLoggingContext.LogBuildEvent(args);
        }

        /// <summary>
        /// If the property's value has changed, it logs a PropertyReassignment event.
        /// </summary>
        /// <param name="predecessor">The property's preceding state. Null if none.</param>
        /// <param name="property">The property's current state.</param>
        /// <param name="location">The location of this property's reassignment.</param>
        private void TrackPropertyReassignment(P? predecessor, P property, IElementLocation? location)
        {
            if (MSBuildNameIgnoreCaseComparer.Default.Equals(property.Name, "MSBuildAllProjects"))
            {
                // There's a huge perf cost to logging this and it increases the binlog size significantly.
                // Meanwhile the usefulness of logging this is very low.
                return;
            }

            string newValue = property.EvaluatedValue;
            string? oldValue = predecessor?.EvaluatedValue;
            if (newValue == oldValue)
            {
                return;
            }

            // Either we want to specifically track property reassignments
            // or we do not want to track nothing - in which case the prop reassignment is enabled by default.
            if ((_settings & PropertyTrackingSetting.PropertyReassignment) == PropertyTrackingSetting.PropertyReassignment ||
                (_settings == 0 && ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave17_10)))
            {
                var args = new PropertyReassignmentEventArgs(
                    property.Name,
                    oldValue,
                    newValue,
                    location?.File,
                    location?.Line ?? 0,
                    location?.Column ?? 0,
                    message: ResourceUtilities.GetResourceString("PropertyReassignment"))
                { BuildEventContext = _evaluationLoggingContext.BuildEventContext, };

                _evaluationLoggingContext.LogBuildEvent(args);
            }
            else
            {
                _evaluationLoggingContext.LogComment(
                    MessageImportance.Low,
                    "PropertyReassignment",
                    property.Name,
                    newValue,
                    oldValue,
                    location);
            }
        }

        /// <summary>
        /// Determines the source of a property given the variables SetProperty arguments provided. This logic follows what's in <see cref="Evaluator{P,I,M,D}"/>.
        /// </summary>
        private PropertySource DeterminePropertySource(bool isGlobalProperty, bool mayBeReserved, bool isEnvironmentVariable, bool isCommandLineProperty)
        {
            if (isEnvironmentVariable)
            {
                return PropertySource.EnvironmentVariable;
            }

            if (isGlobalProperty)
            {
                return isCommandLineProperty ? PropertySource.CommandLine : PropertySource.Global;
            }

            return mayBeReserved ? PropertySource.BuiltIn : PropertySource.Toolset;
        }

        #endregion

        /// <summary>
        /// The available sources for a property.
        /// </summary>
        private enum PropertySource
        {
            Xml,
            BuiltIn,
            Global,
            Toolset,
            EnvironmentVariable,
            CommandLine,
        }
    }

    [Flags]
    internal enum PropertyTrackingSetting
    {
        None = 0,

        PropertyReassignment = 1,
        PropertyInitialValueSet = 1 << 1,
        EnvironmentVariableRead = 1 << 2,
        UninitializedPropertyRead = 1 << 3,

        All = PropertyReassignment | PropertyInitialValueSet | EnvironmentVariableRead | UninitializedPropertyRead
    }
}
