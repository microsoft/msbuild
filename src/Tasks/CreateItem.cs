// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Forward a list of items from input to output. This allows dynamic item lists.
    /// </summary>
    public class CreateItem : TaskExtension
    {
        #region Properties

        [Output]
        public ITaskItem[] Include { get; set; }

        public ITaskItem[] Exclude { get; set; }

        /// <summary>
        /// Only apply the additional metadata is none already exists
        /// </summary>
        public bool PreserveExistingMetadata { get; set; } = false;

        /// <summary>
        /// A list of metadata name/value pairs to apply to the output items.
        /// A typical input: "metadataname1=metadatavalue1", "metadataname2=metadatavalue2", ...
        /// </summary>
        /// <remarks>
        ///   <format type="text/markdown"><![CDATA[
        ///     ## Remarks
        ///     The fact that this is a `string[]` makes the following illegal:
        ///         `<CreateItem AdditionalMetadata="TargetPath=@(OutputPathItem)" />`
        ///     The engine fails on this because it doesn't like item lists being concatenated with string
        ///     constants when the data is being passed into an array parameter.  So the workaround is to
        ///     write this in the project file:
        ///         `<CreateItem AdditionalMetadata="@(OutputPathItem-&gt;'TargetPath=%(Identity)')" />`
        ///     ]]>
        ///   </format>
        /// </remarks>
        public string[] AdditionalMetadata { get; set; }

        #endregion

        #region ITask Members
        /// <summary>
        /// Execute.
        /// </summary>
        public override bool Execute()
        {
            if (Include == null)
            {
                Include = Array.Empty<ITaskItem>();
                return true;
            }

            // Expand wild cards.
            (Include, bool expandedInclude) = TryExpandWildcards(Include, XMakeAttributes.include);
            (Exclude, bool expandedExclude) = TryExpandWildcards(Exclude, XMakeAttributes.exclude);

            // Execution stops if wildcard expansion fails due to drive enumeration and related env var is set.
            if (!(expandedInclude && expandedExclude))
            {
                return false;
            }

            // Simple case:  no additional attribute to add and no Exclude.  In this case the
            // ouptuts are simply the inputs.
            if (AdditionalMetadata == null && Exclude == null)
            {
                return true;
            }

            // Parse the global properties into a hashtable.
            if (!PropertyParser.GetTable(Log, "AdditionalMetadata", AdditionalMetadata, out Dictionary<string, string> metadataTable))
            {
                return false;
            }

            // Build a table of unique items.
            Dictionary<string, string> excludeItems = GetUniqueItems(Exclude);

            // Produce the output items, add attribute and honor exclude.
            List<ITaskItem> outputItems = CreateOutputItems(metadataTable, excludeItems);

            Include = outputItems.ToArray();

            return !Log.HasLoggedErrors;
        }

#nullable enable
        /// <summary>
        /// Create the list of output items.
        /// </summary>
        private List<ITaskItem> CreateOutputItems(Dictionary<string, string> metadataTable, Dictionary<string, string> excludeItems)
        {
            var outputItems = new List<ITaskItem>();

            foreach (ITaskItem i in Include)
            {
                if (
                    (excludeItems.Count == 0) ||        // minor perf optimization
                    (!excludeItems.ContainsKey(i.ItemSpec)))
                {
                    ITaskItem newItem = i;
                    if (metadataTable != null)
                    {
                        foreach (KeyValuePair<string, string> nameAndValue in metadataTable)
                        {
                            // 1. If we have been asked to not preserve existing metadata then overwrite
                            // 2. If there is no existing metadata then apply the new
                            if ((!PreserveExistingMetadata) || String.IsNullOrEmpty(newItem.GetMetadata(nameAndValue.Key)))
                            {
                                if (FileUtilities.ItemSpecModifiers.IsItemSpecModifier(nameAndValue.Key))
                                {
                                    // Explicitly setting built-in metadata, is not allowed.
                                    Log.LogErrorWithCodeFromResources("CreateItem.AdditionalMetadataError", nameAndValue.Key);
                                    break;
                                }

                                newItem.SetMetadata(nameAndValue.Key, nameAndValue.Value);
                            }
                        }
                    }
                    outputItems.Add(newItem);
                }
            }
            return outputItems;
        }


        /// <summary>
        /// Expand wildcards in the item list.
        /// </summary>
        private (ITaskItem[]? Element, bool NoLoggedErrors) TryExpandWildcards(ITaskItem[]? expand, string attributeType)
        {
            // Used to detect and log drive enumerating wildcard patterns.
            string[] files;
            string itemSpec = string.Empty;

            if (expand == null)
            {
                return (null, true);
            }
            else
            {
                var expanded = new List<ITaskItem>();
                foreach (ITaskItem i in expand)
                {
                    if (FileMatcher.HasWildcards(i.ItemSpec))
                    {
                        FileMatcher.Default.GetFileSpecInfo(i.ItemSpec, out string directoryPart, out string wildcardPart, out string filenamePart, out bool needsRecursion, out bool isLegalFileSpec);
                        bool logDriveEnumeratingWildcard = FileMatcher.IsDriveEnumeratingWildcardPattern(directoryPart, wildcardPart);
                        if (logDriveEnumeratingWildcard)
                        {
                            Log.LogWarningWithCodeFromResources(
                                "WildcardResultsInDriveEnumeration",
                                EscapingUtilities.UnescapeAll(i.ItemSpec),
                                attributeType,
                                nameof(CreateItem),
                                BuildEngine.ProjectFileOfTaskNode);
                        }

                        if (logDriveEnumeratingWildcard && Traits.Instance.ThrowOnDriveEnumeratingWildcard)
                        {
                            Log.LogErrorWithCodeFromResources(
                                "WildcardResultsInDriveEnumeration",
                                EscapingUtilities.UnescapeAll(i.ItemSpec),
                                attributeType,
                                nameof(CreateItem),
                                BuildEngine.ProjectFileOfTaskNode);
                        }
                        else if (isLegalFileSpec)
                        {
                            (files, _, _, string? globFailure) = FileMatcher.Default.GetFiles(null /* use current directory */, i.ItemSpec);
                            if (globFailure != null)
                            {
                                Log.LogMessage(MessageImportance.Low, globFailure);
                            }

                            foreach (string file in files)
                            {
                                TaskItem newItem = new TaskItem(i) { ItemSpec = file };

                                // Compute the RecursiveDir portion.
                                FileMatcher.Result match = FileMatcher.Default.FileMatch(i.ItemSpec, file);
                                if (match.isLegalFileSpec && match.isMatch)
                                {
                                    if (!string.IsNullOrEmpty(match.wildcardDirectoryPart))
                                    {
                                        newItem.SetMetadata(FileUtilities.ItemSpecModifiers.RecursiveDir, match.wildcardDirectoryPart);
                                    }
                                }

                                expanded.Add(newItem);
                            }
                        }
                    }
                    else
                    {
                        expanded.Add(i);
                    }
                }
                return (expanded.ToArray(), !Log.HasLoggedErrors);
            }
        }

        /// <summary>
        /// Create a table of unique items
        /// </summary>
        private static Dictionary<string, string> GetUniqueItems(ITaskItem[] items)
        {
            var uniqueItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (items != null)
            {
                foreach (ITaskItem item in items)
                {
                    uniqueItems[item.ItemSpec] = String.Empty;
                }
            }
            return uniqueItems;
        }

        #endregion
    }
}
