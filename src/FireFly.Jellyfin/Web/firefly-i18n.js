/* 萤火虫 FireFly 界面文案：跟随配置页的 TMDb 语言，避免 TMDb 内容和插件界面中英混杂。
   词条的键就是简体中文原文（简体中文不查表），"|"后是同一原文不同用法的说明；{n} 之类是占位符。查不到的词条原样显示。 */
(function () {
  'use strict';

  var DICT = {
    'zh-TW': {
      '剧集|单部': '劇集', '电影|单部': '電影',
      '全{n}集': '全{n}集', '第{a}-{b}集': '第{a}-{b}集', '第{n}集(短)': '第{n}集', '杜比视界': '杜比視界', '杜比视界+HDR': '杜比視界+HDR', '枪版': '槍版', '中英双字': '中英雙字', '中英字幕': '中英字幕', '简繁英字幕': '簡繁英字幕', '简繁字幕': '簡繁字幕', '中文字幕': '中文字幕', '中字': '中字', '国语配音': '國語配音', '粤语配音': '粵語配音',
      '请求失败 {n}': '請求失敗 {n}', '重新搜索': '重新搜尋', '关闭（Esc）': '關閉（Esc）', '关闭': '關閉', '资源版本': '資源版本', '下载任务': '下載任務',
      '获取资源': '取得資源', '第 {n} 季': '第 {n} 季', '正在重新搜索…': '正在重新搜尋…', '正在搜索资源…': '正在搜尋資源…',
      '补充搜索失败，可点右上角刷新重试': '補充搜尋失敗，可點右上角重新整理再試', '没有找到优选资源，正在扩大搜索范围…': '沒有找到優選資源，正在擴大搜尋範圍…',
      '正在补充更多资源…': '正在補充更多資源…', '网络连接中断，正在重试…': '網路連線中斷，正在重試…', '网络连接中断，请检查 Jellyfin 服务后重试': '網路連線中斷，請檢查 Jellyfin 服務後重試',
      '优选': '優選', '部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新': '部分資源來源暫時無法使用，結果可能不完整，稍後可點右上角重新整理',
      '没有找到合适的资源': '沒有找到合適的資源', '可以稍后刷新再试，或换一季': '可以稍後重新整理再試，或換一季', '共 {n} 个版本': '共 {n} 個版本',
      '（缓存结果，可点右上角刷新）': '（快取結果，可點右上角重新整理）', '{n} 个版本': '{n} 個版本', '整季': '整季', '多集': '多集', '单集': '單集',
      '集数未知': '集數未知', '第 {n} 集': '第 {n} 集', '下载': '下載', '提交中…': '提交中…', '已交给迅雷': '已交給迅雷', '查看下载任务': '查看下載任務',
      '{n} 个文件': '{n} 個檔案', '等待迅雷开始': '等待迅雷開始', '下载中': '下載中', '停滞': '停滯', '入库中': '入庫中', '已入库': '已入庫', '失败': '失敗',
      '还没有下载任务': '還沒有下載任務', '在详情页点"下载"选一个版本，就会出现在这里': '在詳情頁點「下載」選一個版本，就會出現在這裡',
      '移除任务（不删除已下载的文件）': '移除任務（不刪除已下載的檔案）', '移除': '移除',
      '← 返回榜单': '← 返回榜單', '← 返回收藏': '← 返回收藏', '刷新': '重新整理', '连不上 Jellyfin，请稍后点刷新重试': '連不上 Jellyfin，請稍後點重新整理再試', 'Jellyfin 正在重启或暂时不可用，请稍后点刷新重试': 'Jellyfin 正在重新啟動或暫時無法使用，請稍後點重新整理再試', '全局搜索电影、剧集': '搜尋全部電影、劇集', '清除': '清除', '清除搜索': '清除搜尋', '萤火虫 · 热门榜单': '螢火蟲 · 熱門榜單',
      '我的收藏': '我的收藏', '正在搜索“{q}”…': '正在搜尋「{q}」…', '加载中…': '載入中…', '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key': '請先在 控制台 → 外掛程式 → FireFly 填寫 TMDb API Key',
      '“{q}”的搜索结果': '「{q}」的搜尋結果', '全部电影和剧集': '全部電影和劇集', '约 {n} 条': '約 {n} 筆', '不受筛选条件影响': '不受篩選條件影響', '返回榜单': '返回榜單',
      '没有找到“{q}”，换个片名或用原名试试': '沒有找到「{q}」，換個片名或用原名試試', '加载更多': '載入更多', '已经到底了': '已經到底了', '加载筛选项…': '載入篩選項…', '全部': '全部',
      '没有符合条件的影片，换个筛选试试': '沒有符合條件的影片，換個篩選試試', '剧集': '劇集', '电影': '電影', '已在库': '已在庫', '还没有收藏。在详情页点"收藏"即可加入这里。': '還沒有收藏。在詳情頁點「收藏」即可加入這裡。',
      '分类': '分類', '类型': '類型', '地区': '地區', '时间': '時間', '出品': '出品', '推荐': '推薦', '分级': '分級', '动漫': '動漫', '纪录片': '紀錄片', '综艺': '綜藝',
      '最热': '最熱', '最新': '最新', '高分': '高分', '最多评价': '最多評價', '更早': '更早',
      '华纳兄弟': '華納兄弟', '环球影业': '環球影業', '派拉蒙': '派拉蒙', '迪士尼': '迪士尼', '哥伦比亚': '哥倫比亞', '漫威影业': '漫威影業', '皮克斯': '皮克斯', '梦工厂动画': '夢工廠動畫',
      '狮门影业': '獅門影業', '吉卜力工作室': '吉卜力工作室', '东宝': '東寶', '腾讯视频': '騰訊視頻', '爱奇艺': '愛奇藝', '优酷': '優酷', '芒果TV': '芒果TV', '哔哩哔哩': '嗶哩嗶哩', '湖南卫视': '湖南衛視',
      'G 全年龄': 'G 普遍級', 'PG 家长指导': 'PG 家長指導', 'PG-13 13岁以上': 'PG-13 13歲以上', 'R 限制级': 'R 限制級', 'NC-17 17岁以下禁止': 'NC-17 17歲以下禁止',
      '加载详情…': '載入詳情…', '♥ 已收藏': '♥ 已收藏', '♡ 收藏': '♡ 收藏', '⬇ 下载': '⬇ 下載', '▶ 播放预告片': '▶ 播放預告片', '用户评分': '使用者評分', '用户评分（{n}人）': '使用者評分（{n}人）',
      '剧情介绍': '劇情簡介', '暂无中文简介。': '暫無簡介。', '预告片': '預告片', '播放不了？在 YouTube 打开': '無法播放？在 YouTube 開啟', 'TMDb 暂无预告片': 'TMDb 暫無預告片',
      '{n} 集': '{n} 集', '导演 & 演员': '導演 & 演員', '暂无演职员信息': '暫無演職員資訊', '导演': '導演', '编剧': '編劇', '主创': '主創',
      '查看全部季（共 {n} 季）': '查看全部季（共 {n} 季）', '收起其他季': '收起其他季', '最新一季': '最新一季', '当前季': '目前季', '首播 {date}': '首播 {date}', '即将开播': '即將開播', '共 {n} 集': '共 {n} 集',
      'FireFly 热门榜单': 'FireFly 熱門榜單',
      'TMDb 没有这个条目': 'TMDb 沒有這個條目', '未配置 TMDb API Key': '未設定 TMDb API Key', '无法识别要搜索的影片': '無法辨識要搜尋的影片',
      '这个版本的信息已过期，请点刷新重新搜索后再下载': '這個版本的資訊已過期，請重新整理後再下載', 'TMDb 请求失败：{x}': 'TMDb 請求失敗：{x}',
      '已交给迅雷，等待开始下载': '已交給迅雷，等待開始下載', '正在移入媒体库': '正在移入媒體庫',
      '迅雷还没有开始下载：请确认迅雷已接收任务、下载目录与 FireFly 设置一致；也可以换一个版本': '迅雷還沒有開始下載：請確認迅雷已接收任務、下載目錄與 FireFly 設定一致；也可以換一個版本',
      '下载长时间没有进展（可能缺少做种或被迅雷拦截），可以继续等待或换一个版本': '下載長時間沒有進展（可能缺少做種或被迅雷攔截），可以繼續等待或換一個版本',
      '读取下载目录失败：{x}': '讀取下載目錄失敗：{x}', '归档失败：{x}': '歸檔失敗：{x}', '已入库 {n} 个文件': '已入庫 {n} 個檔案', '，已清理 {n} 个下载目录': '，已清理 {n} 個下載目錄',
      '无法把磁力交给迅雷（open 退出码 {n}），请确认已安装迅雷并设为磁力链接的默认打开方式': '無法把磁力交給迅雷（open 結束代碼 {n}），請確認已安裝迅雷並設為磁力連結的預設開啟方式',
      '未配置迅雷下载目录': '未設定迅雷下載目錄', '无法创建迅雷下载目录：{x}': '無法建立迅雷下載目錄：{x}',
    },
    en: {
      '剧集|单部': 'TV Series', '电影|单部': 'Movie',
      '全{n}集': 'All {n} episodes', '第{a}-{b}集': 'E{a}-E{b}', '第{n}集(短)': 'E{n}', '杜比视界': 'Dolby Vision', '杜比视界+HDR': 'Dolby Vision+HDR', '枪版': 'CAM', '中英双字': 'Chinese & English subs', '中英字幕': 'Chinese & English subs', '简繁英字幕': 'Chinese (S/T) & English subs', '简繁字幕': 'Chinese (S/T) subs', '中文字幕': 'Chinese subs', '中字': 'Chinese subs', '国语配音': 'Mandarin dub', '粤语配音': 'Cantonese dub',
      '请求失败 {n}': 'Request failed {n}', '重新搜索': 'Search again', '关闭（Esc）': 'Close (Esc)', '关闭': 'Close', '资源版本': 'Releases', '下载任务': 'Downloads',
      '获取资源': 'Get releases', '第 {n} 季': 'Season {n}', '正在重新搜索…': 'Searching again…', '正在搜索资源…': 'Searching for releases…',
      '补充搜索失败，可点右上角刷新重试': 'Extended search failed. Use refresh at the top right to retry.', '没有找到优选资源，正在扩大搜索范围…': 'No preferred releases found. Widening the search…',
      '正在补充更多资源…': 'Looking for more releases…', '网络连接中断，正在重试…': 'Connection lost. Retrying…', '网络连接中断，请检查 Jellyfin 服务后重试': 'Connection lost. Check the Jellyfin server and try again.',
      '优选': 'Preferred', '部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新': 'Some sources are unavailable, so results may be incomplete. Refresh later from the top right.',
      '没有找到合适的资源': 'No suitable releases found', '可以稍后刷新再试，或换一季': 'Refresh later or try another season', '共 {n} 个版本': '{n} releases',
      '（缓存结果，可点右上角刷新）': ' (cached, refresh from the top right)', '{n} 个版本': '{n} releases', '整季': 'Full season', '多集': 'Multiple episodes', '单集': 'Single episode',
      '集数未知': 'Unknown episode', '第 {n} 集': 'Episode {n}', '下载': 'Download', '提交中…': 'Sending…', '已交给迅雷': 'Sent to Thunder', '查看下载任务': 'View downloads',
      '{n} 个文件': '{n} files', '等待迅雷开始': 'Waiting for Thunder', '下载中': 'Downloading', '停滞': 'Stalled', '入库中': 'Importing', '已入库': 'In library', '失败': 'Failed',
      '还没有下载任务': 'No downloads yet', '在详情页点"下载"选一个版本，就会出现在这里': 'Pick a release with "Download" on a detail page and it will show up here',
      '移除任务（不删除已下载的文件）': 'Remove task (downloaded files are kept)', '移除': 'Remove',
      '← 返回榜单': '← Back', '← 返回收藏': '← Back to favorites', '刷新': 'Refresh', '连不上 Jellyfin，请稍后点刷新重试': 'Cannot reach Jellyfin. Try Refresh in a moment.', 'Jellyfin 正在重启或暂时不可用，请稍后点刷新重试': 'Jellyfin is restarting or unavailable. Try Refresh in a moment.', '全局搜索电影、剧集': 'Search all movies and TV', '清除': 'Clear', '清除搜索': 'Clear search', '萤火虫 · 热门榜单': 'FireFly · Popular',
      '我的收藏': 'Favorites', '正在搜索“{q}”…': 'Searching for "{q}"…', '加载中…': 'Loading…', '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key': 'Enter a TMDb API Key in Dashboard → Plugins → FireFly first',
      '“{q}”的搜索结果': 'Results for "{q}"', '全部电影和剧集': 'All movies and TV', '约 {n} 条': 'about {n}', '不受筛选条件影响': 'filters not applied', '返回榜单': 'Back to list',
      '没有找到“{q}”，换个片名或用原名试试': 'Nothing found for "{q}". Try another or the original title.', '加载更多': 'Load more', '已经到底了': 'That\'s everything', '加载筛选项…': 'Loading filters…', '全部': 'All',
      '没有符合条件的影片，换个筛选试试': 'Nothing matches these filters', '剧集': 'TV', '电影': 'Movies', '已在库': 'In library', '还没有收藏。在详情页点"收藏"即可加入这里。': 'No favorites yet. Use "Favorite" on a detail page to add one.',
      '分类': 'Category', '类型': 'Genre', '地区': 'Region', '时间': 'Year', '出品': 'Studio', '推荐': 'Sort', '分级': 'Rating', '动漫': 'Anime', '纪录片': 'Documentary', '综艺': 'Variety',
      '最热': 'Popular', '最新': 'Newest', '高分': 'Top rated', '最多评价': 'Most voted', '更早': 'Earlier',
      '华纳兄弟': 'Warner Bros.', '环球影业': 'Universal', '派拉蒙': 'Paramount', '迪士尼': 'Disney', '哥伦比亚': 'Columbia', '漫威影业': 'Marvel Studios', '皮克斯': 'Pixar', '梦工厂动画': 'DreamWorks Animation',
      '狮门影业': 'Lionsgate', '吉卜力工作室': 'Studio Ghibli', '东宝': 'Toho', '腾讯视频': 'Tencent Video', '爱奇艺': 'iQIYI', '优酷': 'Youku', '芒果TV': 'Mango TV', '哔哩哔哩': 'Bilibili', '湖南卫视': 'Hunan TV',
      'G 全年龄': 'G', 'PG 家长指导': 'PG', 'PG-13 13岁以上': 'PG-13', 'R 限制级': 'R', 'NC-17 17岁以下禁止': 'NC-17',
      '加载详情…': 'Loading details…', '♥ 已收藏': '♥ Favorited', '♡ 收藏': '♡ Favorite', '⬇ 下载': '⬇ Download', '▶ 播放预告片': '▶ Play trailer', '用户评分': 'User score', '用户评分（{n}人）': 'User score ({n} votes)',
      '剧情介绍': 'Overview', '暂无中文简介。': 'No overview available.', '预告片': 'Trailers', '播放不了？在 YouTube 打开': 'Won\'t play? Open on YouTube', 'TMDb 暂无预告片': 'No trailers on TMDb',
      '{n} 集': '{n} episodes', '导演 & 演员': 'Cast & Crew', '暂无演职员信息': 'No cast or crew information', '导演': 'Director', '编剧': 'Writer', '主创': 'Creator',
      '查看全部季（共 {n} 季）': 'View all seasons ({n})', '收起其他季': 'Hide other seasons', '最新一季': 'Latest season', '当前季': 'Current season', '首播 {date}': 'Premiered {date}', '即将开播': 'upcoming', '共 {n} 集': '{n} episodes',
      'FireFly 热门榜单': 'FireFly popular',
      'TMDb 没有这个条目': 'Not found on TMDb', '未配置 TMDb API Key': 'TMDb API Key is not set', '无法识别要搜索的影片': 'Could not tell which title to search for',
      '这个版本的信息已过期，请点刷新重新搜索后再下载': 'This release has expired. Refresh and search again before downloading.', 'TMDb 请求失败：{x}': 'TMDb request failed: {x}',
      '已交给迅雷，等待开始下载': 'Sent to Thunder, waiting to start', '正在移入媒体库': 'Moving into the library',
      '迅雷还没有开始下载：请确认迅雷已接收任务、下载目录与 FireFly 设置一致；也可以换一个版本': 'Thunder has not started: check that it accepted the task and that its download folder matches FireFly, or try another release',
      '下载长时间没有进展（可能缺少做种或被迅雷拦截），可以继续等待或换一个版本': 'No progress for a long time (few seeders or blocked by Thunder). Keep waiting or try another release.',
      '读取下载目录失败：{x}': 'Could not read the download folder: {x}', '归档失败：{x}': 'Import failed: {x}', '已入库 {n} 个文件': 'Imported {n} files', '，已清理 {n} 个下载目录': ', cleaned {n} download folders',
      '无法把磁力交给迅雷（open 退出码 {n}），请确认已安装迅雷并设为磁力链接的默认打开方式': 'Could not send the magnet to Thunder (open exit code {n}). Make sure Thunder is installed and is the default app for magnet links.',
      '未配置迅雷下载目录': 'Thunder download folder is not set', '无法创建迅雷下载目录：{x}': 'Could not create the Thunder download folder: {x}',
    },
    ja: {
      '剧集|单部': 'ドラマ', '电影|单部': '映画',
      '全{n}集': '全{n}話', '第{a}-{b}集': '第{a}-{b}話', '第{n}集(短)': '第{n}話', '杜比视界': 'ドルビービジョン', '杜比视界+HDR': 'ドルビービジョン+HDR', '枪版': 'CAM 版', '中英双字': '中英字幕', '中英字幕': '中英字幕', '简繁英字幕': '中（簡・繁）英字幕', '简繁字幕': '中国語（簡・繁）字幕', '中文字幕': '中国語字幕', '中字': '中国語字幕', '国语配音': '北京語吹替', '粤语配音': '広東語吹替',
      '请求失败 {n}': 'リクエスト失敗 {n}', '重新搜索': '再検索', '关闭（Esc）': '閉じる（Esc）', '关闭': '閉じる', '资源版本': 'リリース', '下载任务': 'ダウンロード',
      '获取资源': 'リリースを探す', '第 {n} 季': 'シーズン {n}', '正在重新搜索…': '再検索中…', '正在搜索资源…': 'リリースを検索中…',
      '补充搜索失败，可点右上角刷新重试': '追加検索に失敗しました。右上の更新で再試行できます', '没有找到优选资源，正在扩大搜索范围…': '優先リリースが見つかりません。検索範囲を広げています…',
      '正在补充更多资源…': 'さらにリリースを検索中…', '网络连接中断，正在重试…': '接続が切れました。再試行中…', '网络连接中断，请检查 Jellyfin 服务后重试': '接続が切れました。Jellyfin サーバーを確認して再試行してください',
      '优选': '優先', '部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新': '一部のソースが利用できないため、結果が不完全な可能性があります。後で右上から更新してください',
      '没有找到合适的资源': '適切なリリースが見つかりません', '可以稍后刷新再试，或换一季': '後で更新するか、別のシーズンを試してください', '共 {n} 个版本': '{n} 件のリリース',
      '（缓存结果，可点右上角刷新）': '（キャッシュ。右上から更新できます）', '{n} 个版本': '{n} 件', '整季': 'シーズン一括', '多集': '複数話', '单集': '単話',
      '集数未知': '話数不明', '第 {n} 集': '第 {n} 話', '下载': 'ダウンロード', '提交中…': '送信中…', '已交给迅雷': 'Thunder に送信済み', '查看下载任务': 'ダウンロードを見る',
      '{n} 个文件': '{n} ファイル', '等待迅雷开始': 'Thunder の開始待ち', '下载中': 'ダウンロード中', '停滞': '停滞', '入库中': 'ライブラリへ移動中', '已入库': 'ライブラリ追加済み', '失败': '失敗',
      '还没有下载任务': 'ダウンロードはまだありません', '在详情页点"下载"选一个版本，就会出现在这里': '詳細ページの「ダウンロード」でリリースを選ぶとここに表示されます',
      '移除任务（不删除已下载的文件）': 'タスクを削除（ダウンロード済みのファイルは残ります）', '移除': '削除',
      '← 返回榜单': '← 戻る', '← 返回收藏': '← お気に入りに戻る', '刷新': '更新', '连不上 Jellyfin，请稍后点刷新重试': 'Jellyfin に接続できません。しばらくしてから更新してください', 'Jellyfin 正在重启或暂时不可用，请稍后点刷新重试': 'Jellyfin が再起動中か利用できません。しばらくしてから更新してください', '全局搜索电影、剧集': '映画・ドラマを検索', '清除': 'クリア', '清除搜索': '検索をクリア', '萤火虫 · 热门榜单': 'FireFly · 人気',
      '我的收藏': 'お気に入り', '正在搜索“{q}”…': '「{q}」を検索中…', '加载中…': '読み込み中…', '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key': '先にダッシュボード → プラグイン → FireFly で TMDb API Key を入力してください',
      '“{q}”的搜索结果': '「{q}」の検索結果', '全部电影和剧集': 'すべての映画とドラマ', '约 {n} 条': '約 {n} 件', '不受筛选条件影响': 'フィルターは適用されません', '返回榜单': '一覧に戻る',
      '没有找到“{q}”，换个片名或用原名试试': '「{q}」は見つかりませんでした。別のタイトルか原題で試してください', '加载更多': 'さらに読み込む', '已经到底了': 'これで全部です', '加载筛选项…': 'フィルターを読み込み中…', '全部': 'すべて',
      '没有符合条件的影片，换个筛选试试': '条件に合う作品がありません', '剧集': 'ドラマ', '电影': '映画', '已在库': 'ライブラリにあり', '还没有收藏。在详情页点"收藏"即可加入这里。': 'お気に入りはまだありません。詳細ページの「お気に入り」で追加できます。',
      '分类': 'カテゴリ', '类型': 'ジャンル', '地区': '地域', '时间': '年代', '出品': '制作', '推荐': '並び順', '分级': 'レーティング', '动漫': 'アニメ', '纪录片': 'ドキュメンタリー', '综艺': 'バラエティ',
      '最热': '人気順', '最新': '新着順', '高分': '高評価', '最多评价': '評価数順', '更早': 'それ以前',
      '华纳兄弟': 'ワーナー・ブラザース', '环球影业': 'ユニバーサル', '派拉蒙': 'パラマウント', '迪士尼': 'ディズニー', '哥伦比亚': 'コロンビア', '漫威影业': 'マーベル・スタジオ', '皮克斯': 'ピクサー', '梦工厂动画': 'ドリームワークス',
      '狮门影业': 'ライオンズゲート', '吉卜力工作室': 'スタジオジブリ', '东宝': '東宝', '腾讯视频': 'Tencent Video', '爱奇艺': 'iQIYI', '优酷': 'Youku', '芒果TV': 'Mango TV', '哔哩哔哩': 'bilibili', '湖南卫视': '湖南衛視',
      'G 全年龄': 'G', 'PG 家长指导': 'PG', 'PG-13 13岁以上': 'PG-13', 'R 限制级': 'R', 'NC-17 17岁以下禁止': 'NC-17',
      '加载详情…': '詳細を読み込み中…', '♥ 已收藏': '♥ お気に入り済み', '♡ 收藏': '♡ お気に入り', '⬇ 下载': '⬇ ダウンロード', '▶ 播放预告片': '▶ 予告編を再生', '用户评分': 'ユーザー評価', '用户评分（{n}人）': 'ユーザー評価（{n}件）',
      '剧情介绍': 'あらすじ', '暂无中文简介。': 'あらすじはありません。', '预告片': '予告編', '播放不了？在 YouTube 打开': '再生できない場合は YouTube で開く', 'TMDb 暂无预告片': 'TMDb に予告編はありません',
      '{n} 集': '{n} 話', '导演 & 演员': 'キャスト・スタッフ', '暂无演职员信息': 'キャスト・スタッフ情報はありません', '导演': '監督', '编剧': '脚本', '主创': '製作者',
      '查看全部季（共 {n} 季）': 'すべてのシーズン（{n}）', '收起其他季': 'ほかのシーズンを閉じる', '最新一季': '最新シーズン', '当前季': '現在のシーズン', '首播 {date}': '初回放送 {date}', '即将开播': '放送予定', '共 {n} 集': '全 {n} 話',
      'FireFly 热门榜单': 'FireFly 人気',
      'TMDb 没有这个条目': 'TMDb に見つかりません', '未配置 TMDb API Key': 'TMDb API Key が未設定です', '无法识别要搜索的影片': '検索する作品を特定できません',
      '这个版本的信息已过期，请点刷新重新搜索后再下载': 'このリリース情報は期限切れです。更新して再検索してからダウンロードしてください', 'TMDb 请求失败：{x}': 'TMDb リクエスト失敗：{x}',
      '已交给迅雷，等待开始下载': 'Thunder に送信済み、開始待ち', '正在移入媒体库': 'ライブラリへ移動中',
      '迅雷还没有开始下载：请确认迅雷已接收任务、下载目录与 FireFly 设置一致；也可以换一个版本': 'Thunder がまだ開始していません。タスクの受信とダウンロード先が FireFly の設定と一致しているか確認するか、別のリリースを試してください',
      '下载长时间没有进展（可能缺少做种或被迅雷拦截），可以继续等待或换一个版本': '長時間進んでいません（シード不足か Thunder にブロックされた可能性）。待つか別のリリースを試してください',
      '读取下载目录失败：{x}': 'ダウンロード先を読めません：{x}', '归档失败：{x}': 'ライブラリへの移動に失敗：{x}', '已入库 {n} 个文件': '{n} ファイルをライブラリに追加', '，已清理 {n} 个下载目录': '、ダウンロードフォルダ {n} 個を削除',
      '无法把磁力交给迅雷（open 退出码 {n}），请确认已安装迅雷并设为磁力链接的默认打开方式': 'マグネットを Thunder に渡せません（open 終了コード {n}）。Thunder がインストールされ、マグネットリンクの既定アプリになっているか確認してください',
      '未配置迅雷下载目录': 'Thunder のダウンロード先が未設定です', '无法创建迅雷下载目录：{x}': 'Thunder のダウンロード先を作成できません：{x}',
    },
    ko: {
      '剧集|单部': 'TV 시리즈', '电影|单部': '영화',
      '全{n}集': '전 {n}화', '第{a}-{b}集': '{a}-{b}화', '第{n}集(短)': '{n}화', '杜比视界': '돌비 비전', '杜比视界+HDR': '돌비 비전+HDR', '枪版': '캠버전', '中英双字': '중영 자막', '中英字幕': '중영 자막', '简繁英字幕': '중(간·번)영 자막', '简繁字幕': '중국어(간·번) 자막', '中文字幕': '중국어 자막', '中字': '중국어 자막', '国语配音': '중국어 더빙', '粤语配音': '광둥어 더빙',
      '请求失败 {n}': '요청 실패 {n}', '重新搜索': '다시 검색', '关闭（Esc）': '닫기 (Esc)', '关闭': '닫기', '资源版本': '릴리스', '下载任务': '다운로드',
      '获取资源': '릴리스 찾기', '第 {n} 季': '시즌 {n}', '正在重新搜索…': '다시 검색 중…', '正在搜索资源…': '릴리스 검색 중…',
      '补充搜索失败，可点右上角刷新重试': '추가 검색에 실패했습니다. 오른쪽 위 새로고침으로 다시 시도하세요', '没有找到优选资源，正在扩大搜索范围…': '우선 릴리스가 없어 검색 범위를 넓히는 중…',
      '正在补充更多资源…': '릴리스를 더 찾는 중…', '网络连接中断，正在重试…': '연결이 끊겼습니다. 다시 시도하는 중…', '网络连接中断，请检查 Jellyfin 服务后重试': '연결이 끊겼습니다. Jellyfin 서버를 확인한 뒤 다시 시도하세요',
      '优选': '우선', '部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新': '일부 소스를 사용할 수 없어 결과가 불완전할 수 있습니다. 나중에 오른쪽 위에서 새로고침하세요',
      '没有找到合适的资源': '알맞은 릴리스가 없습니다', '可以稍后刷新再试，或换一季': '나중에 새로고침하거나 다른 시즌을 선택하세요', '共 {n} 个版本': '릴리스 {n}개',
      '（缓存结果，可点右上角刷新）': ' (캐시된 결과, 오른쪽 위에서 새로고침)', '{n} 个版本': '{n}개', '整季': '시즌 전체', '多集': '여러 화', '单集': '단일 화',
      '集数未知': '회차 알 수 없음', '第 {n} 集': '{n}화', '下载': '다운로드', '提交中…': '보내는 중…', '已交给迅雷': 'Thunder로 보냄', '查看下载任务': '다운로드 보기',
      '{n} 个文件': '파일 {n}개', '等待迅雷开始': 'Thunder 시작 대기', '下载中': '다운로드 중', '停滞': '멈춤', '入库中': '라이브러리로 이동 중', '已入库': '라이브러리에 추가됨', '失败': '실패',
      '还没有下载任务': '다운로드가 없습니다', '在详情页点"下载"选一个版本，就会出现在这里': '상세 페이지에서 "다운로드"로 릴리스를 고르면 여기에 표시됩니다',
      '移除任务（不删除已下载的文件）': '작업 제거 (다운로드한 파일은 유지)', '移除': '제거',
      '← 返回榜单': '← 뒤로', '← 返回收藏': '← 즐겨찾기로', '刷新': '새로고침', '连不上 Jellyfin，请稍后点刷新重试': 'Jellyfin에 연결할 수 없습니다. 잠시 후 새로고침하세요', 'Jellyfin 正在重启或暂时不可用，请稍后点刷新重试': 'Jellyfin이 재시작 중이거나 사용할 수 없습니다. 잠시 후 새로고침하세요', '全局搜索电影、剧集': '영화·TV 전체 검색', '清除': '지우기', '清除搜索': '검색 지우기', '萤火虫 · 热门榜单': 'FireFly · 인기',
      '我的收藏': '즐겨찾기', '正在搜索“{q}”…': '"{q}" 검색 중…', '加载中…': '불러오는 중…', '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key': '먼저 대시보드 → 플러그인 → FireFly에서 TMDb API Key를 입력하세요',
      '“{q}”的搜索结果': '"{q}" 검색 결과', '全部电影和剧集': '모든 영화와 TV', '约 {n} 条': '약 {n}건', '不受筛选条件影响': '필터 미적용', '返回榜单': '목록으로',
      '没有找到“{q}”，换个片名或用原名试试': '"{q}"을(를) 찾지 못했습니다. 다른 제목이나 원제로 검색해 보세요', '加载更多': '더 보기', '已经到底了': '모두 불러왔습니다', '加载筛选项…': '필터 불러오는 중…', '全部': '전체',
      '没有符合条件的影片，换个筛选试试': '조건에 맞는 작품이 없습니다', '剧集': 'TV', '电影': '영화', '已在库': '라이브러리에 있음', '还没有收藏。在详情页点"收藏"即可加入这里。': '즐겨찾기가 없습니다. 상세 페이지에서 "즐겨찾기"로 추가하세요.',
      '分类': '분류', '类型': '장르', '地区': '지역', '时间': '연도', '出品': '제작', '推荐': '정렬', '分级': '등급', '动漫': '애니메이션', '纪录片': '다큐멘터리', '综艺': '예능',
      '最热': '인기순', '最新': '최신순', '高分': '평점순', '最多评价': '평가 많은 순', '更早': '그 이전',
      '华纳兄弟': '워너 브라더스', '环球影业': '유니버설', '派拉蒙': '파라마운트', '迪士尼': '디즈니', '哥伦比亚': '컬럼비아', '漫威影业': '마블 스튜디오', '皮克斯': '픽사', '梦工厂动画': '드림웍스 애니메이션',
      '狮门影业': '라이언스게이트', '吉卜力工作室': '스튜디오 지브리', '东宝': '도호', '腾讯视频': 'Tencent Video', '爱奇艺': 'iQIYI', '优酷': 'Youku', '芒果TV': 'Mango TV', '哔哩哔哩': 'Bilibili', '湖南卫视': '후난 TV',
      'G 全年龄': 'G', 'PG 家长指导': 'PG', 'PG-13 13岁以上': 'PG-13', 'R 限制级': 'R', 'NC-17 17岁以下禁止': 'NC-17',
      '加载详情…': '상세 정보 불러오는 중…', '♥ 已收藏': '♥ 즐겨찾기됨', '♡ 收藏': '♡ 즐겨찾기', '⬇ 下载': '⬇ 다운로드', '▶ 播放预告片': '▶ 예고편 재생', '用户评分': '사용자 평점', '用户评分（{n}人）': '사용자 평점 ({n}명)',
      '剧情介绍': '줄거리', '暂无中文简介。': '줄거리가 없습니다.', '预告片': '예고편', '播放不了？在 YouTube 打开': '재생되지 않나요? YouTube에서 열기', 'TMDb 暂无预告片': 'TMDb에 예고편이 없습니다',
      '{n} 集': '{n}화', '导演 & 演员': '출연·제작진', '暂无演职员信息': '출연·제작진 정보가 없습니다', '导演': '감독', '编剧': '각본', '主创': '크리에이터',
      '查看全部季（共 {n} 季）': '모든 시즌 보기 ({n})', '收起其他季': '다른 시즌 접기', '最新一季': '최신 시즌', '当前季': '현재 시즌', '首播 {date}': '첫 방송 {date}', '即将开播': '방영 예정', '共 {n} 集': '총 {n}화',
      'FireFly 热门榜单': 'FireFly 인기',
      'TMDb 没有这个条目': 'TMDb에 없는 작품입니다', '未配置 TMDb API Key': 'TMDb API Key가 설정되지 않았습니다', '无法识别要搜索的影片': '검색할 작품을 알 수 없습니다',
      '这个版本的信息已过期，请点刷新重新搜索后再下载': '이 릴리스 정보가 만료되었습니다. 새로고침 후 다시 검색해 다운로드하세요', 'TMDb 请求失败：{x}': 'TMDb 요청 실패: {x}',
      '已交给迅雷，等待开始下载': 'Thunder로 보냄, 시작 대기 중', '正在移入媒体库': '라이브러리로 이동 중',
      '迅雷还没有开始下载：请确认迅雷已接收任务、下载目录与 FireFly 设置一致；也可以换一个版本': 'Thunder가 아직 시작하지 않았습니다. 작업을 받았는지, 다운로드 폴더가 FireFly 설정과 같은지 확인하거나 다른 릴리스를 시도하세요',
      '下载长时间没有进展（可能缺少做种或被迅雷拦截），可以继续等待或换一个版本': '오랫동안 진행이 없습니다(시드 부족 또는 Thunder 차단 가능). 계속 기다리거나 다른 릴리스를 시도하세요',
      '读取下载目录失败：{x}': '다운로드 폴더를 읽지 못했습니다: {x}', '归档失败：{x}': '라이브러리 이동 실패: {x}', '已入库 {n} 个文件': '파일 {n}개를 라이브러리에 추가', '，已清理 {n} 个下载目录': ', 다운로드 폴더 {n}개 정리',
      '无法把磁力交给迅雷（open 退出码 {n}），请确认已安装迅雷并设为磁力链接的默认打开方式': '마그넷을 Thunder로 보내지 못했습니다(open 종료 코드 {n}). Thunder가 설치되어 마그넷 링크 기본 앱인지 확인하세요',
      '未配置迅雷下载目录': 'Thunder 다운로드 폴더가 설정되지 않았습니다', '无法创建迅雷下载目录：{x}': 'Thunder 다운로드 폴더를 만들지 못했습니다: {x}',
    },
  };

  // 服务端返回的提示里夹着变量（路径、错误原因、数量），按模板匹配后再翻译
  var PATTERNS = [
    [/^TMDb 请求失败：([\s\S]*)$/, 'TMDb 请求失败：{x}'],
    [/^读取下载目录失败：([\s\S]*)$/, '读取下载目录失败：{x}'],
    [/^归档失败：([\s\S]*)$/, '归档失败：{x}'],
    [/^无法创建迅雷下载目录：([\s\S]*)$/, '无法创建迅雷下载目录：{x}'],
    [/^无法把磁力交给迅雷（open 退出码 (-?\d+)），请确认已安装迅雷并设为磁力链接的默认打开方式$/, '无法把磁力交给迅雷（open 退出码 {n}），请确认已安装迅雷并设为磁力链接的默认打开方式', 'n'],
  ];

  var language = 'zh-CN';

  function dict() {
    var l = language === 'zh-HK' ? 'zh-TW' : language;
    return DICT[l] || DICT[l.split('-')[0]] || null;
  }

  function fill(text, vars) {
    return vars ? text.replace(/\{(\w+)\}/g, function (m, k) { return vars[k] != null ? vars[k] : m; }) : text;
  }

  /** 按当前语言取文案；vars 替换 {n}、{q} 等占位符。键里"|"后面是区分同一原文不同用法的说明，简体中文显示时去掉。 */
  function t(zh, vars) {
    var d = dict();
    return fill((d && d[zh]) || zh.split('|')[0], vars);
  }

  /** 服务端返回的文字（错误、任务提示、职务）：整句或按模板翻译，认不出来的原样返回。 */
  function server(text) {
    if (!text || !dict()) return text;
    var d = dict();
    if (d[text]) return d[text];
    // 入库完成："已入库 N 个文件" + 可选的"，已清理 N 个下载目录"
    var done = /^已入库 (\d+) 个文件(?:，已清理 (\d+) 个下载目录)?$/.exec(text);
    if (done) return t('已入库 {n} 个文件', { n: done[1] }) + (done[2] ? t('，已清理 {n} 个下载目录', { n: done[2] }) : '');
    for (var i = 0; i < PATTERNS.length; i++) {
      var m = PATTERNS[i][0].exec(text);
      if (m) {
        var vars = {};
        vars[PATTERNS[i][2] || 'x'] = m[1];
        return t(PATTERNS[i][1], vars);
      }
    }
    // 职务"导演 / 编剧"逐个翻译
    if (text.indexOf(' / ') > 0) return text.split(' / ').map(function (p) { return d[p] || p; }).join(' / ');
    return text;
  }

  // 右侧信息栏跟随配置页的 TMDb 语言。语言名、地区名用浏览器的 Intl.DisplayNames 按该语言翻译；
  // 港澳台用简短叫法（Intl 默认是"香港特别行政区"这类全称）；简体中文沿用原来的叫法
  var REGION_SHORT = {
    'zh-CN': { CN: '中国大陆', HK: '中国香港', TW: '中国台湾', MO: '中国澳门' },
    'zh-TW': { CN: '中國大陸', HK: '香港', TW: '台灣', MO: '澳門' },
    en: { CN: 'Mainland China', HK: 'Hong Kong', TW: 'Taiwan', MO: 'Macau' },
    ja: { CN: '中国本土', HK: '香港', TW: '台湾', MO: 'マカオ' },
    ko: { CN: '중국 본토', HK: '홍콩', TW: '대만', MO: '마카오' },
  };
  var SIDE_TEXT = {
    'zh-CN': {
      type: '资源类型', status: '状态', originalTitle: '原始片名', originalLanguage: '原始语言', firstAir: '首播日期', release: '上映日期',
      countries: '出品地区', seasonsEpisodes: '季 / 集', runtime: '片长', certification: '分级', networks: '播出平台', streaming: '在线播放',
      rentBuy: '租赁 / 购买', companies: '出品方', budget: '预算', revenue: '票房', homepage: '官方网站', viewOnTmdb: '在 TMDb 查看', noData: '暂无数据', paren: function (x) { return '（' + x + '）'; },
      seasons: function (n) { return n + ' 季'; }, episodes: function (n) { return n + ' 集'; }, perEpisode: '每集约 ',
      hours: function (h, m) { return h + '小时' + (m ? m + '分钟' : ''); }, mins: function (m) { return m + '分钟'; },
      kinds: { series: '剧集', movie: '电影', anime: '动漫', animatedMovie: '动画电影', documentary: '纪录片', variety: '综艺' },
      states: { Released: '已上映', 'Post Production': '后期制作', 'In Production': '制作中', Planned: '筹备中', Rumored: '传闻', Canceled: '已取消', 'Returning Series': '更新中', Ended: '已完结', Pilot: '试播' },
    },
    'zh-TW': {
      type: '資源類型', status: '狀態', originalTitle: '原始片名', originalLanguage: '原始語言', firstAir: '首播日期', release: '上映日期',
      countries: '出品地區', seasonsEpisodes: '季 / 集', runtime: '片長', certification: '分級', networks: '播出平台', streaming: '線上播放',
      rentBuy: '租借 / 購買', companies: '出品方', budget: '預算', revenue: '票房', homepage: '官方網站', viewOnTmdb: '在 TMDb 查看', noData: '暫無資料', paren: function (x) { return '（' + x + '）'; },
      seasons: function (n) { return n + ' 季'; }, episodes: function (n) { return n + ' 集'; }, perEpisode: '每集約 ',
      hours: function (h, m) { return h + '小時' + (m ? m + '分鐘' : ''); }, mins: function (m) { return m + '分鐘'; },
      kinds: { series: '劇集', movie: '電影', anime: '動漫', animatedMovie: '動畫電影', documentary: '紀錄片', variety: '綜藝' },
      states: { Released: '已上映', 'Post Production': '後期製作', 'In Production': '製作中', Planned: '籌備中', Rumored: '傳聞', Canceled: '已取消', 'Returning Series': '更新中', Ended: '已完結', Pilot: '試播' },
    },
    en: {
      type: 'Type', status: 'Status', originalTitle: 'Original Title', originalLanguage: 'Original Language', firstAir: 'First Air Date', release: 'Release Date',
      countries: 'Country', seasonsEpisodes: 'Seasons / Episodes', runtime: 'Runtime', certification: 'Rating', networks: 'Network', streaming: 'Streaming',
      rentBuy: 'Rent / Buy', companies: 'Production', budget: 'Budget', revenue: 'Revenue', homepage: 'Homepage', viewOnTmdb: 'View on TMDb', noData: 'No data', paren: function (x) { return ' (' + x + ')'; },
      seasons: function (n) { return n + (n === 1 ? ' season' : ' seasons'); }, episodes: function (n) { return n + (n === 1 ? ' episode' : ' episodes'); }, perEpisode: '~',
      hours: function (h, m) { return h + 'h' + (m ? ' ' + m + 'm' : ''); }, mins: function (m) { return m + 'm'; },
      kinds: { series: 'TV Series', movie: 'Movie', anime: 'Anime', animatedMovie: 'Animated Film', documentary: 'Documentary', variety: 'Reality / Talk Show' },
      states: { Released: 'Released', 'Post Production': 'Post Production', 'In Production': 'In Production', Planned: 'Planned', Rumored: 'Rumored', Canceled: 'Canceled', 'Returning Series': 'Returning Series', Ended: 'Ended', Pilot: 'Pilot' },
    },
    ja: {
      type: '種類', status: 'ステータス', originalTitle: '原題', originalLanguage: '原語', firstAir: '初回放送日', release: '公開日',
      countries: '製作国', seasonsEpisodes: 'シーズン / 話数', runtime: '上映時間', certification: 'レーティング', networks: '放送局', streaming: '配信',
      rentBuy: 'レンタル / 購入', companies: '製作会社', budget: '製作費', revenue: '興行収入', homepage: '公式サイト', viewOnTmdb: 'TMDb で見る', noData: 'データなし', paren: function (x) { return '（' + x + '）'; },
      seasons: function (n) { return n + ' シーズン'; }, episodes: function (n) { return n + ' 話'; }, perEpisode: '1話 約',
      hours: function (h, m) { return h + '時間' + (m ? m + '分' : ''); }, mins: function (m) { return m + '分'; },
      kinds: { series: 'ドラマ', movie: '映画', anime: 'アニメ', animatedMovie: 'アニメ映画', documentary: 'ドキュメンタリー', variety: 'バラエティ' },
      states: { Released: '公開済み', 'Post Production': 'ポストプロダクション', 'In Production': '製作中', Planned: '企画中', Rumored: '噂', Canceled: '中止', 'Returning Series': '放送中', Ended: '完結', Pilot: 'パイロット' },
    },
    ko: {
      type: '유형', status: '상태', originalTitle: '원제', originalLanguage: '원어', firstAir: '첫 방송일', release: '개봉일',
      countries: '제작 국가', seasonsEpisodes: '시즌 / 에피소드', runtime: '러닝타임', certification: '등급', networks: '방송사', streaming: '스트리밍',
      rentBuy: '대여 / 구매', companies: '제작사', budget: '제작비', revenue: '수익', homepage: '공식 사이트', viewOnTmdb: 'TMDb에서 보기', noData: '데이터 없음', paren: function (x) { return ' (' + x + ')'; },
      seasons: function (n) { return '시즌 ' + n + '개'; }, episodes: function (n) { return n + '화'; }, perEpisode: '회당 약 ',
      hours: function (h, m) { return h + '시간' + (m ? ' ' + m + '분' : ''); }, mins: function (m) { return m + '분'; },
      kinds: { series: 'TV 시리즈', movie: '영화', anime: '애니메이션', animatedMovie: '애니메이션 영화', documentary: '다큐멘터리', variety: '예능' },
      states: { Released: '개봉', 'Post Production': '후반 작업', 'In Production': '제작 중', Planned: '기획 중', Rumored: '루머', Canceled: '취소', 'Returning Series': '방영 중', Ended: '종영', Pilot: '파일럿' },
    },
  };
  function sideText(lang) {
    lang = lang || language;
    if (lang === 'zh-HK') lang = 'zh-TW';
    return SIDE_TEXT[lang] || SIDE_TEXT[lang.split('-')[0]] || SIDE_TEXT['zh-CN'];
  }
  function displayName(type, code, lang) {
    if (!code) return code;
    lang = lang || language;
    var short = REGION_SHORT[lang === 'zh-HK' ? 'zh-TW' : lang] || REGION_SHORT[lang.split('-')[0]];
    if (type === 'region' && short && short[code]) return short[code];
    try {
      // TMDb 用 cn 表示粤语
      return new Intl.DisplayNames([lang], { type: type, fallback: 'code' }).of(type === 'language' && code === 'cn' ? 'yue' : code) || code;
    } catch (e) {
      return code;
    }
  }

  /** 插件生成的版本名末尾的集数（"全12集"、"第01-02集"、"第05集"）按当前语言改写；片名部分不动。 */
  function release(name) {
    if (!name || !dict()) return name;
    return name
      .replace(/全(\d+)集$/, function (m, n) { return t('全{n}集', { n: +n }); })
      .replace(/第(\d+)-(\d+)集$/, function (m, a, b) { return t('第{a}-{b}集', { a: a, b: b }); })
      .replace(/第(\d+)集$/, function (m, n) { return t('第{n}集(短)', { n: n }); });
  }

  /** 版本标签：整条有词条就用词条；"国语配音+中文字幕"这类用"+"拼起来的，每段都有词条时逐段翻译，否则原样显示。 */
  function chip(text) {
    var d = dict();
    if (!d || !text || d[text]) return t(text);
    var parts = String(text).split('+');
    if (parts.length < 2 || !parts.every(function (p) { return d[p]; })) return text;
    return parts.map(function (p) { return d[p]; }).join(' + ');
  }

  window.FireFlyI18n = {
    setLanguage: function (l) { if (l) language = l; },
    language: function () { return language; },
    t: t,
    server: server,
    chip: chip,
    release: release,
    sideText: sideText,
    displayName: displayName,
  };
})();
