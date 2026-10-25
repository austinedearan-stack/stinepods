# Security notes

| # | Risk | Mitigation |
|---|---|---|
| 1 | Anyone on the same network (or within Bluetooth range) sends fake commands (pause, steal headset) | One-time QR pairing that exchanges keys; every message authenticated + encrypted (Noise protocol or pinned-key TLS); sequence numbers block replayed messages; drop anything from unpaired peers |
| 2 | PC exposes a network port | Listen only on networks Windows marks as Private; no admin rights; strict message validation and size limits |
| 3 | LAN discovery broadcasts the device's name | Broadcast a random ID, not the user or device name |
| 4 | Notification Listener can read **all** notifications (OTPs, M-Pesa) | Optional, only for "pause other apps"; never store or send notification content; explain this in the app |
| 5 | Shizuku/root give the app elevated rights, so a bug in it does more damage | Keep the privileged code minimal and in one place; no dynamic code loading |
| 6 | Metadata leakage between devices | Send only play-state + headset address; never titles or app names unless the user enables it |
| 7 | Blueprint repo is unsigned and AI-generated | Don't run its binaries. Clean-room: study its approach only, copy no code |
| 8 | Supply chain / updates | Pin dependency versions; updater checks signatures; Android signing key stored offline and backed up (losing it blocks updates) |
| 9 | Logs reveal habits | Local only, rotated, Bluetooth addresses partially masked |
