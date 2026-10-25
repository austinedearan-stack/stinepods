# Spike S2 — Windows connect/release + hand-off timing

**Date:** 2026-10-22 · **PC:** Windows 10 (19045), Realtek Bluetooth 4.0 adapter, **not elevated**
**Phone:** Infinix Hot 20i (S1 spike app) · **Headset:** ABJ-TW3 · **Tool:** `spikes/windows-s2` (.NET 10 console)

## Windows building blocks

| Mechanism | Admin? | Result |
|---|---|---|
| `KSPROPSETID_BtAudio` / `ONESHOT_DISCONNECT` via the stereo endpoint's KS filter (`IKsControl`) | No | Stereo endpoint unplugged ~0.08–0.25 s |
| Same, via the hands-free endpoint | No | Required as well: stereo-only leaves the HFP link up (same as Android) |
| Both one-shot disconnects → wait for link drop | No | Link (ACL) down after **~1.25 s** (Windows holds the idle link) |
| `IOCTL_BTH_DISCONNECT_DEVICE` on the radio handle | **No** | Link down in **~0.09–0.12 s**; audio endpoints follow; no bounce-back seen |
| `ONESHOT_RECONNECT` via the stereo endpoint | No | Stereo ACTIVE in ~0.8–1.0 s **or** ~3.5–4 s (two clusters, see below) |
| Default output | — | Windows switched the default output to the headset by itself on every connect; `IPolicyConfig` not needed so far |

Notes:
- Windows can re-create a headset's audio endpoint under a **new endpoint ID** on reconnect, so track endpoints by
  the KS filter / Bluetooth address, never by endpoint ID.
- Stale `NOTPRESENT` duplicate endpoints exist and must be ignored.
- Reading endpoint properties can fail mid-switch (`0xE000020B`), so it must be tolerated.
- First use of WinRT `BluetoothDevice` / COM took seconds in a cold process, so the agent must keep these loaded.

## Hand-off timings (5 rounds, PC release = IOCTL, phone release = HFP+A2DP disconnect)

| Round | PC → phone: PC release | phone take (A2DP connected) | phone → PC: phone release | PC take (stereo ACTIVE) |
|---|---|---|---|---|
| 1 | 0.10 s | 0.83 s | 0.30 s | 3.72 s |
| 2 | 0.09 s | 0.99 s | 0.34 s | 1.02 s |
| 3 | 0.09 s | 0.67 s | 0.34 s | 0.87 s |
| 4 | 0.08 s | 0.86 s | 0.30 s | 0.82 s |
| 5 | 0.12 s | 0.92 s | 0.38 s | 4.00 s |

- **PC → phone: 0.75–1.1 s total.** Consistent.
- **Phone → PC: 1.1–1.4 s** in 3/5 rounds, **~4 s** in 2/5. The PC-side connect has two clusters, suggesting the first
  connect attempt fails and Windows retries after ~3 s. **Open item for Phase 2.**
- Tool and coordination overhead (adb, process start) is not included. The real agent stays warm, and the link adds
  one message round trip.

## Other findings
- Starting the phone's connect *before* the PC link is down ("eager take") did **not** help. Paging collides with
  the busy headset. Wait for the release confirmation.
- Earlier (S1) the PC auto-grabbed the headset after the phone released it during rapid cycles. It did not recur in S2
  rounds, but the agent must still handle an unwanted grab (release it if the phone owns the headset).

## Follow-up: why some PC connects take 3–4 s (2026-10-22)

The connect's link setup (ACL) is the variable part; stereo always follows ~0.3–0.7 s after the link.

| Experiment | PC take (stereo ACTIVE) |
|---|---|
| Release → wait 0.5 s → take | 0.92–1.09 s |
| Release → wait 1–3 s → take | 0.82–1.96 s, one 3.8 s |
| Release → wait 10 s → take | 0.97–4.3 s (the headset's page-scan slows down when idle) |
| `take-sdp` (uncached SDP query in parallel with `ONESHOT_RECONNECT`), 0.5–3 s gap | 0.72–1.3 s |
| Graceful vs abrupt prior release | no difference |
| **Wi-Fi quiet** | **0.86–1.55 s** (one outlier 4.8 s) |
| **Wi-Fi busy (download)** | **2.15–3.0 s, one 10 s** |

**Cause:** the laptop's **Realtek RTL8723BE** is a 2.4 GHz-only Wi-Fi + Bluetooth 4.0 combo chip (Wi-Fi on channel 1).
When Wi-Fi is busy, Bluetooth gets less airtime for making connections. The phone was consistently 0.5–1.1 s.

**Mitigations for the agent:**
- Fire the SDP query and `ONESHOT_RECONNECT` together; retry with a short timeout instead of one long attempt.
- Pause PC media *before* taking the headset (less Wi-Fi streaming during the switch).
- Show "switching…" feedback. Laptops with 5 GHz or separate BT radios should be much better (verify on Win11 laptops).

**Robustness bugs seen (the agent must handle them):**
- A WinRT/COM call hung for 20+ min (the process ignored its own timeout). All native Bluetooth/audio calls need
  watchdog timeouts, ideally off the main thread.
- `IMMDeviceEnumerator.GetDevice` can fail with `0xE0000225` (device gone) mid-switch, and property reads with
  `0xE000020B`. Re-resolve and retry.

## Next
- S3/S4: detect playback and pause/resume media on each side.
- S5: phone<->PC RFCOMM link latency and its effect on audio.
