// Drives the REAL background.js against a fake `chrome` API (no browser needed).
const assert = require("assert");
const path = require("path");

function makeEvent() { const l = []; return { addListener: (f) => l.push(f), fire: async (...a) => { for (const f of l) await f(...a); }, listeners: l }; }

function makeChrome(hostReply) {
  const sent = [], cancelled = [], erased = [], badges = [], menus = [];
  const storage = {}; const session = {};
  const area = (o) => ({
    get: async (k) => { if (typeof k === "string") return k in o ? { [k]: o[k] } : {}; if (k && typeof k === "object") return { ...k, ...Object.fromEntries(Object.keys(k).filter((x) => x in o).map((x) => [x, o[x]])) }; return { ...o }; },
    set: async (v) => Object.assign(o, v), remove: async (k) => { delete o[k]; },
  });
  const c = {
    runtime: { sendNativeMessage: (host, msg, cb) => { sent.push({ host, msg }); setTimeout(() => cb(typeof hostReply === "function" ? hostReply(msg) : hostReply), 0); }, lastError: undefined,
               onInstalled: makeEvent(), onMessage: makeEvent() },
    downloads: { onCreated: makeEvent(), cancel: async (id) => cancelled.push(id), erase: async (q) => erased.push(q.id) },
    cookies: { getAll: async ({ url }) => /example\.com/.test(url) ? [{ name: "sid", value: "s1" }, { name: "pref", value: "dark" }] : [] },
    contextMenus: { removeAll: async () => { menus.length = 0; }, create: (o) => { menus.push(o); }, onClicked: makeEvent() },
    webRequest: { onHeadersReceived: makeEvent() },
    tabs: { onUpdated: makeEvent(), onRemoved: makeEvent(), query: async (q) => (q && q.active) ? [{ id: 7, title: "Some Video" }] : [{ id: 7, url: "https://example.com/watch" }, { id: 8, url: "https://other.org/" }, { id: 9, url: "https://example.com/other" }], get: async (id) => ({ id, title: "Some Video | Site" }) },
    storage: { local: area(storage), session: area(session) },
    action: { setBadgeText: (b) => badges.push(b), setBadgeBackgroundColor: () => {} },
  };
  return { chrome: c, sent, cancelled, erased, badges, storage, menus };
}

require(path.join(__dirname, "../../browser-extension/i18n.js"));   // shared texts (English unless a test switches the language)
async function load(hostReply) {
  const env = makeChrome(hostReply);
  delete require.cache[require.resolve(path.join(__dirname, "../../browser-extension/background.js"))];
  globalThis.chrome = env.chrome; delete globalThis.browser;
  Object.defineProperty(globalThis, "navigator", { value: { userAgent: "TestBrowser/1.0" }, configurable: true });
  require(path.join(__dirname, "../../browser-extension/background.js"));
  return env;
}
const ask = (env, message, sender = {}) => new Promise((resolve) => env.chrome.runtime.onMessage.listeners[0](message, sender, resolve));
const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));

let pass = 0, fail = 0;
async function test(name, fn) { try { await fn(); pass++; console.log("  PASS ", name); } catch (e) { fail++; console.log("  FAIL ", name, "\n       ", e.message); } }

