# Architecture & implementation plan

## Architecture

```
 ┌──────────────── Android ─────────────────┐   RFCOMM (offline) /    ┌──────────────── Desktop ─────────────────┐
 │ Playback detector (MediaSession + audio) │   LAN (Wi-Fi/hotspot)   │ Playback detector (GSMTC + WASAPI)       │
 │ BT controller (A2DP connect/disconnect,  │◄──── encrypted, ───────►│ BT controller (KS reconnect/disconnect)  │
 │   via Shizuku/root or public fallback)   │      paired once        │ Default-output switcher (IPolicyConfig)  │
 │ Media control (pause/resume)             │                         │ Media control (pause/resume)             │
 │ Rules engine + ownership coordinator     │                         │ Rules engine + ownership coordinator     │
 └──────────────────────────────────────────┘                         └──────────────────────────────────────────┘
```

### Hand-off sequence (phone takes headset from PC)
1. Phone detects playback start that matches the user's rules.
2. Phone pauses the new playback immediately (no lost audio).
3. Phone → PC: `CLAIM(headset, seq)`.
4. PC pauses its media, disconnects the headset, → phone: `RELEASED(headset, seq)`.
5. Phone connects the headset, verifies the audio route, resumes playback.
6. If the PC does not answer in time (link down), phone falls back to a stand-alone
   attempt (PodSwitch-style).

Ownership: exactly one device owns each headset at a time. Every event records the device
that caused it so we ignore echoes of our own commands; there is a cooldown after each switch.

## Phases

| # | Phase | Exit criteria |
|---|---|---|
| 0 | **Feasibility spikes** (see below) | Measured timings on real hardware for each spike |
| 1 | Protocol + pairing (`protocol/`) | Phone and PC pair via QR, exchange authenticated messages over RFCOMM and LAN |
| 2 | Desktop agent (tray) | Stand-alone connect/release/default-output works on Win10 + Win11 |
| 3 | Android agent (foreground service, QS tile) | Stand-alone connect/release works on several OEMs |
| 4 | Coordinator + rules engine | End-to-end hand-off, no back-and-forth switching, offline |
| 5 | Rules UI + Ask/Auto toggle | User-defined rules on both sides |
| 6 | Hardening, signing, updater, OEM battery guides | Friends can install and run it daily |
| 7 | Play Store prep | Policy review, permission declarations |
| 8 | Linux desktop | BlueZ + MPRIS backend |

## Phase 0 spikes

| Spike | Question it answers |
|---|---|
| S1 Android connect | Can we connect/disconnect A2DP on Android 12–17: (a) without privilege, (b) via Shizuku, (c) via root? How fast? |
| S2 Windows connect | How fast are the audio-driver (KS) one-shot reconnect/disconnect commands vs PodSwitch's service toggle, on Win10 and Win11, across adapters? |
| S3 Windows media | Does GSMTC see/pause browser video, Spotify, VLC? Coverage gaps? |
| S4 Android media | MediaSession detection + pause via Notification Listener; latency of playback callbacks |
| S5 RFCOMM link | Phone<->PC RFCOMM round-trip latency, reconnect behaviour, does it disturb A2DP audio? |
| S6 Hand-off timing | Full manual hand-off (disconnect on A → connect on B) timing per headset model |
