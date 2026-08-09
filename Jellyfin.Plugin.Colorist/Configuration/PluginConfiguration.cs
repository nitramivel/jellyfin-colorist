using System;
using Jellyfin.Plugin.Colorist.Core.Color;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Colorist.Configuration
{
    /// <summary>How black bars are dealt with before sampling.</summary>
    public enum CropMode
    {
        /// <summary>Sample the frame as it is.</summary>
        None = 0,

        /// <summary>Probe each item with cropdetect and use the modal result.</summary>
        Auto = 1,

        /// <summary>Apply the same fixed crop to every item.</summary>
        Fixed = 2,
    }

    /// <summary>Colorist's settings.</summary>
    /// <remarks>
    /// Option order in the configuration page's <c>select</c> elements is
    /// load-bearing for the two enums here, since a stored numeric value falls back
    /// to index matching. Change the labels freely; never reorder them.
    /// </remarks>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Gets or sets the colour strategy key.</summary>
        public string ColorStrategy { get; set; } = StrategyFactory.DefaultKey;

        /// <summary>
        /// Gets or sets how much cluster size counts against how colourful it is.
        /// </summary>
        public double DominanceExponent { get; set; } = 0.6;

        /// <summary>Gets or sets how many clusters a frame is reduced to.</summary>
        public int ClusterCount { get; set; } = 8;

        /// <summary>Gets or sets the level below which a pixel is treated as black.</summary>
        public int BlackFloor { get; set; } = 12;

        /// <summary>Gets or sets how many stripes a barcode has.</summary>
        public int Columns { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether to sample only keyframes.
        /// </summary>
        /// <remarks>
        /// On by default. Decoding every frame of a three-hour film to look at one in
        /// two hundred is work with nothing to show for it, and keyframes tend to sit
        /// on cuts, which is where the colour actually changes.
        /// </remarks>
        public bool KeyframesOnly { get; set; } = true;

        /// <summary>Gets or sets the output width in pixels.</summary>
        public int OutputWidth { get; set; } = 1920;

        /// <summary>Gets or sets the output height in pixels.</summary>
        public int OutputHeight { get; set; } = 240;

        /// <summary>Gets or sets whether stripes blend into each other.</summary>
        public bool Smooth { get; set; }

        /// <summary>Gets or sets how black bars are handled.</summary>
        public CropMode CropMode { get; set; } = CropMode.Auto;

        /// <summary>Gets or sets the crop applied when <see cref="CropMode"/> is Fixed.</summary>
        public int FixedCropWidth { get; set; }

        /// <summary>Gets or sets the crop applied when <see cref="CropMode"/> is Fixed.</summary>
        public int FixedCropHeight { get; set; }

        /// <summary>Gets or sets the crop applied when <see cref="CropMode"/> is Fixed.</summary>
        public int FixedCropX { get; set; }

        /// <summary>Gets or sets the crop applied when <see cref="CropMode"/> is Fixed.</summary>
        public int FixedCropY { get; set; }

        /// <summary>Gets or sets the percentage of runtime skipped at the start.</summary>
        public double HeadTrimPercent { get; set; } = 0.5;

        /// <summary>
        /// Gets or sets the percentage of runtime skipped at the end.
        /// </summary>
        /// <remarks>
        /// Four percent is around seven minutes of a feature and fifty seconds of a
        /// half-hour episode, which covers most credit rolls without eating a real
        /// ending. It is a percentage rather than a fixed number of minutes because
        /// credits scale with production size, and a fixed seven minutes would remove
        /// a quarter of an episode.
        /// </remarks>
        public double TailTrimPercent { get; set; } = 4.0;

        /// <summary>Gets or sets how many items are processed at once.</summary>
        /// <remarks>
        /// Zero means "decide from the processor count" — see
        /// <c>BarcodeService</c>, which resolves it to a quarter of the cores.
        /// </remarks>
        public int MaxConcurrency { get; set; }

        /// <summary>Gets or sets the decoder thread cap per ffmpeg process. Zero lets ffmpeg decide.</summary>
        public int FfmpegThreads { get; set; } = 1;

        /// <summary>
        /// Gets or sets whether HDR and Dolby Vision sources are converted before
        /// their colours are measured.
        /// </summary>
        /// <remarks>
        /// On by default, and it matters more than it sounds. A PQ signal in BT.2020
        /// primaries decoded straight to rgb24 is read as though it were sRGB: the
        /// mid-tones land far too dark and the wide primaries collapse toward grey, so
        /// every 4K HDR film produces a dim, muted strip that looks nothing like the
        /// film. Dolby Vision profile 5 is worse still — with no HDR10 base layer it
        /// decodes pink and green — and needs the RPU applied rather than merely tone
        /// mapping.
        /// <para>
        /// Turn off only if the ffmpeg in use lacks the filters and the fallback
        /// logging becomes noise.
        /// </para>
        /// </remarks>
        public bool ToneMapHdr { get; set; } = true;

        /// <summary>Gets or sets whether existing barcodes are regenerated.</summary>
        public bool ForceRegenerate { get; set; }

        /// <summary>Gets or sets whether episodes are included as well as movies.</summary>
        public bool IncludeEpisodes { get; set; } = true;

        /// <summary>Gets or sets whether movies are included.</summary>
        public bool IncludeMovies { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to write the barcode next to the media file.
        /// </summary>
        /// <remarks>
        /// When false, or when the library folder rejects the write, the file goes to
        /// the plugin's data directory instead. Either way the detail page finds it,
        /// because the page asks the API for it by item ID rather than guessing a path.
        /// </remarks>
        public bool WriteBesideMedia { get; set; } = true;

        /// <summary>Gets or sets whether the client script is added to the web interface.</summary>
        public bool ShowOnDetailPage { get; set; } = true;

        /// <summary>Gets or sets the height the strip is displayed at, in CSS pixels.</summary>
        public int DisplayHeight { get; set; } = 90;

        /// <summary>Reads the colour knobs out as the value type Core expects.</summary>
        /// <returns>The clamped colour options.</returns>
        /// <remarks>
        /// Clamped on the way out rather than trusted. This object is deserialised
        /// from an XML file on disk that a hand edit, a failed upgrade or an older
        /// build can leave holding anything, and every value here would misbehave
        /// quietly rather than loudly: a cluster count of zero divides by nothing, a
        /// negative exponent inverts the whole selection rule.
        /// </remarks>
        public ColorOptions ToColorOptions() => new ColorOptions(
            (float)Math.Clamp(DominanceExponent, 0d, 2d),
            Math.Clamp(ClusterCount, 2, 64),
            (byte)Math.Clamp(BlackFloor, 0, 128));
    }
}
