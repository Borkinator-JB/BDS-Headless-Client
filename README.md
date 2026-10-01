# BDS Headless Client

Lets Xbox and PlayStation friends join a Minecraft Bedrock server.

A bot account shows your server in its friends' Friends tab. They press **Join** and end up on your server.

## How it works

1. The bot signs in with a Microsoft account and pings the selected server. It doesn't join it.
2. It publishes a joinable Xbox Live session for that server.
3. A friend presses Join. The console connects to the bot over NetherNet (WebRTC).
4. The bot sends a `Transfer` packet, and the console connects to the server itself. A friend with an assigned server goes there instead.

Use a separate Microsoft account for the bot. It needs Xbox friends with everyone who wants to join.

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
2. **Servers**: add your server (e.g. `exampleserver.com`, port `19132`) and press **Join**. Only if you enter a local address (`127.0.0.1`, `192.168.x.x`), open **Advanced** and set the public address friends should use.
3. **Friends**: add friends by gamertag, or turn on auto accept. To send a friend to another saved server, pick it next to their name (web) or tap them (Android). **Default** uses the active server.

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

GitHub Actions builds every PR and push to `main`: tests, Linux (deb, rpm, tar.gz), Windows (setup + portable zip) and Android APK. Every merge to `main` publishes a release `v0.1.<build>` with all installers. Pushing a `v*` tag publishes a release with that version.

Optional secrets for a release-signed APK: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`. Without them the APK uses the debug key.

### Protect main

Settings, Rules, Rulesets, New ruleset, **Import a ruleset**, and pick `.github/rulesets/main.json`. It blocks direct pushes to `main` and requires a PR with a passing **Build and test** check.

## Limitations

- Uses the same unofficial Xbox and Minecraft endpoints as other community tools. Mojang updates can break login or NetherNet until this is updated. Version-specific code lives in `src/Bds.Core/Protocol` and `src/Bds.Core/NetherNet`.
- Friend routing trusts the XUID the console sends. It's a convenience, not access control.
