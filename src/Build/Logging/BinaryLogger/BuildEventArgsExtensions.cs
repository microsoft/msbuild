﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using static Microsoft.Build.Internal.Utilities;

namespace Microsoft.Build.Logging;

/// <summary>
/// Helper extension methods for working with data passed via
/// <see cref="ProjectEvaluationFinishedEventArgs"/> and <see cref="ProjectStartedEventArgs"/>
/// </summary>
public static class BuildEventArgsExtensions
{
    /// <summary>
    /// Lazy enumerates and strong types properties from Properties property.
    /// </summary>
    public static IEnumerable<(string propertyName, string propertyValue)> EnumerateProperties(
        this ProjectEvaluationFinishedEventArgs eventArgs)
        => EnumerateProperties(eventArgs.Properties);

    /// <summary>
    /// Lazy enumerates and strong types properties from Properties property.
    /// </summary>
    public static IEnumerable<(string propertyName, string propertyValue)> EnumerateProperties(
        this ProjectStartedEventArgs eventArgs)
        => EnumerateProperties(eventArgs.Properties);

    /// <summary>
    /// Lazy enumerates and partially strong types items from Items property.
    /// The actual item value might be wrapped to be able to provide defined interface
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<(string itemType, IItemData itemValue)> EnumerateItems(
        this ProjectEvaluationFinishedEventArgs eventArgs)
        => EnumerateItems(eventArgs.Items);

    /// <summary>
    /// Lazy enumerates and strong types items from Items property.
    /// The actual item value might be wrapped to be able to provide defined interface
    /// </summary>
    public static IEnumerable<(string itemType, IItemData itemValue)> EnumerateItems(
        this ProjectStartedEventArgs eventArgs)
        => EnumerateItems(eventArgs.Items);

    /// <summary>
    /// Enumerates custom metadata of an item, without known type.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateMetadata(this IItemData itemData)
    {
        if (itemData is IItemDataWithMetadata iTemDataWithMetadata)
        {
            return iTemDataWithMetadata.EnumerateMetadata();
        }

        if (itemData is ITaskItem taskItem)
        {
            return ITaskItemExtensions.EnumerateMetadata(taskItem);
        }

        return [];
    }

    /// <summary>
    /// Gets the item evaluated include data. It is in fact a 'specification' of the item (e.g. path on disk to a specific ProjectReference)
    /// </summary>
    public static string GetEvaluatedInclude(this IItemData itemData)
        => itemData.ItemSpec;

    private static IEnumerable<(string propertyName, string propertyValue)> EnumerateProperties(IEnumerable? properties)
        => Internal.Utilities.EnumerateProperties(properties);

    private static IEnumerable<(string itemType, IItemData itemValue)> EnumerateItems(IEnumerable? items)
        => Internal.Utilities.EnumerateItems(items);
}
