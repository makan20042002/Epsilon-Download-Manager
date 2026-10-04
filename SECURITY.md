# Epsilon Download Manager — Security policy

## Supported version

Security fixes are provided for the latest public release, currently **1.3.x**.

## Reporting a vulnerability

Please use GitHub's private **Report a vulnerability** form in the repository's Security tab. Do not open a public issue for a vulnerability that could put users at risk.

## Security design

- Browser cookies are protected at rest using Windows DPAPI.
- Redirect handling avoids forwarding cookies across origins.
- Browser transfers are not cancelled until Makan acknowledges the job.
- Diagnostics redact cookie, authorization, token and signature values (also inside URLs).
- Native messaging uses a per-user named pipe with `CurrentUserOnly`.
- Update metadata and update packages must use HTTPS; redirects are followed by hand and every hop must be HTTPS.
- Update packages are SHA-256 verified before the installed application is stopped.
- Updates are extracted to an isolated staging directory; files are swapped one by one and every change is rolled back if any file fails.
- A DPAPI-protected secret that cannot be opened is discarded, never used as a cookie.
- Downloaded release artifacts can also be checked with SHA-256 and optional RSA signatures using `SecureUpdateVerifier`.
- Paths are normalized and filenames are sanitized before finalization.
- Browser permissions are limited to the APIs required for download capture, native messaging, cookies, page/media detection and user-triggered link collection.

Production distribution should additionally sign the Windows binaries and installer with the project's production Authenticode certificate.
