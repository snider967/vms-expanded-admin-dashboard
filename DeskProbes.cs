using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal sealed class DeskProbeBusyException : Exception { }

internal sealed partial class DeskProbes(ILibraryManager library, IApplicationPaths paths)
{
    private const int LogBytes = 96 * 1024;
    private const int MaxLogEntries = 512;
    private const int MaxLocations = 32;
    internal static readonly SemaphoreSlim StorageGate = new(1, 1);
    internal static readonly SemaphoreSlim LogsGate = new(1, 1);
    internal static readonly SemaphoreSlim StateGate = new(1, 1);
    private static StorageReport? _storageCache;
    private static LogCatalog? _logCache;
    private static readonly ConcurrentDictionary<string, LogExcerpt> LogCache = new(StringComparer.Ordinal);

    internal StorageReport? CachedStorage => Volatile.Read(ref _storageCache) is { } cached
        && IsFresh(cached.SampledAt, 90) ? cached : null;
    internal LogCatalog? CachedCatalog => Volatile.Read(ref _logCache) is { } cached
        && IsFresh(cached.SampledAt, 30) ? cached : null;
    internal LogExcerpt? GetCachedLog(string name) => LogCache.TryGetValue(name, out var cached)
        && IsFresh(cached.SampledAt, 15) ? cached : null;
    internal static bool IsLogName(string name) => name.Length <= 160 && LogName().IsMatch(name);

    internal static Task<T> Run<T>(
        SemaphoreSlim gate, TimeSpan timeout, Func<CancellationToken, T> probe, CancellationToken cancellationToken,
        bool waitForSlot = false) =>
        RunAsync(gate, timeout, ct => Task.FromResult(probe(ct)), cancellationToken, waitForSlot);

