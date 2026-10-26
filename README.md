# StinePods

Make cheap, **single-point** Bluetooth headphones/earbuds/speakers follow your audio
between your Android phone and your Windows PC — automatically and as fast as the
hardware allows.

Inspired by [PodSwitch](https://github.com/Felip6499/PodSwitch), but written from
scratch (no code is copied from it). Unlike PodSwitch, the phone
and PC **coordinate**: the device that is losing the headphones releases them on
purpose and pauses its media, so the other device can connect immediately instead of
"stealing" the connection.

> Status: **0.2.0 preview**: working Android and Windows apps, being tested with friends.

## Download

Latest release: **[StinePods releases](https://github.com/austinedearan-stack/stinepods/releases/latest)** (setup steps and checksums are on that page)

| | File | For |
|---|---|---|
| Phone (recommended) | [StinePods.apk](https://github.com/austinedearan-stack/stinepods/releases/latest/download/StinePods.apk) | Android 10 – 16 |
| Phone, with per-app rules | [StinePods-full.apk](https://github.com/austinedearan-stack/stinepods/releases/latest/download/StinePods-full.apk) | Android 10 – 16 (Google may block this one when sideloaded in some countries) |
| PC | [StinePods.exe](https://github.com/austinedearan-stack/stinepods/releases/latest/download/StinePods.exe) | Windows 10 / 11, 64-bit; one file, nothing else to install |

**Quick setup:** pair phone ↔ PC ↔ headphones over Bluetooth → run `StinePods.exe` on the PC → install the APK
on the phone, open it, tap **Fix** on each setup item, choose your PC and headphones → confirm the 6-digit code on
both screens.

## Platforms

| Platform | Versions | Status |
|---|---|---|
| Android | 10 – 16 | preview (tested on Android 10 and 12) |
| Windows | 10 and 11 | preview (tested on Windows 10) |
| Linux | — | later |

## Repo layout

```
android/    Android app (Kotlin): "lite" and "full" flavors
desktop/    Windows tray app (.NET 10) + shared core and tests
protocol/   Phone<->PC protocol spec + shared test vectors
spikes/     Phase 0 experiments
tools/      serve.cs: share the built apps on the local network
docs/       Plan, hurdles, security, decisions, spike results
```

## Building

- **Windows:** `powershell -ExecutionPolicy Bypass -File desktop\publish.ps1` → `dist\windows\StinePods.exe`
- **Android:** `cd android && ./gradlew assembleLiteRelease` → `app/build/outputs/apk/lite/release/`
- **Tests:** `dotnet test desktop/StinePods.Core.Tests` (spec scenarios + 1200 two-device simulations) and
  `cd android && ./gradlew testLiteDebugUnitTest`

## Docs

- [docs/PLAN.md](docs/PLAN.md) — architecture and phased implementation plan
- [docs/HURDLES.md](docs/HURDLES.md) — known technical risks
- [docs/SECURITY.md](docs/SECURITY.md) — threat model and mitigations
- [docs/DECISIONS.md](docs/DECISIONS.md) — confirmed requirements and decision log
- [docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md) — things still to decide
