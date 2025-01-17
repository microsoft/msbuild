// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Build.Framework.Telemetry;

internal record TelemetryItem(string Name, object Value, bool Hashed);

/// <summary>
/// 
/// </summary>
internal interface IActivityTelemetryDataHolder
{
    IList<TelemetryItem> GetActivityProperties();
}