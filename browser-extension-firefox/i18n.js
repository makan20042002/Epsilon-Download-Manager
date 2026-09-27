// Epsilon Download Manager – interface texts in English and Persian (the language is the one chosen in the desktop app's Options).
// t("English text") returns the Persian text when Persian is active; tf("... {0} ...", value) fills in values after translating.
(() => {
  "use strict";
  const FA = {
    // overlay on videos
    "Download this video": "دانلود این ویدیو",
    "Open MDM": "باز کردن MDM",
    "Hide this button on this page": "پنهان کردن این دکمه در این صفحه",
    "Looking for video streams…": "در حال جستجوی جریان‌های ویدیو…",
    "Can't reach the Epsilon extension (reload the page).": "دسترسی به افزونهٔ اپسیلون ممکن نیست (صفحه را دوباره بارگذاری کنید).",
    "No downloadable stream found yet. Press play, wait a few seconds and try again. (Some sites protect their videos.)": "هنوز جریان قابل دانلودی پیدا نشد. پخش را بزنید، چند ثانیه صبر کنید و دوباره امتحان کنید. (بعضی سایت‌ها از ویدیوهایشان محافظت می‌کنند.)",
    "Download all": "دانلود همه",
    "Sending to Epsilon…": "در حال ارسال به اپسیلون…",
    "Sent to Epsilon Download Manager ✓": "به اپسیلون دانلود منیجر ارسال شد ✓",
    "Already in Epsilon's list ✓": "از قبل در فهرست اپسیلون است ✓",
    "Epsilon did not answer. Is the native host installed?": "اپسیلون پاسخ نداد. آیا برنامهٔ میزبان نصب شده است؟",
    "Epsilon did not answer": "اپسیلون پاسخ نداد",
    "Epsilon did not answer.": "اپسیلون پاسخ نداد.",
    // selected-links bar
    "Download with Epsilon": "دانلود با اپسیلون",
    "Download with Epsilon ({0} link)": "دانلود با اپسیلون ({0} لینک)",
    "Download with Epsilon ({0} links)": "دانلود با اپسیلون ({0} لینک)",
    "Sending…": "در حال ارسال…",
    "Sent {0} link — choose in Epsilon ✓": "{0} لینک ارسال شد — در اپسیلون انتخاب کنید ✓",
    "Sent {0} links — choose in Epsilon ✓": "{0} لینک ارسال شد — در اپسیلون انتخاب کنید ✓",
    "Hide": "پنهان کردن",
    // context menus
    "Download with MDM": "دانلود با MDM",
    "Download this media with MDM": "دانلود این رسانه با MDM",
    "Download all links with MDM…": "دانلود همهٔ لینک‌ها با MDM…",
    // errors / messages from the background
    "No page.": "صفحه‌ای نیست.",
    "No tab.": "زبانه‌ای نیست.",
    "Can't read this page. Reload the page and try again.": "خواندن این صفحه ممکن نیست. صفحه را دوباره بارگذاری کنید و دوباره امتحان کنید.",
    "No links found on this page.": "هیچ لینکی در این صفحه پیدا نشد.",
    "No links in the selection.": "هیچ لینکی در بخش انتخاب‌شده نیست.",
    "That entry is no longer available. Reopen the menu.": "این مورد دیگر در دسترس نیست. منو را دوباره باز کنید.",
    "Nothing to download.": "چیزی برای دانلود نیست.",
    "Failed": "ناموفق",
    "Unknown error": "خطای ناشناخته",
    "Unknown message": "پیام ناشناخته",
    "No response from Epsilon Download Manager.": "پاسخی از اپسیلون دانلود منیجر دریافت نشد.",
    "Sent {0} download to Epsilon ✓": "{0} دانلود به اپسیلون ارسال شد ✓",
    "Sent {0} downloads to Epsilon ✓": "{0} دانلود به اپسیلون ارسال شد ✓",
    // video menu words
    "file": "فایل", "quality": "کیفیت", "source": "اصلی", "about": "حدود", "size": "حجم", "kbps": "kbps",
    "(+ audio track)": "(+ صدا)", "hr": "ساعت", "min": "دقیقه", "sec": "ثانیه",
    "video stream — can't read the {0}: {1}": "جریان ویدیو — خواندن {0} ممکن نیست: {1}",
    "manifest": "مانیفست", "playlist": "فهرست پخش", "no answer from Epsilon": "اپسیلون پاسخ نداد",
    // YouTube via yt-dlp
    "audio": "صدا", "subtitles": "زیرنویس", "can't read this video: {0}": "خواندن این ویدیو ممکن نیست: {0}",
    "YouTube needs yt-dlp. Click here to open Epsilon, then Options > YouTube & other sites > Download / update tools.": "یوتیوب به yt-dlp نیاز دارد. اینجا کلیک کنید تا اپسیلون باز شود، سپس تنظیمات > یوتیوب و سایت‌های دیگر > دانلود / به‌روزرسانی ابزارها.",
    "Epsilon is open: Options > YouTube & other sites": "اپسیلون باز است: تنظیمات > یوتیوب و سایت‌های دیگر",
    // popup
    "Epsilon Download Manager": "اپسیلون دانلود منیجر",
    "Checking…": "در حال بررسی…",
    "Take over browser downloads": "در اختیار گرفتن دانلودهای مرورگر",
    "Media on this page": "رسانه‌های این صفحه",
    "Play a video or audio on the page and it will show up here.": "ویدیو یا صدایی را در صفحه پخش کنید تا اینجا نمایش داده شود.",
    "Links on this page": "لینک‌های این صفحه",
    "Download all links…": "دانلود همهٔ لینک‌ها…",
    "Opens a window in Epsilon with every link of the page (file name, type, size) so you can tick what to download.": "پنجره‌ای در اپسیلون باز می‌کند با همهٔ لینک‌های صفحه (نام فایل، نوع، حجم) تا موارد دلخواه را تیک بزنید.",
    "Tip: select some links on the page with the mouse and click the “Download with Epsilon” bar that appears at the bottom-left.": "نکته: چند لینک را در صفحه با ماوس انتخاب کنید و روی نوار «دانلود با اپسیلون» که پایین صفحه ظاهر می‌شود کلیک کنید.",
    "Don't take over these sites": "این سایت‌ها را در اختیار نگیر",
    "one per line, e.g. example.com": "در هر خط یکی، مثلاً example.com",
    "Save": "ذخیره", "Open Epsilon": "باز کردن اپسیلون", "Saved.": "ذخیره شد.",
    "Connected — Epsilon {0} is running": "متصل — اپسیلون {0} در حال اجراست",
    "Epsilon will start automatically on the next download": "اپسیلون در دانلود بعدی خودکار اجرا می‌شود",
    "Native host not registered — run install-browser-integration.ps1, then restart the browser": "برنامهٔ میزبان ثبت نشده است — install-browser-integration.ps1 را اجرا کنید و مرورگر را دوباره راه‌اندازی کنید",
    "Extension ID doesn't match the installed host. This ID: {0} — run install-browser-integration.ps1 -ExtensionId {0}": "شناسهٔ افزونه با میزبان نصب‌شده یکی نیست. این شناسه: {0} — install-browser-integration.ps1 -ExtensionId {0} را اجرا کنید",
    "Host found but it won't start — is MakanNativeHost.exe still next to MakanDownloadManager.exe?": "میزبان پیدا شد اما اجرا نمی‌شود — آیا MakanNativeHost.exe هنوز کنار MakanDownloadManager.exe است؟",
    "Can't reach Epsilon": "دسترسی به اپسیلون ممکن نیست",
    "Last problem: {0} (the browser kept that download)": "آخرین مشکل: {0} (مرورگر آن دانلود را نگه داشت)",
    "Extension {0} · ": "افزونه {0} · ",
    "Button on the video: waiting for a video on this page (reload the page if you just updated the extension).": "دکمهٔ روی ویدیو: منتظر ویدیو در این صفحه (اگر افزونه را تازه به‌روز کرده‌اید صفحه را دوباره بارگذاری کنید).",
    "Button on the video: shown": "دکمهٔ روی ویدیو: نمایش داده می‌شود",
    " (top-right corner of the page)": " (گوشهٔ بالای صفحه)",
    "Button on the video: not shown — {0} (media detected: {1}).": "دکمهٔ روی ویدیو: نمایش داده نمی‌شود — {0} (رسانهٔ شناسایی‌شده: {1}).",
    "no video found": "ویدیویی پیدا نشد",
    "Reading video streams…": "در حال خواندن جریان‌های ویدیو…",
    "No downloadable video found yet.": "هنوز ویدیوی قابل دانلودی پیدا نشد.",
    "Download": "دانلود", "Sent ✓": "ارسال شد ✓",
    "Download all (one file per quality)": "دانلود همه (یک فایل برای هر کیفیت)",
    "Reading the page…": "در حال خواندن صفحه…",
    "Sent {0} link to Epsilon — choose in its window ✓": "{0} لینک به اپسیلون ارسال شد — در پنجرهٔ آن انتخاب کنید ✓",
    "Sent {0} links to Epsilon — choose in its window ✓": "{0} لینک به اپسیلون ارسال شد — در پنجرهٔ آن انتخاب کنید ✓"
  };

  let lang = "en";
  const fill = (text, args) => text.replace(/\{(\d)\}/g, (_, i) => (args[+i] !== undefined ? args[+i] : ""));
  const api = {
    get lang() { return lang; },
    get dir() { return lang === "fa" ? "rtl" : "ltr"; },
    setLang(value) { lang = value === "fa" ? "fa" : "en"; },
    t(text) { return lang === "fa" && Object.prototype.hasOwnProperty.call(FA, text) ? FA[text] : text; },
    tf(text, ...args) { return fill(api.t(text), args); },
    /** Translates every element of a page that has data-t="English text" (and placeholders with data-t-placeholder). */
    translateDom(root) {
      root.querySelectorAll("[data-t]").forEach((el) => { el.textContent = api.t(el.getAttribute("data-t")); });
      root.querySelectorAll("[data-t-placeholder]").forEach((el) => { el.setAttribute("placeholder", api.t(el.getAttribute("data-t-placeholder"))); });
      if (root.documentElement) { root.documentElement.dir = api.dir; root.documentElement.lang = lang; }
    }
  };
  globalThis.MakanI18n = api;
})();
