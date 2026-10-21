# 萤火虫 FireFly（Jellyfin 插件，MVP）

在 Jellyfin 里发现资源 → 一键交给迅雷下载 → 下载完成后自动按规范命名入库。

- 首发平台：**macOS + Jellyfin 10.11（Jellyfin.app）+ 迅雷 Mac 版**
- 设计依据：项目文件《萤火虫FireFly初步设计.md》《萤火虫FireFly方案-v5.md》

## 目录结构

```
src/FireFly.Core/           业务核心（不引用 Jellyfin）：搜索源、标题解析、迅雷驱动、下载监控、归档
src/FireFly.Jellyfin/       Jellyfin 插件：网页注入、/FireFly/* 接口、后台监控、配置页、前端
tests/FireFly.Core.Tests/   Core 单元测试
build.sh                    构建并打包 → out/FireFly_<版本>.zip
make-repo.sh                生成 Jellyfin 插件库（manifest.json + zip）
install-local.sh            不经插件库，直接装进本机 Jellyfin（开发自测用）
```

---

## macOS 安装部署

### 1. 准备环境

| 组件 | 要求 | 安装方式 |
|---|---|---|
| macOS | Intel 或 Apple 芯片均可 | — |
| Jellyfin | 10.11.x，以 **Jellyfin.app** 形式安装（不支持 Docker 版） | https://jellyfin.org/downloads/macos |
| 迅雷 Mac 版 | 最新版，已登录 | 迅雷官网 |
| .NET 9 SDK | 仅构建时需要，用户安装插件时不需要 | `brew install dotnet@9` |
| Python 3 | 仅"本机插件库"方式需要，用来临时起一个 HTTP 服务。macOS 自带的 `/usr/bin/python3` 即可 | `xcode-select --install` |

Homebrew 安装的 `dotnet@9` 不会自动加入 PATH，需要在 `~/.zshrc` 里加上下面两行，然后重新打开终端：

```bash
export DOTNET_ROOT="$(brew --prefix dotnet@9)/libexec"
export PATH="$(brew --prefix dotnet@9)/bin:$PATH"
```

检查：`dotnet --list-sdks` 输出里应该有 `9.0.x`。

### 2. 构建与测试

```bash
cd ~/Workspace/FireFly
dotnet test tests/FireFly.Core.Tests -f net9.0     # 单元测试，应全部通过
./build.sh                                         # 生成 out/FireFly_<版本>.zip
```

### 3. 安装插件（任选一种）

**方式 A：通过插件库安装（推荐，与正式发布的流程一致）**

```bash
./make-repo.sh http://127.0.0.1:8765               # 生成 out/repo/manifest.json 和插件包
cd out/repo && python3 -m http.server 8765         # 保持这个终端开着
```

1. 打开 Jellyfin 网页 → 控制台 → 插件 → **存储库** → 点"+"添加：
   - 名称：`FireFly（本机）`
   - 地址：`http://127.0.0.1:8765/manifest.json`
2. 回到插件 → **目录**，找到 **FireFly** → 安装。
3. 按提示重启 Jellyfin：在菜单栏的 Jellyfin 图标里退出，再重新打开 Jellyfin.app。
4. 装好之后可以关掉 `http.server` 那个终端。以后升级时再打开即可。

**方式 B：直接复制进插件目录（开发时改完代码快速自测）**

```bash
./install-local.sh
```

这个脚本会：构建 → 退出 Jellyfin → 删除旧的 `FireFly_*` 目录 → 把 DLL 复制到 `~/Library/Application Support/jellyfin/plugins/FireFly_<版本>/` → 重新打开 Jellyfin。

> 必须先退出 Jellyfin 再覆盖 DLL，否则运行中的进程可能读到半写的文件，报 `BadImageFormatException`。

