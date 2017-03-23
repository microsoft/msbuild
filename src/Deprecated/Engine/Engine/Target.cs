// Copyright (c) Microsoft. All rights reserved.// Licensed under the MIT license. See LICENSE file in the project root for full license information.using System;using System.Xml;using System.Diagnostics;       // for debugger display attributeusing System.Collections;using System.Collections.Generic;using System.Globalization;using Microsoft.Build.Framework;using Microsoft.Build.BuildEngine.Shared;using error = Microsoft.Build.BuildEngine.Shared.ErrorUtilities;namespace Microsoft.Build.BuildEngine{    /// <summary>    /// This class represents a single target in its parent project.    /// </summary>    [DebuggerDisplay("Target (Name = { Name }, Condition = { Condition })")]    public class Target : IEnumerable    {        #region Enums        /// <summary>        /// This enumeration contains a list of the possible states that the target could be in, in terms of the build process.        /// </summary>        internal enum BuildState        {            // This build for this target has not begun.  We don't even know if            // we're going to end up needing to build this target at all.            NotStarted,            // We've determined that this target needs to be built, and we're            // in the process of doing exactly that.  Building other targets            // that are dependents of this target is considered to be part of            // building this target.  And so, while we're building other dependent            // targets, our state will be set to "InProgress".            InProgress,            // This target (and naturally all dependent targets) has been            // successfully built.            CompletedSuccessfully,            // We have attempted to build this target and all its dependent            // targets.  However, something failed during that process, and             // we consider ourselves done with this target.            CompletedUnsuccessfully,            // This target is to be skipped.  This state is the result of a target             // having a condition attribute and that condition evaluating to false.            Skipped        }        #endregion        #region Member Data        // The evaluated name of the target.        private string                      targetName;        // The parent project.object.  We will need this in order get the        // complete list of targets, items, properties, etc.        private Project                     parentProject;        // The parent Engine object.        private Engine                      parentEngine;        // The state of this target, in terms of the build.        private BuildState                  buildState;        // The <Target> XML element, if this is a persisted item.  For virtual        // items (i.e., those generated by tasks), this would be null.        private XmlElement targetElement = null;        // This is the "Condition" attribute on the <Target> element.        private XmlAttribute conditionAttribute = null;        // This is the "DependsOnTargets" attribute on the <Target> element.        private XmlAttribute dependsOnTargetsAttribute = null;        // This is the "Inputs" attribute on the <Target> element.        private XmlAttribute inputsAttribute = null;        // This is the "Outputs" attribute on the <Target> element.        private XmlAttribute outputsAttribute = null;        // This contains all of the child task nodes in this <Target> node.        ArrayList taskElementList = null;        // If this is a persisted <Target> element, this boolean tells us whether        // it came from the main parentProject.file or an imported parentProject.file.        private bool importedFromAnotherProject = false;        // If the Inputs or Outputs attribute changes then we will have to re-calculate the        // targetParameters        private bool recalculateBatchableParameters = false;        // the project file that the target XML was defined in -- this file could be different from the file of this target's        // parent project if the target was defined in an imported project file        private string projectFileOfTargetElement;        // the outputs of the target as BuildItems (if it builds successfully)        private List<BuildItem> targetOutputItems;        // We check the target's condition to ensure it doesn't reference item metadata in an attempt to batch.        private bool conditionCheckedForInvalidMetadataReferences = false;        TargetExecutionWrapper executionState = null;        List<string> batchableTargetParameters = null;                // TargetId        private int id;        #endregion        #region Constructors        /// <summary>        /// Initializes a persisted target from an existing &lt;Target&gt; element which exists either in the main parent project        /// file or one of the imported files.        /// </summary>        /// <param name="targetElement"></param>        /// <param name="project"></param>        /// <param name="importedFromAnotherProject"></param>        internal Target        (            XmlElement  targetElement,            Project     project,            bool        importedFromAnotherProject        )        {            // Make sure a valid node has been given to us.            error.VerifyThrow(targetElement != null,"Need a valid XML node.");            // Make sure this really is the <target> node.            ProjectXmlUtilities.VerifyThrowElementName(targetElement, XMakeElements.target);            this.targetElement = targetElement;            this.parentProject = project;            this.parentEngine = project.ParentEngine;            this.conditionAttribute = null;            this.taskElementList = null;            this.importedFromAnotherProject = importedFromAnotherProject;            this.buildState = BuildState.NotStarted;            this.id = project.ParentEngine.GetNextTargetId();            // The target name and target dependendencies (dependencies on other             // targets) are specified as attributes of the <target> element.            XmlAttribute returnsAttribute = null;             // Loop through all the attributes on the <target> element.            foreach (XmlAttribute targetAttribute in targetElement.Attributes)            {                switch (targetAttribute.Name)                {                    // Process the "condition" attribute.                    case XMakeAttributes.condition:                        this.conditionAttribute = targetAttribute;                        break;                    // Process the "name" attribute.                    case XMakeAttributes.name:                        this.targetName = EscapingUtilities.UnescapeAll(targetAttribute.Value);                        // Target names cannot contain MSBuild special characters, embedded properties,                         // or item lists.                        int indexOfSpecialCharacter = this.targetName.IndexOfAny(XMakeElements.illegalTargetNameCharacters);                        if (indexOfSpecialCharacter >= 0)                        {                            ProjectErrorUtilities.VerifyThrowInvalidProject(false,                                targetAttribute, "NameInvalid", targetName, targetName[indexOfSpecialCharacter]);                        }                        break;                    // Process the "dependsOnTargets" attribute.                    case XMakeAttributes.dependsOnTargets:                        this.dependsOnTargetsAttribute = targetAttribute;                        break;                    case XMakeAttributes.inputs:                        this.inputsAttribute = targetAttribute;                        recalculateBatchableParameters = true;                        break;                    case XMakeAttributes.outputs:                        this.outputsAttribute = targetAttribute;                        recalculateBatchableParameters = true;                        break;                    // This is only recognized by the new OM:                    // so that the compat tests keep passing,                    // ignore it.                    case XMakeAttributes.keepDuplicateOutputs:                        break;                    // This is only recognized by the new OM:                    // so that the compat tests keep passing,                    // ignore it.                    case XMakeAttributes.returns:                        returnsAttribute = targetAttribute;                        break;                    // These are only recognized by the new OM:                    // while the solution wrapper generator is using                     // the old OM to parse projects for dependencies,                    // we must make sure to not fail for these                    case XMakeAttributes.beforeTargets:                    case XMakeAttributes.afterTargets:                        break;                    default:                        ProjectXmlUtilities.ThrowProjectInvalidAttribute(targetAttribute);                        break;                }            }            // Hack to help the 3.5 engine at least pretend to still be able to build on top of             // the 4.0 targets.  In cases where there is no Outputs attribute, just a Returns attribute,             // we can approximate the correct behaviour by making the Returns attribute our "outputs" attribute.             if (this.outputsAttribute == null && returnsAttribute != null)            {                this.outputsAttribute = returnsAttribute;                recalculateBatchableParameters = true;            }            // It's considered an error if a target does not have a name.            ProjectErrorUtilities.VerifyThrowInvalidProject((targetName != null) && (targetName.Length > 0),                targetElement, "MissingRequiredAttribute", XMakeAttributes.name, XMakeElements.target);            this.taskElementList = new ArrayList();            // Process each of the child nodes beneath the <Target>.            XmlElement anyOnErrorElement = null;            List<XmlElement> childElements = ProjectXmlUtilities.GetValidChildElements(targetElement);            foreach (XmlElement childElement in childElements)            {                bool onErrorOutOfOrder = false;                switch (childElement.Name)                {                    case XMakeElements.onError:                        anyOnErrorElement = childElement;                        break;                    default:                        onErrorOutOfOrder = (anyOnErrorElement != null);                        this.taskElementList.Add(new BuildTask(childElement,                            this, this.importedFromAnotherProject));                        break;                }                // Check for out-of-order OnError                ProjectErrorUtilities.VerifyThrowInvalidProject(!onErrorOutOfOrder,                    anyOnErrorElement, "NodeMustBeLastUnderElement", XMakeElements.onError, XMakeElements.target, childElement.Name);            }        }        #endregion        #region Properties        /// <summary>        /// Id for the target        /// </summary>        internal int Id        {            get            {                return this.id;            }        }        /// <summary>        /// Gets the target's name as specified in the "Name" attribute. The value of this attribute is never evaluated.        /// </summary>        /// <value>The target name string.</value>        public string Name        {            get             {                return this.targetName;            }        }        /// <summary>        /// Gets the target's unevaluated "DependsOnTargets" string.        /// Returns unevaluated.        /// </summary>        /// <value>The raw "DependsOnTargets" string.</value>        public string DependsOnTargets        {            get            {                return ProjectXmlUtilities.GetAttributeValue(this.dependsOnTargetsAttribute);            }            set            {                this.dependsOnTargetsAttribute = SetOrRemoveTargetAttribute(XMakeAttributes.dependsOnTargets, value);            }        }        /// <summary>        /// Gets the target's unevaluated "Inputs" string.        /// Returns unevaluated.        /// </summary>        /// <value>The raw "Inputs" string.</value>        public string Inputs        {            get            {                return ProjectXmlUtilities.GetAttributeValue(this.inputsAttribute);            }            set            {                this.inputsAttribute = SetOrRemoveTargetAttribute(XMakeAttributes.inputs, value);                recalculateBatchableParameters = true;            }        }        /// <summary>        /// Gets the target's unevaluated "Outputs" string.        /// Returns unevaluated.        /// </summary>        /// <value>The raw "Outputs" string.</value>        public string Outputs        {            get            {                return ProjectXmlUtilities.GetAttributeValue(this.outputsAttribute);            }            set            {                this.outputsAttribute = SetOrRemoveTargetAttribute(XMakeAttributes.outputs, value);                recalculateBatchableParameters = true;            }        }        /// <summary>        /// Accessor for the item's "condition". Returned unevaluated.        /// </summary>        /// <returns>Condition string.</returns>        /// <value>The raw condition string.</value>        public string Condition        {            get            {                return ProjectXmlUtilities.GetAttributeValue(this.conditionAttribute);            }            set            {                this.conditionAttribute = SetOrRemoveTargetAttribute(XMakeAttributes.condition, value);                this.conditionCheckedForInvalidMetadataReferences = false;            }        }        /// <summary>        /// Gets the XML representing this target.        /// </summary>        /// <value>The XmlElement for the target.</value>        internal XmlElement TargetElement        {            get            {                return this.targetElement;            }        }        /// <summary>        /// Gets the target's unevaluated "DependsOnTargets" XML element.        /// </summary>        internal XmlAttribute DependsOnTargetsAttribute        {            get            {                return this.dependsOnTargetsAttribute;            }        }        /// <summary>        /// Gets the filename/path of the project this target was defined in. This file could be different from the file of this        /// target's parent project, because the target could be imported. If the target is only defined in-memory, then it may        /// not have a filename associated with it.        /// </summary>        /// <value>The filename/path string of this target's original project, or empty string.</value>        internal string ProjectFileOfTargetElement        {            get            {                if (projectFileOfTargetElement == null)                {                    projectFileOfTargetElement = XmlUtilities.GetXmlNodeFile(TargetElement, parentProject.FullFileName);                }                return projectFileOfTargetElement;            }        }        /// <summary>        /// Read-only accessor for this target's parent Project object.        /// </summary>        /// <value></value>        internal Project ParentProject        {            get            {                return this.parentProject;            }            set            {                this.parentProject = value;            }        }        /// <summary>        /// Read-only accessor for this target's parent Project object.        /// </summary>        /// <value></value>        internal Engine ParentEngine        {            get            {                return this.parentEngine;            }        }        /// <summary>        /// Calculates the batchable target parameters, which can be changed if inputs and outputs are        /// set after target creation.        /// </summary>        internal List<string> GetBatchableTargetParameters()        {            if (recalculateBatchableParameters)            {                batchableTargetParameters = new List<string>();                if (inputsAttribute != null)                {                    batchableTargetParameters.Add(inputsAttribute.Value);                }                if (outputsAttribute != null)                {                    batchableTargetParameters.Add(outputsAttribute.Value);                }                recalculateBatchableParameters = false;            }            else if (batchableTargetParameters == null)            {                batchableTargetParameters = new List<string>();            }            return batchableTargetParameters;        }        /// <summary>        /// This returns a boolean telling you whether this particular target        /// was imported from another project, or whether it was defined        /// in the main project.        /// </summary>        /// <value></value>        public bool IsImported        {            get            {                return this.importedFromAnotherProject;            }        }        internal BuildState TargetBuildState        {            get            {                return this.buildState;            }        }        internal TargetExecutionWrapper ExecutionState        {            get            {                return executionState;            }        }        #endregion        #region Methods        /// <summary>        /// Allows the caller to use a foreach loop to enumerate through the individual         /// BuildTask objects contained within this Target.        /// </summary>        /// <returns></returns>        public IEnumerator GetEnumerator            (            )        {            error.VerifyThrow(this.taskElementList != null, "List of TaskElements not initialized!");            return this.taskElementList.GetEnumerator();        }        /// <summary>        /// Sets the build state back to "NotStarted".        /// </summary>        internal void ResetBuildStatus            (            )        {            this.buildState = BuildState.NotStarted;        }        /// <summary>        /// Update the target data structures since the target has completed        /// </summary>        internal void UpdateTargetStateOnBuildCompletion        (            BuildState stateOfBuild,             List<BuildItem> targetOutputItemList        )        {            this.buildState = stateOfBuild;            this.targetOutputItems = targetOutputItemList;            // Clear the execution state since the build is completed            executionState = null;        }        /// <summary>        /// Builds this target if it has not already been built as part of its parent project. Before we actually execute the        /// tasks for this target, though, we first call on all the dependent targets to build themselves.        /// This function may throw InvalidProjectFileException        /// </summary>        internal void Build        (            ProjectBuildState buildContext        )        {            // Depending on the build state, we may do different things.            switch (buildState)            {                case BuildState.InProgress:                    // In single proc mode if the build state was already "in progress"                     // and somebody just told us to build ourselves, it means that there is                     // a loop (circular dependency) in the target dependency graph. In multi                    // proc mode we need to analyze the dependency graph before we can                    // tell if there a circular dependency or if two independent chains                    // of targets happen to need the result of this target.                    if (parentEngine.Router.SingleThreadedMode || buildContext.ContainsCycle(this.Name))                    {                        ProjectErrorUtilities.VerifyThrowInvalidProject(false, TargetElement, "CircularDependency", targetName);                    }                    else                    {                        buildContext.CurrentBuildContextState = ProjectBuildState.BuildContextState.WaitingForTarget;                        this.executionState.AddWaitingBuildContext(buildContext);                    }                    break;                case BuildState.CompletedSuccessfully:                case BuildState.CompletedUnsuccessfully:                    // If this target has already been built as part of this project,                    // we're not going to build it again.  Just return the result                    // from when it was built previously.  Note:  This condition                    // could really only ever hold true if the user specifically                    // requested us to build multiple targets and there existed                    // a direct or indirect dependency relationship between two or                    // more of those top-level targets.                    // Note: we aren't really entering the target in question here, so don't use the target                    // event context. Using the target ID for skipped messages would force us to                    // cache the individual target IDs for unloaded projects and it's not really worth the trouble.                    // Just use the parent event context.                    parentEngine.LoggingServices.LogComment(buildContext.ProjectBuildEventContext,                        ((buildState == BuildState.CompletedSuccessfully) ? "TargetAlreadyCompleteSuccess" : "TargetAlreadyCompleteFailure"),                        this.targetName);                    // Only contexts which are generated from an MSBuild task could need                     // the outputs of this target, such contexts have a non-null evaluation                    // request                    if ((buildState == BuildState.CompletedSuccessfully) &&                         (buildContext.BuildRequest.OutputsByTarget != null &&                         buildContext.NameOfBlockingTarget == null))                    {                        error.VerifyThrow(                            String.Compare(EscapingUtilities.UnescapeAll(buildContext.NameOfTargetInProgress), this.Name, StringComparison.OrdinalIgnoreCase) == 0,                            "The name of the target in progress is inconsistent with the target being built");                        error.VerifyThrow(targetOutputItems != null,                            "If the target built successfully, we must have its outputs.");                        buildContext.BuildRequest.OutputsByTarget[Name] = targetOutputItems.ToArray();                    }                    if (buildContext.NameOfBlockingTarget == null)                    {                        buildContext.BuildRequest.ResultByTarget[Name] = buildState;                    }                    break;                case BuildState.NotStarted:                case BuildState.Skipped:                    {                        // Always have to create a new context in build as other projects or targets may try and build this target                        BuildEventContext targetBuildEventContext = new BuildEventContext                                                        (                                                            buildContext.ProjectBuildEventContext.NodeId,                                                            this.id,                                                            buildContext.ProjectBuildEventContext.ProjectContextId,                                                            buildContext.ProjectBuildEventContext.TaskId                                                        );                        Expander expander = new Expander(this.parentProject.evaluatedProperties, this.parentProject.evaluatedItemsByName);                        // We first make sure no batching was attempted with the target's condition.                        if (!conditionCheckedForInvalidMetadataReferences)                        {                            if (ExpressionShredder.ContainsMetadataExpressionOutsideTransform(this.Condition))                            {                                ProjectErrorUtilities.ThrowInvalidProject(this.conditionAttribute, "TargetConditionHasInvalidMetadataReference", targetName, this.Condition);                            }                            conditionCheckedForInvalidMetadataReferences = true;                        }                        // If condition is false (based on propertyBag), set this target's state to                        // "Skipped" since we won't actually build it.                        if (!Utilities.EvaluateCondition(this.Condition, this.conditionAttribute,                                expander, null, ParserOptions.AllowProperties | ParserOptions.AllowItemLists,                                parentEngine.LoggingServices, targetBuildEventContext))                        {                            buildState = BuildState.Skipped;                            if (buildContext.NameOfBlockingTarget == null)                            {                                buildContext.BuildRequest.ResultByTarget[Name] = buildState;                            }                            if (!parentEngine.LoggingServices.OnlyLogCriticalEvents)                            {                                // Expand the expression for the Log.                                string expanded = expander.ExpandAllIntoString(this.Condition, this.conditionAttribute);                                // By design: Not building dependencies. This is what NAnt does too.                                parentEngine.LoggingServices.LogComment(targetBuildEventContext, "TargetSkippedFalseCondition",                                                        this.targetName, this.Condition, expanded);                            }                        }                        else                        {                            // This target has not been built yet.  So build it!                            // Change our state to "in progress". TargetParameters will need to be re-calculated if Inputs and Outputs attribute has changed.                            buildState = BuildState.InProgress;                            List<string> batchableTargetParameters = GetBatchableTargetParameters();                            executionState = new TargetExecutionWrapper(this, taskElementList, batchableTargetParameters, targetElement, expander, targetBuildEventContext);                            ContinueBuild(buildContext, null);                        }                    }                    break;                default:                    error.VerifyThrow(false, "Build state {0} not handled in Target.Build method", buildState);                    break;            }        }        /// <summary>        /// This method is called repeatedly to execute the target in multi-threaded mode. In single        /// threaded mode it is called once and it loops internally until the execution is finished.        /// </summary>        /// <param name="buildContext">Context within which the target is being executed</param>        /// <param name="taskExecutionContext">Result of last execution (multi-threaded only)</param>        internal void ContinueBuild( ProjectBuildState buildContext, TaskExecutionContext taskExecutionContext)        {            executionState.ContinueBuild(buildContext, taskExecutionContext);        }        /// <summary>        /// Executes a task within a target. This method initializes a task engine for the given task, and then executes the task        /// using the engine.        /// </summary>        /// <param name="taskNode"></param>        /// <param name="hostObject"></param>        /// <returns>true, if successful</returns>        internal bool ExecuteOneTask(XmlElement taskNode, ITaskHost hostObject)        {            bool taskExecutedSuccessfully = false;            string projectFileOfTaskNode = XmlUtilities.GetXmlNodeFile(taskNode, parentProject.FullFileName);            BuildEventContext targetBuildEventContext = new BuildEventContext                                (                                    ParentProject.ProjectBuildEventContext.NodeId,                                    this.id,                                    ParentProject.ProjectBuildEventContext.ProjectContextId,                                    ParentProject.ProjectBuildEventContext.TaskId                                );            int handleId = parentEngine.EngineCallback.CreateTaskContext(ParentProject,this, null, taskNode,                                                                             EngineCallback.inProcNode, targetBuildEventContext);            TaskExecutionModule taskExecutionModule = parentEngine.NodeManager.TaskExecutionModule;            TaskEngine taskEngine = new TaskEngine(taskNode, hostObject, parentProject.FullFileName, projectFileOfTaskNode, parentEngine.LoggingServices, handleId, taskExecutionModule, targetBuildEventContext);            taskExecutedSuccessfully =                taskEngine.ExecuteTask                (                    TaskExecutionMode.ExecuteTaskAndGatherOutputs,                    new Lookup(parentProject.evaluatedItemsByName, parentProject.evaluatedProperties, ParentProject.ItemDefinitionLibrary)                );            return taskExecutedSuccessfully;        }        /// <summary>        /// Indicates that something has changed within the &lt;Target&gt; element, so the project        /// needs to be saved and re-evaluated at next build.        /// </summary>        internal void MarkTargetAsDirty            (            )        {            if (this.ParentProject != null)            {                // This is a change to the contents of the project file.                this.ParentProject.MarkProjectAsDirty();            }        }        /// <summary>        /// Sets or removes an attribute from the target element. Marks the target dirty after the update        /// </summary>        /// <param name="attributeName"></param>        /// <param name="attributeValue"></param>        /// <returns>XmlAttribute which has been updated</returns>        internal XmlAttribute SetOrRemoveTargetAttribute            (            string attributeName,            string attributeValue            )        {            XmlAttribute updatedAttribute = null;            // If this Target object is not actually represented by a             // <Target> element in the parentProject.file, then do not allow            // the caller to set the condition.            error.VerifyThrowInvalidOperation(this.targetElement != null, "CannotSetCondition");            // If this item was imported from another parentProject. we don't allow modifying it.            error.VerifyThrowInvalidOperation(!this.importedFromAnotherProject, "CannotModifyImportedProjects");            updatedAttribute = ProjectXmlUtilities.SetOrRemoveAttribute(this.targetElement, attributeName, attributeValue);            // Mark the project dirty after an attribute has been updated            this.MarkTargetAsDirty();            return updatedAttribute;        }        /// <summary>        /// Adds a task with the specified name to the end of this target.  This method        /// does all of the work to manipulate the project's XML content.        /// </summary>        /// <param name="taskName"></param>        public BuildTask AddNewTask            (            string taskName            )        {            error.VerifyThrow(this.taskElementList != null, "Arraylist not initialized!");            error.VerifyThrowArgumentLength(taskName, "taskName");            // Confirm that it's not an imported target.            error.VerifyThrowInvalidOperation(!this.IsImported, "CannotModifyImportedProjects");            // Create the XML for the new task node and append it to the very end of the <Target> element.            XmlElement newTaskElement = this.targetElement.OwnerDocument.CreateElement(taskName, XMakeAttributes.defaultXmlNamespace);            this.targetElement.AppendChild(newTaskElement);            // Create a new BuildTask object, and add it to our list.            BuildTask newTask = new BuildTask(newTaskElement, this, false);            this.taskElementList.Add(newTask);            this.MarkTargetAsDirty();            return newTask;        }        /// <summary>        /// Removes the specified BuildTask from the target.  This method correctly updates        /// the project's XML content, so the task will no longer show up when the project        /// is saved out.        /// </summary>        /// <param name="taskElement"></param>        public void RemoveTask            (            BuildTask taskElement            )        {            // Confirm that it's not an imported target.            error.VerifyThrowInvalidOperation(!this.IsImported, "CannotModifyImportedProjects");            error.VerifyThrow(this.taskElementList != null, "Arraylist not initialized!");            error.VerifyThrowArgumentNull(taskElement, "taskElement");            // Confirm that the BuildTask belongs to this Target.            error.VerifyThrowInvalidOperation(taskElement.ParentTarget == this,                "IncorrectObjectAssociation", "BuildTask", "Target");            // Remove the BuildTask from our list.            this.taskElementList.Remove(taskElement);            // Remove the task's XML from the project document.            this.targetElement.RemoveChild(taskElement.TaskXmlElement);            // Dissociate the BuildTask from this target.            taskElement.ParentTarget = null;            this.MarkTargetAsDirty();        }        #endregion    }}