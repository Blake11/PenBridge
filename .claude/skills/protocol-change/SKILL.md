---
name: protocol-change
description: Checklist for changing the PenBridge wire protocol — control-channel messages, UDP input sample/datagram layout, pairing/crypto handshake, Bonjour TXT fields — covering Core types, version bump rules, serialization tests, both platform heads, and SimClient. Use before adding/changing/removing any message type, field, flag, or encoding in PenBridge.Core.
---

# Protocol change checklist

Protocol lives in `src/PenBridge.Core` (ADR-0004). Mac server and phone clients ship and update independently — assume mixed versions in the wild.

## 1. Classify
- **Minor (compatible)**: new optional message type, new capability flag, appended field that old peers ignore (length-prefixed body allows skipping the tail). Bump protocol **minor**.
- **Major (breaking)**: changed field order/size/meaning, removed message, changed crypto/handshake, changed sample struct layout without a length/version guard. Bump protocol **major**; peers refuse with a clear message (both sides show "update the Mac app / phone app"). Needs an ADR (skill `adr`) and a `feat!:`/`BREAKING CHANGE:` commit.
- Prefer capability flags negotiated in hello over major bumps.

## 2. Core changes
- Update the message/sample types and the hand-written serializer (`BinaryPrimitives`, explicit **big-endian** (network order, per docs/specs/phase-1-core.md), `Span<byte>`); no reflection, no allocations on the input path.
- Update the protocol version constant(s) and the TXT record version.
- Readers: validate lengths before reading, reject unknown message types on the control channel (or skip if marked optional), never trust counts from the wire (cap batch sizes).
- Input datagrams: keep full-state-per-packet semantics, sequence numbers, AES-GCM AAD covering the header.
- Update `docs/architecture.md` protocol section.

## 3. Tests (`tests/PenBridge.Core.Tests`)
- Round-trip each changed message.
- **Golden bytes**: fixed byte arrays for each message version so accidental layout changes fail.
- Compatibility: new reader ← old bytes, old-shaped reader ← new bytes (minor), major mismatch → refusal path.
- Fuzz-ish: truncated/oversized/garbage input never throws unhandled or over-reads.
- Allocation check for the hot path with `Alloc.AssertZero` (skill `core-testing`), never a one-shot counter delta.

## Pairing security (must hold for any change to pairing, auth or the input channel)
- **Pairing window:** `PinIssuer` accepts PIN/QR attempts only between `OpenPairingWindow()` (Mac "Pair new device…") and its close (success, `ClosePairingWindow()`, or 2 min expiry). Outside it → `WindowClosed`, no strike; send `PairResult(expired)`.
- **Lockout backoff:** 3 failures → new PIN + refusal for 30 s, doubling per lockout to a 10 min cap; resets only on success (`LockedOut` → `PairResult(failed(0))`). Abandoned/aborted attempts count as failures.
- **Complete-before-Confirm:** the server must get `Ok` from `PinAttempt.Complete` before sending its PairConfirm. Use `PairingSession.ForServer(attempt, …)`; it completes the attempt inside `TryVerifyPeerConfirm`, so an expired/invalidated attempt never yields a server confirm. Send `session.AttemptResult` as PairResult. Time out pairing so the single attempt slot can't be held.
- **Nonce uniqueness:** AES-GCM nonce = `sessionId ‖ sequence` under `K_in` (per session: HKDF over fresh client+server nonces). Net must generate a fresh random `serverNonce` and a `sessionId` never reused on that server run, create one `InputCipher` per session/direction, and never reset its sequence. `InputCipher.Seal` throws on a non-increasing sequence. Never reuse `K_in` across sessions.
- **Tampered datagrams:** `TryOpen` returns false (an exception is caught internally); the Net layer pins the peer endpoint and rate-limits/counts failures.

## 4. Both sides
- Mac server (`src/PenBridge.Mac`): handle new messages, version refusal UI.
- Android (`src/PenBridge.Android`): send/handle, refusal UI.
- `tools/PenBridge.SimClient`: update so it still pairs and streams.
- iOS placeholder: build only.

## 5. Verify
`scripts/test.sh`; SimClient ↔ Mac server; S23 ↔ Mac for input-path changes. PR notes state the version change and compatibility.

## Gotchas
- Untrusted base64url (TXT `fp`, QR `fp`/`t`): go through `Base64UrlBytes.TryDecode32` (Protocol), which uses the `OperationStatus` overload of `Base64Url.DecodeFromChars`. `TryDecodeFromChars` still throws `FormatException` on malformed or non-canonical input, because "Try" only covers destination size.
- `PairingQrPayload` keeps a **reference** to the token array (`Token` is `ReadOnlyMemory<byte>` over it). Don't zero or reuse that array after building the payload, or the QR/URI carries zeros; zero it when the payload is no longer needed.
- BouncyCastle J-PAKE: a participant must create its **own** Round2 before it can validate the peer's (`PairingSession.EnsureOwnRound2` generates and caches it). Keep that ordering if you touch the round handling.
- Network-side rules (deadlines, endpoint pinning, per-IP limits, forged-datagram budget): skill `net-engine`.
- Readers ignore trailing bytes in known messages (minor-version extension point). Unknown enum values in *input samples* are rejected; unknown Error codes, Bye reasons and capability bits are accepted.
- Control-stream reads have no built-in timeout: callers pass a per-read deadline (`CancellationTokenSource.CancelAfter`) and a pre-auth handshake timeout. Reserved (`0xF0–0xFF`) frames are skipped but count toward a cap (`ControlFrameReader.MaxConsecutiveReservedFrames`), so they can't keep a dead or hostile peer "alive". Any read failure or cancellation desyncs the stream: dispose and close.

## Minor bumps (compatibility facts)
- A minor bump is safe only because every released version compares the **major** only (`ProtocolVersion.IsCompatibleWith`), accepts any `d.d` in the TXT/QR `v=`, and ignores unknown **capability** bits in Hello/HelloAck. Gate new behaviour on a capability bit, never on the minor number.
- Touch and sample **state** bits are different: decoders reject unknown state bits and drop the whole datagram. Don't add state bits without a capability (ADR-0024 reused the valid `Contact=false` state instead).
- Before relying on any of this, verify it at the oldest supported release tag (`git show <tag>:<path>`).
