# VibePad

VibePad turns an Android phone into a low-latency LAN input companion for a Windows computer.

## Current milestone

The repository contains a usable LAN release:

- Android phone: manual IP connection, multi-line text input, paste, Enter, hold-to-delete, and a multi-touch trackpad.
- Windows Agent: tray application, local WebSocket endpoint, UDP mouse-motion fast path, clipboard controls, and `SendInput` keyboard/mouse injection.
- Shared JSON protocol specification.

Automatic discovery and device pairing are deliberately kept outside this release.

## Install on Windows

For normal use, download and run `VibePad-Agent-Setup-<version>.exe` from the release assets. The installer:

- includes the .NET runtime, so a separate .NET installation is not required;
- creates Start menu and desktop shortcuts;
- adds firewall rules for TCP `8765` and UDP `8767`, restricted to the local subnet;
- starts the Agent once after installation.

The installer requests administrator permission only to add/remove its firewall rules. In the Agent window, enable **开机自动启动** if desired.

## Run locally

### Windows Agent

Install the .NET 8 SDK, then run:

```powershell
dotnet run --project .\windows\VibePad.Agent
```

The Agent listens on port `8765`. Windows Firewall may ask for permission. If `HttpListener` reports an access-denied error, start the terminal once as Administrator or reserve the URL namespace for your user.

### Android app

Open the `android` directory in Android Studio, allow it to install its requested SDK components, and run it on an Android device connected to the same Wi-Fi network.

Enter the computer's LAN IP address, not `localhost`. The Agent prints detected local IPv4 addresses at startup.

## Security note

VibePad has no pairing yet. Only run it on a trusted LAN; do not expose port `8765` to the Internet.
