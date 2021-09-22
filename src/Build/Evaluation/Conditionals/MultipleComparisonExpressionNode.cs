// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Shared;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Evaluates as boolean and evaluates children as boolean, numeric, or string.
    /// Order in which comparisons are attempted is numeric, boolean, then string.
    /// Updates conditioned properties table.
    /// </summary>
    internal abstract class MultipleComparisonNode : OperatorExpressionNode
    {
        private bool _conditionedPropertiesUpdated = false;

        /// <summary>
        /// Compare numbers
        /// </summary>
        protected abstract bool Compare(double left, double right);

        /// <summary>
        /// Compare booleans
        /// </summary>
        protected abstract bool Compare(bool left, bool right);

        /// <summary>
        /// Compare strings
        /// </summary>
        protected abstract bool Compare(string left, string right);

        /// <summary>
        /// Evaluates as boolean and evaluates children as boolean, numeric, or string.
        /// Order in which comparisons are attempted is numeric, boolean, then string.
        /// Updates conditioned properties table.
        /// </summary>
        internal override bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state)
        {
            ProjectErrorUtilities.VerifyThrowInvalidProject
                (LeftChild != null && RightChild != null,
                 state.ElementLocation,
                 "IllFormedCondition",
                 state.Condition);

            // It's sometimes possible to bail out of expansion early if we just need to know whether 
            // the result is empty string.
            // If at least one of the left or the right hand side will evaluate to empty, 
            // and we know which do, then we already have enough information to evaluate this expression.
            // That means we don't have to fully expand a condition like " '@(X)' == '' " 
            // which is a performance advantage if @(X) is a huge item list.
            bool leftEmpty = LeftChild.EvaluatesToEmpty(state);
            bool rightEmpty = RightChild.EvaluatesToEmpty(state);
            if (leftEmpty || rightEmpty)
            {
                UpdateConditionedProperties(state);

                return Compare(leftEmpty, rightEmpty);
            }

            if (LeftChild.TryNumericEvaluate(state, out double leftNumericValue))
            {
                // The left child evaluating to a number and the right child not evaluating to a number
                // is insufficient to say they are not equal because $(MSBuildToolsVersion) evaluates to
                // the string "Current" most of the time but when doing numeric comparisons, is treated
                // as a version and returns "17.0" (or whatever the current tools version is). This means
                // that if '$(MSBuildToolsVersion)' is "equal" to BOTH '17.0' and 'Current' (if 'Current'
                // is 17.0).
                if (RightChild.TryNumericEvaluate(state, out double rightNumericValue))
                {
                    return Compare(leftNumericValue, rightNumericValue);
                }
            }

            if (LeftChild.TryBoolEvaluate(state, out bool leftBoolValue))
            {
                return RightChild.TryBoolEvaluate(state, out bool rightBoolValue) ?
                    Compare(leftBoolValue, rightBoolValue) :
                    this is NotEqualExpressionNode; // If the left child is a bool, and the right child is not, then they are not equal. Return true for != and false for ==
            }

            string leftExpandedValue = LeftChild.GetExpandedValue(state);
            string rightExpandedValue = RightChild.GetExpandedValue(state);

            ProjectErrorUtilities.VerifyThrowInvalidProject
                (leftExpandedValue != null && rightExpandedValue != null,
                    state.ElementLocation,
                    "IllFormedCondition",
                    state.Condition);

            UpdateConditionedProperties(state);

            return Compare(leftExpandedValue, rightExpandedValue);
        }

        /// <summary>
        /// Reset temporary state
        /// </summary>
        internal override void ResetState()
        {
            base.ResetState();
            _conditionedPropertiesUpdated = false;
        }

        /// <summary>
        /// Updates the conditioned properties table if it hasn't already been done.
        /// </summary>
        private void UpdateConditionedProperties(ConditionEvaluator.IConditionEvaluationState state)
        {
            if (!_conditionedPropertiesUpdated && state.ConditionedPropertiesInProject != null)
            {
                string leftUnexpandedValue = LeftChild.GetUnexpandedValue(state);
                string rightUnexpandedValue = RightChild.GetUnexpandedValue(state);

                if (leftUnexpandedValue != null)
                {
                    ConditionEvaluator.UpdateConditionedPropertiesTable
                        (state.ConditionedPropertiesInProject,
                         leftUnexpandedValue,
                         RightChild.GetExpandedValue(state));
                }

                if (rightUnexpandedValue != null)
                {
                    ConditionEvaluator.UpdateConditionedPropertiesTable
                        (state.ConditionedPropertiesInProject,
                         rightUnexpandedValue,
                         LeftChild.GetExpandedValue(state));
                }

                _conditionedPropertiesUpdated = true;
            }
        }
    }
}
