// cgevent-probe post: posts a scripted CGEvent sequence (mouse, phased scroll, gesture type 29/30 with arbitrary
// fields, raw replays from `record --raw`).
//
// Usage: post [-n] [script-file | -e 'cmd; cmd; ...']     (no file and no -e: read the script from stdin)
//   -n       dry run: parse and print the events, post nothing
//   --check  validate the whole script only (no printing, no posting); exit 2 on any error
// The whole script is validated before the first event is posted: bounded numbers, known event types, fN in 0-255,
// repeat counts <= 10000, at most 10000 events in total, and every mouse down needs its up.
//
// Script: one command per line or separated by ';'. '#' starts a comment. Commands:
//   at X Y | at cur          point used by mouse events and by `loc` (default: the cursor at start)
//   to hid|session|pid N     where events are posted (default hid = CGEventPost(kCGHIDEventTap))
//   sleep MS                 wait (fractional ms allowed)
//   move [X Y]               mouse moved to X Y (default: the `at` point); moves the cursor
//   mouse KIND [click=N]     KIND = ldown lup rdown rup odown oup ldrag rdrag at the `at` point (click state default 1)
//   scroll [dx=N] [dy=N] [phase=N] [mom=N] [cont=0|1] [line]
//                            pixel scroll (wheel1 = dy, wheel2 = dx), continuous by default; phase -> field 99,
//                            mom -> field 123 (omitted = 0)
//   gesture [type=N] [hid=N] [phase=N]
//                            a CGEventCreate event with type (default 29), field 110 = hid, field 132 = phase
//   raw HEX                  replay a serialized event (the raw= column of `record --raw`)
//   repeat N ... end         repeat a block (nestable)
//   echo TEXT                print TEXT
// Every event command also takes: fN=V (integer field), fN=V.V or fN=d:V (double field), fN=i:V (integer),
// loc (set the location to the `at` point; mouse events always have it, gesture/scroll/raw keep the cursor
// still without it), ts=keep (keep the event's own timestamp; default = now, ns CLOCK_UPTIME_RAW).
// Fields are applied after the command's own fields, in order, so they can override them.
//
// Needs the Accessibility permission for the host app (Terminal/iTerm) to post.
import AppKit
import CoreGraphics
import Foundation

setvbuf(stdout, nil, _IOLBF, 0)

var dry = false
var checkOnly = false
var scriptText: String? = nil
var argv = Array(CommandLine.arguments.dropFirst())
while !argv.isEmpty {
    let a = argv.removeFirst()
    switch a {
    case "-n": dry = true
    case "--check": checkOnly = true; dry = true
    case "-e":
        guard !argv.isEmpty else { fputs("-e needs a script\n", stderr); exit(2) }
        scriptText = argv.removeFirst()
    case "-h", "--help":
        print("usage: post [-n] [--check] [script-file | -e 'cmd; cmd']  (see the header of post.swift / README)"); exit(0)
    default:
        guard let s = try? String(contentsOfFile: a, encoding: .utf8) else { fputs("cannot read \(a)\n", stderr); exit(2) }
        scriptText = s
    }
}
if scriptText == nil {
    scriptText = String(data: FileHandle.standardInput.readDataToEndOfFile(), encoding: .utf8) ?? ""
}

