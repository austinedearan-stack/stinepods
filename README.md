# StinePods

Make cheap, **single-point** Bluetooth headphones/earbuds/speakers follow your audio
between your Android phone and your Windows PC — automatically and as fast as the
hardware allows.

Inspired by [PodSwitch](https://github.com/Felip6499/PodSwitch), but written from
scratch (no code is copied from it). Unlike PodSwitch, the phone
and PC **coordinate**: the device that is losing the headphones releases them on
purpose and pauses its media, so the other device can connect immediately instead of
"stealing" the connection.

> Status: **Phase 0 (feasibility spikes) complete** on the owner's Infinix Hot 20i (Android 12) + Windows 10 laptop.
> See [docs/spikes/](docs/spikes/). No app build yet.

## Targets

| Platform | Versions | Status |
|---|---|---|
| Android | 12 – 17, many OEMs | planned |
| Windows | 10 and 11 | planned |
| Linux | — | later |

## Repo layout

```
android/    Android app (Kotlin)
desktop/    Desktop app (.NET + Avalonia; Windows now, Linux later)
protocol/   Phone<->PC protocol spec + shared test vectors
spikes/     Throwaway Phase 0 experiments
docs/       Plan, hurdles, security, decisions, open questions
```

## Docs

- [docs/PLAN.md](docs/PLAN.md) — architecture and phased implementation plan
- [docs/HURDLES.md](docs/HURDLES.md) — known technical risks
- [docs/SECURITY.md](docs/SECURITY.md) — threat model and mitigations
- [docs/DECISIONS.md](docs/DECISIONS.md) — confirmed requirements and decision log
- [docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md) — things still to decide
