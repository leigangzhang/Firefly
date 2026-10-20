/* 萤火虫 FireFly：注入 Jellyfin 网页的前端。详情页"获取资源" + 顶栏"热门榜单"（含资源详情页） + 任务列表。仅管理员可见。 */
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

  function api(method, path, body, options) {
    var client = window.ApiClient;
    var headers = { 'Authorization': 'MediaBrowser Token="' + client.accessToken() + '"' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    options = options || {};
    return fetch(client.getUrl('FireFly/' + path), {
      method: method, headers: headers, signal: options.signal,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
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

  // ---------- 侧边抽屉：获取资源 / 下载任务 ----------
  // 与榜单页、详情页同一套深色配色；从右侧滑入，切换"资源版本"和"下载任务"不重开抽屉。
  var drawer = null;
  var toastTimer = null;

  function openDrawer() {
    if (drawer) return drawer;
    var tabs = el('div', { class: 'ff-tabs', role: 'tablist' });
    var body = el('div', { class: 'ff-dbody' });
    var title = el('div', { class: 'ff-dtitle' });
    var sub = el('div', { class: 'ff-dsub' });
    var quota = el('div', { class: 'ff-dquota' });
    var refresh = el('button', { class: 'ff-icon-btn', title: '重新搜索', 'aria-label': '重新搜索', style: 'display:none', onclick: function () { if (drawer && drawer.view === 'search') runSearch(true); } }, [icon('refresh')]);
    var backdrop = el('div', { class: 'ff-backdrop', onclick: closeDrawer });
    var panel = el('aside', { class: 'ff-drawer', role: 'dialog', 'aria-modal': 'true' }, [
      el('div', { class: 'ff-dhead' }, [
        el('div', { class: 'ff-dheading' }, [sub, title]),
        refresh,
        el('button', { class: 'ff-icon-btn', title: '关闭（Esc）', 'aria-label': '关闭', onclick: closeDrawer }, [icon('close')]),
      ]),
      tabs,
      body,
    ]);
    var root = el('div', { class: 'ff-drawer-root' }, [backdrop, panel]);
    document.body.appendChild(root);
    drawer = { root: root, panel: panel, tabs: tabs, body: body, title: title, sub: sub, quota: quota, refresh: refresh, view: null, search: null, cache: {} };
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
    if (d.search) list.push({ key: 'search', label: '资源版本' });
    list.push({ key: 'tasks', label: '下载任务' });
    list.forEach(function (t) {
      d.tabs.appendChild(el('button', {
        class: 'ff-tab' + (d.view === t.key ? ' ff-on' : ''), role: 'tab', 'data-tab': t.key,
        onclick: function () { if (d.view !== t.key) (t.key === 'tasks' ? showTasks : showSearchView)(); },
      }, [t.label, t.key === 'tasks' ? el('span', { class: 'ff-tab-count', 'data-count': '' }) : null]));
    });
    // 额度靠右放在标签行里，切到下载任务也一直可见
    d.tabs.appendChild(d.quota);
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
    var content = switchView('search', s.label, '获取资源');
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
      if (s.seasons) bar.appendChild(el('span', { class: 'ff-dim', text: '第 ' + s.seasons[0] + ' 季' }));
      return;
    }
    s.seasons.forEach(function (n) {
      bar.appendChild(el('button', {
        class: 'ff-fopt' + (n === s.request.season ? ' ff-on' : ''), text: '第 ' + n + ' 季',
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
    list.insertBefore(el('div', { class: 'ff-hint', text: refresh ? '正在重新搜索…' : '正在搜索资源…' }), list.firstChild);
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
        if (hint) hint.textContent = '补充搜索失败，可点右上角刷新重试';
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
            list.insertBefore(el('div', { class: 'ff-hint ff-more', text: '没有找到优选资源，正在扩大搜索范围…' }), list.firstChild);
          } else {
            renderReleases(r);
            if (r.needsFallback) list.insertBefore(el('div', { class: 'ff-hint ff-more', text: '正在补充更多资源…' }), list.firstChild);
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
          list.insertBefore(el('div', { class: 'ff-hint', text: '网络连接中断，正在重试…' }), list.firstChild);
          setTimeout(fetchSearch, 1200);
          return;
        }
        if (drawer && drawer.search === mine && drawer.view === 'search') {
          message(list, e.status ? e.message : '网络连接中断，请检查 Jellyfin 服务后重试', 'ff-error');
        }
      });
    }

    fetchSearch();
  }

  // 版本标签：优选、分辨率、HDR、编码等
  function chips(t, preferred) {
    var main = resLabel(t.resolution);
    return [preferred ? el('span', { class: 'ff-chip ff-chip-pub', text: '优选' }) : null, main ? el('span', { class: 'ff-chip ff-chip-hi', text: main }) : null]
      .concat([t.hdr, t.codec, t.mediaSource, t.audio, t.subtitles].filter(Boolean).map(function (c) { return el('span', { class: 'ff-chip', text: c }); }));
  }

  function quotaLine(q) {
    if (q.isVip) return el('div', { class: 'ff-quota' }, [icon('workspace_premium', 'ff-gold'), el('span', { text: q.licenseStatus })]);
    var pct = q.freeLimit ? Math.min(100, Math.round(q.used * 100 / q.freeLimit)) : 0;
    return el('div', { class: 'ff-quota' }, [
      el('span', { class: 'ff-quota-label', text: '本月免费额度' }),
      el('span', { class: 'ff-quota-short', text: '额度' }),
      el('div', { class: 'ff-meter' }, [el('div', { class: 'ff-meter-bar' + (pct >= 100 ? ' ff-full' : ''), style: 'width:' + pct + '%' })]),
      el('span', { class: 'ff-dim', text: q.used + ' / ' + q.freeLimit }),
    ]);
  }

  function renderReleases(r) {
    var s = drawer.search;
    var list = s.list;
    list.innerHTML = '';
    removeRail();
    if (s.packSlot) s.packSlot.innerHTML = '';
    drawer.quota.innerHTML = '';
    if (r.quota) drawer.quota.appendChild(quotaLine(r.quota));
    if (quotaFull(r.quota) && r.releases.length) {
      list.appendChild(el('div', { class: 'ff-note ff-quota-note' }, [icon(r.quota.counted ? 'info' : 'lock', 'ff-note-icon'), el('span', {
        text: r.quota.counted
          ? '本月免费额度已用完。' + (r.query.kind === 'Series' ? '这一季' : '这部片') + '本月已计过额度，仍可以下载它的其他版本。'
          : '本月免费额度（' + r.quota.freeLimit + ' 部）已用完，开通 VIP 后可以继续下载。',
      })]));
    }
    if (r.sourceErrors) list.appendChild(el('div', { class: 'ff-note ff-error', text: '部分资源源暂时不可用，结果可能不全，稍后可点右上角刷新' }));
    if (!r.releases.length) {
      emptyState(list.appendChild(el('div')), 'search_off', '没有找到合适的资源', el('div', { class: 'ff-dim ff-small', text: '可以稍后刷新再试，或换一季' }));
      return;
    }

    // 电影：直接列出全部版本
    if (r.query.kind !== 'Series') {
      list.appendChild(el('div', { class: 'ff-hint', text: '共 ' + r.releases.length + ' 个版本' + (r.fromCache ? '（缓存结果，可点右上角刷新）' : '') }));
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
    eps.forEach(function (g) { g.head.textContent = epLabel(g.episode) + ' · ' + g.count + ' 个版本'; });
    if (eps.length > 1) showRail(eps);
  }

  var PACK_TYPES = [{ key: 'full', label: '整季' }, { key: 'multi', label: '多集' }, { key: 'single', label: '单集' }];

  // 完整的一季才算整季；覆盖两集以上但不完整的算多集（如 第01-06集）
  function packType(rel) {
    var c = rel.coverage;
    if (!c || c.fullSeason) return 'full';
    return episodesOf(rel).length > 1 ? 'multi' : 'single';
  }

  function epLabel(e) { return e == null ? '集数未知' : '第 ' + (e < 10 ? '0' + e : e) + ' 集'; }

  function packSelect(groups, value, onChange) {
    var current = PACK_TYPES.filter(function (t) { return t.key === value; })[0];
    var menu = el('div', { class: 'ff-pmenu', role: 'listbox' });
    var wrap = el('div', { class: 'ff-pselect' });
    var btn = el('button', { class: 'ff-pbtn', 'aria-haspopup': 'listbox', onclick: function (e) { e.stopPropagation(); toggle(!wrap.classList.contains('ff-open')); } }, [
      el('span', { text: current.label }),
      icon('expand_more', 'ff-pcaret'),
    ]);
    PACK_TYPES.forEach(function (t) {
      var n = groups[t.key].length;
      var opt = menu.appendChild(el('button', {
        class: 'ff-popt' + (t.key === value ? ' ff-on' : ''), role: 'option',
        onclick: function (e) { e.stopPropagation(); toggle(false); if (t.key !== value) onChange(t.key); },
      }, [el('span', { text: t.label })]));
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

  // 额度用满且这部片 / 这一季本月还没计过数时，不能再下载（服务端同样会拒绝）
  function quotaFull(q) { return !!q && !q.isVip && q.used >= q.freeLimit; }
  function quotaBlocks(q) { return quotaFull(q) && !q.counted; }

  function quotaExhaustedText(q) { return '本月免费额度（' + q.freeLimit + ' 部）已用完'; }

  function releaseCard(r, rel, idx) {
    var locked = !rel.__sent && quotaBlocks(r.quota);
    var btn = el('button', { class: 'ff-btn ff-dl-btn' + (locked ? ' ff-locked' : ''), title: locked ? quotaExhaustedText(r.quota) : '' },
      [icon(locked ? 'lock' : 'download'), el('span', { text: locked ? '额度已用完' : '下载' })]);
    if (rel.__sent) markSent(btn);
    btn.addEventListener('click', function () {
      if (rel.__sent) return;
      if (quotaBlocks(r.quota)) { showVip(quotaExhaustedText(r.quota)); return; }
      btn.disabled = true; btn.lastChild.textContent = '提交中…';
      api('POST', 'tasks', { query: r.query, releaseId: rel.id }).then(function (res) {
        rel.__sent = true;
        markSent(btn);
        if (res && res.quota) updateQuota(r, res.quota);
        toast('已交给迅雷', { label: '查看下载任务', run: showTasks });
        api('GET', 'tasks').then(function (tasks) { if (drawer) { drawer.cache.tasks = tasks; refreshTaskCount(); } }).catch(function () {});
      }).catch(function (e) {
        btn.disabled = false; btn.lastChild.textContent = '下载';
        if (e.status === 402) {
          if (e.data && e.data.quota) updateQuota(r, e.data.quota);
          showVip(e.message);
        } else {
          toast(e.message, null, true);
        }
      });
    });
    return el('div', { class: 'ff-rcard', style: 'animation-delay:' + Math.min(idx, 10) * 30 + 'ms' }, [
      el('div', { class: 'ff-rmain' }, [
        el('div', { class: 'ff-rname', text: rel.displayName }),
        el('div', { class: 'ff-meta' }, chips(rel.traits, rel.preferred).concat([
          rel.fileCount > 1 ? el('span', { class: 'ff-dim', text: rel.fileCount + ' 个文件' }) : null,
          el('span', { class: 'ff-dim', text: gb(rel.sizeBytes) }),
          el('span', { class: 'ff-dim', text: date(rel.createdAt) }),
        ])),
      ]),
      btn,
    ]);
  }

  // 提交成功或被拒后用服务端返回的额度刷新右上角的计数
  function updateQuota(r, q) {
    r.quota = q;
    if (drawer && drawer.search && drawer.search.result === r) {
      drawer.quota.innerHTML = '';
      drawer.quota.appendChild(quotaLine(q));
    }
  }

  function markSent(btn) {
    btn.disabled = true;
    btn.classList.add('ff-sent');
    btn.firstChild.textContent = 'check';
    btn.lastChild.textContent = '已交给迅雷';
  }

  function showVip(text) {
    openDrawer();
    var content = switchView('vip', '开通 VIP', '本月免费额度已用完');
    content.appendChild(el('div', { class: 'ff-vip' }, [
      icon('workspace_premium', 'ff-vip-icon'),
      el('div', { class: 'ff-vip-title', text: text }),
      el('p', { text: '开通 VIP 后不限部数。' }),
      el('p', { class: 'ff-dim', text: '购买后会得到一串授权码，粘贴到 控制台 → 插件 → FireFly 的"授权码"一栏并保存即可。' }),
      drawer.search ? el('button', { class: 'ff-btn ff-ghost', text: '返回资源版本', onclick: showSearchView }) : null,
    ]));
  }

  // ---------- 下载任务 ----------
  var stateText = { Submitted: '等待迅雷开始', Downloading: '下载中', Stalled: '停滞', Archiving: '入库中', Done: '已入库', Failed: '失败' };
  var stateIcon = { Submitted: 'schedule', Downloading: 'downloading', Stalled: 'error_outline', Archiving: 'drive_file_move', Done: 'check_circle', Failed: 'cancel' };

  function showTasks() {
    openDrawer();
    var content = switchView('tasks', '下载任务', drawer.search ? drawer.search.label : 'FireFly');
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
      if (!tasks.length) { cards = {}; emptyState(list, 'cloud_download', '还没有下载任务', el('div', { class: 'ff-dim ff-small', text: '在详情页点"下载"选一个版本，就会出现在这里' })); return; }
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
      c.remove = el('button', { class: 'ff-icon-btn ff-tremove', title: '移除任务（不删除已下载的文件）', 'aria-label': '移除', onclick: function () {
        c.node.classList.add('ff-leave');
        api('POST', 'tasks/' + t.id + '/dismiss').then(load).catch(function (e) { c.node.classList.remove('ff-leave'); toast(e.message, null, true); });
      } }, [icon('close')]);
      c.node = el('div', { class: 'ff-tcard' }, [
        el('div', { class: 'ff-thead' }, [
          el('div', { class: 'ff-tname', text: q.title + (q.year ? ' (' + q.year + ')' : ''), title: t.displayName || '' }),
          q.season ? el('span', { class: 'ff-chip', text: '第 ' + q.season + ' 季' }) : null,
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
      c.badge.appendChild(document.createTextNode(stateText[t.state] || t.state));
      c.stats.textContent = t.state === 'Done' ? gb(t.bytesTotal) : gb(t.bytesDone) + ' / ' + gb(t.bytesTotal) + ' · ' + pct + '%';
      c.speed.textContent = t.state === 'Downloading' ? speed(t.speedBps) : '';
      c.bar.style.width = pct + '%';
      c.msg.textContent = t.message || '';
      c.msg.style.display = t.message ? '' : 'none';
      c.remove.style.visibility = t.state === 'Archiving' ? 'hidden' : '';
    }

    load();
    pollTimer = setInterval(load, 5000);
  }

  // ---------- 热门榜单（整页） ----------
  var page = null;
  var DEFAULT_SORT = 'hot';
  var hot = { state: { category: 'movie', sort: DEFAULT_SORT, page: 1 }, genres: {}, totalPages: 1, loading: false, view: null, rows: null };

  function closePage() {
    srch.q = '';
    if (page) { page.root.remove(); page = null; document.removeEventListener('keydown', onPageKey); }
  }
  function onPageKey(e) {
    if (e.key !== 'Escape' || drawer) return;
    if (page && page.detail.style.display !== 'none') showList(); else closePage();
  }

  function openHot() {
    if (page) return;
    var back = el('button', { class: 'ff-btn ff-ghost ff-back', text: '← 返回榜单', onclick: showList });
    var list = el('div', { class: 'ff-view' });
    var detail = el('div', { class: 'ff-view', style: 'display:none' });
    var input = el('input', { class: 'ff-search-input', type: 'search', placeholder: '全局搜索电影、剧集', 'aria-label': '全局搜索电影、剧集', autocomplete: 'off' });
    var clear = el('button', { class: 'ff-search-clear', title: '清除', 'aria-label': '清除搜索', onclick: function () { input.value = ''; endSearch(); input.focus(); } }, [icon('close')]);
    var searchBox = el('div', { class: 'ff-search' }, [icon('search', 'ff-search-icon'), input, clear]);
    var root = el('div', { class: 'ff-page' }, [
      el('div', { class: 'ff-page-head' }, [
        back,
        el('span', { class: 'ff-title', text: '萤火虫 · 热门榜单' }),
        searchBox,
        el('button', { class: 'ff-btn ff-ghost', text: '我的收藏', onclick: showFavorites }),
        el('button', { class: 'ff-btn ff-ghost', text: '下载任务', onclick: showTasks }),
        el('button', { class: 'ff-btn ff-ghost', text: '✕', title: '关闭（Esc）', onclick: closePage }),
      ]),
      el('div', { class: 'ff-page-body' }, [list, detail]),
    ]);
    document.body.appendChild(root);
    document.addEventListener('keydown', onPageKey);
    page = { root: root, list: list, detail: detail, back: back, scroll: 0, input: input, searchBox: searchBox };
    back.style.display = 'none';
    buildList();
    var timer = null;
    input.addEventListener('input', function () {
      searchBox.classList.toggle('ff-has-text', !!input.value);
      clearTimeout(timer);
      timer = setTimeout(function () { input.value.trim() ? startSearch(input.value.trim()) : endSearch(); }, 450);
    });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { clearTimeout(timer); if (input.value.trim()) startSearch(input.value.trim()); }
      // 搜索框里的 Esc 只清空搜索，不关闭整页
      if (e.key === 'Escape' && input.value) { e.stopPropagation(); input.value = ''; searchBox.classList.remove('ff-has-text'); endSearch(); }
    });
  }

  // ---------- 榜单页搜索：结果替换榜单区域，清空后恢复原榜单和滚动位置 ----------
  var srch = { q: '', page: 1, totalPages: 1, loading: false, seq: 0, listScroll: 0 };

  function startSearch(q) {
    if (page.detail.style.display !== 'none') showList();
    var v = hot.view;
    if (!srch.q) srch.listScroll = scroller().scrollTop;
    if (srch.q === q && v.search.style.display !== 'none') return;
    srch.q = q;
    srch.page = 1;
    srch.totalPages = 1;
    v.filters.style.display = v.grid.style.display = v.more.style.display = 'none';
    v.search.style.display = '';
    v.sgrid.innerHTML = '';
    v.shead.innerHTML = '';
    v.shead.appendChild(el('span', { text: '正在搜索“' + q + '”…' }));
    scroller().scrollTop = 0;
    fetchSearch(1, ++srch.seq);
  }

  function endSearch() {
    if (!page || !srch.q) return;
    srch.q = '';
    srch.seq++;
    var v = hot.view;
    v.search.style.display = 'none';
    v.filters.style.display = v.grid.style.display = v.more.style.display = '';
    if (page.detail.style.display !== 'none') showList();
    scroller().scrollTop = srch.listScroll;
  }

  function fetchSearch(pageNo, seq) {
    var v = hot.view;
    srch.loading = true;
    v.smore.innerHTML = '';
    v.smore.appendChild(el('div', { class: 'ff-msg', text: '加载中…' }));
    api('GET', 'discover/search?q=' + encodeURIComponent(srch.q) + '&page=' + pageNo).then(function (r) {
      if (seq !== srch.seq) return;
      srch.loading = false;
      if (!r.configured) { message(v.smore, '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key'); return; }
      srch.page = r.page || pageNo;
      srch.totalPages = r.totalPages || 1;
      v.shead.innerHTML = '';
      v.shead.appendChild(el('span', { text: '“' + srch.q + '”的搜索结果' }));
      // 全局搜索：在全部电影和剧集里按片名找，不带分类、类型、地区等筛选条件
      v.shead.appendChild(el('span', { class: 'ff-dim', text: '全部电影和剧集' + (r.totalResults ? ' · 约 ' + r.totalResults + ' 条' : '') + '，不受筛选条件影响' }));
      v.shead.appendChild(el('button', { class: 'ff-btn ff-ghost', text: '返回榜单', onclick: function () { page.input.value = ''; page.searchBox.classList.remove('ff-has-text'); endSearch(); } }));
      r.items.forEach(function (i) { v.sgrid.appendChild(card(i, true)); });
      v.smore.innerHTML = '';
      if (!v.sgrid.children.length) emptyState(v.smore, 'search_off', '没有找到“' + srch.q + '”，换个片名或用原名试试');
      else if (srch.page < srch.totalPages) v.smore.appendChild(el('button', { class: 'ff-btn ff-ghost', text: '加载更多', onclick: loadMoreSearch }));
      else v.smore.appendChild(el('div', { class: 'ff-msg ff-dim', text: '已经到底了' }));
    }).catch(function (e) {
      if (seq !== srch.seq) return;
      srch.loading = false;
      message(v.smore, e.message, 'ff-error');
    });
  }

  function loadMoreSearch() {
    if (!srch.q || srch.loading || srch.page >= srch.totalPages) return;
    fetchSearch(srch.page + 1, srch.seq);
  }

  function scroller() { return page.root.querySelector('.ff-page-body'); }

  function showList() {
    page.detail.style.display = 'none';
    page.detail.innerHTML = '';
    page.list.style.display = '';
    page.back.style.display = 'none';
    scroller().scrollTop = page.scroll;
  }

  function buildList() {
    var filters = el('div', { class: 'ff-filters' });
    var grid = el('div', { class: 'ff-grid ff-hot-grid' });
    var more = el('div', { class: 'ff-more' });
    page.list.appendChild(filters);
    page.list.appendChild(grid);
    page.list.appendChild(more);
    var shead = el('div', { class: 'ff-search-head' });
    var sgrid = el('div', { class: 'ff-grid ff-hot-grid' });
    var smore = el('div', { class: 'ff-more' });
    var search = el('div', { class: 'ff-search-view', style: 'display:none' }, [shead, sgrid, smore]);
    page.list.appendChild(search);
    hot.view = { filters: filters, grid: grid, more: more, search: search, shead: shead, sgrid: sgrid, smore: smore };
    loadFilters();
    if ('IntersectionObserver' in window) {
      var io = new IntersectionObserver(function (entries) {
        if (!page || page.list.style.display === 'none') return;
        entries.forEach(function (e) {
          if (!e.isIntersecting) return;
          if (e.target === more && !srch.q) loadMore();
          if (e.target === smore && srch.q) loadMoreSearch();
        });
      }, { root: scroller(), rootMargin: '400px' });
      io.observe(more);
      io.observe(smore);
    }
  }

  function loadFilters() {
    var box = hot.view.filters;
    box.innerHTML = '';
    box.appendChild(el('div', { class: 'ff-msg', text: '加载筛选项…' }));
    api('GET', 'discover/filters?category=' + encodeURIComponent(hot.state.category)).then(function (r) {
      if (!r.configured) { message(box, '请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key'); hot.view.grid.innerHTML = ''; return; }
      hot.genres = r.genres || {};
      hot.rows = r.rows;
      // 换分类后，当前分类没有的选项（如剧集没有分级、出品列表不同）要清掉
      r.rows.forEach(function (row) {
        var v = hot.state[row.key];
        if (v && row.key !== 'category' && row.key !== 'sort' && !row.options.some(function (o) { return o.value === v; })) delete hot.state[row.key];
      });
      ['rating', 'company', 'genre'].forEach(function (k) { if (!r.rows.some(function (row) { return row.key === k; })) delete hot.state[k]; });
      renderFilters();
      reload();
    }).catch(function (e) { message(box, e.message, 'ff-error'); });
  }

  function renderFilters() {
    var box = hot.view.filters;
    box.innerHTML = '';
    hot.rows.forEach(function (row) {
      var isCat = row.key === 'category';
      var opts = el('div', { class: 'ff-fopts' });
      var all = isCat || row.key === 'sort' ? [] : [{ value: '', label: '全部' }];
      all.concat(row.options).forEach(function (o) {
        var active = (hot.state[row.key] || '') === o.value;
        opts.appendChild(el('button', {
          class: 'ff-fopt' + (active ? ' ff-on' : ''), text: o.label,
          onclick: function () {
            if (o.value) hot.state[row.key] = o.value; else delete hot.state[row.key];
            if (isCat) { hot.state = { category: o.value, sort: DEFAULT_SORT, region: hot.state.region, time: hot.state.time }; loadFilters(); return; }
            renderFilters();
            reload();
          },
        }));
      });
      box.appendChild(el('div', { class: 'ff-frow' + (isCat ? ' ff-frow-cat' : '') }, [isCat ? null : el('span', { class: 'ff-flabel', text: row.label }), opts]));
    });
  }

  function query(pageNo) {
    var q = [];
    Object.keys(hot.state).forEach(function (k) { if (hot.state[k] && k !== 'page') q.push(k + '=' + encodeURIComponent(hot.state[k])); });
    q.push('page=' + pageNo);
    return 'discover?' + q.join('&');
  }

  var hotSeq = 0;
  function reload() {
    hot.state.page = 1;
    hot.totalPages = 1;
    hot.view.grid.innerHTML = '';
    fetchPage(1, ++hotSeq);
  }
  function loadMore() {
    if (hot.loading || hot.state.page >= hot.totalPages) return;
    fetchPage(hot.state.page + 1, hotSeq);
  }

  function fetchPage(pageNo, seq) {
    var more = hot.view.more;
    hot.loading = true;
    more.innerHTML = '';
    more.appendChild(el('div', { class: 'ff-msg', text: '加载中…' }));
    api('GET', query(pageNo)).then(function (r) {
      if (seq !== hotSeq) return;
      hot.loading = false;
      hot.state.page = r.page || pageNo;
      hot.totalPages = r.totalPages || 1;
      r.items.forEach(function (i) { hot.view.grid.appendChild(card(i)); });
      more.innerHTML = '';
      if (!hot.view.grid.children.length) more.appendChild(el('div', { class: 'ff-msg', text: '没有符合条件的影片，换个筛选试试' }));
      else if (hot.state.page < hot.totalPages) more.appendChild(el('button', { class: 'ff-btn ff-ghost', text: '加载更多', onclick: loadMore }));
      else more.appendChild(el('div', { class: 'ff-msg ff-dim', text: '已经到底了' }));
    }).catch(function (e) {
      if (seq !== hotSeq) return;
      hot.loading = false;
      message(more, e.message, 'ff-error');
    });
  }

  function ratingBadge(rating) {
    if (!rating) return null;
    var cls = rating >= 7.5 ? 'ff-rate-hi' : rating >= 6 ? 'ff-rate-mid' : 'ff-rate-lo';
    return el('span', { class: 'ff-rate ' + cls, text: Number(rating).toFixed(1) });
  }

  function card(i, mixed) {
    // 搜索结果电影、剧集混排：副标题标出类型，不用当前分类的类型表
    var genres = mixed ? [i.kind === 'Series' ? '剧集' : '电影'] : (i.genreIds || []).map(function (g) { return hot.genres[g]; }).filter(Boolean).slice(0, 2);
    var sub = [i.year, genres.join(' / ')].filter(Boolean).join(' · ');
    return el('div', { class: 'ff-card', onclick: function () { openDetail(i.kind, i.tmdbId); } }, [
      el('div', { class: 'ff-poster' }, [
        i.posterUrl ? el('img', { src: i.posterUrl, loading: 'lazy', alt: i.title }) : el('div', { class: 'ff-noposter' }),
        ratingBadge(i.rating),
        i.inLibrary ? el('span', { class: 'ff-chip ff-in', text: '已在库' }) : null,
      ]),
      el('div', { class: 'ff-card-title', text: i.title, title: i.originalTitle ? i.title + ' / ' + i.originalTitle : i.title }),
      sub ? el('div', { class: 'ff-card-sub', text: sub }) : null,
    ]);
  }

  function showFavorites() {
    if (!page) openHot();
    var body = beginDetail();
    body.appendChild(el('h2', { class: 'ff-h2', text: '我的收藏' }));
    var grid = el('div', { class: 'ff-grid ff-hot-grid' });
    body.appendChild(grid);
    api('GET', 'favorites').then(function (items) {
      if (!items.length) { grid.appendChild(el('div', { class: 'ff-msg', text: '还没有收藏。在详情页点"收藏"即可加入这里。' })); return; }
      items.forEach(function (i) { grid.appendChild(card(i)); });
    }).catch(function (e) { message(body, e.message, 'ff-error'); });
  }

  // ---------- 资源详情页 ----------
  var LANG = { zh: '汉语', cn: '粤语', en: '英语', ja: '日语', ko: '韩语', fr: '法语', de: '德语', es: '西班牙语', it: '意大利语', ru: '俄语', th: '泰语', hi: '印地语', pt: '葡萄牙语' };
  var REGION = { CN: '中国大陆', HK: '中国香港', TW: '中国台湾', US: '美国', GB: '英国', JP: '日本', KR: '韩国', FR: '法国', DE: '德国', TH: '泰国', IN: '印度', ES: '西班牙', IT: '意大利', CA: '加拿大', AU: '澳大利亚' };
  var STATUS = { Released: '已上映', 'Post Production': '后期制作', 'In Production': '制作中', Planned: '筹备中', Rumored: '传闻', Canceled: '已取消', 'Returning Series': '更新中', Ended: '已完结', Pilot: '试播' };

  function beginDetail() {
    if (page.list.style.display !== 'none') page.scroll = scroller().scrollTop;
    page.list.style.display = 'none';
    page.detail.innerHTML = '';
    page.detail.style.display = '';
    page.back.style.display = '';
    scroller().scrollTop = 0;
    return page.detail;
  }

  function openDetail(kind, tmdbId) {
    if (!page) openHot();
    var body = beginDetail();
    var seg = kind === 'Series' ? 'tv' : 'movie';
    body.appendChild(el('div', { class: 'ff-msg', text: '加载详情…' }));
    api('GET', 'detail/' + seg + '/' + encodeURIComponent(tmdbId)).then(function (r) {
      if (page.detail !== body || body.style.display === 'none') return;
      body.innerHTML = '';
      renderDetail(body, r.detail, r);
    }).catch(function (e) { message(body, e.message, 'ff-error'); });
  }

  function money(n) { return n ? '$' + Number(n).toLocaleString('en-US') : null; }
  function minutes(m) { return m ? (m >= 60 ? Math.floor(m / 60) + '小时' + (m % 60 ? (m % 60) + '分钟' : '') : m + '分钟') : null; }

  function scoreRing(rating) {
    var pct = Math.round((rating || 0) * 10);
    var color = pct >= 70 ? '#21d07a' : pct >= 40 ? '#d2d531' : '#db2360';
    return el('div', { class: 'ff-ring', style: 'background:conic-gradient(' + color + ' ' + pct + '%, #204529 0)' }, [
      el('div', { class: 'ff-ring-in' }, [rating ? pct + '' : 'NR', rating ? el('sup', { text: '%' }) : null]),
    ]);
  }

  function renderDetail(body, d, meta) {
    var isTv = d.kind === 'Series';
    var label = d.title + (d.year ? ' (' + d.year + ')' : '');
    var favBtn = el('button', { class: 'ff-btn ff-ghost ff-fav' });
    var favorite = !!meta.favorite;
    function paintFav() { favBtn.textContent = favorite ? '♥ 已收藏' : '♡ 收藏'; favBtn.classList.toggle('ff-fav-on', favorite); }
    paintFav();
    favBtn.addEventListener('click', function () {
      favBtn.disabled = true;
      var req = favorite
        ? api('DELETE', 'favorites/' + (isTv ? 'tv' : 'movie') + '/' + d.tmdbId)
        : api('POST', 'favorites', { kind: d.kind, tmdbId: d.tmdbId, title: d.title, originalTitle: d.originalTitle, year: d.year, posterUrl: d.posterUrl, rating: d.rating });
      req.then(function (r) { favorite = r.favorite; paintFav(); })
        .catch(function (e) { toast(e.message, null, true); })
        .then(function () { favBtn.disabled = false; });
    });
    var download = el('button', { class: 'ff-btn', text: '⬇ 下载', onclick: function () {
      openSearch({ title: d.title, originalTitle: d.originalTitle, year: d.year, kind: d.kind, tmdbId: d.tmdbId }, d.title);
    } });
    var trailerBtn = d.videos.length ? el('button', { class: 'ff-btn ff-ghost', text: '▶ 播放预告片', onclick: function () {
      var t = body.querySelector('.ff-trailer');
      if (t) { t.scrollIntoView({ behavior: 'smooth', block: 'center' }); play(d.videos[0]); }
    } }) : null;

    var facts = [d.certification ? el('span', { class: 'ff-cert', text: d.certification }) : null,
      el('span', { text: [d.releaseDate, d.genres.join(' / '), minutes(d.runtime)].filter(Boolean).join(' · ') })];

    body.appendChild(el('div', { class: 'ff-hero', style: d.backdropUrl ? 'background-image:url("' + d.backdropUrl + '")' : '' }, [
      el('div', { class: 'ff-hero-in' }, [
        el('div', { class: 'ff-hero-poster' }, [d.posterUrl ? el('img', { src: d.posterUrl, alt: d.title }) : el('div', { class: 'ff-noposter' })]),
        el('div', { class: 'ff-hero-info' }, [
          el('h1', { class: 'ff-h1' }, [d.title, d.year ? el('span', { class: 'ff-year', text: ' (' + d.year + ')' }) : null]),
          d.originalTitle ? el('div', { class: 'ff-dim', text: d.originalTitle }) : null,
          el('div', { class: 'ff-facts' }, facts),
          el('div', { class: 'ff-actions' }, [
            scoreRing(d.rating), el('span', { class: 'ff-score-label', text: '用户评分' + (d.voteCount ? '（' + d.voteCount + '人）' : '') }),
            favBtn, download, trailerBtn,
            meta.inLibrary ? el('span', { class: 'ff-chip ff-in-static', text: '已在库' }) : null,
          ]),
          d.tagline ? el('div', { class: 'ff-tagline', text: d.tagline }) : null,
          d.directors.length ? el('div', { class: 'ff-crew' }, d.directors.map(function (p) {
            return el('div', { class: 'ff-crew-item' }, [el('div', { class: 'ff-crew-name', text: p.name }), el('div', { class: 'ff-dim', text: p.role })]);
          })) : null,
        ]),
      ]),
    ]));

    var main = el('div', { class: 'ff-main-col' });
    var side = el('aside', { class: 'ff-side' });
    body.appendChild(el('div', { class: 'ff-cols' }, [main, side]));

    // 1. 剧情介绍
    main.appendChild(section('剧情介绍', el('p', { class: 'ff-overview', text: d.overview || '暂无中文简介。' })));

    // 2. 预告片
    var player = el('div', { class: 'ff-trailer' });
    function play(v) {
      player.innerHTML = '';
      player.appendChild(el('iframe', {
        src: 'https://www.youtube.com/embed/' + encodeURIComponent(v.key)
          + '?autoplay=1&rel=0&origin=' + encodeURIComponent(location.origin),
        allow: 'autoplay; encrypted-media; picture-in-picture; fullscreen', allowfullscreen: 'true', frameborder: '0', title: v.name,
        referrerpolicy: 'strict-origin-when-cross-origin',
      }));
      Array.prototype.forEach.call(body.querySelectorAll('.ff-vid'), function (b) { b.classList.toggle('ff-on', b.dataset.key === v.key); });
    }
    if (d.videos.length) {
      var first = d.videos[0];
      player.appendChild(el('button', { class: 'ff-trailer-cover', onclick: function () { play(first); },
        style: d.backdropUrl ? 'background-image:url("' + d.backdropUrl + '")' : '' }, [el('span', { class: 'ff-play', text: '▶' })]));
      var picks = el('div', { class: 'ff-vids' }, d.videos.map(function (v) {
        return el('button', { class: 'ff-fopt ff-vid', 'data-key': v.key, text: v.name || v.type, onclick: function () { play(v); } });
      }));
      main.appendChild(section('预告片', player, picks,
        el('a', { class: 'ff-dim ff-small', href: 'https://www.youtube.com/watch?v=' + encodeURIComponent(first.key), target: '_blank', rel: 'noopener', text: '播放不了？在 YouTube 打开' })));
    } else {
      main.appendChild(section('预告片', el('div', { class: 'ff-msg ff-dim', text: 'TMDb 暂无预告片' })));
    }

    // 3. 导演 & 演员
    var people = el('div', { class: 'ff-cast' });
    d.directors.concat(d.cast).forEach(function (p, idx) {
      var isCrew = idx < d.directors.length;
      people.appendChild(el('div', { class: 'ff-person' }, [
        p.profileUrl ? el('img', { src: p.profileUrl, loading: 'lazy', alt: p.name }) : el('div', { class: 'ff-noface', text: (p.name || '?').slice(0, 1) }),
        el('div', { class: 'ff-person-name', text: p.name }),
        el('div', { class: 'ff-dim ff-small', text: isCrew ? p.role : (p.role || '') }),
        p.episodes ? el('div', { class: 'ff-dim ff-small', text: p.episodes + ' 集' }) : null,
      ]));
    });
    main.appendChild(section('导演 & 演员', people.children.length ? people : el('div', { class: 'ff-msg ff-dim', text: '暂无演职员信息' })));

    // 4. 分季：默认只显示当前季，"查看全部季"在下方展开其余各季
    if (isTv && d.seasons.length) main.appendChild(seasonSection(d));

    // 右侧栏
    var p = d.providers;
    put(side, fact('资源类型', resourceType(d)));
    put(side, fact('状态', STATUS[d.status] || d.status));
    put(side, fact('原始片名', d.originalTitle || d.title));
    put(side, fact('原始语言', LANG[d.originalLanguage] || d.originalLanguage));
    put(side, fact(isTv ? '首播日期' : '上映日期', d.releaseDate));
    put(side, fact('出品地区', d.countries.map(function (c) { return REGION[c] || c; }).join(' / ')));
    if (isTv) put(side, fact('季 / 集', [d.seasonCount ? d.seasonCount + ' 季' : null, d.episodeCount ? d.episodeCount + ' 集' : null].filter(Boolean).join(' / ')));
    put(side, fact('片长', minutes(d.runtime) && (isTv ? '每集约 ' : '') + minutes(d.runtime)));
    put(side, fact('分级', d.certification));
    if (isTv) put(side, logos('播出平台', d.networks));
    put(side, logos(p ? '在线播放（' + (REGION[p.region] || p.region) + '）' : '在线播放', p ? p.stream : [], p && p.link));
    if (p && (p.rent.length || p.buy.length)) put(side, logos('租赁 / 购买', p.rent.concat(p.buy).filter(function (x, i, a) { return a.findIndex(function (y) { return y.name === x.name; }) === i; }), p.link));
    put(side, logos('出品方', d.companies));
    if (!isTv) { put(side, fact('预算', money(d.budget))); put(side, fact('票房', money(d.revenue))); }
    if (d.homepage) side.appendChild(el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: '官方网站' }), el('a', { href: d.homepage, target: '_blank', rel: 'noopener', class: 'ff-link', text: d.homepage.replace(/^https?:\/\//, '').replace(/\/$/, '') })]));
    side.appendChild(el('a', { class: 'ff-dim ff-small', href: 'https://www.themoviedb.org/' + (isTv ? 'tv' : 'movie') + '/' + d.tmdbId, target: '_blank', rel: 'noopener', text: '在 TMDb 查看' }));
  }

  function seasonSection(d) {
    var current = d.seasons.filter(function (s) { return s.number === d.currentSeason; })[0] || d.seasons[d.seasons.length - 1];
    var latest = d.seasons.reduce(function (m, s) { return s.number > m ? s.number : m; }, 0);
    var others = d.seasons.filter(function (s) { return s !== current; }).sort(function (a, b) { return b.number - a.number; });
    var list = el('div', { class: 'ff-season-list' }, [seasonCard(d, current)]);
    var rest = null;
    var toggle = others.length ? el('button', { class: 'ff-link-btn', text: '查看全部季（共 ' + d.seasons.length + ' 季）', onclick: function () {
      if (!rest) {
        rest = el('div', { class: 'ff-season-list' }, others.map(function (s) { return seasonCard(d, s); }));
        list.parentNode.insertBefore(rest, toggle);
      } else {
        rest.style.display = rest.style.display === 'none' ? '' : 'none';
      }
      toggle.textContent = rest.style.display === 'none' ? '查看全部季（共 ' + d.seasons.length + ' 季）' : '收起其他季';
    } }) : null;
    return section(current.number === latest ? '最新一季' : '当前季', list, toggle);
  }

  function cnDate(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s || '');
    return m ? m[1] + ' 年 ' + m[2] + ' 月 ' + m[3] + ' 日' : '';
  }

  function seasonCard(d, s) {
    var foot = el('div', { class: 'ff-season-foot' }, [el('span', { class: 'ff-dim', text: s.airDate ? '首播 ' + cnDate(s.airDate) : '' })]);
    api('GET', 'detail/tv/' + d.tmdbId + '/season/' + s.number).then(function (r) {
      var e = r.episode;
      if (!e) return;
      foot.innerHTML = '';
      foot.appendChild(el('span', { class: 'material-icons ff-cal', text: 'event', 'aria-hidden': 'true' }));
      foot.appendChild(el('span', { class: 'ff-ep-name', text: e.name || ('第 ' + e.episode + ' 集') }));
      foot.appendChild(el('span', { text: '(' + e.season + 'x' + e.episode + (e.airDate ? ', ' + cnDate(e.airDate) : '') + ')' + (e.aired ? '' : ' 即将开播') }));
    }).catch(function () {});
    return el('div', { class: 'ff-season-card' }, [
      el('div', { class: 'ff-season-poster' }, [s.posterUrl ? el('img', { src: s.posterUrl, loading: 'lazy', alt: s.name }) : el('div', { class: 'ff-noposter' })]),
      el('div', { class: 'ff-season-body' }, [
        el('h3', { class: 'ff-season-title', text: s.name || ('第 ' + s.number + ' 季') }),
        el('div', { class: 'ff-season-meta' }, [
          s.rating ? el('span', { class: 'ff-badge', text: '★ ' + Math.round(s.rating * 10) + '%' }) : null,
          el('span', { text: [s.year, s.episodeCount ? '共 ' + s.episodeCount + ' 集' : null].filter(Boolean).join(' • ') }),
        ]),
        s.overview ? el('p', { class: 'ff-season-overview', text: s.overview }) : null,
        foot,
      ]),
    ]);
  }

  function resourceType(d) {
    var g = d.genres.join('/');
    if (/动画/.test(g)) return d.kind === 'Series' ? '动漫' : '动画电影';
    if (/纪录/.test(g)) return '纪录片';
    if (d.kind === 'Series' && /真人秀|脱口秀/.test(g)) return '综艺';
    return d.kind === 'Series' ? '剧集' : '电影';
  }

  function section(title) {
    var s = el('section', { class: 'ff-section' }, [el('h2', { class: 'ff-h2', text: title })]);
    Array.prototype.slice.call(arguments, 1).forEach(function (c) { if (c) s.appendChild(c); });
    return s;
  }
  function fact(k, v) { return v ? el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: k }), el('div', { text: String(v) })]) : null; }
  function put(parent, child) { if (child) parent.appendChild(child); return child; }
  function logos(k, list, link) {
    if (!list || !list.length) return k.indexOf('在线播放') === 0 ? fact(k, '暂无数据') : null;
    return el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: k }), el('div', { class: 'ff-logos' }, list.map(function (x) {
      var inner = x.logoUrl ? el('img', { src: x.logoUrl, alt: x.name, title: x.name, loading: 'lazy' }) : el('span', { class: 'ff-chip', text: x.name });
      return link ? el('a', { href: link, target: '_blank', rel: 'noopener', title: x.name }, [inner]) : inner;
    }))]);
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
      is: 'paper-icon-button-light', class: 'headerButton ff-header-btn', title: 'FireFly 热门榜单', onclick: openHot,
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
