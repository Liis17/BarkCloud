import AppIntents
import SwiftUI
import WidgetKit

/// Контрол Пункта управления (iOS 18+): показывает текущий процент заполнения
/// диска и по тапу запускает `RefreshStorageIntent` — тот же фетч по gRPC,
/// что и кнопка на виджете хранилища. Значение читается из App Group через
/// `StorageSnapshot.current()` (см. `StorageWidget.swift`).
struct StorageControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "StorageControl", provider: Provider()) { value in
            ControlWidgetButton(action: RefreshStorageIntent()) {
                Label(value.hasData ? "Диск \(value.percent)%" : "Диск BarkCloud", systemImage: "cloud.fill")
            }
        }
        .displayName("Квота BarkCloud")
        .description("Заполнение диска BarkCloud. Тап — обновить.")
    }

    struct Provider: ControlValueProvider {
        var previewValue: StorageSnapshot { .sample }

        func currentValue() async throws -> StorageSnapshot {
            StorageSnapshot.current()
        }
    }
}
