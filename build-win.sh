#!/usr/bin/env bash
# Builds the Windows x64 release: dist/BgCompass-win-x64.zip with BgCompass.exe (self-contained, single file) and README.txt.
# Runs on macOS or Linux; needs the .NET 10 SDK on PATH (or DOTNET_ROOT set up by the caller).
set -euo pipefail
cd "$(dirname "$0")"

out=dist/publish
# Optional VERSION env (release.sh) overrides <Version> from the csproj.
version_args=()
[ -n "${VERSION:-}" ] && version_args=(-p:Version="$VERSION")
rm -rf dist
mkdir -p "$out"

# No trimming: Avalonia compiled bindings and XAML loading use reflection, and trimming them away breaks the UI at run time.
# Compression keeps the single file small. IncludeNativeLibrariesForSelfExtract bundles the native Skia and HarfBuzz
# into the exe; .NET extracts them to %TEMP%\.net on first run.
dotnet publish src/BgCompass.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false \
  -p:SatelliteResourceLanguages=en \
  ${version_args[@]+"${version_args[@]}"} \
  -o "$out"

# Anything but the exe is noise for the player (pdb, xml docs).
find "$out" -type f ! -name 'BgCompass.exe' -delete

cat > "$out/README.txt" <<'EOF'
BG Compass

1. Запустите BgCompass.exe. SmartScreen: «Подробнее» > «Выполнить в любом случае».
2. Настройки > «Включить логи», затем перезапустите Hearthstone.
3. Играйте в окне или в окне без рамки.

Ctrl+Shift+H  показать или скрыть панель
Ctrl+Shift+T  клики сквозь панель

Данные: %LOCALAPPDATA%\BgCompass\. Лицензии: THIRD_PARTY.md.
EOF

cp THIRD_PARTY.md src/BgCompass.App/Assets/Fonts/OFL.txt "$out/"
(cd "$out" && zip -9 -q ../BgCompass-win-x64.zip BgCompass.exe README.txt THIRD_PARTY.md OFL.txt)
ls -l "$out/BgCompass.exe" dist/BgCompass-win-x64.zip
file "$out/BgCompass.exe"
