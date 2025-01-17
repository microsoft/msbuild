// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
#if DEBUG
using System.Diagnostics;
#endif
using System.Linq;
using System.Threading;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
        where P : class, IProperty, IEquatable<P>, IValued
        where I : class, IItem<M>, IMetadataTable
        where M : class, IMetadatum
        where D : class, IItemDefinition<M>
    {
        private readonly IEvaluatorData<P, I, M, D> _outerEvaluatorData;
        private readonly Expander<P, I> _outerExpander;
        private readonly IEvaluatorData<P, I, M, D> _evaluatorData;
        private readonly Expander<P, I> _expander;
        private readonly IItemFactory<I, I> _itemFactory;
        private readonly LoggingContext _loggingContext;
        private readonly EvaluationProfiler _evaluationProfiler;

        private int _nextElementOrder = 0;

        private Dictionary<string, LazyItemList> _itemLists = Traits.Instance.EscapeHatches.UseCaseSensitiveItemNames ?
            new Dictionary<string, LazyItemList>() :
            new Dictionary<string, LazyItemList>(StringComparer.OrdinalIgnoreCase);

        protected EvaluationContext EvaluationContext { get; }

        protected IFileSystem FileSystem => EvaluationContext.FileSystem;

        protected FileMatcher FileMatcher => EvaluationContext.FileMatcher;

        public LazyItemEvaluator(IEvaluatorData<P, I, M, D> data, IItemFactory<I, I> itemFactory, LoggingContext loggingContext, EvaluationProfiler evaluationProfiler, EvaluationContext evaluationContext)
        {
            _outerEvaluatorData = data;
            _outerExpander = new Expander<P, I>(_outerEvaluatorData, _outerEvaluatorData, evaluationContext, loggingContext);
            _evaluatorData = new EvaluatorData(_outerEvaluatorData, _itemLists);
            _expander = new Expander<P, I>(_evaluatorData, _evaluatorData, evaluationContext, loggingContext);
            _itemFactory = itemFactory;
            _loggingContext = loggingContext;
            _evaluationProfiler = evaluationProfiler;

            EvaluationContext = evaluationContext;
        }

        public bool EvaluateConditionWithCurrentState(ProjectElement element, ExpanderOptions expanderOptions, ParserOptions parserOptions)
        {
            return EvaluateCondition(element.Condition, element, expanderOptions, parserOptions, _expander, this);
        }

        private static bool EvaluateCondition(
            string condition,
            ProjectElement element,
            ExpanderOptions expanderOptions,
            ParserOptions parserOptions,
            Expander<P, I> expander,
            LazyItemEvaluator<P, I, M, D> lazyEvaluator)
        {
            if (condition?.Length == 0)
            {
                return true;
            }
            MSBuildEventSource.Log.EvaluateConditionStart(condition);

            using (lazyEvaluator._evaluationProfiler.TrackCondition(element.ConditionLocation, condition))
            {
                bool result = ConditionEvaluator.EvaluateCondition(
                    condition,
                    parserOptions,
                    expander,
                    expanderOptions,
                    GetCurrentDirectoryForConditionEvaluation(element, lazyEvaluator),
                    element.ConditionLocation,
                    lazyEvaluator.FileSystem,
                    loggingContext: lazyEvaluator._loggingContext);
                MSBuildEventSource.Log.EvaluateConditionStop(condition, result);

                return result;
            }
        }

        /// <summary>
        /// COMPAT: Whidbey used the "current project file/targets" directory for evaluating Import and PropertyGroup conditions
        /// Orcas broke this by using the current root project file for all conditions
        /// For Dev10+, we'll fix this, and use the current project file/targets directory for Import, ImportGroup and PropertyGroup
        /// but the root project file for the rest. Inside of targets will use the root project file as always.
        /// </summary>
        private static string GetCurrentDirectoryForConditionEvaluation(ProjectElement element, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
        {
            if (element is ProjectPropertyGroupElement || element is ProjectImportElement || element is ProjectImportGroupElement)
            {
                return element.ContainingProject.DirectoryPath;
            }
            else
            {
                return lazyEvaluator._outerEvaluatorData.Directory;
            }
        }

        public struct ItemData
        {
            public ItemData(I item, ProjectItemElement originatingItemElement, int elementOrder, bool conditionResult, string normalizedItemValue = null)
            {
                Item = item;
                OriginatingItemElement = originatingItemElement;
                ElementOrder = elementOrder;
                ConditionResult = conditionResult;
                _normalizedItemValue = normalizedItemValue;
            }

            public readonly ItemData Clone(IItemFactory<I, I> itemFactory, ProjectItemElement initialItemElementForFactory)
            {
                // setting the factory's item element to the original item element that produced the item
                // otherwise you get weird things like items that appear to have been produced by update elements
                itemFactory.ItemElement = OriginatingItemElement;
                var clonedItem = itemFactory.CreateItem(Item, OriginatingItemElement.ContainingProject.FullPath);
                itemFactory.ItemElement = initialItemElementForFactory;

                return new ItemData(clonedItem, OriginatingItemElement, ElementOrder, ConditionResult, _normalizedItemValue);
            }

            public I Item { get; }
            public ProjectItemElement OriginatingItemElement { get; }
            public int ElementOrder { get; }
            public bool ConditionResult { get; }

            /// <summary>
            /// Lazily created normalized item value.
            /// </summary>
            private string _normalizedItemValue;
            public string NormalizedItemValue
            {
                get
                {
                    var normalizedItemValue = Volatile.Read(ref _normalizedItemValue);
                    if (normalizedItemValue == null)
                    {
                        normalizedItemValue = FileUtilities.NormalizePathForComparisonNoThrow(Item.EvaluatedInclude, Item.ProjectDirectory);
                        Volatile.Write(ref _normalizedItemValue, normalizedItemValue);
                    }
                    return normalizedItemValue;
                }
            }
        }

        private class MemoizedOperation : IItemOperation
        {
            public LazyItemOperation Operation { get; }
            private Dictionary<ISet<string>, OrderedItemDataCollection> _cache;

            private bool _isReferenced;
#if DEBUG
            private int _applyCalls;
#endif

            public MemoizedOperation(LazyItemOperation operation)
            {
                Operation = operation;
            }

            public void Apply(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
#if DEBUG
                CheckInvariant();
#endif

                Operation.Apply(listBuilder, globsToIgnore);

                // cache results if somebody is referencing this operation
                if (_isReferenced)
                {
                    AddItemsToCache(globsToIgnore, listBuilder.ToImmutable());
                }
#if DEBUG
                _applyCalls++;
                CheckInvariant();
#endif
            }

#if DEBUG
            private void CheckInvariant()
            {
                if (_isReferenced)
                {
                    var cacheCount = _cache?.Count ?? 0;
                    Debug.Assert(_applyCalls == cacheCount, "Apply should only be called once per globsToIgnore. Otherwise caching is not working");
                }
                else
                {
                    // non referenced operations should not be cached
                    // non referenced operations should have as many apply calls as the number of cache keys of the immediate dominator with _isReferenced == true
                    Debug.Assert(_cache == null);
                }
            }
#endif

            public bool TryGetFromCache(ISet<string> globsToIgnore, out OrderedItemDataCollection items)
            {
                if (_cache != null)
                {
                    return _cache.TryGetValue(globsToIgnore, out items);
                }

                items = null;
                return false;
            }

            /// <summary>
            /// Somebody is referencing this operation
            /// </summary>
            public void MarkAsReferenced()
            {
                _isReferenced = true;
            }

            private void AddItemsToCache(ImmutableHashSet<string> globsToIgnore, OrderedItemDataCollection items)
            {
                if (_cache == null)
                {
                    _cache = new Dictionary<ISet<string>, OrderedItemDataCollection>();
                }

                _cache[globsToIgnore] = items;
            }
        }

        private class LazyItemList
        {
            private readonly LazyItemList _previous;
            private readonly MemoizedOperation _memoizedOperation;

            public LazyItemList(LazyItemList previous, LazyItemOperation operation)
            {
                _previous = previous;
                _memoizedOperation = new MemoizedOperation(operation);
            }

            public ImmutableList<I> GetMatchedItems(ImmutableHashSet<string> globsToIgnore)
            {
                ImmutableList<I>.Builder items = ImmutableList.CreateBuilder<I>();
                foreach (ItemData data in GetItemData(globsToIgnore))
                {
                    if (data.ConditionResult)
                    {
                        items.Add(data.Item);
                    }
                }

                return items.ToImmutable();
            }

            public OrderedItemDataCollection.Builder GetItemData(ImmutableHashSet<string> globsToIgnore)
            {
                // Cache results only on the LazyItemOperations whose results are required by an external caller (via GetItems). This means:
                //   - Callers of GetItems who have announced ahead of time that they would reference an operation (via MarkAsReferenced())
                // This includes: item references (Include="@(foo)") and metadata conditions (Condition="@(foo->Count()) == 0")
                // Without ahead of time notifications more computation is done than needed when the results of a future operation are requested
                // The future operation is part of another item list referencing this one (making this operation part of the tail).
                // The future operation will compute this list but since no ahead of time notifications have been made by callers, it won't cache the
                // intermediary operations that would be requested by those callers.
                //   - Callers of GetItems that cannot announce ahead of time. This includes item referencing conditions on
                // Item Groups and Item Elements. However, those conditions are performed eagerly outside of the LazyItemEvaluator, so they will run before
                // any item referencing operations from inside the LazyItemEvaluator. This
                //
                // If the head of this LazyItemList is uncached, then the tail may contain cached and un-cached nodes.
                // In this case we have to compute the head plus the part of the tail up to the first cached operation.
                //
                // The cache is based on a couple of properties:
                // - uses immutable lists for structural sharing between multiple cached nodes (multiple include operations won't have duplicated memory for the common items)
                // - if an operation is cached for a certain set of globsToIgnore, then the entire operation tail can be reused. This is because (i) the structure of LazyItemLists
                // does not mutate: one can add operations on top, but the base never changes, and (ii) the globsToIgnore passed to the tail is the concatenation between
                // the globsToIgnore received as an arg, and the globsToIgnore produced by the head (if the head is a Remove operation)

                OrderedItemDataCollection items;
                if (_memoizedOperation.TryGetFromCache(globsToIgnore, out items))
                {
                    return items.ToBuilder();
                }
                else
                {
                    // tell the cache that this operation's result is needed by an external caller
                    // this is required for callers that cannot tell the item list ahead of time that
                    // they would be using an operation
                    MarkAsReferenced();

                    return ComputeItems(this, globsToIgnore);
                }
            }

            /// <summary>
            /// Applies uncached item operations (include, remove, update) in order. Since Remove effectively overwrites Include or Update,
            /// Remove operations are preprocessed (adding to globsToIgnore) to create a longer list of globs we don't need to process
            /// properly because we know they will be removed. Update operations are batched as much as possible, meaning rather
            /// than being applied immediately, they are combined into a dictionary of UpdateOperations that need to be applied. This
            /// is to optimize the case in which as series of UpdateOperations, each of which affects a single ItemSpec, are applied to all
            /// items in the list, leading to a quadratic-time operation.
            /// </summary>
            private static OrderedItemDataCollection.Builder ComputeItems(LazyItemList lazyItemList, ImmutableHashSet<string> globsToIgnore)
            {
                // Stack of operations up to the first one that's cached (exclusive)
                Stack<LazyItemList> itemListStack = new Stack<LazyItemList>();

                OrderedItemDataCollection.Builder items = null;

                // Keep a separate stack of lists of globs to ignore that only gets modified for Remove operations
                Stack<ImmutableHashSet<string>> globsToIgnoreStack = null;

                for (var currentList = lazyItemList; currentList != null; currentList = currentList._previous)
                {
                    var globsToIgnoreFromFutureOperations = globsToIgnoreStack?.Peek() ?? globsToIgnore;

                    OrderedItemDataCollection itemsFromCache;
                    if (currentList._memoizedOperation.TryGetFromCache(globsToIgnoreFromFutureOperations, out itemsFromCache))
                    {
                        // the base items on top of which to apply the uncached operations are the items of the first operation that is cached
                        items = itemsFromCache.ToBuilder();
                        break;
                    }

                    // If this is a remove operation, then add any globs that will be removed
                    //  to a list of globs to ignore in previous operations
                    if (currentList._memoizedOperation.Operation is RemoveOperation removeOperation)
                    {
                        globsToIgnoreStack ??= new Stack<ImmutableHashSet<string>>();

                        var globsToIgnoreForPreviousOperations = removeOperation.GetRemovedGlobs();
                        foreach (var globToRemove in globsToIgnoreFromFutureOperations)
                        {
                            globsToIgnoreForPreviousOperations.Add(globToRemove);
                        }

                        globsToIgnoreStack.Push(globsToIgnoreForPreviousOperations.ToImmutable());
                    }

                    itemListStack.Push(currentList);
                }

                if (items == null)
                {
                    items = OrderedItemDataCollection.CreateBuilder();
                }

                ImmutableHashSet<string> currentGlobsToIgnore = globsToIgnoreStack == null ? globsToIgnore : globsToIgnoreStack.Peek();

                Dictionary<string, UpdateOperation> itemsWithNoWildcards = new Dictionary<string, UpdateOperation>(StringComparer.OrdinalIgnoreCase);
                bool addedToBatch = false;

                // Walk back down the stack of item lists applying operations
                while (itemListStack.Count > 0)
                {
                    var currentList = itemListStack.Pop();

                    if (currentList._memoizedOperation.Operation is UpdateOperation op)
                    {
                        bool addToBatch = true;
                        int i;
                        // The TextFragments are things like abc.def or x*y.*z.
                        for (i = 0; i < op.Spec.Fragments.Count; i++)
                        {
                            ItemSpecFragment frag = op.Spec.Fragments[i];
                            if (MSBuildConstants.CharactersForExpansion.Any(frag.TextFragment.Contains))
                            {
                                // Fragment contains wild cards, items, or properties. Cannot batch over it using a dictionary.
                                addToBatch = false;
                                break;
                            }

                            string fullPath = FileUtilities.NormalizePathForComparisonNoThrow(frag.TextFragment, frag.ProjectDirectory);
                            if (itemsWithNoWildcards.ContainsKey(fullPath))
                            {
                                // Another update will already happen on this path. Make that happen before evaluating this one.
                                addToBatch = false;
                                break;
                            }
                            else
                            {
                                itemsWithNoWildcards.Add(fullPath, op);
                            }
                        }
                        if (!addToBatch)
                        {
                            // We found a wildcard. Remove any fragments associated with the current operation and process them later.
                            for (int j = 0; j < i; j++)
                            {
                                itemsWithNoWildcards.Remove(currentList._memoizedOperation.Operation.Spec.Fragments[j].TextFragment);
                            }
                        }
                        else
                        {
                            addedToBatch = true;
                            continue;
                        }
                    }

                    if (addedToBatch)
                    {
                        addedToBatch = false;
                        ProcessNonWildCardItemUpdates(itemsWithNoWildcards, items);
                    }

                    // If this is a remove operation, then it could modify the globs to ignore, so pop the potentially
                    //  modified entry off the stack of globs to ignore
                    if (currentList._memoizedOperation.Operation is RemoveOperation)
                    {
                        globsToIgnoreStack.Pop();
                        currentGlobsToIgnore = globsToIgnoreStack.Count == 0 ? globsToIgnore : globsToIgnoreStack.Peek();
                    }

                    currentList._memoizedOperation.Apply(items, currentGlobsToIgnore);
                }

                // We finished looping through the operations. Now process the final batch if necessary.
                ProcessNonWildCardItemUpdates(itemsWithNoWildcards, items);

                return items;
            }

            private static void ProcessNonWildCardItemUpdates(Dictionary<string, UpdateOperation> itemsWithNoWildcards, OrderedItemDataCollection.Builder items)
            {
                if (itemsWithNoWildcards.Count > 0)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        string fullPath = FileUtilities.NormalizePathForComparisonNoThrow(items[i].Item.EvaluatedInclude, items[i].Item.ProjectDirectory);
                        if (itemsWithNoWildcards.TryGetValue(fullPath, out UpdateOperation op))
                        {
                            items[i] = op.UpdateItem(items[i]);
                        }
                    }
                    itemsWithNoWildcards.Clear();
                }
            }

            public void MarkAsReferenced()
            {
                _memoizedOperation.MarkAsReferenced();
            }
        }

        private class OperationBuilder
        {
            // WORKAROUND: Unnecessary boxed allocation: https://github.com/dotnet/corefx/issues/24563
            private static readonly ImmutableDictionary<string, LazyItemList> s_emptyIgnoreCase = ImmutableDictionary.Create<string, LazyItemList>(StringComparer.OrdinalIgnoreCase);

            public ProjectItemElement ItemElement { get; set; }
            public string ItemType { get; set; }
            public ItemSpec<P, I> ItemSpec { get; set; }

            public ImmutableDictionary<string, LazyItemList>.Builder ReferencedItemLists { get; } = Traits.Instance.EscapeHatches.UseCaseSensitiveItemNames ?
                ImmutableDictionary.CreateBuilder<string, LazyItemList>() :
                s_emptyIgnoreCase.ToBuilder();

            public bool ConditionResult { get; set; }

            public OperationBuilder(ProjectItemElement itemElement, bool conditionResult)
            {
                ItemElement = itemElement;
                ItemType = itemElement.ItemType;
                ConditionResult = conditionResult;
            }
        }

        private class OperationBuilderWithMetadata : OperationBuilder
        {
            public readonly ImmutableArray<ProjectMetadataElement>.Builder Metadata = ImmutableArray.CreateBuilder<ProjectMetadataElement>();

            public OperationBuilderWithMetadata(ProjectItemElement itemElement, bool conditionResult) : base(itemElement, conditionResult)
            {
            }
        }

        private void AddReferencedItemList(string itemType, IDictionary<string, LazyItemList> referencedItemLists)
        {
            if (_itemLists.TryGetValue(itemType, out LazyItemList itemList))
            {
                itemList.MarkAsReferenced();
                referencedItemLists[itemType] = itemList;
            }
        }

        public IEnumerable<ItemData> GetAllItemsDeferred()
        {
            return _itemLists.Values.SelectMany(itemList => itemList.GetItemData(ImmutableHashSet<string>.Empty))
                                    .OrderBy(itemData => itemData.ElementOrder);
        }

        public void ProcessItemElement(string rootDirectory, ProjectItemElement itemElement, bool conditionResult)
        {
            LazyItemOperation operation = null;

            if (itemElement.IncludeLocation != null)
            {
                operation = BuildIncludeOperation(rootDirectory, itemElement, conditionResult);
            }
            else if (itemElement.RemoveLocation != null)
            {
                operation = BuildRemoveOperation(rootDirectory, itemElement, conditionResult);
            }
            else if (itemElement.UpdateLocation != null)
            {
                operation = BuildUpdateOperation(rootDirectory, itemElement, conditionResult);
            }
            else
            {
                ErrorUtilities.ThrowInternalErrorUnreachable();
            }

            _itemLists.TryGetValue(itemElement.ItemType, out LazyItemList previousItemList);
            LazyItemList newList = new LazyItemList(previousItemList, operation);
            _itemLists[itemElement.ItemType] = newList;
        }

        private UpdateOperation BuildUpdateOperation(string rootDirectory, ProjectItemElement itemElement, bool conditionResult)
        {
            OperationBuilderWithMetadata operationBuilder = new OperationBuilderWithMetadata(itemElement, conditionResult);

            // Proces Update attribute
            ProcessItemSpec(rootDirectory, itemElement.Update, itemElement.UpdateLocation, operationBuilder);

            ProcessMetadataElements(itemElement, operationBuilder);

            return new UpdateOperation(operationBuilder, this);
        }

        private IncludeOperation BuildIncludeOperation(string rootDirectory, ProjectItemElement itemElement, bool conditionResult)
        {
            IncludeOperationBuilder operationBuilder = new IncludeOperationBuilder(itemElement, conditionResult);
            operationBuilder.ElementOrder = _nextElementOrder++;
            operationBuilder.RootDirectory = rootDirectory;
            operationBuilder.ConditionResult = conditionResult;

            // Process include
            ProcessItemSpec(rootDirectory, itemElement.Include, itemElement.IncludeLocation, operationBuilder);

            // Code corresponds to Evaluator.EvaluateItemElement

            // Process exclude (STEP 4: Evaluate, split, expand and subtract any Exclude)
            if (itemElement.Exclude.Length > 0)
            {
                // Expand properties here, because a property may have a value which is an item reference (ie "@(Bar)"), and
                //  if so we need to add the right item reference
                string evaluatedExclude = _expander.ExpandIntoStringLeaveEscaped(itemElement.Exclude, ExpanderOptions.ExpandProperties, itemElement.ExcludeLocation);

                if (evaluatedExclude.Length > 0)
                {
                    var excludeSplits = ExpressionShredder.SplitSemiColonSeparatedList(evaluatedExclude);

                    foreach (var excludeSplit in excludeSplits)
                    {
                        operationBuilder.Excludes.Add(excludeSplit);
                        AddItemReferences(excludeSplit, operationBuilder, itemElement.ExcludeLocation);
                    }
                }
            }

            // Process Metadata (STEP 5: Evaluate each metadata XML and apply them to each item we have so far)
            ProcessMetadataElements(itemElement, operationBuilder);

            return new IncludeOperation(operationBuilder, this);
        }

        private RemoveOperation BuildRemoveOperation(string rootDirectory, ProjectItemElement itemElement, bool conditionResult)
        {
            RemoveOperationBuilder operationBuilder = new RemoveOperationBuilder(itemElement, conditionResult);

            ProcessItemSpec(rootDirectory, itemElement.Remove, itemElement.RemoveLocation, operationBuilder);

            // Process MatchOnMetadata
            if (itemElement.MatchOnMetadata.Length > 0)
            {
                string evaluatedmatchOnMetadata = _expander.ExpandIntoStringLeaveEscaped(itemElement.MatchOnMetadata, ExpanderOptions.ExpandProperties, itemElement.MatchOnMetadataLocation);

                if (evaluatedmatchOnMetadata.Length > 0)
                {
                    var matchOnMetadataSplits = ExpressionShredder.SplitSemiColonSeparatedList(evaluatedmatchOnMetadata);

                    foreach (var matchOnMetadataSplit in matchOnMetadataSplits)
                    {
                        AddItemReferences(matchOnMetadataSplit, operationBuilder, itemElement.MatchOnMetadataLocation);
                        string metadataExpanded = _expander.ExpandIntoStringLeaveEscaped(matchOnMetadataSplit, ExpanderOptions.ExpandPropertiesAndItems, itemElement.MatchOnMetadataLocation);
                        var metadataSplits = ExpressionShredder.SplitSemiColonSeparatedList(metadataExpanded);
                        operationBuilder.MatchOnMetadata.AddRange(metadataSplits);
                    }
                }
            }

            operationBuilder.MatchOnMetadataOptions = MatchOnMetadataOptions.CaseSensitive;
            if (Enum.TryParse(itemElement.MatchOnMetadataOptions, out MatchOnMetadataOptions options))
            {
                operationBuilder.MatchOnMetadataOptions = options;
            }

            return new RemoveOperation(operationBuilder, this);
        }

        private void ProcessItemSpec(string rootDirectory, string itemSpec, IElementLocation itemSpecLocation, OperationBuilder builder)
        {
            builder.ItemSpec = new ItemSpec<P, I>(itemSpec, _outerExpander, itemSpecLocation, rootDirectory);

            foreach (ItemSpecFragment fragment in builder.ItemSpec.Fragments)
            {
                if (fragment is ItemSpec<P, I>.ItemExpressionFragment itemExpression)
                {
                    AddReferencedItemLists(builder, itemExpression.Capture);
                }
            }
        }

        private static IEnumerable<string> GetExpandedMetadataValuesAndConditions(ICollection<ProjectMetadataElement> metadata, Expander<P, I> expander)
        {
            // Since we're just attempting to expand properties in order to find referenced items and not expanding metadata,
            // unexpected errors may occur when evaluating property functions on unexpanded metadata. Just ignore them if that happens.
            // See: https://github.com/dotnet/msbuild/issues/3460
            const ExpanderOptions expanderOptions = ExpanderOptions.ExpandProperties | ExpanderOptions.LeavePropertiesUnexpandedOnError;

            // Expand properties here, because a property may have a value which is an item reference (ie "@(Bar)"), and
            // if so we need to add the right item reference.
            foreach (var metadatumElement in metadata)
            {
                yield return expander.ExpandIntoStringLeaveEscaped(
                    metadatumElement.Value,
                    expanderOptions,
                    metadatumElement.Location);

                yield return expander.ExpandIntoStringLeaveEscaped(
                    metadatumElement.Condition,
                    expanderOptions,
                    metadatumElement.ConditionLocation);
            }
        }

        private void ProcessMetadataElements(ProjectItemElement itemElement, OperationBuilderWithMetadata operationBuilder)
        {
            if (itemElement.HasMetadata)
            {
                operationBuilder.Metadata.AddRange(itemElement.Metadata);

                var itemsAndMetadataFound = ExpressionShredder.GetReferencedItemNamesAndMetadata(GetExpandedMetadataValuesAndConditions(itemElement.Metadata, _expander));
                if (itemsAndMetadataFound.Items != null)
                {
                    foreach (var itemType in itemsAndMetadataFound.Items)
                    {
                        AddReferencedItemList(itemType, operationBuilder.ReferencedItemLists);
                    }
                }
            }
        }

        private void AddItemReferences(string expression, OperationBuilder operationBuilder, IElementLocation elementLocation)
        {
            if (expression.Length == 0)
            {
                return;
            }
            else
            {
                ExpressionShredder.ItemExpressionCapture match = Expander<P, I>.ExpandSingleItemVectorExpressionIntoExpressionCapture(
                    expression, ExpanderOptions.ExpandItems, elementLocation);

                if (match == null)
                {
                    return;
                }

                AddReferencedItemLists(operationBuilder, match);
            }
        }

        private void AddReferencedItemLists(OperationBuilder operationBuilder, ExpressionShredder.ItemExpressionCapture match)
        {
            if (match.ItemType != null)
            {
                AddReferencedItemList(match.ItemType, operationBuilder.ReferencedItemLists);
            }
            if (match.Captures != null)
            {
                foreach (var subMatch in match.Captures)
                {
                    AddReferencedItemLists(operationBuilder, subMatch);
                }
            }
        }
    }
}
