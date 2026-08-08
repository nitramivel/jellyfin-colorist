using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Colorist.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Colorist
{
    /// <summary>
    /// The Colorist plugin: movie barcodes sampled from the footage and shown at the
    /// foot of the detail page.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>Initialises a new instance of the <see cref="Plugin"/> class.</summary>
        /// <param name="applicationPaths">Server paths.</param>
        /// <param name="xmlSerializer">Configuration serializer.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("1dd662e3-27c3-4e43-bbfe-108509a0b84f");

        /// <inheritdoc />
        public override string Name => "Colorist";

        /// <inheritdoc />
        public override string Description =>
            "Samples the dominant colour of frames across a video and renders them as a vertical-stripe movie barcode, stored beside the media and shown at the foot of the detail page.";

        /// <summary>Gets the current plugin instance.</summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return
            [
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                },
            ];
        }
    }
}
