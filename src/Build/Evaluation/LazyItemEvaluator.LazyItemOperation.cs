// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Construction;
using Microsoft.Build.Eventing;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        private abstract class LazyItemOperation : IItemOperation
        {
            private readonly string _itemType;
            private readonly ImmutableDictionary<string, LazyItemList> _referencedItemLists;

            protected readonly LazyItemEvaluator<P, I, M, D> _lazyEvaluator;
            protected readonly ProjectItemElement _itemElement;
            protected readonly ItemSpec<P, I> _itemSpec;
            protected readonly EvaluatorData _evaluatorData;
            protected readonly Expander<P, I> _expander;
            protected readonly bool _conditionResult;

            //  This is used only when evaluating an expression, which instantiates
            //  the items and then removes them
            protected readonly IItemFactory<I, I> _itemFactory;
            internal ItemSpec<P, I> Spec => _itemSpec;
            
            protected LazyItemOperation(OperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
            {
                _itemElement = builder.ItemElement;
                _itemType = builder.ItemType;
                _itemSpec = builder.ItemSpec;
                _referencedItemLists = builder.ReferencedItemLists.ToImmutable();
                _conditionResult = builder.ConditionResult;

                _lazyEvaluator = lazyEvaluator;

                _evaluatorData = new EvaluatorData(_lazyEvaluator._outerEvaluatorData, itemType => GetReferencedItems(itemType, ImmutableHashSet<string>.Empty));
                _itemFactory = new ItemFactoryWrapper(_itemElement, _lazyEvaluator._itemFactory);
                _expander = new Expander<P, I>(_evaluatorData, _evaluatorData, _lazyEvaluator.EvaluationContext);

                _itemSpec.Expander = _expander;
            }

            protected FileMatcher FileMatcher => _lazyEvaluator.FileMatcher;

            public void Apply(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                MSBuildEventSource.Log.ApplyLazyItemOperationsStart(_itemElement.ItemType);
                using (_lazyEvaluator._evaluationProfiler.TrackElement(_itemElement))
                {
                    ApplyImpl(listBuilder, globsToIgnore);
                }
                MSBuildEventSource.Log.ApplyLazyItemOperationsStop(_itemElement.ItemType);
            }

            protected virtual void ApplyImpl(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                var items = SelectItems(listBuilder, globsToIgnore);
                MutateItems(items);
                SaveItems(items, listBuilder);
            }

            /// <summary>
            /// Produce the items to operate on. For example, create new ones or select existing ones
            /// </summary>
            protected virtual ImmutableList<I> SelectItems(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                return listBuilder.Select(itemData => itemData.Item)
                                  .ToImmutableList();
            }

            // todo Refactoring: MutateItems should clone each item before mutation. See https://github.com/Microsoft/msbuild/issues/2328
            protected virtual void MutateItems(ImmutableList<I> items) { }

            protected virtual void SaveItems(ImmutableList<I> items, OrderedItemDataCollection.Builder listBuilder) { }

            private IList<I> GetReferencedItems(string itemType, ImmutableHashSet<string> globsToIgnore)
            {
                if (_referencedItemLists.TryGetValue(itemType, out var itemList))
                {
                    return itemList.GetMatchedItems(globsToIgnore);
                }
                else
                {
                    return ImmutableList<I>.Empty;
                }
            }

            [DebuggerDisplay(@"{DebugString()}")]
            protected readonly struct ItemBatchingContext
            {
                public I OperationItem { get; }
                private Dictionary<string, I> CapturedItems { get; }

                public ItemBatchingContext(I operationItem, Dictionary<string, I> capturedItems = null)
                {
                    OperationItem = operationItem;

                    CapturedItems = capturedItems == null || capturedItems.Count == 0
                        ? null
                        : capturedItems;
                }

                public IMetadataTable GetMetadataTable()
                {
                    return CapturedItems == null
                        ? (IMetadataTable) OperationItem
                        : new ItemOperationMetadataTable(OperationItem, CapturedItems);
                }

                private string DebugString()
                {
                    var referencedItemsString = CapturedItems == null
                        ? "none"
                        : string.Join(";", CapturedItems.Select(kvp => $"{kvp.Key} : {kvp.Value.EvaluatedInclude}"));

                    return $"{OperationItem.Key} : {OperationItem.EvaluatedInclude}; CapturedItems: {referencedItemsString}";
                }
            }

            private class ItemOperationMetadataTable : IMetadataTable
            {
                private readonly I _operationItem;
                private readonly Dictionary<string, I> _capturedItems;

                public ItemOperationMetadataTable(I operationItem, Dictionary<string, I> capturedItems)
                {
                    ErrorUtilities.VerifyThrow(
                        capturedItems.Comparer == StringComparer.OrdinalIgnoreCase,
                        "MSBuild assumes case insensitive item name comparison");

                    _operationItem = operationItem;
                    _capturedItems = capturedItems;
                }

                public string GetEscapedValue(string name)
                {
                    return _operationItem.GetEscapedValue(name);
                }

                public string GetEscapedValue(string itemType, string name)
                {
                    return RouteCall(itemType, name, (t, it, n) => t.GetEscapedValue(it, n));
                }

                public string GetEscapedValueIfPresent(string itemType, string name)
                {
                    return RouteCall(itemType, name, (t, it, n) => t.GetEscapedValueIfPresent(it, n));
                }

                private string RouteCall(string itemType, string name, Func<IMetadataTable, string, string, string> getEscapedValueFunc)
                {
                    if (itemType?.Equals(_operationItem.Key, StringComparison.OrdinalIgnoreCase) != false)
                    {
                        return getEscapedValueFunc(_operationItem, itemType, name);
                    }
                    else if (_capturedItems.TryGetValue(itemType, out var item))
                    {
                        return getEscapedValueFunc(item, itemType, name);
                    }
                    else
                    {
                        return string.Empty;
                    }
                }
            }

            protected void DecorateItemsWithMetadata(IEnumerable<ItemBatchingContext> itemBatchingContexts, ImmutableList<ProjectMetadataElement> metadata, bool? needToExpandMetadata = null)
            {
                if (metadata.Count > 0)
                {
                    ////////////////////////////////////////////////////
                    // UNDONE: Implement batching here.
                    //
                    // We want to allow built-in metadata in metadata values here. 
                    // For example, so that an Idl file can specify that its Tlb output should be named %(Filename).tlb.
                    // 
                    // In other words, we want batching. However, we won't need to go to the trouble of using the regular batching code!
                    // That's because that code is all about grouping into buckets of similar items. In this context, we're not
                    // invoking a task, and it's fine to process each item individually, which will always give the correct results.
                    //
                    // For the CTP, to make the minimal change, we will not do this quite correctly.
                    //
                    // We will do this:
                    // -- check whether any metadata values or their conditions contain any bare built-in metadata expressions,
                    //    or whether they contain any custom metadata && the Include involved an @(itemlist) expression.
                    // -- if either case is found, we go ahead and evaluate all the metadata separately for each item.
                    // -- otherwise we can do the old thing (evaluating all metadata once then applying to all items)
                    // 
                    // This algorithm gives the correct results except when:
                    // -- batchable expressions exist on the include, exclude, or condition on the item element itself
                    //
                    // It means that 99% of cases still go through the old code, which is best for the CTP.
                    // When we ultimately implement this correctly, we should make sure we optimize for the case of very many items
                    // and little metadata, none of which varies between items.

                    // Do not expand properties as they have been already expanded by the lazy evaluator upon item operation construction.
                    // Prior to lazy evaluation ExpanderOptions.ExpandAll was used.
                    const ExpanderOptions metadataExpansionOptions = ExpanderOptions.ExpandAll;

                    needToExpandMetadata ??= NeedToExpandMetadataForEachItem(metadata, out _);

                    if (needToExpandMetadata.Value)
                    {
                        foreach (var itemContext in itemBatchingContexts)
                        {
                            _expander.Metadata = itemContext.GetMetadataTable();

                            foreach (var metadataElement in metadata)
                            {
                                if (!EvaluateCondition(metadataElement.Condition, metadataElement, metadataExpansionOptions, ParserOptions.AllowAll, _expander, _lazyEvaluator))
                                {
                                    continue;
                                }

                                string evaluatedValue = _expander.ExpandIntoStringLeaveEscaped(metadataElement.Value, metadataExpansionOptions, metadataElement.Location);

                                itemContext.OperationItem.SetMetadata(metadataElement, FileUtilities.MaybeAdjustFilePath(evaluatedValue, metadataElement.ContainingProject.DirectoryPath));
                            }
                        }

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }
                    // End of pseudo batching
                    ////////////////////////////////////////////////////
                    // Start of old code
                    else
                    {
                        // Metadata expressions are allowed here.
                        // Temporarily gather and expand these in a table so they can reference other metadata elements above.
                        EvaluatorMetadataTable metadataTable = new EvaluatorMetadataTable(_itemType);
                        _expander.Metadata = metadataTable;

                        // Also keep a list of everything so we can get the predecessor objects correct.
                        List<Pair<ProjectMetadataElement, string>> metadataList = new List<Pair<ProjectMetadataElement, string>>(metadata.Count);

                        foreach (var metadataElement in metadata)
                        {
                            // Because of the checking above, it should be safe to expand metadata in conditions; the condition
                            // will be true for either all the items or none
                            if (
                                !EvaluateCondition(
                                    metadataElement.Condition,
                                    metadataElement,
                                    metadataExpansionOptions,
                                    ParserOptions.AllowAll,
                                    _expander,
                                    _lazyEvaluator
                                    ))
                            {
                                continue;
                            }

                            string evaluatedValue = _expander.ExpandIntoStringLeaveEscaped(metadataElement.Value, metadataExpansionOptions, metadataElement.Location);
                            evaluatedValue = FileUtilities.MaybeAdjustFilePath(evaluatedValue, metadataElement.ContainingProject.DirectoryPath);

                            metadataTable.SetValue(metadataElement, evaluatedValue);
                            metadataList.Add(new Pair<ProjectMetadataElement, string>(metadataElement, evaluatedValue));
                        }

                        // Apply those metadata to each item
                        // Note that several items could share the same metadata objects

                        // Set all the items at once to make a potential copy-on-write optimization possible.
                        // This is valuable in the case where one item element evaluates to
                        // many items (either by semicolon or wildcards)
                        // and that item also has the same piece/s of metadata for each item.
                        _itemFactory.SetMetadata(metadataList, itemBatchingContexts.Select(i => i.OperationItem));

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }
                }
            }

            private static IEnumerable<string> GetMetadataValuesAndConditions(ImmutableList<ProjectMetadataElement> metadata)
            {
                foreach (var metadataElement in metadata)
                {
                    yield return metadataElement.Value;
                    yield return metadataElement.Condition;
                }
            }

            protected bool NeedToExpandMetadataForEachItem(ImmutableList<ProjectMetadataElement> metadata, out ItemsAndMetadataPair itemsAndMetadataFound)
            {
                itemsAndMetadataFound = ExpressionShredder.GetReferencedItemNamesAndMetadata(GetMetadataValuesAndConditions(metadata));

                bool needToExpandMetadataForEachItem = false;

                if (itemsAndMetadataFound.Metadata?.Values.Count > 0)
                {
                    // If there is bare metadata of any kind, and the Include involved an item list, we should
                    // run items individually, as even non-built-in metadata might differ between items

                    if (_referencedItemLists.Count >= 0)
                    {
                        needToExpandMetadataForEachItem = true;
                    }
                    else
                    {
                        // If there is bare built-in metadata, we must always run items individually, as that almost
                        // always differs between items.

                        // UNDONE: When batching is implemented for real, we need to make sure that
                        // item definition metadata is included in all metadata operations during evaluation
                        if (itemsAndMetadataFound.Metadata.Values.Count > 0)
                        {
                            needToExpandMetadataForEachItem = true;
                        }
                    }
                }

                return needToExpandMetadataForEachItem;
            }

            /// <summary>
            /// Is this spec a single reference to a specific item?
            /// </summary>
            /// <returns>True if the item is a simple reference to the referenced item type.</returns>
            protected static bool ItemspecContainsASingleBareItemReference(ItemSpec<P, I> itemSpec, string referencedItemType)
            {
                if (itemSpec.Fragments.Count != 1)
                {
                    return false;
                }

                var itemExpressionFragment = itemSpec.Fragments[0] as ItemSpec<P, I>.ItemExpressionFragment;
                if (itemExpressionFragment == null)
                {
                    return false;
                }

                if (!itemExpressionFragment.Capture.ItemType.Equals(referencedItemType, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // If the itemSpec is a single call to an item function, like @(X->Something(...)), it may get this
                // far, but shouldn't be treated as a single reference: the item function may return entirely
                // different results from a bare reference like @(X).
                if (itemExpressionFragment.Capture.Captures is object)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
