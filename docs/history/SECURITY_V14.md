# V14 Security Hardening

- Update metadata and packages are HTTPS-only.
- Update package SHA-256 is verified before installation.
- Unsafe update redirects are rejected.
- Failed staged updates restore the previous installation directory.
- Browser/native messaging keeps the stable `com.makan.downloadmanager` identity.
- Secret/cookie handling remains DPAPI-protected in the desktop application.
- Browser content is isolated by origin before credentials are forwarded.
- Download redirects continue through the existing SafeRedirectHandler.
- Release packages should be Authenticode-signed in the Windows release pipeline.
- Installer should be signed before public distribution.

## Release rule
Never publish an update manifest or installer over HTTP. The V14 updater intentionally rejects HTTP metadata and packages.
