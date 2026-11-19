"""Disposable Jellyfin 12.4 catalog test. All server and Arr traffic stays on loopback."""
import contextlib
import hashlib
import http.server
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "http://127.0.0.1:18096"
FEED = "http://127.0.0.1:18097"
PLUGIN = "626706cb-1cd6-4f67-94ca-c5b54f297d1d"
NAME = "VMS Admin Ext. Dash."
AUTH = 'MediaBrowser Client="VMS Lab", Device="CI", DeviceId="vms-upgrade", Version="1.0"'
TASK_TIMES = {"RemoteAdminDeskScan": 72000000000, "RemoteAdminDeskMedia": 81000000000,
              "RemoteAdminDeskVisual": 90000000000}
MISSING = object()


def normalized(value):
    if isinstance(value, dict):
        return {key[0].upper() + key[1:]: normalized(item) for key, item in value.items()}
    if isinstance(value, list):
        return [normalized(item) for item in value]
    return value


def call(route, body=MISSING, token=None, method=None):
    data = None if body is MISSING else json.dumps(body).encode()
    req = urllib.request.Request(BASE + route, data=data, method=method,
                                 headers={"Authorization": AUTH + (f', Token="{token}"' if token else ""),
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=8) as response:
            raw = response.read()
            return normalized(json.loads(raw)) if raw else None
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{req.get_method()} {route} returned HTTP {error.code}") from None


def wait(check, label, seconds=45):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(0.5)
    raise RuntimeError(f"Timed out waiting for {label}")


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


class FakeArr(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        raw = json.dumps({"totalRecords": 1, "records": [
            {"sourceTitle": "Synthetic completed import", "eventType": "downloadFolderImported",
             "date": "2026-11-19T13:00:00Z", "data": {"ImportedPath": "/synthetic/arr-only.mkv"}}
        ]}).encode()
        self.send_response(200 if self.path.startswith("/api/v3/history?") else 404)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


@contextlib.contextmanager
def serve(port, handler):
    with http.server.ThreadingHTTPServer(("127.0.0.1", port), handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield
        finally:
            server.shutdown()
            thread.join()


class Lab:
    def __init__(self, root):
        self.root = root
        self.launches = 0
        self.token = None
        for subdirectory in ("data", "config", "cache", "logs", "feed"):
            (root / subdirectory).mkdir()
        (root / "config/network.xml").write_text("""<NetworkConfiguration>
<InternalHttpPort>18096</InternalHttpPort><PublicHttpPort>18096</PublicHttpPort>
<LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess>
<EnableHttps>false</EnableHttps><AutoDiscovery>false</AutoDiscovery><EnableUPnP>false</EnableUPnP>
</NetworkConfiguration>""")
        self.ffmpeg = Path("/usr/lib/jellyfin-ffmpeg/ffmpeg")
        self.settings = root / "data/data/remote-admin-desk/settings.json"

    @contextlib.contextmanager
    def start(self):
        self.launches += 1
        log_path = self.root / f"console-{self.launches}.log"
        with log_path.open("w") as log:
            command = ["jellyfin/jellyfin", "--datadir", str(self.root / "data"),
                       "--configdir", str(self.root / "config"), "--cachedir", str(self.root / "cache"),
                       "--logdir", str(self.root / "logs"), "--webdir", "jellyfin/jellyfin-web",
                       "--ffmpeg", str(self.ffmpeg)]
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                       env={**os.environ, "ASPNETCORE_URLS": BASE})
            try:
                def ready():
                    if process.poll() is not None:
                        raise RuntimeError(f"Jellyfin exited with {process.returncode}")
                    if "Startup complete" not in log_path.read_text():
                        return False
                    try:
                        info = call("/System/Info/Public")
                    except (OSError, RuntimeError):
                        return False
                    return info["Version"] in {"12.4", "12.4.0", "12.4.0.0"}
                wait(ready, "Jellyfin startup")
                yield
            except Exception:
                print("\n".join(log_path.read_text().splitlines()[-35:]), flush=True)
                raise
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=12)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()

    def get(self, route):
        return call(route, token=self.token)

    def post(self, route, body=MISSING):
        return call(route, body, self.token, method="POST")

    def plugins(self):
        return [entry for entry in self.get("/Plugins") if entry["Id"].replace("-", "").lower() == PLUGIN.replace("-", "")]

    def tasks(self):
        return {task["Key"]: task for task in self.get("/ScheduledTasks?isHidden=false")}

    def catalog(self, candidate=False):
        entries = []
        packages = [("0.3.0.0", "baseline.zip")]
        if candidate:
            packages.insert(0, ("0.4.0.0", "VMS-Admin-Ext-Dash-0.4.0.0.zip"))
        for version, filename in packages:
            source = Path(filename if version == "0.3.0.0" else f"dist/{filename}")
            content = source.read_bytes()
            (self.root / "feed" / filename).write_bytes(content)
            entries.append({"version": version, "changelog": "Lab package",
                            "targetAbi": "12.4.0.0", "sourceUrl": f"{FEED}/{filename}",
                            "checksum": hashlib.md5(content, usedforsecurity=False).hexdigest(),
                            "timestamp": "2026-11-19T14:00:00Z"})
        path = self.root / "feed" / "manifest.json"
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps([{"guid": PLUGIN, "name": NAME, "description": "Lab package",
                                         "owner": "VMS", "category": "General", "versions": entries}]))
        temporary.replace(path)

    def seed(self):
        username, password = "vms-ci", secrets.token_hex(18)
        self.post("/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US",
                                             "PreferredMetadataLanguage": "en"})
        self.get("/Startup/FirstUser")
        self.post("/Startup/User", {"Name": username, "Password": password})
        self.post("/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False})
        self.post("/Startup/Complete")
        self.token = self.post("/Users/AuthenticateByName", {"Username": username, "Pw": password})["AccessToken"]
        print("Disposable Jellyfin initialized.", flush=True)
        self.post("/Repositories", [{"Name": "Local validation feed", "Url": f"{FEED}/manifest.json", "Enabled": True}])
        query = urllib.parse.urlencode({"assemblyGuid": PLUGIN, "version": "0.3.0.0"})
        self.post("/Packages/Installed/" + urllib.parse.quote(NAME) + "?" + query)
        wait(lambda: any(p["Version"] == "0.3.0.0" for p in self.plugins()), "catalog baseline install")
        print("Published 0.3.0.0 baseline installed from test catalog.", flush=True)

    def setup_upgrade(self):
        assert self.plugins()[0]["Version"] == "0.3.0.0"
        self.get("/RemoteAdminDesk/Access")
        values = {"sonarrKey": "syntheticSonarr", "radarrKey": "syntheticRadarr",
                  "mediaBranches": "/mnt/synthetic",
                  "diagnosticsWebhook": "https://discord.com/api/webhooks/000000000000000000/" + "A" * 40,
                  "mediaWebhook": "https://discord.com/api/webhooks/000000000000000001/" + "B" * 40}
        call("/RemoteAdminDesk/Settings", values, self.token, "PUT")
        for key, ticks in TASK_TIMES.items():
            self.post(f'/ScheduledTasks/{self.tasks()[key]["Id"]}/Triggers',
                      [{"Type": "DailyTrigger", "TimeOfDayTicks": ticks}])
        baseline = list((self.root / "data/plugins").glob("*_0.3.0.0/meta.json"))[0]
        assert json.loads(baseline.read_text())["autoUpdate"] is True, "Baseline has automatic updates disabled"
        expected_settings = self.settings.read_bytes()
        expected_triggers = {key: self.tasks()[key]["Triggers"] for key in TASK_TIMES}
        updater = self.tasks()["PluginUpdates"]
        wait(lambda: self.tasks()["PluginUpdates"]["State"].lower() == "idle", "startup updater idle")
        self.catalog(candidate=True)
        self.post(f'/ScheduledTasks/Running/{updater["Id"]}')
        wait(lambda: any(p["Version"] == "0.4.0.0" for p in self.plugins()), "automatic catalog upgrade")
        wait(lambda: self.tasks()["PluginUpdates"]["State"].lower() == "idle", "catalog updater completion")
        assert self.tasks()["PluginUpdates"]["LastExecutionResult"]["Status"].lower() == "completed"
        print("Automatic updater staged 0.4.0.0 with the previous release still in the feed.", flush=True)
        return expected_settings, expected_triggers

    def verify(self, expected_settings, expected_triggers):
        self.get("/RemoteAdminDesk/Access")
        assert self.settings.read_bytes() == expected_settings and self.settings.stat().st_mode & 0o777 == 0o600
        assert {key: self.tasks()[key]["Triggers"] for key in TASK_TIMES} == expected_triggers
        checks = self.get("/RemoteAdminDesk/ImportHistory")
        assert len(checks["Services"]) == 2
        for service in checks["Services"]:
            assert service["State"] == "ready" and service["Items"][0]["Jellyfin"] == "not-in-sample"
        wait(lambda: self.tasks()["PluginUpdates"]["State"].lower() == "idle", "updater idle after activation")
        time.sleep(8)
        assert not self.get("/System/Info")["HasPendingRestart"], "Updater set Restart pending after activation"
        assert any(p["Version"] == "0.4.0.0" and p["Status"].lower() == "active" for p in self.plugins())
        print("PASS: catalog upgrade, active candidate, unchanged settings and triggers, live Import History, and clean restart.", flush=True)


subprocess.run(["sudo", "apt-get", "update", "-qq"], check=True)
subprocess.run(["sudo", "apt-get", "install", "-y", "./ffmpeg.deb"], check=True)
subprocess.run(["/usr/lib/jellyfin-ffmpeg/ffmpeg", "-version"], check=True, stdout=subprocess.DEVNULL)
subprocess.run(["/usr/lib/jellyfin-ffmpeg/ffprobe", "-version"], check=True, stdout=subprocess.DEVNULL)

with tempfile.TemporaryDirectory(prefix="vms-ci-") as folder:
    root = Path(folder)
    lab = Lab(root)
    lab.catalog()
    with serve(18097, lambda *args, **kwargs: Quiet(*args, directory=root / "feed", **kwargs)), \
         serve(8989, FakeArr), serve(7878, FakeArr):
        with lab.start():
            lab.seed()
        with lab.start():
            expected = lab.setup_upgrade()
        with lab.start():
            lab.verify(*expected)
