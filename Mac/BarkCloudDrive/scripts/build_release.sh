#!/usr/bin/env bash
# Этап 3 — сборка релиза BarkCloud Drive: archive → export → .dmg/.pkg →
# подпись Developer ID → нотаризация (notarytool) → staple.
#
# ⚠️ Требует платный Apple Developer аккаунт и настроенные сертификаты в Keychain:
#   - «Developer ID Application: <Team> (<TeamID>)» — подпись .app/.dmg
#   - «Developer ID Installer:   <Team> (<TeamID>)» — подпись .pkg
#   - профиль notarytool: xcrun notarytool store-credentials <profile> \
#       --apple-id <you@apple.id> --team-id <TEAMID> --password <app-spec-pароль>
#
# Запуск (из Mac/BarkCloudDrive):
#   TEAM_ID=ABCDE12345 \
#   SIGN_APP="Developer ID Application: Acme (ABCDE12345)" \
#   SIGN_PKG="Developer ID Installer: Acme (ABCDE12345)" \
#   NOTARY_PROFILE=barkcloud-notary \
#   scripts/build_release.sh
#
# Что делает скрипт нельзя проверить без аккаунта — он задаёт корректную
# последовательность; команды нотаризации фактически запускаешь ты.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$HERE"

PROJECT="BarkCloudDrive.xcodeproj"
SCHEME="BarkCloudDrive"
APP_NAME="BarkCloud Drive"
BUILD="$HERE/build"
ARCHIVE="$BUILD/$SCHEME.xcarchive"
EXPORT="$BUILD/export"
DMG="$BUILD/$APP_NAME.dmg"
PKG="$BUILD/$APP_NAME.pkg"

: "${TEAM_ID:?нужен TEAM_ID}"
: "${SIGN_APP:?нужен SIGN_APP (Developer ID Application identity)}"
: "${SIGN_PKG:?нужен SIGN_PKG (Developer ID Installer identity)}"
: "${NOTARY_PROFILE:?нужен NOTARY_PROFILE (профиль notarytool в Keychain)}"

rm -rf "$BUILD"; mkdir -p "$BUILD"

echo "==> archive (Release, Developer ID)"
xcodebuild archive \
  -project "$PROJECT" -scheme "$SCHEME" -configuration Release \
  -archivePath "$ARCHIVE" \
  DEVELOPMENT_TEAM="$TEAM_ID" \
  CODE_SIGN_STYLE=Manual \
  CODE_SIGN_IDENTITY="$SIGN_APP" \
  -allowProvisioningUpdates

echo "==> export .app из архива"
mkdir -p "$EXPORT"
cp -R "$ARCHIVE/Products/Applications/$APP_NAME.app" "$EXPORT/"
APP="$EXPORT/$APP_NAME.app"

echo "==> проверка подписи .app (включая встроенный BarkCloudFS.appex)"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "==> .dmg (подпись + нотаризация)"
hdiutil create -volname "$APP_NAME" -srcfolder "$APP" -ov -format UDZO "$DMG"
codesign --sign "$SIGN_APP" --timestamp "$DMG"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$DMG"

echo "==> .pkg (подпись Installer + нотаризация)"
productbuild --component "$APP" /Applications --sign "$SIGN_PKG" "$PKG"
xcrun notarytool submit "$PKG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$PKG"

echo "==> готово:"
echo "    $DMG"
echo "    $PKG"
echo
echo "Онбординг для пользователя: после установки включить расширение в"
echo "System Settings → General → Login Items & Extensions → File System Extensions → BarkCloud."
