#!/usr/bin/env bash
# 生成 Jellyfin 插件库：out/repo/manifest.json + 插件包。
# 用法：./make-repo.sh <插件库对外地址>
#   本机试装：./make-repo.sh http://127.0.0.1:8765 && (cd out/repo && python3 -m http.server 8765)
#            然后在 Jellyfin 控制台 → 插件 → 存储库 添加 http://127.0.0.1:8765/manifest.json
#   正式发布：./make-repo.sh https://你的域名/firefly ，把 out/repo 下的文件上传到该地址（M3）
set -euo pipefail
cd "$(dirname "$0")"
BASE_URL="${1:?用法：./make-repo.sh <插件库对外地址>}"
BASE_URL="${BASE_URL%/}"

./build.sh
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
ZIP="FireFly_$VERSION.zip"
mkdir -p out/repo
cp "out/$ZIP" "out/repo/$ZIP"
if command -v md5 >/dev/null; then SUM=$(md5 -q "out/repo/$ZIP"); else SUM=$(md5sum "out/repo/$ZIP" | cut -d' ' -f1); fi

cat > out/repo/manifest.json <<JSON
[
  {
    "guid": "4f1d2c8e-9b7a-4e35-8c61-f1ae0f17f1f1",
    "name": "FireFly",
    "description": "萤火虫：发现资源，一键交给迅雷下载，下载完成自动入库。",
    "overview": "发现 → 迅雷下载 → 自动入库",
    "owner": "FireFly",
    "category": "General",
    "versions": [
      {
        "version": "$VERSION",
        "changelog": "",
        "targetAbi": "10.11.11.0",
        "sourceUrl": "$BASE_URL/$ZIP",
        "checksum": "$SUM",
        "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      }
    ]
  }
]
JSON
echo "插件库已生成：out/repo（manifest 指向 $BASE_URL/$ZIP）"
