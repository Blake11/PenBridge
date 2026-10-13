# PenBridge

PenBridge turns your phone into a pressure-sensitive drawing tablet for macOS over your local network. The primary target is a Samsung Galaxy S23 Ultra with S Pen. A small menu-bar app on the Mac receives pen input (pressure, tilt, hover, side button) and injects it as native tablet events.

**Status:** pre-alpha. The repository is scaffolded and nothing works end to end yet.

## Architecture

- `src/PenBridge.Core`: platform-free protocol, crypto, pairing, mapping and gestures (`net10.0`)
- `src/PenBridge.Mac`: menu-bar server (`net10.0-macos`, macOS 14+)
- `src/PenBridge.Android`: client app (`net10.0-android`, API 29+)
- `src/PenBridge.iOS`: placeholder only (`net10.0-ios`)
- `tests/PenBridge.Core.Tests`: xUnit v3 tests for Core

For details, see [docs/architecture.md](docs/architecture.md) and [docs/decisions.md](docs/decisions.md).

## Quick start

```sh
./scripts/bootstrap.sh          # installs the .NET SDK and workloads, checks Xcode / Android SDK
dotnet test tests/PenBridge.Core.Tests
```

For the full development workflow, see [docs/development.md](docs/development.md).

## License

[MIT](LICENSE) © 2026 Paul Ciurea
