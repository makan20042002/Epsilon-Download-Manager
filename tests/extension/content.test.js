// Runs the REAL content.js inside jsdom with a fake extension API. (jsdom has no layout: video rectangles are stubbed.)
const assert = require("assert");
const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const code = fs.readFileSync(path.join(__dirname, "../../browser-extension/content.js"), "utf8");
const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));
let pass = 0, fail = 0;
async function test(name, fn) { try { await fn(); pass++; console.log("  PASS ", name); } catch (e) { fail++; console.log("  FAIL ", name, "\n       ", e.stack.split("\n").slice(0, 3).join("\n        ")); } }

const i18nCode = fs.readFileSync(path.join(__dirname, "../../browser-extension/i18n.js"), "utf8");
function page({ lang = "", videoRect = { left: 100, top: 50, width: 640, height: 360 }, replies = {}, currentSrc = "", noVideo = false, playing = false } = {}) {
  const dom = new JSDOM('<!doctype html><body><video id="v"></video></body>', { runScripts: "outside-only", pretendToBeVisual: true, url: "https://example.com/watch" });
  const win = dom.window;
  const sent = [];
  const listeners = [];
  win.chrome = { runtime: { id: "test-id", lastError: undefined, onMessage: { addListener: (f) => listeners.push(f) }, sendMessage: (message, cb) => { sent.push(message); setTimeout(() => cb(typeof replies[message.type] === "function" ? replies[message.type](message) : replies[message.type]), 0); } } };
  const v = win.document.getElementById("v");
  const r = { ...videoRect, right: videoRect.left + videoRect.width, bottom: videoRect.top + videoRect.height };
  v.getBoundingClientRect = () => r;
  if (currentSrc) Object.defineProperty(v, "currentSrc", { value: currentSrc });
  if (playing) Object.defineProperty(v, "paused", { value: false });
  if (noVideo) v.remove();
  const original = win.Element.prototype.attachShadow;               // expose the (closed) shadow root to the test
  win.Element.prototype.attachShadow = function (init) { const root = original.call(this, { ...init, mode: "open" }); win.__shadow = root; return root; };
  if (lang) { win.chrome.storage = { local: { get: async () => ({ lang }) } }; win.eval(i18nCode); }
  win.eval(code);
  return { win, sent, v, dom, listeners };
}
const sh = (win, sel) => win.__shadow.querySelector(sel);
const click = (win, el) => el.dispatchEvent(new win.MouseEvent("click", { bubbles: true, composed: true }));

