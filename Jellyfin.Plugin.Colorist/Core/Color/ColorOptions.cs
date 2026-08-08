namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// The knobs shared by every clustering strategy.
    /// </summary>
    /// <param name="DominanceExponent">
    /// How much a cluster's size counts against how colourful it is, as the
    /// exponent in <c>population^e × chroma</c>.
    /// <para>
    /// This single number is the whole "average versus dominant" argument made
    /// continuous. At <c>0.0</c> population drops out entirely and the most
    /// colourful cluster wins however few pixels it covers, so one speck of lens
    /// flare decides the frame. Raising it tilts progressively toward area, and
    /// somewhere above <c>1.0</c> — where exactly depends on how large the chroma
    /// gap is — a big desaturated background starts beating a small vivid subject,
    /// which is the muddy result reached by a different route than averaging.
    /// </para>
    /// <para>
    /// Note that <c>1.0</c> is <i>not</i> "largest area always wins": chroma is
    /// still a factor there, and a vivid tenth of the frame will still beat a dull
    /// nine tenths. Genuine area-dominance needs roughly <c>1.5</c> and above. The
    /// default of <c>0.6</c> sits well inside the range where a small vivid subject
    /// beats a large grey wall but a single pixel does not beat the subject.
    /// </para>
    /// </param>
    /// <param name="ClusterCount">
    /// How many clusters to reduce a frame to before picking one. Low values blend
    /// distinct colours together; high values fragment one real colour across
    /// several clusters and deflate each one's population.
    /// </param>
    /// <param name="BlackFloor">
    /// Pixels with every channel at or below this are left out of clustering.
    /// <para>
    /// This is the second line of defence against letterboxing, behind cropping:
    /// bars survive cropdetect as a few residual rows, and compression noise keeps
    /// them from being exactly zero. It also does something useful on real
    /// footage — a night interior lit by one warm lamp reduces to the lamp rather
    /// than to the black around it, which is the more informative stripe. A frame
    /// with nothing above the floor falls back to its plain mean, so genuinely
    /// black frames still produce black.
    /// </para>
    /// </param>
    public readonly record struct ColorOptions(
        float DominanceExponent,
        int ClusterCount,
        byte BlackFloor)
    {
        /// <summary>Gets the defaults, as reasoned about above.</summary>
        public static ColorOptions Default => new ColorOptions(0.6f, 8, 12);
    }
}
