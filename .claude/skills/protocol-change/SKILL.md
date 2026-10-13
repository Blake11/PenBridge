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
- Update the message/sample types and the hand-written serializer (`BinaryPrimitives`, explicit little-endian, `Span<byte>`); no reflection, no allocations on the input path.
- Update the protocol version constant(s) and the TXT record version.
- Readers: validate lengths before reading, reject unknown message types on the control channel (or skip if marked optional), never trust counts from the wire (cap batch sizes).
- Input datagrams: keep full-state-per-packet semantics, sequence numbers, AES-GCM AAD covering the header.
- Update `docs/architecture.md` protocol section.

## 3. Tests (`tests/PenBridge.Core.Tests`)
- Round-trip each changed message.
- **Golden bytes**: fixed byte arrays for each message version so accidental layout changes fail.
- Compatibility: new reader ← old bytes, old-shaped reader ← new bytes (minor), major mismatch → refusal path.
- Fuzz-ish: truncated/oversized/garbage input never throws unhandled or over-reads.
- Allocation check for the hot path (`GC.GetAllocatedBytesForCurrentThread` delta == 0) where applicable.

## 4. Both sides
- Mac server (`src/PenBridge.Mac`): handle new messages, version refusal UI.
- Android (`src/PenBridge.Android`): send/handle, refusal UI.
- `tools/PenBridge.SimClient`: update so it still pairs and streams.
- iOS placeholder: build only.

## 5. Verify
`scripts/test.sh`; SimClient ↔ Mac server; S23 ↔ Mac for input-path changes. PR notes state the version change and compatibility.
