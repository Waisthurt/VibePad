# VibePad

VibePad turns an Android phone into a low-latency LAN input companion for a Windows computer.

## Current milestone

The repository contains the first vertical slice:

- Android phone: manual IP connection, multi-line text input, paste, Enter, hold-to-delete, and a multi-touch trackpad.
- Windows Agent: local WebSocket endpoint, clipboard paste, plus `SendInput` keyboard and mouse injection.
- Shared JSON protocol specification.

Automatic discovery, pairing, and tray UI are deliberately kept outside this first runnable slice.

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

This milestone has no pairing yet. Only run it on a trusted LAN. The next milestone must add the six-digit pairing flow and a stored device token before the service is distributed.
