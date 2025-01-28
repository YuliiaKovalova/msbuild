// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        private class RemoveOperation : LazyItemOperation
        {
            private readonly ImmutableList<string> _matchOnMetadata;
            private MetadataTrie<P, I> _metadataSet;

            public RemoveOperation(RemoveOperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
                : base(builder, lazyEvaluator)
            {
                _matchOnMetadata = builder.MatchOnMetadata.ToImmutable();

                ProjectFileErrorUtilities.VerifyThrowInvalidProjectFile(
                    _matchOnMetadata.IsEmpty || _itemSpec.Fragments.All(f => f is ItemSpec<P, I>.ItemExpressionFragment),
                    new BuildEventFileInfo(string.Empty),
                    "OM_MatchOnMetadataIsRestrictedToReferencedItems");

                if (_matchOnMetadata.Any())
                {
                    _metadataSet = new MetadataTrie<P, I>(builder.MatchOnMetadataOptions, _matchOnMetadata, _itemSpec);
                }
            }

            /// <summary>
            /// Apply the Remove operation.
            /// </summary>
            /// <remarks>
            /// This override exists to apply the removing-everything short-circuit and to avoid creating a redundant list of items to remove.
            /// </remarks>
            protected override void ApplyImpl(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                if (!_conditionResult)
                {
                    return;
                }

                bool matchingOnMetadata = _matchOnMetadata.Any();
                if (!matchingOnMetadata)
                {
                    // If we're removing all items via @(ItemType), show what's being removed
                    if (ItemspecContainsASingleBareItemReference(_itemSpec, _itemElement.ItemType))
                    {
                        // First log the operation itself
                        _lazyEvaluator._loggingContext?.LogComment(
                            MessageImportance.Low,
                            "ItemRemove",
                            _itemElement.ItemType,
                            _itemSpec.ItemSpecString,
                            $"Operation: Remove by item reference");

                        // Then log each item that will be removed
                        foreach (var item in listBuilder)
                        {
                            _lazyEvaluator._loggingContext?.LogComment(
                                MessageImportance.Low,
                                "ItemRemove",
                                _itemElement.ItemType,
                                item.Item.EvaluatedInclude,
                                $"Operation: Reference @({_itemElement.ItemType}) | {GetMetadataString(item.Item)}");
                        }

                        listBuilder.Clear();
                        return;
                    }

                    // For each fragment in the Remove operation
                    foreach (var fragment in _itemSpec.Fragments)
                    {
                        // Handle wildcards/globs
                        if (fragment is GlobFragment globFragment)
                        {
                            _lazyEvaluator._loggingContext?.LogComment(
                                MessageImportance.Low,
                                "ItemRemove",
                                _itemElement.ItemType,
                                globFragment.TextFragment,
                                "Operation: Remove by glob pattern");
                        }

                        // Handle direct file references
                        else if (fragment is ValueFragment valueFragment)
                        {
                            _lazyEvaluator._loggingContext?.LogComment(
                                MessageImportance.Low,
                                "ItemRemove",
                                _itemElement.ItemType,
                                valueFragment.TextFragment,
                                "Operation: Direct file removal");
                        }

                        // Handle item references that aren't @(ItemType) style
                        else if (fragment is ItemSpec<P, I>.ItemExpressionFragment itemFragment)
                        {
                            _lazyEvaluator._loggingContext?.LogComment(
                                MessageImportance.Low,
                                "ItemRemove",
                                _itemElement.ItemType,
                                itemFragment.Capture.Value,
                                "Operation: Remove by item expression");
                        }
                    }
                }

                // Track actual items being removed
                HashSet<I> items = null;
                foreach (ItemData item in listBuilder)
                {
                    bool isMatch = matchingOnMetadata ? MatchesItemOnMetadata(item.Item) : _itemSpec.MatchesItem(item.Item);
                    if (isMatch)
                    {
                        items ??= new HashSet<I>();
                        items.Add(item.Item);

                        var reason = matchingOnMetadata ? "Remove by metadata match" : "Remove by pattern match";
                        _lazyEvaluator._loggingContext?.LogComment(
                            MessageImportance.Low,
                            "ItemRemove",
                            _itemElement.ItemType,
                            item.Item.EvaluatedInclude,
                            $"Operation: {reason} | {GetMetadataString(item.Item)}");
                    }
                }

                if (items is not null)
                {
                    listBuilder.RemoveAll(items);
                }
            }

            private bool MatchesItemOnMetadata(I item)
            {
                return _metadataSet.Contains(_matchOnMetadata.Select(m => item.GetMetadataValue(m)));
            }

            public ImmutableHashSet<string>.Builder GetRemovedGlobs()
            {
                var builder = ImmutableHashSet.CreateBuilder<string>();

                if (!_conditionResult)
                {
                    return builder;
                }

                var globs = _itemSpec.Fragments.OfType<GlobFragment>().Select(g => g.TextFragment);

                builder.UnionWith(globs);

                return builder;
            }
        }

        private class RemoveOperationBuilder : OperationBuilder
        {
            public ImmutableList<string>.Builder MatchOnMetadata { get; } = ImmutableList.CreateBuilder<string>();

            public MatchOnMetadataOptions MatchOnMetadataOptions { get; set; }

            public RemoveOperationBuilder(ProjectItemElement itemElement, bool conditionResult) : base(itemElement, conditionResult)
            {
            }
        }
    }
}
