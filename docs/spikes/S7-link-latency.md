# S7: Link latency after idle (Bluetooth sniff)

**Date:** 2026-10-22 · Real apps (Windows agent ↔ Android service), RFCOMM link, phone screen mostly off.

## Symptom
In the first real hand-off, the PC's `claim` took ~0.55 s to reach the phone and the phone's `released` ~1.3 s to
reach the PC, against ~0.05 s during pairing. The claimer's 2.5 s timeout fired before `released` arrived.

## Measurement (PC pings the phone after an idle gap, then once more immediately)

| Idle gap | Round trip (two rounds) | Awake round trip right after |
|---|---|---|
| 1 s | 58, 53 ms | 111, 52 ms |
| 2 s | 22, 52 ms | 21, 46 ms |
| 3 s | 67, 34 ms | 50, 56 ms |
| 5 s | 41, 131 ms | 25, 32 ms |
| 8 s | **761**, 32 ms | 30, 49 ms |
| 13 s | **854, 998** ms | 76, 40 ms |
| 20 s | **1161, 1209** ms | 9, 17 ms |
| 30 s | **1207, 676**, 2183, 1019 ms | 9–51 ms |

The link falls asleep after **~5–8 s** of silence (consistent with Bluetooth sniff mode, possibly plus phone CPU
suspend). The first message after that takes **~0.7–2.2 s**.

## Decision
- While media plays on a device, that device sends a keep-alive ping every **4 s**. Sniff is per link, so this
  keeps both ends awake exactly when a switch is likely.
- When playback is about to start (PC: GSMTC says Playing, before the audible check; phone: first media player
  appears, ~100 ms before the session's PLAYING), send an immediate wake-up ping.
- Nothing is sent while nothing plays: no idle battery cost.
- **To measure:** the battery cost on the idle device while the other one plays (Phase M4).
