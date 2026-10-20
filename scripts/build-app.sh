#!/bin/zsh
# Builds Pocketbay.app and installs it to ~/Applications.
set -euo pipefail
ROOT="${0:A:h:h}"
cd "$ROOT"

swift build -c release

APP="$ROOT/build/Pocketbay.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Pocketbay "$APP/Contents/MacOS/Pocketbay"

# Icon
ICONSET="$ROOT/build/AppIcon.iconset"
if [[ ! -f "$ROOT/build/AppIcon.icns" ]]; then
  rm -rf "$ICONSET"; mkdir -p "$ICONSET"
  swift scripts/make-icon.swift "$ROOT/build/icon-1024.png"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$ROOT/build/icon-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s * 2)) $((s * 2)) "$ROOT/build/icon-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$ROOT/build/AppIcon.icns"
fi
cp "$ROOT/build/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Pocketbay</string>
  <key>CFBundleDisplayName</key><string>Pocketbay</string>
  <key>CFBundleIdentifier</key><string>com.devesh.pocketbay</string>
  <key>CFBundleExecutable</key><string>Pocketbay</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Android in your pocket bay.</string>
  <key>NSMicrophoneUsageDescription</key><string>Pocketbay sends your microphone to Android for in-game voice chat, only while the microphone button is on.</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Android App</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>CFBundleTypeExtensions</key><array><string>apk</string></array>
    </dict>
  </array>
</dict>
</plist>
EOF

codesign --force --deep -s - "$APP"
rm -rf "$HOME/Applications/Pocketbay.app"
cp -R "$APP" "$HOME/Applications/Pocketbay.app"
echo "Installed $HOME/Applications/Pocketbay.app"
