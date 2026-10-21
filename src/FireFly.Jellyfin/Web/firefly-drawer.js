/* 萤火虫 FireFly：获取资源抽屉（选季、版本列表、分集）和任务列表。 */
(function () {
  'use strict';
  var F = window.FireFlyApp;
  if (!F || F.loaded_drawer) return;
  F.loaded_drawer = true;

  // 前面文件里的函数
  var T = F.T, el = F.el, api = F.api, gb = F.gb, speed = F.speed, resLabel = F.resLabel, date = F.date;

  var pollTimer = null;

  var drawer = null;
  var toastTimer = null;

  function openDrawer() {
    if (drawer) return drawer;
    var tabs = el('div', { class: 'ff-tabs', role: 'tablist' });
    var body = el('div', { class: 'ff-dbody' });
    var title = el('div', { class: 'ff-dtitle' });
    var sub = el('div', { class: 'ff-dsub' });
    var refresh = el('button', { class: 'ff-icon-btn', title: T('重新搜索'), 'aria-label': T('重新搜索'), style: 'display:none', onclick: function () { if (drawer && drawer.view === 'search') runSearch(true); } }, [icon('refresh')]);
    var backdrop = el('div', { class: 'ff-backdrop', onclick: closeDrawer });
    var panel = el('aside', { class: 'ff-drawer', role: 'dialog', 'aria-modal': 'true' }, [
      el('div', { class: 'ff-dhead' }, [
        el('div', { class: 'ff-dheading' }, [sub, title]),
        refresh,
        el('button', { class: 'ff-icon-btn', title: T('关闭（Esc）'), 'aria-label': T('关闭'), onclick: closeDrawer }, [icon('close')]),
      ]),
      tabs,
      body,
    ]);
    var root = el('div', { class: 'ff-drawer-root' }, [backdrop, panel]);
    document.body.appendChild(root);
    drawer = { root: root, panel: panel, tabs: tabs, body: body, title: title, sub: sub, refresh: refresh, view: null, search: null, cache: {} };
    document.addEventListener('keydown', onDrawerKey, true);
    requestAnimationFrame(function () { requestAnimationFrame(function () { root.classList.add('ff-open'); }); });
    return drawer;
  }

  function closeDrawer() {
    if (!drawer) return;
    stopPoll();
    if (drawer.search && drawer.search.searchController) drawer.search.searchController.abort();
    var root = drawer.root;
    drawer = null;
    document.removeEventListener('keydown', onDrawerKey, true);
    root.classList.remove('ff-open');
    setTimeout(function () { root.remove(); }, 300);
  }

  function onDrawerKey(e) {
    if (e.key === 'Escape' && drawer) { e.stopPropagation(); closeDrawer(); }
  }

  function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

  function icon(name, cls) { return el('span', { class: 'material-icons ' + (cls || ''), text: name, 'aria-hidden': 'true' }); }

  function renderTabs() {
    var d = drawer;
    d.tabs.innerHTML = '';
    var list = [];
    if (d.search) list.push({ key: 'search', label: T('资源版本') });
    list.push({ key: 'tasks', label: T('下载任务') });
    list.forEach(function (t) {
      d.tabs.appendChild(el('button', {
        class: 'ff-tab' + (d.view === t.key ? ' ff-on' : ''), role: 'tab', 'data-tab': t.key,
        onclick: function () { if (d.view !== t.key) (t.key === 'tasks' ? showTasks : showSearchView)(); },
      }, [t.label, t.key === 'tasks' ? el('span', { class: 'ff-tab-count', 'data-count': '' }) : null]));
    });
    refreshTaskCount();
  }

  // 切换视图：旧内容淡出、新内容淡入，抽屉本身不动
  function switchView(view, title, sub) {
    var d = drawer;
    stopPoll();
    d.view = view;
    d.title.textContent = title;
    d.sub.textContent = sub;
    d.refresh.style.display = view === 'search' ? '' : 'none';
    removeRail();
    renderTabs();
    var content = el('div', { class: 'ff-view-anim' });
    d.body.innerHTML = '';
    d.body.appendChild(content);
    d.body.scrollTop = 0;
    return content;
  }

  function message(body, text, cls) { body.innerHTML = ''; body.appendChild(el('div', { class: 'ff-msg ' + (cls || ''), text: text })); }

  function emptyState(box, iconName, text, extra) {
    box.innerHTML = '';
    box.appendChild(el('div', { class: 'ff-empty' }, [icon(iconName, 'ff-empty-icon'), el('div', { text: text }), extra || null]));
  }

  function skeleton(box, n) {
    box.innerHTML = '';
    for (var i = 0; i < n; i++) {
      box.appendChild(el('div', { class: 'ff-rcard ff-skel' }, [
        el('div', { class: 'ff-skel-line', style: 'width:' + (70 + (i * 13) % 25) + '%' }),
        el('div', { class: 'ff-skel-line ff-skel-short' }),
      ]));
    }
  }

  function toast(text, action, isError) {
    var old = document.querySelector('.ff-toast');
    if (old) old.remove();
    if (toastTimer) clearTimeout(toastTimer);
    var t = el('div', { class: 'ff-toast' + (isError ? ' ff-toast-err' : '') }, [icon(isError ? 'error_outline' : 'check_circle'), el('span', { text: text }),
      action ? el('button', { class: 'ff-toast-btn', text: action.label, onclick: function () { t.remove(); action.run(); } }) : null]);
    if (drawer) t.classList.add('ff-toast-drawer');
    document.body.appendChild(t);
    requestAnimationFrame(function () { t.classList.add('ff-show'); });
    toastTimer = setTimeout(function () { t.classList.remove('ff-show'); setTimeout(function () { t.remove(); }, 300); }, 4000);
  }

  function refreshTaskCount() {
    if (!drawer) return;
    var badge = drawer.tabs.querySelector('[data-count]');
    var tasks = drawer.cache.tasks;
    if (!badge || !tasks) return;
    var active = tasks.filter(function (t) { return ['Submitted', 'Downloading', 'Stalled', 'Archiving'].indexOf(t.state) >= 0; }).length;
    badge.textContent = active ? String(active) : '';
    badge.style.display = active ? '' : 'none';
  }

  // ---------- 获取资源 ----------
  function openSearch(request, label) {
    openDrawer();
    drawer.search = { request: request, label: label, result: null, seasons: null };
    showSearchView();
    // 后台取一次任务数，给"下载任务"标签上的角标用
    api('GET', 'tasks').then(function (tasks) { if (drawer) { drawer.cache.tasks = tasks; refreshTaskCount(); } }).catch(function () {});
  }

  function showSearchView() {
    var s = drawer.search;
    var content = switchView('search', s.label, T('获取资源'));
    var seasonBar = el('div', { class: 'ff-seasonbar' });
    var packSlot = el('div', { class: 'ff-packslot' });
    var list = el('div', { class: 'ff-rlist' });
    content.appendChild(el('div', { class: 'ff-seasonrow' }, [seasonBar, packSlot]));
    content.appendChild(list);
    s.list = list;
    s.packSlot = packSlot;
    if (s.request.kind === 'Series') {
      var q = s.request.itemId ? 'seasons?itemId=' + s.request.itemId : 'seasons?tmdbId=' + (s.request.tmdbId || '');
      var ready = s.seasons ? Promise.resolve({ seasons: s.seasons }) : api('GET', q);
      ready.then(function (r) {
        s.seasons = r.seasons;
        if (!s.request.season || r.seasons.indexOf(s.request.season) < 0) s.request.season = r.seasons[0];
        renderSeasons(seasonBar);
        s.result ? renderReleases(s.result) : runSearch(false);
      }).catch(function () { runSearch(false); });
    } else {
      s.result ? renderReleases(s.result) : runSearch(false);
    }
  }

  function renderSeasons(bar) {
    var s = drawer.search;
    bar.innerHTML = '';
    if (!s.seasons || s.seasons.length < 2) {
      if (s.seasons) bar.appendChild(el('span', { class: 'ff-dim', text: T('第 {n} 季', { n: s.seasons[0] }) }));
      return;
    }
    s.seasons.forEach(function (n) {
      bar.appendChild(el('button', {
        class: 'ff-fopt' + (n === s.request.season ? ' ff-on' : ''), text: T('第 {n} 季', { n: n }),
        onclick: function () { if (s.request.season === n) return; s.request.season = n; s.packTab = null; renderSeasons(bar); runSearch(false); },
      }));
    });
  }

  function runSearch(refresh) {
    var s = drawer.search;
    var list = s.list;
    if (s.searchController) s.searchController.abort();
    var controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    s.searchController = controller;
    removeRail();
    if (s.packSlot) s.packSlot.innerHTML = '';
    skeleton(list, 4);
    list.insertBefore(el('div', { class: 'ff-hint', text: refresh ? T('正在重新搜索…') : T('正在搜索资源…') }), list.firstChild);
    s.request.refresh = refresh;
    var mine = s;
    var season = s.request.season;
    var retried = false;

    function current() {
      return !(controller && controller.signal.aborted) && drawer && drawer.search === mine && mine.request.season === season;
    }

    // 两步搜索：先拿优选结果马上展示；还不够时再异步请求降级结果，回来后整体重绘
    function fetchFallback() {
      var body = Object.assign({}, s.request, { stage: 'fallback' });
      api('POST', 'search', body, { signal: controller ? controller.signal : undefined }).then(function (r) {
        if (!current()) return;
        mine.result = r;
        if (drawer.view === 'search') renderReleases(r);
      }).catch(function (e) {
        if (e && e.name === 'AbortError') return;
        if (!current()) return;
        var hint = mine.list.querySelector('.ff-more');
        if (hint) hint.textContent = T('补充搜索失败，可点右上角刷新重试');
      });
    }

    function fetchSearch() {
      var body = Object.assign({}, s.request, { stage: 'preferred' });
      api('POST', 'search', body, { signal: controller ? controller.signal : undefined }).then(function (r) {
        if (!current()) return;
        mine.result = r;
        if (drawer.view === 'search') {
          if (r.needsFallback && !r.releases.length) {
            skeleton(list, 4);
            list.insertBefore(el('div', { class: 'ff-hint ff-more', text: T('没有找到优选资源，正在扩大搜索范围…') }), list.firstChild);
          } else {
            renderReleases(r);
            if (r.needsFallback) list.insertBefore(el('div', { class: 'ff-hint ff-more', text: T('正在补充更多资源…') }), list.firstChild);
          }
        }
        if (r.needsFallback) fetchFallback();
      }).catch(function (e) {
        if (e && e.name === 'AbortError') return;
        if (controller && controller.signal.aborted) return;
        // fetch 的网络层失败没有 HTTP status；切季/重启时偶发。搜索是只读请求，自动重试一次。
        if (!e.status && !retried) {
          retried = true;
          if (!drawer || drawer.search !== mine || mine.request.season !== season || mine.searchController !== controller) return;
          skeleton(list, 4);
          list.insertBefore(el('div', { class: 'ff-hint', text: T('网络连接中断，正在重试…') }), list.firstChild);
          setTimeout(fetchSearch, 1200);
          return;
        }
        if (drawer && drawer.search === mine && drawer.view === 'search') {
          message(list, e.status ? e.message : T('网络连接中断，请检查 Jellyfin 服务后重试'), 'ff-error');
        }
      });
    }

    fetchSearch();
  }

  // 版本标签：优选、分辨率、HDR、编码等
  function chips(t, preferred) {
    var main = resLabel(t.resolution);
    return [preferred ? el('span', { class: 'ff-chip ff-chip-pub', text: T('优选') }) : null, main ? el('span', { class: 'ff-chip ff-chip-hi', text: main }) : null]
      .concat([t.hdr, t.codec, t.mediaSource, t.audio, t.subtitles].filter(Boolean).map(function (c) { return el('span', { class: 'ff-chip', text: T.chip(c) }); }));
  }

  function renderReleases(r) {
    var s = drawer.search;
    var list = s.list;
    list.innerHTML = '';
    removeRail();
    if (s.packSlot) s.packSlot.innerHTML = '';
    if (r.sourceErrors) list.appendChild(el('div', { class: 'ff-note ff-error', text: T('部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新') }));
    if (!r.releases.length) {
      emptyState(list.appendChild(el('div')), 'search_off', T('没有找到合适的资源'), el('div', { class: 'ff-dim ff-small', text: T('可以稍后刷新再试，或换一季') }));
      return;
    }

    // 电影：直接列出全部版本
    if (r.query.kind !== 'Series') {
      list.appendChild(el('div', { class: 'ff-hint', text: T('共 {n} 个版本', { n: r.releases.length }) + (r.fromCache ? T('（缓存结果，可点右上角刷新）') : '') }));
      var box = list.appendChild(el('div', { class: 'ff-rlist' }));
      r.releases.forEach(function (rel, idx) { box.appendChild(releaseCard(r, rel, idx)); });
      return;
    }

    // 剧集：按 整季 / 多集 / 单集 分开展示，季选择右侧的下拉框切换
    var groups = { full: [], multi: [], single: [] };
    r.releases.forEach(function (x) { groups[packType(x)].push(x); });
    var byEpisode = function (x, y) {
      var a = x.coverage, b = y.coverage;
      return (a.from || 0) - (b.from || 0) || ((a.to || 0) - (a.from || 0)) - ((b.to || 0) - (b.from || 0))
        || (y.preferred ? 1 : 0) - (x.preferred ? 1 : 0) || (x.demoted ? 1 : 0) - (y.demoted ? 1 : 0)
        || (y.traits.resolution || 0) - (x.traits.resolution || 0);
    };
    groups.multi.sort(byEpisode);
    groups.single.sort(byEpisode);
    if (!s.packTab || !groups[s.packTab].length) {
      s.packTab = ['full', 'multi', 'single'].filter(function (k) { return groups[k].length; })[0];
    }
    if (s.packSlot) {
      s.packSlot.innerHTML = '';
      s.packSlot.appendChild(packSelect(groups, s.packTab, function (key) { s.packTab = key; renderReleases(r); }));
    }
    var body = list.appendChild(el('div', { class: 'ff-rlist' }));

    if (s.packTab !== 'single') {
      groups[s.packTab].forEach(function (rel, idx) { body.appendChild(releaseCard(r, rel, idx)); });
      return;
    }

    // 单集：按集分组，右侧导航条悬停显示集数，点击跳到那一集
    var eps = [];
    var current = null;
    groups.single.forEach(function (rel, idx) {
      var e = episodesOf(rel)[0];
      if (!current || current.episode !== e) {
        current = { episode: e, count: 0, head: el('div', { class: 'ff-epgroup' }) };
        eps.push(current);
        body.appendChild(current.head);
      }
      current.count++;
      body.appendChild(releaseCard(r, rel, idx));
    });
    eps.forEach(function (g) { g.head.textContent = epLabel(g.episode) + ' · ' + T('{n} 个版本', { n: g.count }); });
    if (eps.length > 1) showRail(eps);
  }

  var PACK_TYPES = [{ key: 'full', label: '整季' }, { key: 'multi', label: '多集' }, { key: 'single', label: '单集' }];

  // 完整的一季才算整季；覆盖两集以上但不完整的算多集（如 第01-06集）
  function packType(rel) {
    var c = rel.coverage;
    if (!c || c.fullSeason) return 'full';
    return episodesOf(rel).length > 1 ? 'multi' : 'single';
  }

  function epLabel(e) { return e == null ? T('集数未知') : T('第 {n} 集', { n: e < 10 ? '0' + e : e }); }

  function packSelect(groups, value, onChange) {
    var current = PACK_TYPES.filter(function (t) { return t.key === value; })[0];
    var menu = el('div', { class: 'ff-pmenu', role: 'listbox' });
    var wrap = el('div', { class: 'ff-pselect' });
    var btn = el('button', { class: 'ff-pbtn', 'aria-haspopup': 'listbox', onclick: function (e) { e.stopPropagation(); toggle(!wrap.classList.contains('ff-open')); } }, [
      el('span', { text: T(current.label) }),
      icon('expand_more', 'ff-pcaret'),
    ]);
    PACK_TYPES.forEach(function (t) {
      var n = groups[t.key].length;
      var opt = menu.appendChild(el('button', {
        class: 'ff-popt' + (t.key === value ? ' ff-on' : ''), role: 'option',
        onclick: function (e) { e.stopPropagation(); toggle(false); if (t.key !== value) onChange(t.key); },
      }, [el('span', { text: T(t.label) })]));
      opt.disabled = !n;
    });
    wrap.appendChild(btn);
    wrap.appendChild(menu);
    function outside(e) { if (!wrap.contains(e.target)) toggle(false); }
    function toggle(open) {
      wrap.classList.toggle('ff-open', open);
      if (open) document.addEventListener('click', outside, true); else document.removeEventListener('click', outside, true);
    }
    return wrap;
  }

  // 单集导航条：每集一条短横线，当前所在集高亮；悬停时在导航条里展开集数，点击滚动到该集
  function showRail(eps) {
    removeRail();
    var d = drawer;
    var rail = el('div', { class: 'ff-rail' });
    var marks = rail.appendChild(el('div', { class: 'ff-rail-marks' }));
    eps.forEach(function (g, i) {
      g.mark = marks.appendChild(el('button', { class: 'ff-rail-mark', title: epLabel(g.episode), onclick: function () { jump(i); } }, [
        el('span', { class: 'ff-rail-label', text: epLabel(g.episode) }),
        el('span', { class: 'ff-rail-dash' }),
      ]));
    });
    function jump(i) {
      var top = eps[i].head.getBoundingClientRect().top - d.body.getBoundingClientRect().top + d.body.scrollTop - 8;
      d.body.scrollTo({ top: top, behavior: 'smooth' });
    }
    function sync() {
      var edge = d.body.getBoundingClientRect().top + 24;
      var active = 0;
      eps.forEach(function (g, i) { if (g.head.getBoundingClientRect().top <= edge) active = i; });
      if (d.body.scrollTop + d.body.clientHeight >= d.body.scrollHeight - 2) active = eps.length - 1;
      eps.forEach(function (g, i) { g.mark.classList.toggle('ff-on', i === active); });
    }
    d.panel.appendChild(rail);
    d.body.classList.add('ff-has-rail');
    d.body.addEventListener('scroll', sync, { passive: true });
    d.rail = { el: rail, sync: sync };
    sync();
  }

  function removeRail() {
    var d = drawer;
    if (!d || !d.rail) return;
    d.body.removeEventListener('scroll', d.rail.sync);
    d.body.classList.remove('ff-has-rail');
    d.rail.el.remove();
    d.rail = null;
  }

  function episodesOf(rel) {
    var c = rel.coverage;
    if (!c) return [];
    if (c.episodes && c.episodes.length) return c.episodes;
    var list = [];
    if (c.from != null) for (var e = c.from; e <= (c.to || c.from); e++) list.push(e);
    return list;
  }

  function releaseCard(r, rel, idx) {
    var btn = el('button', { class: 'ff-btn ff-dl-btn' }, [icon('download'), el('span', { text: T('下载') })]);
    if (rel.__sent) markSent(btn);
    btn.addEventListener('click', function () {
      if (rel.__sent) return;
      btn.disabled = true; btn.lastChild.textContent = T('提交中…');
      api('POST', 'tasks', { query: r.query, releaseId: rel.id }).then(function (res) {
        rel.__sent = true;
        markSent(btn);
        toast(T('已交给迅雷'), { label: T('查看下载任务'), run: showTasks });
        api('GET', 'tasks').then(function (tasks) { if (drawer) { drawer.cache.tasks = tasks; refreshTaskCount(); } }).catch(function () {});
      }).catch(function (e) {
        btn.disabled = false; btn.lastChild.textContent = T('下载');
        toast(e.message, null, true);
      });
    });
    return el('div', { class: 'ff-rcard', style: 'animation-delay:' + Math.min(idx, 10) * 30 + 'ms' }, [
      el('div', { class: 'ff-rmain' }, [
        el('div', { class: 'ff-rname', text: T.release(rel.displayName) }),
        el('div', { class: 'ff-meta' }, chips(rel.traits, rel.preferred).concat([
          rel.fileCount > 1 ? el('span', { class: 'ff-dim', text: T('{n} 个文件', { n: rel.fileCount }) }) : null,
          el('span', { class: 'ff-dim', text: gb(rel.sizeBytes) }),
          el('span', { class: 'ff-dim', text: date(rel.createdAt) }),
        ])),
      ]),
      btn,
    ]);
  }

  function markSent(btn) {
    btn.disabled = true;
    btn.classList.add('ff-sent');
    btn.firstChild.textContent = 'check';
    btn.lastChild.textContent = T('已交给迅雷');
  }

  // ---------- 下载任务 ----------
  var stateText = { Submitted: '等待迅雷开始', Downloading: '下载中', Stalled: '停滞', Archiving: '入库中', Done: '已入库', Failed: '失败' };
  var stateIcon = { Submitted: 'schedule', Downloading: 'downloading', Stalled: 'error_outline', Archiving: 'drive_file_move', Done: 'check_circle', Failed: 'cancel' };

  function showTasks() {
    openDrawer();
    var content = switchView('tasks', T('下载任务'), drawer.search ? drawer.search.label : 'FireFly');
    var list = el('div', { class: 'ff-tlist' });
    content.appendChild(list);
    var cards = {};
    if (drawer.cache.tasks) render(drawer.cache.tasks); else skeleton(list, 3);

    function load() {
      api('GET', 'tasks').then(function (tasks) {
        if (!drawer || drawer.view !== 'tasks') return;
        drawer.cache.tasks = tasks;
        refreshTaskCount();
        render(tasks);
      }).catch(function (e) { if (drawer && drawer.view === 'tasks') message(list, e.message, 'ff-error'); });
    }

    // 按任务 ID 原地更新卡片：进度条平滑前进，不整块重绘、不闪烁
    function render(tasks) {
      if (!tasks.length) { cards = {}; emptyState(list, 'cloud_download', T('还没有下载任务'), el('div', { class: 'ff-dim ff-small', text: T('在详情页点"下载"选一个版本，就会出现在这里') })); return; }
      if (list.querySelector('.ff-empty, .ff-skel, .ff-msg')) list.innerHTML = '';
      var seen = {};
      tasks.forEach(function (t, idx) {
        seen[t.id] = true;
        var c = cards[t.id] || (cards[t.id] = taskCard(t));
        updateTask(c, t);
        if (list.children[idx] !== c.node) list.insertBefore(c.node, list.children[idx] || null);
      });
      Object.keys(cards).forEach(function (id) {
        if (!seen[id]) { var n = cards[id].node; delete cards[id]; n.classList.add('ff-leave'); setTimeout(function () { n.remove(); }, 250); }
      });
    }

    function taskCard(t) {
      var q = t.query;
      var c = {};
      c.badge = el('span', { class: 'ff-state' });
      c.bar = el('div', { class: 'ff-progress-bar' });
      c.stats = el('span', { class: 'ff-dim' });
      c.speed = el('span', { class: 'ff-speed' });
      c.msg = el('div', { class: 'ff-tmsg' });
      c.remove = el('button', { class: 'ff-icon-btn ff-tremove', title: T('移除任务（不删除已下载的文件）'), 'aria-label': T('移除'), onclick: function () {
        c.node.classList.add('ff-leave');
        api('POST', 'tasks/' + t.id + '/dismiss').then(load).catch(function (e) { c.node.classList.remove('ff-leave'); toast(e.message, null, true); });
      } }, [icon('close')]);
      c.node = el('div', { class: 'ff-tcard' }, [
        el('div', { class: 'ff-thead' }, [
          el('div', { class: 'ff-tname', text: q.title + (q.year ? ' (' + q.year + ')' : ''), title: T.release(t.displayName) || '' }),
          q.season ? el('span', { class: 'ff-chip', text: T('第 {n} 季', { n: q.season }) }) : null,
          c.remove,
        ]),
        el('div', { class: 'ff-trow' }, [c.badge, c.stats, c.speed]),
        el('div', { class: 'ff-progress' }, [c.bar]),
        c.msg,
      ]);
      return c;
    }

    function updateTask(c, t) {
      var pct = t.state === 'Done' ? 100 : t.bytesTotal ? Math.min(100, Math.floor(t.bytesDone * 100 / t.bytesTotal)) : 0;
      c.node.className = 'ff-tcard ff-' + t.state.toLowerCase();
      c.badge.innerHTML = '';
      c.badge.appendChild(icon(stateIcon[t.state] || 'info'));
      c.badge.appendChild(document.createTextNode(stateText[t.state] ? T(stateText[t.state]) : t.state));
      c.stats.textContent = t.state === 'Done' ? gb(t.bytesTotal) : gb(t.bytesDone) + ' / ' + gb(t.bytesTotal) + ' · ' + pct + '%';
      c.speed.textContent = t.state === 'Downloading' ? speed(t.speedBps) : '';
      c.bar.style.width = pct + '%';
      c.msg.textContent = T.server(t.message) || '';
      c.msg.style.display = t.message ? '' : 'none';
      c.remove.style.visibility = t.state === 'Archiving' ? 'hidden' : '';
    }

    load();
    pollTimer = setInterval(load, 5000);
  }

  // ---------- 热门榜单（整页） ----------

  F.drawerOpen = function () { return !!drawer; };
  F.emptyState = emptyState;
  F.icon = icon;
  F.message = message;
  F.openSearch = openSearch;
  F.showTasks = showTasks;
  F.toast = toast;
})();