(async () => {
  await test("captured download: cookies, referrer, UA and browser name are forwarded; browser copy cancelled + erased only after ack", async () => {
    const env = await load({ ok: true, id: 1 });
    await env.chrome.downloads.onCreated.fire({ id: 11, url: "https://files.example.com/a/b.zip", filename: "C:\\Users\\me\\Downloads\\b.zip", referrer: "https://example.com/page", mime: "application/zip" });
    assert.strictEqual(env.sent.length, 1);
    const m = env.sent[0].msg;
    assert.strictEqual(env.sent[0].host, "com.makan.downloadmanager");
    assert.strictEqual(m.url, "https://files.example.com/a/b.zip");
    assert.strictEqual(m.cookie, "sid=s1; pref=dark");
    assert.strictEqual(m.referrer, "https://example.com/page");
    assert.strictEqual(m.userAgent, "TestBrowser/1.0");
    assert.strictEqual(m.filePath, "C:\\Users\\me\\Downloads\\b.zip");
    assert.deepStrictEqual(env.cancelled, [11]); assert.deepStrictEqual(env.erased, [11]);
  });

  await test("host says no (or is missing): the browser keeps its own download", async () => {
    const env = await load({ ok: false, error: "The server sent a web page instead of a file" });
    await env.chrome.downloads.onCreated.fire({ id: 12, url: "https://example.com/login.php?f=1" });
    assert.strictEqual(env.sent.length, 1); assert.deepStrictEqual(env.cancelled, []);
    const { lastError } = await env.chrome.storage.local.get({ lastError: null });
    assert.match(lastError.message, /web page/);
    const env2 = await load(undefined); // no reply at all
    await env2.chrome.downloads.onCreated.fire({ id: 13, url: "https://example.com/a.zip" });
    assert.deepStrictEqual(env2.cancelled, []);
  });

  await test("blob:, data:, file:, extension-made downloads, disabled state and excluded sites are left alone", async () => {
    const env = await load({ ok: true });
    for (const url of ["blob:https://x/1", "data:text/plain,hi", "file:///c:/a.zip", "ftp://x/a.zip"]) await env.chrome.downloads.onCreated.fire({ id: 20, url });
    await env.chrome.downloads.onCreated.fire({ id: 21, url: "https://example.com/a.zip", byExtensionId: "other" });
    assert.strictEqual(env.sent.length, 0);
    await ask(env, { type: "saveSettings", enabled: true, exclude: ["example.com"] });
    await env.chrome.downloads.onCreated.fire({ id: 22, url: "https://cdn.example.com/a.zip" });
    assert.strictEqual(env.sent.length, 0, "subdomain of an excluded site");
    await env.chrome.downloads.onCreated.fire({ id: 23, url: "https://notexample.com/a.zip" });
    assert.strictEqual(env.sent.length, 1, "similar-looking other site is NOT excluded");
    await ask(env, { type: "saveSettings", enabled: false, exclude: [] });
    await env.chrome.downloads.onCreated.fire({ id: 24, url: "https://example.com/b.zip" });
    assert.strictEqual(env.sent.length, 1, "disabled");
  });

  await test("media sniffer: keeps video/HLS/DASH, drops segments, thumbnails, html; dedupes; per-tab; badge; reset on navigation", async () => {
    const env = await load({ ok: true });
    const hdr = (type, extra = {}) => [{ name: "Content-Type", value: type }, ...Object.entries(extra).map(([name, value]) => ({ name, value }))];
    const fire = (url, type, extra, status = 200, tabId = 7) => env.chrome.webRequest.onHeadersReceived.listeners[0]({ url, tabId, statusCode: status, responseHeaders: hdr(type, extra), documentUrl: "https://example.com/watch" });
    fire("https://cdn.example.com/v/movie.mp4", "video/mp4", { "Content-Length": "52428800" });
    fire("https://cdn.example.com/v/movie.mp4", "video/mp4", { "Content-Length": "52428800" });          // duplicate
    fire("https://cdn.example.com/v/x?range=0-99", "video/mp4", { "Content-Range": "bytes 0-99/9000000" }, 206); // partial => total size
    fire("https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl");
    fire("https://cdn.example.com/dash/manifest.mpd", "application/dash+xml");
    fire("https://cdn.example.com/hls/seg1.ts", "video/mp2t", { "Content-Length": "999999" });          // segment
    fire("https://cdn.example.com/thumb.mp4", "video/mp4", { "Content-Length": "2048" });               // tiny
    fire("https://cdn.example.com/page.html", "text/html", { "Content-Length": "90000" });
    fire("https://cdn.example.com/blocked.mp4", "video/mp4", {}, 403);                                    // error status
    fire("https://cdn.example.com/other.mp4", "video/mp4", { "Content-Length": "5000000" }, 200, 9);       // other tab
    await tick(50);
    const { media } = await ask(env, { type: "getMedia", tabId: 7 });
    assert.deepStrictEqual(media.map((e) => e.kind), ["file", "file", "hls", "dash"], JSON.stringify(media.map((e) => e.url)));
    assert.strictEqual(media[1].size, 9000000);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 9 })).media.length, 1);
    assert.ok(env.badges.some((b) => b.tabId === 7 && b.text === "4"), JSON.stringify(env.badges.slice(-3)));
    await env.chrome.tabs.onUpdated.fire(7, { status: "loading", url: "https://example.com/next" });
    await tick(30);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 7 })).media.length, 0, "navigation clears the list");
  });

  await test("popup actions: file media -> normal download with page cookies; HLS -> Media window request", async () => {
    const env = await load({ ok: true });
    await ask(env, { type: "downloadMedia", entry: { kind: "file", url: "https://cdn.example.com/v.mp4", type: "video/mp4", referrer: "https://example.com/watch" }, fileName: "Some Video.mp4" });
    let m = env.sent.at(-1).msg;
    assert.strictEqual(m.filePath, "Some Video.mp4"); assert.strictEqual(m.cookie, "sid=s1; pref=dark"); assert.strictEqual(m.kind, undefined);
    await ask(env, { type: "downloadMedia", entry: { kind: "hls", url: "https://cdn.example.com/m.m3u8", referrer: "https://example.com/watch" } });
    m = env.sent.at(-1).msg;
    assert.strictEqual(m.kind, "media"); assert.strictEqual(m.referrer, "https://example.com/watch"); assert.strictEqual(m.cookie, "sid=s1; pref=dark");
  });

  await test("download-all-links: one batch per site, each with ONLY its own site's cookies", async () => {
    const env = await load((msg) => ({ ok: true, count: msg.urls.length }));
    const r = await ask(env, { type: "downloadLinks", referrer: "https://example.com/list", urls: ["https://example.com/1.zip", "https://example.com/2.zip", "https://other.org/3.zip", "javascript:alert(1)"] });
    assert.strictEqual(env.sent.length, 2);
    const a = env.sent.find((s) => s.msg.urls[0].includes("example.com")).msg, b = env.sent.find((s) => s.msg.urls[0].includes("other.org")).msg;
    assert.strictEqual(a.kind, "batch"); assert.strictEqual(a.cookie, "sid=s1; pref=dark"); assert.strictEqual(a.urls.length, 2);
    assert.strictEqual(b.cookie, null, "other site must not receive example.com cookies");
    assert.strictEqual(r.count, 3);
  });

  await test("status: ping uses lowercase kind (so the native host answers it without launching the app)", async () => {
    const env = await load({ ok: true, version: "10.0.0", running: true });
    const s = await ask(env, { type: "status" });
    assert.strictEqual(env.sent.at(-1).msg.kind, "ping");
    assert.strictEqual(s.ping.version, "10.0.0"); assert.strictEqual(s.settings.enabled, true);
  });

  await test("magnet message: a magnet link goes to the native host as an explicit request; junk is rejected without asking the host", async () => {
    const env = await load({ ok: true, id: 5 });
    const reply = await ask(env, { type: "magnet", url: "magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038&dn=Movie" });
    assert.ok(reply.ok, JSON.stringify(reply));
    const call = env.sent.find((s) => typeof s.msg.url === "string" && s.msg.url.startsWith("magnet:"));
    assert.ok(call, "the magnet link reached the native host");
    assert.strictEqual(call.msg.explicit, true, "capture rules (file type/site) must not apply to something the user clicked");
    const bad = await ask(env, { type: "magnet", url: "https://example.com/not-a-magnet" });
    assert.ok(!bad.ok, "a non-magnet URL is refused by this message type, never sent as one");
  });

  // ---- IDM-style menu ------------------------------------------------------------------------------------------
  const hostForMenu = (msg) => {
    if (msg.kind === "streams") {
      if (msg.url.endsWith("/hls/master.m3u8")) return { ok: true, streams: [
        { kind: "hls", quality: "640x360", height: 360, bitrate: 800000, codec: "avc1.64001f,mp4a.40.2", url: "https://cdn.example.com/hls/v360/index.m3u8" },
        { kind: "hls", quality: "1280x720", height: 720, bitrate: 2800000, codec: "avc1.640028", url: "https://cdn.example.com/hls/v720/index.m3u8", audioUrl: "https://cdn.example.com/hls/audio/en.m3u8" } ] };
      if (msg.url.endsWith("/broken.m3u8")) return { ok: false, error: "HTTP 403" };
      if (msg.url.endsWith(".mpd")) return { ok: true, streams: [
        { kind: "dash", quality: "640x360", height: 360, bitrate: 500000, duration: 12, url: msg.url + "#makan-v=v1&makan-a=a1" },
        { kind: "dash", quality: "1280x720", height: 720, bitrate: 1500000, duration: 12, url: msg.url + "#makan-v=v2&makan-a=a1" } ] };
      return { ok: true, streams: [{ kind: "hls", quality: "Source", url: msg.url }] };   // a plain media playlist
    }
    return { ok: true, id: 1 };
  };
  const sniff = (env, url, type, extra = {}, tabId = 7) => env.chrome.webRequest.onHeadersReceived.listeners[0]({
    url, tabId, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: type }, ...Object.entries(extra).map(([name, value]) => ({ name, value }))], documentUrl: "https://example.com/watch" });

  await test("menu: master expands to TS+MP4 per quality (like IDM), variant/audio playlists are not listed twice, direct file included", async () => {
    const env = await load(hostForMenu);
    sniff(env, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl");
    sniff(env, "https://cdn.example.com/hls/v720/index.m3u8", "application/vnd.apple.mpegurl");
    sniff(env, "https://cdn.example.com/hls/audio/en.m3u8", "application/vnd.apple.mpegurl");
    sniff(env, "https://cdn.example.com/files/movie.mp4", "video/mp4", { "Content-Length": "52428800" });
    await tick(50);
    const { items } = await ask(env, { type: "menu", tabId: 7 });
    assert.deepStrictEqual(items.map((i) => i.text), [
      "Some Video | Site, TS file, quality 360p, 800 kbps",
      "Some Video | Site, MP4 file, quality 360p, 800 kbps",
      "Some Video | Site, TS file, quality 720p HD, 2800 kbps (+ audio track)",
      "Some Video | Site, MP4 file, quality 720p HD, 2800 kbps (+ audio track)",
      "Some Video | Site, MP4 file, size 50.0 MB" ]);
    const streamCalls = env.sent.filter((s) => s.msg.kind === "streams");
    assert.strictEqual(streamCalls.length, 3, "one playlist lookup per detected playlist");
    assert.strictEqual(streamCalls[0].msg.cookie, "sid=s1; pref=dark"); assert.strictEqual(streamCalls[0].msg.referrer, "https://example.com/watch");
    await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(env.sent.filter((s) => s.msg.kind === "streams").length, 3, "playlist lookups are cached");
  });

  await test("menu: picking a quality sends the stream (cookie of the stream host, referrer, format, title, audio); prompt is allowed", async () => {
    const env = await load(hostForMenu);
    sniff(env, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl"); await tick(30);
    await ask(env, { type: "menu", tabId: 7 });
    const r = await ask(env, { type: "menuPick", tabId: 7, index: 3, all: false });
    assert.strictEqual(r.ok, true);
    const m = env.sent.at(-1).msg;
    assert.strictEqual(m.kind, "stream"); assert.strictEqual(m.url, "https://cdn.example.com/hls/v720/index.m3u8");
    assert.strictEqual(m.audioUrl, "https://cdn.example.com/hls/audio/en.m3u8"); assert.strictEqual(m.format, "mp4");
    assert.strictEqual(m.title, "Some Video | Site"); assert.strictEqual(m.quality, "720p HD");
    assert.strictEqual(m.cookie, "sid=s1; pref=dark"); assert.strictEqual(m.referrer, "https://example.com/watch"); assert.ok(!m.noPrompt);
  });

  await test("menu: Download all queues one MP4 per quality without prompting, with distinct names", async () => {
    const env = await load(hostForMenu);
    sniff(env, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl");
    sniff(env, "https://cdn.example.com/files/movie.mp4", "video/mp4", { "Content-Length": "52428800" }); await tick(30);
    await ask(env, { type: "menu", tabId: 7 });
    const before = env.sent.length;
    const r = await ask(env, { type: "menuPick", tabId: 7, index: -1, all: true });
    const sent = env.sent.slice(before).map((s) => s.msg);
    assert.strictEqual(r.ok, true); assert.match(r.message, /3 downloads/);
    const streams = sent.filter((m) => m.kind === "stream");
    assert.deepStrictEqual(streams.map((m) => m.title), ["Some Video | Site 360p", "Some Video | Site 720p HD"]);
    assert.ok(streams.every((m) => m.noPrompt === true && m.format === "mp4"));
    assert.strictEqual(sent.filter((m) => m.kind === undefined && m.url.endsWith("movie.mp4")).length, 1);
  });

  await test("menu: unreadable playlist shows a disabled explanation; DASH lists MP4 per quality; direct <video> src is offered", async () => {
    const env = await load(hostForMenu);
    sniff(env, "https://cdn.example.com/hls/broken.m3u8", "application/vnd.apple.mpegurl");
    sniff(env, "https://cdn.example.com/dash/manifest.mpd", "application/dash+xml"); await tick(30);
    const { items } = await ask(env, { type: "menu", tabId: 7, directUrl: "https://media.example.com/clip.webm" });
    assert.strictEqual(items[0].disabled, true); assert.match(items[0].text, /can't read the playlist: HTTP 403/);
    assert.match(items[1].text, /MP4 file, 12 sec, quality 360p, 500 kbps/);
    assert.match(items[2].text, /MP4 file, 12 sec, quality 720p HD, 1500 kbps/);
    assert.match(items[3].text, /WEBM file/);
    assert.strictEqual(items.length, 4, "DASH has no TS variant");
    const r = await ask(env, { type: "menuPick", tabId: 7, index: 2, all: false });
    const m = env.sent.at(-1).msg;
    assert.strictEqual(m.kind, "stream"); assert.strictEqual(m.format, "mp4"); assert.ok(m.url.endsWith("/dash/manifest.mpd#makan-v=v2&makan-a=a1")); assert.strictEqual(r.ok, true);
    const bad = await ask(env, { type: "menuPick", tabId: 7, index: 0, all: false });
    assert.strictEqual(bad.ok, false);
  });

  await test("menu: pick works after the service worker restarted (cache lost -> rebuilt in the same order)", async () => {
    const env = await load(hostForMenu);
    sniff(env, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl"); await tick(30);
    const { items } = await ask(env, { type: "menu", tabId: 7 });
    assert.ok(items.length === 4);
    const env2 = await load(hostForMenu);            // fresh module state, same tab data lives only in this env...
    sniff(env2, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl"); await tick(30);
    const r = await ask(env2, { type: "menuPick", tabId: 7, index: 1, all: false });   // no menu was requested before picking
    assert.strictEqual(r.ok, true); assert.strictEqual(env2.sent.at(-1).msg.format, "mp4");
  });

  await test("sniffer: octet-stream / unlabeled media by extension, .m3u8 in the query, direct video pages; images and html ignored", async () => {
    const env = await load({ ok: true });
    const fire = (url, type, extra = {}, reqType) => env.chrome.webRequest.onHeadersReceived.listeners[0]({ url, tabId: 7, statusCode: 200, type: reqType, responseHeaders: type === null ? [] : [{ name: "Content-Type", value: type }, ...Object.entries(extra).map(([name, value]) => ({ name, value }))] });
    fire("https://cdn.example.com/dl/movie.mp4", "application/octet-stream", { "Content-Length": "9000000" });
    fire("https://cdn.example.com/dl/clip.webm", null);
    fire("https://cdn.example.com/play?file=stream.m3u8&t=1", "text/plain");
    fire("https://cdn.example.com/get.php?id=5", "application/octet-stream", { "Content-Length": "9000000" });      // unknown binary: not media
    fire("https://cdn.example.com/x.mp4", "text/html", { "Content-Length": "9000000" });                           // error page named .mp4
    fire("https://cdn.example.com/poster.mp4", "video/mp4", { "Content-Length": "9000000" }, "image");            // wrong request type
    fire("https://cdn.example.com/watch/direct", "video/mp4", { "Content-Length": "30000000" }, "main_frame");     // a video opened directly in the tab
    await tick(50);
    const { media } = await ask(env, { type: "getMedia", tabId: 7 });
    assert.deepStrictEqual(media.map((m) => [m.kind, new URL(m.url).pathname]), [["file", "/dl/movie.mp4"], ["file", "/dl/clip.webm"], ["hls", "/play"], ["file", "/watch/direct"]]);
  });

  await test("page scan: <video src>, og:video, JSON in scripts are added (deduplicated, non-media ignored); needs a tab", async () => {
    const env = await load({ ok: true });
    const sender = { tab: { id: 7, url: "https://example.com/watch" } };
    sniff(env, "https://cdn.example.com/files/movie.mp4", "video/mp4", { "Content-Length": "9000000" }); await tick(30);
    const r = await ask(env, { type: "pageUrls", referrer: "https://example.com/watch", urls: [
      "https://cdn.example.com/files/movie.mp4", "https://cdn.example.com/player/master.m3u8?token=1", "https://cdn.example.com/a.mpd",
      "https://cdn.example.com/cover.jpg", "blob:https://example.com/123", "javascript:alert(1)", "https://cdn.example.com/song.mp3" ] }, sender);
    assert.strictEqual(r.ok, true); await tick(30);
    const { media } = await ask(env, { type: "getMedia", tabId: 7 });
    assert.deepStrictEqual(media.map((m) => m.kind), ["file", "hls", "dash", "file"]);
    assert.strictEqual(media[1].referrer, "https://example.com/watch"); assert.strictEqual(media[1].fromPage, true);
    assert.strictEqual((await ask(env, { type: "pageUrls", urls: ["https://x/a.mp4"] }, {})).ok, false, "no tab, no entry");
    assert.strictEqual((await ask(env, { type: "pageMedia" }, sender)).count, 4);
  });

  await test("requests without a tab (service worker / worker) are handed to open tabs of the same site only", async () => {
    const env = await load({ ok: true });
    env.chrome.webRequest.onHeadersReceived.listeners[0]({ url: "https://cdn.example.com/w/master.m3u8", tabId: -1, statusCode: 200, initiator: "https://example.com",
      responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
    await tick(80);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 7 })).media.length, 1);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 9 })).media.length, 1);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 8 })).media.length, 0, "different site is not touched");
  });

  await test("menu text: length and estimated size like IDM ('33 min 10 sec, quality 1080p HD, 4912 kbps, about 1.1 GB')", async () => {
    const env = await load((msg) => msg.kind === "streams"
      ? { ok: true, streams: [{ kind: "hls", quality: "1920x1080", height: 1080, bitrate: 4912000, duration: 1990, url: "https://cdn.example.com/hls/v1080/index.m3u8" },
                              { kind: "hls", quality: "640x360", height: 360, bitrate: 530000, duration: 45, url: "https://cdn.example.com/hls/v360/index.m3u8" }] }
      : { ok: true });
    sniff(env, "https://cdn.example.com/hls/master.m3u8", "application/vnd.apple.mpegurl"); await tick(30);
    const { items } = await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(items[0].text, "Some Video | Site, TS file, 45 sec, quality 360p, 530 kbps, about 2.8 MB");
    assert.strictEqual(items[3].text, "Some Video | Site, MP4 file, 33 min 10 sec, quality 1080p HD, 4912 kbps, about 1.1 GB");
  });

  await test("overlay reports are kept per tab and frame and can be read back (popup shows why a button is missing)", async () => {
    const env = await load({ ok: true });
    await ask(env, { type: "overlayReport", info: { url: "https://example.com/", top: true, videos: 1, mediaCount: 0, shown: false, reason: "a video is here but nothing downloadable has been detected yet" } }, { tab: { id: 7 }, frameId: 0 });
    await ask(env, { type: "overlayReport", info: { url: "https://player.example.net/", top: false, videos: 1, mediaCount: 2, shown: true, mode: "anchored" } }, { tab: { id: 7 }, frameId: 5 });
    const { frames } = await ask(env, { type: "overlayInfo", tabId: 7 });
    assert.strictEqual(frames.length, 2); assert.strictEqual(frames.find((f) => f.shown).mode, "anchored");
    await env.chrome.tabs.onUpdated.fire(7, { status: "loading", url: "https://example.com/next" }); await tick(30);
    assert.strictEqual((await ask(env, { type: "overlayInfo", tabId: 7 })).frames.length, 0, "navigation clears it");
  });

  await test("download all links: page links go to Makan with one cookie per host (image hosts skipped); errors are explained", async () => {
    const env = await load({ ok: true, count: 3 });
    const page = { url: "https://example.com/serial", title: "Lanterns S01", links: [
      { url: "https://example.com/watch/1.html", text: "Watch 1", kind: "link" }, { url: "https://dl.example.com/e1.mkv", text: "Episode 1", kind: "link" },
      { url: "https://files.other.org/e2.mkv", text: "Episode 2", kind: "link" }, { url: "https://img.example.com/cover.jpg", text: "", kind: "image" } ] };
    let asked = null;
    env.chrome.tabs.sendMessage = async (id, msg, opts) => { asked = { id, msg, opts }; return page; };
    const r = await ask(env, { type: "grabLinks", tabId: 7 });
    assert.strictEqual(r.ok, true);
    assert.deepStrictEqual(asked, { id: 7, msg: { type: "collectLinks", selectionOnly: false }, opts: { frameId: 0 } });
    const m = env.sent.at(-1).msg;
    assert.strictEqual(m.kind, "links"); assert.strictEqual(m.items.length, 4); assert.strictEqual(m.referrer, "https://example.com/serial"); assert.strictEqual(m.pageTitle, "Lanterns S01");
    assert.deepStrictEqual(Object.keys(m.cookies).sort(), ["dl.example.com", "example.com"], "cookies only for hosts that have them, none for other.org or the image host");
    assert.strictEqual(m.cookies["dl.example.com"], "sid=s1; pref=dark");
    env.chrome.tabs.sendMessage = async () => { throw new Error("no receiver"); };
    const bad = await ask(env, { type: "grabLinks", tabId: 7 });
    assert.strictEqual(bad.ok, false); assert.match(bad.error, /Reload the page/);
    env.chrome.tabs.sendMessage = async () => ({ links: [] });
    assert.match((await ask(env, { type: "grabLinks", tabId: 7 })).error, /No links/);
  });

  await test("selected links: the frame that holds the selection is asked, and only for the selection", async () => {
    const env = await load({ ok: true, count: 3 });
    let asked = null;
    env.chrome.tabs.sendMessage = async (id, msg, opts) => { asked = { id, msg, opts }; return { url: "https://example.com/dl", title: "Assassins", links: [{ url: "https://edge14.example.com/g/p1.rar", text: "Part 1", kind: "link" }] }; };
    const r = await ask(env, { type: "grabSelection" }, { tab: { id: 7, url: "https://example.com/dl" }, frameId: 3 });
    assert.strictEqual(r.ok, true);
    assert.deepStrictEqual(asked, { id: 7, msg: { type: "collectLinks", selectionOnly: true }, opts: { frameId: 3 } });
    assert.strictEqual(env.sent.at(-1).msg.kind, "links");
    const popup = await ask(env, { type: "grabSelectedLinks", tabId: 7 });
    assert.strictEqual(popup.ok, true, "the popup can send the highlighted batch too");
    assert.deepStrictEqual(asked, { id: 7, msg: { type: "collectLinks", selectionOnly: true }, opts: { frameId: 0 } });
    env.chrome.tabs.sendMessage = async () => ({ links: [] });
    assert.match((await ask(env, { type: "grabSelection" }, { tab: { id: 7 }, frameId: 0 })).error, /No links in the selection/);
  });

  await test("a download that Makan's options leave to the browser is 'skipped': no error, no badge, the browser keeps it", async () => {
    const env = await load({ ok: false, skipped: true, error: "Files of type .html are left to the browser (Options > File types)." });
    await env.chrome.downloads.onCreated.fire({ id: 41, url: "https://example.com/page.html" });
    assert.deepStrictEqual(env.cancelled, []);
    const { lastError } = await env.chrome.storage.local.get({ lastError: null });
    assert.strictEqual(lastError, null, "skipped is not a problem to report");
  });

  await test("Alt + click on a link: the browser downloads it (Makan is not asked); later downloads are captured again", async () => {
    const env = await load({ ok: true, id: 9 });
    await ask(env, { type: "altClick" });
    await env.chrome.downloads.onCreated.fire({ id: 42, url: "https://files.example.com/alt.zip" });
    assert.strictEqual(env.sent.length, 0, "nothing sent to Makan"); assert.deepStrictEqual(env.cancelled, []);
  });

  await test("things picked from the video menu are 'explicit' (the file-type rules do not apply to them)", async () => {
    const env = await load({ ok: true, id: 1 });
    const sniffFile = (url, type, extra = {}) => env.chrome.webRequest.onHeadersReceived.listeners[0]({ url, tabId: 7, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: type }, ...Object.entries(extra).map(([name, value]) => ({ name, value }))], documentUrl: "https://example.com/watch" });
    sniffFile("https://cdn.example.com/files/clip.mp4", "video/mp4", { "Content-Length": "5000000" });
    await tick(50);
    const r = await ask(env, { type: "menuPick", tabId: 7, index: 0, all: false });
    assert.strictEqual(r.ok, true, JSON.stringify(r));
    assert.strictEqual(env.sent.at(-1).msg.explicit, true);
  });

  await test("Persian: the language comes with Makan's reply; menus, messages and video-menu words are translated (and back to English)", async () => {
    const env = await load({ ok: true, count: 3, language: "fa" });
    await ask(env, { type: "status" });                       // any reply from Makan carries the language
    await tick(30);
    assert.strictEqual(globalThis.MakanI18n.lang, "fa");
    const titles = env.menus.map((m) => m.title);
    assert.ok(titles.includes("دانلود با اپسیلون دانلود منیجر") && titles.includes("دانلود همهٔ لینک‌ها با اپسیلون دانلود منیجر…"), JSON.stringify(titles));
    env.chrome.tabs.sendMessage = async () => ({ links: [] });
    const err = await ask(env, { type: "grabSelection" }, { tab: { id: 7 }, frameId: 0 });
    assert.strictEqual(err.error, "هیچ لینکی در بخش انتخاب‌شده نیست.");
    const stored = await env.chrome.storage.local.get({ lang: "en" });
    assert.strictEqual(stored.lang, "fa", "remembered for the popup and the page scripts");
    globalThis.MakanI18n.setLang("en");
  });

  const YT = "https://www.youtube.com/watch?v=abcdefghijk";
  const ytHost = (msg) => msg.kind === "ytformats"
    ? { ok: true, title: "The #1 Workout", durationSeconds: 630, options: [
        { key: "v1080", label: "1080p HD", kind: "video", height: 1080, extension: "mp4", size: 350000000 },
        { key: "v360", label: "360p", kind: "video", height: 360, extension: "mp4", size: 20000000 },
        { key: "a", label: "Audio only (M4A)", kind: "audio", extension: "m4a", size: 5000000 },
        { key: "s:en", label: "Subtitles: en", kind: "subtitle", extension: "srt" }] }
    : { ok: true, id: 1 };

  await test("YouTube: the menu comes from yt-dlp (qualities, audio, subtitles); a pick sends 'ytdl'; there is no 'Download all'", async () => {
    const env = await load(ytHost);
    env.chrome.tabs.get = async (id) => ({ id, title: "The #1 Workout - YouTube", url: YT });
    const menu = await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(menu.items.length, 4);
    assert.match(menu.items[0].text, /^The #1 Workout, MP4 file, 10 min 30 sec, quality 1080p HD, about 334 MB$/);
    assert.match(menu.items[2].text, /M4A audio, about 4\.8 MB/);
    assert.match(menu.items[3].text, /SRT file, en subtitles/);
    assert.strictEqual(menu.canAll, false, "all qualities at once would be silly");
    assert.ok(env.sent.some((m) => m.msg.kind === "ytformats" && m.msg.url === YT));
    const before = env.sent.length;
    await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(env.sent.length, before, "answers are cached for a few minutes");
    const r = await ask(env, { type: "menuPick", tabId: 7, index: 0, all: false });
    assert.strictEqual(r.ok, true);
    const m = env.sent.at(-1).msg;
    assert.deepStrictEqual([m.kind, m.url, m.format, m.title], ["ytdl", YT, "v1080", "The #1 Workout [1080p]"]);
    assert.strictEqual(m.size, 350000000, "the desktop receives the same combined size estimate shown by the extension");
    await ask(env, { type: "menuPick", tabId: 7, index: 2, all: false });
    assert.strictEqual(env.sent.at(-1).msg.title, "The #1 Workout [audio]");
    await ask(env, { type: "menuPick", tabId: 7, index: 3, all: false });
    assert.strictEqual(env.sent.at(-1).msg.format, "s:en");
  });

  await test("YouTube without yt-dlp: the menu explains the set-up and the entry opens Makan; other errors are shown as a disabled line", async () => {
    const env = await load((msg) => msg.kind === "ytformats" ? { ok: false, needsSetup: true, error: "yt-dlp is not set up." } : { ok: true });
    env.chrome.tabs.get = async (id) => ({ id, title: "Clip - YouTube", url: YT });
    const menu = await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(menu.items.length, 1); assert.match(menu.items[0].text, /YouTube needs yt-dlp/); assert.strictEqual(menu.items[0].disabled, false);
    const r = await ask(env, { type: "menuPick", tabId: 7, index: 0, all: false });
    assert.strictEqual(r.ok, true); assert.match(r.message, /Options/); assert.strictEqual(env.sent.at(-1).msg.kind, "show");
    const env2 = await load((msg) => msg.kind === "ytformats" ? { ok: false, error: "Video unavailable" } : { ok: true });
    env2.chrome.tabs.get = async (id) => ({ id, title: "Gone - YouTube", url: YT });
    const m2 = await ask(env2, { type: "menu", tabId: 7 });
    assert.match(m2.items[0].text, /can't read this video: Video unavailable/); assert.strictEqual(m2.items[0].disabled, true);
  });

  await test("other pages with nothing sniffed get a quiet second chance through yt-dlp; without yt-dlp nothing changes", async () => {
    const env = await load(ytHost);
    env.chrome.tabs.get = async (id) => ({ id, title: "Some page", url: "https://example.com/watch?x=1" });
    const menu = await ask(env, { type: "menu", tabId: 7 });
    assert.strictEqual(menu.items.length, 4);
    const env2 = await load((msg) => msg.kind === "ytformats" ? { ok: false, needsSetup: true } : { ok: true });
    env2.chrome.tabs.get = async (id) => ({ id, title: "Some page", url: "https://example.com/watch?x=1" });
    assert.strictEqual((await ask(env2, { type: "menu", tabId: 7 })).items.length, 0, "the usual 'no stream found' note stays");
  });

  await test("YouTube tabs always offer the button (the page player is not sniffable)", async () => {
    const env = await load({ ok: true });
    assert.strictEqual((await ask(env, { type: "pageMedia" }, { tab: { id: 7, url: YT } })).count, 1);
    assert.strictEqual((await ask(env, { type: "pageMedia" }, { tab: { id: 7, url: "https://example.com/" } })).count, 0);
  });

  await test("manifests: both browsers have their toolbar popup, the same version as VERSION.txt, and every file they name exists", async () => {
    const fs = require("fs"), path = require("path");
    const root = path.join(__dirname, "..", "..");
    const version = fs.readFileSync(path.join(root, "VERSION.txt"), "utf8").trim();
    for (const dir of ["browser-extension", "browser-extension-firefox"]) {
      const m = JSON.parse(fs.readFileSync(path.join(root, dir, "manifest.json"), "utf8"));
      assert.strictEqual(m.version, version, dir + " version");
      assert.strictEqual(m.manifest_version, 3, dir + " manifest_version");
      assert.strictEqual(m.action?.default_popup, "popup.html", dir + " needs its toolbar popup");
      for (const p of ["nativeMessaging", "downloads", "storage", "tabs"]) assert.ok(m.permissions.includes(p), dir + " permission " + p);
      const files = [...(m.content_scripts || []).flatMap((c) => c.js), m.action.default_popup, ...Object.values(m.icons)];
      files.push(...(m.background?.scripts || []), ...(m.background?.service_worker ? [m.background.service_worker] : []));
      for (const f of files) assert.ok(fs.existsSync(path.join(root, dir, f)), dir + " is missing " + f);
      assert.ok(!("version_name" in m) || dir === "browser-extension", "Firefox does not know version_name");
    }
    const chrome = JSON.parse(fs.readFileSync(path.join(root, "browser-extension", "manifest.json"), "utf8"));
    const firefox = JSON.parse(fs.readFileSync(path.join(root, "browser-extension-firefox", "manifest.json"), "utf8"));
    assert.ok(firefox.browser_specific_settings?.gecko?.id, "Firefox needs an add-on id");
    assert.ok(chrome.key, "the Chrome key keeps the extension id stable");
    for (const f of ["background.js", "content.js", "popup.js", "i18n.js", "popup.html"])
      assert.strictEqual(fs.readFileSync(path.join(root, "browser-extension", f), "utf8"), fs.readFileSync(path.join(root, "browser-extension-firefox", f), "utf8"), f + " must be identical in both extensions");
  });

  // ---- Firefox flavour: `browser` namespace, promise-based sendNativeMessage, browserAction, no storage.session ----
  async function loadFirefox(hostBehaviour) {
    const env = makeChrome(null);
    const c = env.chrome;
    c.runtime.sendNativeMessage = (host, msg) => { env.sent.push({ host, msg }); return hostBehaviour(msg); };
    c.browserAction = c.action; delete c.action;
    delete c.storage.session;
    delete require.cache[require.resolve(path.join(__dirname, "../../browser-extension-firefox/background.js"))];
    delete globalThis.chrome; globalThis.browser = c;
    Object.defineProperty(globalThis, "navigator", { value: { userAgent: "Firefox/140" }, configurable: true });
    require(path.join(__dirname, "../../browser-extension-firefox/background.js"));
    return env;
  }

  await test("Firefox: capture works through promise API + browserAction; cancels and erases after ack", async () => {
    const env = await loadFirefox(async () => ({ ok: true, id: 5 }));
    await env.chrome.downloads.onCreated.fire({ id: 31, url: "https://files.example.com/x.iso", filename: "/home/me/x.iso", referrer: "https://example.com/" });
    assert.strictEqual(env.sent.length, 1);
    assert.strictEqual(env.sent[0].msg.cookie, "sid=s1; pref=dark");
    assert.strictEqual(env.sent[0].msg.userAgent, "Firefox/140");
    assert.deepStrictEqual(env.cancelled, [31]); assert.deepStrictEqual(env.erased, [31]);
  });

  await test("Firefox: 'No such native application' rejection is reported, browser keeps the download, popup gets the message", async () => {
    const env = await loadFirefox(async () => { throw new Error("No such native application com.makan.downloadmanager"); });
    await env.chrome.downloads.onCreated.fire({ id: 32, url: "https://example.com/y.zip" });
    assert.deepStrictEqual(env.cancelled, []);
    const s = await ask(env, { type: "status" });
    assert.match(s.ping.error, /No such native application/);
  });

  await test("Firefox: media sniffer works without storage.session (falls back to local storage)", async () => {
    const env = await loadFirefox(async () => ({ ok: true }));
    env.chrome.webRequest.onHeadersReceived.listeners[0]({ url: "https://cdn.example.com/a.mp4", tabId: 3, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: "video/mp4" }, { name: "Content-Length", value: "9000000" }] });
    await tick(50);
    assert.strictEqual((await ask(env, { type: "getMedia", tabId: 3 })).media.length, 1);
  });

  console.log(`\n${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})();
