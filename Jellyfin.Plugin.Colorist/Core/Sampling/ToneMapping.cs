namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>
    /// How a source's colour has to be converted before its colours mean anything.
    /// </summary>
    /// <remarks>
    /// Ordered by how much intervention they need, which is also the order the sampler
    /// falls back through when a filter turns out to be missing from the ffmpeg build
    /// in use.
    /// </remarks>
    public enum ToneMapping
    {
        /// <summary>SDR. Decode and sample as-is.</summary>
        None = 0,

        /// <summary>
        /// PQ or HLG in wide primaries — HDR10, HDR10+, HLG, and the Dolby Vision
        /// profiles that carry an HDR10 base layer.
        /// </summary>
        Hdr = 1,

        /// <summary>
        /// Dolby Vision with no usable base layer (profile 5), which needs the RPU
        /// applied before there is a correct picture to tone map at all.
        /// </summary>
        DolbyVision = 2,
    }

    /// <summary>Chooses the conversion for a source.</summary>
    public static class ToneMappingPolicy
    {
        /// <summary>
        /// Decides from what ffprobe reported.
        /// </summary>
        /// <param name="colorTransfer">The stream's transfer characteristic.</param>
        /// <param name="dolbyVisionProfile">The Dolby Vision profile, or null when absent.</param>
        /// <param name="hasHdr10Base">Whether a conventional HDR10 base layer is present.</param>
        /// <returns>The conversion to apply.</returns>
        /// <remarks>
        /// The interesting case is a Dolby Vision profile with no HDR10 base. Profiles
        /// 7 and 8.x carry one, so they are ordinary HDR as far as the pixels are
        /// concerned and the RPU is a bonus nobody here needs. Profile 5 does not, and
        /// is the only one that genuinely requires libplacebo.
        /// <para>
        /// Profile 5 is also identified by profile number rather than by transfer,
        /// because its transfer is frequently reported as unknown — treating "no
        /// transfer stated" as SDR is exactly how it ends up sampled as pink.
        /// </para>
        /// </remarks>
        public static ToneMapping Decide(
            string? colorTransfer,
            int? dolbyVisionProfile,
            bool hasHdr10Base)
        {
            if (dolbyVisionProfile == 5 && !hasHdr10Base)
            {
                return ToneMapping.DolbyVision;
            }

            if (IsHdrTransfer(colorTransfer))
            {
                return ToneMapping.Hdr;
            }

            // A Dolby Vision stream that got this far states no HDR transfer but does
            // carry a profile. Profiles 4, 7 and 8 all imply a PQ or HLG base, so the
            // metadata is simply missing rather than the content being SDR.
            return dolbyVisionProfile.HasValue ? ToneMapping.Hdr : ToneMapping.None;
        }

        /// <summary>
        /// Whether a transfer characteristic means the picture is HDR.
        /// </summary>
        /// <remarks>
        /// <c>smpte2084</c> is PQ, covering HDR10, HDR10+ and Dolby Vision's HDR10
        /// base; <c>arib-std-b67</c> is HLG. Everything else — including the bt709 and
        /// unspecified that most libraries are full of — is left alone, because tone
        /// mapping an SDR picture washes it out just as badly as not tone mapping an
        /// HDR one.
        /// </remarks>
        public static bool IsHdrTransfer(string? transfer) =>
            string.Equals(transfer, "smpte2084", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(transfer, "arib-std-b67", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>The next conversion to try when one fails.</summary>
        /// <param name="current">The conversion that produced nothing.</param>
        /// <returns>Something simpler, or null when there is nothing left to try.</returns>
        public static ToneMapping? Fallback(ToneMapping current) => current switch
        {
            // libplacebo missing: the HDR10 chain at least linearises whatever the
            // decoder produced. On a true profile 5 that is still wrong, but on a
            // misdetected profile 8 it is right, and it costs one retry to find out.
            ToneMapping.DolbyVision => ToneMapping.Hdr,
            ToneMapping.Hdr => ToneMapping.None,
            _ => null,
        };
    }
}
