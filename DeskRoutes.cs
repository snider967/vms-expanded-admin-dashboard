using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed class DeskRoutes : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.Configure<MvcOptions>(options => options.Conventions.Add(new PreferNewestController()));
        if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IPluginManager))
            ?.ImplementationInstance is IPluginManager manager && RetireOlderCopies(manager))
        {
            DeskTaskRegistration.FilterLegacyTasks(services);
        }
    }

    private static bool RetireOlderCopies(IPluginManager manager)
    {
        var assembly = typeof(Plugin).Assembly.Location;
        var copies = manager.Plugins.Where(plugin => plugin.Id == Plugin.PluginId).ToArray();
        var current = copies.FirstOrDefault(plugin => plugin.IsEnabledAndSupported
            && plugin.DllFiles.Contains(assembly, StringComparer.Ordinal));
        if (current is null || copies.Any(plugin => plugin.IsEnabledAndSupported && plugin.Version > current.Version))
        {
            return false;
        }

        var olderCopies = copies.Where(plugin => plugin.Version < current.Version).ToArray();
        // Jellyfin's updater considers superseded copies. An explicit Disabled state
        // is preserved on catalog refresh and keeps older copies out of future updates.
        foreach (var older in olderCopies.Where(plugin => plugin.Manifest.Status
            is PluginStatus.Active or PluginStatus.Superseded))
        {
            older.Manifest.Status = PluginStatus.Disabled;
            try
            {
                manager.SaveManifest(older.Manifest, older.Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep it disabled for this process. A later startup can retry the save.
            }
        }
        return olderCopies.Any(plugin => plugin.DllFiles.Count != 0);
    }

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
