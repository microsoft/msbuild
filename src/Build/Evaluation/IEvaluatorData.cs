// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// An interface for objects which the Evaluator can use as a destination for evaluation of ProjectRootElement.
    /// </summary>
    /// <typeparam name="P">The type of properties to be produced.</typeparam>
    /// <typeparam name="I">The type of items to be produced.</typeparam>
    /// <typeparam name="M">The type of metadata on those items.</typeparam>
    /// <typeparam name="D">The type of item definitions to be produced.</typeparam>
    internal interface IEvaluatorData<P, I, M, D> : IPropertyProvider<P>, IItemProvider<I>
        where P : class, IProperty, IEquatable<P>, IValued
        where I : class, IItem
        where M : class, IMetadatum
        where D : class, IItemDefinition<M>
    {
        /// <summary>
        /// The ID of this evaluation
        /// </summary>
        int EvaluationId
        {
            get;
            set;
        }

        /// <summary>
        /// The (project) directory that should be used during evaluation
        /// </summary>
        string Directory
        {
            get;
        }

        /// <summary>
        /// Task classes and locations known to this project.
        /// This is the project-specific task registry, which is consulted before
        /// the toolset's task registry.
        /// </summary>
        TaskRegistry TaskRegistry
        {
            get;
            set;
        }

        /// <summary>
        /// The toolset data used during evaluation, and which should be used for build.
        /// </summary>
        Toolset Toolset
        {
            get;
        }

        /// <summary>
        /// The sub-toolset version that should be used with this toolset to determine
        /// the full set of properties to be used by the build.
        /// </summary>
        string SubToolsetVersion
        {
            get;
        }

        /// <summary>
        /// The externally specified tools version to evaluate with, if any.
        /// For example, the tools version from a /tv switch.
        /// This is not the tools version specified on the Project tag, if any.
        /// May be null.
        /// </summary>
        string ExplicitToolsVersion
        {
            get;
        }

        /// <summary>
        /// Gets the global properties
        /// </summary>
        PropertyDictionary<ProjectPropertyInstance> GlobalPropertiesDictionary
        {
            get;
        }

        /// <summary>
        /// List of names of the properties that, while global, are still treated as overridable
        /// </summary>
        ISet<string> GlobalPropertiesToTreatAsLocal
        {
            get;
        }

        /// <summary>
        /// Sets the initial targets
        /// </summary>
        List<string> InitialTargets
        {
            get;
            set;
        }

        /// <summary>
        /// Sets the default targets
        /// </summary>
        List<string> DefaultTargets
        {
            get;
            set;
        }

        /// <summary>
        /// Sets or retrieves the list of targets which run before the keyed target.
        /// </summary>
        IDictionary<string, List<TargetSpecification>> BeforeTargets
        {
            get;
            set;
        }

        /// <summary>
        /// Sets or retrieves the list of targets which run after the keyed target.
        /// </summary>
        IDictionary<string, List<TargetSpecification>> AfterTargets
        {
            get;
            set;
        }

        /// <summary>
        /// List of possible values for properties inferred from certain conditions,
        /// keyed by the property name.
        /// </summary>
        Dictionary<string, List<string>> ConditionedProperties
        {
            get;
        }

        /// <summary>
        /// Whether evaluation should collect items ignoring condition,
        /// as well as items respecting condition; and collect
        /// conditioned properties, as well as regular properties
        /// </summary>
        bool ShouldEvaluateForDesignTime
        {
            get;
        }

        /// <summary>
        /// Tells the evaluator whether it should evaluate elements with false conditions
        /// </summary>
        bool CanEvaluateElementsWithFalseConditions
        {
            get;
        }

        /// <summary>
        /// Enumerator over properties in this project.
        /// Exposed for debugging display.
        /// </summary>
        PropertyDictionary<P> Properties
        {
            get;
        }

        /// <summary>
        /// Enumerator over all item definitions.
        /// Exposed for debugging display.
        /// Ideally the dictionary would be exposed, but there are
        /// covariance problems. (A dictionary of Key, Value cannot be upcast
        /// to a Dictionary of Key, IValue).
        /// </summary>
        IEnumerable<D> ItemDefinitionsEnumerable
        {
            get;
        }

        /// <summary>
        /// Enumerator over all items.
        /// Exposed for debugging display.
        /// Ideally the dictionary would be exposed, but there are
        /// covariance problems. (A dictionary of Key, Value cannot be upcast
        /// to a Dictionary of Key, IValue).
        /// </summary>
        IItemDictionary<I> Items
        {
            get;
        }

        /// <summary>
        /// Evaluation ordered list of project item elements that were evaluated by the Evaluator
        /// It means that both the item element's condition and the item group element's conditions evaluated to true
        /// </summary>
        List<ProjectItemElement> EvaluatedItemElements
        {
            get;
        }

        /// <summary>
        /// A dictionary of all of the environment variable properties.
        /// </summary>
        PropertyDictionary<ProjectPropertyInstance> EnvironmentVariablePropertiesDictionary { get; }

        /// <summary>
        /// Prepares the data block for a new evaluation pass
        /// </summary>
        void InitializeForEvaluation(IToolsetProvider toolsetProvider, EvaluationContext evaluationContext, LoggingContext loggingContext);

        /// <summary>
        /// Indicates to the data block that evaluation has completed,
        /// so for example it can mark datastructures read-only.
        /// </summary>
        void FinishEvaluation();

        /// <summary>
        /// Adds a new item
        /// </summary>
        void AddItem(I item);

        /// <summary>
        /// Adds a new item to the collection of all items ignoring condition
        /// </summary>
        void AddItemIgnoringCondition(I item);

        /// <summary>
        /// Adds a new item definition
        /// </summary>
        IItemDefinition<M> AddItemDefinition(string itemType);

        /// <summary>
        /// Properties encountered during evaluation. These are read during the first evaluation pass.
        /// Unlike those returned by the Properties property, these are ordered, and include any properties that
        /// were subsequently overridden by others with the same name. It does not include any
        /// properties whose conditions did not evaluate to true.
        /// </summary>
        void AddToAllEvaluatedPropertiesList(P property);

        /// <summary>
        /// Item definition metadata encountered during evaluation. These are read during the second evaluation pass.
        /// Unlike those returned by the ItemDefinitions property, these are ordered, and include any metadata that
        /// were subsequently overridden by others with the same name and item type. It does not include any
        /// elements whose conditions did not evaluate to true.
        /// </summary>
        void AddToAllEvaluatedItemDefinitionMetadataList(M itemDefinitionMetadatum);

        /// <summary>
        /// Items encountered during evaluation. These are read during the third evaluation pass.
        /// Unlike those returned by the Items property, these are ordered.
        /// It does not include any elements whose conditions did not evaluate to true.
        /// It does not include any items added since the last evaluation.
        /// </summary>
        void AddToAllEvaluatedItemsList(I item);

        /// <summary>
        /// Retrieves an existing item definition, if any.
        /// </summary>
        IItemDefinition<M> GetItemDefinition(string itemType);

        /// <summary>
        /// Sets a property which does not come from the Xml.
        /// </summary>
        P SetProperty(string name, string evaluatedValueEscaped, bool isGlobalProperty, bool mayBeReserved, BackEnd.Logging.LoggingContext loggingContext, bool isEnvironmentVariable = false, bool isCommandLineProperty = false);

        /// <summary>
        /// Sets a property which comes from the Xml.
        /// </summary>
        P SetProperty(ProjectPropertyElement propertyElement, string evaluatedValueEscaped, BackEnd.Logging.LoggingContext loggingContext);

        /// <summary>
        /// Retrieves an existing target, if any.
        /// </summary>
        ProjectTargetInstance GetTarget(string targetName);

        /// <summary>
        /// Adds a new target, overwriting any existing target with the same name.
        /// </summary>
        void AddTarget(ProjectTargetInstance target);

        /// <summary>
        /// Record an import opened during evaluation, if appropriate.
        /// </summary>
        void RecordImport(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated, SdkResult sdkResult);

        /// <summary>
        /// Record an import opened during evaluation, if appropriate.
        /// </summary>
        void RecordImportWithDuplicates(ProjectImportElement importElement, ProjectRootElement import, int versionEvaluated);

        /// <summary>
        /// Evaluates the provided string by expanding items and properties,
        /// using the current items and properties available.
        /// This is useful for the immediate window.
        /// Does not expand bare metadata expressions.
        /// </summary>
        /// <comment>
        /// Not for internal use.
        /// </comment>
        string ExpandString(string unexpandedValue);

        /// <summary>
        /// Evaluates the provided string as a condition by expanding items and properties,
        /// using the current items and properties available, then doing a logical evaluation.
        /// This is useful for the immediate window.
        /// Does not expand bare metadata expressions.
        /// </summary>
        /// <comment>
        /// Not for internal use.
        /// </comment>
        bool EvaluateCondition(string condition);
    }
}
