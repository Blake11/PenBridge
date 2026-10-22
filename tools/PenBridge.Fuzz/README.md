# PenBridge.Fuzz

Coverage-guided fuzzing of the protocol parsers that read untrusted bytes (#262). It runs locally and on demand.
CI doesn't run it. `tests/PenBridge.Core.Tests/Protocol/FuzzTests.cs` keeps the fast, blind, fixed-seed checks in
the normal test run. Inputs that fail here become `type: bug` issues, and their minimized bytes go in
`tests/PenBridge.Core.Tests/Protocol/FuzzRegressionTests.cs`.

## How it works

- [SharpFuzz](https://github.com/Metalnem/sharpfuzz) rewrites the harness's **copy** of `PenBridge.Core.dll`
  (in `bin/Release/net10.0/`) so that every branch updates an AFL-style 64 KiB edge map. `src/` and the test
  build are not changed.
- `penbridge-fuzz fuzz` is a small AFL-style engine that runs in the same process (`InProcessFuzzer.cs`). It owns the
  map, keeps each input that reaches a new edge or a new hit-count bucket, and mutates the corpus with havoc
  operators, a per-target dictionary, splicing and length-prefix fixing. Exec rates on an M-series Mac are about
  100k/s (control) to 350k/s (payload).
- A finding is any exception that escapes a target, or one execution that takes longer than `--hang-seconds`
  (default 2 s). After a hang the input is saved and the process exits with code 2.

On macOS this in-process engine is the only option. The native fuzzers don't work here:

| Fuzzer | macOS (arm64) | Linux |
|---|---|---|
| libFuzzer + [libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet) | **No.** The driver feeds .NET coverage to libFuzzer through `__libfuzzer_extra_counters`. libFuzzer only supports that section on Linux/BSD/Fuchsia, Apple clang ships no libFuzzer runtime, and there is no macOS release of the driver. | Yes: `penbridge-fuzz libfuzzer <target>` is the child process |
| AFL++ (`afl-fuzz`) | Not tested. It needs `brew install afl++`, and AFL++'s macOS setup (`afl-system-config`) uses sudo. | Yes: `penbridge-fuzz afl <target>` is the child process |

## Targets

| Target | What it feeds | Contract checked |
|---|---|---|
| `control` | control-channel bytes: `ControlFrame.TryRead` in a loop, every `ControlMessageType` parser, then `ControlFrameReader` + `ReceivedControlFrame.Read<T>` over the same bytes. Parsed settings are applied the way each side does it: `MacSettingValues.FromEntries`/`ApplyUpdate`/`TryApply` and `InputSettings.Validate` | only `ProtocolException` (with a defined code) escapes the frame layer; `TryRead` never throws; consumed/payload sizes add up; whatever is accepted re-encodes, and decoding that gives the same bytes; applied settings stay valid |
| `payload` | the plaintext input payload after decryption (`InputPayload.TryRead`, also used by `InputFrame`) | never throws; caps hold; samples valid; the round trip is stable |
| `datagram` | the UDP datagram before decryption: `InputDatagramHeader.TryRead`, `InputDatagram.TrySplit`, `InputCipher.TryOpen` with a key the sender doesn't have. Then the same bytes as the plaintext a paired phone sealed: seal, split, open, parse | slices are consistent; nothing forged opens; sealed datagrams open to the same plaintext |
| `qr` | untrusted text: `PairingQrPayload.TryParse` (the scanned QR), `BonjourTxtRecord.TryParse` (the input split on `\n` into version, fingerprint and name), `ProtocolVersion.TryParse` | never throws; `Format()`/`ToEntries()` output parses back to the same values; `ToString()` doesn't print the token |

Seeds are valid messages written by the protocol writers (`Seeds.cs`): every message type, settings snapshot,
update and display list, payloads from empty to full, sealed datagrams, and QR/TXT texts. `seeds <target> <dir>`
writes them out.

## Run

No install is needed beyond the .NET SDK. SharpFuzz is a NuGet package, and its `Fuzzer.Instrument` API does the
rewrite, so you don't need the `sharpfuzz` CLI tool.

```bash
tools/PenBridge.Fuzz/fuzz.sh                                   # build + instrument only
tools/PenBridge.Fuzz/fuzz.sh fuzz control --minutes 15         # corpus/findings in artifacts/fuzz/control/
tools/PenBridge.Fuzz/fuzz.sh fuzz qr --minutes 15 --seed 262   # fixed mutation seed
tools/PenBridge.Fuzz/fuzz.sh repro control artifacts/fuzz/control/findings/crash-…
tools/PenBridge.Fuzz/fuzz.sh minimize control artifacts/fuzz/control/findings/crash-…   # writes ….min, prints hex
```

Each target runs on one thread. Run the targets as separate processes to use more cores. A rerun with the same
`--corpus` directory continues from that corpus. `--seed` makes the mutations repeatable, but a run isn't fully
deterministic because the stop time is set in minutes.

`fuzz.sh` always rebuilds, and the build copies a fresh `PenBridge.Core.dll` over the instrumented one whenever
Core changed, so the instrumentation never goes stale. Don't run the instrumented `bin/Release` output for
anything else: instrumented code without a trace map crashes.

### Linux, optional (libFuzzer)

```bash
git clone https://github.com/Metalnem/libfuzzer-dotnet && clang -fsanitize=fuzzer libfuzzer-dotnet/libfuzzer-dotnet.cc -o libfuzzer-dotnet
tools/PenBridge.Fuzz/fuzz.sh   # instrument
./libfuzzer-dotnet --target_path=tools/PenBridge.Fuzz/bin/Release/net10.0/penbridge-fuzz --target_arg="libfuzzer control" -max_total_time=900 corpus/
```
This was not run on the dev Mac (see the table above). A `--target_arg` with spaces is split into arguments.

## After a finding

1. `minimize` the input, then `repro` it to confirm.
2. File a `type: bug` issue (skill `issues`) with the signature and the minimized hex.
3. Add the hex to `FuzzRegressionTests.cs` as a test with `Skip = "#<issue>"`. The fix PR removes the skip.

## Campaign log

Each target ran for 13 minutes on an M-series Mac mini, with `--seed 262` and the four targets in parallel.

| Date | Target | Execs | Exec/s | Corpus | Edges | Findings |
|---|---|---|---|---|---|---|
| 2026-10-22 | control | 41.4 M | 53k (90k at start; it slows as inputs grow) | 29 → 1175 | 1414 → 1693 | 0 |
| 2026-10-22 | payload | 251 M | 322k | 4 → 43 | 152 → 175 | 0 |
| 2026-10-22 | datagram | 131 M | 168k | 8 → 50 | 207 → 232 | 0 |
| 2026-10-22 | qr | 194 M | 249k | 5 → 98 | 218 → 287 | 0 |

Edges stopped growing within about 2 minutes on every target: the parsers are small, and the seeds already cover
the valid paths. The only report was a writer/reader asymmetry. A SettingsUpdate whose entries all fail to decode
parses to zero entries, as documented, and re-encoding it writes count 0, which the reader rejects. Nothing
re-sends a parsed update, so it is pinned in `FuzzRegressionTests` and is not filed as a bug.
