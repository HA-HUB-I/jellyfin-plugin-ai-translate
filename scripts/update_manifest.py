#!/usr/bin/env python3
"""Adds (or replaces) one version entry in the Jellyfin plugin repository manifest.

usage: update_manifest.py <manifest.json> <version> <targetAbi> <sourceUrl> <zip> <changelog>
"""
import hashlib
import json
import sys
from datetime import datetime, timezone

PLUGIN = {
    "guid": "4cad7295-fa8b-46c9-a2e0-b6e5abe8a234",
    "name": "AI Translate",
    "overview": "AI subtitles and voice-over: translate a subtitle or recognise the audio, priced before you choose",
    "description": (
        "Adds an AI subtitle provider to Jellyfin's subtitle search: translates an existing subtitle (embedded, external "
        "or from another provider) or recognises the speech in the audio track (OpenAI, Google Gemini) and translates it "
        "with an LLM. Shows the estimated price before you choose; monthly budget, cost log, stop button and disk "
        "clean-up. Also makes a TV-style AI voice-over audio track from subtitles in your language."
    ),
    "imageUrl": "https://raw.githubusercontent.com/HA-HUB-I/jellyfin-plugin-ai-translate/main/images/logo.png",
    "owner": "HA-HUB-I",
    "category": "General",
}


def main(manifest_path, version, target_abi, source_url, zip_path, changelog):
    with open(zip_path, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()  # Jellyfin verifies plugin zips with MD5

    try:
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    except FileNotFoundError:
        manifest = []

    plugin = next((p for p in manifest if p.get("guid") == PLUGIN["guid"]), None)
    if plugin is None:
        plugin = {**PLUGIN, "versions": []}
        manifest.append(plugin)
    plugin.update(PLUGIN)

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin.get("versions", []) if v.get("version") != version]

    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"manifest: {version} -> {source_url} (md5 {checksum})")


if __name__ == "__main__":
    if len(sys.argv) != 7:
        sys.exit(__doc__)
    main(*sys.argv[1:])
