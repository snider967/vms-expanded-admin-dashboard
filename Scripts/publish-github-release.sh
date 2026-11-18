#!/usr/bin/env bash
set -euo pipefail

repository="${1:?GitHub repository required}"
tag="${2:?Release tag required}"
commit="${3:?Commit SHA required}"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

python3 Scripts/build-release.py --validate-tag "$tag"
git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}" > "$tmp/tag-ref"
tag_commit="$(awk 'END { print $1 }' "$tmp/tag-ref")"
if [[ -n "$tag_commit" && "$tag_commit" != "$commit" ]]; then
    printf 'Tag %s already points to another commit. Bump the plugin version.\n' "$tag" >&2
    exit 1
fi
gh release list --repo "$repository" --limit 100 --json tagName,isDraft,isLatest > "$tmp/releases.json"
state="$(jq -r --arg tag "$tag" '.[] | select(.tagName == $tag) | if .isDraft then "draft" else "published" end' "$tmp/releases.json")"
if [[ "$state" == published ]]; then
    gh release download "$tag" --repo "$repository" --pattern 'manifest.json' --dir "$tmp"
    printf '%s is already published.\n' "$tag"
    exit 0
fi

latest="$(jq -r '.[] | select(.isLatest) | .tagName' "$tmp/releases.json")"
history=()
if [[ -n "$latest" ]]; then
    gh release download "$latest" --repo "$repository" --pattern 'manifest.json' --dir "$tmp"
    history=(--previous-manifest "$tmp/manifest.json")
fi

python3 Scripts/build-release.py --out "$tmp/dist" --github-repository "$repository" --validate-tag "$tag" "${history[@]}"
if [[ "$state" != draft ]]; then
    if [[ -n "$tag_commit" ]]; then
        gh release create "$tag" --repo "$repository" --verify-tag --draft --generate-notes
    else
        gh release create "$tag" --repo "$repository" --target "$commit" --draft --generate-notes
    fi
fi
gh release upload "$tag" "$tmp/dist/"* --clobber --repo "$repository"
gh release edit "$tag" --repo "$repository" --draft=false --latest
