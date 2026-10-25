# Spikes S5 + S6 — offline phone↔PC link, and scripted end-to-end hand-off

**Date:** 2026-10-22 · Laptop: Windows 10, Realtek RTL8723BE (Wi-Fi + BT 4.0 combo, 2.4 GHz) · Phone: Infinix Hot 20i
**Tools:** `spikes/windows-s5` (WinRT RFCOMM server), `spikes/android-s1` op `link` (RFCOMM client), `spikes/handoff.sh`

## S5 — Bluetooth RFCOMM control link (works fully offline)

Phone and laptop are bonded (laptop lists the phone as "Stine"). PC advertises a spike RFCOMM service and the phone
connects with `createRfcommSocketToServiceRecord` and pings it.

| Condition | Connect | RTT median | p95 | max |
|---|---|---|---|---|
| Earbuds idle on laptop | 3.44 s (cold) | 49 ms | 134 ms | 683 ms |
| Earbuds on laptop, Spotify playing | 3.95 s (cold) | 59 ms | 163 ms | 389 ms |
| Earbuds on phone, Spotify playing | 0.63 s (warm) | 52 ms | 181 ms | 435 ms |
| Earbuds on phone, YouTube playing | 0.95 s | 51 ms | 195 ms | 219 ms |

- **Owner listening test: no noticeable stutter caused by the link** on either side. The laptop has small baseline
  stutters even without the link (combo chip).
- **No audio profiles were auto-connected** between phone and laptop when the link came up. Still, both have
  HFP/A2DP registered for each other, so keep watching for this.
- One-way message ≈ 25 ms typical, which is negligible next to the Bluetooth switch.
- The WinRT RFCOMM server took up to 11 s to start advertising in a cold process. The agent must start it once and keep it.

## S6 — scripted hand-off (pause → release → take → resume)

`handoff.sh` mimics the coordinator using the spike tools. adb (Wi-Fi) and per-step process start-up add roughly
0.3–1 s per step, so these totals are an **upper bound**.

| Run | Direction | Paused | Released | Connected | Resumed |
|---|---|---|---|---|---|
| 1 | phone → laptop (YouTube → Brave) | 0.58 s | 1.75 s | 3.91 s | **5.17 s** |
| 2 | laptop → phone | 0.99 s | 1.65 s | 3.59 s | ✗ (see below) |
| 3 | phone → laptop | 0.92 s | 1.82 s | 6.39 s | **8.64 s** |
| 4 | laptop → phone | 0.97 s | 2.34 s | 5.36 s | **6.77 s** |

Owner: "everything was ok, as scripted, negligible stutters."

### Findings
1. **Free YouTube refuses remote `play` when it isn't in the foreground** (state ERROR → STOPPED). In run 2 the spike's
   activity had come to the front. Fixed for testing by driving the spike through its notification-listener service
   (adb broadcast, no UI). **Real-app consequence:** a foreground-only player that isn't on screen can't be resumed.
   The coordinator must still switch the headset, report "couldn't resume", and not retry in a loop.
2. **Spotify Connect** (same account on both devices) moves playback between devices on its own, and the laptop's
   Spotify reports "Playing" while audio is actually on the phone. GSMTC "Playing" ≠ "audible here". The agent also
   needs a local audio-activity check (WASAPI session/peak meter) before acting.
3. Cheap earbuds may send AVRCP PLAY on connect, and apps may auto-resume. Every play/pause event must be tagged
   with its cause (user vs. our command vs. headset) to avoid switch loops.
4. PC connect still hits the slow case (~4.6 s) right after a video has been streaming over the combo chip's Wi-Fi.

## Phase 0 conclusion (owner's phone + laptop)
Every building block works without root, Shizuku or admin: Android connect/release, Windows fast release + connect,
media detection + control on both sides, an offline Bluetooth link, and an end-to-end hand-off. Expected real-app
switch: **~1.5–2.5 s** typical, worse on this laptop when its Wi-Fi is busy.
