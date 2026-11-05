#!/usr/bin/env bash
# Builds Oversight-<rid>.AppImage on a Linux machine of the same architecture.
# Usage: tools/build-appimage.sh linux-x64|linux-arm64 [extra dotnet publish args, e.g. -p:Version=2.5.0]
set -euo pipefail
rid="$1"; shift
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "unknown rid $rid"; exit 1 ;;
esac
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/out/$rid"
app="$root/out/AppDir-$rid"

dotnet publish "$root/Oversight.Linux/Oversight.Linux.csproj" -c Release -r "$rid" -o "$out" "$@"

rm -rf "$app"
mkdir -p "$app/usr/bin"
cp "$out/oversight" "$app/usr/bin/oversight"
cp "$root/Oversight.Linux/Packaging/AppRun" "$app/AppRun"
cp "$root/Oversight.Linux/Packaging/oversight.desktop" "$app/oversight.desktop"
cp "$root/Oversight.Linux/Assets/oversight.png" "$app/oversight.png"
chmod +x "$app/AppRun" "$app/usr/bin/oversight"

tool="$root/out/appimagetool-$arch"
[ -x "$tool" ] || { curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$arch.AppImage"; chmod +x "$tool"; }
# Extract-and-run so it works without FUSE (CI runners, containers).
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$arch" "$tool" --no-appstream "$app" "$root/Oversight-$rid.AppImage"
echo "Built $root/Oversight-$rid.AppImage"
