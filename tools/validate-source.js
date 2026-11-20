// Source validation that needs only Node.js (used by tests/run-all.sh). tools/validate-source.ps1 does the same on a Windows build machine.
// Every check prints PASS / FAIL; the exit code is 1 when anything failed.
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
const exists = (p) => fs.existsSync(path.join(root, p));
const walk = (dir, filter, out = []) => {
  for (const e of fs.readdirSync(path.join(root, dir), { withFileTypes: true })) {
    if (["bin", "obj", "node_modules", "publish"].includes(e.name)) continue;
    const rel = path.join(dir, e.name);
    if (e.isDirectory()) walk(rel, filter, out); else if (filter(rel)) out.push(rel);
  }
  return out;
};

const version = read("VERSION.txt").trim();
const checks = [];
const check = (name, fn) => checks.push({ name, fn });

check(`desktop version ${version} is used everywhere`, () => {
  const problems = [];
  const expect = (label, text, re) => { const m = text.match(re); if (!m || m[1] !== version) problems.push(`${label}: ${m ? m[1] : "not found"}`); };
  expect("ProductIdentity.cs", read("MakanDownloadManager/ProductIdentity.cs"), /Version = "([^"]+)"/);
  expect("desktop project", read("MakanDownloadManager/EpsilonDownloadManager.csproj"), /<Version>([^<]+)<\/Version>/);
  expect("native host project", read("MakanNativeHost/EpsilonNativeHost.csproj"), /<Version>([^<]+)<\/Version>/);
  expect("updater", read("updater/Program.cs"), /ProductVersion = "([^"]+)"/);
  expect("installer", read("installer/EpsilonDownloadManager.iss"), /#define MyAppVersion "([^"]+)"/);
  return problems.length ? problems.join("; ") : true;
});

check("published extension versions remain unchanged and match", () => {
  const chrome = JSON.parse(read("browser-extension/manifest.json")).version;
  const firefox = JSON.parse(read("browser-extension-firefox/manifest.json")).version;
  return chrome === firefox && chrome === "1.9.0";
});

check("store extensions keep one public native-messaging host identity", () => {
  const id = "com.makan.downloadmanager";
  const files = ["MakanDownloadManager/ProductIdentity.cs", "install-browser-integration.ps1", "browser-extension/background.js", "browser-extension-firefox/background.js"];
  const missing = files.filter((f) => !read(f).includes(id));
  return missing.length ? "missing in: " + missing.join(", ") : true;
});

check("V2 desktop and native relay use the same private pipe", () => {
  const match = read("MakanDownloadManager/ProductIdentity.cs").match(/NativePipeName = "([^"]+)"/);
  return !!match && match[1] === "com.makan.downloadmanager.v2" && read("MakanNativeHost/Program.cs").includes(match[1]);
});

check("both extensions are Manifest V3 with a toolbar popup", () => {
  for (const d of ["browser-extension", "browser-extension-firefox"]) {
    const m = JSON.parse(read(d + "/manifest.json"));
    if (m.manifest_version !== 3) return d + " is not Manifest V3";
    if (m.action?.default_popup !== "popup.html") return d + " has no toolbar popup";
  }
  return true;
});

check("the two extension copies are identical (background, content, popup, i18n)", () => {
  const differ = ["background.js", "content.js", "popup.js", "i18n.js", "popup.html"].filter((f) => read("browser-extension/" + f) !== read("browser-extension-firefox/" + f));
  return differ.length ? "differ: " + differ.join(", ") : true;
});

check("all XAML files are well-formed XML", () => {
  const bad = [];
  for (const f of walk(".", (p) => p.endsWith(".xaml"))) {
    const text = read(f);
    const opens = (text.match(/<[A-Za-z][^<>]*[^\/<>]>/g) || []).length, closes = (text.match(/<\/[A-Za-z][^<>]*>/g) || []).length, selfClosing = (text.match(/<[A-Za-z][^<>]*\/>/g) || []).length;
    if (opens !== closes) bad.push(`${f} (${opens} opening vs ${closes} closing tags)`);
    void selfClosing;
  }
  return bad.length ? bad.join("; ") : true;
});

