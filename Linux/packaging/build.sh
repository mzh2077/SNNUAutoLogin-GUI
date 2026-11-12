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
dotnet publish "$root/Linux/Desktop/SNNUAutoLogin.Linux.Desktop.csproj" -c Release -r linux-x64 --self-contained true \
    "-p:Version=$informational_version" "-p:FileVersion=$assembly_version.0" -o "$stage/desktop"

cli_pkg="$stage/cli-pkg"
desktop_pkg="$stage/desktop-pkg"
common='libc6 (>= 2.35), libgcc-s1, libstdc++6, libicu74 | libicu76, libssl3t64, zlib1g'
mkdir -p "$cli_pkg/DEBIAN" "$cli_pkg/usr/lib/snnuautologin/cli" "$cli_pkg/usr/bin"
mkdir -p "$desktop_pkg/DEBIAN" "$desktop_pkg/usr/lib/snnuautologin/desktop" "$desktop_pkg/usr/bin"
cp -a "$stage/cli/." "$cli_pkg/usr/lib/snnuautologin/cli/"
cp -a "$stage/desktop/." "$desktop_pkg/usr/lib/snnuautologin/desktop/"
ln -s ../lib/snnuautologin/cli/snnuautologin "$cli_pkg/usr/bin/snnuautologin"
ln -s ../lib/snnuautologin/desktop/snnuautologin-desktop "$desktop_pkg/usr/bin/snnuautologin-desktop"
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
Depends: snnuautologin (= $version), $common, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libxrender1, libfontconfig1, libgl1, libice6, libsm6, libxkbcommon0
Recommends: fonts-noto-cjk
Description: Desktop client for SNNU campus network login
EOF

dpkg-deb --root-owner-group --build "$cli_pkg" "$output/$cli"
dpkg-deb --root-owner-group --build "$desktop_pkg" "$output/$desktop"
portable="SNNUAutoLogin-${version}-linux-x64"
mkdir -p "$stage/$portable"
mv "$stage/cli" "$stage/desktop" "$stage/$portable/"
install -m755 "$root/Linux/packaging/install-user-service.sh" "$stage/$portable/install-user-service.sh"
install -m644 "$root/Linux/README.md" "$stage/$portable/README.md"
tar -C "$stage" -czf "$output/$archive" "$portable"
(cd "$output" && sha256sum "$cli" "$desktop" "$archive" > SHA256SUMS.txt)
