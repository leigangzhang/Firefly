/* 萤火虫 FireFly：注入 Jellyfin 网页的前端。详情页"获取资源" + 顶栏"发现" + 任务列表。仅管理员可见。 */
(function () {
  'use strict';
  if (window.__fireflyLoaded) return;
  window.__fireflyLoaded = true;

  var isAdmin = false;
  var pendingId = null;
  var pollTimer = null;

  // ---------- 工具 ----------
  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      if (k === 'class') node.className = attrs[k];
      else if (k === 'text') node.textContent = attrs[k];
      else if (k.indexOf('on') === 0) node.addEventListener(k.slice(2), attrs[k]);
      else node.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return node;
  }

  function api(method, path, body) {
    var client = window.ApiClient;
    var headers = { 'Authorization': 'MediaBrowser Token="' + client.accessToken() + '"' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(client.getUrl('FireFly/' + path), { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body) })
      .then(function (res) {
        return res.text().then(function (text) {
          var data = text ? JSON.parse(text) : null;
          if (!res.ok) { var err = new Error((data && data.error) || ('请求失败 ' + res.status)); err.status = res.status; err.data = data; throw err; }
          return data;
        });
      });
  }

  function gb(bytes) { return bytes ? (bytes / 1073741824).toFixed(bytes >= 10737418240 ? 0 : 1) + ' GB' : '—'; }
  function speed(bps) { return bps > 0 ? (bps / 1048576).toFixed(1) + ' MB/s' : ''; }
  function resLabel(r) { return r === 2160 ? '4K' : r ? r + 'p' : ''; }
  function date(s) { return s ? String(s).slice(0, 10) : ''; }

  // ---------- 弹层 ----------
  var overlay = null;
  function openPanel(title) {
    closePanel();
    var body = el('div', { class: 'ff-body' });
    overlay = el('div', { class: 'ff-overlay', onclick: function (e) { if (e.target === overlay) closePanel(); } }, [
      el('div', { class: 'ff-panel' }, [
        el('div', { class: 'ff-head' }, [
          el('span', { class: 'ff-title', text: title }),
          el('button', { class: 'ff-btn ff-ghost', text: '下载任务', onclick: showTasks }),
          el('button', { class: 'ff-btn ff-ghost', text: '✕', onclick: closePanel }),
        ]),
        body,
      ]),
    ]);
    document.body.appendChild(overlay);
    return body;
  }
  function closePanel() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (overlay) { overlay.remove(); overlay = null; }
  }
  function message(body, text, cls) { body.innerHTML = ''; body.appendChild(el('div', { class: 'ff-msg ' + (cls || ''), text: text })); }

  // ---------- 获取资源 ----------
  function openSearch(request, label) {
    var body = openPanel('获取资源 · ' + label);
    if (request.kind === 'Series') {
      var q = request.itemId ? 'seasons?itemId=' + request.itemId : 'seasons?tmdbId=' + (request.tmdbId || '');
      api('GET', q).then(function (r) {
        var select = el('select', { class: 'ff-select', onchange: function () { request.season = +select.value; runSearch(body, request, false); } });
        r.seasons.forEach(function (n) { select.appendChild(el('option', { value: n, text: '第 ' + n + ' 季' })); });
        request.season = r.seasons[0];
        body.parentNode.insertBefore(el('div', { class: 'ff-bar' }, ['季：', select]), body);
        runSearch(body, request, false);
      }).catch(function () { runSearch(body, request, false); });
    } else {
      runSearch(body, request, false);
    }
  }

  function runSearch(body, request, refresh) {
    message(body, '正在搜索资源…');
    request.refresh = refresh;
    api('POST', 'search', request).then(function (r) { renderReleases(body, request, r); })
      .catch(function (e) { message(body, e.message, 'ff-error'); });
  }

  function chips(t) {
    return [resLabel(t.resolution), t.hdr, t.codec, t.mediaSource, t.audio, t.subtitles, t.publisher]
      .filter(Boolean).map(function (c) { return el('span', { class: 'ff-chip', text: c }); });
  }

  function renderReleases(body, request, r) {
    body.innerHTML = '';
    var quota = r.quota;
    body.appendChild(el('div', { class: 'ff-bar' }, [
      el('span', { text: quota.isVip ? quota.licenseStatus : '本月免费额度：已用 ' + quota.used + ' / ' + quota.freeLimit }),
      el('button', { class: 'ff-btn ff-ghost', text: '重新搜索', onclick: function () { runSearch(body, request, true); } }),
    ]));
    if (r.errors && r.errors.length) body.appendChild(el('div', { class: 'ff-msg ff-error', text: '部分搜索源出错：' + r.errors.join('；') }));
    if (!r.releases.length) {
      body.appendChild(el('div', { class: 'ff-msg', text: '没有找到合适的资源（已搜索：' + r.queries.join('、') + '）' }));
      return;
    }
    r.releases.forEach(function (rel) {
      var btn = el('button', { class: 'ff-btn', text: '下载' });
      btn.addEventListener('click', function () {
        btn.disabled = true; btn.textContent = '提交中…';
        api('POST', 'tasks', { query: r.query, release: rel }).then(function () {
          btn.textContent = '已交给迅雷';
        }).catch(function (e) {
          btn.disabled = false; btn.textContent = '下载';
          if (e.status === 402) showVip(e.message); else alert(e.message);
        });
      });
      body.appendChild(el('div', { class: 'ff-row' }, [
        el('div', { class: 'ff-main' }, [
          el('div', { class: 'ff-name', text: rel.name, title: rel.name }),
          el('div', { class: 'ff-meta' }, chips(rel.traits).concat([
            el('span', { class: 'ff-dim', text: gb(rel.sizeBytes) }),
            el('span', { class: 'ff-dim', text: date(rel.createdAt) }),
          ])),
        ]),
        btn,
      ]));
    });
  }

  function showVip(text) {
    var body = openPanel('开通 VIP');
    body.appendChild(el('div', { class: 'ff-msg', text: text + '。开通 VIP 后不限部数。' }));
    body.appendChild(el('div', { class: 'ff-msg', text: '购买后会得到一串授权码，粘贴到 控制台 → 插件 → FireFly 的"授权码"一栏并保存即可。' }));
  }

  // ---------- 任务 ----------
  var stateText = { Submitted: '等待迅雷开始', Downloading: '下载中', Stalled: '停滞', Archiving: '入库中', Done: '已入库', Failed: '失败' };
  function showTasks() {
    var body = openPanel('下载任务');
    function load() {
      api('GET', 'tasks').then(function (tasks) {
        body.innerHTML = '';
        if (!tasks.length) { message(body, '还没有下载任务'); return; }
        tasks.forEach(function (t) {
          var pct = t.bytesTotal ? Math.min(100, Math.floor(t.bytesDone * 100 / t.bytesTotal)) : 0;
          var q = t.query;
          body.appendChild(el('div', { class: 'ff-row ff-task ff-' + t.state.toLowerCase() }, [
            el('div', { class: 'ff-main' }, [
              el('div', { class: 'ff-name', text: q.title + (q.year ? ' (' + q.year + ')' : '') + (q.season ? ' 第 ' + q.season + ' 季' : '') }),
              el('div', { class: 'ff-progress' }, [el('div', { class: 'ff-progress-bar', style: 'width:' + pct + '%' })]),
              el('div', { class: 'ff-meta' }, [
                el('span', { class: 'ff-chip', text: stateText[t.state] || t.state }),
                el('span', { class: 'ff-dim', text: gb(t.bytesDone) + ' / ' + gb(t.bytesTotal) + ' · ' + pct + '% ' + speed(t.speedBps) }),
                t.message ? el('span', { class: 'ff-dim', text: t.message }) : null,
              ]),
            ]),
            t.state === 'Archiving' ? null : el('button', { class: 'ff-btn ff-ghost', text: '移除', onclick: function () { api('POST', 'tasks/' + t.id + '/dismiss').then(load); } }),
          ]));
        });
      }).catch(function (e) { message(body, e.message, 'ff-error'); });
    }
    load();
    pollTimer = setInterval(load, 5000);
  }

  // ---------- 发现 ----------
  function openDiscover() {
    var body = openPanel('发现 · 本周热门');
    var current = 'movie';
    var tabs = el('div', { class: 'ff-bar' });
    ['movie', 'tv'].forEach(function (k) {
      tabs.appendChild(el('button', { class: 'ff-btn ff-ghost', text: k === 'movie' ? '电影' : '剧集', onclick: function () { current = k; load(); } }));
    });
    body.parentNode.insertBefore(tabs, body);
    function load() {
      message(body, '加载中…');
      api('GET', 'discover?kind=' + current).then(function (r) {
        body.innerHTML = '';
        if (!r.configured) { message(body, '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key'); return; }
        var grid = el('div', { class: 'ff-grid' });
        r.items.forEach(function (i) {
          grid.appendChild(el('div', { class: 'ff-card', title: i.overview || '', onclick: function () {
            openSearch({ title: i.title, originalTitle: i.originalTitle, year: i.year, kind: i.kind, tmdbId: i.tmdbId }, i.title);
          } }, [
            i.posterUrl ? el('img', { src: i.posterUrl, loading: 'lazy' }) : el('div', { class: 'ff-noposter' }),
            el('div', { class: 'ff-card-title', text: i.title + (i.year ? ' (' + i.year + ')' : '') }),
            i.inLibrary ? el('span', { class: 'ff-chip ff-in', text: '已在库' }) : null,
          ]));
        });
        body.appendChild(grid);
      }).catch(function (e) { message(body, e.message, 'ff-error'); });
    }
    load();
  }

  // ---------- 注入入口 ----------
  function currentItemId() {
    var m = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(location.hash || location.search);
    return m ? m[1] : null;
  }

  function injectDetailButton() {
    var id = currentItemId();
    if (!id || location.hash.indexOf('details') < 0) return;
    var page = document.querySelector('.itemDetailPage:not(.hide)') || document;
    var host = page.querySelector('.mainDetailButtons');
    if (!host || host.querySelector('.ff-detail-btn[data-id="' + id + '"]')) return;
    if (pendingId === id) return;
    pendingId = id;
    var old = host.querySelector('.ff-detail-btn');
    if (old) old.remove();
    window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), id).then(function (item) {
      pendingId = null;
      var kind = { Movie: 'Movie', Series: 'Series', Season: 'Series', Episode: 'Series' }[item.Type];
      if (!kind || host.querySelector('.ff-detail-btn')) return;
      var season = item.Type === 'Season' ? item.IndexNumber : item.Type === 'Episode' ? item.ParentIndexNumber : undefined;
      host.appendChild(el('button', {
        is: 'emby-button', class: 'button-flat detailButton ff-detail-btn', 'data-id': id, title: '获取资源',
        onclick: function () { openSearch({ itemId: id, kind: kind, season: season }, item.SeriesName || item.Name); },
      }, [el('div', { class: 'detailButton-content' }, [el('span', { class: 'material-icons detailButton-icon', text: 'cloud_download', 'aria-hidden': 'true' })])]));
    }, function () { pendingId = null; });
  }

  function injectHeaderButton() {
    var right = document.querySelector('.headerRight');
    if (!right || right.querySelector('.ff-header-btn')) return;
    right.insertBefore(el('button', {
      is: 'paper-icon-button-light', class: 'headerButton ff-header-btn', title: 'FireFly 发现', onclick: openDiscover,
    }, [el('span', { class: 'material-icons', text: 'local_fire_department', 'aria-hidden': 'true' })]), right.firstChild);
  }

  function tick() {
    if (!isAdmin) return;
    injectHeaderButton();
    injectDetailButton();
  }

  function start() {
    var client = window.ApiClient;
    if (!client || !client.getCurrentUserId || !client.getCurrentUserId()) { setTimeout(start, 1000); return; }
    client.getCurrentUser().then(function (user) {
      isAdmin = !!(user && user.Policy && user.Policy.IsAdministrator);
      if (!isAdmin) return;
      new MutationObserver(function () { tick(); }).observe(document.body, { childList: true, subtree: true });
      window.addEventListener('hashchange', tick);
      tick();
    });
  }

  start();
})();