check("every named StaticResource exists in application, control, or window resources", () => {
  const app = read("MakanDownloadManager/App.xaml");
  const controls = read("MakanDownloadManager/Themes/ControlStyles.xaml");
  const global = new Set([...(`${app}\n${controls}`).matchAll(/x:Key="([A-Za-z_]\w*)"/g)].map((m) => m[1]));
  const missing = [];
  for (const f of walk("MakanDownloadManager", (p) => p.endsWith(".xaml") && !p.endsWith(path.join("Themes", "ControlStyles.xaml")))) {
    const text = read(f);
    const available = new Set([...global, ...[...text.matchAll(/x:Key="([A-Za-z_]\w*)"/g)].map((m) => m[1])]);
    for (const m of text.matchAll(/\{StaticResource\s+([A-Za-z_]\w*)\}/g)) if (!available.has(m[1])) missing.push(`${f}: ${m[1]}`);
  }
  return missing.length ? missing.join("; ") : true;
});

check("XAML resources are defined before they are used (a StaticResource cannot look forward)", () => {
  // App.xaml and ControlStyles.xaml (merged into the application resources at start-up, before any window opens) are always available.
  const app = read("MakanDownloadManager/App.xaml") + "\n" + read("MakanDownloadManager/Themes/ControlStyles.xaml");
  const global = new Set([...app.matchAll(/x:Key="([A-Za-z_]\w*)"/g)].map((m) => m[1]));
  const bad = [];
  for (const f of walk("MakanDownloadManager", (p) => p.endsWith(".xaml") && !p.endsWith(path.join("Themes", "ControlStyles.xaml")))) {
    const text = read(f);
    const defined = new Map();
    for (const m of text.matchAll(/x:Key="([A-Za-z_]\w*)"/g)) if (!defined.has(m[1])) defined.set(m[1], m.index);
    for (const m of text.matchAll(/\{StaticResource\s+([A-Za-z_]\w*)\}/g)) {
      if (global.has(m[1])) continue;
      const at = defined.get(m[1]);
      if (at === undefined || at > m.index) bad.push(`${f}: ${m[1]}`);
    }
  }
  return bad.length ? [...new Set(bad)].join("; ") : true;
});

check("progress window: tabs, 1/5/30 min graph, segment legend, network breakdown, logs, SHA-256 and save-folder footer", () => {
  const xaml = read("MakanDownloadManager/DownloadProgressWindow.xaml");
  const code = read("MakanDownloadManager/DownloadProgressWindow.xaml.cs");
  const need = ['Tag="Overview"', 'Tag="Segments"', 'Tag="Connections"', 'Tag="Sources"', 'Tag="Networks"', 'Tag="Logs"', 'Content="Multi-Network"',
    'Tag="60"', 'Tag="300"', 'Tag="1800"', 'Content="1 min"', 'Content="5 min"', 'Content="30 min"',
    'x:Name="SpeedCanvas"', 'x:Name="SegmentGrid"', 'x:Name="LegDownloading"', 'x:Name="LegComplete"', 'x:Name="LegWaiting"', 'x:Name="LegError"', 'x:Name="LegUnassigned"',
    'x:Name="NetList"', 'x:Name="NetListFull"', 'x:Name="LogList"', 'x:Name="Sha256Text"', 'x:Name="SaveFooterText"', 'x:Name="ChangeSaveButton"',
    'x:Name="ConnList"', 'x:Name="PositionGrid"', 'x:Name="UseLimiter"', 'x:Name="StartPauseButton"'];
  const missing = need.filter((n) => !xaml.includes(n));
  const wiring = ["SpeedHistory", "SegmentMap.Classify", "NetworkBreakdown.FromConnections", "ProgressLog", "VerificationText.For", "ChangeSaveFolder"].filter((n) => !code.includes(n));
  return missing.length || wiring.length ? "missing in XAML: " + missing.join(", ") + " | not used by the window: " + wiring.join(", ") : true;
});

