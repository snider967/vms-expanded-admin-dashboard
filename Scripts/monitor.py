#!/usr/bin/env python3
"""One HTTPS Jellyfin check per invocation, for a scheduler outside the Jellyfin host."""

import argparse
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def opener():
    return urllib.request.build_opener(NoRedirect())


def health_url(value):
    parts = urllib.parse.urlsplit(value)
    if (parts.scheme != "https" or not parts.hostname or parts.username or parts.password
            or parts.query or parts.fragment or not parts.path.endswith("/health")):
        raise ValueError("Use an HTTPS Jellyfin URL ending in /health.")
    return value


def discord_url(value):
    parts = urllib.parse.urlsplit(value)
    if (parts.scheme != "https" or parts.netloc.lower() != "discord.com" or parts.query or parts.fragment
            or not re.fullmatch(r"/api/webhooks/[0-9]{1,25}/[A-Za-z0-9_-]{10,200}", parts.path)):
        raise ValueError("Set RAD_UPTIME_DISCORD_WEBHOOK to a Discord webhook URL.")
    return value


def healthy(client, url):
    try:
        with client.open(url, timeout=4) as reply:
            return reply.status == 200 and reply.read(32).strip() == b"Healthy"
    except (OSError, urllib.error.URLError):
        return False


def notify(client, url, content):
    data = json.dumps({"content": content, "allowed_mentions": {"parse": []}}).encode()
    request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with client.open(request, timeout=5) as reply:
            return 200 <= reply.status < 300
    except (OSError, urllib.error.URLError):
        return False


def assess(prior, ok, threshold):
    failures = 0 if ok else min(threshold, prior["failures"] + 1)
    outage = not ok and failures >= threshold
    return {"failures": failures, "down": (not ok and prior["down"]) or outage}


def load_state(path):
    try:
        with path.open("rb") as stream:
            raw = stream.read(1025)
    except FileNotFoundError:
        return {"failures": 0, "down": False}
    if len(raw) > 1024:
        raise ValueError("Invalid monitor state.")
    state = json.loads(raw)
    if (not isinstance(state, dict) or type(state.get("down")) is not bool or type(state.get("failures")) is not int
            or not 0 <= state["failures"] <= 20):
        raise ValueError("Invalid monitor state.")
    return state


def save_state(path, state):
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        json.dump(state, stream)
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state", required=True, type=Path)
    parser.add_argument("--failures", default=3, type=int, choices=range(1, 21), metavar="1–20")
    options = parser.parse_args()
    try:
        url = health_url(os.environ.get("RAD_HEALTH_URL", ""))
        webhook = discord_url(os.environ.get("RAD_UPTIME_DISCORD_WEBHOOK", ""))
        import fcntl
        options.state.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        with options.state.with_suffix(".lock").open("a") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                return 0
            prior = load_state(options.state)
            client = opener()
            current = assess(prior, healthy(client, url), options.failures)
            if prior["down"] != current["down"]:
                message = ("Jellyfin is unreachable from the external monitor." if current["down"]
                           else "Jellyfin is reachable again from the external monitor.")
                if not notify(client, webhook, message):
                    print("Discord alert delivery failed. The next scheduled run will retry.", file=sys.stderr)
                    return 1
            save_state(options.state, current)
            return 0
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    except OSError:
        print("Cannot access monitor state.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
