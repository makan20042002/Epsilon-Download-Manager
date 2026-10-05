// Epsilon Download Manager – background logic shared by the Chrome/Edge (MV3) and Firefox (MV2) builds.
// Everything is promise based; `ext` is `browser` on Firefox and `chrome` elsewhere.
// texts in English / Persian (i18n.js is loaded first: importScripts in the Chrome service worker, the scripts list in Firefox)
if (typeof importScripts === "function" && !globalThis.MakanI18n) { try { importScripts("i18n.js"); } catch { /* English only */ } }
const I18N = globalThis.MakanI18n || { lang: "en", setLang() {}, t: (s) => s, tf: (s, ...a) => s.replace(/\{(\d)\}/g, (_, i) => (a[+i] !== undefined ? a[+i] : "")) };
const t = (s) => I18N.t(s), tf = (s, ...a) => I18N.tf(s, ...a);

const ext = globalThis.browser ?? globalThis.chrome;
const IS_FIREFOX = typeof globalThis.browser !== "undefined";
const HOST = "com.makan.downloadmanager";
const action = ext.action ?? ext.browserAction;

const DEFAULTS = { enabled: true, exclude: [] };
const pending = new Set();

// ------------------------------------------------------------------ talking to the desktop app

function callHost(payload) {
  return new Promise((resolve) => {
    const done = (r) => { noteLanguage(r); noteAppearance(r); resolve(r || { ok: false, error: t("No response from Epsilon Download Manager.") }); };
    const fail = (e) => resolve({ ok: false, error: e?.message || String(e) });
    try {
      if (IS_FIREFOX) ext.runtime.sendNativeMessage(HOST, payload).then(done, fail);
      else ext.runtime.sendNativeMessage(HOST, payload, (r) => {
        const err = ext.runtime.lastError;
        err ? fail(err) : done(r);
      });
    } catch (e) { fail(e); }
  });
}

/** Remember the desktop app's resolved palette so the popup and in-page controls can follow it. */
function noteAppearance(reply) {
  const theme = reply && reply.theme;
  if (!["obsidian-gold", "platinum-blue", "royal-amethyst", "emerald-executive", "champagne-minimal", "graphite-copper", "sapphire-noir", "ivory-luxe", "rose-titanium", "arctic-glass", "dracula"].includes(theme)) return;
  try { ext.storage.local.set({ appTheme: theme }); } catch { /* cosmetic */ }
}

/** Epsilon's replies carry its interface language; the extension follows it (menus are rebuilt when it changes). */
function noteLanguage(reply) {
  const lang = reply && reply.language;
  if (!lang || lang === I18N.lang) return;
  I18N.setLang(lang);
  try { ext.storage.local.set({ lang: I18N.lang }); } catch { /* cosmetic */ }
  try { createMenus(); } catch { /* menus are recreated on the next start */ }
}
try { ext.storage.local.get({ lang: "en" }).then((r) => { if (r && r.lang !== I18N.lang) { I18N.setLang(r.lang); try { createMenus(); } catch { /* ignore */ } } }, () => {}); } catch { /* ignore */ }

// Alt + click on a link: the user wants the browser to download it (like IDM's "prevent" key)
let lastAltClick = 0;

async function settings() {
  const stored = await ext.storage.local.get(DEFAULTS);
  return { enabled: stored.enabled !== false, exclude: Array.isArray(stored.exclude) ? stored.exclude : [] };
}

/** Cookie header for a URL, including HttpOnly cookies (this is what makes logged-in downloads work). */
async function cookieHeader(url) {
  try {
    const cookies = await ext.cookies.getAll({ url });
    return cookies.length ? cookies.map((c) => `${c.name}=${c.value}`).join("; ") : null;
  } catch { return null; }
}

function isHttp(url) { return /^https?:\/\//i.test(url || ""); }

function hostExcluded(url, list) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    return list.some((entry) => { const e = entry.trim().toLowerCase(); return e && (host === e || host.endsWith("." + e)); });
  } catch { return false; }
}