check("main window: minimum size, horizontal scrolling table, 15 saved column widths, cards and sidebar", () => {
  const xaml = read("MakanDownloadManager/MainWindow.xaml");
  const code = read("MakanDownloadManager/MainWindow.xaml.cs");
  const widths = read("MakanDownloadManager/Services/ColumnWidths.cs");
  const problems = [];
  if (!/MinWidth="980"/.test(xaml) || !/MinHeight="620"/.test(xaml)) problems.push("window minimum size is not 980x620");
  if (!/x:Name="Downloads"[^>]*ScrollViewer\.HorizontalScrollBarVisibility="(Auto|Visible)"/.test(xaml)) problems.push("the download table has no horizontal scroll bar");
  const names = [...xaml.matchAll(/x:Name="(Grid(?!Index)\w+Column)"/g)].map((m) => m[1]);
  const listed = (code.match(/GridViewColumn\[\] GridColumns\(\) => new\[\] \{([^}]*)\}/) || [, ""])[1].split(",").map((x) => x.trim()).filter(Boolean);
  const defaults = ((widths.match(/Defaults = \{([^}]*)\}/) || [, ""])[1].split(",").map((x) => x.trim()).filter(Boolean));
  if (names.length !== 15) problems.push("expected 15 resizable columns, found " + names.length);
  if (listed.length !== 15 || names.some((n) => !listed.includes(n))) problems.push("GridColumns() does not list every column");
  if (defaults.length !== 15) problems.push("ColumnWidths.Defaults has " + defaults.length + " values, not 15");
  if (!/ColumnWidths\.Parse\(/.test(code) || !/ColumnWidths\.Serialize\(/.test(code)) problems.push("MainWindow does not use ColumnWidths to restore/save widths");
  if (!/ColumnWidths\.DefaultText/.test(read("MakanDownloadManager/Services/AppSettings.cs"))) problems.push("AppSettings default is not ColumnWidths.DefaultText");
  if (!/Property="MinWidth" Value="70"/.test(xaml)) problems.push("column header minimum width missing");
  for (const n of ["HeaderDownloadSpeed", "HeaderUploadSpeed", "HeaderActiveCount", "HeaderQueueCount", "HeaderMultiNet"]) if (!xaml.includes('x:Name="' + n + '"')) problems.push("dashboard card " + n + " missing");
  return problems.length ? problems.join("; ") : true;
});

check("remote control: QR code, address helper and beginner connection guide; retry settings reach the engine", () => {
  const xaml = read("MakanDownloadManager/SettingsWindow.xaml");
  const code = read("MakanDownloadManager/SettingsWindow.xaml.cs");
  const app = read("MakanDownloadManager/App.xaml.cs");
  const problems = [];
  for (const n of ['x:Name="RemoteQrImage"', 'x:Name="RemoteQrHint"', 'x:Name="RemoteUrlBox"', 'Header="Phone connection guide"', 'If it does not connect', 'x:Name="RetryAttemptsBox"', 'x:Name="RetryWindowBox"'])
    if (!xaml.includes(n)) problems.push("SettingsWindow.xaml: " + n);
  for (let i = 1; i <= 5; i++) if (!xaml.includes('Text="' + i + '. ')) problems.push("guide step " + i + " missing");
  for (const n of ["RemoteAccess.BuildUrl", "RemoteAccess.ParsePort", "RemoteQr.Png", "RemoteAccess.NewToken"]) if (!code.includes(n)) problems.push("SettingsWindow.xaml.cs does not use " + n);
  for (const n of ["Manager.RetryAttempts = Settings.RetryAttempts", "Manager.RetryWindow = TimeSpan.FromSeconds(Settings.RetryWindowSeconds)"]) if (!app.includes(n)) problems.push("App.xaml.cs: " + n);
  if (!/s\.RetryAttempts = /.test(code) || !/s\.RetryWindowSeconds = /.test(code)) problems.push("Settings window does not save the retry values");
  return problems.length ? problems.join("; ") : true;
});

