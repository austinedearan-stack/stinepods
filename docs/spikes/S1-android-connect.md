# Spike S1 — Android A2DP/HFP connect & release

**Date:** 2026-10-22 · **Phone:** Infinix Hot 20i (X665E), Android 12 / API 31, XOS 10.6, not rooted, 4 GB RAM
**Headset:** ABJ-TW3 (single-point, SBC/AAC) · **App:** `spikes/android-s1`, targetSdk 36, normal app, **no Shizuku/root**
**Transport for testing:** adb over Wireless debugging

## Results

| Operation | Works without privilege? | Timing |
|---|---|---|
| `BluetoothA2dp.getActiveDevice()` (reflection) | ✅ | — |
| `BluetoothA2dp.connect()` (reflection), headset idle | ✅ | A2DP CONNECTED **453 ms**, active output 476 ms |
| `BluetoothA2dp.connect()` while HFP link still up | ✅ | 206–407 ms |
| `BluetoothA2dp.disconnect()` (reflection) | ✅ | 216–589 ms, **but the HFP link stays up** |
| Full release: `BluetoothHeadset.disconnect()` + `BluetoothA2dp.disconnect()` | ✅ | Bluetooth link fully dropped (`ACL_DISCONNECTED`) at **399–560 ms** |
| Full take: A2DP + HFP `connect()`, headset idle | ✅ | A2DP CONNECTED **640 ms** (audio usable), HFP 1053 ms |
| `setConnectionPolicy`, `fetchUuidsWithSdp` poke | not tested yet (not needed on API 31) | |

## Findings

1. On Android 12 the hidden A2DP/HFP connect/disconnect calls work from a normal app with
   only `BLUETOOTH_CONNECT`. **Shizuku is not needed on this phone.** Android 13–17 still
   need testing; restrictions tightened there (`@SystemApi`, `BLUETOOTH_PRIVILEGED`).
2. **Releasing requires disconnecting HFP too.** A2DP-only disconnect leaves the
   Bluetooth link up and the single-point headset stays tied to the phone.
3. **Windows auto-reconnects to the headset within seconds of the phone releasing it.** That
   made the phone's next connect stall at CONNECTING. The coordinator must stop
   Windows auto-reconnecting while the phone owns the headset. (Feeds spike S2.)
4. (Corrected in S2: Windows does have both "Headphones (ABJ-TW3 Stereo)" and "Headset (ABJ-TW3
   Hands-Free AG Audio)" endpoints; an earlier query filter missed the stereo one.)
5. Phone-side switch budget is ~0.4–0.6 s release + ~0.65 s take. Most of the remaining
   switch time will be on the Windows side and in coordination.

## Next

- Repeat on Android 13, 14, 15, 16, 17 phones (friends' devices); add Shizuku path where blocked.
- S2: Windows connect/release speed and stopping its auto-reconnect.