    internal static async Task<T> RunAsync<T>(
        SemaphoreSlim gate, TimeSpan timeout, Func<CancellationToken, Task<T>> probe, CancellationToken cancellationToken,
        bool waitForSlot = false)
    {
        var acquired = waitForSlot
            ? await gate.WaitAsync(timeout, cancellationToken)
            : gate.Wait(0, cancellationToken);
        if (!acquired) throw new DeskProbeBusyException();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        var token = budget.Token;
        // Kernel I/O can outlive cancellation. Keep the worker slot until actual completion.
        var work = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return await probe(token);
            }
            finally
            {
                gate.Release();
            }
        });
        try
        {
            return await work.WaitAsync(token);
        }
        finally
        {
            if (!work.IsCompletedSuccessfully)
            {
                _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    internal StorageReport ReadStorage(CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsLinux())
            return new StorageReport(at, [], [], false, "Media storage probing is available on Linux.");
        var mounts = ReadMounts();
        if (mounts is null) return new StorageReport(at, [], [], false, "Mount table exceeds the diagnostic limit.");

        ct.ThrowIfCancellationRequested();
        var locations = library.GetVirtualFolders(false)
            .SelectMany(folder => folder.Locations.Select(location => (folder.Name, Path: location)))
            .Take(MaxLocations + 1).ToArray();
        var readings = new Dictionary<string, Volume>(StringComparer.Ordinal);
        var mappings = new List<LibraryLocation>(MaxLocations);
        foreach (var location in locations.Take(MaxLocations))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(location.Path)) continue;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
            var mount = FindMount(path, mounts);
            if (mount is null || (mount.Path == "/" && IsExternalRoot(path)))
            {
                mappings.Add(new LibraryLocation(location.Name, path, null, "No dedicated mount is visible for this location."));
                continue;
            }
            if (!readings.ContainsKey(mount.Path)) readings.Add(mount.Path, ReadVolume(mount));
            mappings.Add(new LibraryLocation(location.Name, path, mount.Path, null));
        }
        ct.ThrowIfCancellationRequested();
        var report = new StorageReport(at, readings.Values.ToArray(), mappings.ToArray(), locations.Length > MaxLocations, null);
        Volatile.Write(ref _storageCache, report);
        return report;
    }

    private static Dictionary<string, Mount>? ReadMounts()
    {
        using var file = File.OpenRead("/proc/self/mountinfo");
        var buffer = new byte[256 * 1024 + 1];
        var length = file.ReadAtLeast(buffer, buffer.Length, false);
        if (length == buffer.Length)
            return null;

        var mounts = new Dictionary<string, Mount>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, length).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 8) continue;
            var separator = Array.IndexOf(fields, "-", 6);
            if (separator < 0 || separator + 1 >= fields.Length) continue;
            var name = MountEscape().Replace(fields[4], match => ((char)Convert.ToInt32(match.Value[1..], 8)).ToString());
            mounts[name] = new Mount(name, fields[separator + 1], fields[2]);
        }

        return mounts;
    }

    private static string[] BranchPaths(string? configured) => (configured ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(path => path.Length < 1024 && Path.IsPathFullyQualified(path))
        .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
        .Where(path => path != "/").Distinct(StringComparer.Ordinal).Take(9).ToArray();

    internal BranchReport ReadBranches(string? configured, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return new BranchReport([], false, "Branch checks require Linux.");
        var candidates = BranchPaths(configured);
        if (candidates.Length == 0) return new BranchReport([], false, "Add the expected mount points in Settings.");
        var mounts = ReadMounts();
        if (mounts is null) return new BranchReport([], false, "Mount table exceeds the diagnostic limit.");
        var branches = new List<Volume>();
        foreach (var path in candidates.Take(8))
        {
            ct.ThrowIfCancellationRequested();
            branches.Add(mounts.TryGetValue(path, out var mount)
                ? ReadVolume(mount) : new Volume(path, "unknown", "", null, null, "Expected mount is missing."));
        }
        return new BranchReport(branches.ToArray(), candidates.Length > 8, null);
    }

    internal PlaybackTriageReport ReadPlaybackTriage(CancellationToken ct)
    {
        var catalog = CachedCatalog ?? ReadCatalog(ct);
        var recent = new List<PlaybackLog>();
        foreach (var entry in catalog.Files.Where(file =>
            file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var log = OpenLog(entry.Name);
                var tail = ReadTail(log, entry.Name, ct, 12 * 1024);
                // FFmpeg rewrites its progress line with carriage returns.
                var lines = tail.Content.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var matches = lines.Where(line => PlaybackSignal().IsMatch(line)).ToArray();
                var summary = matches.TakeLast(4).Select(line => line[..Math.Min(240, line.Length)]).ToArray();
                var progress = lines.Reverse().Select(ParsePlaybackProgress).FirstOrDefault(value => value is not null);
                log.Seek(0, SeekOrigin.Begin);
                var (head, length) = ReadLogBytes(log, (int)Math.Min(log.Length, 64 * 1024), ct);
                var complete = length == 0 ? 0 : Array.LastIndexOf(head, (byte)'\n', length - 1, length) + 1;
                var encoders = VideoEncoders(Encoding.UTF8.GetString(head, 0, complete));
                recent.Add(new PlaybackLog(entry.Name, entry.ModifiedAt, summary, tail.Truncated, encoders,
                    tail.Bytes, lines.Length, matches.Length, progress));
            }
            catch (FileNotFoundException)
            {
                // A log can rotate between catalog and probe.
            }
        }
        return new PlaybackTriageReport(DateTimeOffset.UtcNow, recent.ToArray(), catalog.Limited);
    }

    [GeneratedRegex(@"conversion failed|error while opening encoder|device creation failed|failed to initialize|error initializing|no va display|permission denied|no such file|no space left|invalid data|input/output error",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PlaybackSignal();

    private static PlaybackProgress? ParsePlaybackProgress(string line)
    {
        if (!line.StartsWith("frame=", StringComparison.Ordinal) && !line.StartsWith("size=", StringComparison.Ordinal)
            && !line.StartsWith("Lsize=", StringComparison.Ordinal)) return null;
        var values = ProgressField().Matches(line).ToLookup(match => match.Groups["key"].Value, match => match.Groups["value"].Value);
        if (!values.Contains("frame") && !values.Contains("time")) return null;
        return new PlaybackProgress(values["time"].FirstOrDefault(), values["frame"].FirstOrDefault(),
            values["fps"].FirstOrDefault(), values["speed"].FirstOrDefault(), values["drop"].FirstOrDefault());
    }

    [GeneratedRegex(@"\b(?<key>frame|time|fps|speed|drop)= *(?<value>-?[0-9][0-9:.]*x?)(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ProgressField();

    internal static EncoderEvidence[] VideoEncoders(string header) =>
        StreamEncoder().Matches(header).Select(match => match.Groups["encoder"].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Select(encoder => new EncoderEvidence(encoder,
                encoder switch
                {
                    "libx264" or "libx264rgb" or "libx265" or "libsvtav1" or "libaom-av1"
                        or "libvpx" or "libvpx-vp9" or "librav1e" => "Software encoder",
                    _ => encoder.Split('_').Last() switch
                    {
                        "qsv" => "Intel Quick Sync encoder",
                        "vaapi" => "VA-API encoder",
                        "nvenc" => "NVIDIA NVENC encoder",
                        "amf" => "AMD AMF encoder",
                        "videotoolbox" => "VideoToolbox encoder",
                        "v4l2m2m" => "V4L2 M2M encoder",
                        "rkmpp" => "Rockchip MPP encoder",
                        _ => null
                    }
                })).Where(evidence => evidence.Acceleration is not null).Take(4).ToArray();

    [GeneratedRegex(@"^[ \t]+(?:Stream #\d+:\d+ -> #\d+:\d+ \([^\r\n]* -> [a-z0-9_-]+ \((?<encoder>[a-z0-9_-]+)\)\)|[a-z0-9_:@.-]+ -> Stream #\d+:\d+ \((?<encoder>[a-z0-9_-]+)\))\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex StreamEncoder();

    private static Mount? FindMount(string path, Dictionary<string, Mount> mounts)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (mounts.TryGetValue(current, out var mount)) return mount;
        }
        return null;
    }

    private static bool IsExternalRoot(string path) => path is "/mnt" or "/media"
        || path.StartsWith("/mnt/", StringComparison.Ordinal) || path.StartsWith("/media/", StringComparison.Ordinal);

    private static Volume ReadVolume(Mount mount)
    {
        try
        {
            var drive = new DriveInfo(mount.Path);
            var total = drive.TotalSize;
            var available = drive.AvailableFreeSpace;
            if (total > 0 && available >= 0 && available <= total)
                return new Volume(mount.Path, mount.Kind, mount.Device, total, available, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The mount might have disappeared or denied access.
        }
        return new Volume(mount.Path, mount.Kind, mount.Device, null, null, "Capacity is unavailable.");
    }

    internal LogCatalog ReadCatalog(CancellationToken ct)
    {
        var candidates = new List<LogEntry>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(paths.LogDirectoryPath))
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned > MaxLogEntries) break;
            var file = new FileInfo(path);
            try
            {
                if (IsLogName(file.Name) && IsOrdinaryFile(file.Attributes))
                    candidates.Add(new LogEntry(file.Name, file.LastWriteTimeUtc, file.Length));
            }
            catch (FileNotFoundException)
            {
                // The file rotated during enumeration.
            }
        }
        var catalog = new LogCatalog(DateTimeOffset.UtcNow,
            candidates.OrderByDescending(log => log.ModifiedAt).Take(20).ToArray(), scanned > MaxLogEntries);
        Volatile.Write(ref _logCache, catalog);
        return catalog;
    }

    internal LogExcerpt ReadTail(string name, CancellationToken ct, int limit = LogBytes)
    {
        using var fs = OpenLog(name);
        var result = ReadTail(fs, name, ct, limit);
        if (limit != LogBytes) return result;
        if (LogCache.Count >= 4)
            LogCache.TryRemove(LogCache.OrderBy(pair => pair.Value.SampledAt).First().Key, out _);
        LogCache[name] = result;
        return result;
    }

    private static LogExcerpt ReadTail(FileStream fs, string name, CancellationToken ct, int limit)
    {
        if (!fs.CanSeek) throw new FileNotFoundException();
        var length = fs.Length;
        var count = (int)Math.Min(length, limit + 1);
        fs.Seek(length - count, SeekOrigin.Begin);
        var (bytes, read) = ReadLogBytes(fs, count, ct);
        var truncated = length > limit;
        var start = 0;
        if (truncated)
        {
            var separator = bytes.AsSpan(0, read).IndexOfAny((byte)'\n', (byte)'\r');
            // Prefer complete lines. Keep the bounded suffix if the only available line is longer than the limit.
            start = separator >= 0 && separator + 1 < read ? separator + 1 : Math.Max(0, read - limit);
            while (start < read && (bytes[start] & 0xc0) == 0x80) start++;
        }
        var content = Encoding.UTF8.GetString(bytes, start, read - start);
        return new LogExcerpt(DateTimeOffset.UtcNow, name, length, truncated, content);
    }

    private FileStream OpenLog(string name)
    {
        if (!IsLogName(name)) throw new FileNotFoundException();
        var path = Path.Join(paths.LogDirectoryPath, name);
        if (!IsOrdinaryFile(File.GetAttributes(path))) throw new FileNotFoundException();
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
    }

    private static (byte[] Bytes, int Read) ReadLogBytes(FileStream fs, int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            ct.ThrowIfCancellationRequested();
            var next = fs.Read(bytes.AsSpan(read));
            if (next == 0) break;
            read += next;
        }
        ct.ThrowIfCancellationRequested();
        return (bytes, read);
    }

    private static bool IsOrdinaryFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0;
    private static bool IsFresh(DateTimeOffset timestamp, int seconds)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"\A(?:log(?:[_-][0-9A-Za-z._-]+)?|jellyfin[0-9]{8}|FFmpeg[._-][0-9A-Za-z._-]+)\.(?:log|txt)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LogName();

    [GeneratedRegex(@"\\[0-7]{3}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MountEscape();

    private sealed record Mount(string Path, string Kind, string Device);
    internal sealed record Volume(string Mount, string FileSystem, string Device, long? Total, long? Available, string? Problem);
    internal sealed record LibraryLocation(string Library, string Path, string? Mount, string? Problem);
    internal sealed record StorageReport(DateTimeOffset SampledAt, Volume[] Volumes, LibraryLocation[] Locations, bool Limited, string? Problem);
    internal sealed record BranchReport(Volume[] Branches, bool Limited, string? Problem);
    internal sealed record EncoderEvidence(string Encoder, string? Acceleration);
    internal sealed record PlaybackLog(string Name, DateTimeOffset ModifiedAt, string[] Signals, bool Truncated,
        EncoderEvidence[] Encoders, long Bytes, int SampledLines, int SignalCount, PlaybackProgress? Progress);
    internal sealed record PlaybackProgress(string? Position, string? Frames, string? Fps, string? Speed, string? Dropped);
    internal sealed record PlaybackTriageReport(DateTimeOffset CheckedAt, PlaybackLog[] Logs, bool Limited);
    internal sealed record LogEntry(string Name, DateTimeOffset ModifiedAt, long Bytes);
    internal sealed record LogCatalog(DateTimeOffset SampledAt, LogEntry[] Files, bool Limited);
    internal sealed record LogExcerpt(DateTimeOffset SampledAt, string Name, long Bytes, bool Truncated, string Content);
}
