// Full-stack test: the REAL extension background script -> the REAL MakanNativeHost.exe (native-messaging framing, one process per message,
// exactly like a browser) -> the REAL named pipe -> the REAL bridge + download engine -> files on disk fetched from the mock web server.
// Only the browser API (`chrome.*`) is faked. Needs: mock server on :18080, built BridgeServer and MakanNativeHost.
const assert = require("assert");
const path = require("path");
const fs = require("fs");
const os = require("os");
const crypto = require("crypto");
const http = require("http");
const { spawn } = require("child_process");

// A developer may have the installed desktop app running while the suite runs. Give this
// end-to-end stack its own pipe so native-host messages cannot be consumed by that app.
process.env.EPSILON_NATIVE_PIPE = `com.makan.downloadmanager.e2e.${process.pid}`;

const ROOT = path.join(__dirname, "..", "..");
const BASE = "http://127.0.0.1:18080";
const HOST_DLL = path.join(ROOT, "MakanNativeHost/bin/Release/net8.0/MakanNativeHost.dll");
const SERVER_DLL = path.join(ROOT, "tests/BridgeServer/bin/Release/net8.0/BridgeServer.dll");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getText = (url) => new Promise((res, rej) => http.get(url, (r) => { let b = ""; r.on("data", (d) => (b += d)); r.on("end", () => res(b)); }).on("error", rej));
const sha = (file) => crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const expected = (name) => getText(`${BASE}/__expect/${name}`);
async function waitFor(fn, seconds = 60, what = "condition") {
  const t0 = Date.now();
  while (Date.now() - t0 < seconds * 1000) { const v = await fn(); if (v) return v; await sleep(150); }
  throw new Error("timed out waiting for " + what);
}

