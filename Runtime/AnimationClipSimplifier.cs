using UnityEngine;
using System.Collections.Generic;

namespace MocapTools
{
    /// <summary>
    /// Pure utility class for simplifying (key-reducing) AnimationCurves.
    /// Uses a deterministic algorithm that always preserves first and last keys,
    /// removing intermediate keys that are within an error tolerance of linear interpolation.
    ///
    /// This class contains no UnityEditor dependencies and can be used at runtime.
    /// </summary>
    public static class AnimationClipSimplifier
    {
        /// <summary>
        /// Simplifies an AnimationCurve by removing redundant keys within the specified tolerance.
        /// Always preserves the first and last keys.
        /// </summary>
        /// <param name="sourceCurve">The original curve to simplify.</param>
        /// <param name="valueTolerance">Maximum allowed absolute error from linear interpolation.</param>
        /// <param name="maxPasses">Maximum number of reduction passes (default 3).</param>
        /// <returns>A new simplified AnimationCurve.</returns>
        public static AnimationCurve SimplifyCurve(AnimationCurve sourceCurve, float valueTolerance, int maxPasses = 3)
        {
            if (sourceCurve == null || sourceCurve.length < 3)
            {
                // Nothing to simplify - return a copy
                return CopyCurve(sourceCurve);
            }

            if (valueTolerance < 0f)
            {
                valueTolerance = 0f;
            }

            maxPasses = Mathf.Max(1, maxPasses);

            // Start with a copy of all keys
            List<Keyframe> keys = new List<Keyframe>(sourceCurve.keys);

            // Run multiple passes until no more keys can be removed or max passes reached
            for (int pass = 0; pass < maxPasses; pass++)
            {
                int keysBeforePass = keys.Count;

                // Identify keys to remove (mark indices)
                List<int> indicesToRemove = new List<int>();

                // Never remove first (0) or last (keys.Count-1) key
                for (int i = 1; i < keys.Count - 1; i++)
                {
                    if (CanRemoveKey(keys, i, valueTolerance))
                    {
                        indicesToRemove.Add(i);
                    }
                }

                // Remove marked keys in reverse order to maintain valid indices
                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                {
                    keys.RemoveAt(indicesToRemove[i]);
                }

                // If no keys were removed this pass, we're done
                if (keys.Count == keysBeforePass)
                {
                    break;
                }
            }

            // Build the result curve
            AnimationCurve result = new AnimationCurve(keys.ToArray());

            // Preserve wrap modes
            result.preWrapMode = sourceCurve.preWrapMode;
            result.postWrapMode = sourceCurve.postWrapMode;

            return result;
        }

        /// <summary>
        /// Simplifies a curve using separate tolerances for value and tangent preservation.
        /// </summary>
        /// <param name="sourceCurve">The original curve to simplify.</param>
        /// <param name="valueTolerance">Maximum allowed absolute error from linear interpolation.</param>
        /// <param name="tangentTolerance">Maximum allowed tangent deviation (radians). Set to 0 to disable tangent checking.</param>
        /// <param name="maxPasses">Maximum number of reduction passes.</param>
        /// <returns>A new simplified AnimationCurve.</returns>
        public static AnimationCurve SimplifyCurveWithTangents(
            AnimationCurve sourceCurve,
            float valueTolerance,
            float tangentTolerance,
            int maxPasses = 3)
        {
            if (sourceCurve == null || sourceCurve.length < 3)
            {
                return CopyCurve(sourceCurve);
            }

            if (valueTolerance < 0f) valueTolerance = 0f;
            if (tangentTolerance < 0f) tangentTolerance = 0f;

            maxPasses = Mathf.Max(1, maxPasses);

            List<Keyframe> keys = new List<Keyframe>(sourceCurve.keys);

            for (int pass = 0; pass < maxPasses; pass++)
            {
                int keysBeforePass = keys.Count;
                List<int> indicesToRemove = new List<int>();

                for (int i = 1; i < keys.Count - 1; i++)
                {
                    if (CanRemoveKeyWithTangents(keys, i, valueTolerance, tangentTolerance))
                    {
                        indicesToRemove.Add(i);
                    }
                }

                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                {
                    keys.RemoveAt(indicesToRemove[i]);
                }

                if (keys.Count == keysBeforePass)
                {
                    break;
                }
            }

            AnimationCurve result = new AnimationCurve(keys.ToArray());
            result.preWrapMode = sourceCurve.preWrapMode;
            result.postWrapMode = sourceCurve.postWrapMode;

            return result;
        }

        /// <summary>
        /// Determines if a key at the given index can be removed without exceeding the tolerance.
        /// A key can be removed if the value at that time, when linearly interpolated from
        /// the previous to the next key, is within the tolerance of the actual key value.
        /// </summary>
        private static bool CanRemoveKey(List<Keyframe> keys, int index, float tolerance)
        {
            if (index <= 0 || index >= keys.Count - 1)
            {
                return false; // Never remove first or last key
            }

            Keyframe prev = keys[index - 1];
            Keyframe current = keys[index];
            Keyframe next = keys[index + 1];

            // Calculate linear interpolation from prev to next at current.time
            float t = (current.time - prev.time) / (next.time - prev.time);
            float interpolatedValue = Mathf.Lerp(prev.value, next.value, t);

            // Check if the actual value is within tolerance of the interpolated value
            float error = Mathf.Abs(current.value - interpolatedValue);

            return error <= tolerance;
        }

