#!/usr/bin/env bash
# Usage: ./release.sh <version>   (e.g. 0.2.0)
# Builds the Windows release, writes the sha256 file, tags v<version> and publishes a GitHub Release.
# Needs a clean working tree, the .NET 10 SDK on PATH and an authenticated gh.
set -euo pipefail
cd "$(dirname "$0")"

version="${1:?usage: ./release.sh <version>}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must look like 1.2.3" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "working tree is not clean" >&2; exit 1; }

VERSION="$version" ./build-win.sh

out=dist/publish
(cd "$out" && printf '%s  BgCompass.exe\n' "$(shasum -a 256 BgCompass.exe | cut -d' ' -f1)" > BgCompass.exe.sha256)

git tag "v$version"
git push origin HEAD "v$version"
gh release create "v$version" "$out/BgCompass.exe" "$out/BgCompass.exe.sha256" dist/BgCompass-win-x64.zip \
  --repo SolasWyrd/bg-compass --verify-tag --title "v$version" --notes ""
