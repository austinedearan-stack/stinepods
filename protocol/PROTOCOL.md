# StinePods link protocol v1

The phone and PC talk over a **Bluetooth RFCOMM** link (works offline). The PC is the server; the phone connects and
keeps the link open, reconnecting with backoff when it drops.

- Service UUID: `8198aff7-3480-44df-af7f-bf9bd001588c`
- Bluetooth addresses are written `AA:BB:CC:DD:EE:FF` (upper case).

## Framing

```
u16  length        big-endian, bytes that follow (max 4096)
u8   kind          0 = plain (handshake only), 1 = authenticated
u64  seq           big-endian; authenticated frames only
u8[16] tag         HMAC-SHA256(connKey, dir || seq || body) truncated to 16 bytes; authenticated frames only
u8[] body          UTF-8 JSON object with a "t" (type) field
```

`dir` is the single byte `P` for phone→PC frames and `C` for PC→phone frames. Each side starts its own `seq` at 1 per
connection and the receiver drops any authenticated frame whose seq is not greater than the last one accepted, whose
tag does not verify, or that arrives before the handshake finished. Unknown `t` values are ignored (forward
compatibility); unknown fields are ignored.

## Handshake

1. Both sides send `{"t":"hello","v":1,"id":"<device uuid>","name":"<display name>","nonce":"<base64, 16 random bytes>"}` (plain).
2. If both hold a long-term key for the other's `id`, they derive
   `connKey = HMAC-SHA256(longKey, "stinepods-conn-v1" || phoneNonce || pcNonce)` and the phone sends `{"t":"ready"}`
   (authenticated). The PC replies `{"t":"ready"}`. Done.
3. Otherwise they pair:
   - each sends `{"t":"pair","pub":"<base64 uncompressed P-256 point>"}` (plain);
   - `shared = ECDH(own private, peer public)` (32-byte x coordinate);
   - `code = (first 4 bytes of SHA-256("stinepods-pair-v1" || shared || phonePub || pcPub) as u32 big-endian) mod 1 000 000`,
     shown as 6 digits on both devices;
   - when the user confirms on a device it sends `{"t":"pair-ok"}`, or `{"t":"pair-no"}` to reject (plain);
   - when both confirmed: `longKey = HMAC-SHA256(shared, "stinepods-key-v1")`, stored against the peer `id`; then step 2.
   A mismatched code (man-in-the-middle) means the user rejects. Nothing secret is ever sent in plain frames.

## Messages (authenticated)

| `t` | fields | sent by | meaning |
|---|---|---|---|
| `config` | `headsets:[addr]`, `prefer:"phone"\|"pc"\|"none"`, `returnOnStop:"stay"\|"return"` | phone | shared settings; the phone owns them |
| `state` | `playing:bool`, `here:[addr]` | both | sender's playback state and which headsets are connected to it |
| `claim` | `h:addr`, `id:int` | both | "I want this headset" (new playback started here) |
| `released` | `h:addr`, `id:int?`, `why:"claim"\|"prefer"\|"return"` | both | "the headset's link to me is down; take it" |
| `deny` | `h:addr`, `id:int`, `why:"call"` | phone | claim refused (e.g. phone call in progress) |
| `ping` / `pong` | — | both | liveness, at most once per 60 s of silence |

## Hand-off sequence (phone takes the headset from the PC)

```
phone: playback starts (app A) ──► pause A ──► claim{h,id}
pc:                                 pause its media ──► release h ──► (link down) ──► released{h,id}
phone: take h ──► (A2DP connected) ──► resume A
```

If no `released` arrives within 2.5 s, or the link is down, the claimer takes the headset on its own (PodSwitch-style
fallback).

## Coordinator behaviour

The same rules engine runs on both sides; see [vectors/](vectors/) for the executable specification shared by the
C# and Kotlin test suites.
