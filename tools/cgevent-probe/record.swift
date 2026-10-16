// cgevent-probe record: a listen-only CGEventTap that prints one line per event.
//
// Usage: record [--moves] [--all] [--hid] [--raw] [--types 22,29,...]
//   --moves   include mouse-moved events (type 5), off by default
//   --all     dump every non-zero raw field (0...200) for mouse/scroll/other events, not only gestures
//   --hid     tap at the HID level instead of the session level
//   --raw     append the serialized event (CGEventCreateData, hex) so `post` can replay it with `raw <hex>`
//   --types   only print these CGEvent types (comma-separated numbers)
//
// Needs Input Monitoring for the host app (Terminal/iTerm). Never alters events (listen-only).
// Line format: t_ms(since first event) +dt_ms type(name) ns=<CGEvent timestamp, ns, CLOCK_UPTIME_RAW base> @x,y ...
// Gesture types (18-20, 29-34, 37, 38) add the NSEvent type, phase, touches and every non-zero field as f<n>=int[/double].
import AppKit
import CoreGraphics
import Foundation

var showMoves = false, allFields = false, useHID = false, rawDump = false
var onlyTypes: Set<UInt32>? = nil
var argv = Array(CommandLine.arguments.dropFirst())
while !argv.isEmpty {
    let a = argv.removeFirst()
    switch a {
    case "--moves": showMoves = true
    case "--all": allFields = true
    case "--hid": useHID = true
    case "--raw": rawDump = true
    case "--types":
        guard !argv.isEmpty else { fputs("--types needs a list\n", stderr); exit(2) }
        var set = Set<UInt32>()
        for part in argv.removeFirst().split(separator: ",", omittingEmptySubsequences: false) {
            guard let t = UInt32(part), t <= 1000 else { fputs("bad --types value '\(part)' (comma-separated event type numbers)\n", stderr); exit(2) }
            set.insert(t)
        }
        onlyTypes = set
    case "-h", "--help":
        print("usage: record [--moves] [--all] [--hid] [--raw] [--types 22,29,...]"); exit(0)
    default: fputs("unknown option \(a)\n", stderr); exit(2)
    }
}

setvbuf(stdout, nil, _IOLBF, 0)
signal(SIGPIPE, SIG_IGN)

// CGEvent.timestamp is nanoseconds (CLOCK_UPTIME_RAW base), not mach ticks.
func ms(_ t: UInt64) -> Double { Double(t) / 1_000_000.0 }

func pref(_ key: String, _ domain: String) -> String {
    if let v = CFPreferencesCopyValue(key as CFString, domain as CFString, kCFPreferencesCurrentUser, kCFPreferencesAnyHost) { return "\(v)" }
    if let v = CFPreferencesCopyAppValue(key as CFString, domain as CFString) { return "\(v)" }
    return "-"
}
let tp = "com.apple.AppleMultitouchTrackpad", bt = "com.apple.driver.AppleBluetoothMultitouch.trackpad"
var hdr = "# cgevent-probe record | macOS \(ProcessInfo.processInfo.operatingSystemVersionString) | tap=\(useHID ? "hid" : "session")"
for k in ["TrackpadTwoFingerDoubleTapGesture", "TrackpadRightClick", "TrackpadCornerSecondaryClick", "Clicking", "TrackpadThreeFingerTapGesture", "FirstClickThreshold", "ForceSuppressed", "ActuateDetents"] {
    hdr += " | \(k)=\(pref(k, tp))/\(pref(k, bt))"
}
hdr += " | doubleClickInterval=\(NSEvent.doubleClickInterval)"
print(hdr)
print("# cols: t_ms +dt_ms type(name) ns=<timestamp> @x,y ... ; gestures dump non-zero fields as f<n>=int[/double]")

let names: [UInt32: String] = [
    0: "Null", 1: "LDown", 2: "LUp", 3: "RDown", 4: "RUp", 5: "Moved", 6: "LDrag", 7: "RDrag",
    10: "KeyDown", 11: "KeyUp", 12: "Flags", 13: "AppKitDefined", 14: "SysDefined", 15: "AppDefined",
    16: "Periodic", 17: "CursorUpdate", 18: "Rotate", 19: "BeginGesture", 20: "EndGesture",
    21: "TabletPointer", 22: "Scroll", 23: "TabletProx", 24: "ODown", 25: "OUp", 26: "ODrag",
    29: "Gesture", 30: "Magnify", 31: "Swipe", 32: "SmartMagnify", 33: "QuickLook", 34: "Pressure",
    37: "DirectTouch", 38: "ChangeMode",
    0xFFFFFFFE: "TapDisabledTimeout", 0xFFFFFFFF: "TapDisabledUser",
]
func field(_ i: Int) -> CGEventField { unsafeBitCast(UInt32(i), to: CGEventField.self) }
func dump(_ e: CGEvent, _ range: ClosedRange<Int>) -> String {
    var s = ""
    for i in range {
        let iv = e.getIntegerValueField(field(i))
        let dv = e.getDoubleValueField(field(i))
        if iv == 0 && dv == 0 { continue }
        if dv == Double(iv) { s += " f\(i)=\(iv)" } else { s += " f\(i)=\(iv)/" + String(format: "%.4g", dv) }
    }
    return s
}
func hex(_ e: CGEvent) -> String {
    guard let d = e.data as Data? else { return "" }
    return d.map { String(format: "%02x", $0) }.joined()
}

