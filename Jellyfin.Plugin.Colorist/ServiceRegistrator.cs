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

            // Singleton because it holds the live run in memory — that snapshot is
            // what makes the settings page's progress realtime, and a scoped
            // instance would have nothing in it.
            serviceCollection.AddSingleton<Services.Runs.RunLogStore>();

            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, GenerateBarcodesTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, DeleteBarcodesTask>();

            // Registered as IStartupFilter, which is how a plugin gets middleware into
            // the server's pipeline. See ScriptInjector for why this is preferred over
            // the File Transformation plugin.
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjector>();
        }
    }
}
