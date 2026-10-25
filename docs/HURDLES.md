# Known hurdles

Ordered roughly by risk. Update as spikes produce data.

1. **Android A2DP connect is restricted.** `BluetoothA2dp.connect()` and
   `BluetoothDevice.connect()` are system-only APIs (@SystemApi, need `BLUETOOTH_PRIVILEGED`)
   on recent Android, and enforcement varies by version and OEM. Plan: Shizuku (shell identity) or root,
   with a no-privilege fallback. Must be proven in spike S1 on each Android version.
2. **Shizuku without root must be restarted after every reboot** via Wireless Debugging,
   and Wireless Debugging needs a Wi-Fi connection, which mobile-data users may not have.
   Root avoids this. Mitigation: clear "Shizuku stopped" warning + guided restart.
3. **"Instant" has a physical floor.** Classic Bluetooth paging + link setup + A2DP stream
   setup is typically ~0.5–2 s depending on the headset. We hide it with pause → switch →
   resume, but cheap headsets vary a lot.
4. **Windows has no public connect API.** Use the audio driver's (KS) `ONESHOT_RECONNECT/DISCONNECT`
   commands (undocumented). The fallback is the service toggle (`BluetoothSetServiceState`), which is slow. The default-speaker switch
   also uses an undocumented interface (`IPolicyConfig`). Windows updates may break these.
5. **OEM background killers** (Tecno, Infinix, Xiaomi, Oppo, Vivo, Samsung): needs a foreground
   service of type `connectedDevice`, battery-optimisation exemption and setup guides per brand.
6. **Headsets switching into low-quality call mode (HFP)** when an app uses the mic (Teams/Discord/browser). Needs a rule.
7. **Detection gaps.** Apps that don't publish media sessions can't be paused remotely.
   Notification sounds/short clips must be filtered (minimum play duration).
8. **Two devices fighting.** Simultaneous playback and echoes of our own pause events. Solved by
   the ownership protocol and cooldown.
9. **Cross-platform device identity.** Match headsets by Bluetooth address, never by name.
   Each headset must be paired to both phone and PC.
10. **Cheap headset quirks.** Some auto-reconnect to the last host after a disconnect and
    race us; some take seconds to accept a new host. Needs a per-model timing database.
11. **Distribution.** Unsigned Windows exe triggers SmartScreen; Play Store will scrutinise
    Notification Listener use and Shizuku/hidden-API use.
12. **Repo in OneDrive.** Gradle/.NET build output and `.git` inside a synced folder cause
    file-lock errors and sync churn. Development happens in `C:\dev\stinepods`; it moves to
    `Documents\GitHub\stinepods` (OneDrive) only when it's time to push.
13. **Low-end hardware budget.** Users run cheap phones (e.g. Infinix Hot 20i, 4 GB RAM,
    XOS) and light laptops. Every component must be event-driven (no polling) and
    stay inside the resource budget in [DECISIONS.md](DECISIONS.md) (D7).
14. **Always-on phone<->PC Bluetooth link costs battery.** A persistent RFCOMM link
    should sit in Bluetooth's low-power sniff mode; measure its cost in spike S5. If too high, connect on
    demand instead (adds latency to each switch).
15. **Don't tune for the test hardware.** First tests use an Infinix Hot 20i (Android 12) +
    ABJ headphones; timings and workarounds must stay configurable, not hard-coded to them.