// ---- the desktop side ---------------------------------------------------------------------------------------------
function startApp(dir, mode) {
  return new Promise((resolve, reject) => {
    const p = spawn("dotnet", [SERVER_DLL, dir, mode], { stdio: ["pipe", "pipe", "inherit"] });
    let ready = false;
    p.stdout.on("data", (d) => { if (!ready && String(d).includes("ready")) { ready = true; resolve(p); } });
    p.on("error", reject);
    setTimeout(() => !ready && reject(new Error("BridgeServer did not start")), 20000);
  });
}
const stopApp = (p) => new Promise((r) => { p.on("exit", r); p.stdin.end(); setTimeout(() => p.kill(), 4000); });
const state = (dir) => { try { return JSON.parse(fs.readFileSync(path.join(dir, "state.json"), "utf8")); } catch { return []; } };
const prompts = (dir) => { try { return fs.readFileSync(path.join(dir, "prompts.jsonl"), "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l)); } catch { return []; } };

// ---- what Chrome/Firefox do for chrome.runtime.sendNativeMessage: start the host, frame in, frame out --------------------
function nativeMessage(message) {
  return new Promise((resolve, reject) => {
    const host = spawn("dotnet", [HOST_DLL, "chrome-extension://nglldicodleblllopbkgncogljbdldpd/"], { stdio: ["pipe", "pipe", "inherit"] });
    const body = Buffer.from(JSON.stringify(message), "utf8");
    const head = Buffer.alloc(4); head.writeInt32LE(body.length);
    host.stdin.write(Buffer.concat([head, body]));
    let buf = Buffer.alloc(0);
    host.stdout.on("data", (d) => {
      buf = Buffer.concat([buf, d]);
      if (buf.length >= 4 && buf.length >= 4 + buf.readInt32LE(0)) {
        const reply = JSON.parse(buf.subarray(4, 4 + buf.readInt32LE(0)).toString("utf8"));
        host.stdin.end();
        resolve(reply);
      }
    });
    host.on("error", reject);
    setTimeout(() => reject(new Error("native host did not answer")), 40000);
  });
}

// ---- the fake browser ---------------------------------------------------------------------------------------------
function makeEvent() { const l = []; return { addListener: (f) => l.push(f), fire: async (...a) => { for (const f of l) await f(...a); }, listeners: l }; }
function loadExtension() {
  const cancelled = [], erased = [];
  const local = {}, session = {};
  const area = (o) => ({
    get: async (k) => (typeof k === "string" ? (k in o ? { [k]: o[k] } : {}) : { ...k, ...Object.fromEntries(Object.keys(k || {}).filter((x) => x in o).map((x) => [x, o[x]])) }),
    set: async (v) => Object.assign(o, v), remove: async (k) => { delete o[k]; },
  });
  const chrome = {
    runtime: {
      sendNativeMessage: (host, msg, cb) => { nativeMessage(msg).then((r) => cb(r), (e) => { chrome.runtime.lastError = { message: String(e.message) }; cb(undefined); chrome.runtime.lastError = undefined; }); },
      lastError: undefined, onInstalled: makeEvent(), onMessage: makeEvent(),
    },
    downloads: { onCreated: makeEvent(), cancel: async (id) => cancelled.push(id), erase: async (q) => erased.push(q.id) },
    cookies: { getAll: async ({ url }) => (url.startsWith(BASE) ? [{ name: "session", value: "abc" }] : []) },
    contextMenus: { removeAll: async () => {}, create: () => {}, onClicked: makeEvent() },
    webRequest: { onHeadersReceived: makeEvent() },
    tabs: { sendMessage: async () => ({ url: BASE + "/serial", title: "Lanterns S01", links: [
        { url: BASE + "/needs-cookie", text: "Episode 1 (needs login)", kind: "link" }, { url: BASE + "/small.bin?ep2", text: "Episode 2", kind: "link" },
        { url: BASE + "/serial/watch.html", text: "Watch online", kind: "link" }, { url: BASE + "/cover.jpg", text: "", kind: "image" } ] }),
      onUpdated: makeEvent(), onRemoved: makeEvent(), query: async (q) => (q && q.active ? [{ id: 7, title: "Some Video | Site" }] : [{ id: 7, url: BASE + "/watch" }]), get: async (id) => ({ id, title: "Some Video | Site" }) },
    storage: { local: area(local), session: area(session) },
    action: { setBadgeText: () => {}, setBadgeBackgroundColor: () => {} },
  };
  const file = path.join(ROOT, "browser-extension/background.js");
  delete require.cache[require.resolve(file)];
  globalThis.chrome = chrome; delete globalThis.browser;
  Object.defineProperty(globalThis, "navigator", { value: { userAgent: "E2E-Browser/1.0" }, configurable: true });
  require(file);
  const ask = (message, sender = {}) => new Promise((resolve) => chrome.runtime.onMessage.listeners[0](message, sender, resolve));
  const sniff = (url, type = "application/vnd.apple.mpegurl") => chrome.webRequest.onHeadersReceived.listeners[0]({ url, tabId: 7, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: type }], documentUrl: BASE + "/watch" });
  return { chrome, cancelled, erased, ask, sniff, local };
}

let pass = 0, fail = 0;
async function test(name, fn) {
  const t0 = Date.now();
  try { await fn(); pass++; console.log(`  PASS  ${name}  (${((Date.now() - t0) / 1000).toFixed(1)}s)`); }
  catch (e) { fail++; console.log(`  FAIL  ${name}\n        ${e.stack.split("\n").slice(0, 4).join("\n        ")}`); }
}

(async () => {
  for (const f of [HOST_DLL, SERVER_DLL]) if (!fs.existsSync(f)) { console.log("missing " + f + " (build MakanNativeHost and tests/BridgeServer in Release first)"); process.exit(2); }
  await getText(BASE + "/__reset");

  // =================================================== "always ask" OFF: everything starts immediately
  {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "makan-e2e-direct-"));
    const app = await startApp(dir, "direct");
    const x = loadExtension();
    console.log("\n== extension -> native host -> app, downloads start immediately");

    await test("browser download of a cookie-protected file: cookie is forwarded, file arrives, browser copy is cancelled + erased", async () => {
      await x.chrome.downloads.onCreated.fire({ id: 101, url: BASE + "/needs-cookie", filename: "C:\\Users\\me\\Downloads\\cookie.bin", referrer: BASE + "/page", mime: "application/octet-stream" });
      const item = await waitFor(() => state(dir).find((i) => i.Url.endsWith("/needs-cookie") && i.Status === "Complete"), 30, "cookie download");
      assert.strictEqual(path.basename(item.FilePath), "cookie.bin");
      assert.strictEqual(fs.statSync(item.FilePath).size, 307200);
      assert.deepStrictEqual(x.cancelled, [101]); assert.deepStrictEqual(x.erased, [101]);
    });

    await test("a login page instead of a file: the app refuses, the browser keeps its own download", async () => {
      await x.chrome.downloads.onCreated.fire({ id: 102, url: BASE + "/html" });
      await sleep(500);
      assert.ok(!x.cancelled.includes(102), "browser download must NOT be cancelled");
      const { lastError } = await x.chrome.storage.local.get({ lastError: null });
      assert.match(lastError.message, /web page/i);
      assert.ok(!state(dir).some((i) => i.Url.endsWith("/html")));
    });

    await test("video menu: qualities come from the real playlist (length + estimate), TS pick downloads the exact bytes", async () => {
      x.sniff(BASE + "/hls/master2.m3u8"); await sleep(50);
      const { items } = await x.ask({ type: "menu", tabId: 7 });
      const texts = items.map((i) => i.text);
      assert.strictEqual(texts.length, 4, texts.join(" | "));
      assert.match(texts[0], /Some Video \| Site, TS file, 12 sec, quality 240p, 500 kbps, about \d+/);
      assert.match(texts[3], /MP4 file, 12 sec, quality 720p HD, 3000 kbps/);
      const r = await x.ask({ type: "menuPick", tabId: 7, index: 2, all: false });      // 720p, TS
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      const item = await waitFor(() => state(dir).find((i) => i.Url.endsWith("/hls/vod.m3u8") && i.Status === "Complete"), 40, "HLS download");
      assert.strictEqual(path.basename(item.FilePath), "Some Video _ Site.ts");
      assert.strictEqual(sha(item.FilePath), await expected("vod"));
    });

    await test("'Download all links': one batch, both files complete", async () => {
      const r = await x.ask({ type: "downloadLinks", urls: [BASE + "/small.bin?one", BASE + "/small.bin?two"], referrer: BASE + "/list" });
      assert.strictEqual(r.ok, true); assert.strictEqual(r.count, 2);
      await waitFor(() => state(dir).filter((i) => i.Url.includes("/small.bin?") && i.Status === "Complete").length === 2, 30, "batch");
      const names = state(dir).filter((i) => i.Url.includes("/small.bin?")).map((i) => path.basename(i.FilePath)).sort();
      assert.strictEqual(new Set(names).size, 2, "two different file names: " + names);
    });

    await test("'Download all links' with no window: page links go through the real host; the login-only file gets THIS site's cookie", async () => {
      const y = loadExtension();
      const r = await y.ask({ type: "grabLinks", tabId: 7 });
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      assert.strictEqual(r.count, 3, "images are not downloaded automatically");
      // (the earlier cookie test already left one completed /needs-cookie item behind, so count per address)
      await waitFor(() => state(dir).some((i) => i.Url.includes("?ep2") && i.Status === "Complete") && state(dir).filter((i) => i.Url.endsWith("/needs-cookie") && i.Status === "Complete").length >= 2, 30, "links");
      const login = state(dir).filter((i) => i.Url.endsWith("/needs-cookie") && i.Status === "Complete");
      assert.ok(login.every((i) => fs.statSync(i.FilePath).size === 307200), "both copies are the real file, not an error page");
    });

    await test("DASH: qualities listed, pick downloads video (+ audio kept separately without FFmpeg)", async () => {
      const y = loadExtension();
      y.sniff(BASE + "/dash/static.mpd", "application/dash+xml"); await sleep(50);
      const { items } = await y.ask({ type: "menu", tabId: 7 });
      assert.strictEqual(items.length, 2, items.map((i) => i.text).join(" | "));       // DASH has one format (MP4) per quality
      assert.match(items[1].text, /MP4 file, 12 sec, quality 720p HD, 1500 kbps/);
      const r = await y.ask({ type: "menuPick", tabId: 7, index: 1, all: false });
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      const item = await waitFor(() => state(dir).find((i) => i.Url.includes("/dash/static.mpd") && i.Status === "Complete"), 40, "DASH download");
      assert.strictEqual(sha(item.FilePath), await expected("dash-v2"));
    });

    await stopApp(app);
  }

  // =================================================== "always ask" ON: nothing may start by itself
  {
    await getText(BASE + "/__reset");
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "makan-e2e-ask-"));
    const app = await startApp(dir, "ask");
    const x = loadExtension();
    console.log("\n== extension -> native host -> app, 'always ask' is ON");

    await test("browser download: the app is asked (with size + real name), the browser copy is cancelled, NOTHING is downloaded", async () => {
      await x.chrome.downloads.onCreated.fire({ id: 201, url: BASE + "/cd", referrer: BASE + "/page" });
      const p = await waitFor(() => prompts(dir).find((q) => q.kind === "download"), 20, "download prompt");
      assert.strictEqual(p.payload.Size, 307200);
      assert.strictEqual(p.payload.ProbedName, "Quarterly Report (final).pdf");
      assert.strictEqual(p.payload.Cookie, "session=abc");
      assert.deepStrictEqual(x.cancelled, [201]);
      await sleep(1500);
      assert.strictEqual(state(dir).length, 0, "no download item exists");
      assert.deepStrictEqual(fs.readdirSync(dir).filter((f) => f.endsWith(".pdf") || f.endsWith(".part")), []);
    });

    await test("video pick: the save-as prompt gets url, title, format and audio; nothing is queued", async () => {
      x.sniff(BASE + "/hls/master-audio.m3u8"); await sleep(50);
      const { items } = await x.ask({ type: "menu", tabId: 7 });
      assert.ok(items.length >= 2, items.map((i) => i.text).join(" | "));
      const r = await x.ask({ type: "menuPick", tabId: 7, index: 1, all: false });      // MP4
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      const p = await waitFor(() => prompts(dir).find((q) => q.kind === "stream"), 20, "stream prompt");
      assert.strictEqual(p.payload.Format, "mp4"); assert.strictEqual(p.payload.Title, "Some Video | Site");
      assert.ok(p.payload.Url.endsWith("/hls/vonly.m3u8")); assert.ok(p.payload.AudioUrl.endsWith("/hls/audio-en.m3u8"));
      assert.strictEqual(state(dir).length, 0);
    });

    await test("'Download all' while asking: items are added but stay Stopped (nothing starts)", async () => {
      const r = await x.ask({ type: "menuPick", tabId: 7, index: -1, all: true });
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      await waitFor(() => state(dir).length >= 1, 20, "stopped items");
      await sleep(2000);
      const items = state(dir);
      assert.ok(items.every((i) => i.Status === "Paused"), JSON.stringify(items.map((i) => i.Status)));
      assert.ok(items.every((i) => !fs.existsSync(i.FilePath)), "no file was created");
    });

    await test("'Download all links': the app gets the whole page (links, text, kinds) and per-host cookies; nothing starts", async () => {
      const before = state(dir).length;
      const r = await x.ask({ type: "grabLinks", tabId: 7 });
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      const p = await waitFor(() => prompts(dir).find((q) => q.kind === "links"), 20, "links prompt");
      assert.strictEqual(p.payload.Links.length, 4); assert.strictEqual(p.payload.PageTitle, "Lanterns S01");
      assert.strictEqual(p.payload.Links[0].Text, "Episode 1 (needs login)");
      assert.deepStrictEqual(Object.keys(p.payload.Cookies), ["127.0.0.1"]); assert.strictEqual(p.payload.Cookies["127.0.0.1"], "session=abc");
      await sleep(800);
      assert.strictEqual(state(dir).length, before, "nothing was queued behind the window's back");
    });

    await test("selected links (the 'Download with Makan' bar): only that frame's selection goes to the app's picker", async () => {
      const r = await x.ask({ type: "grabSelection" }, { tab: { id: 7, url: BASE + "/serial" }, frameId: 0 });
      assert.strictEqual(r.ok, true, JSON.stringify(r));
      await waitFor(() => prompts(dir).filter((q) => q.kind === "links").length >= 2, 20, "second links prompt");
    });

    await test("batch of links: one prompt for the whole batch, nothing starts", async () => {
      const before = state(dir).length;
      const r = await x.ask({ type: "downloadLinks", urls: [BASE + "/small.bin?a", BASE + "/small.bin?b"], referrer: BASE + "/list" });
      assert.strictEqual(r.ok, true);
      const p = await waitFor(() => prompts(dir).find((q) => q.kind === "batch"), 20, "batch prompt");
      assert.strictEqual(p.payload.Urls.length, 2);
      await sleep(1000);
      assert.strictEqual(state(dir).length, before);
    });

    await stopApp(app);
  }

  console.log(`\n${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
