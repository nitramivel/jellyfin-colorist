using Jellyfin.Plugin.Colorist.Services;
using Jellyfin.Plugin.Colorist.Services.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Colorist
{
    /// <summary>Registers Colorist's services with Jellyfin's DI container.</summary>
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<FfmpegRunner>();
            serviceCollection.AddSingleton<FrameSampler>();
            serviceCollection.AddSingleton<BarcodeStore>();
            serviceCollection.AddSingleton<BarcodeService>();

            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, GenerateBarcodesTask>();

            // Registered as IStartupFilter, which is how a plugin gets middleware into
            // the server's pipeline. See ScriptInjector for why this is preferred over
            // the File Transformation plugin.
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjector>();
        }
    }
}
