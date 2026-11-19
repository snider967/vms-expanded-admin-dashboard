using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed class DeskConfiguration : BasePluginConfiguration
{
}

public sealed class Plugin : BasePlugin<DeskConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer serializer)
        : base(applicationPaths, serializer)
    {
    }

    internal static string DisplayName { get; } = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "PluginDisplayName").Value!;

    internal static readonly Guid PluginId = new("626706cb-1cd6-4f67-94ca-c5b54f297d1d");

    private static string IdentityName { get; } = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "PluginIdentityName").Value!;

    public override string Name => IdentityName;

    public override Guid Id => PluginId;

    public override string Description => "Read-only server, playback, storage, and log diagnostics for Jellyfin.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = DisplayName,
            DisplayName = DisplayName,
            MenuIcon = "dashboard",
            EmbeddedResourcePath = "Jellyfin.Plugin.RemoteAdminDesk.Pages.desk.html",
            EnableInMainMenu = true
        };
        yield return new PluginPageInfo
        {
            Name = IdentityName,
            EmbeddedResourcePath = "Jellyfin.Plugin.RemoteAdminDesk.Pages.desk.html"
        };
    }
}
