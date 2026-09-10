#!/bin/bash
# Build ClaudeUsageBar.app.
#
# swiftc is invoked directly rather than through SwiftPM: SwiftPM wraps manifest
# evaluation in its own sandbox-exec, which cannot nest inside a sandboxed shell,
# and this app is a handful of files with no dependencies.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/build/ClaudeUsageBar.app"
SDK="${SDKROOT:-$(xcrun --show-sdk-path 2>/dev/null || echo /Library/Developer/CommandLineTools/SDKs/MacOSX.sdk)}"

# clang derives its module cache from the darwin user cache dir, which some
# sandboxes deny; point it somewhere writable.
CACHE="${TMPDIR:-/tmp}/claude-usage-bar-modcache"
mkdir -p "$CACHE" "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "==> Compiling (SDK: $SDK)"
xcrun --sdk macosx swiftc \
    -O -whole-module-optimization \
    -target arm64-apple-macos14.0 \
    -sdk "$SDK" \
    -module-cache-path "$CACHE" \
    -framework AppKit -framework SwiftUI -framework Security \
    -o "$APP/Contents/MacOS/ClaudeUsageBar" \
    "$ROOT"/Sources/ClaudeUsageBar/*.swift

echo "==> Writing Info.plist"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>ClaudeUsageBar</string>
    <key>CFBundleDisplayName</key>     <string>Claude Usage</string>
    <key>CFBundleExecutable</key>      <string>ClaudeUsageBar</string>
    <key>CFBundleIdentifier</key>      <string>local.claude-usage-bar</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>1.0</string>
    <key>CFBundleVersion</key>         <string>1</string>
    <key>LSMinimumSystemVersion</key>  <string>14.0</string>
    <!-- Menu bar only: no Dock icon, no app switcher entry. -->
    <key>LSUIElement</key>             <true/>
    <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

echo "==> Signing (ad-hoc, stable identifier so the keychain grant sticks)"
codesign --force --sign - --identifier local.claude-usage-bar "$APP"

echo "==> Built $APP"
