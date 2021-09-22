// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

using Microsoft.Build.Shared;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Base class for all expression nodes.
    /// </summary>
    internal abstract class GenericExpressionNode
    {
        internal abstract bool CanBoolEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract bool CanNumericEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract bool CanVersionEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract double NumericEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract Version VersionEvaluate(ConditionEvaluator.IConditionEvaluationState state);
        internal abstract bool TryBoolEvaluate(ConditionEvaluator.IConditionEvaluationState state, out bool result);
        internal abstract bool TryNumericEvaluate(ConditionEvaluator.IConditionEvaluationState state, out double result);
        internal abstract bool TryVersionEvaluate(ConditionEvaluator.IConditionEvaluationState state, out Version result);

        /// <summary>
        /// Returns true if this node evaluates to an empty string,
        /// otherwise false.
        /// (It may be cheaper to determine whether an expression will evaluate
        /// to empty than to fully evaluate it.)
        /// Implementations should cache the result so that calls after the first are free.
        /// </summary>
        internal virtual bool EvaluatesToEmpty(ConditionEvaluator.IConditionEvaluationState state)
        {
            return false;
        }

        /// <summary>
        /// Value after any item and property expressions are expanded
        /// </summary>
        /// <returns></returns>
        internal abstract string GetExpandedValue(ConditionEvaluator.IConditionEvaluationState state);

        /// <summary>
        /// Value before any item and property expressions are expanded
        /// </summary>
        /// <returns></returns>
        internal abstract string GetUnexpandedValue(ConditionEvaluator.IConditionEvaluationState state);

        /// <summary>
        /// If any expression nodes cache any state for the duration of evaluation, 
        /// now's the time to clean it up
        /// </summary>
        internal abstract void ResetState();

        /// <summary>
        /// The main evaluate entry point for expression trees
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        internal bool Evaluate(ConditionEvaluator.IConditionEvaluationState state)
        {
            ProjectErrorUtilities.VerifyThrowInvalidProject(
                CanBoolEvaluate(state),
                state.ElementLocation,
                "ConditionNotBooleanDetail",
                state.Condition,
                GetExpandedValue(state));

            return BoolEvaluate(state);
        }

        /// <summary>
        /// Get display string for this node for use in the debugger.
        /// </summary>
        internal virtual string DebuggerDisplay { get; }


        #region REMOVE_COMPAT_WARNING
        internal virtual bool PossibleAndCollision
        {
            set { /* do nothing */ }
            get { return false; }
        }

        internal virtual bool PossibleOrCollision
        {
            set { /* do nothing */ }
            get { return false; }
        }

        internal bool PotentialAndOrConflict()
        {
            // The values of the functions are assigned to boolean locals
            // in order to force evaluation of the functions even when the 
            // first one returns false
            bool detectOr = DetectOr();
            bool detectAnd = DetectAnd();
            return detectOr && detectAnd;
        }

        internal abstract bool DetectOr();
        internal abstract bool DetectAnd();
        #endregion

    }
}
