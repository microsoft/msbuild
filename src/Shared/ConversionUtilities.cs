// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Globalization;
using System.Text;
using error = Microsoft.Build.Shared.ErrorUtilities;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class contains only static methods, which are useful throughout many
    /// of the MSBuild classes and don't really belong in any specific class.   
    /// </summary>
    internal static class ConversionUtilities
    {
        /// <summary>
        /// Converts a string to a bool.  We consider "true/false", "on/off", and 
        /// "yes/no" to be valid boolean representations in the XML.
        /// </summary>
        /// <param name="parameterValue">The string to convert.</param>
        /// <returns>Boolean true or false, corresponding to the string.</returns>
        internal static bool ConvertStringToBool(string parameterValue)
        {
            if (ValidBooleanTrue(parameterValue))
            {
                return true;
            }
            else if (ValidBooleanFalse(parameterValue))
            {
                return false;
            }
            else
            {
                // Unsupported boolean representation.
                error.VerifyThrowArgument(false, "Shared.CannotConvertStringToBool", parameterValue);
                return false;
            }
        }

        internal static bool ConvertStringToBool(string parameterValue, bool nullOrWhitespaceIsFalse)
        {
            if (nullOrWhitespaceIsFalse && string.IsNullOrWhiteSpace(parameterValue))
            {
                return false;
            }

            return ConvertStringToBool(parameterValue);
        }

        /// <summary>
        /// Returns a hex representation of a byte array.
        /// </summary>
        /// <param name="bytes">The bytes to convert</param>
        /// <returns>A string byte types formated as X2.</returns>
        internal static string ConvertByteArrayToHex(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.AppendFormat("{0:X2}", b);
            }

            return sb.ToString();
        }

        internal static bool TryConvertStringToBool(string parameterValue, out bool boolValue)
        {
            boolValue = false;
            if (ValidBooleanTrue(parameterValue))
            {
                boolValue = true;
                return true;
            }
            else if (ValidBooleanFalse(parameterValue))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the string can be successfully converted to a bool,
        /// such as "on" or "yes"
        /// </summary>
        internal static bool CanConvertStringToBool(string parameterValue)
        {
            return ValidBooleanTrue(parameterValue) || ValidBooleanFalse(parameterValue);
        }

        /// <summary>
        /// Returns true if the string represents a valid MSBuild boolean true value,
        /// such as "on", "!false", "yes"
        /// </summary>
        private static bool ValidBooleanTrue(string parameterValue)
        {
            return String.Equals(parameterValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "on", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "yes", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!false", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!off", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!no", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the string represents a valid MSBuild boolean false value,
        /// such as "!on" "off" "no" "!true"
        /// </summary>
        private static bool ValidBooleanFalse(string parameterValue)
        {
            return String.Equals(parameterValue, "false", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "off", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "no", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!true", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!on", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(parameterValue, "!yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a string like "123.456" into a double. Leading sign is allowed.
        /// </summary>
        internal static double ConvertDecimalToDouble(string number)
        {
            return Double.Parse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture.NumberFormat);
        }

        /// <summary>
        /// Converts a hex string like "0xABC" into a double.
        /// </summary>
        internal static double ConvertHexToDouble(string number)
        {
            return (double)Int32.Parse(number.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture.NumberFormat);
        }

        /// <summary>
        /// Converts a string like "123.456" or "0xABC" into a double.
        /// Tries decimal conversion first.
        /// </summary>
        internal static double ConvertDecimalOrHexToDouble(string number)
        {
            if (TryConvertDecimalOrHexToDouble(number, out double result))
            {
                return result;
            }
            ErrorUtilities.VerifyThrow(false, "Cannot numeric evaluate");
            return 0.0D;
        }

        internal static bool TryConvertDecimalOrHexToDouble(string number, out double doubleValue)
        {
            if (ConversionUtilities.ValidDecimalNumber(number, out doubleValue))
            {
                return true;
            }
            else if (ConversionUtilities.ValidHexNumber(number, out int hexValue))
            {
                doubleValue = (double)hexValue;
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the string is a valid hex number, like "0xABC"
        /// </summary>
        private static bool ValidHexNumber(string number, out int value)
        {
            bool canConvert = false;
            value = 0;
            if (number.Length >= 3 && number[0] == '0' && (number[1] == 'x' || number[1] == 'X'))
            {
                canConvert = Int32.TryParse(number.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture.NumberFormat, out value);
            }
            return canConvert;
        }

        /// <summary>
        /// Returns true if the string is a valid decimal number, like "-123.456"
        /// </summary>
        private static bool ValidDecimalNumber(string number, out double value)
        {
            return Double.TryParse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture.NumberFormat, out value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Returns true if the string is a valid decimal or hex number
        /// </summary>
        internal static bool ValidDecimalOrHexNumber(string number)
        {
            return ValidDecimalNumber(number, out _) || ValidHexNumber(number, out _);
        }
    }
}
