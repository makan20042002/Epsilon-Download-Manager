// Epsilon Download Manager – "Download this video" button shown on top of videos (like IDM).
// Runs in every frame; the video list itself lives in the background script (it sees the network requests).
(() => {
  "use strict";
  const I18N = globalThis.MakanI18n || { lang: "en", dir: "ltr", setLang() {}, t: (x) => x, tf: (x, ...a) => x.replace(/\{(\d)\}/g, (_, i) => (a[+i] !== undefined ? a[+i] : "")) };
  const t = (x) => I18N.t(x), tf = (x, ...a) => I18N.tf(x, ...a);
  const ext = globalThis.browser ?? globalThis.chrome;
  const IS_FIREFOX = typeof globalThis.browser !== "undefined";
  if (!ext?.runtime?.id || window.__makanOverlay) return;
  window.__makanOverlay = true;
  const IS_TOP = window === window.top;

  // the language follows Epsilon's Options (the background remembers it)
  try { ext.storage.local.get({ lang: "en" }).then((r) => { if (r && r.lang) I18N.setLang(r.lang); }, () => {}); } catch { /* English */ }

  // The popup and in-page controls follow Epsilon's active desktop theme unless the user overrides it here.
  const THEMES = ["obsidian-gold", "platinum-blue", "royal-amethyst", "emerald-executive", "champagne-minimal", "graphite-copper", "sapphire-noir", "ivory-luxe", "rose-titanium", "arctic-glass"];
  const OLD_THEMES = { light: "platinum-blue", orange: "champagne-minimal", makan: "sapphire-noir", dark: "sapphire-noir", epsilon: "sapphire-noir", obsidian: "obsidian-gold", uhnohh: "obsidian-gold", nebula: "royal-amethyst", lilac: "rose-titanium", dracula: "rose-titanium" };
  let savedTheme = "app";
  let appTheme = "sapphire-noir";
  const prefersDark = () => { try { return matchMedia("(prefers-color-scheme: dark)").matches; } catch { return false; } };
  const currentTheme = () => THEMES.includes(savedTheme) ? savedTheme : (OLD_THEMES[savedTheme] || (THEMES.includes(appTheme) ? appTheme : (OLD_THEMES[appTheme] || (prefersDark() ? "sapphire-noir" : "platinum-blue"))));
  function applyTheme() {
    const theme = currentTheme();
    if (host) host.setAttribute("data-theme", theme);
    if (pill) pill.setAttribute("data-theme", theme);
  }
  try { ext.storage.local.get({ theme: "app", appTheme: prefersDark() ? "sapphire-noir" : "platinum-blue" }).then((r) => { savedTheme = (r && r.theme) || "app"; appTheme = (r && r.appTheme) || appTheme; applyTheme(); }, () => {}); } catch { /* follow app */ }
  try { ext.storage.onChanged?.addListener((changes, area) => { if (area !== "local") return; if (changes.theme) savedTheme = changes.theme.newValue || "app"; if (changes.appTheme) appTheme = changes.appTheme.newValue || appTheme; if (changes.theme || changes.appTheme) applyTheme(); }); } catch { /* Firefox MV3 quirk: theme stays as first read */ }
  try { matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { if (savedTheme === "app" && !THEMES.includes(appTheme)) applyTheme(); }); } catch { /* older browsers: use the remembered app theme */ }
  // Alt + click on a link means "let the browser download it" (like IDM's prevent key)
  const DOWNLOADABLE_EXT = /\.(m3u8|mpd|mp4|m4v|webm|mkv|mov|flv|avi|wmv|3gp|mp3|m4a|aac|ogg|opus|wav|flac|weba)$/i;
  document.addEventListener("click", (e) => {
    if (!alive()) return;
    if (e.altKey || e.ctrlKey || e.metaKey) { ext.runtime.sendMessage({ type: "altClick" }, () => { void ext.runtime.lastError; }); return; }   // Alt / Ctrl / Cmd + click: let the browser handle this one (any regular download, magnet links included)
    if (e.button !== 0 || e.shiftKey) return;                                                                        // new-tab / new-window clicks are left alone
    const link = e.target && e.target.closest && e.target.closest("a[href]");
    if (!link) return;
    if (isMagnetLink(link.href)) {
      e.preventDefault(); e.stopPropagation();
      const toast = makeToast(e.clientX, e.clientY, t("Sending magnet link to Epsilon…"));
      send({ type: "magnet", url: link.href }).then((reply) => {
        if (reply?.ok) toast.update(reply.duplicate ? t("Already in Epsilon's list") : t("Sent to Epsilon Download Manager ✓"), false);
        else toast.update((reply && reply.error) || t("Epsilon did not answer. Is the native host installed?"), true);
      });
      return;
    }
    // A direct link to a video/audio file: most browsers play these inline instead of downloading them, so the
    // extension's usual download-event capture never even fires for them - this is the one case worth intercepting
    // the click itself for, the same way a magnet link already is. Anything else (a plain file like .rar/.zip/.exe)
    // is left completely alone here: the browser already turns those into a real download on its own, which the
    // existing capture (downloads.onCreated, in the background script) already handles correctly.
    let href; try { href = new URL(link.href, location.href).pathname; } catch { return; }
    if (!DOWNLOADABLE_EXT.test(href)) return;
    e.preventDefault(); e.stopPropagation();
    const toast = makeToast(e.clientX, e.clientY, t("Sending to Epsilon…"));
    send({ type: "capture", url: link.href, referrer: location.href }).then((reply) => {
      if (reply?.ok) toast.update(reply.duplicate ? t("Already in Epsilon's list") : t("Sent to Epsilon Download Manager ✓"), false);
      else toast.update((reply && reply.error) || t("Epsilon did not answer. Is the native host installed?"), true);
    });
  }, true);

  const isMagnetLink = (href) => typeof href === "string" && href.slice(0, 7).toLowerCase() === "magnet:";

  /** A small, theme-aware toast near a click - used only for the magnet-link capture above. */
  function makeToast(x, y, text) {
    const host = document.createElement("div");
    host.setAttribute("data-makan", "toast");
    host.style.cssText = "position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;";
    const root = host.attachShadow({ mode: "closed" });
    const style = document.createElement("style"); style.textContent = CSS;
    const box = document.createElement("div"); box.className = "toast"; box.textContent = text;
    root.append(style, box);
    (document.documentElement || document.body).append(host);
    host.setAttribute("data-theme", currentTheme());
    box.style.left = Math.max(4, Math.min(window.innerWidth - 260, x + 8)) + "px";
    box.style.top = Math.max(4, y + 8) + "px";
    requestAnimationFrame(() => box.classList.add("show"));
    const hide = () => { box.classList.remove("show"); setTimeout(() => host.remove(), 200); };
    let timer = setTimeout(hide, 2600);
    return {
      update(newText, bad) {
        box.textContent = newText; box.classList.toggle("bad", !!bad);
        clearTimeout(timer); timer = setTimeout(hide, 2600);
      }
    };
  }

  const alive = () => { try { return !!ext.runtime.id; } catch { return false; } };   // false once the extension was reloaded/updated
  const send = (message) => new Promise((resolve) => {
    try {
      if (IS_FIREFOX) ext.runtime.sendMessage(message).then(resolve, () => resolve(undefined));
      else ext.runtime.sendMessage(message, (reply) => { void ext.runtime.lastError; resolve(reply); });
    } catch { resolve(undefined); }
  });

  let host = null, shadow = null, bar = null, menu = null;
  let canAll = true;
  let hiddenForPage = false, menuOpen = false, mediaCount = 0, anchorVideo = null, mode = null;
  let timer = null, tickCount = 0, shadowVideos = [], lastChildActive = 0, lastReport = "";

  const CSS = `
    :host { all: initial; }
    * { box-sizing: border-box; font-family: "Segoe UI", -apple-system, BlinkMacSystemFont, Arial, sans-serif; }

    /* The same markup supports all desktop palettes and repaints instantly through data-theme. */
    :host { --radius:3px; --pad-x:10px; --pad-y:5px; --font:13px; --tool-size:20px; --menu-shadow:0 18px 44px rgba(0,0,0,.55); }
    :host, :host([data-theme="sapphire-noir"]) { --bar-bg:linear-gradient(180deg,#102B50,#071429); --bar-border:#1B477A; --bar-shadow:0 6px 18px rgba(0,0,0,.55); --accent:#2E86FF; --accent2:#51DBA3; --tool-bg:#0B2140; --tool-border:#1B477A; --tool-fg:#A7C4E5; --tool-hover:#123561; --menu-bg:#0B2140; --menu-fg:#EFF7FF; --menu-border:#1B477A; --item-hover:#123561; --item-active:#174474; --item-disabled:#4E6B8B; --sep:#1B477A; --note-fg:#A7C4E5; --note-bad:#FF657A; }
    :host([data-theme="obsidian-gold"]) { --bar-bg:linear-gradient(180deg,#201D16,#11100D); --bar-border:#4A3B22; --bar-shadow:0 6px 18px rgba(0,0,0,.65); --accent:#E0B75A; --accent2:#64D68A; --tool-bg:#181611; --tool-border:#4A3B22; --tool-fg:#D8C7A0; --tool-hover:#28231A; --menu-bg:#181611; --menu-fg:#FFF8E7; --menu-border:#4A3B22; --item-hover:#28231A; --item-active:#3A2F1C; --item-disabled:#6E634E; --sep:#4A3B22; --note-fg:#D8C7A0; --note-bad:#FF6B6B; }
    :host([data-theme="platinum-blue"]) { --bar-bg:linear-gradient(180deg,#FFFFFF,#F4F9FF); --bar-border:#B7CEE6; --bar-shadow:0 3px 12px rgba(18,38,63,.16); --accent:#1467D8; --accent2:#197347; --tool-bg:#DCEBFA; --tool-border:#AFC8E2; --tool-fg:#4D6680; --tool-hover:#E2EFFC; --menu-bg:#FFFFFF; --menu-fg:#12263F; --menu-border:#B7CEE6; --menu-shadow:0 12px 32px rgba(18,38,63,.18); --item-hover:#E2EFFC; --item-active:#C8DFFF; --item-disabled:#8B9DB0; --sep:#B7CEE6; --note-fg:#4D6680; --note-bad:#B4233E; }
    :host([data-theme="royal-amethyst"]) { --bar-bg:linear-gradient(180deg,#2A1854,#150D2C); --bar-border:#54338B; --bar-shadow:0 6px 20px rgba(12,8,26,.65); --accent:#A873F0; --accent2:#55DDA6; --tool-bg:#211343; --tool-border:#54338B; --tool-fg:#C7B5E5; --tool-hover:#321E61; --menu-bg:#211343; --menu-fg:#F8F2FF; --menu-border:#54338B; --item-hover:#321E61; --item-active:#432777; --item-disabled:#6E5C86; --sep:#54338B; --note-fg:#C7B5E5; --note-bad:#FF668C; }
    :host([data-theme="emerald-executive"]) { --bar-bg:linear-gradient(180deg,#123B32,#08221D); --bar-border:#245C4E; --bar-shadow:0 6px 20px rgba(0,0,0,.58); --accent:#45D8AE; --accent2:#55E6A8; --tool-bg:#0D3029; --tool-border:#245C4E; --tool-fg:#A7D7C8; --tool-hover:#17483D; --menu-bg:#0D3029; --menu-fg:#EDFFF9; --menu-border:#245C4E; --item-hover:#17483D; --item-active:#1D594A; --item-disabled:#52796E; --sep:#245C4E; --note-fg:#A7D7C8; --note-bad:#FF6F78; }
    :host([data-theme="champagne-minimal"]) { --bar-bg:linear-gradient(180deg,#FFFFFF,#FBF7F1); --bar-border:#D7C2A4; --bar-shadow:0 3px 12px rgba(45,36,26,.16); --accent:#9B682D; --accent2:#287546; --tool-bg:#EDE2D2; --tool-border:#CFB999; --tool-fg:#6F5E49; --tool-hover:#F1E7D8; --menu-bg:#FFFFFF; --menu-fg:#2D241A; --menu-border:#D7C2A4; --menu-shadow:0 12px 32px rgba(45,36,26,.18); --item-hover:#F1E7D8; --item-active:#E6D3B7; --item-disabled:#A2927D; --sep:#D7C2A4; --note-fg:#6F5E49; --note-bad:#B52D3E; }
    :host([data-theme="graphite-copper"]) { --bar-bg:linear-gradient(180deg,#29211B,#151311); --bar-border:#5A3C28; --bar-shadow:0 6px 18px rgba(0,0,0,.65); --accent:#E28A4D; --accent2:#66D796; --tool-bg:#201A16; --tool-border:#5A3C28; --tool-fg:#D6B9A5; --tool-hover:#32251D; --menu-bg:#201A16; --menu-fg:#FFF4EC; --menu-border:#5A3C28; --item-hover:#32251D; --item-active:#463020; --item-disabled:#745E50; --sep:#5A3C28; --note-fg:#D6B9A5; --note-bad:#FF6B64; }
    :host([data-theme="ivory-luxe"]) { --bar-bg:linear-gradient(180deg,#FFFFFF,#FFFDF8); --bar-border:#D8CBB8; --bar-shadow:0 3px 12px rgba(42,36,27,.15); --accent:#93642D; --accent2:#267348; --tool-bg:#ECE5D8; --tool-border:#D0C1AC; --tool-fg:#6B604F; --tool-hover:#F2ECE2; --menu-bg:#FFFFFF; --menu-fg:#2A241B; --menu-border:#D8CBB8; --menu-shadow:0 12px 32px rgba(42,36,27,.18); --item-hover:#F2ECE2; --item-active:#E5D8C4; --item-disabled:#A19584; --sep:#D8CBB8; --note-fg:#6B604F; --note-bad:#B42C42; }
    :host([data-theme="rose-titanium"]) { --bar-bg:linear-gradient(180deg,#3C1E30,#20111B); --bar-border:#6A3B56; --bar-shadow:0 6px 20px rgba(0,0,0,.62); --accent:#E187B1; --accent2:#63D59A; --tool-bg:#301825; --tool-border:#6A3B56; --tool-fg:#D9B4C8; --tool-hover:#48243A; --menu-bg:#301825; --menu-fg:#FFF3F9; --menu-border:#6A3B56; --item-hover:#48243A; --item-active:#5B2C48; --item-disabled:#79596A; --sep:#6A3B56; --note-fg:#D9B4C8; --note-bad:#FF6680; }
    :host([data-theme="arctic-glass"]) { --bar-bg:linear-gradient(180deg,#FFFFFF,#F4FAFF); --bar-border:#B7D7EC; --bar-shadow:0 3px 12px rgba(16,42,67,.16); --accent:#087AB8; --accent2:#167451; --tool-bg:#D9ECFA; --tool-border:#ACCEE4; --tool-fg:#486B86; --tool-hover:#E0F1FC; --menu-bg:#FFFFFF; --menu-fg:#102A43; --menu-border:#B7D7EC; --menu-shadow:0 12px 32px rgba(16,42,67,.18); --item-hover:#E0F1FC; --item-active:#C5E5F7; --item-disabled:#879EAF; --sep:#B7D7EC; --note-fg:#486B86; --note-bad:#B42346; }

    .bar { display: inline-flex; align-items: center; gap: 2px; background: var(--bar-bg); border: 1px solid var(--bar-border); border-radius: var(--radius);
           box-shadow: var(--bar-shadow); padding: 2px; opacity: .92; user-select: none; transition: opacity .15s ease; }
    .bar:hover, .bar.open { opacity: 1; }
    .main { display: inline-flex; align-items: center; gap: 6px; padding: var(--pad-y) var(--pad-x) var(--pad-y) 6px; cursor: pointer; font-weight: 700;
            font-size: var(--font); color: var(--accent); white-space: nowrap; letter-spacing: .1px; }
    .main .arrow { color: var(--accent2); font-size: var(--font); }
    .tool { width: var(--tool-size); height: var(--tool-size); line-height: calc(var(--tool-size) - 2px); text-align: center; border: 1px solid var(--tool-border);
            border-radius: 2px; background: var(--tool-bg); color: var(--tool-fg); font-size: 11px; font-weight: 700; cursor: pointer; transition: background .12s ease; }
    .tool:hover { background: var(--tool-hover); }
    .menu { position: absolute; top: 100%; left: 0; margin-top: 6px; min-width: 250px; max-width: min(92vw, 760px); max-height: 64vh; overflow: auto;
            background: var(--menu-bg); color: var(--menu-fg); border: 1px solid var(--menu-border); border-radius: var(--radius); box-shadow: var(--menu-shadow);
            font-size: 13px; padding: 6px; }
    .menu.right { left: auto; right: 0; }
    .item { padding: 7px 12px; border-radius: 2px; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin: 1px 0; transition: background .1s ease; }
    .item:hover { background: var(--item-hover); }
    .item:active { background: var(--item-active); }
    .item.disabled { color: var(--item-disabled); cursor: default; }
    .item.disabled:hover { background: none; }
    .sep { height: 1px; background: var(--sep); margin: 5px 4px; }
    .note { padding: 8px 10px; font-size: 12px; color: var(--note-fg); max-width: 440px; white-space: normal; line-height: 1.4; }
    .note.bad { color: var(--note-bad); }
    .toast { position: fixed; z-index: 2147483647; background: var(--menu-bg); color: var(--menu-fg); border: 1px solid var(--menu-border);
             border-radius: var(--radius); box-shadow: var(--menu-shadow); padding: 7px 12px; font-size: 12.5px; white-space: nowrap;
             opacity: 0; transform: translateY(4px); transition: opacity .15s ease, transform .15s ease; pointer-events: none; }
    .toast.show { opacity: 1; transform: translateY(0); }
    .toast.bad { color: var(--note-bad); }
  `;

  // ---------------------------------------------------------------- the button

  function build() {
    host = document.createElement("div");
    host.setAttribute("data-makan", "overlay");
    host.style.cssText = "position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;display:none;";
    shadow = host.attachShadow({ mode: "closed" });
    const style = document.createElement("style"); style.textContent = CSS;
    bar = document.createElement("div"); bar.className = "bar";
    const main = document.createElement("div"); main.className = "main";
    const arrow = document.createElement("span"); arrow.className = "arrow"; arrow.textContent = "▶";
    const label = document.createElement("span"); label.textContent = t("Download this video");
    main.append(arrow, label);
    const help = document.createElement("div"); help.className = "tool"; help.textContent = "?"; help.title = t("Open Epsilon Download Manager");
    const close = document.createElement("div"); close.className = "tool"; close.textContent = "✕"; close.title = t("Hide this button on this page");
    bar.append(main, help, close);
    menu = document.createElement("div"); menu.className = "menu"; menu.hidden = true;
    shadow.append(style, bar, menu);
    mount();
    applyTheme();

    main.addEventListener("click", (e) => { e.stopPropagation(); menuOpen ? closeMenu() : openMenu(); });
    help.addEventListener("click", (e) => { e.stopPropagation(); send({ type: "open" }); });
    close.addEventListener("click", (e) => { e.stopPropagation(); hiddenForPage = true; closeMenu(); host.style.display = "none"; });
    document.addEventListener("mousedown", (e) => { if (menuOpen && !e.composedPath().includes(host)) closeMenu(); }, true);
    document.addEventListener("keydown", (e) => { if (e.key === "Escape" && menuOpen) closeMenu(); }, true);
    document.addEventListener("fullscreenchange", () => { mount(); tick().catch(() => {}); });
  }

  /** In full screen only the full-screen element's subtree is drawn, so the button has to live inside it. */
  function mount() {
    if (!host) return;
    const fs = document.fullscreenElement;
    const parent = fs && fs.tagName !== "VIDEO" ? fs : (document.documentElement || document.body);
    if (host.parentNode !== parent) parent.append(host);
  }

  function hideHost() { if (host) { closeMenu(); host.style.display = "none"; } }
  function removeHost() { closeMenu(); if (host) { host.remove(); host = null; } }
  function closeMenu() { menuOpen = false; if (menu) menu.hidden = true; if (bar) bar.classList.remove("open"); }

  function showNote(text, bad) {
    menu.textContent = "";
    const note = document.createElement("div"); note.className = "note" + (bad ? " bad" : ""); note.textContent = text;
    menu.append(note); menu.hidden = false;
  }

  async function openMenu() {
    menuOpen = true; bar.classList.add("open");
    placeMenu();
    showNote(t("Looking for video streams…"));
    const direct = anchorVideo && /^https?:/i.test(anchorVideo.currentSrc || "") ? anchorVideo.currentSrc : undefined;
    const reply = await send({ type: "menu", directUrl: direct });
    if (!menuOpen) return;
    if (!reply) { showNote(t("Can't reach the Epsilon extension (reload the page)."), true); return; }
    const items = reply.items || [];
    canAll = reply.canAll !== false;
    if (items.length === 0) { showNote(t("No downloadable stream found yet. Press play, wait a few seconds and try again. (Some sites protect their videos.)"), false); return; }
    render(items);
  }

  function render(items) {
    menu.textContent = "";
    const add = (text, index, disabled) => {
      const el = document.createElement("div"); el.className = "item" + (disabled ? " disabled" : "");
      el.textContent = text; el.title = text;
      if (!disabled) el.addEventListener("click", (e) => { e.stopPropagation(); pick(index); });
      menu.append(el);
    };
    const usable = items.filter((i) => !i.disabled).length;
    if (usable > 1 && canAll) { add(t("Download all"), -1, false); const sep = document.createElement("div"); sep.className = "sep"; menu.append(sep); }
    items.forEach((item, i) => add(`${i + 1}.  ${item.text}`, i, !!item.disabled));
    menu.hidden = false;
    placeMenu();
  }

  async function pick(index) {
    showNote(t("Sending to Epsilon…"));
    const reply = await send({ type: "menuPick", index, all: index < 0 });
    if (reply?.ok) { showNote(reply.message || t("Sent to Epsilon Download Manager ✓")); setTimeout(closeMenu, 1600); }
    else showNote((reply && reply.error) || t("Epsilon did not answer. Is the native host installed?"), true);
  }

  function placeMenu() {
    if (!host) return;
    const left = parseFloat(host.style.left) || 0;
    menu.classList.toggle("right", left > window.innerWidth * 0.55);
  }

  // ---------------------------------------------------------------- finding the video

  function refreshShadowVideos() {
    // Modern players (custom elements) keep their <video> inside a shadow root that document.querySelectorAll can't see.
    const found = [];
    const walk = (root, depth) => {
      for (const el of root.querySelectorAll("*")) {
        if (!el.shadowRoot) continue;
        el.shadowRoot.querySelectorAll("video").forEach((v) => found.push(v));
        if (depth < 3) walk(el.shadowRoot, depth + 1);
      }
    };
    try { walk(document, 0); } catch { /* closed roots are simply invisible to us */ }
    shadowVideos = found;
  }

  function isVisible(v) {
    const r = v.getBoundingClientRect();
    if (r.width < 160 || r.height < 90) return false;
    if (r.bottom < 0 || r.top > window.innerHeight || r.right < 0 || r.left > window.innerWidth) return false;
    const cs = getComputedStyle(v);
    return cs.visibility !== "hidden" && cs.display !== "none";
  }

  function pickVideo() {
    let best = null, area = 0;
    const all = [...document.querySelectorAll("video"), ...shadowVideos.filter((v) => v.isConnected)];
    for (const v of all) {
      if (!isVisible(v)) continue;
      const r = v.getBoundingClientRect();
      if (r.width * r.height > area) { best = v; area = r.width * r.height; }
    }
    return best;
  }

  const played = (v) => !v.paused || v.currentTime > 0.5;

  // ---------------------------------------------------------------- deciding where (and whether) to show the button

  async function tick() {
    if (hiddenForPage) return;
    tickCount++;
    if (tickCount % 4 === 1) refreshShadowVideos();
    const video = pickVideo();
    anchorVideo = video;

    const reply = await send({ type: "pageMedia" });
    if (reply === undefined) {
      if (!alive()) { stop(); removeHost(); }      // orphaned by an extension update: clean up
      return;                                       // otherwise a hiccup (worker starting up): try again next tick
    }
    mediaCount = reply.count || 0;

    let reason = "";
    mode = null;
    if (video) {
      if (mediaCount > 0 || /^https?:/i.test(video.currentSrc || "") || played(video)) mode = "anchored";
      else reason = "a video is here but nothing downloadable has been detected yet";
    } else reason = "no visible video in this frame";
    // The video may sit where we can't see it (iframe we don't run in, closed shadow root): show a floating button in the top frame instead.
    if (!mode && IS_TOP && mediaCount > 0 && Date.now() - lastChildActive > 4500) mode = "floating";

    report(mode, reason, video ? 1 : 0);
    if (!mode) { hideHost(); return; }

    if (!host) build();
    mount();
    host.style.display = "block";
    if (mode === "anchored") {
      const r = video.getBoundingClientRect();
      host.style.left = Math.max(4, Math.min(window.innerWidth - 60, r.left + 8)) + "px";
      host.style.top = Math.max(4, r.top + 8) + "px";
      if (!IS_TOP) { try { window.top.postMessage({ __makan: "active" }, "*"); } catch { /* cross-origin top: ignore */ } }
    } else {
      host.style.left = Math.max(4, window.innerWidth - (bar.offsetWidth || 190) - 16) + "px";
      host.style.top = "14px";
    }
    placeMenu();
  }

  function report(currentMode, reason, videos) {
    const info = { url: location.href, top: IS_TOP, videos, mediaCount, shown: !!currentMode, mode: currentMode, reason };
    const key = JSON.stringify(info);
    if (key === lastReport) return;
    lastReport = key;
    send({ type: "overlayReport", info });
  }

  window.addEventListener("message", (e) => {
    if (IS_TOP && e.source !== window && e.data && e.data.__makan === "active") lastChildActive = Date.now();
  });

  // ---------------------------------------------------------------- what the page itself contains (covers cached videos and players that hide their requests)

  const MEDIA_LINK = /\.(mp4|webm|mkv|mov|flv|avi|m4v|3gp|mp3|m4a|flac|ogg|opus|wav|m3u8|mpd)$/i;
  const URL_IN_TEXT = /https?:(?:\\\/|\/){2}(?:[^"'\s<>)\\]|\\\/|\\u0026)+?\.(?:m3u8|mpd|mp4|webm|mkv|mov|flv|mp3|m4a)(?:\?(?:[^"'\s<>)\\]|\\\/|\\u0026)*)?/gi;
  let lastScan = "", scans = 0;

  function scanPage() {
    const urls = new Set();
    const add = (value) => {
      try { const href = new URL(value, location.href).href; if (/^https?:/i.test(href)) urls.add(href); } catch { /* not a URL */ }
    };
    document.querySelectorAll("video, audio, source").forEach((el) => { if (el.currentSrc) add(el.currentSrc); const src = el.getAttribute("src"); if (src) add(src); });
    document.querySelectorAll('meta[property^="og:video"], meta[property="og:audio"], meta[name="twitter:player:stream"]').forEach((m) => { if (m.content) add(m.content); });
    document.querySelectorAll("a[href]").forEach((a) => { try { if (MEDIA_LINK.test(new URL(a.href, location.href).pathname)) add(a.href); } catch { /* ignore */ } });
    if (scans < 3) {                     // inline player configs / JSON: only the first few scans, they are the expensive part
      let budget = 1500000;
      for (const script of document.scripts) {
        if (script.src || budget <= 0) continue;
        const text = script.textContent || ""; budget -= text.length;
        for (const m of text.matchAll(URL_IN_TEXT)) { add(m[0].replace(/\\\//g, "/").replace(/\\u0026/g, "&")); if (urls.size > 100) break; }
      }
    }
    scans++;
    return [...urls].slice(0, 100);
  }

  async function reportPage() {
    const urls = scanPage();
    const key = urls.join("\n");
    if (urls.length === 0 || key === lastScan) return;
    lastScan = key;
    await send({ type: "pageUrls", urls, referrer: location.href });
  }
  window.addEventListener("play", () => { reportPage().catch(() => {}); tick().catch(() => {}); }, true);
  window.addEventListener("loadedmetadata", () => { reportPage().catch(() => {}); }, true);
  [300, 2500, 7000].forEach((ms) => setTimeout(() => reportPage().catch(() => {}), ms));
  setInterval(() => { if (!document.hidden) reportPage().catch(() => {}); }, 6000);

  // ---- every link of the page (or only the selected ones), for Epsilon's "Download all links" window ------------------------------

  function collectLinks(selectionOnly) {
    const found = new Map();
    const add = (value, text, kind) => {
      let href; try { href = new URL(value, location.href).href; } catch { return; }
      if (!/^https?:/i.test(href)) return;
      const label = (text || "").replace(/\s+/g, " ").trim().slice(0, 160);
      const known = found.get(href);
      if (!known) found.set(href, { url: href, text: label, kind });
      else if (!known.text && label) known.text = label;
    };
    const selection = selectionOnly && window.getSelection ? window.getSelection() : null;
    const ranges = [];
    if (selection) for (let i = 0; i < selection.rangeCount; i++) ranges.push(selection.getRangeAt(i));
    document.querySelectorAll("a[href]").forEach((a) => {
      if (selection) { let inside = false; try { inside = ranges.some((r) => r.intersectsNode(a)); } catch { /* ignore */ } if (!inside) return; }
      add(a.href, a.innerText || a.textContent || a.title || a.getAttribute("aria-label") || (a.querySelector("img") || {}).alt, "link");
    });
    if (!selection) {
      document.querySelectorAll("video[src], audio[src], source[src]").forEach((m) => add(m.currentSrc || m.src, "", "media"));
      document.querySelectorAll("img[src]").forEach((i) => { if (!i.complete || (i.naturalWidth || i.width) >= 48) add(i.currentSrc || i.src, i.alt, "image"); });
    }
    return { links: [...found.values()].slice(0, 3000), title: document.title, url: location.href };
  }
  ext.runtime.onMessage?.addListener((message, _sender, sendResponse) => {
    if (message && message.type === "collectLinks") {
      // the whole page is read by the top frame; a selection by the frame that holds it
      if (message.selectionOnly || IS_TOP) sendResponse(collectLinks(!!message.selectionOnly));
    }
    return false;
  });
  window.__makanLinks = collectLinks;   // used by the automated tests

  // ---- the bar for selected links (IDM shows "Download with IDM" at the bottom-left when you select links) -----------------------

  let pill = null, pillShadow = null, pillLabel = null, pillDismissedFor = "", pillTimer = null;

  function buildPill() {
    pill = document.createElement("div");
    pill.setAttribute("data-makan", "links");
    pill.style.cssText = "position:fixed;left:14px;bottom:16px;width:0;height:0;z-index:2147483647;display:none;";
    pillShadow = pill.attachShadow({ mode: "closed" });
    const style = document.createElement("style"); style.textContent = CSS + ".bar { position: absolute; bottom: 0; left: 0; opacity: 1; } .main { color: #1b56c9; } .note { padding: 3px 10px; }";
    const bar = document.createElement("div"); bar.className = "bar";
    const main = document.createElement("div"); main.className = "main";
    const arrow = document.createElement("span"); arrow.className = "arrow"; arrow.textContent = "⬇";
    pillLabel = document.createElement("span"); pillLabel.textContent = t("Download with Epsilon");
    main.append(arrow, pillLabel);
    const close = document.createElement("div"); close.className = "tool"; close.textContent = "✕"; close.title = t("Hide");
    bar.append(main, close);
    pillShadow.append(style, bar);
    (document.documentElement || document.body).append(pill);
    applyTheme();

    main.addEventListener("click", async (e) => {
      e.stopPropagation();
      pillLabel.textContent = t("Sending…");
      const reply = await send({ type: "grabSelection" });
      if (reply?.ok) { pillLabel.textContent = tf(reply.count === 1 ? "Sent {0} link — choose in Epsilon ✓" : "Sent {0} links — choose in Epsilon ✓", reply.count ?? ""); setTimeout(hidePill, 2500); }
      else { pillLabel.textContent = (reply && reply.error) || t("Epsilon did not answer"); setTimeout(() => updatePill(), 3500); }
    });
    close.addEventListener("click", (e) => { e.stopPropagation(); pillDismissedFor = selectionKey(); hidePill(); });
  }

  const selectionKey = () => { const s = window.getSelection ? window.getSelection() : null; return s ? String(s).slice(0, 200) : ""; };
  function hidePill() { if (pill) pill.style.display = "none"; }

  async function updatePill() {
    if (!alive()) return;
    const sel = window.getSelection ? window.getSelection() : null;
    if (!sel || sel.isCollapsed || sel.rangeCount === 0) { pillDismissedFor = ""; hidePill(); return; }
    if (selectionKey() === pillDismissedFor) return;
    const count = collectLinks(true).links.length;
    if (count === 0) { hidePill(); return; }
    if (!pill) buildPill();
    pillLabel.textContent = tf(count === 1 ? "Download with Epsilon ({0} link)" : "Download with Epsilon ({0} links)", count);
    pill.style.direction = I18N.dir;
    pill.style.display = "block";
  }
  document.addEventListener("selectionchange", () => { clearTimeout(pillTimer); pillTimer = setTimeout(() => updatePill().catch(() => {}), 250); });
  window.__makanSelection = updatePill;   // used by the automated tests

  function stop() { if (timer) { clearInterval(timer); timer = null; } }
  timer = setInterval(() => { tick().catch(() => {}); }, 1200);
  setTimeout(() => tick().catch(() => {}), 400);
  window.addEventListener("scroll", () => { if (host && host.style.display !== "none" && mode === "anchored") tick().catch(() => {}); }, { passive: true });
  window.addEventListener("resize", () => { tick().catch(() => {}); });

  window.__makanTick = tick;          // used by the automated tests
  window.__makanScan = reportPage;
})();
