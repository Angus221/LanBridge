#!/usr/bin/env bash
# Cross-publish a self-contained Windows 11 x64 single-file exe and zip it.
# The exe only runs on Windows. Building it needs the .NET 8 SDK.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${LANBRIDGE_WIN_OUT:-$ROOT/dist/windows}"
PUBLISH="$OUT/publish"
ZIP="$OUT/LanBridge-1.0.2-win-x64.zip"

mkdir -p "$PUBLISH"
rm -rf "$PUBLISH"/*
rm -f "$ZIP"

dotnet publish "$ROOT/windows/src/LanBridge.App/LanBridge.App.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "$PUBLISH"

if [[ ! -f "$PUBLISH/LanBridge.exe" ]]; then
  echo "publish did not produce LanBridge.exe" >&2
  exit 1
fi

# A single-file publish should not need sidecar dlls. Keep the zip to the exe.
extras="$(find "$PUBLISH" -type f ! -name 'LanBridge.exe' ! -name '*.pdb' | wc -l)"
if [[ "$extras" -ne 0 ]]; then
  echo "unexpected files next to LanBridge.exe:" >&2
  find "$PUBLISH" -type f ! -name 'LanBridge.exe' ! -name '*.pdb' >&2
  exit 1
fi

(
  cd "$PUBLISH"
  zip -q -j "$ZIP" LanBridge.exe
)
echo "$ZIP"
