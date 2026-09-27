# V15 Feature Matrix

| Roadmap area | V15 state |
|---|---|
| Smarter download engine | Existing adaptive + persistent per-server learning retained; V15 exposes the learned profile. |
| Download Intelligence Engine | V15 local intelligence/recommendation center added. |
| Premium download cards/progress | Existing premium UI retained; no duplicate engine/UI path introduced. |
| Download detail panel | Existing Download Info dialog retained. |
| Browser integration | Existing Chrome/Edge/Firefox interception retained; final browser E2E remains a Windows validation gate. |
| Download library/history/search | Existing category/history/search UI retained. |
| Rich notifications | Existing completion/failure notifications retained. |
| Powerful scheduler | Existing queue/scheduler retained; advanced OS-condition rules remain an extension point. |
| Bandwidth profiles | New Unlimited/Gaming/Work/Night profiles in V15 Center. |
| Security | Existing cookie protection, SHA-256 and signed-update helpers retained; V15 diagnostic export is redaction-safe. |
| Health Center | New database/engine/network/storage/native-host/tool checks. |
| Diagnostic report | V15 report enriches the existing safe diagnostic ZIP with system/engine/browser/database/update/health files. |
| Crash recovery | Existing atomic state manifests retained; V15 recovery discovery UI added. |
| Download verification | Existing SHA-256 and size verification retained. |
| 500+ download performance | Existing centralized manager/event architecture retained; final stress testing is a Windows validation task. |
| Local statistics | New V15 local statistics panel. |
| Optional AI | Architecture remains local-first; no cloud AI dependency was added. |
| Production architecture | V15 layer sits over the V15 download engine without replacing stable download paths. |

## Final validation still required

Because this is a WPF/Windows application, the source package should be compiled and tested on Windows with the .NET 8 SDK. That final pass should include WPF compilation, installer execution, native-messaging registration, browser E2E, Windows notifications, sleep/resume, battery/idle conditions, and high-concurrency stress tests.
