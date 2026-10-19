# 萤火虫 FireFly（Jellyfin 插件，MVP）

在 Jellyfin 里发现资源 → 一键交给迅雷下载 → 下载完成后自动按规范命名入库。首发平台：macOS + Jellyfin 10.11（Jellyfin.app）。

设计依据：项目文件《萤火虫FireFly初步设计.md》《萤火虫FireFly方案-v5.md》。

## 目录

```
src/FireFly.Core/        业务核心（不引用 Jellyfin）：搜索源、标题解析、迅雷驱动、下载监控、归档、授权与额度
src/FireFly.Jellyfin/    Jellyfin 插件：网页注入、/FireFly/* 接口、后台监控、配置页、前端
tools/FireFly.LicenseTool/  生成密钥对、签发授权码（只在作者电脑上用）
tests/FireFly.Core.Tests/   Core 单元测试
```

## 构建与本机安装

需要 .NET 9 SDK：`brew install dotnet@9`。

```bash
dotnet test tests/FireFly.Core.Tests -f net9.0        # 单元测试
./make-repo.sh http://127.0.0.1:8765                  # 构建并生成本机插件库
cd out/repo && python3 -m http.server 8765            # 临时开一个本机插件库
```

然后在 Jellyfin：控制台 → 插件 → 存储库 → 添加 `http://127.0.0.1:8765/manifest.json` → 目录里找到 **FireFly** → 安装 → 重启 Jellyfin。

## 首次配置

1. **迅雷**：设置 → 下载目录设为 `~/Downloads/FireFly`（或你在插件里填的目录），关闭"新建任务时弹出确认"之类的选项；确认迅雷是磁力链接的默认打开方式。
2. **插件配置页**（控制台 → 插件 → FireFly）：顶部状态会显示下载目录、电影库、剧集库是否就绪，以及是否在同一块磁盘。按需填写 TMDb API Key。
3. 打开任意电影或剧集详情页，点云朵图标 **获取资源**；或点顶栏火焰图标 **发现** 看本周热门。

## 三项假设（M1 / M2 / M3）的验证方法

| 项 | 怎么看是否通过 | 不通过时改哪里 |
|---|---|---|
| M1 不改迅雷配置也能自动开始下载 | 点"下载"后，迅雷不弹窗就自动开始，文件出现在下载目录 | `MacThunderDownloader.SubmitAsync`：提交前加 `defaults write com.xunlei.Thunder specifySavePath/LastSavePath` |
| M2 只看文件系统就能判断进度和完成 | 任务列表里的进度会增长，下载完成后状态自动变为"已入库" | `rules.default.json` 的 `thunder.tempSuffixes` 改成实测的临时后缀；仍不行就在 `MacThunderDownloader.Probe` 里只读查询 `etm_map.db` |
| M3 插件库不放 GitHub | 用 `./make-repo.sh https://你的地址` 生成文件并上传，在 Jellyfin 里添加存储库并安装成功 | 先改用 GitHub Releases |

M2 实测时可以在下载过程中执行 `ls -laR ~/Downloads/FireFly`，记录迅雷临时文件的命名方式。

## 授权码

```bash
cd tools/FireFly.LicenseTool
dotnet run -f net9.0 -- keygen                       # 生成 keys/private.key（保密，已在 .gitignore 中）并打印公钥
dotnet run -f net9.0 -- issue 买家标识 yearly         # 签发授权码：monthly / yearly / lifetime
```

把公钥填进 `src/FireFly.Jellyfin/FireFlyRuntime.cs` 的 `LicensePublicKey` 后再发版。公钥为空时，插件只提供每月免费额度（`FreeMonthlyQuota`，当前为 5 部）。

## 运行数据

插件数据目录（`~/Library/Application Support/jellyfin/plugins/configurations/FireFly/` 一带，以 Jellyfin 实际路径为准）里有：

- `tasks.json`：下载任务；
- `quota.json`：月度额度；
- `rules.json`（可选）：覆盖内置规则，版本号不低于内置时生效。
