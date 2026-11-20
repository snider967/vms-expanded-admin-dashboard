# VMS Admin Ext. Dash.

Administrator dashboard for Jellyfin Server **12.4.0** and Jellyfin Web from the same release. The build targets .NET 10.

The Overview shows timestamped trends for playback starts, sign-ins, indexed additions, and free space. Live stream counts and playback methods refresh while the Overview is visible. The Media & usage, Logs, Imports, Notifications, and Settings tabs retain the detailed checks. Jellyfin stores successful capacity and mount checks and the scheduled charts. Most other manual checks return after navigation in the same administrator browser tab. Raw log excerpts are fetched on demand. The shareable snapshot omits user and device names, titles, paths, and raw log excerpts.

Three Jellyfin tasks support independent schedules. The Diagnostic Scan samples media storage and checks recent Jellyfin and FFmpeg errors. The new movies and TV task scans library additions for Discord alerts. **Update visual dash** refreshes the Overview charts from retained Jellyfin activity, indexed movie and episode creation dates, and saved storage measurements. A separately hosted HTTPS monitor can send Discord outage and recovery notifications. Notifications shows webhook readiness, delivery history, and queued media alerts.

In **Overview → Playback troubleshooting**, **Check recent transcodes** summarizes the four latest FFmpeg logs. Expand a log to see recorded position, frame count, frame rate, encoding speed, dropped frames, and any matching error lines. **Read raw log tail** fetches up to 96 KiB from the end of the selected file. Raw output is only displayed in the current page.

## Release status

**0.6.2.0** removes the 500-event and 120-item limits from Overview Trends and gives Free Space and Runway an independent 7-, 30-, and 90-day selector. Run **Update visual dash** after upgrading to replace the earlier chart snapshot. See [GitHub Releases](https://github.com/snider967/vms-expanded-admin-dashboard/releases) for release packages.

## Build and use in staging

With a .NET 10 SDK, Python 3 and NuGet access, run from this directory:

```bash
python3 Scripts/build-release.py --out dist
```

To generate a catalog manifest, also pass `--archive-url` with the final HTTPS URL of the generated release ZIP. Host the generated ZIP and the manifest over HTTPS. Use **Dashboard → Plugins → Repositories** to add the manifest, then install from the catalog on a staging server. Jellyfin requires a restart to load the plugin. An installation zip contains the compiled plugin DLL at the root. The source archive has a project directory and cannot be used in the catalog.

After restart, open **Dashboard → VMS Admin Ext. Dash.** or the installed plugin's configuration page.

## Schedules and Discord

Open **Dashboard → Scheduled Tasks → VMS Admin Ext. Dash.** to run each task manually or set its trigger. New installs start with no triggers. Run the Diagnostic Scan to collect storage readings, then run **Update visual dash**. The event charts count retained records across a rolling 24 hours or the latest 7, 30, or 90 UTC calendar dates, including the current date through the snapshot time. Free Space and Runway has its own 7-, 30-, and 90-day selector and defaults to 7 days. An earlier chart snapshot remains available until the refresh succeeds.

On Linux, enter the diagnostics and media HTTPS Discord webhook URLs in **Settings → Plugin settings**. Both accept a standard webhook on `discord.com`. Use Notifications to check readiness and delivery. The diagnostic task alerts on low free space, unavailable measured mounts, recoveries, and recognized recent server or FFmpeg failures. The first successful media check establishes a baseline. Later checks collect the complete scan window before sending one heading with the total. Longer title lists continue in additional Discord messages. Delivery resumes from its saved checkpoint after a retry or restart. Library dates follow Jellyfin's indexing configuration. Confirmed downloader completion requires Arr or downloader history.

On other platforms, or to manage secrets outside the UI, provide `RAD_DIAGNOSTICS_DISCORD_WEBHOOK` and `RAD_MEDIA_DISCORD_WEBHOOK` in Jellyfin's private service environment. Existing environment values take precedence. VMS stores web-entered credentials in a service-owner-only Linux file with mode `0600`. Include that file only in protected backups. The dashboard reports whether a value is set and never returns the credential.

For downtime, put `Scripts/monitor.py` on an always-on machine outside the Jellyfin host. Provide `RAD_HEALTH_URL` with the externally reachable HTTPS Jellyfin `/health` URL and `RAD_UPTIME_DISCORD_WEBHOOK` with its webhook. Schedule one invocation every one or two minutes using the monitor machine's scheduler and a private configuration:

```bash
python3 /path/to/monitor.py --state /private/path/uptime.json
```

Three failed checks trigger an outage alert by default. A healthy response triggers recovery. This check detects unreachability from that machine. To determine whether the cause was a Jellyfin process crash, reverse proxy failure, or home network outage, compare the alert time with host and proxy logs.

Seerr has a built-in **Settings → Notifications → Discord** agent. Use the pending-request notification type for items awaiting approval. Its settings are independent of Jellyfin.

## Optional import and branch diagnostics

Enter Sonarr and Radarr API keys in Settings on Linux, or set `RAD_SONARR_API_KEY` and `RAD_RADARR_API_KEY` in the private Jellyfin service environment. VMS issues read-only queue and history GET requests to Sonarr on `127.0.0.1:8989` and Radarr on `127.0.0.1:7878`. The upstream keys may grant broader rights and should stay private on the host. Both services must share Jellyfin's network namespace and use their default ports and base paths.

**Check import history** reads up to 20 recent grab, import, failure, and ignored-download events per service. Imported paths are compared by exact match against up to 120 recently indexed Jellyfin movies and episodes. If the file falls outside that sample or the services use different paths, VMS shows that it could not confirm a match. Grab events indicate release selection. Arr import events have their own timestamps, and exact downloader completion times are still unavailable.

Enter expected branch mount points in Settings as a colon-separated list, or set `RAD_MEDIA_BRANCHES` in the service environment. For example, `/mnt/media5tb:/mnt/media10tb`. The branch view reads up to eight expected mount points, reports a missing mount even when its underlying directory still exists, and keeps branch numbers separate from union capacity. The diagnostic task can flag missing and recovered mounts when Discord is configured.

## GitHub Releases and Jellyfin updates

The catalog is the `manifest.json` asset attached to the latest published GitHub release. Add this URL in **Dashboard → Plugins → Repositories** after the first release is published.

```text
https://github.com/snider967/vms-expanded-admin-dashboard/releases/latest/download/manifest.json
```

Open **Catalog**, install **VMS Admin Ext. Dash.**, and restart Jellyfin when convenient. Jellyfin's **Update Plugins** scheduled task checks the configured feed for compatible versions.

To publish an update, increment both `AssemblyVersion` and `Version`, then push the source and a matching tag such as `v0.3.0.0`. You can also run **Publish Jellyfin plugin** from GitHub Actions. The workflow tests and builds the plugin, uploads a versioned ZIP and manifest to a draft release, then publishes it as latest. Earlier catalog entries remain available.

## Local checks

```bash
dotnet run --project tests/Trends.csproj -c Release
node --test tests/dashboard.test.cjs
python3 -m unittest discover -s tests -v
```
