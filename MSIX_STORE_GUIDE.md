# Microsoft Store / MSIX release guide

Epsilon's website installer and Microsoft Store package are separate release channels:

- SignPath signs the Inno Setup `.exe` distributed from GitHub and makanlab.tech.
- Microsoft signs the `.msix` distributed through Microsoft Store after certification.

## 1. Reserve the product

In Partner Center, open **Windows & Xbox > Apps and games**, choose **New product > MSIX or PWA app**, check **Epsilon Download Manager**, and reserve it if available.

Then open **Product management > Product identity** and copy these values exactly:

- Package/Identity/Name
- Package/Identity/Publisher
- Publisher display name

## 2. Build the Store package

From the repository root:

```powershell
.\build-msix.ps1 `
  -IdentityName 'VALUE FROM PARTNER CENTER' `
  -Publisher 'VALUE FROM PARTNER CENTER' `
  -PublisherDisplayName 'VALUE FROM PARTNER CENTER'
```

The result is written to `msix-output\EpsilonDownloadManager-<version>-x64.msix`.

Do not invent or normalize the identity values. A package whose Identity Name or Publisher differs from Partner Center will be rejected.

## 3. Store submission information

- Product name: Epsilon Download Manager
- Category: Utilities & tools
- Pricing: Free
- Website: https://makanlab.tech/epsilon/
- Support: https://github.com/makan20042002/Epsilon-Download-Manager/issues
- Privacy policy: https://makanlab.tech/privacy.html
- Recommended minimum OS: Windows 10, version 2004 (build 19041)
- Architecture: x64

The package declares internet access, private-network access (for the optional phone status page and peer-to-peer features), full trust, magnet-link activation, `.torrent` file activation, and an optional disabled-by-default startup task.

## Browser integration note

MSIX does not run the Inno Setup browser-registration steps. Store users should open Epsilon and use **Options > General > Set up / repair browser integration** once after installation. The browser extensions are distributed separately.
