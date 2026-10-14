# PenBridge icon masters

Concept A, "Two shores" (issue #101): the S Pen nib draws on the phone, and its pressure stroke bridges over to the Mac screen, landing as the cursor dot. These SVGs are the source of truth. Every platform asset is generated from them, so edit these and never the generated files.

**Regenerate:** `scripts/gen-icons.sh` (macOS + Xcode), then commit the changed outputs. `scripts/gen-icons.sh --check` fails if the committed outputs are stale. `--preview <dir>` renders PNGs to look at.

| Master | What | Generated output |
|---|---|---|
| `mark.svg` | Full-colour mark on transparent, 1024 = 108 dp adaptive layer. Its `<g id>` groups (`devices`, `ink`) are the layers. | Android `drawable/ic_launcher_foreground.xml` and `ic_launcher_monochrome.xml` (themed icon); Mac `Resources/AppIcon.icon/Assets/*.svg` |
| `background.svg` | Vertical navy gradient | Android `drawable/ic_launcher_background.xml`; Mac `AppIcon.icon/icon.json` fill |
| `menubar-{idle,connected,paused,attention}.svg` | 18 pt template glyphs for the Mac status item | Mac `Resources/Assets.xcassets/MenuBar*.imageset` |

Rules (the generator enforces them):
- `mark.svg` and `background.svg` use only the subset that Android VectorDrawables can express: `g` (translate/scale), `path`, `rect`, `circle`, solid colours, and a userSpaceOnUse `linearGradient`. Masks, filters and CSS are not allowed.
- The menu-bar glyphs don't use `<mask>`, because CoreSVG rasterises masks at 18 px and they blur on Retina. Use even-odd clip paths or fills instead. The `<g id="glyph">` group must be identical in all four.
- The gap where the stroke crosses the screen's top edge is real geometry (an open path). If you move the stroke, move the gap with it (see the comment in `mark.svg`).

Palette: devices `#9ECAFF`, ink `#F2F8FF`, background `#27486A` → `#1F3A55` → `#16263A` (the Android Material 3 theme colours).
