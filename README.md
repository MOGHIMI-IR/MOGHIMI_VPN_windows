# Moghimi VPN for Windows

A native Windows desktop client for Moghimi VPN. This repository is independent from the Android and iOS projects.

## Current implementation plan
- Windows 10/11 x64, .NET 8 WPF desktop app.
- Modern dark red/graphite interface, Persian-first RTL layout.
- sing-box 1.14.4 as the pinned tunnel engine; Wintun 0.14.1 official signed DLL for the TUN interface.
- VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC, AnyTLS, WireGuard, SOCKS5 and HTTP profile handling (per-engine compatibility and transport caveats apply).
- Import from clipboard, links, files, subscription URLs and the active Moghimi Supabase catalogue.
- TUN and system-proxy connection modes, TCP latency checks, profile management, DNS/routing/ad-block settings and local diagnostic logs.
- Local profile/subscription data protected with Windows DPAPI (CurrentUser).
- GitHub Actions builds a portable package and setup installer; releases are attached to tags.

## Security model
- No antivirus disabling, exclusions, process hiding, obfuscation, persistence tricks or security bypasses.
- No analytics, ads, tracking SDK, account login, Windows credentials or browser data access.
- No service-role / Supabase secret key in the client. Only the project's public publishable key is used; access is controlled by RLS.
- TUN mode asks Windows for elevation at connect time through a dedicated Moghimi tunnel host; the desktop UI remains a standard user process. Cancelling UAC cancels the connection.
- System Proxy is a separate, user-level option. It saves and restores the previous settings.
- The app does not auto-connect at launch.
- Wintun is obtained from the official WireGuard/Wintun distribution and verified against its published SHA-256 before packaging. sing-box is downloaded from its official release and verified before packaging.

## SmartScreen and antivirus
No developer can guarantee that Microsoft Defender or SmartScreen will never warn on a new application. New/unsigned releases can have unknown reputation. A verified Authenticode publisher signature and consistent release identity can improve trust over time; self-signed certificates do not establish public trust. Never disable Defender to install this app.

## Build
Open `MoghimiVPN.sln` with Visual Studio 2022 (or newer) and build the `MoghimiVPN` project for `win-x64`.

The GitHub Actions workflow downloads the pinned, hash-verified network engine and Wintun binary, builds the app, packages the portable ZIP and creates an Inno Setup installer.

## Secrets
No GitHub secret is required for normal CI or for reading the Moghimi Supabase catalogue. Optional Authenticode signing is intentionally not configured until a real certificate is available. Never put certificate private keys, Supabase service-role keys or passwords in source code.

## Validation status
A successful CI build proves compilation and packaging only. Before calling a release stable, test on a clean Windows 10 and Windows 11 x64 machine: UAC cancellation, TUN connect/disconnect, DNS, routing, IPv4/IPv6 leak behavior, system-proxy restoration, sleep/resume, network changes, subscription updates, and each protocol/transport against real endpoints.
