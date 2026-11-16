#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
version="${1:-}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(~[A-Za-z0-9.]+)?$ ]]; then
    printf '用法：bash Linux/packaging/build.sh VERSION [OUTPUT]\n' >&2
    exit 2
fi
output="${2:-$root/artifacts/linux}"
mkdir -p -- "$output"
output="$(cd -- "$output" && pwd -P)"
stage="$(mktemp -d)"
trap 'rm -rf -- "$stage"' EXIT

cli="snnuautologin_${version}_amd64.deb"
desktop="snnuautologin-desktop_${version}_amd64.deb"
archive="SNNUAutoLogin-${version}-linux-x64.tar.gz"
assembly_version="${version%%~*}"
informational_version="${version//~/-}"
dotnet publish "$root/Linux/Agent/SNNUAutoLogin.Linux.csproj" -c Release -r linux-x64 --self-contained true \
    "-p:Version=$informational_version" "-p:FileVersion=$assembly_version.0" -o "$stage/cli"
# 桌面端不再是 .NET 程序：界面是 Python + GTK4/libadwaita，运行时用系统自带的 python3-gi，
# 所以这里没有 dotnet publish，桌面包从 34MB 掉到几十 KB。

cli_pkg="$stage/cli-pkg"
desktop_pkg="$stage/desktop-pkg"
desktop_src="$root/Linux/DesktopGtk/snnuautologin_desktop"
common='libc6 (>= 2.35), libgcc-s1, libstdc++6, libicu74 | libicu76, libssl3t64, zlib1g'
mkdir -p "$cli_pkg/DEBIAN" "$cli_pkg/usr/lib/snnuautologin/cli" "$cli_pkg/usr/bin"
mkdir -p "$desktop_pkg/DEBIAN" "$desktop_pkg/usr/lib/snnuautologin/desktop" "$desktop_pkg/usr/bin"
cp -a "$stage/cli/." "$cli_pkg/usr/lib/snnuautologin/cli/"
cp -a "$desktop_src" "$desktop_pkg/usr/lib/snnuautologin/desktop/"
find "$desktop_pkg" -name '__pycache__' -type d -prune -exec rm -rf {} +
ln -s ../lib/snnuautologin/cli/snnuautologin "$cli_pkg/usr/bin/snnuautologin"
cat > "$desktop_pkg/usr/bin/snnuautologin-desktop" <<'LAUNCHER'
#!/bin/sh
# 界面是 Python + GTK4/libadwaita，用的是系统的 python3-gi，不自带运行时。
PYTHONPATH="/usr/lib/snnuautologin/desktop${PYTHONPATH:+:$PYTHONPATH}" \
    exec /usr/bin/python3 -m snnuautologin_desktop "$@"
LAUNCHER
chmod 0755 "$desktop_pkg/usr/bin/snnuautologin-desktop"
install -Dm644 "$root/Linux/packaging/snnuautologin.service" "$cli_pkg/usr/lib/systemd/user/snnuautologin.service"
install -Dm644 "$root/Linux/packaging/snnuautologin.desktop" "$desktop_pkg/usr/share/applications/snnuautologin.desktop"
install -Dm644 "$root/Assets/app_256.png" "$desktop_pkg/usr/share/icons/hicolor/256x256/apps/snnuautologin.png"
cat > "$cli_pkg/DEBIAN/control" <<EOF
Package: snnuautologin
Version: $version
Architecture: amd64
Maintainer: SNNUAutoLogin contributors
Section: net
Priority: optional
Depends: $common, iproute2
Recommends: libsecret-tools
Suggests: network-manager
Description: SNNU campus network login command line and user service
EOF
cat > "$desktop_pkg/DEBIAN/control" <<EOF
Package: snnuautologin-desktop
Version: $version
Architecture: amd64
Maintainer: SNNUAutoLogin contributors
Section: net
Priority: optional
Depends: snnuautologin (= $version), python3 (>= 3.10), python3-gi (>= 3.42), gir1.2-gtk-4.0 (>= 4.10), gir1.2-adw-1 (>= 1.2), gir1.2-pango-1.0, librsvg2-common
Recommends: fonts-noto-cjk
Description: Desktop client for SNNU campus network login
 GTK4 与 libadwaita 写的桌面客户端，通过 Unix 域套接字与 snnuautologin 用户服务通信。
EOF

dpkg-deb --root-owner-group --build "$cli_pkg" "$output/$cli"
dpkg-deb --root-owner-group --build "$desktop_pkg" "$output/$desktop"
portable="SNNUAutoLogin-${version}-linux-x64"
mkdir -p "$stage/$portable/desktop"
mv "$stage/cli" "$stage/$portable/"
cp -a "$desktop_src" "$stage/$portable/desktop/"
find "$stage/$portable" -name '__pycache__' -type d -prune -exec rm -rf {} +
cat > "$stage/$portable/desktop/snnuautologin-desktop" <<'LAUNCHER'
#!/bin/sh
here="$(cd -- "$(dirname -- "$0")" && pwd -P)"
PYTHONPATH="$here${PYTHONPATH:+:$PYTHONPATH}" exec /usr/bin/python3 -m snnuautologin_desktop "$@"
LAUNCHER
chmod 0755 "$stage/$portable/desktop/snnuautologin-desktop"
install -m755 "$root/Linux/packaging/install-user-service.sh" "$stage/$portable/install-user-service.sh"
install -m644 "$root/Linux/README.md" "$stage/$portable/README.md"
tar -C "$stage" -czf "$output/$archive" "$portable"

# AppImage 是绿色版：后台和界面都塞在里面，不装包、双击就能跑。
# 没有 appimagetool 的机器上这一步会自己跳过，deb 和 tar.gz 照样出。
appimage="SNNUAutoLogin-${informational_version}-x86_64.AppImage"
bash "$root/Linux/packaging/build_appimage.sh" "$version" "$stage/$portable/cli" "$desktop_src" \
    "$root/Assets/app_256.png" "$output/$appimage"

files=("$cli" "$desktop" "$archive")
if [[ -s "$output/$appimage" ]]; then
    files+=("$appimage")
fi
(cd "$output" && sha256sum "${files[@]}" > SHA256SUMS.txt)
