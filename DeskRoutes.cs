using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed class DeskRoutes : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost) =>
        services.Configure<MvcOptions>(options => options.Conventions.Add(new PreferNewestController()));

    private sealed class PreferNewestController : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            var controller = typeof(DiagnosticsController);
            var assemblyName = controller.Assembly.GetName().Name;
            // Jellyfin 12.4 can load old and new copies under different plugin names.
            // Keep one VMS controller so ASP.NET does not register the same routes twice.
            var redundant = application.Controllers
                .Where(candidate => candidate.ControllerType.FullName == controller.FullName
                    && candidate.ControllerType.Assembly.GetName().Name == assemblyName)
                .OrderByDescending(candidate => candidate.ControllerType.Assembly.GetName().Version)
                .ThenBy(candidate => candidate.ControllerType.Assembly.Location, StringComparer.Ordinal)
                .Skip(1).ToArray();
            foreach (var candidate in redundant)
                application.Controllers.Remove(candidate);
        }
    }
}
