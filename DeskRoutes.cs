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
        foreach (var older in olderCopies)
        {
            var standardInstallation = (older.Name == Plugin.IdentityName || older.Name == Plugin.DisplayName)
                && IsVersionedSibling(older, current);
            if (older.Manifest.Status == PluginStatus.Disabled)
            {
                // Jellyfin removes the retired folder and its in-memory UI record.
                if (older.Instance is null && standardInstallation)
                    manager.RemovePlugin(older);
                continue;
            }
            if (older.Manifest.Status is not (PluginStatus.Active or PluginStatus.Superseded))
                continue;

            // Jellyfin's updater considers superseded copies. A Disabled state keeps
            // them out of future updates, and standard folders can retire next boot.
            older.Manifest.Status = PluginStatus.Disabled;
            if (standardInstallation)
                older.Manifest.Name = Plugin.IdentityName;
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

    private static bool IsVersionedSibling(LocalPlugin older, LocalPlugin current)
    {
        var directory = new DirectoryInfo(older.Path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var currentDirectory = new DirectoryInfo(current.Path);
        return directory.Parent is not null && directory.Parent.FullName.Equals(currentDirectory.Parent?.FullName, comparison)
            && !directory.FullName.Equals(currentDirectory.FullName, comparison)
            && (directory.Name.Equals(Plugin.IdentityName + "_" + older.Version, comparison)
                || directory.Name.Equals(Plugin.DisplayName + "_" + older.Version, comparison))
            && directory.Exists && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
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
