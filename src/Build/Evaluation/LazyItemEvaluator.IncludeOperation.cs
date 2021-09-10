// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Construction;
using Microsoft.Build.Eventing;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.Collections;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        class IncludeOperation : LazyItemOperation
        {
            readonly int _elementOrder;
            
            readonly string _rootDirectory;

            readonly ImmutableSegmentedList<string> _excludes;

            readonly ImmutableList<ProjectMetadataElement> _metadata;

            public IncludeOperation(IncludeOperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
                : base(builder, lazyEvaluator)
            {
                _elementOrder = builder.ElementOrder;
                _rootDirectory = builder.RootDirectory;

                _excludes = builder.Excludes.ToImmutable();
                _metadata = builder.Metadata.ToImmutable();
            }

            protected override ImmutableList<I> SelectItems(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                var itemsToAdd = ImmutableList.CreateBuilder<I>();

                Lazy<Func<string, bool>> excludeTester = null;
                ImmutableList<string>.Builder excludePatterns = ImmutableList.CreateBuilder<string>();
                if (_excludes != null)
                {
                    // STEP 4: Evaluate, split, expand and subtract any Exclude
                    foreach (string exclude in _excludes)
                    {
                        string excludeExpanded = _expander.ExpandIntoStringLeaveEscaped(exclude, ExpanderOptions.ExpandPropertiesAndItems, _itemElement.ExcludeLocation);
                        var excludeSplits = ExpressionShredder.SplitSemiColonSeparatedList(excludeExpanded);
                        excludePatterns.AddRange(excludeSplits);
                    }

                    if (excludePatterns.Count > 0)
                    {
                        excludeTester = new Lazy<Func<string, bool>>(() => EngineFileUtilities.GetFileSpecMatchTester(excludePatterns, _rootDirectory));
                    }
                }

                ISet<string> excludePatternsForGlobs = null;

                foreach (var fragment in _itemSpec.Fragments)
                {
                    if (fragment is ItemSpec<P, I>.ItemExpressionFragment itemReferenceFragment)
                    {
                        // STEP 3: If expression is "@(x)" copy specified list with its metadata, otherwise just treat as string
                        var itemsFromExpression = _expander.ExpandExpressionCaptureIntoItems(
                            itemReferenceFragment.Capture,
                            _evaluatorData,
                            _itemFactory,
                            ExpanderOptions.ExpandItems,
                            includeNullEntries: false,
                            isTransformExpression: out _,
                            elementLocation: _itemElement.IncludeLocation);

                        itemsToAdd.AddRange(
                            excludeTester != null
                                ? itemsFromExpression.Where(item => !excludeTester.Value(item.EvaluatedInclude))
                                : itemsFromExpression);
                    }
                    else if (fragment is ValueFragment valueFragment)
                    {
                        string value = valueFragment.TextFragment;

                        if (excludeTester?.Value(EscapingUtilities.UnescapeAll(value)) != true)
                        {
                            var item = _itemFactory.CreateItem(value, value, _itemElement.ContainingProject.FullPath);
                            itemsToAdd.Add(item);
                        }
                    }
                    else if (fragment is GlobFragment globFragment)
                    {
                        // If this item is behind a false condition and represents a full drive/filesystem scan, expanding it is
                        // almost certainly undesired. It should be skipped to avoid evaluation taking an excessive amount of time.
                        bool skipGlob = !_conditionResult && globFragment.IsFullFileSystemScan && !Traits.Instance.EscapeHatches.AlwaysEvaluateDangerousGlobs;
                        if (!skipGlob)
                        {
                            string glob = globFragment.TextFragment;

                            if (excludePatternsForGlobs == null)
                            {
                                excludePatternsForGlobs = BuildExcludePatternsForGlobs(globsToIgnore, excludePatterns);
                            }

                            string[] includeSplitFilesEscaped;
                            if (MSBuildEventSource.Log.IsEnabled())
                            {
                                MSBuildEventSource.Log.ExpandGlobStart(_rootDirectory, glob, string.Join(", ", excludePatternsForGlobs));
                            }
                            using (_lazyEvaluator._evaluationProfiler.TrackGlob(_rootDirectory, glob, excludePatternsForGlobs))
                            {
                                includeSplitFilesEscaped = EngineFileUtilities.GetFileListEscaped(
                                    _rootDirectory,
                                    glob,
                                    excludePatternsForGlobs,
                                    fileMatcher: FileMatcher
                                );
                            }
                            if (MSBuildEventSource.Log.IsEnabled())
                            {
                                MSBuildEventSource.Log.ExpandGlobStop(_rootDirectory, glob, string.Join(", ", excludePatternsForGlobs));
                            }

                            foreach (string includeSplitFileEscaped in includeSplitFilesEscaped)
                            {
                                itemsToAdd.Add(_itemFactory.CreateItem(includeSplitFileEscaped, glob, _itemElement.ContainingProject.FullPath));
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(fragment.GetType().ToString());
                    }
                }

                return itemsToAdd.ToImmutable();
            }

            private static ISet<string> BuildExcludePatternsForGlobs(ImmutableHashSet<string> globsToIgnore, ImmutableList<string>.Builder excludePatterns)
            {
                var anyExcludes = excludePatterns.Count > 0;
                var anyGlobsToIgnore = globsToIgnore.Count > 0;

                if (anyGlobsToIgnore && anyExcludes)
                {
                    return excludePatterns.Concat(globsToIgnore).ToImmutableHashSet();
                }

                return anyExcludes ? excludePatterns.ToImmutableHashSet() : globsToIgnore;
            }

            protected override void MutateItems(ImmutableList<I> items)
            {
                DecorateItemsWithMetadata(items.Select(i => new ItemBatchingContext(i)), _metadata);
            }

            protected override void SaveItems(ImmutableList<I> items, OrderedItemDataCollection.Builder listBuilder)
            {
                foreach (var item in items)
                {
                    listBuilder.Add(new ItemData(item, _itemElement, _elementOrder, _conditionResult));
                }
            }
        }

        class IncludeOperationBuilder : OperationBuilderWithMetadata
        {
            public int ElementOrder { get; set; }
            public string RootDirectory { get; set; }

            public ImmutableSegmentedList<string>.Builder Excludes { get; } = ImmutableSegmentedList.CreateBuilder<string>();

            public IncludeOperationBuilder(ProjectItemElement itemElement, bool conditionResult) : base(itemElement, conditionResult)
            {
            }
        }
    }
}
