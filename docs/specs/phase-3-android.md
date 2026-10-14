# Phase 3 — Android client spec

Epic #12. Scope: the `src/PenBridge.Android` head (net10.0-android, minSdk 29, target 36), a thin layer over `PenBridge.Core.Net.PenBridgeClient` and `PenBridge.Core.Input.InputFilter`. The ADRs that apply are 0006, 0008, 0009, 0012, 0013, 0016 (the Mac maps the side button to right-click), and ADR-0019 (Phase 3 UX, in this PR).

The primary device is the Samsung Galaxy S23 Ultra over wireless adb. The emulator covers the UI and the connection flow only; it has no stylus and no mDNS (use manual host `10.0.2.2`).

## Folders (one workstream each)

| Folder (`src/PenBridge.Android/…`) | Workstream | Wave |
|---|---|---|
| `Services/` | P3-A Platform services + `ClientHost` | 1 |
| `Pad/` | P3-B Drawing surface, sampler, overlay | 1 |
| `Connect/`, `MainActivity.cs`, `AndroidManifest.xml`, `Resources/`, csproj | P3-C Connect flow, navigation, composition | 2 |
| — (scripts/docs/skills only) | P3-D On-device verification on the S23 | 3 |

Wave 2 composes the wave-1 types. P3-C is the only workstream that edits the csproj and manifest; wave-1 agents report the packages and permissions they need.

## P3-A — Services
- **`AndroidKeystoreSecretStore : ISecretStore`:**
  - an AES-256-GCM key in AndroidKeyStore (alias `penbridge.secrets`, not exportable, no user auth);
  - each secret is stored as `nonce ‖ ciphertext ‖ tag` in a 0600 file under `FilesDir/secrets`;
  - fresh random nonces, and a corrupt or undecryptable entry counts as missing.
- **`MacDiscovery`:**
  - `NsdManager` browses `_penbridge._tcp`, holding a `WifiManager.MulticastLock` only while browsing;
  - it resolves the TXT record into Core's `BonjourTxtRecord` (`v`, `fp`, `name`) and exposes the list of discovered Macs (name, host, port, fingerprint, version-compatible);
  - browsing stops while the app is backgrounded.
- **`SettingsStore`** (SharedPreferences): last-used Mac (device id / fingerprint), orientation mode, synthetic finger pressure.
- **`ClientHost`:**
  - owns `PenBridgeClient` (from Core): connect (discovered, manual host:port, or QR payload), pair (PIN/QR), auth, and an observable state (Idle / Discovering / Connecting / Pairing / Connected(transport, rtt) / Error(kind, message));
  - auto-connects to the last-used paired Mac when it's discovered, and reconnects with backoff (1, 2, 4 … 30 s) while the pad is open;
  - disconnects when the app goes to the background (`onStop`) and reconnects on `onStart`;
  - implements `IPadOutput` (below) on top of `SendState`.
- **Untrusted text:** treat peer `Error` text as untrusted when shown in the UI.

## P3-B — Pad (`DrawSurfaceView` + overlay)
- **Surface:** a full-screen immersive `View` that keeps the screen on. `RequestUnbufferedDispatch(InputDevice.SourceClassPointer)` is called on the first down event.
- **Input sources:** it consumes `OnTouchEvent`, `OnHoverEvent` and `OnGenericMotionEvent`, including **all historical samples** (`HistorySize` / `GetHistorical*`). Samples go into Core's `InputFilter`, and its output is sent via `IPadOutput.SendState(in PenSample?, ReadOnlySpan<TouchSample>, ReadOnlySpan<PenSample> history)`.
- **Hot path:** no allocations per event; use reusable buffers.
- **Stylus mapping:**
  - **Tool:** `ToolType.Stylus` → Pen, `ToolType.Eraser` → Eraser.
  - **Pressure:** 0..1 → 0..65535.
  - **Tilt:**
    - Convert `Axis.Tilt` (0..π/2 from vertical) and `Axis.Orientation` (−π..π, 0 = pen pointing up) to the Core convention: tiltX > 0 when the pen top leans to +x (right), tiltY > 0 when it leans to +y (down).
    - Units are centi-degrees, clamped to ±9000.
    - Write it as a pure static function, `TiltConverter`, in Core `Input/` with tests. P3-B may add that one Core file.
  - **Rotation:** use the orientation axis.
  - **Hover:** `ActionHoverEnter`/`Move`/`Exit` → InRange without Contact.
  - **Button:** `MotionEventButtonState.StylusPrimary` → Primary (the Mac maps it to right-click).
  - **Coordinates:** x/y are normalized over the view's size in its current orientation.
