using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>Maps the configured strategy key onto an implementation.</summary>
    public static class StrategyFactory
    {
        private static readonly IReadOnlyDictionary<string, Func<IFrameColorStrategy>> Registry =
            new Dictionary<string, Func<IFrameColorStrategy>>(StringComparer.OrdinalIgnoreCase)
            {
                [MeanStrategy.StrategyKey] = static () => new MeanStrategy(),
                [MedianCutStrategy.StrategyKey] = static () => new MedianCutStrategy(),
                [KMeansStrategy.StrategyKey] = static () => new KMeansStrategy(),
            };

        /// <summary>Gets the key used when configuration holds nothing usable.</summary>
        public static string DefaultKey => MedianCutStrategy.StrategyKey;

        /// <summary>Gets every selectable key, for the configuration page.</summary>
        public static IEnumerable<string> Keys => Registry.Keys;

        /// <summary>Builds the strategy for a key.</summary>
        /// <param name="key">The configured key; unknown or empty falls back to the default.</param>
        /// <returns>A strategy, never null.</returns>
        /// <remarks>
        /// Unknown keys fall back rather than throw. This is read from a settings
        /// file that a downgrade or a hand edit can leave holding a key this build
        /// does not have, and the failure mode for throwing is a scheduled task that
        /// dies on every item — a much worse outcome than stripes computed by the
        /// default algorithm.
        /// </remarks>
        public static IFrameColorStrategy Create(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && Registry.TryGetValue(key, out var factory))
            {
                return factory();
            }

            return Registry[DefaultKey]();
        }
    }
}
