/* 萤火虫 FireFly：资源详情页的渲染，以及往 Jellyfin 页面注入按钮的入口。 */
(function () {
  'use strict';
  var F = window.FireFlyApp;
  if (!F || F.loaded_detail) return;
  F.loaded_detail = true;

  // 前面文件里的函数
  var I18N = F.I18N, T = F.T, tmdbImg = F.tmdbImg, el = F.el, api = F.api, date = F.date, toast = F.toast,
      openSearch = F.openSearch, openHot = F.openHot, sideText = F.sideText, displayName = F.displayName;

  var isAdmin = false;
  var pendingId = null;

  function money(n) { return n ? '$' + Number(n).toLocaleString('en-US') : null; }
  function minutes(m, t) {
    t = t || SIDE_TEXT['zh-CN'];
    return m ? (m >= 60 ? t.hours(Math.floor(m / 60), m % 60) : t.mins(m)) : null;
  }

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
    function paintFav() { favBtn.textContent = favorite ? T('♥ 已收藏') : T('♡ 收藏'); favBtn.classList.toggle('ff-fav-on', favorite); }
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
    var download = el('button', { class: 'ff-btn', text: T('⬇ 下载'), onclick: function () {
      openSearch({ title: d.title, originalTitle: d.originalTitle, year: d.year, kind: d.kind, tmdbId: d.tmdbId }, d.title);
    } });
    var trailers = meta.showTrailers ? d.videos : [];
    var trailerBtn = trailers.length ? el('button', { class: 'ff-btn ff-ghost', text: T('▶ 播放预告片'), onclick: function () {
      var t = body.querySelector('.ff-trailer');
      if (t) { t.scrollIntoView({ behavior: 'smooth', block: 'center' }); play(trailers[0]); }
    } }) : null;

    var facts = [d.certification ? el('span', { class: 'ff-cert', text: d.certification }) : null,
      el('span', { text: [d.releaseDate, d.genres.join(' / '), minutes(d.runtime, sideText(meta.language))].filter(Boolean).join(' · ') })];

    body.appendChild(el('div', { class: 'ff-hero', style: d.backdropUrl ? 'background-image:url("' + tmdbImg(d.backdropUrl) + '")' : '' }, [
      el('div', { class: 'ff-hero-in' }, [
        el('div', { class: 'ff-hero-poster' }, [d.posterUrl ? el('img', { src: tmdbImg(d.posterUrl), alt: d.title }) : el('div', { class: 'ff-noposter' })]),
        el('div', { class: 'ff-hero-info' }, [
          el('h1', { class: 'ff-h1' }, [d.title, d.year ? el('span', { class: 'ff-year', text: ' (' + d.year + ')' }) : null]),
          d.originalTitle ? el('div', { class: 'ff-dim', text: d.originalTitle }) : null,
          el('div', { class: 'ff-facts' }, facts),
          el('div', { class: 'ff-actions' }, [
            scoreRing(d.rating), el('span', { class: 'ff-score-label', text: d.voteCount ? T('用户评分（{n}人）', { n: d.voteCount }) : T('用户评分') }),
            favBtn, download, trailerBtn,
            meta.inLibrary ? el('span', { class: 'ff-chip ff-in-static', text: T('已在库') }) : null,
          ]),
          d.tagline ? el('div', { class: 'ff-tagline', text: d.tagline }) : null,
          d.directors.length ? el('div', { class: 'ff-crew' }, d.directors.map(function (p) {
            return el('div', { class: 'ff-crew-item' }, [el('div', { class: 'ff-crew-name', text: p.name }), el('div', { class: 'ff-dim', text: T.server(p.role) })]);
          })) : null,
        ]),
      ]),
    ]));

    var main = el('div', { class: 'ff-main-col' });
    var side = el('aside', { class: 'ff-side' });
    body.appendChild(el('div', { class: 'ff-cols' }, [main, side]));

    // 1. 剧情介绍
    main.appendChild(section(T('剧情介绍'), el('p', { class: 'ff-overview', text: d.overview || T('暂无中文简介。') })));

    // 2. 预告片（YouTube；配置页可关闭，大陆网络不翻墙时打不开）
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
    if (trailers.length) {
      var first = trailers[0];
      player.appendChild(el('button', { class: 'ff-trailer-cover', onclick: function () { play(first); },
        style: d.backdropUrl ? 'background-image:url("' + tmdbImg(d.backdropUrl) + '")' : '' }, [el('span', { class: 'ff-play', text: '▶' })]));
      var picks = el('div', { class: 'ff-vids' }, trailers.map(function (v) {
        return el('button', { class: 'ff-fopt ff-vid', 'data-key': v.key, text: v.name || v.type, onclick: function () { play(v); } });
      }));
      main.appendChild(section(T('预告片'), player, picks,
        el('a', { class: 'ff-dim ff-small', href: 'https://www.youtube.com/watch?v=' + encodeURIComponent(first.key), target: '_blank', rel: 'noopener', text: T('播放不了？在 YouTube 打开') })));
    } else if (meta.showTrailers) {
      main.appendChild(section(T('预告片'), el('div', { class: 'ff-msg ff-dim', text: T('TMDb 暂无预告片') })));
    }

    // 3. 导演 & 演员
    var people = el('div', { class: 'ff-cast' });
    d.directors.concat(d.cast).forEach(function (p, idx) {
      var isCrew = idx < d.directors.length;
      people.appendChild(el('div', { class: 'ff-person' }, [
        p.profileUrl ? el('img', { src: tmdbImg(p.profileUrl), loading: 'lazy', alt: p.name }) : el('div', { class: 'ff-noface', text: (p.name || '?').slice(0, 1) }),
        el('div', { class: 'ff-person-name', text: p.name }),
        el('div', { class: 'ff-dim ff-small', text: isCrew ? T.server(p.role) : (p.role || '') }),
        p.episodes ? el('div', { class: 'ff-dim ff-small', text: T('{n} 集', { n: p.episodes }) }) : null,
      ]));
    });
    main.appendChild(section(T('导演 & 演员'), people.children.length ? people : el('div', { class: 'ff-msg ff-dim', text: T('暂无演职员信息') })));

    // 4. 分季：默认只显示当前季，"查看全部季"在下方展开其余各季
    if (isTv && d.seasons.length) main.appendChild(seasonSection(d));

    // 右侧栏（跟随 TMDb 语言）
    var p = d.providers;
    var lang = meta.language;
    var t = sideText(lang);
    var runtime = minutes(d.runtime, t);
    put(side, fact(t.type, resourceType(d, t)));
    put(side, fact(t.status, t.states[d.status] || d.status));
    put(side, fact(t.originalTitle, d.originalTitle || d.title));
    put(side, fact(t.originalLanguage, displayName('language', d.originalLanguage, lang)));
    put(side, fact(isTv ? t.firstAir : t.release, d.releaseDate));
    put(side, fact(t.countries, d.countries.map(function (c) { return displayName('region', c, lang); }).join(' / ')));
    if (isTv) put(side, fact(t.seasonsEpisodes, [d.seasonCount ? t.seasons(d.seasonCount) : null, d.episodeCount ? t.episodes(d.episodeCount) : null].filter(Boolean).join(' / ')));
    put(side, fact(t.runtime, runtime && (isTv ? t.perEpisode : '') + runtime));
    put(side, fact(t.certification, d.certification));
    if (isTv) put(side, logos(t.networks, d.networks));
    put(side, logos(p ? t.streaming + t.paren(displayName('region', p.region, lang)) : t.streaming, p ? p.stream : [], p && p.link, t.noData));
    if (p && (p.rent.length || p.buy.length)) put(side, logos(t.rentBuy, p.rent.concat(p.buy).filter(function (x, i, a) { return a.findIndex(function (y) { return y.name === x.name; }) === i; }), p.link));
    put(side, logos(t.companies, d.companies));
    if (!isTv) { put(side, fact(t.budget, money(d.budget))); put(side, fact(t.revenue, money(d.revenue))); }
    if (d.homepage) side.appendChild(el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: t.homepage }), el('a', { href: d.homepage, target: '_blank', rel: 'noopener', class: 'ff-link', text: d.homepage.replace(/^https?:\/\//, '').replace(/\/$/, '') })]));
    side.appendChild(el('a', { class: 'ff-dim ff-small', href: 'https://www.themoviedb.org/' + (isTv ? 'tv' : 'movie') + '/' + d.tmdbId, target: '_blank', rel: 'noopener', text: t.viewOnTmdb }));
  }

  // 按 TMDb 类型 ID 判断，不依赖类型名称的语言：16 动画、99 纪录、10764 真人秀、10767 脱口秀
  function resourceType(d, t) {
    var ids = d.genreIds || [];
    var has = function (id) { return ids.indexOf(id) >= 0; };
    var series = d.kind === 'Series';
    if (has(16)) return series ? t.kinds.anime : t.kinds.animatedMovie;
    if (has(99)) return t.kinds.documentary;
    if (series && (has(10764) || has(10767))) return t.kinds.variety;
    return series ? t.kinds.series : t.kinds.movie;
  }

  function seasonSection(d) {
    var current = d.seasons.filter(function (s) { return s.number === d.currentSeason; })[0] || d.seasons[d.seasons.length - 1];
    var latest = d.seasons.reduce(function (m, s) { return s.number > m ? s.number : m; }, 0);
    var others = d.seasons.filter(function (s) { return s !== current; }).sort(function (a, b) { return b.number - a.number; });
    var list = el('div', { class: 'ff-season-list' }, [seasonCard(d, current)]);
    var rest = null;
    var toggle = others.length ? el('button', { class: 'ff-link-btn', text: T('查看全部季（共 {n} 季）', { n: d.seasons.length }), onclick: function () {
      if (!rest) {
        rest = el('div', { class: 'ff-season-list' }, others.map(function (s) { return seasonCard(d, s); }));
        list.parentNode.insertBefore(rest, toggle);
      } else {
        rest.style.display = rest.style.display === 'none' ? '' : 'none';
      }
      toggle.textContent = rest.style.display === 'none' ? T('查看全部季（共 {n} 季）', { n: d.seasons.length }) : T('收起其他季');
    } }) : null;
    return section(current.number === latest ? T('最新一季') : T('当前季'), list, toggle);
  }

  // 日期：简体中文保持"2026 年 08 月 16 日"，其他语言用浏览器按该语言格式化
  function cnDate(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s || '');
    if (!m) return '';
    if (T.language() === 'zh-CN') return m[1] + ' 年 ' + m[2] + ' 月 ' + m[3] + ' 日';
    try {
      return new Intl.DateTimeFormat(T.language(), { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])));
    } catch (e) {
      return m[0];
    }
  }

  function seasonCard(d, s) {
    var foot = el('div', { class: 'ff-season-foot' }, [el('span', { class: 'ff-dim', text: s.airDate ? T('首播 {date}', { date: cnDate(s.airDate) }) : '' })]);
    api('GET', 'detail/tv/' + d.tmdbId + '/season/' + s.number).then(function (r) {
      var e = r.episode;
      if (!e) return;
      foot.innerHTML = '';
      foot.appendChild(el('span', { class: 'material-icons ff-cal', text: 'event', 'aria-hidden': 'true' }));
      foot.appendChild(el('span', { class: 'ff-ep-name', text: e.name || T('第 {n} 集', { n: e.episode }) }));
      foot.appendChild(el('span', { text: '(' + e.season + 'x' + e.episode + (e.airDate ? ', ' + cnDate(e.airDate) : '') + ')' + (e.aired ? '' : ' ' + T('即将开播')) }));
    }).catch(function () {});
    return el('div', { class: 'ff-season-card' }, [
      el('div', { class: 'ff-season-poster' }, [s.posterUrl ? el('img', { src: tmdbImg(s.posterUrl), loading: 'lazy', alt: s.name }) : el('div', { class: 'ff-noposter' })]),
      el('div', { class: 'ff-season-body' }, [
        el('h3', { class: 'ff-season-title', text: s.name || T('第 {n} 季', { n: s.number }) }),
        el('div', { class: 'ff-season-meta' }, [
          s.rating ? el('span', { class: 'ff-badge', text: '★ ' + Math.round(s.rating * 10) + '%' }) : null,
          el('span', { text: [s.year, s.episodeCount ? T('共 {n} 集', { n: s.episodeCount }) : null].filter(Boolean).join(' • ') }),
        ]),
        s.overview ? el('p', { class: 'ff-season-overview', text: s.overview }) : null,
        foot,
      ]),
    ]);
  }

  function section(title) {
    var s = el('section', { class: 'ff-section' }, [el('h2', { class: 'ff-h2', text: title })]);
    Array.prototype.slice.call(arguments, 1).forEach(function (c) { if (c) s.appendChild(c); });
    return s;
  }
  function fact(k, v) { return v ? el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: k }), el('div', { text: String(v) })]) : null; }
  function put(parent, child) { if (child) parent.appendChild(child); return child; }
  // empty 有值时，列表为空也显示这一栏（如"在线播放：暂无数据"）
  function logos(k, list, link, empty) {
    if (!list || !list.length) return empty ? fact(k, empty) : null;
    return el('div', { class: 'ff-fact' }, [el('div', { class: 'ff-fact-k', text: k }), el('div', { class: 'ff-logos' }, list.map(function (x) {
      var inner = x.logoUrl ? el('img', { src: tmdbImg(x.logoUrl), alt: x.name, title: x.name, loading: 'lazy' }) : el('span', { class: 'ff-chip', text: x.name });
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
        is: 'emby-button', class: 'button-flat detailButton ff-detail-btn', 'data-id': id, title: T('获取资源'),
        onclick: function () { openSearch({ itemId: id, kind: kind, season: season }, item.SeriesName || item.Name); },
      }, [el('div', { class: 'detailButton-content' }, [el('span', { class: 'material-icons detailButton-icon', text: 'cloud_download', 'aria-hidden': 'true' })])]));
    }, function () { pendingId = null; });
  }

  function injectHeaderButton() {
    var right = document.querySelector('.headerRight');
    if (!right || right.querySelector('.ff-header-btn')) return;
    right.insertBefore(el('button', {
      is: 'paper-icon-button-light', class: 'headerButton ff-header-btn', title: T('FireFly 热门榜单'), onclick: openHot,
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
      // 先取 TMDb 语言再注入按钮，按钮提示也用这个语言；取不到就用简体中文
      return api('GET', 'ui').then(function (r) { if (I18N && r && r.language) I18N.setLanguage(r.language); }, function () {}).then(function () {
        new MutationObserver(function () { tick(); }).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', tick);
        tick();
      });
    });
  }

  F.renderDetail = renderDetail;

  start();
})();
