# VMS Admin Ext. Dash.

Administrator dashboard for Jellyfin Server **12.4.0** and Jellyfin Web from the same release. The build targets .NET 10.

The Overview shows timestamped trends for playback starts, sign-ins, indexed additions, and free space. Live stream counts and playback methods refresh while the Overview is visible. The Media & usage, Logs, Imports, Notifications, and Settings tabs retain the detailed checks. Jellyfin stores successful capacity and mount checks and the scheduled charts. Most other manual checks return after navigation in the same administrator browser tab. Raw log excerpts are fetched on demand. The shareable snapshot omits user and device names, titles, paths, and raw log excerpts.

Three Jellyfin tasks support independent schedules. The Diagnostic Scan samples media storage and checks recent Jellyfin and FFmpeg errors. The new movies and TV task samples library additions and supports Discord alerts. **Update visual dash** refreshes the Overview charts from recorded activity and the last saved media and storage samples. A separately hosted HTTPS monitor can send Discord outage and recovery notifications. Notifications shows webhook readiness, delivery history, and queued media alerts.

## Release status

`0.3.0.0` adds the saved Overview, live playback refresh, a new dashboard icon, and bounded waiting when the scheduled scans share the state lock. The plugin ID, settings file, and existing scheduled-task identities carry forward from `0.2.0.0`.

## Build and use in staging

With a .NET 10 SDK, Python 3 and NuGet access, run from this directory:

```bash
python3 Scripts/build-release.py --out dist
```

To generate a catalog manifest, also pass `--archive-url` with the final HTTPS URL of the generated release ZIP. Host the generated ZIP and the manifest over HTTPS. Use **Dashboard → Plugins → Repositories** to add the manifest, then install from the catalog on a staging server. Jellyfin requires a restart to load the plugin. An installation zip contains the compiled plugin DLL at the root. The source archive has a project directory and cannot be used in the catalog.

After restart, open **Dashboard → VMS Admin Ext. Dash.** or the installed plugin's configuration page.

## Schedules and Discord

Open **Dashboard → Scheduled Tasks → VMS Admin Ext. Dash.** to run each task manually or set its trigger. New installs start with no triggers. Run diagnostics and media before **Update visual dash** so the chart refresh can use their latest samples. The chart uses up to 500 activity events and the latest 120 indexed movies and episodes. It labels capped samples.

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

Open **Catalog**, install **VMS Admin Ext. Dash.**, and restart Jellyfin when convenient. Jellyfin's **Update Plugins** scheduled task checks the configured feed for compatible versions.

To publish an update, increment both `AssemblyVersion` and `Version`, then push the source and a matching tag such as `v0.3.0.0`. You can also run **Publish Jellyfin plugin** from GitHub Actions. The workflow tests and builds the plugin, uploads a versioned ZIP and manifest to a draft release, then publishes it as latest. Earlier catalog entries remain available.

## Local checks

```bash
node tests/dashboard.test.cjs
python3 -m unittest discover -s tests -v
```
