# 萤火虫 FireFly

**发现好片，一键下载入库。**

萤火虫是一款 Jellyfin 插件，把影视发现、资源检索、下载和片库归档整合进你熟悉的 Jellyfin 界面。不用另外部署服务，装好插件，从发现影片到入库观看都在 Jellyfin 里完成。

[官网](https://firefly.raystudio.club/) · [下载安装](https://firefly.raystudio.club/install.html) · [最新版本](https://github.com/leigangzhang/Firefly/releases/latest)

> 当前版本 0.3 · 支持 macOS 上的 Jellyfin 10.11（Jellyfin.app）+ 迅雷 Mac 版 · 开源免费

## 一分钟了解萤火虫

[![萤火虫产品演示](docs/images/demo-poster.jpg)](https://firefly.raystudio.club/#demo)

点击图片到官网观看演示视频。

## 功能

- **热门榜单**：电影、剧集、动漫、纪录片、综艺五类榜单，可按类型、地区、年代、出品方筛选；顶栏、榜单、媒体详情页三处都能搜片名，中文名或原名都能搜到。
- **资源详情页**：简介、主创、分季分集、评分来自 TMDb；可以收藏，可以播放 YouTube 预告片（网络受限时可关闭）。
- **按你的规则找资源**：不内置任何资源站。站点由你自己添加，粘贴一次站内搜索的网址就能自动识别；可以按电影、剧集、动漫分配站点，用优选关键词决定版本的排序，搜索计划会提前显示每次搜索要发多少请求。
- **一键下载**：整季、多集、单集任选，画质、字幕、体积标注清楚，点"下载"就交给迅雷。
- **自动归档**：下载完成后按 Jellyfin 规范改名、连同字幕移入片库，清理下载目录，再通知 Jellyfin 刷新，海报和简介由 Jellyfin 自己刮削。
- **适配国内网络**：中文界面，插件直连 TMDb、不需要代理，元数据和图片地址可切换，海报缓存在本机。

## 快速上手

完整步骤和常见问题见官网的[下载安装](https://firefly.raystudio.club/install.html)页，这里是简化版。

**1. 准备环境**

| 组件 | 要求 |
|---|---|
| 系统 | macOS，Intel 或 Apple 芯片 |
| Jellyfin | 10.11.x，以 Jellyfin.app 形式安装（暂不支持 Docker 版） |
| 下载器 | 迅雷 Mac 版，已登录 |
| 元数据 | TMDb API Key，在 [themoviedb.org](https://www.themoviedb.org/settings/api) 免费申请 |

**2. 安装插件**

在 Jellyfin 网页里打开 **控制台 → 插件 → 存储库**，点"+"添加下面这个地址，然后在 **插件 → 目录** 里找到 FireFly 安装，最后退出并重新打开 Jellyfin.app。

```
https://firefly.raystudio.club/repo/manifest.json
```

也可以下载 [Releases](https://github.com/leigangzhang/Firefly/releases/latest) 里的 zip，退出 Jellyfin 后解压到 `~/Library/Application Support/jellyfin/plugins/FireFly_<版本号>/`，再重新打开 Jellyfin.app。

**3. 配置**

打开 **控制台 → 插件 → FireFly**：

1. 填好迅雷下载目录（默认 `~/Downloads/FireFly`），并在迅雷的设置里把下载目录改成同一个，关掉"新建任务时弹出确认"。
2. 填 TMDb API Key。
3. 点 **＋ 添加站点**，粘贴资源站的网址或站内搜索结果页的网址。萤火虫不内置任何站点，至少要添加一个才能搜到资源。开了人机验证或接口需要签名的站点不支持。
4. 按需添加优选关键词，比如 `2160p`、`中字`。

**4. 使用**

在浏览器里按 `⌘⇧R` 强制刷新 Jellyfin 网页。点顶栏的火焰图标打开热门榜单，或在媒体详情页点 **获取资源**，选好版本点 **下载**。进度在 **下载任务** 里查看，完成后状态变为"已入库"。萤火虫的入口只对 Jellyfin 管理员账号显示。

## 项目结构

```
src/FireFly.Core/          业务核心，不依赖 Jellyfin：资源搜索、标题解析、迅雷驱动、下载监控、归档、TMDb
src/FireFly.Jellyfin/      Jellyfin 插件：配置页、网页注入的前端、/FireFly/* 接口、后台任务监控
tests/FireFly.Core.Tests/  单元测试
docs/                      设计文档、规则文件格式、开发说明
scripts/                   构建打包（build.sh）、生成插件存储库（make-repo.sh）、开发时装进本机 Jellyfin（install-local.sh）
```

从源码构建、本机自测和开发期排查见 [开发说明](docs/开发说明.md)；站点和关键词的规则格式见 [规则文件格式](docs/规则文件格式.md)。

## 版权声明

萤火虫不提供任何影视资源：

- 不内置任何资源站点和关键词，只连接你自己配置的站点和下载器；
- 不存储、不分发任何影视内容；
- 源代码完全公开，可以随时审阅。

请只下载你有权使用的内容，尊重版权。萤火虫是独立的开源项目，与 Jellyfin、TMDb、迅雷及文中提到的其他项目没有隶属关系。

## 开源协议

[MIT License](LICENSE)