var first: UInt64 = 0, prev: UInt64 = 0
var tapRef: CFMachPort?
let gestureTypes: Set<UInt32> = [18, 19, 20, 29, 30, 31, 32, 33, 34, 37, 38]

let cb: CGEventTapCallBack = { _, type, e, _ in
    let t = type.rawValue
    if t == 0xFFFFFFFE || t == 0xFFFFFFFF {
        print("# tap disabled (\(t == 0xFFFFFFFE ? "timeout" : "user")), re-enabling")
        if let p = tapRef { CGEvent.tapEnable(tap: p, enable: true) }
        return Unmanaged.passUnretained(e)
    }
    if t == 5 && !showMoves { return Unmanaged.passUnretained(e) }
    if let only = onlyTypes, !only.contains(t) { return Unmanaged.passUnretained(e) }
    let ts = e.timestamp
    if first == 0 { first = ts; prev = ts }
    var line = String(format: "%10.2f +%7.2f %2u(%@)", ms(ts - min(ts, first)), ts < prev ? 0 : ms(ts - prev), t, names[t] ?? "?")
    prev = max(prev, ts)
    line += " ns=\(ts)"
    let l = e.location
    line += String(format: " @%.0f,%.0f", l.x, l.y)
    // What AppKit sees as the event time (seconds since boot); compare with ns= to spot bad posted time fields.
    if let ns = NSEvent(cgEvent: e) { line += String(format: " nsts=%.3f", ns.timestamp) }
    switch t {
    case 1, 2, 3, 4, 5, 6, 7, 24, 25, 26:
        line += " click=\(e.getIntegerValueField(.mouseEventClickState)) btn=\(e.getIntegerValueField(.mouseEventButtonNumber))"
        let p = e.getDoubleValueField(.mouseEventPressure)
        if p != 0 { line += String(format: " pr=%.3f", p) }
        let sub = e.getIntegerValueField(.mouseEventSubtype)
        if sub != 0 { line += " sub=\(sub)" }
        line += " flags=0x" + String(e.flags.rawValue, radix: 16)
        if allFields { line += " |" + dump(e, 0...200) }
    case 22:
        line += " d1=\(e.getIntegerValueField(.scrollWheelEventDeltaAxis1)) d2=\(e.getIntegerValueField(.scrollWheelEventDeltaAxis2))"
        line += " pt=\(e.getIntegerValueField(.scrollWheelEventPointDeltaAxis1)),\(e.getIntegerValueField(.scrollWheelEventPointDeltaAxis2))"
        line += " cont=\(e.getIntegerValueField(.scrollWheelEventIsContinuous))"
        line += " phase=\(e.getIntegerValueField(field(99))) mom=\(e.getIntegerValueField(.scrollWheelEventMomentumPhase))"
        if allFields { line += " |" + dump(e, 0...200) }
    case 10, 11, 12:
        line += " key=\(e.getIntegerValueField(.keyboardEventKeycode)) flags=0x" + String(e.flags.rawValue, radix: 16)
    default:
        if gestureTypes.contains(t) {
            if let ns = NSEvent(cgEvent: e) {
                line += " nst=\(ns.type.rawValue)"
                if [.gesture, .magnify, .rotate, .swipe, .smartMagnify, .beginGesture, .endGesture, .pressure].contains(ns.type) {
                    line += " ph=\(ns.phase.rawValue)"
                    if ns.type == .magnify { line += String(format: " mag=%.4f", ns.magnification) }
                    if ns.type == .gesture || ns.type == .beginGesture || ns.type == .endGesture {
                        let touches = ns.allTouches()
                        if !touches.isEmpty {
                            line += " touches=\(touches.count)["
                            for th in touches { line += String(format: "%lu:%.2f,%.2f ", th.phase.rawValue, th.normalizedPosition.x, th.normalizedPosition.y) }
                            line += "]"
                        }
                    }
                    if ns.type == .pressure { line += String(format: " stage=%ld pr=%.3f", ns.stage, ns.pressure) }
                }
            }
            line += " |" + dump(e, 0...200)
        } else if allFields {
            line += " |" + dump(e, 0...200)
        }
    }
    if rawDump { line += " raw=" + hex(e) }
    print(line)
    return Unmanaged.passUnretained(e)
}

signal(SIGINT) { _ in print("# bye"); fflush(stdout); exit(0) }
signal(SIGTERM) { _ in fflush(stdout); exit(0) }

guard let tap = CGEvent.tapCreate(tap: useHID ? .cghidEventTap : .cgSessionEventTap, place: .headInsertEventTap,
                                  options: .listenOnly, eventsOfInterest: CGEventMask(UInt64.max), callback: cb, userInfo: nil) else {
    let msg = "ERROR: could not create the event tap. Grant Input Monitoring to your terminal app: System Settings > Privacy & Security > Input Monitoring > enable Terminal (or iTerm), quit and reopen the terminal, then re-run.\n"
    FileHandle.standardError.write(msg.data(using: .utf8)!)
    print("# " + msg, terminator: "")
    exit(1)
}
tapRef = tap
let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
CFRunLoopAddSource(CFRunLoopGetCurrent(), src, .commonModes)
CGEvent.tapEnable(tap: tap, enable: true)
print("# listening... Ctrl-C to stop | uptime_raw_ns=\(clock_gettime_nsec_np(CLOCK_UPTIME_RAW)) mach_abs=\(mach_absolute_time())")
CFRunLoopRun()