        /// <summary>
        /// Determines if a key can be removed considering both value and tangent tolerance.
        /// </summary>
        private static bool CanRemoveKeyWithTangents(
            List<Keyframe> keys,
            int index,
            float valueTolerance,
            float tangentTolerance)
        {
            if (index <= 0 || index >= keys.Count - 1)
            {
                return false;
            }

            Keyframe prev = keys[index - 1];
            Keyframe current = keys[index];
            Keyframe next = keys[index + 1];

            // Value check
            float t = (current.time - prev.time) / (next.time - prev.time);
            float interpolatedValue = Mathf.Lerp(prev.value, next.value, t);
            float valueError = Mathf.Abs(current.value - interpolatedValue);

            if (valueError > valueTolerance)
            {
                return false;
            }

            // Tangent check (if enabled)
            if (tangentTolerance > 0f)
            {
                // Calculate the expected tangent from linear interpolation
                float linearSlope = (next.value - prev.value) / (next.time - prev.time);

                // Check if the current key's tangents deviate significantly
                float inTangentDiff = Mathf.Abs(Mathf.Atan(current.inTangent) - Mathf.Atan(linearSlope));
                float outTangentDiff = Mathf.Abs(Mathf.Atan(current.outTangent) - Mathf.Atan(linearSlope));

                if (inTangentDiff > tangentTolerance || outTangentDiff > tangentTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Creates a deep copy of an AnimationCurve.
        /// </summary>
        /// <param name="source">The curve to copy.</param>
        /// <returns>A new AnimationCurve with copied keyframes.</returns>
        public static AnimationCurve CopyCurve(AnimationCurve source)
        {
            if (source == null)
            {
                return null;
            }

            AnimationCurve copy = new AnimationCurve(source.keys);
            copy.preWrapMode = source.preWrapMode;
            copy.postWrapMode = source.postWrapMode;

            return copy;
        }

        /// <summary>
        /// Calculates the total key count across all curves in a dictionary.
        /// </summary>
        public static int CountTotalKeys(Dictionary<string, AnimationCurve> curves)
        {
            int total = 0;
            foreach (var kvp in curves)
            {
                if (kvp.Value != null)
                {
                    total += kvp.Value.length;
                }
            }
            return total;
        }

        /// <summary>
        /// Calculates the total key count for an array of curves.
        /// </summary>
        public static int CountTotalKeys(AnimationCurve[] curves)
        {
            int total = 0;
            foreach (var curve in curves)
            {
                if (curve != null)
                {
                    total += curve.length;
                }
            }
            return total;
        }

        /// <summary>
        /// Simplifies all curves in a dictionary, returning a new dictionary with simplified curves.
        /// </summary>
        /// <param name="curves">Dictionary of property name to AnimationCurve.</param>
        /// <param name="valueTolerance">Maximum allowed absolute error.</param>
        /// <param name="maxPasses">Maximum number of reduction passes.</param>
        /// <returns>New dictionary with simplified curves.</returns>
        public static Dictionary<string, AnimationCurve> SimplifyAllCurves(
            Dictionary<string, AnimationCurve> curves,
            float valueTolerance,
            int maxPasses = 3)
        {
            var result = new Dictionary<string, AnimationCurve>(curves.Count);

            foreach (var kvp in curves)
            {
                result[kvp.Key] = SimplifyCurve(kvp.Value, valueTolerance, maxPasses);
            }

            return result;
        }

        /// <summary>
        /// Gets statistics about a curve.
        /// </summary>
        public struct CurveStats
        {
            public int KeyCount;
            public float MinValue;
            public float MaxValue;
            public float Duration;
            public float AverageKeyInterval;
        }

        /// <summary>
        /// Analyzes a curve and returns statistics.
        /// </summary>
        public static CurveStats GetCurveStats(AnimationCurve curve)
        {
            var stats = new CurveStats();

            if (curve == null || curve.length == 0)
            {
                return stats;
            }

            stats.KeyCount = curve.length;

            Keyframe[] keys = curve.keys;
            stats.MinValue = float.MaxValue;
            stats.MaxValue = float.MinValue;

            foreach (var key in keys)
            {
                if (key.value < stats.MinValue) stats.MinValue = key.value;
                if (key.value > stats.MaxValue) stats.MaxValue = key.value;
            }

            if (keys.Length > 1)
            {
                stats.Duration = keys[keys.Length - 1].time - keys[0].time;
                stats.AverageKeyInterval = stats.Duration / (keys.Length - 1);
            }

            return stats;
        }
    }
}