/** Sends one URL to Epsilon with the browser session (cookies, referrer, user agent). */
async function sendUrl(url, { filePath = null, referrer = null, mime = null, kind = null, explicit = false } = {}) {
  const payload = {
    url, filePath, mime, referrer,
    cookie: await cookieHeader(url),
    userAgent: navigator.userAgent,
    priority: 5,
  };
  if (kind) payload.kind = kind;
  if (explicit) payload.explicit = true;      // the user asked for it: Epsilon's file-type / site rules do not apply
  return callHost(payload);
}

function flash(text, color) {
  try {
    action.setBadgeBackgroundColor({ color });
    action.setBadgeText({ text });
    setTimeout(() => refreshBadgeForActiveTab(), 2500);
  } catch { /* badge is cosmetic */ }
}

// ------------------------------------------------------------------ capture browser downloads

ext.downloads.onCreated.addListener(async (item) => {
  const url = item.finalUrl || item.url;
  if (!isHttp(url) || item.byExtensionId || pending.has(item.id)) return; // blob:, data:, file: and other extensions' downloads stay with the browser
  const s = await settings();
  if (!s.enabled || hostExcluded(url, s.exclude)) return;
  if (Date.now() - lastAltClick < 3000) return;    // Alt, Ctrl or Cmd was held on the click that started this: the browser keeps this one

  pending.add(item.id);
  let paused = false;
  let captured = false;
  try {
    // Stop the browser from racing several megabytes ahead while Epsilon checks and accepts the link.
    // If Epsilon declines or is unavailable, finally resumes the exact same browser download.
    try { await ext.downloads.pause(item.id); paused = true; } catch { /* very small downloads may already be done */ }
    const response = await sendUrl(url, { filePath: item.filename || null, referrer: item.referrer || null, mime: item.mime || null });
    // Never cancel the browser's own transfer unless the desktop app has acknowledged the job.
    if (response?.ok === true) {
      captured = true;
      try { await ext.downloads.cancel(item.id); } catch { /* already finished/cancelled */ }
      try { await ext.downloads.erase({ id: item.id }); } catch { /* keep history entry if erase is refused */ }
      flash("✓", "#2e9e5b");
    } else if (response?.skipped) {
      // Epsilon's options (file type, site, browser) leave this download to the browser: not an error, nothing to report
    } else {
      await ext.storage.local.set({ lastError: { message: response?.error || t("Unknown error"), url, at: Date.now() } });
      flash("!", "#d64545"); // the browser simply continues its own download
    }
  } finally {
    if (paused && !captured) {
      try { await ext.downloads.resume(item.id); } catch { /* already completed or removed */ }
    }
    pending.delete(item.id);
  }
});

// ------------------------------------------------------------------ context menus

function createMenus() {
  ext.contextMenus.removeAll().then(() => {
    ext.contextMenus.create({ id: "makan-link", title: t("Download with Epsilon Download Manager"), contexts: ["link"] });
    ext.contextMenus.create({ id: "makan-media", title: t("Download this media with Epsilon Download Manager"), contexts: ["video", "audio", "image"] });
    ext.contextMenus.create({ id: "makan-selected", title: t("Download all highlighted links with Epsilon Download Manager…"), contexts: ["selection"] });
    ext.contextMenus.create({ id: "makan-links", title: t("Download all links with Epsilon Download Manager…"), contexts: ["page"] });
    ext.contextMenus.create({ id: "makan-open", title: t("Open Epsilon Download Manager"), contexts: ["page", IS_FIREFOX ? "browser_action" : "action"] });
  });
}
ext.runtime.onInstalled.addListener(() => createMenus());

// Keyboard shortcut: hand the current page to Epsilon
ext.commands?.onCommand?.addListener(async (command) => {
  if (command !== "download-all-links") return;
  try {
    const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
    if (tab?.id !== undefined) await report(await grabLinks(tab.id));
  } catch (e) {
    await report({ ok: false, error: e?.message || String(e) });
  }
});


ext.contextMenus.onClicked.addListener(async (info, tab) => {
  const referrer = info.frameUrl || tab?.url || null;
  if (info.menuItemId === "makan-link" && info.linkUrl) await report(await sendUrl(info.linkUrl, { referrer, kind: "link" }));
  if (info.menuItemId === "makan-media" && info.srcUrl && isHttp(info.srcUrl)) await report(await sendUrl(info.srcUrl, { referrer, kind: "link" }));
  if (info.menuItemId === "makan-selected" && tab?.id !== undefined) await report(await grabLinks(tab.id, { selectionOnly: true, frameId: info.frameId ?? 0 }));
  if (info.menuItemId === "makan-links" && tab?.id !== undefined) await report(await grabLinks(tab.id));
  if (info.menuItemId === "makan-open") await callHost({ kind: "show" });
});