struct Cmd { let line: Int; let words: [String] }
var cmds: [Cmd] = []
for (i, rawLine) in scriptText!.components(separatedBy: "\n").enumerated() {
    let noComment = rawLine.split(separator: "#", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
    for part in noComment.split(separator: ";") {
        let w = part.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
        if !w.isEmpty { cmds.append(Cmd(line: i + 1, words: w)) }
    }
}

func fail(_ c: Cmd, _ msg: String) -> Never { fputs("line \(c.line): \(msg): \(c.words.joined(separator: " "))\n", stderr); exit(2) }
func field(_ i: Int) -> CGEventField { unsafeBitCast(UInt32(i), to: CGEventField.self) }
func nowNs() -> UInt64 { clock_gettime_nsec_np(CLOCK_UPTIME_RAW) }

let src = CGEventSource(stateID: .hidSystemState)
var point = CGEvent(source: nil)!.location
enum Dest { case hid, session, pid(pid_t) }
var dest = Dest.hid
var posted = 0

/// key=value options of a command (after the leading words).
func opts(_ c: Cmd, from: Int) -> [(String, String)] {
    var r: [(String, String)] = []
    for w in c.words.dropFirst(from) {
        if let eq = w.firstIndex(of: "=") { r.append((String(w[..<eq]), String(w[w.index(after: eq)...]))) } else { r.append((w, "")) }
    }
    return r
}
func opt(_ o: [(String, String)], _ k: String) -> String? { o.first(where: { $0.0 == k })?.1 }
let maxMagnitude = 1e9
let maxRepeat = 10_000, maxEvents = 10_000, maxCommands = 200_000
func num(_ c: Cmd, _ s: String) -> Double {
    guard let v = Double(s), v.isFinite, abs(v) <= maxMagnitude else { fail(c, "not a number in +-1e9 '\(s)'") }
    return v
}
/// A bounded integer (rejects fractions).
func intArg(_ c: Cmd, _ s: String, _ range: ClosedRange<Int>) -> Int {
    let v = num(c, s)
    guard v == v.rounded(), range.contains(Int(v)) else { fail(c, "expected an integer in \(range.lowerBound)...\(range.upperBound), got '\(s)'") }
    return Int(v)
}
let knownTypes: Set<Int> = [1, 2, 3, 4, 5, 6, 7, 22, 25, 26, 27, 29, 30]

/// Applies the generic options (fN=, loc, ts=keep), then posts.
func finish(_ c: Cmd, _ e: CGEvent, _ o: [(String, String)], what: String) {
    for (k, v) in o where k.hasPrefix("f") {
        guard let n = Int(k.dropFirst()) else { continue }
        if v.hasPrefix("d:") { e.setDoubleValueField(field(n), value: num(c, String(v.dropFirst(2)))) }
        else if v.hasPrefix("i:") { e.setIntegerValueField(field(n), value: Int64(num(c, String(v.dropFirst(2))).rounded())) }
        else if v.contains(".") || v.contains("e") { e.setDoubleValueField(field(n), value: num(c, v)) }
        else { guard let iv = Int64(v) else { fail(c, "bad field value '\(v)'") }; e.setIntegerValueField(field(n), value: iv) }
    }
    if opt(o, "loc") != nil { e.location = point }
    if opt(o, "ts") != "keep" { e.timestamp = nowNs() }
    if dry { print("would post \(what) type=\(e.type.rawValue) @\(Int(e.location.x)),\(Int(e.location.y))"); return }
    switch dest {
    case .hid: e.post(tap: .cghidEventTap)
    case .session: e.post(tap: .cgSessionEventTap)
    case .pid(let p): e.postToPid(p)
    }
    posted += 1
}

func hexData(_ c: Cmd, _ s: String) -> Data {
    guard s.count % 2 == 0 else { fail(c, "odd hex length") }
    var d = Data(capacity: s.count / 2)
    var i = s.startIndex
    while i < s.endIndex {
        let j = s.index(i, offsetBy: 2)
        guard let b = UInt8(s[i..<j], radix: 16) else { fail(c, "bad hex") }
        d.append(b); i = j
    }
    return d
}

let mouseKinds: [String: (CGEventType, CGMouseButton)] = [
    "ldown": (.leftMouseDown, .left), "lup": (.leftMouseUp, .left), "ldrag": (.leftMouseDragged, .left),
    "rdown": (.rightMouseDown, .right), "rup": (.rightMouseUp, .right), "rdrag": (.rightMouseDragged, .right),
    "odown": (.otherMouseDown, .center), "oup": (.otherMouseUp, .center),
]


/// Walks the script exactly as `run` would (repeats expanded) and fails before anything is posted.
func validate(_ list: ArraySlice<Cmd>, _ expanded: inout Int, _ events: inout Int, _ down: inout [CGMouseButton: Int]) {
    var i = list.startIndex
    while i < list.endIndex {
        let c = list[i], w = c.words
        expanded += 1
        if expanded > maxCommands { fail(c, "script expands to more than \(maxCommands) commands") }
        func checkOpts(_ o: [(String, String)]) {
            for (k, v) in o where k.hasPrefix("f") {
                guard !k.dropFirst().isEmpty, k.dropFirst().allSatisfy(\.isNumber) else { continue }
                guard let n = Int(k.dropFirst()), n >= 0, n < 256 else { fail(c, "field number must be 0-255 ('\(k)')") }
                if v.hasPrefix("d:") || v.hasPrefix("i:") { _ = num(c, String(v.dropFirst(2))) }
                else if v.contains(".") || v.contains("e") { _ = num(c, v) }
                else { guard Int64(v) != nil else { fail(c, "bad field value '\(v)'") } }
            }
        }
        func event() { events += 1; if events > maxEvents { fail(c, "more than \(maxEvents) events") } }
        switch w[0] {
        case "at":
            if w.count >= 2 && w[1] == "cur" {}
            else if w.count >= 3 { _ = num(c, w[1]); _ = num(c, w[2]) }
            else { fail(c, "at X Y | at cur") }
        case "to":
            if w.count >= 2 && (w[1] == "hid" || w[1] == "session") {}
            else if w.count >= 3 && w[1] == "pid" { _ = intArg(c, w[2], 1...4_194_304) }
            else { fail(c, "to hid|session|pid N") }
        case "sleep":
            guard w.count >= 2 else { fail(c, "sleep MS") }
            _ = num(c, w[1])
        case "echo": break
        case "move":
            var from = 1
            if w.count >= 3, let _ = Double(w[1]), let _ = Double(w[2]) { _ = num(c, w[1]); _ = num(c, w[2]); from = 3 }
            checkOpts(opts(c, from: from)); event()
        case "mouse":
            guard w.count >= 2, let (t, b) = mouseKinds[w[1]] else { fail(c, "mouse ldown|lup|rdown|rup|odown|oup|ldrag|rdrag") }
            let o = opts(c, from: 2)
            _ = intArg(c, opt(o, "click") ?? "1", 0...1000)
            checkOpts(o); event()
            if w[1].hasSuffix("down") { down[b, default: 0] += 1 } else if w[1].hasSuffix("up") { down[b, default: 0] = max(0, down[b, default: 0] - 1) }
            _ = t
        case "scroll":
            let o = opts(c, from: 1)
            _ = intArg(c, opt(o, "dy") ?? "0", -1_000_000...1_000_000); _ = intArg(c, opt(o, "dx") ?? "0", -1_000_000...1_000_000)
            _ = intArg(c, opt(o, "cont") ?? "1", 0...1)
            if let p = opt(o, "phase") { _ = intArg(c, p, -1_000_000...1_000_000) }
            if let m = opt(o, "mom") { _ = intArg(c, m, -1_000_000...1_000_000) }
            checkOpts(o); event()
        case "gesture":
            let o = opts(c, from: 1)
            let t = intArg(c, opt(o, "type") ?? "29", 0...1000)
            guard knownTypes.contains(t) else { fail(c, "unknown event type \(t) (known: \(knownTypes.sorted()))") }
            if let h = opt(o, "hid") { _ = intArg(c, h, -1_000_000...1_000_000) }
            if let p = opt(o, "phase") { _ = intArg(c, p, -1_000_000...1_000_000) }
            checkOpts(o); event()
        case "raw":
            guard w.count >= 2 else { fail(c, "raw HEX") }
            guard CGEvent(withDataAllocator: nil, data: hexData(c, w[1]) as CFData) != nil else { fail(c, "CGEventCreateFromData failed") }
            checkOpts(opts(c, from: 2)); event()
        case "repeat":
            guard w.count >= 2 else { fail(c, "repeat N") }
            let n = intArg(c, w[1], 0...maxRepeat)
            var depth = 1, j = i + 1
            while j < list.endIndex {
                if list[j].words[0] == "repeat" { depth += 1 }
                if list[j].words[0] == "end" { depth -= 1; if depth == 0 { break } }
                j += 1
            }
            guard j < list.endIndex else { fail(c, "repeat without end") }
            for _ in 0..<n { validate(list[(i + 1)..<j], &expanded, &events, &down) }
            i = j
        case "end": fail(c, "end without repeat")
        default: fail(c, "unknown command")
        }
        i += 1
    }
}

func run(_ list: ArraySlice<Cmd>) {
    var i = list.startIndex
    while i < list.endIndex {
        let c = list[i]
        let w = c.words
        switch w[0] {
        case "at":
            if w.count >= 2 && w[1] == "cur" { point = CGEvent(source: nil)!.location }
            else if w.count >= 3 { point = CGPoint(x: num(c, w[1]), y: num(c, w[2])) }
            else { fail(c, "at X Y | at cur") }
        case "to":
            if w.count >= 2 && w[1] == "hid" { dest = .hid }
            else if w.count >= 2 && w[1] == "session" { dest = .session }
            else if w.count >= 3 && w[1] == "pid" { dest = .pid(pid_t(intArg(c, w[2], 1...4_194_304))) }
            else { fail(c, "to hid|session|pid N") }
        case "sleep":
            guard w.count >= 2 else { fail(c, "sleep MS") }
            if !dry { usleep(useconds_t(max(0, min(num(c, w[1]), 600_000) * 1000))) }
        case "echo":
            print(w.dropFirst().joined(separator: " "))
        case "move":
            var at = point
            var from = 1
            if w.count >= 3, let x = Double(w[1]), let y = Double(w[2]) { at = CGPoint(x: x, y: y); point = at; from = 3 }
            let e = CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: at, mouseButton: .left)!
            finish(c, e, opts(c, from: from), what: "move")
        case "mouse":
            guard w.count >= 2, let (t, b) = mouseKinds[w[1]] else { fail(c, "mouse ldown|lup|rdown|rup|odown|oup|ldrag|rdrag") }
            let o = opts(c, from: 2)
            let e = CGEvent(mouseEventSource: src, mouseType: t, mouseCursorPosition: point, mouseButton: b)!
            e.setIntegerValueField(.mouseEventClickState, value: Int64(intArg(c, opt(o, "click") ?? "1", 0...1000)))
            finish(c, e, o, what: "mouse \(w[1])")
        case "scroll":
            let o = opts(c, from: 1)
            let dy = Int32(intArg(c, opt(o, "dy") ?? "0", -1_000_000...1_000_000)), dx = Int32(intArg(c, opt(o, "dx") ?? "0", -1_000_000...1_000_000))
            let e = CGEvent(scrollWheelEvent2Source: src, units: opt(o, "line") != nil ? .line : .pixel,
                            wheelCount: 2, wheel1: dy, wheel2: dx, wheel3: 0)!
            e.setIntegerValueField(.scrollWheelEventIsContinuous, value: Int64(intArg(c, opt(o, "cont") ?? "1", 0...1)))
            if let p = opt(o, "phase") { e.setIntegerValueField(field(99), value: Int64(intArg(c, p, -1_000_000...1_000_000))) }
            if let m = opt(o, "mom") { e.setIntegerValueField(.scrollWheelEventMomentumPhase, value: Int64(intArg(c, m, -1_000_000...1_000_000))) }
            finish(c, e, o, what: "scroll")
        case "gesture":
            let o = opts(c, from: 1)
            let e = CGEvent(source: src)!
            e.type = CGEventType(rawValue: UInt32(intArg(c, opt(o, "type") ?? "29", 0...1000)))!
            if let h = opt(o, "hid") { e.setIntegerValueField(field(110), value: Int64(intArg(c, h, -1_000_000...1_000_000))) }
            if let p = opt(o, "phase") { e.setIntegerValueField(field(132), value: Int64(intArg(c, p, -1_000_000...1_000_000))) }
            finish(c, e, o, what: "gesture")
        case "raw":
            guard w.count >= 2 else { fail(c, "raw HEX") }
            guard let e = CGEvent(withDataAllocator: nil, data: hexData(c, w[1]) as CFData) else { fail(c, "CGEventCreateFromData failed") }
            finish(c, e, opts(c, from: 2), what: "raw")
        case "repeat":
            guard w.count >= 2 else { fail(c, "repeat N") }
            // find the matching end
            var depth = 1, j = i + 1
            while j < list.endIndex {
                if list[j].words[0] == "repeat" { depth += 1 }
                if list[j].words[0] == "end" { depth -= 1; if depth == 0 { break } }
                j += 1
            }
            guard j < list.endIndex else { fail(c, "repeat without end") }
            for _ in 0..<intArg(c, w[1], 0...maxRepeat) { run(list[(i + 1)..<j]) }
            i = j
        case "end":
            fail(c, "end without repeat")
        default:
            fail(c, "unknown command")
        }
        i += 1
    }
}

if !dry && !AXIsProcessTrusted() {
    fputs("warning: this process is not trusted for Accessibility; the window server may drop posted events. Grant it to your terminal app: System Settings > Privacy & Security > Accessibility.\n", stderr)
}
var vExpanded = 0, vEvents = 0, vDown: [CGMouseButton: Int] = [:]
validate(cmds[...], &vExpanded, &vEvents, &vDown)
if let (b, n) = vDown.first(where: { $0.value > 0 }) {
    fputs("error: \(n) mouse down(s) of button \(b.rawValue) without a matching up; add the up to the script\n", stderr); exit(2)
}
if checkOnly { print("# script ok: \(vEvents) events"); exit(0) }
run(cmds[...])
if !dry { print("# posted \(posted) events") }
