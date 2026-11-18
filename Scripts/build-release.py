#!/usr/bin/env python3
"""Build the plugin for its configured Jellyfin version and create a catalog archive. Supply its final HTTPS URL for a manifest."""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.parse
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "Jellyfin.Plugin.RemoteAdminDesk.csproj"
PROPERTIES = ElementTree.parse(PROJECT).getroot().find("PropertyGroup")
VERSION = PROPERTIES.findtext("AssemblyVersion")
SERVER_VERSION = PROPERTIES.findtext("JellyfinVersion")
ABI = SERVER_VERSION + ".0"
SDK = PROPERTIES.findtext("TargetFramework").removeprefix("net")
DISPLAY_NAME = PROPERTIES.findtext("PluginDisplayName")
DLL = "Jellyfin.Plugin.RemoteAdminDesk.dll"
PACKAGE = "-".join(DISPLAY_NAME.split()) + f"-{VERSION}.zip"
GUID = "626706cb-1cd6-4f67-94ca-c5b54f297d1d"
TAG = f"v{VERSION}"


def version_parts(value):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", value):
        raise ValueError(f"Expected a four-component plugin version: {value}")
    return tuple(map(int, value.split(".")))


def previous_versions(path):
    if path is None:
        return []
    previous = json.loads(path.read_text())
    if not isinstance(previous, list) or len(previous) != 1 or previous[0].get("guid") != GUID:
        raise ValueError("The previous catalog does not belong to VMS.")
    versions = previous[0]["versions"]
    if any(version_parts(entry["version"]) >= version_parts(VERSION) for entry in versions):
        raise ValueError(f"{VERSION} must be newer than every published version.")
    return versions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    address = parser.add_mutually_exclusive_group()
    address.add_argument("--archive-url", help=f"Final HTTPS URL for {PACKAGE}")
    address.add_argument("--github-repository", help="GitHub repository as OWNER/REPOSITORY")
    parser.add_argument("--previous-manifest", type=Path, help="Retain earlier versions from this catalog")
    parser.add_argument("--validate-tag", help=f"Require the release tag to match {TAG}")
    args = parser.parse_args()
    if args.validate_tag is not None and args.validate_tag != TAG:
        parser.error(f"Project version {VERSION} requires tag {TAG}.")
    if args.out is None:
        if args.validate_tag and not (args.archive_url or args.github_repository or args.previous_manifest):
            version_parts(VERSION)
            return 0
        parser.error("--out is required to build a package.")
    if args.github_repository:
        if not re.fullmatch(r"[A-Za-z0-9-]+/[A-Za-z0-9_.-]+", args.github_repository):
            parser.error("Expected --github-repository OWNER/REPOSITORY.")
        args.archive_url = f"https://github.com/{args.github_repository}/releases/download/{TAG}/{PACKAGE}"
    if args.previous_manifest and not args.archive_url:
        parser.error("--previous-manifest requires a release URL or GitHub repository.")
    try:
        version_parts(VERSION)
        older = previous_versions(args.previous_manifest)
    except (OSError, KeyError, TypeError, AttributeError, ValueError) as error:
        parser.error(str(error))
    if args.archive_url:
        parsed = urllib.parse.urlsplit(args.archive_url)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
                or parsed.query or parsed.fragment or Path(parsed.path).name != PACKAGE):
            parser.error(f"--archive-url must be an HTTPS URL ending in {PACKAGE}")
    if not shutil.which("dotnet"):
        parser.exit(2, f"The .NET {SDK} SDK is required. No release files were generated.\n")
    with tempfile.TemporaryDirectory() as folder:
        target = Path(folder)
        subprocess.run(["dotnet", "build", str(PROJECT),
                        "-c", "Release", "-o", str(target)], check=True)
        args.out.mkdir(parents=True, exist_ok=True)
        archive = args.out / PACKAGE
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as zipped:
            zipped.write(target / DLL, DLL)
        binary = archive.read_bytes()
        (args.out / (PACKAGE + ".sha256")).write_text(hashlib.sha256(binary).hexdigest() + "  " + PACKAGE + "\n")
        if args.archive_url:
            payload = [{
                "guid": GUID,
                "name": DISPLAY_NAME,
                "description": "Administrator dashboard for playback, media storage, usage, logs, and scheduled alerts.",
                "overview": "Read-only Jellyfin diagnostics and optional Discord alerts",
                "owner": "VMS",
                "category": "General",
                "versions": [{
                    "version": VERSION, "changelog": f"{DISPLAY_NAME} for Jellyfin {SERVER_VERSION}.",
                    "targetAbi": ABI, "sourceUrl": args.archive_url,
                    "checksum": hashlib.md5(binary, usedforsecurity=False).hexdigest(),
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                }] + older,
            }]
            (args.out / "manifest.json").write_text(json.dumps(payload, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
