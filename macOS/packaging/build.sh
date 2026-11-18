#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
output="${1:-$root/artifacts/macos}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
app="$stage/SNNUAutoLogin.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish "$root/macOS/SNNUAutoLogin.Mac.csproj" -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:NuGetAudit=false -o "$app/Contents/MacOS"
xcrun swiftc -swift-version 5 -target arm64-apple-macos14.0 -emit-library \
  "$root/macOS/Native/MacBridge.swift" -o "$app/Contents/MacOS/libSnnuMac.dylib" \
  -Xlinker -install_name -Xlinker @rpath/libSnnuMac.dylib
cp "$root/macOS/packaging/Info.plist" "$app/Contents/Info.plist"
mkdir "$stage/AppIcon.iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/Assets/app_256.png" --out "$stage/AppIcon.iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/Assets/app_256.png" --out "$stage/AppIcon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$stage/AppIcon.iconset" -o "$app/Contents/Resources/AppIcon.icns"
for notice in LICENSE NOTICE; do cp "$root/$notice" "$app/Contents/Resources/"; done
# Managed assemblies are data resources, not nested native executables.
python3 - "$app" <<'PYTHON'
from pathlib import Path
import shutil
import subprocess
import sys
app = Path(sys.argv[1])
mac = app / "Contents/MacOS"
resources = app / "Contents/Resources/payload"
resources.mkdir()
for entry in list(mac.iterdir()):
    if entry.is_file() and "Mach-O" in subprocess.check_output(["file", str(entry)], text=True):
        if entry.name != "SNNUAutoLogin.Mac":
            (resources / entry.name).symlink_to("../../MacOS/" + entry.name)
        continue
    shutil.move(str(entry), str(resources / entry.name))
    entry.symlink_to("../Resources/payload/" + entry.name)
PYTHON
while IFS= read -r -d '' binary; do
  if [[ "$binary" != "$app/Contents/MacOS/SNNUAutoLogin.Mac" ]] && file "$binary" | grep -q 'Mach-O'; then
    codesign --force --sign - "$binary"
  fi
done < <(find "$app/Contents/MacOS" -type f -print0)
codesign --force --sign - --entitlements "$root/macOS/packaging/Entitlements.plist" "$app"
codesign --verify --deep --strict "$app"
cp -R "$app" "$output/SNNUAutoLogin.app"
mkdir "$stage/image"
cp "$root/macOS/README.md" "$stage/image/测试说明.md"
ln -s /Applications "$stage/image/Applications"
mv "$stage/SNNUAutoLogin.app" "$stage/image/"
hdiutil create -volname SNNUAutoLogin -srcfolder "$stage/image" -ov -format UDZO \
  "$output/SNNUAutoLogin-1.6.5-macos-preview.1-macos-arm64.dmg"
cd "$output"
shasum -a 256 ./*.dmg > SHA256SUMS-macos-arm64.txt
