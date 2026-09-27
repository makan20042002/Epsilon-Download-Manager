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

check(`version ${version} is used everywhere`, () => {
  const problems = [];
  const expect = (label, text, re) => { const m = text.match(re); if (!m || m[1] !== version) problems.push(`${label}: ${m ? m[1] : "not found"}`); };
  expect("Branding.cs", read("MakanDownloadManager/Branding.cs"), /Version = "([^"]+)"/);
  expect("NativeBridge.cs", read("MakanDownloadManager/Services/NativeBridge.cs"), /Version = "([^"]+)"/);
  expect("Chrome manifest", read("browser-extension/manifest.json"), /"version":\s*"([^"]+)"/);
  expect("Firefox manifest", read("browser-extension-firefox/manifest.json"), /"version":\s*"([^"]+)"/);
  expect("updater", read("updater/Program.cs"), /ProductVersion = "([^"]+)"/);
  expect("installer", read("installer/MakanDownloadManager.iss"), /#define MyAppVersion "([^"]+)"/);
  return problems.length ? problems.join("; ") : true;
});

check("one native-messaging identity (bridge pipe, host, installer script, Windows integration, extensions)", () => {
  const id = "com.makan.downloadmanager";
  const files = ["MakanDownloadManager/Services/NativeBridge.cs", "MakanNativeHost/Program.cs", "install-browser-integration.ps1", "MakanDownloadManager/Services/WindowsIntegration.cs", "browser-extension/background.js", "browser-extension-firefox/background.js"];
  const missing = files.filter((f) => !read(f).includes(id));
  return missing.length ? "missing in: " + missing.join(", ") : true;
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
  const need = ["installer/MakanDownloadManager.iss", "updater/MakanUpdater.csproj", "updater/Program.cs", "tools/windows-production-test.ps1", "build-release.ps1", "install-browser-integration.ps1",
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