手工安装后，FireFly 会由 Jellyfin 自身提供插件仓库清单（`http://127.0.0.1:8096/FireFly/manifest.json`）和安装包（`/FireFly/package.zip`）。把该清单地址加到 **控制台 → 插件 → 存储库**（名称可填 `FireFly（本机）`）后，插件详情页才能从仓库读到 FireFly 的元数据；否则页面会显示“从存储库获取插件详情时发生错误”。

### 4. 设置迅雷（M1）

1. FireFly 提交前会自动把迅雷的 `specifySavePath` / `LastSavePath` 改成插件配置里的**迅雷下载目录**，并确保该目录存在；为保险起见，仍建议在迅雷 → 设置 → **下载目录**里设成同一个目录。
2. 关闭"新建任务时弹出确认 / 选择下载位置"之类的选项（以你的迅雷版本里的实际名称为准）。
3. 确认迅雷是磁力链接的默认打开方式：在终端执行 `open "magnet:?xt=urn:btih:0000000000000000000000000000000000000000"`，看是否由迅雷接管（这个测试任务可以直接删除）。

### 5. 配置插件

控制台 → 插件 → **FireFly**：

| 配置项 | 说明 |
|---|---|
| 迅雷下载目录 | 留空为 `~/Downloads/FireFly`。提交前会写入迅雷并用于扫描进度；迅雷里也建议设成同一目录 |
| 电影库目录 / 剧集库目录 | 留空时自动使用 Jellyfin 第一个"电影"/"节目"媒体库。最好与下载目录在同一块磁盘，入库时就是瞬间完成的移动；不在同一块磁盘时会变成复制 |
| TMDb API Key | 用于热门榜单和资源详情页，在 themoviedb.org 账号设置 → API 里免费申请 |
| TMDb 接口地址 / 图片地址 | 下拉选择，默认 `api.tmdb.org` 和 `image.tmdb.org`（中国大陆可直连）；插件直连 TMDb，不经过中转。默认地址打不开时换一个候选，或选"自定义"。候选列表在规则文件的 `tmdb.apiBases` / `tmdb.imageBases` 里 |
| 详情页显示 YouTube 预告片 | 默认关闭；大陆网络不翻墙时 YouTube 打不开 |
| 发布者关键词 | 留空使用内置默认（高清剧集网、高清影视之家：前者发剧集、后者发电影；DDHDTV 等各分站的发布名都带这两个词），一行一个词 |

页面顶部的状态区会显示：版本、下载目录是否存在、媒体库路径、是否在同一块磁盘、规则版本和可用搜索源、数据目录；填了 TMDb Key 后还会自检接口和图片域名是否可达，并在浏览器里试加载一张海报。全部正常后再开始使用。

### 6. 使用

1. 浏览器按 `Cmd+Shift+R` 强制刷新 Jellyfin 网页（首次安装后需要刷新一次，才能加载注入的脚本）。
2. 打开任意电影 / 剧集详情页 → 点云朵图标 **获取资源** → 剧集先选季 → 在版本列表里点 **下载**。
3. 或点顶栏的火焰图标打开 **热门榜单**：按分类、类型、地区、时间、出品、推荐、分级筛选 → 点海报进入 **资源详情页**（简介、预告片、导演 & 演员、播放平台、出品方）→ 点 **收藏** 或 **下载**（进入同样的版本列表）。右上角搜索框可以按片名直接搜电影和剧集；顶栏"我的收藏"查看收藏。设计说明见 `docs/热门榜单与资源详情页设计.md`。
4. 点面板右上角的 **下载任务** 查看进度。下载完成后状态会变为"已入库"，媒体库会自动刷新，并清理下载目录中本次发布的残留目录；下载根本身不会删除。

> 入口只对 Jellyfin 管理员账号显示。

### 7. 升级

1. 修改 `Directory.Build.props` 里的 `<Version>`（例如 `0.1.0.1`）；
2. 方式 A：重新执行 `./make-repo.sh http://127.0.0.1:8765` 并启动 `http.server`，Jellyfin 的插件页面会提示有更新；方式 B：重新执行 `./install-local.sh`。

