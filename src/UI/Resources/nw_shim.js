// RocketRPG: NW.js environment for RPG Maker MV/MZ running in WebView2.
// The games ship with NW.js and check Utils.isNwjs() (typeof require === 'function' && typeof process === 'object').
// Under NW.js they save into <game>/www/save (MV) or <game>/save (MZ) with require('fs'); as a plain web page they fall
// back to localStorage, which every game shares here, and plugins that use fs/path/nw break. This shim provides the
// parts of Node/NW.js the engine and common plugins use, with file access done by the host (game folder only).
(function () {
  'use strict';
  if (location.hostname !== 'rocket.local' || typeof window.require === 'function') return;
  var host;
  try { host = chrome.webview.hostObjects.sync.rrnw; } catch (e) { return; }
  if (!host) return;
  var root = String(host.Root() || '');
  if (!root) return;

  function call(r) {
    var o = JSON.parse(r);
    if (o && o.e) {
      var err = new Error(o.e);
      err.code = String(o.e).split(':')[0];
      throw err;
    }
    return o ? o.d : undefined;
  }
  function str(p) { return String(p && p.href ? p.pathname : p); }

  // ---- bytes helpers / minimal Buffer ----
  function b64ToBytes(b64) {
    var bin = atob(b64 || ''), u8 = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    return u8;
  }
  function bytesToB64(u8) {
    var s = '';
    for (var i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
    return btoa(s);
  }
  function asBuffer(u8) {
    u8.toString = function (enc) {
      enc = (enc || 'utf8').toLowerCase();
      if (enc === 'base64') return bytesToB64(this);
      if (enc === 'binary' || enc === 'latin1') { var s = ''; for (var i = 0; i < this.length; i++) s += String.fromCharCode(this[i]); return s; }
      return new TextDecoder('utf-8').decode(this);
    };
    return u8;
  }
  var Buffer = {
    from: function (data, enc) {
      if (typeof data === 'string') {
        enc = (enc || 'utf8').toLowerCase();
        if (enc === 'base64') return asBuffer(b64ToBytes(data));
        if (enc === 'binary' || enc === 'latin1') { var u = new Uint8Array(data.length); for (var i = 0; i < data.length; i++) u[i] = data.charCodeAt(i) & 255; return asBuffer(u); }
        return asBuffer(new TextEncoder().encode(data));
      }
      return asBuffer(new Uint8Array(data.buffer ? data.buffer : data));
    },
    alloc: function (n) { return asBuffer(new Uint8Array(n)); },
    isBuffer: function (b) { return b instanceof Uint8Array; },
    byteLength: function (s) { return new TextEncoder().encode(String(s)).length; }
  };

  // ---- fs ----
  function encOf(opt) { return typeof opt === 'string' ? opt : (opt && opt.encoding) || null; }
  function statObj(d) {
    return {
      size: d.size, mtimeMs: d.mtime, mtime: new Date(d.mtime), ctime: new Date(d.mtime), atime: new Date(d.mtime),
      isDirectory: function () { return !!d.dir; }, isFile: function () { return !d.dir; }, isSymbolicLink: function () { return false; }
    };
  }
  var fs = {
    existsSync: function (f) { return !!host.Exists(str(f)); },
    readFileSync: function (f, opt) {
      if (encOf(opt)) return call(host.ReadText(str(f)));
      return asBuffer(b64ToBytes(call(host.ReadBase64(str(f)))));
    },
    writeFileSync: function (f, data) {
      if (typeof data === 'string') call(host.WriteText(str(f), data, false));
      else call(host.WriteBase64(str(f), bytesToB64(new Uint8Array(data.buffer ? data.buffer : data))));
    },
    appendFileSync: function (f, data) { call(host.WriteText(str(f), String(data), true)); },
    mkdirSync: function (f) { call(host.Mkdir(str(f))); },
    unlinkSync: function (f) { call(host.Unlink(str(f))); },
    rmdirSync: function (f) { call(host.Rmdir(str(f))); },
    renameSync: function (a, b) { call(host.Rename(str(a), str(b))); },
    readdirSync: function (f) { return call(host.Readdir(str(f))) || []; },
    statSync: function (f) { return statObj(call(host.Stat(str(f)))); },
    copyFileSync: function (a, b) { fs.writeFileSync(b, fs.readFileSync(a)); }
  };
  fs.lstatSync = fs.statSync;
  ['readFile', 'writeFile', 'appendFile', 'mkdir', 'unlink', 'rmdir', 'rename', 'readdir', 'stat', 'lstat', 'copyFile'].forEach(function (n) {
    var sync = fs[n + 'Sync'];
    fs[n] = function () {
      var args = Array.prototype.slice.call(arguments);
      var cb = typeof args[args.length - 1] === 'function' ? args.pop() : null;
      setTimeout(function () {
        var r, e = null;
        try { r = sync.apply(fs, args); } catch (x) { e = x; }
        if (cb) cb(e, r);
      }, 0);
    };
  });
  fs.exists = function (f, cb) { setTimeout(function () { cb(fs.existsSync(f)); }, 0); };
  fs.promises = {};
  ['readFile', 'writeFile', 'appendFile', 'mkdir', 'unlink', 'rmdir', 'rename', 'readdir', 'stat', 'lstat', 'copyFile'].forEach(function (n) {
    var sync = fs[n + 'Sync'];
    fs.promises[n] = function () {
      var args = arguments;
      return new Promise(function (res, rej) { try { res(sync.apply(fs, args)); } catch (e) { rej(e); } });
    };
  });

  // ---- path (Windows paths written with '/') ----
  function norm(p) {
    p = String(p).replace(/\\/g, '/');
    var drive = (p.match(/^[A-Za-z]:/) || [''])[0];
    var rest = p.slice(drive.length);
    var abs = rest.charAt(0) === '/';
    var out = [];
    rest.split('/').forEach(function (s) {
      if (!s || s === '.') return;
      if (s === '..') { if (out.length && out[out.length - 1] !== '..') out.pop(); else if (!abs) out.push('..'); }
      else out.push(s);
    });
    var r = drive + (abs ? '/' : '') + out.join('/');
    if (/\/$/.test(p) && out.length) r += '/';
    return r || '.';
  }
  var path = {
    sep: '/', delimiter: ';',
    normalize: norm,
    join: function () { return norm(Array.prototype.filter.call(arguments, function (s) { return s !== ''; }).join('/')); },
    resolve: function () {
      var r = root;
      for (var i = 0; i < arguments.length; i++) {
        var s = String(arguments[i]).replace(/\\/g, '/');
        r = /^[A-Za-z]:/.test(s) ? s : (s.charAt(0) === '/' ? r.slice(0, 2) + s : r + '/' + s);
      }
      return norm(r).replace(/\/$/, '');
    },
    dirname: function (p) { p = norm(p).replace(/\/$/, ''); var i = p.lastIndexOf('/'); return i < 0 ? '.' : (i === 0 ? '/' : p.slice(0, i)); },
    basename: function (p, ext) { p = norm(p).replace(/\/$/, ''); var b = p.slice(p.lastIndexOf('/') + 1); return ext && b.slice(-ext.length) === ext ? b.slice(0, -ext.length) : b; },
    extname: function (p) { var b = path.basename(p); var i = b.lastIndexOf('.'); return i <= 0 ? '' : b.slice(i); },
    isAbsolute: function (p) { return /^([A-Za-z]:)?[\\/]/.test(String(p)); },
    relative: function (a, b) { a = path.resolve(a); b = path.resolve(b); return b.indexOf(a + '/') === 0 ? b.slice(a.length + 1) : b; }
  };
  path.win32 = path; path.posix = path;

  // ---- nw / process ----
  var mainFile = root + decodeURIComponent(location.pathname);
  var win = {
    x: 0, y: 0, width: window.innerWidth, height: window.innerHeight, title: document.title, menu: null, isFullscreen: false, zoomLevel: 0,
    on: function () { return win; }, once: function () { return win; }, removeListener: function () { return win; }, removeAllListeners: function () { return win; },
    close: function () { window.close(); }, reload: function () { location.reload(); }, reloadIgnoringCache: function () { location.reload(); },
    showDevTools: function () {}, closeDevTools: function () {}, focus: function () {}, blur: function () {}, show: function () {}, hide: function () {},
    maximize: function () {}, minimize: function () {}, restore: function () {}, moveTo: function () {}, moveBy: function () {}, resizeTo: function () {},
    resizeBy: function () {}, setPosition: function () {}, setResizable: function () {}, setAlwaysOnTop: function () {}, setMinimumSize: function () {},
    setMaximumSize: function () {}, enterFullscreen: function () {}, leaveFullscreen: function () {}, toggleFullscreen: function () {},
    requestAttention: function () {}, setShowInTaskbar: function () {}
  };
  function postHost(msg) { try { chrome.webview.postMessage(msg); } catch (e) {} }
  var nw = {
    Window: { get: function () { return win; }, open: function () { return win; } },
    App: { argv: [], fullArgv: [], dataPath: root + '/save', manifest: { name: document.title }, quit: function () { window.close(); }, clearCache: function () {}, on: function () {} },
    Shell: { openExternal: function (url) { postHost({ type: 'openExternal', url: String(url) }); }, openItem: function () {}, showItemInFolder: function () {} },
    Screen: { Init: function () {}, screens: [], on: function () {} },
    Clipboard: { get: function () { return { get: function () { return ''; }, set: function () {}, clear: function () {} }; } },
    Menu: function () { return { append: function () {}, insert: function () {}, items: [], createMacBuiltin: function () {} }; },
    MenuItem: function (o) { return o || {}; }
  };
  var proc = {
    platform: 'win32', arch: 'x64', env: {}, argv: [], execArgv: [], pid: 1, title: 'nw',
    version: 'v14.16.0', versions: { node: '14.16.0', nw: '0.50.0', 'node-webkit': '0.50.0', chromium: '' },
    mainModule: { filename: mainFile }, execPath: root + '/Game.exe',
    cwd: function () { return root; }, exit: function () { window.close(); }, on: function () { return proc; }, once: function () { return proc; },
    removeListener: function () { return proc; }, nextTick: function (f) { var a = Array.prototype.slice.call(arguments, 1); setTimeout(function () { f.apply(null, a); }, 0); },
    memoryUsage: function () { return { rss: 0, heapTotal: 0, heapUsed: 0, external: 0 }; },
    hrtime: function (p) { var t = performance.now(), s = Math.floor(t / 1000), n = Math.floor((t % 1000) * 1e6); return p ? [s - p[0], n - p[1]] : [s, n]; },
    uptime: function () { return performance.now() / 1000; }
  };
  var os = {
    platform: function () { return 'win32'; }, type: function () { return 'Windows_NT'; }, release: function () { return '10.0'; }, arch: function () { return 'x64'; },
    homedir: function () { return root; }, tmpdir: function () { return root; }, hostname: function () { return 'rocketrpg'; },
    cpus: function () { return []; }, totalmem: function () { return 0; }, freemem: function () { return 0; }, EOL: '\r\n'
  };

  // ---- greenworks (Steam API, native module) ----
  // Steam releases ask greenworks for the game language set in Steam (e.g. Ib picks its first language this way).
  // Native modules cannot load here, so this stands in: for a game installed through Steam it reports Steam as running
  // with the game's Steam language; achievements/cloud/overlay are no-ops. Other copies behave as if Steam is not running.
  var greenworks = null;
  function makeGreenworks() {
    var lang = '';
    try { lang = String(host.GetSteamLanguage() || ''); } catch (e) {}
    var steam = lang !== '';
    function noop() {}
    function later(f) {
      var a = Array.prototype.slice.call(arguments, 1);
      if (typeof f === 'function') setTimeout(function () { f.apply(null, a); }, 0);
    }
    function unavailable(err) { later(err, new Error('Steam Cloud is not available')); }
    var user = {
      accountId: 0, steamId: '0', staticAccountId: '0', screenName: 'Player', level: 0,
      isValid: function () { return 1; }, getAccountID: function () { return 0; },
      getPersonaName: function () { return 'Player'; }, getRawSteamID: function () { return '0'; }, getStaticAccountID: function () { return '0'; }
    };
    var gw = {
      initAPI: function () { return steam; }, init: function () { return steam; }, isSteamRunning: function () { return steam; },
      restartAppIfNecessary: function () { return false; },
      getCurrentGameLanguage: function () { return lang || 'english'; },
      getCurrentUILanguage: function () { return lang || 'english'; },
      getSteamId: function () { return user; },
      getAppId: function () { return 0; },
      activateAchievement: function (name, ok) { later(ok); },
      clearAchievement: function (name, ok) { later(ok); },
      getAchievement: function (name, ok) { later(ok, false); },
      getAchievementNames: function () { return []; },
      getNumberOfAchievements: function () { return 0; },
      indicateAchievementProgress: function (name, cur, max, ok) { later(ok); },
      storeStats: function (ok) { later(ok); },
      isCloudEnabled: function () { return false; }, isCloudEnabledForUser: function () { return false; },
      enableCloud: noop,
      getCloudQuota: function (ok) { later(ok, 0, 0); },
      saveTextToFile: function (file, text, ok, err) { unavailable(err); },
      readTextFromFile: function (file, ok, err) { unavailable(err); },
      getNumberOfPlayers: function (ok) { later(ok, 0); },
      isGameOverlayEnabled: function () { return false; },
      activateGameOverlay: noop, activateGameOverlayToWebPage: noop, activateGameOverlayToStore: noop,
      getFriendCount: function () { return 0; }, getFriends: function () { return []; },
      on: function () { return gw; }, once: function () { return gw; }, removeListener: function () { return gw; }
    };
    // Anything else a plugin calls: do nothing instead of throwing.
    return typeof Proxy === 'function'
      ? new Proxy(gw, { get: function (t, k) { return k in t ? t[k] : (typeof k === 'string' && k !== 'then' && k !== 'toJSON' ? noop : undefined); } })
      : gw;
  }

  window.require = function (name) {
    switch (String(name)) {
      case 'fs': case 'original-fs': return fs;
      case 'path': return path;
      case 'os': return os;
      case 'nw.gui': return nw;
      case 'buffer': return { Buffer: Buffer };
    }
    if (/(^|[\/\\])greenworks(\.js)?$/i.test(String(name))) return greenworks || (greenworks = makeGreenworks());
    var e = new Error("Cannot find module '" + name + "'");
    e.code = 'MODULE_NOT_FOUND';
    throw e;
  };
  window.process = proc;
  window.nw = nw;
  window.global = window;
  if (typeof window.Buffer === 'undefined') window.Buffer = Buffer;
  window.__dirname = path.dirname(mainFile);
  window.__filename = mainFile;
  try {
    window.chrome = window.chrome || {};
    chrome.runtime = chrome.runtime || {};
    if (!chrome.runtime.reload) chrome.runtime.reload = function () { location.reload(); };
  } catch (e) {}
})();
