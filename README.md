# VMS Expanded Admin Dashboard

Source preview for Jellyfin Server **12.4.0** and Jellyfin Web from the same release. The build targets .NET 10.

The admin dashboard has separate Overview, Media & usage, Logs, Imports, Notifications, and Settings tabs, with charts for playback methods, free-space history, indexed additions, codecs, playback activity, and log severity. It also shows scheduled-task results, grouped log findings, recent FFmpeg evidence, expected branch capacity, and current Sonarr/Radarr queue states. The Overview history separates successful sign-ins, connected sessions, and stream starts from Jellyfin's existing activity log. Its shareable snapshot omits user and device names, titles, paths, and raw log excerpts.

Two optional Jellyfin tasks support independent schedules. The diagnostic task samples media storage and checks recent Jellyfin and FFmpeg errors. The media task sends Discord notifications for newly indexed movies and episodes. A separately hosted HTTPS monitor can send Discord outage and recovery notifications. The Notifications tab shows webhook readiness, last recorded delivery, and queued media alerts without displaying credentials or making a network request to Discord.

## Release status

The dashboard calculations and the external monitor passed local tests. The server plugin compiled against Jellyfin 12.4 using .NET SDK 10.0.404 with zero warnings and errors. The dummy server played a generated video before installation. The corrected build loaded in a disposable Jellyfin 12.4 lab. Admin diagnostics, manual scan, and concurrent direct playback passed. The remaining staging checks and publication are still pending. The source archive is for review and staging. Keep Jellyfin Helper installed during evaluation if you depend on its broader analytics.

## Build and use in staging

With a .NET 10 SDK, Python 3 and NuGet access, run from this directory:

```bash
python3 Scripts/build-release.py --out dist
```

To generate a catalog manifest, also pass `--archive-url` with the final HTTPS URL of the generated release ZIP. Host the generated ZIP and the manifest over HTTPS. Use **Dashboard → Plugins → Repositories** to add the manifest, then install from the catalog on a staging server. Jellyfin requires a restart to load the plugin. An installation zip contains the compiled plugin DLL at the root. The source archive has a project directory and cannot be used in the catalog.

After restart, open **Dashboard → VMS Expanded Admin Dashboard** or the installed plugin's configuration page.

## Schedules and Discord

Open **Dashboard → Scheduled Tasks → VMS Expanded Admin Dashboard**. Set an independent trigger for each task you want. Both start with no triggers. A diagnostic interval of 15 to 30 minutes and a media interval of 5 to 15 minutes are suggested starting points. A daily sample is enough for storage history. Shorter intervals improve alert latency and reduce the chance of a large media burst exceeding the query cap.

On Linux, enter the diagnostics and media HTTPS Discord webhook URLs in **Settings → Plugin settings**. Both accept a standard webhook on `discord.com`. Use Notifications to check readiness and delivery. The diagnostic task alerts on low free space, unavailable measured mounts, recoveries, and recognized recent server or FFmpeg failures. The first successful media check establishes a baseline. Later checks send new titles in batches of up to 14 and retain a bounded queue on delivery errors. Library dates follow Jellyfin's indexing configuration. Confirmed downloader completion requires Arr or downloader history.

On other platforms, or to manage secrets outside the UI, provide `RAD_DIAGNOSTICS_DISCORD_WEBHOOK` and `RAD_MEDIA_DISCORD_WEBHOOK` in Jellyfin's private service environment. Existing environment values take precedence. VMS stores web-entered credentials in a service-owner-only Linux file with mode `0600`. Include that file only in protected backups. The dashboard reports whether a value is set and never returns the credential.

For downtime, put `Scripts/monitor.py` on an always-on machine outside the Jellyfin host. Provide `RAD_HEALTH_URL` with the externally reachable HTTPS Jellyfin `/health` URL and `RAD_UPTIME_DISCORD_WEBHOOK` with its webhook. Schedule one invocation every one or two minutes using the monitor machine's scheduler and a private configuration:

```bash
python3 /path/to/monitor.py --state /private/path/uptime.json
```

Three failed checks trigger an outage alert by default. A healthy response triggers recovery. This check detects unreachability from that machine. To determine whether the cause was a Jellyfin process crash, reverse proxy failure, or home network outage, compare the alert time with host and proxy logs.

Seerr has a built-in **Settings → Notifications → Discord** agent. Use the pending-request notification type for items awaiting approval. Its settings are independent of Jellyfin.

## Optional import and branch diagnostics

Enter Sonarr and Radarr API keys in Settings on Linux, or set `RAD_SONARR_API_KEY` and `RAD_RADARR_API_KEY` in the private Jellyfin service environment. VMS only issues a queue GET to Sonarr on `127.0.0.1:8989` and Radarr on `127.0.0.1:7878`. The upstream keys may grant broader rights and should stay private on the host. This initial integration assumes both services share Jellyfin's network namespace and use their default ports and base paths.

Enter expected branch mount points in Settings as a colon-separated list, or set `RAD_MEDIA_BRANCHES` in the service environment. For example, `/mnt/media-a:/mnt/media-b`. The branch view reads up to eight expected mount points, reports a missing mount even when its underlying directory still exists, and keeps branch numbers separate from union capacity. The diagnostic task can flag missing and recovered mounts when Discord is configured.

## GitHub Releases and Jellyfin updates

The catalog is the `manifest.json` asset attached to the latest published GitHub release. Add this URL in **Dashboard → Plugins → Repositories** after the first release is published.

```text
https://github.com/snider967/vms-expanded-admin-dashboard/releases/latest/download/manifest.json
```

Open **Catalog**, install **VMS Expanded Admin Dashboard**, and restart Jellyfin when convenient. The plugin build targets Jellyfin 12.4.0. Jellyfin's **Update Plugins** scheduled task checks the configured feed for new compatible versions.

To publish an update, increment both `AssemblyVersion` and `Version` in the project file, commit, and push. Then push a matching tag such as `v0.2.1.0`, or run **Publish Jellyfin plugin** from GitHub Actions on the commit's branch. The workflow tests and builds the plugin, uploads a versioned ZIP and manifest to a draft release, then publishes it as latest. Earlier catalog entries remain available. Use a new version for fixes to an already published release.

## Local checks

```bash
node tests/dashboard.test.cjs
python3 -m unittest discover -s tests -v
```
