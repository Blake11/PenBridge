# cgevent-probe

Two small Swift command-line tools for investigating CGEvent injection (skill `mac-build-run`, "mac-gesture-events"):

- `record`: a listen-only CGEventTap that prints one line per event (ns timestamps, the NSEvent view, every non-zero
  gesture field, and optionally the serialized event for replay). It never changes events.
- `post`: posts a scripted CGEvent sequence (mouse, phased scroll, type 29/30 gesture events with arbitrary fields,
  raw replays of recorded events).

Build and run through the wrapper (it compiles into the git-ignored `.build/cgevent-probe/`):

```bash
scripts/cgevent-probe.sh build
scripts/cgevent-probe.sh record --types 22,29 > /tmp/rec.log      # Ctrl-C to stop
scripts/cgevent-probe.sh post -e 'at 600 375; move; gesture hid=22 loc'
```

Permissions belong to the terminal app: `record` needs **Input Monitoring**, `post` needs **Accessibility**.

## record

`record [--moves] [--all] [--hid] [--raw] [--types 22,29,...]`

| Option | Effect |
|---|---|
| `--moves` | include mouse-moved events (off by default, they flood the log) |
| `--all` | dump every non-zero field (0...200) for mouse and scroll events too, not only gestures |
| `--hid` | tap at the HID level instead of the session |
| `--raw` | append `raw=<hex>` (CGEventCreateData), which `post` can replay |
| `--types` | print only these CGEvent types |

Line: `t_ms +dt_ms type(name) ns=<CGEvent timestamp> @x,y nsts=<NSEvent.timestamp, s> ...`. Gesture types add
`nst=` (NSEvent type), `ph=` (NSEvent phase), `touches=n[phase:x,y ...]` and `| f<n>=int[/double]` for every non-zero
field. The header records the trackpad preferences that change gesture behaviour.

## post

`post [-n] [script-file | -e 'cmd; cmd']` (no file and no `-e`: the script comes from stdin; `-n` = dry run).

One command per line or separated by `;`, `#` comments:

| Command | Posts |
|---|---|
| `at X Y` / `at cur` | sets the point used by mouse events and `loc` (default: the cursor at start) |
| `to hid` / `to session` / `to pid N` | destination (default `hid`, as PenBridge) |
| `sleep MS` | waits |
| `move [X Y]` | mouse moved (moves the cursor) |
| `mouse ldown\|lup\|rdown\|rup\|odown\|oup\|ldrag\|rdrag [click=N]` | a mouse event at the point (click state default 1) |
| `scroll [dx=N] [dy=N] [phase=N] [mom=N] [cont=0\|1] [line]` | pixel scroll, continuous by default; phase = field 99, mom = field 123 |
| `gesture [type=N] [hid=N] [phase=N]` | `CGEventCreate` + type (default 29), field 110 = hid, 132 = phase |
| `raw HEX` | replays a `record --raw` event |
| `repeat N` ... `end` | repeats a block |
| `echo TEXT` | prints |

Every event command also takes `fN=V` (integer field; `fN=1.5` or `fN=d:V` double, `fN=i:V` integer), `loc` (set
the location to the point; without it gesture/scroll/raw events keep the cursor still) and `ts=keep` (keep the
event's own timestamp; default now). Field options apply last, so they override the command's own values.

Examples:

```bash
# smart zoom at a fixed point (the frontmost app only)
post -e 'at 600 375; move; gesture hid=22 loc'
# a pinch: magnify stream with deltas in field 113
post -e 'gesture hid=8 phase=1; repeat 10; sleep 16; gesture hid=8 phase=2 f113=0.05; end; gesture hid=8 phase=4'
# a phased scroll with its swipe companion (type 29, hid 6, deltas in 118/119)
post -e 'gesture hid=6 phase=1 f119=4.0; scroll dy=4 phase=1; repeat 10; sleep 16; gesture hid=6 phase=2 f119=4.0; scroll dy=4 phase=2; end; gesture hid=6 phase=4; scroll phase=4'
# replay a recording's events, 8 ms apart (the recorded timing is not replayed)
grep -o 'raw=[0-9a-f]*' mbp.log | sed 's/^raw=/raw /; s/$/; sleep 8/' | post
```

## Pitfalls

- **Run `post` outside any sandbox.** A sandboxed process (e.g. an agent's sandboxed shell) can post: the events
  reach a session tap (so `record` shows them) but are not delivered to apps. Nothing reports an error.
- The window server overwrites the source fields on post: f39-f44 (connection, pid, uid), f58 and f169 (timestamps;
  a real device has f169 in mach ticks, posts get ns), f59, f85, f102, f107. Setting them in a script has no effect,
  so they cannot be what an app keys on for a posted event.
- Magnify, rotate and smart zoom reach only the frontmost app. Activate it first (`open -a Preview file.pdf`).
- If someone is using the mouse, a posted gesture without `loc` lands wherever the cursor is now.

## MacBook over LAN (recording a real trackpad)

The binaries are standalone (built for macOS 14+), so a MacBook needs no repo or toolchain.

```bash
# dev Mac: build and serve the binaries
scripts/cgevent-probe.sh build && cd .build/cgevent-probe && python3 -m http.server 8765
# MacBook (Terminal with Input Monitoring granted): fetch and record
curl -fO http://<dev-mac>.local:8765/record && chmod +x record && ./record --raw --types 22,29,3,4 > mbp.log
#   ...do the gesture, then Ctrl-C
# dev Mac: receive the log                        # MacBook: send it
nc -l 8766 > mbp.log                               nc <dev-mac>.local 8766 < mbp.log
#   (macOS nc may not exit at end of file: once the size on the dev Mac stops growing, Ctrl-C both)
```

Stop the http server afterwards. With `--raw`, the touch-stream events can then be replayed on the dev Mac with `raw`.
