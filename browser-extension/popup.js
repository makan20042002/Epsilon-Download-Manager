const I18N = globalThis.MakanI18n || { lang: "en", dir: "ltr", setLang() {}, t: (x) => x, tf: (x, ...a) => x.replace(/\{(\d)\}/g, (_, i) => (a[+i] !== undefined ? a[+i] : "")), translateDom() {} };
const t = (x) => I18N.t(x), tf = (x, ...a) => I18N.tf(x, ...a);
const ext = globalThis.browser ?? globalThis.chrome;
const IS_FIREFOX = typeof globalThis.browser !== "undefined";
const $ = (id) => document.getElementById(id);
const send = (message) => ext.runtime.sendMessage(message);

let tab = null;
let foundLinks = [];
const THEMES = ["obsidian-gold", "platinum-blue", "royal-amethyst", "emerald-executive", "champagne-minimal", "graphite-copper", "sapphire-noir", "ivory-luxe", "rose-titanium", "arctic-glass", "dracula"];
const OLD_THEMES = { light: "platinum-blue", orange: "champagne-minimal", makan: "sapphire-noir", dark: "sapphire-noir", epsilon: "sapphire-noir", obsidian: "obsidian-gold", uhnohh: "obsidian-gold", nebula: "royal-amethyst", lilac: "rose-titanium" };
let selectedTheme = "app";
let appTheme = "sapphire-noir";

const TYPE_EXT = { "video/mp4": "mp4", "video/webm": "webm", "video/x-matroska": "mkv", "video/quicktime": "mov", "video/x-flv": "flv",
                   "audio/mpeg": "mp3", "audio/mp4": "m4a", "audio/aac": "aac", "audio/ogg": "ogg", "audio/webm": "weba", "audio/wav": "wav", "audio/flac": "flac" };

function formatSize(bytes) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"]; let v = bytes, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function niceName(entry) {
  // Page titles make much better file names than opaque CDN URLs.
  const extension = TYPE_EXT[entry.type] || (/\.(\w{2,4})$/.exec(new URL(entry.url).pathname) || [])[1] || "mp4";
  const base = (tab?.title || "media").replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim().slice(0, 120) || "media";
  return `${base}.${extension}`;
}

function setState(kind, text) { $("dot").className = kind; $("stateText").textContent = text; }

async function init() {
  try { const stored = await ext.storage.local.get({ lang: "en" }); I18N.setLang(stored.lang); } catch { /* English */ }
  I18N.translateDom(document);
  await initTheme();
  [tab] = await ext.tabs.query({ active: true, currentWindow: true });

  const status = await send({ type: "status" });
  $("enabled").checked = status.settings.enabled;
  $("exclude").value = status.settings.exclude.join("\n");

  const ping = status.ping || {};
  await noteAppTheme(ping.theme);
  const err = ping.error || "";
  const myId = ext.runtime.id;
  if (ping.ok) setState("ok", tf("Connected — Epsilon {0} is running", ping.version || ""));
  else if (ping.running === false) setState("idle", t("Epsilon will start automatically on the next download"));
  else if (/not found|no such native application/i.test(err)) setState("bad", t("Native host not registered — run install-browser-integration.ps1, then restart the browser"));
  else if (/forbidden|permission/i.test(err)) setState("bad", tf("Extension ID doesn't match the installed host. This ID: {0} — run install-browser-integration.ps1 -ExtensionId {0}", myId));
  else if (/exited|communicating|failed to start/i.test(err)) setState("bad", t("Host found but it won't start — is MakanNativeHost.exe still next to MakanDownloadManager.exe?"));
  else setState("bad", err || t("Can't reach Epsilon"));
  if (!ping.ok && err) $("stateText").title = err; // hover for the browser's exact message

  if (status.lastError && Date.now() - status.lastError.at < 10 * 60 * 1000) {
    $("lastError").hidden = false;
    $("lastError").textContent = tf("Last problem: {0} (the browser kept that download)", status.lastError.message);
  }
  await renderMedia();
  await renderOverlayInfo();
}

// Explains whether the on-video button is shown on this page (and why not) so problems are easy to describe.
async function renderOverlayInfo() {
  if (!tab) return;
  const { frames } = await send({ type: "overlayInfo", tabId: tab.id });
  const box = $("overlayInfo");
  const version = tf("Extension {0} · ", ext.runtime.getManifest().version);
  if (!frames || frames.length === 0) { box.textContent = version + t("Button on the video: waiting for a video on this page (reload the page if you just updated the extension)."); return; }
  const shown = frames.find((f) => f.shown);
  if (shown) { box.textContent = version + t("Button on the video: shown") + (shown.mode === "floating" ? t(" (top-right corner of the page)") : " ✓"); return; }
  const top = frames.find((f) => f.top) || frames[0];
  box.textContent = version + tf("Button on the video: not shown — {0} (media detected: {1}).", top.reason || t("no video found"), top.mediaCount || 0);
}

