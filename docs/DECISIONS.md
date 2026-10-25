# Requirements & decision log

Newest entries at the bottom. Each decision says whether it is **confirmed** (by the
owner) or **proposed** (pending Phase 0 validation or owner sign-off).

## Confirmed requirements (2026-10-22)

1. **Target hardware:** cheap single-point (non-multipoint) Bluetooth audio devices.
   Must work across many brands/models, not one specific headset.
2. **Phones:** many OEMs, Android 12 through 17. Used by the owner and friends.
3. **PCs:** Windows 10 (owner) and Windows 11 (friends). Linux later.
4. **Connectivity:** users are usually on mobile data / hotspot, often *not* on the
   same Wi-Fi. The phone<->PC link must not depend on shared Wi-Fi.
5. **Privilege:** Shizuku or root is acceptable on Android. Switching speed is the
   top priority.
6. **Distribution:** sideloaded to a small group first; Google Play later, once stable.
7. **Switching rules:** fully user-configurable (no hard-coded policy).
8. **Ask vs automatic:** user toggle.
9. **Tech stack:** builder's choice.
10. **Repo:** monorepo, tracked in git and pushed to GitHub.

## Decisions

### D1 — Android app in Kotlin (confirmed by necessity)
Bluetooth, MediaSession and Shizuku APIs are native Android APIs; Kotlin + Jetpack
Compose is the simplest route.

### D2 — Desktop app in C# / .NET 10, split into a tiny agent + on-demand settings UI (proposed)
- The Windows pieces we need (WinRT Bluetooth/RFCOMM, media sessions (GSMTC), audio driver
  (KS) commands, `IPolicyConfig` for the default speaker) are COM/WinRT and are easiest from C#.
- **Agent** (always running): headless, plain Win32 tray icon, no UI framework,
  compiled ahead-of-time to native code (NativeAOT) for low RAM and fast startup.
- **Settings UI** (Avalonia): a separate process launched from the tray and exited
  when closed, so the UI framework never sits in memory. Avalonia also runs on Linux.
- Rust was considered. Revisit it if the NativeAOT agent misses the D7 budget.

### D3 — Phone<->PC link: Bluetooth RFCOMM first, LAN second (proposed, validate in Phase 0)
- Works **offline**: no Wi-Fi, no hotspot, no internet needed. The phone and PC
  only need to be Bluetooth-paired with each other once.
- Control messages are tiny, so they shouldn't take Bluetooth bandwidth from the audio stream.
- LAN (TCP over Wi-Fi or the phone's hotspot) used opportunistically when available.
- An internet relay is **not** planned (it needs a server, costs money and leaks
  usage metadata); revisit only if both local transports prove unreliable.

### D4 — Coordinated hand-off, not stealing (confirmed direction)
The losing device pauses media and disconnects the headset *before* the winning device
connects. Single-point headsets cannot be paged by a second host while connected, so
this is the main source of speed over PodSwitch.

### D5 — One shared rules engine spec, two implementations (proposed)
Pure `decide(event, rules, state) -> actions` in Kotlin and C#, driven by the **same**
JSON test vectors in `protocol/`.

## Owner answers (2026-10-22, round 2)

### D6 — Naming and repo (confirmed)
- App name: **StinePods**. GitHub: `austinedearan-stack/stinepods`.
- Android package ID is **provisional** (`com.stinepods.app`); revisit before any Play Store release.
- **No commits or pushes until the owner says so.**
- **Clean-room:** PodSwitch is studied only for approach; no code is copied.

### D7 — Resource budget (confirmed requirement, targets proposed)
Must not slow down low-end phones or laptops.

| Component | Idle RAM | Idle CPU | Other |
|---|---|---|---|
| Desktop agent | ≤ 25 MB | ~0 % (event-driven, no polling) | No admin, single native exe |
| Desktop settings UI | only while open | — | Separate process |
| Android service | ≤ 30 MB | ~0 % | No wakelocks; UI not loaded unless opened; small APK |

Verified with real measurements at the end of every phase.

### D8 — Behaviour settings (confirmed)
- **Ask vs Auto is set per app** (e.g. Spotify = Auto, YouTube = Ask).
- **Phone calls taking the headset is a user setting**, not automatic.
- Users pair their phone with their PC over Bluetooth once (needed for the offline link, D3).

### D9 — Dev environment (confirmed)
- Repo lives in `C:\dev\stinepods` during development (outside OneDrive).
- Android: command-line SDK at `C:\Android\Sdk` + JDK 17 (no Android Studio, to save
  disk/RAM; it can be added later).
- First test hardware: Infinix Hot 20i (Android 12) + ABJ headphones. Must still be
  built for all phones and headphones.
- **Scope for now (owner, 2026-10-22):** make it work perfectly on the owner's phone
  (Infinix Hot 20i, Android 12) and laptop (Windows 10) first; other Android/Windows versions later.

### D10 — Switch mechanics (proposed, from spikes S1/S2)
- **Android release:** hidden `BluetoothHeadset.disconnect` + `BluetoothA2dp.disconnect` (both profiles).
  **Android take:** hidden `BluetoothA2dp.connect` (+ HFP). No Shizuku needed on Android 12.
- **Windows release:** `IOCTL_BTH_DISCONNECT_DEVICE` (link down in ~0.1 s, no admin).
  **Windows take:** `ONESHOT_RECONNECT` on the stereo endpoint's KS filter.
- The taking side waits for the releasing side's "link down" confirmation before connecting.

### D12 — Pairing & message authentication (confirmed, 2026-10-22)
First connection: ECDH P-256 key exchange; both devices show a 6-digit code derived from the exchange and the user
confirms on both (protects against a man-in-the-middle). The resulting long-term key is stored on both sides
(Android: app-private storage wrapped by an Android Keystore key; Windows: DPAPI). Every later message carries a
sequence number and an HMAC-SHA256 tag under a per-connection key. The Bluetooth link itself is already encrypted
(bonded RFCOMM). Message content (play state, headset address) is not secret, so authentication is the goal.

### D13 — When playback stops (confirmed, 2026-10-22)
User setting `returnOnStop`: **`stay` (default)**: the headset stays where it is; `return`: after the media has been
idle a while, hand the headset back to the device it was taken from, and resume what was paused there.

### D14 — Preferred device when idle (confirmed, 2026-10-22)
**Phone is preferred.** If the PC picks up the headset on its own (e.g. headset just powered on) while nothing is
playing on the PC and the phone link is up, the PC releases it and tells the phone to take it. Setting:
`prefer = phone (default) | pc | none`.

### D15 — Calls (confirmed earlier, defaults proposed)
- `callsTakeHeadset` (default **off**): an incoming/outgoing phone call pulls the headset to the phone.
- During an active phone call the phone refuses to give the headset away (proposed default, always on).

### D11 — Speaker blip (confirmed, 2026-10-22)
When playback starts on a device that doesn't own the headset, the first ~0.1–0.4 s may come out of that device's
own speaker before it is paused. **Default: accept the blip.** Possible later option: a per-device setting to mute the
local speaker while the headset is away.
