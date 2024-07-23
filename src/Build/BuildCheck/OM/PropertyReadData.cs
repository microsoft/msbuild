﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck;

/// <summary>
/// Information about property being accessed - whether during evaluation or build.
/// </summary>
internal class PropertyReadData(
    string projectFilePath,
    int? projectInstanceId,
    string propertyName,
    IMsBuildElementLocation elementLocation,
    bool isUninitialized,
    PropertyReadContext propertyReadContext)
    : AnalysisData(projectFilePath, projectInstanceId)
{
    public PropertyReadData(
        string projectFilePath,
        int? projectInstanceId,
        PropertyReadInfo propertyReadInfo)
        : this(projectFilePath,
            projectInstanceId,
            propertyReadInfo.PropertyName.Substring(propertyReadInfo.StartIndex, propertyReadInfo.EndIndex - propertyReadInfo.StartIndex + 1),
            propertyReadInfo.ElementLocation,
            propertyReadInfo.IsUninitialized,
            propertyReadInfo.PropertyReadContext)
    { }

    /// <summary>
    /// Name of the property that was accessed.
    /// </summary>
    public string PropertyName { get; } = propertyName;

    /// <summary>
    /// Location of the property access.
    /// </summary>
    public IMsBuildElementLocation ElementLocation { get; } = elementLocation;

    /// <summary>
    /// Indicates whether the property was accessed before being initialized.
    /// </summary>
    public bool IsUninitialized { get; } = isUninitialized;

    /// <summary>
    /// Gets the context type in which the property was accessed.
    /// </summary>
    public PropertyReadContext PropertyReadContext { get; } = propertyReadContext;
}