async function renderMedia() {
  if (!tab) return;
  const { media } = await send({ type: "getMedia", tabId: tab.id });
  const youtube = /^https?:\/\/([a-z0-9-]+\.)*(youtube\.com|youtube-nocookie\.com|youtu\.be)\//i.test(tab.url || "");
  if ((!media || media.length === 0) && !youtube) return;      // keep the "play a video first" hint
  const box = $("media");
  box.textContent = t("Reading video streams…");
  const reply = await send({ type: "menu", tabId: tab.id });
  const items = reply?.items || [];
  box.textContent = "";
  if (items.length === 0) { const none = document.createElement("div"); none.className = "empty"; none.textContent = t("No downloadable video found yet."); box.append(none); return; }

  const addRow = (text, index, disabled) => {
    const row = document.createElement("div"); row.className = "item";
    const label = document.createElement("div"); label.className = "name"; label.textContent = text; label.title = text;
    row.append(label);
    if (!disabled) {
      const button = document.createElement("button"); button.textContent = index < 0 ? t("Download all") : t("Download");
      button.onclick = async () => {
        button.disabled = true;
        const r = await send({ type: "menuPick", tabId: tab.id, index, all: index < 0 });
        button.textContent = r?.ok ? t("Sent ✓") : t("Failed");
        if (!r?.ok) { $("lastError").hidden = false; $("lastError").textContent = r?.error || t("Failed"); button.disabled = false; }
      };
      row.append(button);
    }
    box.append(row);
  };
  if (reply?.canAll !== false && items.filter((i) => !i.disabled).length > 1) addRow(t("Download all (one file per quality)"), -1, false);
  items.forEach((item, i) => addRow(`${i + 1}. ${item.text}`, i, !!item.disabled));
}

// ---- links on the page ------------------------------------------------------------------------------------------

$("grab").onclick = async () => {
  if (!tab) return;
  $("grab").disabled = true;
  $("grabResult").textContent = t("Reading the page…");
  const r = await send({ type: "grabLinks", tabId: tab.id });
  $("grab").disabled = false;
  $("grabResult").textContent = r?.ok ? tf(r.count === 1 ? "Sent {0} link to Epsilon — choose in its window ✓" : "Sent {0} links to Epsilon — choose in its window ✓", r.count) : (r?.error || t("Failed"));
  if (r?.ok) setTimeout(() => window.close(), 1200);
};

$("grabSelected").onclick = async () => {
  if (!tab) return;
  $("grabSelected").disabled = true;
  $("grabResult").textContent = t("Reading the highlighted links…");
  const r = await send({ type: "grabSelectedLinks", tabId: tab.id });
  $("grabSelected").disabled = false;
  $("grabResult").textContent = r?.ok ? tf(r.count === 1 ? "Sent {0} selected link to Epsilon — choose in its window ✓" : "Sent {0} selected links to Epsilon — choose in its window ✓", r.count) : (r?.error || t("Failed"));
  if (r?.ok) setTimeout(() => window.close(), 1200);
};

// ---- settings ---------------------------------------------------------------------------------------------------

async function save() {
  const exclude = $("exclude").value.split(/[\n,;]+/).map((x) => x.trim()).filter(Boolean);
  await send({ type: "saveSettings", enabled: $("enabled").checked, exclude });
  $("saved").hidden = false; setTimeout(() => ($("saved").hidden = true), 1500);
}
$("enabled").onchange = save;
$("save").onclick = save;
$("open").onclick = async () => { await send({ type: "open" }); window.close(); };

init();

// ---- appearance of the popup and the on-page video controls -----------------------------------------------------

async function initTheme() {
  try {
    const stored = await ext.storage.local.get({ theme: "app", appTheme: "sapphire-noir" });
    selectedTheme = THEMES.includes(stored.theme) || stored.theme === "app" ? stored.theme : (OLD_THEMES[stored.theme] || "app");
    appTheme = THEMES.includes(stored.appTheme) ? stored.appTheme : (OLD_THEMES[stored.appTheme] || "sapphire-noir");
  } catch { /* follow app with the light fallback */ }
  applyAppearance();
  $("themeSeg").querySelectorAll("button").forEach((btn) => btn.addEventListener("click", async () => {
    selectedTheme = btn.dataset.theme;
    applyAppearance();
    try { await ext.storage.local.set({ theme: selectedTheme }); } catch { /* the next open will show the old choice */ }
  }));
  try { ext.storage.onChanged?.addListener((changes, area) => { if (area === "local" && changes.appTheme && THEMES.includes(changes.appTheme.newValue)) { appTheme = changes.appTheme.newValue; applyAppearance(); } }); } catch { /* cosmetic */ }
}

async function noteAppTheme(theme) {
  if (!THEMES.includes(theme)) return;
  appTheme = theme;
  applyAppearance();
  try { await ext.storage.local.set({ appTheme }); } catch { /* cosmetic */ }
}

function applyAppearance() {
  const resolved = THEMES.includes(selectedTheme) ? selectedTheme : appTheme;
  document.documentElement.dataset.theme = resolved;
  $("themeSeg").querySelectorAll("button").forEach((btn) => btn.classList.toggle("on", btn.dataset.theme === selectedTheme));
}
