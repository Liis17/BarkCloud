# BarkCloud iOS — Xcode setup steps

Часть PR 1 уже выполнена через файловую систему: понижен deployment target до 18.0, опционные платформы убраны до `iphoneos/iphonesimulator`, в `knownRegions` добавлен `ru`, создан каркас Swift-файлов, `Localizable.xcstrings`, скрипт `sync_proto.sh`, конфиг `Proto/grpc-swift-proto-generator-config.json`.

Эти шаги нужно выполнить в Xcode UI вручную (правка `project.pbxproj` напрямую слишком хрупкая — Xcode может перезаписать наши изменения).

## 1. Добавить SwiftPM-пакеты

В Xcode: **File → Add Package Dependencies…**

| URL | Версия | Продукты для линковки в target BarkCloud |
|---|---|---|
| `https://github.com/grpc/grpc-swift` | `from 2.0.0` | `GRPCCore` |
| `https://github.com/grpc/grpc-swift-nio-transport` | `from 2.0.0` | `GRPCNIOTransportHTTP2` |
| `https://github.com/grpc/grpc-swift-protobuf` | `from 2.0.0` | `GRPCProtobuf` |
| `https://github.com/apple/swift-protobuf` | `from 1.28.0` | `SwiftProtobuf` |

## 2. Build-tool plugin для proto-кодогена

Target BarkCloud → **Build Phases → Run Build Tool Plug-ins** (или вкладка General → Frameworks, Libraries, and Embedded Content рядом с продуктами).

Подключить плагин `GRPCProtobufGenerator` из пакета `grpc-swift-protobuf`.

Конфиг плагина уже лежит рядом с .proto-файлами: `BarkCloud/Proto/grpc-swift-proto-generator-config.json`. Плагин его подхватит автоматически.

Сами .proto-файлы будет создавать build phase «Sync Shared Proto» (см. ниже).

### Fallback (если build-tool plugin для app target не работает)

Создать второй Run-Script с прямым вызовом `protoc`:
```bash
PROTOC=/opt/homebrew/bin/protoc
SWIFT_GEN=$(find ${HOME}/Library/Developer/Xcode/DerivedData -name "protoc-gen-swift" -type f 2>/dev/null | head -1)
GRPC_GEN=$(find ${HOME}/Library/Developer/Xcode/DerivedData -name "protoc-gen-grpc-swift" -type f 2>/dev/null | head -1)
mkdir -p "${SRCROOT}/BarkCloud/Generated/Proto"
for f in "${SRCROOT}/BarkCloud/Proto/"*.proto; do
    "${PROTOC}" --plugin=protoc-gen-swift="${SWIFT_GEN}" \
                --plugin=protoc-gen-grpc-swift="${GRPC_GEN}" \
                --swift_out="${SRCROOT}/BarkCloud/Generated/Proto" \
                --grpc-swift_out="${SRCROOT}/BarkCloud/Generated/Proto" \
                --proto_path="${SRCROOT}/BarkCloud/Proto" \
                "$f"
done
```
Файлы появятся в `BarkCloud/Generated/Proto/` и подхватятся filesystem-synchronized group.

## 3. Run-Script build phase «Sync Shared Proto»

Target BarkCloud → **Build Phases → New Run Script Phase**. Поставить ПЕРЕД «Compile Sources» (перетащить вверх).

- **Name**: `Sync Shared Proto`
- **Shell**: `/bin/bash`
- **Script**: `"${SRCROOT}/sync_proto.sh"`
- Снять галочку «Based on dependency analysis», иначе фаза не будет запускаться каждый раз.
- (Опционально) **Input File Lists** / **Output File Lists** — оставить пустыми.

Скрипт уже создан: `Ios/BarkCloud/sync_proto.sh`. Сделать его исполняемым:
```bash
chmod +x Ios/BarkCloud/sync_proto.sh
```

## 4. Info.plist для ATS exception (dev only)

Backend в dev-окружении работает с self-signed TLS на `localhost:5001`. iOS ATS блокирует это по умолчанию.

Target BarkCloud → **Build Settings → Packaging → Generate Info.plist File** → переключить в `No`. Затем создать файл `Ios/BarkCloud/BarkCloud/Info.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTD/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>ru</string>
    <key>CFBundleExecutable</key>
    <string>$(EXECUTABLE_NAME)</string>
    <key>CFBundleIdentifier</key>
    <string>$(PRODUCT_BUNDLE_IDENTIFIER)</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$(PRODUCT_NAME)</string>
    <key>CFBundlePackageType</key>
    <string>$(PRODUCT_BUNDLE_PACKAGE_TYPE)</string>
    <key>CFBundleShortVersionString</key>
    <string>$(MARKETING_VERSION)</string>
    <key>CFBundleVersion</key>
    <string>$(CURRENT_PROJECT_VERSION)</string>
    <key>LSRequiresIPhoneOS</key>
    <true/>
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsArbitraryLoads</key>
        <true/>
        <key>NSAllowsLocalNetworking</key>
        <true/>
    </dict>
</dict>
</plist>
```

И в Build Settings указать **Info.plist File** = `BarkCloud/Info.plist`.

(Этот шаг можно отложить до PR 2, когда подключится gRPC.)

## 5. Проверка

```bash
cd Ios/BarkCloud
xcodebuild -project BarkCloud.xcodeproj \
  -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 16,OS=18.2' \
  build
```

После шагов 1–3 в логе сборки должны появиться:
- `Sync Shared Proto` (копирование .proto в `BarkCloud/Proto/`)
- `GRPCProtobufGenerator` (генерация Swift-стабов в build-products dir)
- `Compiling Identity_*.swift`, `Users_*.swift`, `Files_*.swift`, `Shared_*.swift` (сгенерённые символы).

Если что-то не собирается — открыть Report Navigator (⌘9) и посмотреть лог соответствующей фазы.
