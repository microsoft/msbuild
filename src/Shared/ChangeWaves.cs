// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Shared;
using System;
using System.Linq;

namespace Microsoft.Build.Utilities
{
    internal enum ChangeWaveConversionState
    {
        Valid,
        InvalidFormat,
        OutOfRotation,
        InvalidVersion
    }

    /// <summary>
    /// All waves are enabled by default, meaning all features behind change wave versions are enabled.
    /// </summary>
    public class ChangeWaves
    {
        public static readonly string[] AllWaves = { Wave16_8, Wave16_10, Wave17_0 };
        public static readonly Version[] AllWavesAsVersion = Array.ConvertAll<string, Version>(AllWaves, Version.Parse);
        public const string Wave16_8 = "16.8";
        public const string Wave16_10 = "16.10";
        public const string Wave17_0 = "17.0";

        /// <summary>
        /// Special value indicating that all features behind change-waves should be enabled.
        /// </summary>
        public const string EnableAllFeatures = "999.999";

        internal static readonly Version LowestWaveAsVersion = new Version(AllWaves[0]);
        internal static readonly Version HighestWaveAsVersion = new Version(AllWaves[AllWaves.Length - 1]);
        internal static readonly Version EnableAllFeaturesAsVersion = new Version(EnableAllFeatures);

        internal static string LowestWave
        {
            get
            {
                return AllWaves[0];
            }
        }

        internal static string HighestWave
        {
            get
            {
                return AllWaves[AllWaves.Length - 1];
            }
        }

        private static string cachedWave = null;

        public static string DisabledWave
        {
            get
            {
                if (cachedWave == null)
                {
                    cachedWave = Traits.Instance.MSBuildDisableFeaturesFromVersion;
                }

                return cachedWave;
            }
            set
            {
                cachedWave = value;
            }
        }

        private static ChangeWaveConversionState _state;
        internal static ChangeWaveConversionState ConversionState
        {
            get
            {
                return _state;
            }
        }

        /// <summary>
        /// Ensure the the environment variable MSBuildDisableFeaturesFromWave is set to a proper value.
        /// </summary>
        /// <returns> String representation of the set change wave. "999.999" if unset or invalid, and clamped if out of bounds. </returns>
        internal static string ApplyChangeWave()
        {
            Version changeWave;

            // If unset, enable all features.
            if (string.IsNullOrEmpty(DisabledWave))
            {
                _state = ChangeWaveConversionState.Valid;
                return DisabledWave = ChangeWaves.EnableAllFeatures;
            }

            // If the version is of invalid format, log a warning and enable all features.
            if (!Version.TryParse(DisabledWave, out changeWave))
            {
                _state = ChangeWaveConversionState.InvalidFormat;
                return DisabledWave = ChangeWaves.EnableAllFeatures;
            }
            // If the version is 999.999, we're done.
            else if (changeWave == EnableAllFeaturesAsVersion)
            {
                _state = ChangeWaveConversionState.Valid;
                return DisabledWave = changeWave.ToString();
            }
            // If the version is out of rotation, log a warning and clamp the value.
            else if (changeWave < LowestWaveAsVersion)
            {
                _state = ChangeWaveConversionState.OutOfRotation;
                return DisabledWave = LowestWave;
            }
            else if (changeWave > HighestWaveAsVersion)
            {
                _state = ChangeWaveConversionState.OutOfRotation;
                return DisabledWave = HighestWave;
            }

            // Ensure it's set to an existing version within the current rotation
            if (!AllWavesAsVersion.Contains(changeWave))
            {
                foreach (Version wave in AllWavesAsVersion)
                {
                    if (wave > changeWave)
                    {
                        _state = ChangeWaveConversionState.InvalidVersion;
                        return DisabledWave = wave.ToString();
                    }
                }
            }

            _state = ChangeWaveConversionState.Valid;
            return DisabledWave = changeWave.ToString();
        }

        /// <summary>
        /// Compares the passed wave to the MSBuildDisableFeaturesFromVersion environment variable.
        /// Version MUST be of the format: "xx.yy".
        /// </summary>
        /// <param name="wave">The version to compare.</param>
        /// <returns>A bool indicating whether the feature behind a version is enabled.</returns>
        public static bool AreFeaturesEnabled(string wave)
        {
            Version waveToCheck;

            // When a caller passes an invalid wave, fail the build.
            ErrorUtilities.VerifyThrow(Version.TryParse(wave.ToString(), out waveToCheck),
                                       $"Argument 'wave' passed with invalid format." +
                                       $"Please use pre-existing const strings or define one with format 'xx.yy");

            return AreFeaturesEnabled(waveToCheck);
        }

        /// <summary>
        /// Compares the passed wave to the MSBuildDisableFeaturesFromVersion environment variable.
        /// </summary>
        /// <param name="wave">The version to compare.</param>
        /// <returns>A bool indicating whether the version is enabled.</returns>
        public static bool AreFeaturesEnabled(Version wave)
        {
            // This is opt out behavior, all waves are enabled by default.
            if (string.IsNullOrEmpty(DisabledWave))
            {
                return true;
            }

            Version currentSetWave;

            // If we can't parse the environment variable, default to enabling features.
            if (!Version.TryParse(DisabledWave, out currentSetWave))
            {
                return true;
            }

            return wave < currentSetWave;
        }
    }
}
