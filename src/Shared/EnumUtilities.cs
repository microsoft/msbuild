// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace Microsoft.Build.Shared
{
    public static class EnumUtilities
    {
        private static readonly Dictionary<Enum, string> _enumStringCache = [];

        public static string GetEnumString(Enum value)
        {
            if (_enumStringCache.TryGetValue(value, out string? stringValue))
            {
                return stringValue;
            }

            _enumStringCache[value] = value.ToString();

            return _enumStringCache[value];
        }
    }
}
