import SwiftUI
import WidgetKit

/// Виджет «сейфа» (приватного раздела за биометрией). Размеры `.systemSmall` и
/// `.accessoryCircular`. Тап открывает сейф через таб «Настройки»
/// (`barkcloud://vault`). Число элементов — privacy opt-in (`VaultWidgetBridge`):
/// если показ выключен, виджет рисует только замок. Превью контента не показываем
/// никогда — он остаётся за биометрией.
struct VaultWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "VaultWidget", provider: VaultProvider()) { entry in
            VaultWidgetEntryView(snapshot: entry.snapshot)
                .widgetURL(URL(string: "barkcloud://vault"))
                .containerBackground(for: .widget) {
                    LinearGradient(
                        colors: [Color(uiColor: .systemBackground), Color(uiColor: .secondarySystemBackground)],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                }
        }
        .configurationDisplayName("Сейф BarkCloud")
        .description("Быстрый вход в защищённые фото и видео.")
        .supportedFamilies([.systemSmall, .accessoryCircular])
    }
}

// MARK: - Снимок

/// Контракт с main app (`VaultWidgetBridge`): счётчик + флаг показа числа.
struct VaultSnapshot {
    let count: Int
    /// Показывать ли число (privacy opt-in). Если `false` — только замок.
    let showsCount: Bool

    var countText: String? { showsCount ? "\(count)" : nil }

    static let sample = VaultSnapshot(count: 18, showsCount: true)

    static func current() -> VaultSnapshot {
        guard let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud") else {
            return VaultSnapshot(count: 0, showsCount: false)
        }
        return VaultSnapshot(
            count: d.integer(forKey: "vault_widget.count"),
            showsCount: d.bool(forKey: "vault_widget.enabled")
        )
    }
}

// MARK: - Timeline

struct VaultEntry: TimelineEntry {
    let date: Date
    let snapshot: VaultSnapshot
}

struct VaultProvider: TimelineProvider {
    func placeholder(in context: Context) -> VaultEntry {
        VaultEntry(date: .now, snapshot: .sample)
    }

    func getSnapshot(in context: Context, completion: @escaping (VaultEntry) -> Void) {
        let snap = context.isPreview ? .sample : VaultSnapshot.current()
        completion(VaultEntry(date: .now, snapshot: snap))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<VaultEntry>) -> Void) {
        completion(Timeline(entries: [VaultEntry(date: .now, snapshot: .current())], policy: .never))
    }
}

// MARK: - Представления

private let accentOrange = Color(red: 1.0, green: 0.46, blue: 0.16)

struct VaultWidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: VaultSnapshot

    var body: some View {
        switch family {
        case .accessoryCircular: AccessoryCircularVaultView(snapshot: snapshot)
        default: SmallVaultView(snapshot: snapshot)
        }
    }
}

private struct SmallVaultView: View {
    let snapshot: VaultSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Image(systemName: "lock.shield.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(accentOrange)
                Text("Сейф")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if let countText = snapshot.countText {
                Text(countText)
                    .font(.system(size: 40, weight: .bold, design: .rounded))
                    .foregroundStyle(.primary)
                    .minimumScaleFactor(0.7)
                    .lineLimit(1)
                Text(snapshot.count == 1 ? "элемент" : "элементов")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
            } else {
                Image(systemName: "lock.fill")
                    .font(.system(size: 34, weight: .semibold))
                    .foregroundStyle(accentOrange)
                Spacer(minLength: 6)
                Text("Открыть сейф")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
    }
}

private struct AccessoryCircularVaultView: View {
    let snapshot: VaultSnapshot

    var body: some View {
        VStack(spacing: 1) {
            Image(systemName: "lock.shield.fill")
                .font(.system(size: 12, weight: .semibold))
            if let countText = snapshot.countText {
                Text(countText)
                    .font(.system(size: 16, weight: .bold, design: .rounded))
            }
        }
        .widgetAccentable()
    }
}

#Preview("Small", as: .systemSmall) {
    VaultWidget()
} timeline: {
    VaultEntry(date: .now, snapshot: .sample)
    VaultEntry(date: .now, snapshot: VaultSnapshot(count: 18, showsCount: false))
}