### 8. 卸载

控制台 → 插件 → FireFly → 卸载 → 重启 Jellyfin。下载任务和收藏保存在配置页显示的"数据目录"里，需要彻底清除时手动删除该目录即可。

### 9. 常见问题

| 现象 | 排查 |
|---|---|
| 详情页没有"获取资源"按钮 | 按 `Cmd+Shift+R` 强制刷新；确认当前账号是管理员；在 `~/Library/Application Support/jellyfin/log/` 最新的日志里搜索 `FireFly`，确认插件已加载 |
| 点"下载"后报"无法把磁力交给迅雷" | 迅雷没有安装，或者不是磁力链接的默认打开方式，见第 4 步第 3 项 |
| 任务一直是"等待迅雷开始"，10 分钟后变成"停滞" | 迅雷可能弹出了确认窗口；先确认文件是否落在配置的迅雷下载目录。若迅雷拒绝了自动保存路径，仍按 **M1** 排查 |
| 迅雷在下载，但进度一直是 0 或者下完了不入库 | 迅雷的临时文件命名与规则不符（**M2 不通过**），见下一节 |
| 状态显示"归档失败" | 媒体库目录不存在或没有写入权限；在配置页检查电影库 / 剧集库路径 |
| 热门榜单提示填写 TMDb Key | 在配置页填写 TMDb API Key 并保存 |
| 热门榜单报错或海报不显示 | 看配置页的 TMDb 自检：接口不可用时换"TMDb 接口地址"；图片加载失败时换"TMDb 图片地址" |
| 用中文片名搜不到资源，用原名能搜到 | 资源站常用和 TMDb 不同的译名。搜索会依次用 TMDb 主标题、原名、中港台别名加发布者关键词去搜，命中一个就停；仍然搜不到时点抽屉右上角刷新重试 |
| 预告片黑屏或加载不出来 | 预告片来自 YouTube，网络访问不了 YouTube 时播放不了；在配置页关掉"详情页显示 YouTube 预告片" |
| 某个"出品"选项筛出来的结果不对 | 制片公司 / 平台的 TMDb ID 写在规则文件 `discover.companies` / `discover.networks` 里，在数据目录的 `rules.json` 里只写要改的部分即可，格式见 `docs/规则文件格式.md` |

---

## 三项假设（M1 / M2 / M3）的验证与回退

| 项 | 怎么判断通过 | 不通过时改哪里 |
|---|---|---|
| M1 自动使用插件配置的下载目录 | 点"下载"后，文件出现在配置的迅雷下载目录 | 已实现：`MacThunderDownloader.SubmitAsync` 在 `open magnet` 前执行 `defaults write com.xunlei.Thunder specifySavePath/LastSavePath`，并创建目标目录 |
| M2 只看文件系统就能判断进度和完成 | 任务列表里的进度在增长，下载完成后状态自动变为"已入库" | 先在数据目录的 `rules.json` 里把 `thunder.tempSuffixes` 改成实测的临时后缀；仍然不行，就在 `MacThunderDownloader.Probe` 里改为只读查询 `etm_map.db` |
| M3 插件库不放 GitHub | 执行 `./make-repo.sh https://你的地址`，把 `out/repo` 里的文件上传到该地址，在 Jellyfin 里添加存储库并安装成功 | 先改用 GitHub Releases |

M2 实测方法：下载过程中执行 `ls -laR ~/Downloads/FireFly`，记录迅雷临时文件的命名方式，以及下载完成那一刻文件名发生了什么变化。

---

## 运行数据

插件配置页的"数据目录"一栏显示实际路径，其中：

- `tasks.json`：下载任务；
- `favorites.json`：收藏；
- `rules.json`（可选）：叠加在内置规则上，只写要改的部分；格式和加载顺序见 `docs/规则文件格式.md`。
