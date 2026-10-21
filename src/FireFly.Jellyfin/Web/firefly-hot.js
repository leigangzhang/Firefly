/* 萤火虫 FireFly：热门榜单页（筛选、搜索、收藏）和详情页的打开入口。 */
(function () {
  'use strict';
  var F = window.FireFlyApp;
  if (!F || F.loaded_hot) return;
  F.loaded_hot = true;

  // 前面文件里的函数
  var T = F.T, tmdbImg = F.tmdbImg, el = F.el, api = F.api, icon = F.icon, message = F.message,
      emptyState = F.emptyState, showTasks = F.showTasks;
  // 后面文件里的函数：调用时才取，加载顺序不影响
  function renderDetail() { return F.renderDetail.apply(null, arguments); }

  var page = null;
  var DEFAULT_SORT = 'hot';
  // 当前所在的视图：list（榜单/搜索）、favorites（收藏）、detail（详情，from 记来处，返回时回到那里）
  var route = { view: 'list' };
  var favScroll = 0;
  // 浏览器刷新后回到刷新前的 FireFly 页面；只存在本标签页里
  var SAVED_KEY = 'firefly.page';
  var hot = { state: { category: 'movie', sort: DEFAULT_SORT, page: 1 }, genres: {}, totalPages: 1, loading: false, view: null, rows: null };

  function closePage() {
    srch.q = '';
    if (page) { page.root.remove(); page = null; document.removeEventListener('keydown', onPageKey); }
    route = { view: 'list' };
    try { sessionStorage.removeItem(SAVED_KEY); } catch (e) { /* 存不了就不恢复 */ }
  }
  function onPageKey(e) {
    if (e.key !== 'Escape' || F.drawerOpen()) return;
    if (page && page.detail.style.display !== 'none') goBack(); else closePage();
  }

  function save() {
    if (!page) return;
    try { sessionStorage.setItem(SAVED_KEY, JSON.stringify({ route: route, state: hot.state, q: srch.q })); } catch (e) { /* 存不了就不恢复 */ }
  }

  function goBack() {
    if (route.view === 'detail' && route.from === 'favorites') showFavorites(favScroll); else showList();
  }

  // 刷新当前视图，不用浏览器刷新（那会回到 Jellyfin 首页）
  function refresh() {
    if (route.view === 'favorites') showFavorites();
    else if (route.view === 'detail') openDetail(route.kind, route.tmdbId, route.from);
    else if (srch.q) { var q = srch.q; srch.q = ''; srch.seq++; startSearch(q); }
    else loadFilters();
  }

  // 顶栏按钮进入：先取一次当前的 TMDb 语言，配置页改了语言不用刷新浏览器
  function enter() {
    if (page) return;
    api('GET', 'ui').then(function (r) { if (r && r.language && window.FireFlyI18n) window.FireFlyI18n.setLanguage(r.language); }, function () {}).then(function () { openHot(); });
  }

  // 浏览器刷新后：上次没关 FireFly 页面就重新打开，回到原来的筛选、搜索和视图
  function restore() {
    var saved = null;
    try { saved = JSON.parse(sessionStorage.getItem(SAVED_KEY) || 'null'); } catch (e) { saved = null; }
    if (!saved || page) return;
    if (saved.state && saved.state.category) hot.state = Object.assign({}, saved.state, { page: 1 });
    openHot();
    if (saved.q) { page.input.value = saved.q; page.searchBox.classList.add('ff-has-text'); startSearch(saved.q); }
    var r = saved.route || {};
    if (r.view === 'favorites') showFavorites();
    else if (r.view === 'detail' && r.tmdbId) openDetail(r.kind, r.tmdbId, r.from);
  }

  function openHot() {
    if (page) return;
    var back = el('button', { class: 'ff-btn ff-ghost ff-back', text: T('← 返回榜单'), onclick: goBack });
    var list = el('div', { class: 'ff-view' });
    var detail = el('div', { class: 'ff-view', style: 'display:none' });
    var input = el('input', { class: 'ff-search-input', type: 'search', placeholder: T('全局搜索电影、剧集'), 'aria-label': T('全局搜索电影、剧集'), autocomplete: 'off' });
    var clear = el('button', { class: 'ff-search-clear', title: T('清除'), 'aria-label': T('清除搜索'), onclick: function () { input.value = ''; endSearch(); input.focus(); } }, [icon('close')]);
    var searchBox = el('div', { class: 'ff-search' }, [icon('search', 'ff-search-icon'), input, clear]);
    var root = el('div', { class: 'ff-page' }, [
      el('div', { class: 'ff-page-head' }, [
        back,
        el('span', { class: 'ff-title', text: T('萤火虫 · 热门榜单') }),
        searchBox,
        el('button', { class: 'ff-btn ff-ghost ff-icon-only', title: T('刷新'), 'aria-label': T('刷新'), onclick: refresh }, [icon('refresh')]),
        el('button', { class: 'ff-btn ff-ghost', text: T('我的收藏'), onclick: function () { showFavorites(); } }),
        el('button', { class: 'ff-btn ff-ghost', text: T('下载任务'), onclick: showTasks }),
        el('button', { class: 'ff-btn ff-ghost', text: '✕', title: T('关闭（Esc）'), onclick: closePage }),
      ]),
      el('div', { class: 'ff-page-body' }, [list, detail]),
    ]);
    document.body.appendChild(root);
    document.addEventListener('keydown', onPageKey);
    page = { root: root, list: list, detail: detail, back: back, scroll: 0, input: input, searchBox: searchBox };
    back.style.display = 'none';
    route = { view: 'list' };
    buildList();
    save();
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
    srch.pending = [];
    v.filters.style.display = v.grid.style.display = v.more.style.display = 'none';
    v.search.style.display = '';
    v.sgrid.innerHTML = '';
    v.shead.innerHTML = '';
    v.shead.appendChild(el('span', { text: T('正在搜索“{q}”…', { q: q }) }));
    scroller().scrollTop = 0;
    fetchSearch(1, ++srch.seq);
    save();
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
    save();
  }

  function fetchSearch(pageNo, seq, toppingUp) {
    var v = hot.view;
    srch.loading = true;
    v.smore.innerHTML = '';
    v.smore.appendChild(el('div', { class: 'ff-msg', text: T('加载中…') }));
    api('GET', 'discover/search?q=' + encodeURIComponent(srch.q) + '&page=' + pageNo).then(function (r) {
      if (seq !== srch.seq) return;
      srch.loading = false;
      if (!r.configured) { message(v.smore, T('请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key')); return; }
      srch.page = r.page || pageNo;
      srch.totalPages = r.totalPages || 1;
      srch.pending = (srch.pending || []).concat(r.items);
      var hasMore = srch.page < srch.totalPages;
      if (!toppingUp && needsTopUp(v.sgrid, srch.pending, hasMore)) { srch.loading = true; fetchSearch(srch.page + 1, seq, true); return; }
      v.shead.innerHTML = '';
      v.shead.appendChild(el('span', { text: T('“{q}”的搜索结果', { q: srch.q }) }));
      // 全局搜索：在全部电影和剧集里按片名找，不带分类、类型、地区等筛选条件
      v.shead.appendChild(el('span', { class: 'ff-dim', text: T('全部电影和剧集') + (r.totalResults ? ' · ' + T('约 {n} 条', { n: r.totalResults }) : '') + ' · ' + T('不受筛选条件影响') }));
      v.shead.appendChild(el('button', { class: 'ff-btn ff-ghost', text: T('返回榜单'), onclick: function () { page.input.value = ''; page.searchBox.classList.remove('ff-has-text'); endSearch(); } }));
      fullRows(v.sgrid, srch.pending, hasMore).forEach(function (i) { v.sgrid.appendChild(card(i, true)); });
      v.smore.innerHTML = '';
      if (!v.sgrid.children.length && !srch.pending.length) emptyState(v.smore, 'search_off', T('没有找到“{q}”，换个片名或用原名试试', { q: srch.q }));
      else if (srch.page < srch.totalPages) v.smore.appendChild(el('button', { class: 'ff-btn ff-ghost', text: T('加载更多'), onclick: loadMoreSearch }));
      else v.smore.appendChild(el('div', { class: 'ff-msg ff-dim', text: T('已经到底了') }));
    }).catch(function (e) {
      if (seq !== srch.seq) return;
      srch.loading = false;
      fullRows(v.sgrid, srch.pending || [], false).forEach(function (i) { v.sgrid.appendChild(card(i, true)); });
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
    route = { view: 'list' };
    save();
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
    box.appendChild(el('div', { class: 'ff-msg', text: T('加载筛选项…') }));
    api('GET', 'discover/filters?category=' + encodeURIComponent(hot.state.category)).then(function (r) {
      if (!r.configured) { message(box, T('请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key')); hot.view.grid.innerHTML = ''; return; }
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

  // 筛选项文字：地区按代码用当前语言显示，其余（分类、出品、分级、排序、"更早"）查词条；TMDb 类型名本身已是当前语言
  function optionLabel(key, o) {
    if (key === 'region' && o.value && T.language() !== 'zh-CN') return displayName('region', o.value);
    return T(o.label);
  }

  function renderFilters() {
    var box = hot.view.filters;
    box.innerHTML = '';
    hot.rows.forEach(function (row) {
      var isCat = row.key === 'category';
      var opts = el('div', { class: 'ff-fopts' });
      var all = isCat || row.key === 'sort' ? [] : [{ value: '', label: T('全部') }];
      all.concat(row.options).forEach(function (o) {
        var active = (hot.state[row.key] || '') === o.value;
        opts.appendChild(el('button', {
          class: 'ff-fopt' + (active ? ' ff-on' : ''), text: optionLabel(row.key, o),
          onclick: function () {
            // 再点一次已选中的项：取消选择，回到默认值（推荐回到"最热"，其他回到"全部"）；分类没有"不选"，保持不变
            if (active) {
              if (isCat || !o.value || (row.key === 'sort' && o.value === DEFAULT_SORT)) return;
              if (row.key === 'sort') hot.state.sort = DEFAULT_SORT; else delete hot.state[row.key];
              renderFilters();
              reload();
              return;
            }
            if (o.value) hot.state[row.key] = o.value; else delete hot.state[row.key];
            if (isCat) { hot.state = { category: o.value, sort: DEFAULT_SORT, region: hot.state.region, time: hot.state.time }; loadFilters(); return; }
            renderFilters();
            reload();
          },
        }));
      });
      box.appendChild(el('div', { class: 'ff-frow' + (isCat ? ' ff-frow-cat' : '') }, [isCat ? null : el('span', { class: 'ff-flabel', text: T(row.label) }), opts]));
    });
  }

  function query(pageNo) {
    var q = [];
    Object.keys(hot.state).forEach(function (k) { if (hot.state[k] && k !== 'page') q.push(k + '=' + encodeURIComponent(hot.state[k])); });
    q.push('page=' + pageNo);
    return 'discover?' + q.join('&');
  }

  // TMDb 一页 20 条，网格每行的列数随窗口宽度变化，20 往往填不满最后一行。
  // 每次只渲染能排成整行的部分，剩下的留到下一次；缺口还在且有下一页时先多取一页再渲染。
  function gridColumns(grid) {
    var cols = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length;
    return Math.max(1, cols || 1);
  }
  function fullRows(grid, pending, hasMore) {
    if (!hasMore) return pending.splice(0, pending.length);
    var cols = gridColumns(grid);
    var count = Math.floor((grid.children.length + pending.length) / cols) * cols - grid.children.length;
    return pending.splice(0, Math.max(0, count));
  }
  function needsTopUp(grid, pending, hasMore) {
    return hasMore && (grid.children.length + pending.length) % gridColumns(grid) !== 0;
  }

  var hotSeq = 0;
  function reload() {
    hot.state.page = 1;
    hot.totalPages = 1;
    hot.pending = [];
    hot.view.grid.innerHTML = '';
    fetchPage(1, ++hotSeq);
    save();
  }
  function loadMore() {
    if (hot.loading || hot.state.page >= hot.totalPages) return;
    fetchPage(hot.state.page + 1, hotSeq);
  }

  function fetchPage(pageNo, seq, toppingUp) {
    var more = hot.view.more;
    var grid = hot.view.grid;
    hot.loading = true;
    more.innerHTML = '';
    more.appendChild(el('div', { class: 'ff-msg', text: T('加载中…') }));
    api('GET', query(pageNo)).then(function (r) {
      if (seq !== hotSeq) return;
      hot.state.page = r.page || pageNo;
      hot.totalPages = r.totalPages || 1;
      hot.pending = (hot.pending || []).concat(r.items);
      var hasMore = hot.state.page < hot.totalPages;
      if (!toppingUp && needsTopUp(grid, hot.pending, hasMore)) { fetchPage(hot.state.page + 1, seq, true); return; }
      hot.loading = false;
      fullRows(grid, hot.pending, hasMore).forEach(function (i) { grid.appendChild(card(i)); });
      more.innerHTML = '';
      if (!grid.children.length && !hot.pending.length) more.appendChild(el('div', { class: 'ff-msg', text: T('没有符合条件的影片，换个筛选试试') }));
      else if (hot.state.page < hot.totalPages) more.appendChild(el('button', { class: 'ff-btn ff-ghost', text: T('加载更多'), onclick: loadMore }));
      else more.appendChild(el('div', { class: 'ff-msg ff-dim', text: T('已经到底了') }));
    }).catch(function (e) {
      if (seq !== hotSeq) return;
      hot.loading = false;
      // 补页失败时，已经取到的先显示出来
      fullRows(grid, hot.pending || [], false).forEach(function (i) { grid.appendChild(card(i)); });
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
    var genres = mixed ? [i.kind === 'Series' ? T('剧集|单部') : T('电影|单部')] : (i.genreIds || []).map(function (g) { return hot.genres[g]; }).filter(Boolean).slice(0, 2);
    var sub = [i.year, genres.join(' / ')].filter(Boolean).join(' · ');
    return el('div', { class: 'ff-card', onclick: function () { openDetail(i.kind, i.tmdbId); } }, [
      el('div', { class: 'ff-poster' }, [
        i.posterUrl ? el('img', { src: tmdbImg(i.posterUrl), loading: 'lazy', alt: i.title }) : el('div', { class: 'ff-noposter' }),
        ratingBadge(i.rating),
        i.inLibrary ? el('span', { class: 'ff-chip ff-in', text: T('已在库') }) : null,
      ]),
      el('div', { class: 'ff-card-title', text: i.title, title: i.originalTitle ? i.title + ' / ' + i.originalTitle : i.title }),
      sub ? el('div', { class: 'ff-card-sub', text: sub }) : null,
    ]);
  }

  // scrollTo：从详情页返回时回到原来的滚动位置
  function showFavorites(scrollTo) {
    if (!page) openHot();
    var body = beginDetail();
    route = { view: 'favorites' };
    page.back.textContent = T('← 返回榜单');
    save();
    body.appendChild(el('h2', { class: 'ff-h2', text: T('我的收藏') }));
    var grid = el('div', { class: 'ff-grid ff-hot-grid' });
    body.appendChild(grid);
    api('GET', 'favorites').then(function (items) {
      if (page.detail !== body || route.view !== 'favorites') return;
      if (!items.length) { grid.appendChild(el('div', { class: 'ff-msg', text: T('还没有收藏。在详情页点"收藏"即可加入这里。') })); return; }
      items.forEach(function (i) { grid.appendChild(card(i)); });
      if (scrollTo) scroller().scrollTop = scrollTo;
    }).catch(function (e) { message(body, e.message, 'ff-error'); });
  }

  // ---------- 资源详情页 ----------
  var sideText = function (lang) { return window.FireFlyI18n.sideText(lang); };
  var displayName = function (type, code, lang) { return window.FireFlyI18n.displayName(type, code, lang); };

  function beginDetail() {
    if (page.list.style.display !== 'none') page.scroll = scroller().scrollTop;
    page.list.style.display = 'none';
    page.detail.innerHTML = '';
    page.detail.style.display = '';
    page.back.style.display = '';
    scroller().scrollTop = 0;
    return page.detail;
  }

  // from：返回时回到哪里（favorites 或 list）；不传时按当前视图推断
  function openDetail(kind, tmdbId, from) {
    if (!page) openHot();
    if (!from) from = route.view === 'favorites' ? 'favorites' : route.view === 'detail' ? route.from : 'list';
    if (route.view === 'favorites') favScroll = scroller().scrollTop;
    var body = beginDetail();
    route = { view: 'detail', kind: kind, tmdbId: tmdbId, from: from };
    page.back.textContent = from === 'favorites' ? T('← 返回收藏') : T('← 返回榜单');
    save();
    var seg = kind === 'Series' ? 'tv' : 'movie';
    body.appendChild(el('div', { class: 'ff-msg', text: T('加载详情…') }));
    api('GET', 'detail/' + seg + '/' + encodeURIComponent(tmdbId)).then(function (r) {
      if (page.detail !== body || body.style.display === 'none') return;
      body.innerHTML = '';
      renderDetail(body, r.detail, r);
    }).catch(function (e) { message(body, e.message, 'ff-error'); });
  }

  F.displayName = displayName;
  F.enter = enter;
  F.openHot = openHot;
  F.restore = restore;
  F.sideText = sideText;
})();
