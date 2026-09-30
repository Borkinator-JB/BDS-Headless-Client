# BDS Headless Client

Lets Xbox and PlayStation friends join a Minecraft Bedrock server.

A bot account joins your server. Friends of that account see it in their Friends tab, press **Join**, and end up on the same server.

## How it works

1. The bot signs in with a Microsoft account and joins the selected server over RakNet. It reads the player list.
2. It publishes a joinable Xbox Live session for that server.
3. A friend presses Join. The console connects to the bot over NetherNet (WebRTC).
4. The bot sends a `Transfer` packet, and the console connects to the server itself.

Use a separate Microsoft account for the bot. It needs Xbox friends with everyone who wants to join, and it needs access to the server (allowlist if you use one).

## Install

Download from the latest [release](../../releases) or from the artifacts of a CI run.

| Platform | File |
| --- | --- |
| Windows | `bds-headless-*-win-x64-setup.exe` |
| Debian/Ubuntu | `bds-headless_*_amd64.deb` / `arm64.deb` |
| Fedora/RHEL | `bds-headless-*.x86_64.rpm` / `aarch64.rpm` |
| Other Linux | `bds-headless-*-linux-*.tar.gz`, then `sudo ./install.sh` |
| Android | `bds-headless-*-android.apk` |

### Windows and Linux

- Installs a system service (`BdsHeadless` on Windows, `bds-headless.service` on Linux).
- **Start at boot** (default on) starts it at boot, before anyone logs in. Toggle it in Settings.
- Opening the app starts the service if needed and opens the UI at `http://127.0.0.1:5199`. Closing the browser keeps it running.
- Tray icon: **Open**, **Stop service**, **Exit** (closes only the tray).
- **Force close** in Settings stops the service.
- On Linux, log out and back in once after install so your user can start/stop the service.

### Android

Start the gateway from the main screen. It runs as a foreground service with a notification, so the back button or swiping the app away doesn't stop it. Use **Stop** in the notification or app. Allow the battery optimization exemption when asked.

## First run

1. **Account**: sign in. You get a code to enter on microsoft.com.
2. **Servers**: add your server and press **Join**. If the host is a LAN or localhost address, also set **Public host** so friends outside your network can reach it.
3. **Friends**: add friends by gamertag, or turn on auto accept.

## Security

- Sign in uses Microsoft's device code flow. The app never sees your password.
- Only the refresh token and a device key are stored, encrypted:
  - Windows: DPAPI under the service's own virtual account. Folder ACL allows only that account, SYSTEM and admins.
  - Linux: AES-GCM with a key sealed by `systemd-creds` (TPM2 if available), in a folder only the `bdsheadless` user can read.
  - Android: AES-GCM with a non-exportable Android Keystore key. Excluded from backups.
- The web UI listens on localhost only. LAN access is opt-in and password protected.
- Sign out deletes the stored token.

## Build

Needs the .NET 10 SDK.

```sh
dotnet build src/Bds.Host
dotnet run --project src/Bds.Host        # UI on http://127.0.0.1:5199
dotnet test --project tests/Bds.Core.Tests
```

Android needs `dotnet workload install android` and an Android SDK.

Database changes: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/Bds.Core --output-dir Storage/Migrations`.

## CI

GitHub Actions builds every PR and push to `main`: tests, Linux (deb, rpm, tar.gz), Windows (setup + portable zip) and Android APK. Tagging `v*` creates a release.

Optional secrets for a release-signed APK: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`. Without them the APK uses the debug key.

### Protect main

Settings, Rules, Rulesets, New ruleset, **Import a ruleset**, and pick `.github/rulesets/main.json`. It blocks direct pushes to `main` and requires a PR with a passing **Build and test** check.

## Limitations

- Uses the same unofficial Xbox and Minecraft endpoints as other community tools. Mojang updates can break login or NetherNet until this is updated. Version-specific code lives in `src/Bds.Core/Protocol` and `src/Bds.Core/NetherNet`.
- The bot takes one player slot.
- Servers with an idle kick may kick the bot. Disable `player-idle-timeout` or exempt the account.
