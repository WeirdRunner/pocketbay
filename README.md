# Pocketbay

**Download for Windows:** [latest release](https://github.com/WeirdRunner/pocketbay/releases/latest) → `Pocketbay-Windows.zip`
(installed copies update themselves). The Mac app is built from source (see Build).

A native macOS front end for the Android Emulator, built for playing Android games
on an Apple Silicon Mac: one window, a MuMu-style sidebar, and built-in keyboard/mouse
game controls.

## How it works

- Runs the `Pocketbay` AVD headless (`-no-window -gpu host`) with its gRPC API on
  port 8554 and token auth. The port and token come from the emulator's discovery
  file in `~/Library/Caches/TemporaryItems/avd/running/pid_<pid>.ini`.
- **Screen:** `streamScreenshot` delivers RGBA frames, which are uploaded straight to
  a Metal texture. No video encode/decode. (The emulator's MMAP/shared-memory
  transport reports success but never writes pixels in emulator 37.1, so frames come
  inline.)
- **Input:** one long-lived `streamInputEvent` stream carries touches, keys (Mac key
  codes, sent as-is) and wheel events.
- **Game controls:** `KeymapEngine` turns keys and the mouse into multi-touch. Each
  control has its own finger, so move + aim + fire + scope can all be held at once.
  Layouts live in `~/Library/Application Support/Pocketbay/keymap.json` and apply
  only while a listed app (BGMI: `com.pubg.imobile`) is in front.
- Quitting saves a quick-boot snapshot (`adb emu kill`), so the next launch resumes
  in seconds. If the emulator stops unexpectedly, the app restarts it.

## Build

```bash
brew install protobuf swift-protobuf protoc-gen-grpc-swift   # only to regenerate the gRPC code
./scripts/build-app.sh                                        # builds + installs ~/Applications/Pocketbay.app
```

To regenerate `Sources/Pocketbay/Generated` after an emulator update:

```bash
cd proto && cp ~/Library/Android/sdk/emulator/lib/emulator_controller.proto . && \
protoc -I . -I /opt/homebrew/include \
  --swift_out=../Sources/Pocketbay/Generated --swift_opt=Visibility=Internal \
  --grpc-swift-2_out=../Sources/Pocketbay/Generated --grpc-swift-2_opt=Visibility=Internal,Client=true,Server=false \
  emulator_controller.proto
```

## Controls

| Input | Does |
|---|---|
| Click / drag | Touch |
| Trackpad scroll | Finger swipe (Android scrolls and flings natively) |
| Pinch | Two-finger pinch |
| Esc | Back (or leaves mouse aim) |
| ⌘V | Paste the Mac clipboard into Android (only sent on ⌘V, never in the background) |
| ⌘C / ⌘X / ⌘A | Copy / cut / select all in Android; anything copied in Android lands on the Mac clipboard |
| ⌘K / ⌘E / ⌘/ | Game controls on/off, edit them, show/hide key hints |
| ⇧⌘H / ⇧⌘R / ⇧⌘S | Home, Recent apps, Screenshot to Desktop |
| ⌃⌘F | Full screen (the sidebar slides in at the right edge) |

Drag `.apk` files onto the window or the Dock icon to install them.

## Windows version

`windows/` is a C# / .NET 8 WPF port with the same features and the same layout file
format. First launch downloads the Android emulator and an x86_64 Play Store image from
Google, creates the AVD itself, and checks hardware virtualization (WHPX / AEHD).
It checks GitHub Releases for updates and installs them on the next launch.

Release a new Windows build (from a Mac or PC with the .NET 8 SDK):

```bash
scripts/release-windows.sh 1.0.1 "What changed"
```

Game packs (`.pbpack`, a zip with `pack.json`, APKs, OBB and game data) can be dropped on
the window to install a large game without re-downloading it. They are not distributed here.
