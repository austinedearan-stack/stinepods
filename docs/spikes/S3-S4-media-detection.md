# Spikes S3 + S4 — detecting playback and controlling media

**Date:** 2026-10-22 · Laptop: Windows 10, Brave + Spotify desktop · Phone: Infinix Hot 20i, Spotify
**Tools:** `spikes/windows-s3` (GSMTC), `spikes/android-s1` ops `media|pause|play` (MediaSessionManager + notification listener)

## S3 — Windows (GSMTC, the system media sessions)

| App | Seen as | Play/pause events | Remote pause | Remote play |
|---|---|---|---|---|
| Brave (YouTube tab) | `brave.exe` | ✅ every change | ✅ confirmed in 35–56 ms | ✅ 39 ms (543 ms first, cold call) |
| Spotify desktop | `Spotify.exe` | ✅ | not tested | not tested |

- Events arrive in pairs (duplicates), so they must be de-duplicated.
- No admin, no special permission needed.
- Not yet checked: apps that don't publish a media session (some games, older players). The fallback would be WASAPI
  audio sessions (detects sound, can't pause).

## S4 — Android (MediaSessionManager with notification-listener access)

- Access granted via `adb shell cmd notification allow_listener <pkg>/<Listener>`. The real app asks the user once
  (Settings → Notification access). The listener reads **no** notifications.

| App | Seen as | Events | Remote pause | Remote play |
|---|---|---|---|---|
| Spotify | `com.spotify.music` | ✅ | ✅ PAUSED confirmed in 0.38–0.43 s | ✅ PLAYING confirmed in 0.15–0.58 s |

- `AudioManager.AudioPlaybackCallback` (any app's audio output, no package name) fires **~60–110 ms before**
  the media session reports PLAYING/PAUSED. Use it as the fast trigger and the session to identify the app.
- Spotify spams dozens of duplicate PAUSED updates per second (position updates): de-duplicate by state.
- XOS kept the spike's process alive in the background during the test. The real app still needs a foreground service.

## Design consequence: the "speaker blip"
When the headset is on the other device and the user presses play, the audio starts on **this device's own speaker**
for ~0.1–0.4 s before we can pause it. Options (owner decision):
1. Accept the short blip.
2. Mute this device's media volume while it does not own the headset, and restore it on take. Risk: confusing when the
   user *wants* the speaker. This would be a per-device setting.

## Hand-off budget (from S1–S4)
Detect ~0.05–0.1 s → pause new playback ~0.05–0.4 s → message the other side → other side pauses + releases
~0.1–0.4 s → this side takes ~0.7–1.5 s → resume ~0.15–0.6 s. **Expected total ≈ 1.5–2.5 s, audio resumes
where it was paused.**