- **Keepalives:** handled by `PenBridgeClient` (50 ms with contact or touches, 250 ms hover). The pad only reports state changes and samples.
- **Ink echo:** a faint trail of the last ~1 s of pen positions, fading out, rendered on a separate lightweight layer (`SurfaceView`/`TextureView`, or invalidating only the dirty rect). It must never block input dispatch.
- **Orientation toggle:** landscape-locked by default, with lock portrait and auto as options. A change applies only when no contact or touch is active, and triggers a new SurfaceInfo; the `ClientHost` API sends it.
- **Status overlay:**
  - a tiny dim dot in a corner: green = Connected (UDP), amber = Connected (TLS fallback) or reconnecting, red = disconnected;
  - tapping it, or swiping in from the edge, opens a small menu: latency, transport, orientation, Disconnect, Change Mac;
  - system Back leaves the pad, and the dot ignores pen input.
  - **Accepted deviation:** the menu opens only from the status dot (tap, or a swipe that starts on it). A swipe in from the screen edge conflicts with the system back gesture, so it isn't used.
- **Touch-size readout (#76):** an optional overlay (Settings ▸ "Show touch size on the pad") shows each finger's live normalized contact size and the last touch's peak, amber while a contact counts as a palm. It draws only, never takes input, and allocates nothing per event; it's for tuning the palm threshold.
- **Contract for P3-A:** P3-B defines `IPadOutput` and an observable `PadConnectionState` the overlay renders. P3-A implements them.

## P3-C — Connect flow + composition
- **Navigation:** single-Activity (`MainActivity`) with simple screens: **Mac list** → **Pair** (PIN entry or Scan QR) → **Pad**. Advanced ▸ Connect manually (host, port, PIN). There's also a small Settings screen: finger pressure, finger hold before drawing (`FingerFallbackArmDelay`; a second finger within it starts a gesture), palm size threshold (`PalmSizeThreshold`, or Off), the touch-size readout toggle (#76), pad orientation, paired Macs (Forget), and About (app and protocol version).
- **QR:** CameraX + Google ML Kit barcode scanning (**bundled** model, no Play Services dependency). The CAMERA permission is requested only when "Scan QR" is tapped. Only `penbridge://pair` payloads are accepted (Core parser).
- **Manifest:** INTERNET, ACCESS_NETWORK_STATE, ACCESS_WIFI_STATE, CHANGE_WIFI_MULTICAST_STATE, CAMERA (runtime). NEARBY_WIFI_DEVICES is not needed: NSD discovery works without it on API 33+. Network-security config allows no cleartext.
- **Startup:** if a last-used paired Mac is discovered within ~3 s, go straight to the Pad; otherwise show the Mac list.
- **Theme:** dark, Material 3 basics, no heavy UI framework.

## P3-D — On-device verification (S23 Ultra)
- Scripted checks over wireless adb:
  - install and launch;
  - discovery of the Mac; QR pairing and PIN pairing;
  - pressure ramp, tilt sweep, hover, side button, eraser end;
  - palm rejection with a hand on the screen;
  - two-finger scroll/pinch reaching the Mac recognizer (Phase 4 maps the actions);
  - reconnect after Wi-Fi toggles and after app background/foreground.
- **Latency measurement:** pen-down → Mac event time, using the timestamps both sides already carry. Report p50/p95.
  - **Diagnostics:**
    - Run `scripts/latency-report.sh --enable` and restart the Mac app. Alternatively, launch the app with `PENBRIDGE_LATENCY_LOG=1`.
    - Draw for 30 s or more: the 2 s Pings feed the offset estimate.
    - End the session, or use `--dump-now`, then run `scripts/latency-report.sh` (add `--logcat` to save the phone log as well).
  - **Measured:** phone `SendState` → Mac inject (`pen_down_to_inject`, `contact_to_inject`, `send_to_inject`, `send_to_receive`). This excludes MotionEvent → `SendState` on the phone.
  - **Error:** the clock offset is accurate to ±minRTT/2, reported per session as `offset_error_ms`.
- The user performs the Krita checks. The agent collects logcat and the Mac logs.
