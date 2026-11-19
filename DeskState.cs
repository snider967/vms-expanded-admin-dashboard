using System.Text.Json;
using MediaBrowser.Common.Configuration;
using static Jellyfin.Plugin.RemoteAdminDesk.DeskProbes;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class DeskState
{
    private const int MaxBytes = 1024 * 1024;
    private const string DiagnosticFile = "diagnostic-samples.json";
    private const string MediaFile = "media-baseline.json";
    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web);
    internal sealed record Sample(DateTimeOffset SampledAt, Volume[] Volumes);
    internal sealed record Delivery(DateTimeOffset AttemptedAt, DateTimeOffset? DeliveredAt, string Status);
    internal sealed record Diagnostics(Sample[] Samples, string[] Delivered, bool? StorageWarning, DateTimeOffset? LastScanAt,
        Delivery? Discord = null, bool? MountWarning = null);
    internal const int MediaQueueLimit = 240;
    internal const int MediaBoundaryLimit = 12000;
    internal sealed record MediaAlert(Guid Id, string Title);
    internal sealed record Media(Guid[] Known, DateTimeOffset? OldestDate, string[] Pending,
        DateTimeOffset? LastScanAt = null, Delivery? Discord = null, DateTimeOffset? Cursor = null,
        Guid[]? CursorIds = null, DateTimeOffset? ScanThrough = null, MediaAlert[]? Outbox = null,
        Guid[]? DeliveryReceipt = null)
    {
        public int QueuedCount => Pending.Length + (Outbox?.Length ?? 0);
    }
    internal sealed record NotificationChannel(string Configuration, DateTimeOffset? LastScanAt, Delivery? Delivery);
    internal sealed record NotificationStatus(NotificationChannel Diagnostics, NotificationChannel Media, int PendingMedia, bool MediaBaseline);

    internal static NotificationStatus ReadNotifications(IApplicationPaths paths, CancellationToken ct)
    {
        var diagnostics = ReadDiagnostics(paths, ct);
        var media = ReadMedia(paths, ct);
        var settings = DeskSettings.Read(paths.DataPath, ct);
        return new NotificationStatus(
            new NotificationChannel(DeskDiscord.Configuration(DeskSettings.Effective(DeskDiscord.DiagnosticsVariable,
                settings.DiagnosticsWebhook)), diagnostics.LastScanAt, diagnostics.Discord),
            new NotificationChannel(DeskDiscord.Configuration(DeskSettings.Effective(DeskDiscord.MediaVariable,
                settings.MediaWebhook)), media?.LastScanAt, media?.Discord),
            media?.QueuedCount ?? 0, media is not null);
    }

    internal static Delivery RecordDelivery(Delivery? previous, string status)
    {
        var now = DateTimeOffset.UtcNow;
        return new Delivery(now, status == DeskDiscord.Delivered ? now : previous?.DeliveredAt, status);
    }

    internal static Diagnostics ReadDiagnostics(IApplicationPaths paths, CancellationToken ct)
    {
        var state = Read<Diagnostics>(paths, DiagnosticFile, ct);
        if (state is null) return new Diagnostics([], [], null, null);
        if (state.Samples is null || state.Delivered is null || state.Samples.Length > 120 || state.Delivered.Length > 64)
            throw new JsonException("Remote Admin Desk diagnostic state is invalid.");
        return state;
    }

    internal static Media? ReadMedia(IApplicationPaths paths, CancellationToken ct)
    {
        var state = Read<Media>(paths, MediaFile, ct);
        if (state is not null && (state.Known is null || state.Known.Length > 512
            || state.Pending is null || state.QueuedCount > MediaQueueLimit || state.CursorIds?.Length > MediaBoundaryLimit
            || state.DeliveryReceipt?.Length > 14))
            throw new JsonException("Remote Admin Desk media state is invalid.");
        return state;
    }

    internal static bool SaveDiagnostics(IApplicationPaths paths, Diagnostics data, CancellationToken ct) =>
        Save(paths, DiagnosticFile, data, ct);
    internal static bool SaveMedia(IApplicationPaths paths, Media data, CancellationToken ct) =>
        Save(paths, MediaFile, data, ct);

    private static string DeliveryShard(Guid id) => "media-delivered-" + id.ToString("N")[..2] + ".json";

    internal static HashSet<Guid> ReadDeliveredMedia(IApplicationPaths paths, IEnumerable<Guid> ids, CancellationToken ct) =>
        ids.GroupBy(DeliveryShard).SelectMany(group =>
            (Read<Guid[]>(paths, group.Key, ct) ?? []).Intersect(group)).ToHashSet();

    internal static void RecordDeliveredMedia(IApplicationPaths paths, IEnumerable<Guid> ids, CancellationToken ct)
    {
        foreach (var group in ids.GroupBy(DeliveryShard))
            Save(paths, group.Key, (Read<Guid[]>(paths, group.Key, ct) ?? []).Union(group).ToArray(), ct);
    }

    private static string StateDirectory(IApplicationPaths paths) => Path.Join(paths.DataPath, "remote-admin-desk");

    internal static T? Read<T>(IApplicationPaths paths, string filename, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = File.OpenRead(Path.Join(StateDirectory(paths), filename));
            if (stream.Length > MaxBytes) throw new IOException("Remote Admin Desk state exceeds its size limit.");
            var data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            ct.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<T>(data, WireFormat)
                ?? throw new JsonException("Remote Admin Desk state is invalid.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static bool Save<T>(IApplicationPaths paths, string filename, T data, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, WireFormat);
        if (bytes.Length > MaxBytes) throw new IOException("Remote Admin Desk state exceeds its size limit.");
        ct.ThrowIfCancellationRequested();
        var directory = StateDirectory(paths);
        Directory.CreateDirectory(directory);
        var target = Path.Join(directory, filename);
        var temporary = target + ".new";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options)) stream.Write(bytes);
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, target, overwrite: true);
        return true;
    }
}
