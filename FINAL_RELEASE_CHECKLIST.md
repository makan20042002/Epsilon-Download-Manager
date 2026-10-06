# Epsilon Download Manager 1.7.1 — Release Checklist

## Source and automated verification

- [x] Version synchronized across app, installer, updater, native bridge, torrent peer ID, and both extensions
- [x] Release build succeeds on Windows with zero application warnings and errors
- [x] Source, XAML, native-messaging identity, and package validation
- [x] Segmented download, pause/resume, restart recovery, checksum, HLS/DASH, queue, scheduler, database, updater, and bridge tests
- [x] Multi-Network simulated two-route, failed-route, metered, approval, daily-budget, and keep-one-free tests
- [x] Torrent MSE/PE, IPv4/IPv6 tracker parsing, DHT persistence, UPnP, and NAT-PMP tests
- [x] English/Persian localization scan and theme contrast checks

## Windows visual QA

- [x] Main window checked in English, Persian RTL, Platinum Blue, and Makan Lab dark theme
- [x] Theme gallery and Multi-Network popup checked in light and dark themes
- [x] Options checked in dark theme
- [x] Persian filter/count labels corrected after visual inspection
- [ ] Recheck every secondary window on Windows 10
- [ ] Recheck every secondary window on Windows 11

## Real hardware and network gates

- [ ] Run “Test selected networks now” with two physical Internet connections active and confirm both adapters transfer bytes
- [ ] Download one large range-capable file with Wi-Fi + Ethernet and confirm both network names appear in Download Details
- [ ] Compare the same legal public torrent in Epsilon and a reference client under the same network conditions
- [ ] Sleep/wake, network interruption/recovery, and disk-full tests

## Installer, extensions, and release

- [x] Build self-contained 1.7.1 application and Inno Setup installer
- [ ] Verify upgrade from 1.7.0 preserves settings, history, queues, and partial downloads
- [ ] Verify clean install and uninstall on Windows 10/11
- [ ] Verify Chrome/Edge extension package on a clean profile
- [ ] Verify Firefox extension package on a clean profile
- [ ] Authenticode-sign application binaries and installer when a SignPath or commercial certificate is available
- [ ] Verify signatures with `Get-AuthenticodeSignature`
- [ ] Publish installer, portable zip, source archive, and both extension packages
- [x] Update README SHA-256 to match the final installer

**Release rule:** publish only after every applicable unchecked gate is completed or is explicitly documented as an external/manual limitation.
