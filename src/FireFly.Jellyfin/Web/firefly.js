/* 萤火虫 FireFly：注入 Jellyfin 网页的前端，仅管理员可见。本文件是公共工具（文案、DOM、接口调用），其余按功能分在 firefly-drawer.js（获取资源抽屉、任务列表）、firefly-hot.js（热门榜单、搜索、收藏）、firefly-detail.js（资源详情页、注入入口）。四个文件按此顺序加载，通过 window.FireFlyApp 共享函数。 */
(function () {
  'use strict';
  if (window.FireFlyApp) return;
  var F = window.FireFlyApp = {};

  // 界面文字跟随 TMDb 语言，词条在 firefly-i18n.js
  var I18N = window.FireFlyI18n;
  function T(zh, vars) { return I18N ? I18N.t(zh, vars) : zh; }
  T.server = function (text) { return I18N ? I18N.server(text) : text; };
  T.release = function (name) { return I18N ? I18N.release(name) : name; };
  T.chip = function (text) { return I18N ? I18N.chip(text) : text; };
  T.language = function () { return I18N ? I18N.language() : 'zh-CN'; };


  // ---------- 工具 ----------
  // TMDb 图片改由插件取并缓存到本地：把 .../t/p/<尺寸>/<文件> 换成 FireFly/image/<尺寸>/<文件>。
  // 收藏里存的旧地址也会被换掉；不是 TMDb 图片路径的地址原样返回
  function tmdbImg(url) {
    var m = url && /\/t\/p\/(w\d+|original)\/([A-Za-z0-9_-]+\.(?:jpe?g|png|svg|webp))$/i.exec(url);
    return m ? window.ApiClient.getUrl('FireFly/image/' + m[1] + '/' + m[2]) : url;
  }

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
      .catch(function (e) {
        if (e && e.name === 'AbortError') throw e;
        throw new Error(T('连不上 Jellyfin，请稍后点刷新重试'));
      })
      .then(function (res) {
        return res.text().then(function (text) {
          var data;
          // Jellyfin 重启期间返回的是"服务器正在启动"网页，不是 JSON
          try { data = text ? JSON.parse(text) : null; } catch (e) { throw new Error(T('Jellyfin 正在重启或暂时不可用，请稍后点刷新重试')); }
          if (!res.ok) { var err = new Error((data && data.error ? T.server(data.error) : T('请求失败 {n}', { n: res.status }))); err.status = res.status; err.data = data; throw err; }
          return data;
        });
      });
  }

  // 插件里的海报加载失败（如 Jellyfin 正在重启）时，过几秒重试一次
  var IMAGE_RETRY_MS = 3000;
  document.addEventListener('error', function (e) {
    var img = e.target;
    if (!img || img.tagName !== 'IMG' || img.dataset.ffRetried || img.src.indexOf('/FireFly/image/') < 0) return;
    img.dataset.ffRetried = '1';
    setTimeout(function () { img.src = img.src + (img.src.indexOf('?') < 0 ? '?' : '&') + 'retry=1'; }, IMAGE_RETRY_MS);
  }, true);

  function gb(bytes) { return bytes ? (bytes / 1073741824).toFixed(bytes >= 10737418240 ? 0 : 1) + ' GB' : '—'; }
  function speed(bps) { return bps > 0 ? (bps / 1048576).toFixed(1) + ' MB/s' : ''; }
  function resLabel(r) { return r === 2160 ? '4K' : r ? r + 'p' : ''; }
  function date(s) { return s ? String(s).slice(0, 10) : ''; }

  // ---------- 侧边抽屉：获取资源 / 下载任务 ----------
  // 与榜单页、详情页同一套深色配色；从右侧滑入，切换"资源版本"和"下载任务"不重开抽屉。

  F.I18N = I18N;
  F.T = T;
  F.api = api;
  F.date = date;
  F.el = el;
  F.gb = gb;
  F.resLabel = resLabel;
  F.speed = speed;
  F.tmdbImg = tmdbImg;
})();