(async () => {
  await test("button appears on top-left of the video when the tab has media", async () => {
    const { win } = page({ replies: { pageMedia: { count: 2 } } });
    await win.__makanTick(); await tick();
    const host = win.document.querySelector('[data-makan="overlay"]');
    assert.ok(host, "overlay host exists");
    assert.strictEqual(host.style.display, "block");
    assert.strictEqual(host.style.left, "108px"); assert.strictEqual(host.style.top, "58px");
    assert.strictEqual(sh(win, ".main").textContent.trim(), "▶Download this video");
    win.close();
  });

  await test("no media and no direct source => no button; a tiny video is not anchored (media found => floating button)", async () => {
    const { win } = page({ replies: { pageMedia: { count: 0 } } });
    await win.__makanTick(); await tick();
    assert.ok(!win.document.querySelector('[data-makan="overlay"]'));
    win.close();
    const small = page({ videoRect: { left: 0, top: 0, width: 120, height: 80 }, replies: { pageMedia: { count: 3 } } });
    await small.win.__makanTick(); await tick();
    const floating = small.win.document.querySelector('[data-makan="overlay"]');
    assert.ok(floating && floating.style.top === "14px", "tiny video is not anchored, but the detected media is offered as a floating button");
    small.win.close();
  });

  await test("a plain http(s) <video src> is offered even before the sniffer saw anything", async () => {
    const { win } = page({ replies: { pageMedia: { count: 0 } }, currentSrc: "https://cdn.example.com/a.mp4" });
    await win.__makanTick(); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').style.display, "block");
    win.close();
  });

  await test("clicking opens the menu: 'Download all' + separator + numbered list, like IDM", async () => {
    const items = [{ text: "Clip, TS file, quality 240p, 530 kbps" }, { text: "Clip, MP4 file, quality 240p, 530 kbps" }, { text: "Clip, MP4 file, quality 1080p HD, 4912 kbps" }];
    const { win, sent } = page({ replies: { pageMedia: { count: 1 }, menu: { items } } });
    await win.__makanTick(); await tick();
    click(win, sh(win, ".main")); await tick(30);
    const rows = [...win.__shadow.querySelectorAll(".item")].map((e) => e.textContent);
    assert.deepStrictEqual(rows, ["Download all", "1.  Clip, TS file, quality 240p, 530 kbps", "2.  Clip, MP4 file, quality 240p, 530 kbps", "3.  Clip, MP4 file, quality 1080p HD, 4912 kbps"]);
    assert.ok(sh(win, ".sep"));
    assert.ok(sent.some((m) => m.type === "menu"));
    win.close();
  });

  await test("picking an item sends its index; success message shown; 'Download all' sends all:true", async () => {
    const items = [{ text: "A" }, { text: "B" }];
    const { win, sent } = page({ replies: { pageMedia: { count: 1 }, menu: { items }, menuPick: { ok: true, message: "Sent to Epsilon Download Manager ✓" } } });
    await win.__makanTick(); await tick();
    click(win, sh(win, ".main")); await tick(30);
    click(win, [...win.__shadow.querySelectorAll(".item")].find((e) => e.textContent.includes("2.  B"))); await tick(30);
    assert.strictEqual(JSON.stringify(sent.filter((m) => m.type === "menuPick").at(-1)), JSON.stringify({ type: "menuPick", index: 1, all: false }));
    assert.match(sh(win, ".note").textContent, /Sent to Epsilon/);
    win.close();
    const b = page({ replies: { pageMedia: { count: 1 }, menu: { items }, menuPick: { ok: true } } });
    await b.win.__makanTick(); await tick();
    click(b.win, sh(b.win, ".main")); await tick(30);
    click(b.win, [...b.win.__shadow.querySelectorAll(".item")].find((e) => e.textContent === "Download all")); await tick(30);
    assert.strictEqual(JSON.stringify(b.sent.filter((m) => m.type === "menuPick").at(-1)), JSON.stringify({ type: "menuPick", index: -1, all: true }));
    b.win.close();
  });

  await test("errors from Epsilon are shown in red; disabled entries can't be picked; empty list explains itself", async () => {
    const bad = page({ replies: { pageMedia: { count: 1 }, menu: { items: [{ text: "X" }] }, menuPick: { ok: false, error: "Epsilon Download Manager is not running." } } });
    await bad.win.__makanTick(); await tick();
    click(bad.win, sh(bad.win, ".main")); await tick(30);
    click(bad.win, sh(bad.win, ".item")); await tick(30);
    assert.ok(sh(bad.win, ".note.bad")); assert.match(sh(bad.win, ".note").textContent, /not running/);
    bad.win.close();
    const dis = page({ replies: { pageMedia: { count: 1 }, menu: { items: [{ text: "can't read", disabled: true }] } } });
    await dis.win.__makanTick(); await tick();
    click(dis.win, sh(dis.win, ".main")); await tick(30);
    click(dis.win, sh(dis.win, ".item.disabled")); await tick(30);
    assert.strictEqual(dis.sent.filter((m) => m.type === "menuPick").length, 0);
    dis.win.close();
    const empty = page({ replies: { pageMedia: { count: 1 }, menu: { items: [] } } });
    await empty.win.__makanTick(); await tick();
    click(empty.win, sh(empty.win, ".main")); await tick(30);
    assert.match(sh(empty.win, ".note").textContent, /No downloadable stream found/);
    empty.win.close();
  });

  await test("✕ hides the button for this page; ? asks Epsilon to open; page text can't inject markup", async () => {
    const evil = '<img src=x onerror=alert(1)>';
    const { win, sent } = page({ replies: { pageMedia: { count: 1 }, menu: { items: [{ text: evil }] } } });
    await win.__makanTick(); await tick();
    click(win, sh(win, ".main")); await tick(30);
    const item = sh(win, ".item");
    assert.strictEqual(item.textContent.includes("<img"), true); assert.strictEqual(win.__shadow.querySelector("img"), null, "rendered as text, not HTML");
    click(win, [...win.__shadow.querySelectorAll(".tool")][0]); await tick();
    assert.ok(sent.some((m) => m.type === "open"));
    click(win, [...win.__shadow.querySelectorAll(".tool")][1]); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').style.display, "none");
    await win.__makanTick(); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').style.display, "none", "stays hidden");
    win.close();
  });

  await test("page scan reports <video src>, <source>, og:video, media links and player JSON (once, deduplicated)", async () => {
    const { win, sent } = page({ replies: { pageMedia: { count: 0 }, pageUrls: { ok: true } } });
    win.document.head.innerHTML = '<meta property="og:video:secure_url" content="https://cdn.example.com/og.mp4">';
    win.document.body.insertAdjacentHTML("beforeend", '<video id="v2"><source src="/rel/clip.webm"></video><a href="https://cdn.example.com/dl/file.mkv">dl</a><a href="/page.html">x</a>' +
      '<script>var cfg = {"hls":"https:\\/\\/cdn.example.com\\/live\\/master.m3u8?a=1\\u0026b=2","img":"https://cdn.example.com/a.jpg"};</script>');
    await win.__makanScan(); await tick();
    const msg = sent.filter((m) => m.type === "pageUrls").at(-1);
    assert.ok(msg, "pageUrls sent");
    const urls = [...msg.urls].sort();
    assert.deepStrictEqual(urls, ["https://cdn.example.com/dl/file.mkv", "https://cdn.example.com/live/master.m3u8?a=1&b=2", "https://cdn.example.com/og.mp4", "https://example.com/rel/clip.webm"], JSON.stringify(urls));
    const before = sent.filter((m) => m.type === "pageUrls").length;
    await win.__makanScan(); await tick();
    assert.strictEqual(sent.filter((m) => m.type === "pageUrls").length, before, "unchanged page is not re-reported");
    win.close();
  });

  await test("a hiccup (worker not answering once) does NOT remove the button", async () => {
    let calls = 0;
    const { win } = page({ replies: { pageMedia: () => (++calls === 2 ? undefined : { count: 1 }) } });
    await win.__makanTick(); await tick();
    assert.ok(win.document.querySelector('[data-makan="overlay"]'));
    await win.__makanTick(); await tick();                    // this one gets no answer
    assert.ok(win.document.querySelector('[data-makan="overlay"]'), "still there");
    await win.__makanTick(); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').style.display, "block");
    win.close();
  });

  await test("video inside a shadow root (custom-element players) gets the button", async () => {
    const { win } = page({ noVideo: true, replies: { pageMedia: { count: 1 } } });
    const holder = win.document.createElement("my-player"); win.document.body.append(holder);
    const root = holder.attachShadow({ mode: "open" });
    const inner = win.document.createElement("video");
    inner.getBoundingClientRect = () => ({ left: 30, top: 40, width: 500, height: 280, right: 530, bottom: 320 });
    root.append(inner);
    await win.__makanTick(); await tick();
    const host = win.document.querySelector('[data-makan="overlay"]');
    assert.ok(host && host.style.display === "block"); assert.strictEqual(host.style.left, "38px"); assert.strictEqual(host.style.top, "48px");
    win.close();
  });

  await test("no visible <video> but media detected: floating button in the top-right corner; disappears when a frame shows its own", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 2 } } });
    await win.__makanTick(); await tick();
    const host = win.document.querySelector('[data-makan="overlay"]');
    assert.ok(host && host.style.display === "block"); assert.strictEqual(host.style.top, "14px");
    assert.ok(parseFloat(host.style.left) > win.innerWidth / 2, "on the right side");
    const iframe = win.document.createElement("iframe"); win.document.body.append(iframe);
    win.dispatchEvent(new win.MessageEvent("message", { data: { __makan: "active" }, source: iframe.contentWindow }));
    await win.__makanTick(); await tick();
    assert.strictEqual(host.style.display, "none", "the iframe with the video shows the anchored button, no duplicate");
    const reports = sent.filter((m) => m.type === "overlayReport").map((m) => m.info);
    assert.ok(reports.some((i) => i.mode === "floating") && reports.at(-1).shown === false);
    win.close();
  });

  await test("a playing video with nothing detected still gets the button; menu explains the situation", async () => {
    const { win, sent } = page({ playing: true, replies: { pageMedia: { count: 0 }, menu: { items: [] } } });
    await win.__makanTick(); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').style.display, "block");
    click(win, sh(win, ".main")); await tick(30);
    assert.match(sh(win, ".note").textContent, /No downloadable stream found/);
    win.close();
    const idle = page({ replies: { pageMedia: { count: 0 } } });               // paused, nothing detected: stay out of the way
    await idle.win.__makanTick(); await idle.win.__makanTick(); await tick();
    assert.ok(!idle.win.document.querySelector('[data-makan="overlay"]'));
    const info = idle.sent.filter((m) => m.type === "overlayReport").at(-1).info;
    assert.strictEqual(info.shown, false); assert.match(info.reason, /nothing downloadable has been detected/);
    idle.win.close();
  });

  await test("full screen: the button moves into the full-screen element (otherwise it would be invisible)", async () => {
    const { win } = page({ replies: { pageMedia: { count: 1 } } });
    await win.__makanTick(); await tick();
    const wrapper = win.document.createElement("div"); win.document.body.append(wrapper);
    Object.defineProperty(win.document, "fullscreenElement", { value: wrapper, configurable: true });
    win.document.dispatchEvent(new win.Event("fullscreenchange")); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').parentNode, wrapper);
    Object.defineProperty(win.document, "fullscreenElement", { value: null, configurable: true });
    win.document.dispatchEvent(new win.Event("fullscreenchange")); await tick();
    assert.strictEqual(win.document.querySelector('[data-makan="overlay"]').parentNode, win.document.documentElement);
    win.close();
  });

  await test("collectLinks: links with their visible text, media and images; duplicates merged; javascript:/mailto: dropped; only the top frame answers", async () => {
    const { win, listeners } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
    win.document.title = "Lanterns S01";
    win.document.body.innerHTML = '<a href="/dl/e1.mkv"><b>Download</b> part 1</a><a href="/dl/e1.mkv" title="again">x</a><a href="/w/1.html"></a><a href="/w/1.html" aria-label="Watch online"></a>' +
      '<a href="javascript:void(0)">js</a><a href="mailto:a@b.c">mail</a><video src="/v/clip.mp4"></video><img src="/big.jpg" alt="Cover" width="300"><img src="/dot.gif" width="1">';
    win.document.querySelectorAll("img").forEach((i) => Object.defineProperty(i, "complete", { value: true }));
    Object.defineProperty(win.document.querySelectorAll("img")[0], "naturalWidth", { value: 300 });
    Object.defineProperty(win.document.querySelectorAll("img")[1], "naturalWidth", { value: 1 });
    let reply; listeners.forEach((f) => f({ type: "collectLinks" }, {}, (r) => (reply = r)));
    assert.ok(reply, "top frame answered");
    const byUrl = Object.fromEntries(reply.links.map((l) => [new URL(l.url).pathname, l]));
    assert.strictEqual(byUrl["/dl/e1.mkv"].kind, "link");
    assert.ok(/part 1/.test(byUrl["/dl/e1.mkv"].text) || byUrl["/dl/e1.mkv"].text === "again", "text from the link");
    assert.strictEqual(byUrl["/w/1.html"].text, "Watch online", "text filled in from the duplicate");
    assert.strictEqual(byUrl["/v/clip.mp4"].kind, "media"); assert.strictEqual(byUrl["/big.jpg"].kind, "image"); assert.strictEqual(byUrl["/big.jpg"].text, "Cover");
    assert.ok(!byUrl["/dot.gif"], "tiny images are ignored");
    assert.strictEqual(reply.links.length, 4); assert.strictEqual(reply.title, "Lanterns S01");
    win.close();
  });

  await test("selecting links shows the 'Download with Epsilon (N links)' bar; only selected links are collected; click sends grabSelection", async () => {
    const { win, sent, listeners } = page({ noVideo: true, replies: { pageMedia: { count: 0 }, grabSelection: { ok: true, count: 3 } } });
    win.document.body.innerHTML = '<div id="box"><a href="https://cdn.example.com/g/p1.rar">Part 1</a> <a href="https://cdn.example.com/g/p2.rar">Part 2</a> <a href="https://cdn.example.com/g/p3.rar">Part 3</a></div><a id="outside" href="https://x.example/other.zip">Other</a>';
    const range = win.document.createRange(); range.selectNodeContents(win.document.getElementById("box"));
    const selection = win.getSelection(); selection.removeAllRanges(); selection.addRange(range);
    let reply; listeners.forEach((f) => f({ type: "collectLinks", selectionOnly: true }, {}, (r) => (reply = r)));
    assert.strictEqual(JSON.stringify(reply.links.map((l) => new URL(l.url).pathname)), JSON.stringify(["/g/p1.rar", "/g/p2.rar", "/g/p3.rar"]), "the link outside the selection is not included");
    assert.strictEqual(JSON.stringify(reply.links.map((l) => l.text)), JSON.stringify(["Part 1", "Part 2", "Part 3"]));
    await win.__makanSelection(); await tick();
    const pill = win.document.querySelector('[data-makan="links"]');
    assert.ok(pill && pill.style.display === "block", "bar is shown");
    assert.match(win.__shadow.textContent, /Download with Epsilon \(3 links\)/);
    click(win, win.__shadow.querySelector(".main")); await tick(30);
    assert.ok(sent.some((m) => m.type === "grabSelection"));
    assert.match(win.__shadow.textContent, /Sent 3 links/);
    selection.removeAllRanges(); await win.__makanSelection(); await tick();
    assert.strictEqual(pill.style.display, "none", "bar disappears when the selection is cleared");
    win.close();
  });

  await test("Persian: the on-video button and the selected-links bar speak Persian and read right-to-left", async () => {
    const { win } = page({ lang: "fa", noVideo: true, replies: { pageMedia: { count: 0 } } });
    await tick(30);
    win.document.body.innerHTML = '<div id="box"><a href="https://cdn.example.com/g/p1.rar">a</a> <a href="https://cdn.example.com/g/p2.rar">b</a></div>';
    const range = win.document.createRange(); range.selectNodeContents(win.document.getElementById("box"));
    const sel = win.getSelection(); sel.removeAllRanges(); sel.addRange(range);
    await win.__makanSelection(); await tick();
    assert.match(win.__shadow.textContent, /دانلود با اپسیلون \(2 لینک\)/);
    assert.strictEqual(win.document.querySelector('[data-makan="links"]').style.direction, "rtl");
    win.close();
    const v = page({ lang: "fa", replies: { pageMedia: { count: 2 } } });
    await tick(30); await v.win.__makanTick(); await tick();
    assert.match(v.win.__shadow.querySelector(".main").textContent, /دانلود این ویدیو/);
    v.win.close();
  });

  await test("Alt + click on a page tells the background (so the browser may keep that download)", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
    win.document.body.innerHTML = '<a id="l" href="https://x.example/f.zip">f</a>';
    win.document.getElementById("l").dispatchEvent(new win.MouseEvent("click", { bubbles: true, altKey: true, cancelable: true }));
    await tick();
    assert.ok(sent.some((m) => m.type === "altClick"));
    sent.length = 0;
    win.document.getElementById("l").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true }));
    await tick();
    assert.ok(!sent.some((m) => m.type === "altClick"), "a plain click is not reported");
    win.close();
  });

  await test("Ctrl + click (and Cmd, for Mac) on a regular link also tells the background - not just Alt", async () => {
    for (const mods of [{ ctrlKey: true }, { metaKey: true }]) {
      const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
      win.document.body.innerHTML = '<a id="l" href="https://x.example/f.zip">f</a>';
      const notCanceled = win.document.getElementById("l").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ...mods }));
      await tick();
      assert.ok(sent.some((m) => m.type === "altClick"), JSON.stringify(mods) + ": the background is told to let the browser keep this download");
      assert.strictEqual(notCanceled, true, JSON.stringify(mods) + ": the browser's own click/navigation is not blocked");
      win.close();
    }
  });

  await test("clicking a magnet link is intercepted and sent to Epsilon instead of the browser", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 }, magnet: { ok: true } } });
    win.document.body.innerHTML = '<a id="m" href="magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038&dn=Movie">Get it</a>';
    const notCanceled = win.document.getElementById("m").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    assert.strictEqual(notCanceled, false, "the browser's own navigation is prevented");
    await tick();
    const sentMagnet = sent.find((m) => m.type === "magnet");
    assert.ok(sentMagnet, "a magnet message was sent");
    assert.match(sentMagnet.url, /^magnet:\?xt=urn:btih:/);
    assert.ok(win.document.querySelector('[data-makan="toast"]'), "a toast is shown near the click");
    win.close();
  });

  await test("clicking a direct link to a video/audio file is intercepted too (the browser would otherwise just play it)", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 }, capture: { ok: true } } });
    win.document.body.innerHTML = '<a id="v" href="https://cdn.example.com/movies/episode.mp4">Watch</a>';
    const notCanceled = win.document.getElementById("v").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    assert.strictEqual(notCanceled, false, "the browser's own navigation (which would just play the video) is prevented");
    await tick();
    const captured = sent.find((m) => m.type === "capture");
    assert.ok(captured, "a capture message was sent");
    assert.strictEqual(captured.url, "https://cdn.example.com/movies/episode.mp4");
    assert.ok(win.document.querySelector('[data-makan="toast"]'), "a toast is shown near the click");
    win.close();
  });

  await test("a direct link to a plain file (.rar, .zip, .exe - not video/audio) is left completely alone by the click handler", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
    win.document.body.innerHTML = '<a id="r" href="https://example.com/archive.rar">Download</a>';
    const notCanceled = win.document.getElementById("r").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    assert.strictEqual(notCanceled, true, "the browser's own click/navigation is not blocked - the existing download-event capture handles this one, not the click handler");
    await tick();
    assert.ok(!sent.some((m) => m.type === "capture" || m.type === "magnet"), "nothing was sent for it from the click handler");
    win.close();
  });

  await test("Ctrl/Shift/Meta-click and Alt-click on a magnet link are left to the browser", async () => {
    for (const mods of [{ ctrlKey: true }, { shiftKey: true }, { metaKey: true }, { altKey: true }]) {
      const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
      win.document.body.innerHTML = '<a id="m" href="magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038">Get it</a>';
      const notCanceled = win.document.getElementById("m").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ...mods }));
      assert.strictEqual(notCanceled, true, JSON.stringify(mods) + ": the browser keeps handling this click");
      await tick();
      assert.ok(!sent.some((m) => m.type === "magnet"), JSON.stringify(mods) + ": no magnet message sent");
      win.close();
    }
  });

  await test("a non-magnet link click is left completely alone", async () => {
    const { win, sent } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
    win.document.body.innerHTML = '<a id="l" href="https://x.example/page">Read more</a>';
    const notCanceled = win.document.getElementById("l").dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    assert.strictEqual(notCanceled, true);
    await tick();
    assert.ok(!sent.some((m) => m.type === "magnet"));
    win.close();
  });

  await test("a selection without links shows no bar; the ✕ dismisses it for that selection", async () => {
    const { win } = page({ noVideo: true, replies: { pageMedia: { count: 0 } } });
    win.document.body.innerHTML = '<p id="p">just text</p><a id="a" href="https://x.example/f.zip">File</a>';
    const sel = win.getSelection(); const r1 = win.document.createRange(); r1.selectNodeContents(win.document.getElementById("p")); sel.removeAllRanges(); sel.addRange(r1);
    await win.__makanSelection(); await tick();
    assert.ok(!win.document.querySelector('[data-makan="links"]'), "no links selected: no bar");
    const r2 = win.document.createRange(); r2.selectNodeContents(win.document.getElementById("a")); sel.removeAllRanges(); sel.addRange(r2);
    await win.__makanSelection(); await tick();
    const pill = win.document.querySelector('[data-makan="links"]'); assert.strictEqual(pill.style.display, "block");
    click(win, win.__shadow.querySelector(".tool")); await tick();
    assert.strictEqual(pill.style.display, "none");
    await win.__makanSelection(); await tick();
    assert.strictEqual(pill.style.display, "none", "stays dismissed for the same selection");
    win.close();
  });

  await test("orphaned script (extension reloaded) removes its button instead of throwing", async () => {
    const { win } = page({ replies: { pageMedia: { count: 1 } } });
    await win.__makanTick(); await tick();
    assert.ok(win.document.querySelector('[data-makan="overlay"]'));
    win.chrome.runtime.id = undefined;                       // what an orphaned content script sees
    win.chrome.runtime.sendMessage = () => { throw new Error("Extension context invalidated."); };
    await win.__makanTick(); await tick();
    assert.ok(!win.document.querySelector('[data-makan="overlay"]'));
    win.close();
  });

  console.log(`\n${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})();