async function report(response) {
  if (response?.ok) flash("✓", "#2e9e5b");
  else { flash("!", "#d64545"); await ext.storage.local.set({ lastError: { message: response?.error || t("Unknown error"), at: Date.now() } }); }
}

// ------------------------------------------------------------------ media sniffer (video / audio / HLS / DASH)

const store = ext.storage.session ?? ext.storage.local;
const mediaByTab = new Map();       // tabId -> entries (write-through to storage.session so MV3 service-worker restarts keep them)
let queue = Promise.resolve();
const enqueue = (fn) => (queue = queue.then(fn).catch(() => {}));

const SEGMENT_TYPES = /^(video\/mp2t|video\/iso\.segment|audio\/aac-seg)/i;
const MEDIA_EXT = /\.(m3u8|mpd|mp4|m4v|webm|mkv|mov|flv|avi|wmv|3gp|mp3|m4a|aac|ogg|opus|wav|flac|weba)$/i;
const GENERIC_TYPE = /^(application\/octet-stream|binary\/octet-stream|application\/force-download|application\/x-download)$/i;
const SKIP_REQUEST_TYPES = new Set(["image", "imageset", "font", "stylesheet", "script", "ping", "csp_report", "beacon", "websocket", "speculative"]);

function classify(url, contentType) {
  let path = "", full = url;
  try { const u = new URL(url); path = u.pathname; full = u.pathname + u.search; } catch { return null; }
  const type = (contentType || "").split(";")[0].trim().toLowerCase();
  if (SEGMENT_TYPES.test(type) || /\.(ts|m4s|cmfv|cmfa|aac-seg)$/i.test(path)) return null;
  if (/\.m3u8$/i.test(path) || /mpegurl/.test(type) || /\.m3u8(?:[?&#]|$)/i.test(full)) return "hls";
  if (/\.mpd$/i.test(path) || /dash\+xml/.test(type) || /\.mpd(?:[?&#]|$)/i.test(full)) return "dash";
  if (/^(video|audio)\//.test(type)) return "file";
  // Many servers label media as octet-stream (or nothing): the file extension decides.
  if (MEDIA_EXT.test(path) && (type === "" || GENERIC_TYPE.test(type) || !/^text\//.test(type))) return "file";
  return null;
}

function headerValue(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value : "";
}

async function loadTab(tabId) {
  if (mediaByTab.has(tabId)) return mediaByTab.get(tabId);
  const key = "media:" + tabId;
  let list = [];
  try { list = (await store.get(key))[key] || []; } catch { /* fresh start */ }
  mediaByTab.set(tabId, list);
  return list;
}
async function saveTab(tabId) { try { await store.set({ ["media:" + tabId]: mediaByTab.get(tabId) || [] }); } catch { /* best effort */ } }

/** One place that adds a detected item to a tab's list (network sniffer and page scan both use it). */
function recordMedia(tabId, entry) {
  enqueue(async () => {
    const list = await loadTab(tabId);
    const key = entry.url.split("#")[0];
    if (list.some((e) => e.url === key)) return;
    list.push({ ...entry, url: key, at: Date.now() });
    if (list.length > 60) list.shift();
    menuCache.delete(tabId);
    await saveTab(tabId);
    setBadge(tabId, list.length);
  });
}

/** Players that fetch from a service worker or Web Worker have no tab id: hand the request to the open tabs of the same site. */
async function attributeToTabs(details, entry) {
  let origin = "";
  try { origin = new URL(details.initiator || details.originUrl || details.documentUrl || "").origin; } catch { return; }
  let tabs = [];
  try { tabs = await ext.tabs.query({}); } catch { return; }
  tabs.filter((t) => { try { return t.id >= 0 && new URL(t.url || "").origin === origin; } catch { return false; } })
      .slice(0, 5).forEach((t) => recordMedia(t.id, entry));
}

ext.webRequest.onHeadersReceived.addListener((details) => {
  if (details.statusCode !== 200 && details.statusCode !== 206) return;
  if (SKIP_REQUEST_TYPES.has(details.type)) return;
  const contentType = headerValue(details.responseHeaders, "content-type");
  const kind = classify(details.url, contentType);
  if (!kind) return;

  let size = 0;
  const range = headerValue(details.responseHeaders, "content-range");
  const total = /\/(\d+)$/.exec(range);
  if (total) size = Number(total[1]);
  else size = Number(headerValue(details.responseHeaders, "content-length")) || 0;
  if (kind === "file" && size > 0 && size < 150 * 1024) return; // tiny clips/beacons/thumbnails

  const entry = {
    url: details.url, kind, type: contentType.split(";")[0].trim().toLowerCase(), size,
    referrer: details.documentUrl || details.originUrl || details.initiator || null,
  };
  if (details.tabId < 0) attributeToTabs(details, entry); else recordMedia(details.tabId, entry);
}, { urls: ["<all_urls>"] }, ["responseHeaders"]);

function setBadge(tabId, count) {
  try {
    action.setBadgeBackgroundColor({ tabId, color: "#4f8cff" });
    action.setBadgeText({ tabId, text: count > 0 ? String(count) : "" });
  } catch { /* tab may be gone */ }
}

async function refreshBadgeForActiveTab() {
  try {
    const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
    if (!tab) return;
    const list = await loadTab(tab.id);
    setBadge(tab.id, list.length);
  } catch { /* ignore */ }
}

// A new page in the same tab starts a fresh list.
ext.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading" && change.url) {
    enqueue(async () => { mediaByTab.set(tabId, []); menuCache.delete(tabId); overlayByTab.delete(tabId); await saveTab(tabId); setBadge(tabId, 0); });
  }
});
ext.tabs.onRemoved.addListener((tabId) => {
  enqueue(async () => { mediaByTab.delete(tabId); menuCache.delete(tabId); try { await store.remove("media:" + tabId); } catch { /* ignore */ } });
});

// ------------------------------------------------------------------ "Download all links" (IDM's picker window in the app)

/** Reads every link of the tab's page and hands the list to Epsilon, which shows a window to tick the ones to download. */
async function grabLinks(tabId, { selectionOnly = false, frameId = null } = {}) {
  if (tabId === undefined) return { ok: false, error: t("No page.") };
  let page;
  try {
    if (selectionOnly && frameId === null && ext.scripting?.executeScript) {
      const frames = await ext.scripting.executeScript({
        target: { tabId, allFrames: true },
        func: () => window.__makanLinks ? window.__makanLinks(true) : null,
      });
      const pages = (frames || []).map((x) => x?.result).filter((x) => x?.links?.length);
      const found = new Map();
      for (const p of pages) for (const link of p.links) {
        const known = found.get(link.url);
        if (!known) found.set(link.url, { ...link });
        else if (!known.text && link.text) known.text = link.text;
      }
      const first = pages[0];
      page = first ? { ...first, links: [...found.values()].slice(0, 3000) } : null;
    } else {
      page = await ext.tabs.sendMessage(tabId, { type: "collectLinks", selectionOnly }, { frameId: frameId ?? 0 });
    }
  }
  catch { return { ok: false, error: t("Can't read this page. Reload the page and try again.") }; }
  if (!page?.links?.length) return { ok: false, error: t(selectionOnly ? "No links in the selection." : "No links found on this page.") };

  // A site's cookies belong to that site only: one cookie header per host, for the hosts of real links (not of images).
  const hosts = new Map();
  for (const link of page.links) {
    if (link.kind === "image" || hosts.size >= 30) continue;
    try { const u = new URL(link.url); if (!hosts.has(u.hostname)) hosts.set(u.hostname, u.origin + "/"); } catch { /* skip */ }
  }
  const cookies = {};
  for (const [host, origin] of hosts) { const header = await cookieHeader(origin); if (header) cookies[host] = header; }
  return callHost({ kind: "links", items: page.links, cookies, referrer: page.url, userAgent: navigator.userAgent, pageTitle: page.title });
}

// ------------------------------------------------------------------ "Download this video" menu (page overlay + popup)

const overlayByTab = new Map();   // tabId -> Map(frameId -> what the page overlay decided; shown in the popup)
const menuCache = new Map();        // tabId -> { items: [{ text, disabled, action }] }
const variantCache = new Map();     // playlist url -> { at, result }

function qualityLabel(st) {
  let h = st.height;
  if (!h && st.quality) { const m = /(\d+)x(\d+)/.exec(st.quality); if (m) h = Number(m[2]); }
  if (!h) return st.quality && st.quality !== "Source" ? st.quality : "source";
  return `${h}p` + (h >= 720 ? " HD" : "");
}
const bitrateLabel = (st) => (st.bitrate ? `, ${Math.round(st.bitrate / 1000)} ${t("kbps")}` : "");
const shownQuality = (st) => { const q = qualityLabel(st); return q === "source" ? t("source") : q; };
function durationLabel(seconds) {
  if (!seconds || seconds < 1) return "";
  const s = Math.round(seconds), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return h > 0 ? `${h} ${t("hr")} ${m} ${t("min")}` : m > 0 ? `${m} ${t("min")} ${r} ${t("sec")}` : `${r} ${t("sec")}`;
}
function estimateLabel(st) {   // "about 240 MB" from bitrate × length: helps choosing a quality
  if (!st.bitrate || !st.duration) return "";
  return sizeLabel(Math.round((st.bitrate * st.duration) / 8), t("about"));
}
function sizeLabel(bytes, word = t("size")) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"]; let v = bytes, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `, ${word} ${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}
const TYPE_EXT = { "video/mp4": "mp4", "video/webm": "webm", "video/x-matroska": "mkv", "video/quicktime": "mov", "video/x-flv": "flv",
                   "audio/mpeg": "mp3", "audio/mp4": "m4a", "audio/aac": "aac", "audio/ogg": "ogg", "audio/webm": "weba", "audio/wav": "wav", "audio/flac": "flac" };

function fileExtension(entry) {
  try { const m = /\.(\w{2,4})$/.exec(new URL(entry.url).pathname); if (m) return m[1].toLowerCase(); } catch { /* fall through */ }
  return TYPE_EXT[entry.type] || "mp4";
}

async function streamsFor(entry) {
  const cached = variantCache.get(entry.url);
  if (cached && Date.now() - cached.at < 5 * 60 * 1000) return cached.result;
  const result = await callHost({ kind: "streams", url: entry.url, cookie: await cookieHeader(entry.url), referrer: entry.referrer, userAgent: navigator.userAgent });
  if (result?.ok) variantCache.set(entry.url, { at: Date.now(), result });
  return result;
}

// ---- YouTube (and other sites Epsilon cannot read on its own): Epsilon asks yt-dlp what the page offers -------------------------------

const YT_HOST = /^(?:[a-z0-9-]+\.)*(?:youtube\.com|youtube-nocookie\.com)$/i;
function isYouTubeWatch(url) {
  try {
    const u = new URL(url); const host = u.hostname.toLowerCase();
    if (host === "youtu.be") return u.pathname.length > 6;
    if (!YT_HOST.test(host)) return false;
    if (u.pathname === "/watch") return /[?&]v=[\w-]{6,}/.test(u.search);
    return /^\/(shorts|live|embed)\/[\w-]{6,}/.test(u.pathname);
  } catch { return false; }
}

const ytCache = new Map();           // page url -> { at, items }

function ytText(title, o, duration) {
  const size = o.size ? sizeLabel(o.size, t("about")) : "";
  if (o.kind === "video") return `${title}, MP4 ${t("file")}, ${duration ? durationLabel(duration) + ", " : ""}${t("quality")} ${o.label}${size}`;
  if (o.kind === "audio") return `${title}, ${(o.label.match(/\(([^)]+)\)/) || [])[1] || (o.extension || "audio").toUpperCase()} ${t("audio")}${size}`;
  return `${title}, SRT ${t("file")}, ${o.label.replace(/^Subtitles: /, "")} ${t("subtitles")}`;
}

/** strict = a YouTube page (say why it fails); otherwise a quiet second chance for pages the sniffer found nothing on. */
async function buildYtMenu(pageUrl, fallbackTitle, strict) {
  const cached = ytCache.get(pageUrl);
  if (cached && Date.now() - cached.at < 5 * 60 * 1000) return cached.items;
  const r = await callHost({ kind: "ytformats", url: pageUrl, userAgent: navigator.userAgent });
  if (!r?.ok) {
    if (!strict) return [];
    if (r?.needsSetup) return [{ text: t("YouTube needs yt-dlp. Click here to open Epsilon, then Options > YouTube & other sites > Download / update tools."), action: { type: "setup" } }];
    return [{ text: `${fallbackTitle}, ${tf("can't read this video: {0}", r?.error || t("no answer from Epsilon"))}`, disabled: true }];
  }
  const videoTitle = (r.title || fallbackTitle).replace(/\s+/g, " ").trim().slice(0, 150) || "video";
  const items = (r.options || []).map((o) => ({
    text: ytText(videoTitle, o, r.durationSeconds),
    action: { type: "ytdl", pageUrl, key: o.key, size: o.size || 0, referrer: pageUrl, title: o.kind === "video" ? `${videoTitle} [${o.height}p]` : o.kind === "audio" ? `${videoTitle} [audio]` : videoTitle },
  }));
  if (items.length) ytCache.set(pageUrl, { at: Date.now(), items });
  return items;
}

async function buildMenu(tabId, directUrl) {
  let title = "video", pageUrl = "";
  try { const tab = await ext.tabs.get(tabId); pageUrl = tab.url || ""; title = (tab.title || "video").replace(/\s+/g, " ").trim().slice(0, 150) || "video"; } catch { /* tab closed */ }
  if (isYouTubeWatch(pageUrl)) {                 // YouTube streams are chunked and protected: only yt-dlp can read them
    const items = await buildYtMenu(pageUrl, title.replace(/ - YouTube$/, ""), true);
    menuCache.set(tabId, { items, at: Date.now() });
    return items;
  }
  const media = (await loadTab(tabId)).slice();
  if (directUrl && !media.some((e) => e.url === directUrl)) media.push({ url: directUrl, kind: "file", type: "", size: 0, referrer: null });

  // HLS playlists and DASH manifests are both listed by Epsilon (qualities, length) and downloaded by Epsilon itself.
  const lookups = media.filter((e) => e.kind === "hls" || e.kind === "dash");
  const answers = await Promise.all(lookups.map((e) => streamsFor(e)));
  // A player fetches the master playlist AND the variant/audio playlists it picked: don't list those a second time.
  const covered = new Set();
  answers.forEach((r) => (r?.streams || []).forEach((st) => { if (st.url) covered.add(st.url); if (st.audioUrl) covered.add(st.audioUrl); }));

  const items = [];
  media.forEach((entry) => {
    if (entry.kind === "file") {
      const extension = fileExtension(entry);
      items.push({ text: `${title}, ${extension.toUpperCase()} ${t("file")}${sizeLabel(entry.size)}`, action: { type: "file", entry, fileName: `${title}.${extension}` } });
      return;
    }
    const answer = answers[lookups.indexOf(entry)];
    const what = t(entry.kind === "dash" ? "manifest" : "playlist");
    if (!answer?.ok) { items.push({ text: `${title}, ${tf("video stream — can't read the {0}: {1}", what, answer?.error || t("no answer from Epsilon"))}`, disabled: true }); return; }
    if (entry.kind === "hls" && covered.has(entry.url) && answer.streams.length === 1 && qualityLabel(answer.streams[0]) === "source") return; // just a variant/audio playlist of another master
    const streams = answer.streams.slice().sort((a, b) => (a.height || 0) - (b.height || 0) || (a.bitrate || 0) - (b.bitrate || 0));
    streams.forEach((st) => (st.kind === "dash" ? ["mp4"] : ["ts", "mp4"]).forEach((format) => {   // DASH is always fragmented MP4
      const dur = durationLabel(st.duration);
      items.push({ text: `${title}, ${format.toUpperCase()} ${t("file")}, ${dur ? dur + ", " : ""}${t("quality")} ${shownQuality(st)}${bitrateLabel(st)}${estimateLabel(st)}${st.audioUrl ? " " + t("(+ audio track)") : ""}`,
                   action: { type: "stream", stream: st, format, entry, title } });
    }));
  });
  if (items.length === 0 && isHttp(pageUrl)) items.push(...(await buildYtMenu(pageUrl, title, false)));   // e.g. embedded players yt-dlp knows
  menuCache.set(tabId, { items, at: Date.now() });
  return items;
}

async function performItem(item, { suffix = false, noPrompt = false } = {}) {
  const a = item.action;
  if (a.type === "file") return sendUrl(a.entry.url, { filePath: a.fileName, referrer: a.entry.referrer, mime: a.entry.type, explicit: true });
  if (a.type === "ytdl") return callHost({ kind: "ytdl", url: a.pageUrl, format: a.key, size: a.size || 0, title: a.title, referrer: a.referrer, userAgent: navigator.userAgent, noPrompt });
  if (a.type === "setup") { const r = await callHost({ kind: "show" }); return r?.ok ? { ok: true, message: t("Epsilon is open: Options > YouTube & other sites") } : r; }
  if (a.type === "dash") return callHost({ kind: "media", url: a.entry.url, cookie: await cookieHeader(a.entry.url), referrer: a.entry.referrer, userAgent: navigator.userAgent });
  return callHost({
    kind: "stream", url: a.stream.url, audioUrl: a.stream.audioUrl || undefined, format: a.format, quality: qualityLabel(a.stream),
    title: suffix ? `${a.title} ${qualityLabel(a.stream)}` : a.title,
    cookie: await cookieHeader(a.stream.url), referrer: a.entry.referrer, userAgent: navigator.userAgent, noPrompt,
  });
}

async function pickMenuItem(tabId, index, all) {
  let cached = menuCache.get(tabId);
  if (!cached) { await buildMenu(tabId); cached = menuCache.get(tabId); }   // service worker was restarted: rebuild (same order)
  const items = cached.items;
  if (!all) {
    const item = items[index];
    if (!item || item.disabled) return { ok: false, error: t("That entry is no longer available. Reopen the menu.") };
    const r = await performItem(item);
    return r?.ok ? { ok: true, message: r.message || t(r.duplicate ? "Already in Epsilon's list ✓" : "Sent to Epsilon Download Manager ✓") } : { ok: false, error: r?.error || t("Epsilon did not answer.") };
  }
  // Download all: every direct file once, and every quality once (as MP4; without FFmpeg Epsilon keeps it as .ts).
  const chosen = items.filter((i) => !i.disabled && (i.action.type !== "stream" || i.action.format === "mp4"));
  let sent = 0, error = null;
  for (const item of chosen) {
    const r = await performItem(item, { suffix: true, noPrompt: true });
    if (r?.ok) sent++; else error = r?.error || t("Failed");
  }
  return sent > 0 ? { ok: true, message: tf(sent === 1 ? "Sent {0} download to Epsilon ✓" : "Sent {0} downloads to Epsilon ✓", sent) } : { ok: false, error: error || t("Nothing to download.") };
}

// ------------------------------------------------------------------ popup API

ext.runtime.onMessage.addListener((message, sender, sendResponse) => {
  (async () => {
    switch (message?.type) {
      case "status": {
        const s = await settings();
        const ping = await callHost({ kind: "ping" });
        const { lastError } = await ext.storage.local.get({ lastError: null });
        sendResponse({ settings: s, ping, lastError });
        break;
      }
      case "getMedia": {
        await queue;
        sendResponse({ media: await loadTab(message.tabId ?? sender.tab?.id) });
        break;
      }
      case "grabSelection": {       // the "Download with Epsilon" bar for selected links
        sendResponse(await grabLinks(sender.tab?.id, { selectionOnly: true, frameId: sender.frameId ?? 0 }));
        break;
      }
      case "grabLinks": {
        sendResponse(await grabLinks(message.tabId ?? sender.tab?.id));
        break;
      }
      case "grabSelectedLinks": {
        sendResponse(await grabLinks(message.tabId ?? sender.tab?.id, { selectionOnly: true }));
        break;
      }
      case "pageUrls": {            // video / audio addresses the page itself contains (<video src>, og:video, players' JSON…)
        const tabId = sender.tab?.id;
        if (tabId === undefined) { sendResponse({ ok: false }); break; }
        for (const url of (message.urls || []).slice(0, 100)) {
          if (!isHttp(url)) continue;
          const kind = classify(url, "");
          if (kind) recordMedia(tabId, { url, kind, type: "", size: 0, referrer: message.referrer || sender.tab?.url || null, fromPage: true });
        }
        sendResponse({ ok: true });
        break;
      }
      case "overlayReport": {       // the on-video button says whether it is shown and why not
        const tabId = sender.tab?.id;
        if (tabId !== undefined) {
          if (!overlayByTab.has(tabId)) overlayByTab.set(tabId, new Map());
          overlayByTab.get(tabId).set(sender.frameId ?? 0, message.info);
        }
        sendResponse({ ok: true });
        break;
      }
      case "overlayInfo": {
        sendResponse({ frames: [...(overlayByTab.get(message.tabId)?.values() ?? [])] });
        break;
      }
      case "pageMedia": {           // from the page overlay: is there anything to offer on this tab?
        await queue;
        const tabId = sender.tab?.id;
        const found = tabId === undefined ? 0 : (await loadTab(tabId)).length;
        sendResponse({ count: isYouTubeWatch(sender.tab?.url) ? Math.max(1, found) : found });
        break;
      }
      case "menu": {                // from the page overlay or the popup
        await queue;
        const tabId = message.tabId ?? sender.tab?.id;
        if (tabId === undefined) { sendResponse({ items: [] }); break; }
        const items = await buildMenu(tabId, message.directUrl);
        sendResponse({ items: items.map((i) => ({ text: i.text, disabled: !!i.disabled })), canAll: !items.some((i) => i.action?.type === "ytdl" || i.action?.type === "setup") });
        break;
      }
      case "menuPick": {
        const tabId = message.tabId ?? sender.tab?.id;
        sendResponse(tabId === undefined ? { ok: false, error: t("No tab.") } : await pickMenuItem(tabId, message.index, !!message.all));
        break;
      }
      case "saveSettings": {
        await ext.storage.local.set({ enabled: !!message.enabled, exclude: (message.exclude || []).map(String) });
        sendResponse({ ok: true });
        break;
      }
      case "downloadMedia": {
        const e = message.entry;
        if (e.kind === "hls" || e.kind === "dash") {
          // Streams are assembled by FFmpeg in the desktop app's Media window (with this page's cookies/referrer).
          sendResponse(await callHost({ kind: "media", url: e.url, cookie: await cookieHeader(e.url), referrer: e.referrer, userAgent: navigator.userAgent }));
        } else {
          sendResponse(await sendUrl(e.url, { filePath: message.fileName || null, referrer: e.referrer, mime: e.type }));
        }
        break;
      }
      case "downloadLinks": {
        // One request per site so a site's cookies are never sent to another site.
        const byOrigin = new Map();
        for (const link of message.urls || []) {
          if (!isHttp(link)) continue;
          const origin = new URL(link).origin;
          if (!byOrigin.has(origin)) byOrigin.set(origin, []);
          byOrigin.get(origin).push(link);
        }
        let count = 0, error = null;
        for (const [, urls] of byOrigin) {
          const r = await callHost({ kind: "batch", urls, cookie: await cookieHeader(urls[0]), referrer: message.referrer || null, userAgent: navigator.userAgent });
          if (r?.ok) count += r.count || 0; else error = r?.error || "Failed";
        }
        sendResponse({ ok: !error, count, error });
        break;
      }
      case "magnet": {              // a magnet link clicked on a page, or handed over from the extension's own link menu
        if (!message.url || !message.url.toLowerCase().startsWith("magnet:")) { sendResponse({ ok: false, error: t("Not a magnet link.") }); break; }
        sendResponse(await sendUrl(message.url, { explicit: true }));
        break;
      }
      case "capture": {              // a direct link to a video/audio file clicked on a page - the browser would otherwise just play it inline instead of downloading it
        if (!message.url) { sendResponse({ ok: false, error: t("No address given.") }); break; }
        sendResponse(await sendUrl(message.url, { referrer: message.referrer || null, explicit: true }));
        break;
      }
      case "open": sendResponse(await callHost({ kind: "show" })); break;
      case "altClick": lastAltClick = Date.now(); sendResponse({ ok: true }); break;
      case "language": sendResponse({ lang: I18N.lang }); break;
      default: sendResponse({ ok: false, error: t("Unknown message") });
    }
  })().catch((e) => sendResponse({ ok: false, error: e?.message || String(e) }));
  return true; // async response
});
