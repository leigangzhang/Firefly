#!/usr/bin/env bash
# 不经插件库，直接把插件装进本机 Jellyfin.app（开发自测用）。
# 会先退出 Jellyfin 再复制 DLL（运行中覆盖 DLL 会报 BadImageFormatException），复制完重新打开。
set -euo pipefail
cd "$(dirname "$0")"

PLUGINS="${JELLYFIN_PLUGINS_DIR:-$HOME/Library/Application Support/jellyfin/plugins}"
./build.sh
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)

osascript -e 'quit app "Jellyfin"' >/dev/null 2>&1 || true
sleep 3

# 清掉旧版本目录（插件库安装的目录名同样以 FireFly_ 开头）
find "$PLUGINS" -maxdepth 1 -type d -name 'FireFly_*' -exec rm -rf {} +
mkdir -p "$PLUGINS/FireFly_$VERSION"
cp out/plugin/* "$PLUGINS/FireFly_$VERSION/"
echo "已安装到 $PLUGINS/FireFly_$VERSION"

open -a Jellyfin
