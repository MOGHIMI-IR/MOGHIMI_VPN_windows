# Third-party components

Moghimi VPN for Windows is a desktop front end and uses separately built third-party networking components.

## sing-box 1.14.4
- Project: https://github.com/SagerNet/sing-box
- License: GNU General Public License v3.0 (GPL-3.0).
- The official Windows x64 binary is downloaded at build time from the tagged upstream release and checked against the SHA-256 digest published by GitHub.
- The corresponding upstream license is included in the packaged runtime folder when present in the official release archive.

## Wintun 0.14.1
- Project: https://github.com/WireGuard/wintun
- Official download: https://www.wintun.net/
- The AMD64 DLL is extracted from the official Wintun ZIP and the ZIP is checked against the SHA-256 digest published by the Wintun website.
- Wintun's own license and signature are preserved; this project does not modify or re-sign the driver DLL.

No modified system driver, test-signing mode, security exclusion or Defender setting is installed by this application.