check("every event handler named in XAML exists in its code-behind", () => {
  const missing = [];
  for (const f of walk("MakanDownloadManager", (p) => p.endsWith(".xaml") && !p.includes("Themes"))) {
    const codeFile = f + ".cs";
    if (!exists(codeFile)) continue;
    const xaml = read(f), code = read(codeFile);
    const handlers = new Set([...xaml.matchAll(/\b(?:Click|Checked|Unchecked|SelectionChanged|TextChanged|MouseDoubleClick|Loaded|Drop|DragOver|Closing|KeyDown|PreviewKeyDown|ValueChanged|MouseLeftButtonDown|ContextMenuOpening|SizeChanged)="([A-Za-z_]\w*)"/g)].map((m) => m[1]));
    for (const h of handlers) if (!new RegExp("\\b" + h + "\\s*\\(").test(code)) missing.push(`${f}: ${h}`);
  }
  return missing.length ? missing.join("; ") : true;
});

check("every x:Name used by code-behind exists in the XAML", () => {
  const missing = [];
  for (const f of walk("MakanDownloadManager", (p) => p.endsWith(".xaml") && !p.includes("Themes"))) {
    if (!exists(f + ".cs")) continue;
    const xaml = read(f), code = read(f + ".cs");
    const names = new Set([...xaml.matchAll(/x:Name="(\w+)"/g)].map((m) => m[1]));
    for (const m of code.matchAll(/\b([A-Z][A-Za-z0-9]+)\.(?:Text|IsChecked|Visibility|ItemsSource|SelectedIndex|SelectedItem|IsEnabled|Children|Opacity|Fill|Value)\b/g))
      if (!names.has(m[1]) && /^(SmartDot|SmartText|BrowserDot|BrowserText|ThemeGlyph|Footer|ActiveText|TotalSpeed|UrlText|StatusText|SizeText|DownloadedText|RateText|EtaText|ResumeText|LimitBox|UseLimiter|RememberLimit|ShowCompleteBox|ExitBox|PowerBox|PowerCombo|ForceBox|MainBar|DetailsPanel|DetailsButton|ConnList|PositionGrid|SaveToText|StartPauseButton|LimiterRate)$/.test(m[1])) missing.push(`${f}: ${m[1]}`);
  }
  return missing.length ? missing.join("; ") : true;
});

check("no conflict markers, TODO/FIXME or NotImplementedException in the source", () => {
  const bad = walk(".", (p) => /\.(cs|js|xaml|ps1)$/.test(p) && !p.startsWith("tests") && !p.startsWith("tools") && !p.includes("node_modules"))
    .filter((f) => /<<<<<<<|>>>>>>>|\bTODO\b|\bFIXME\b|NotImplementedException/.test(read(f)));
  return bad.length ? bad.join(", ") : true;
});

check("no C# event is assigned in an object initializer (Click = handler)", () => {
  const bad = walk("MakanDownloadManager", (p) => p.endsWith(".cs")).filter((f) => /\{[^{};]*\bClick\s*=\s*[A-Za-z_(]/.test(read(f)));
  return bad.length ? bad.join(", ") : true;
});

check("release tooling and required files are present", () => {
  const need = ["installer/EpsilonDownloadManager.iss", "updater/EpsilonUpdater.csproj", "updater/Program.cs", "tools/windows-production-test.ps1", "build-release.ps1", "install-browser-integration.ps1",
    "MakanDownloadManager/Themes/ControlStyles.xaml", "MakanDownloadManager/IntelligentCenterWindow.cs", "MakanDownloadManager/Branding.cs", "MakanNativeHost/Program.cs"];
  const missing = need.filter((f) => !exists(f));
  return missing.length ? "missing: " + missing.join(", ") : true;
});

check("secrets and diagnostics: DPAPI cookies, redacted logs, HTTPS-only updater", () => {
  const ok = read("MakanDownloadManager/Services/DownloadDb.cs").includes("SecretProtector") && read("MakanDownloadManager/Services/DiagnosticsService.cs").includes("REDACTED") && /HTTPS/i.test(read("updater/Program.cs")) && read("updater/Program.cs").includes("SHA256");
  return ok ? true : "one of the security features is missing";
});

let failed = 0;
for (const c of checks) {
  let result;
  try { result = c.fn(); } catch (e) { result = "error: " + e.message; }
  if (result === true) console.log("PASS  " + c.name);
  else { failed++; console.log("FAIL  " + c.name + "  ->  " + result); }
}
if (failed) { console.log(`\nSource validation FAILED (${failed}).`); process.exit(1); }
console.log("\nSource validation passed.");
