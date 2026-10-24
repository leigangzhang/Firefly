#!/usr/bin/env bash
# 构建 FireFly 插件并打成 Jellyfin 插件包：out/FireFly_<版本>.zip
# 需要 .NET 9 SDK（macOS：brew install dotnet@9）。
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
TARGET_ABI="10.11.11.0"
GUID="4f1d2c8e-9b7a-4e35-8c61-f1ae0f17f1f1"
STAGE="out/plugin"

rm -rf "$STAGE" && mkdir -p "$STAGE"
dotnet publish src/FireFly.Jellyfin -c Release -o out/publish --nologo -v q
cp out/publish/FireFly.Jellyfin.dll out/publish/FireFly.Core.dll "$STAGE/"

cat > "$STAGE/meta.json" <<JSON
{
  "category": "General",
  "changelog": "",
  "description": "萤火虫：发现资源，一键交给迅雷下载，下载完成自动入库。",
  "guid": "$GUID",
  "name": "FireFly",
  "overview": "发现 → 迅雷下载 → 自动入库",
  "owner": "FireFly",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": ["FireFly.Core.dll", "FireFly.Jellyfin.dll"]
}
JSON

(cd "$STAGE" && rm -f "../FireFly_$VERSION.zip" && zip -q -X "../FireFly_$VERSION.zip" ./*)
echo "已生成 out/FireFly_$VERSION.zip"
