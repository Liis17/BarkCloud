import SwiftUI
import AppKit
import WidgetKit
import BarkCloudKit

/// Виджет заполнения облачного диска BarkCloud для macOS — размеры
/// `.systemSmall` и `.systemMedium`. Контракт с контейнер-приложением
/// (`StorageWidgetBridge`): три ключа в App Group `UserDefaults`. App Group
/// id виджет читает через `BarkCloudAppGroup.identifier` (на macOS включает
/// TeamID prefix через `INFOPLIST_KEY_BarkCloudAppGroupID`).
struct StorageWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "StorageWidget", provider: StorageProvider()) { entry in
            StorageWidgetEntryView(snapshot: entry.snapshot)
                .containerBackground(for: .widget) {
                    LinearGradient(
                        colors: [Color(nsColor: .windowBackgroundColor),
                                 Color(nsColor: .underPageBackgroundColor)],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                }
        }
        .configurationDisplayName("Хранилище BarkCloud")
        .description("Сколько места занято в облаке.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

// MARK: - Снимок квоты

/// Контракт с main app (`StorageWidgetBridge`): три ключа в App Group UserDefaults.
struct StorageSnapshot {
    let used: Int64
    let limit: Int64

    var hasData: Bool { limit > 0 }
    var fraction: Double {
        guard limit > 0 else { return 0 }
        return min(1, max(0, Double(used) / Double(limit)))
    }
    var percent: Int { Int((fraction * 100).rounded()) }
    var free: Int64 { max(0, limit - used) }

    static let empty = StorageSnapshot(used: 0, limit: 0)
    static let sample = StorageSnapshot(used: 33_285_996_544, limit: 53_687_091_200)

    static func current() -> StorageSnapshot {
        guard let d = UserDefaults(suiteName: BarkCloudAppGroup.identifier),
              d.object(forKey: "storage_widget.limit") != nil else { return .empty }
        return StorageSnapshot(
            used: Int64(d.integer(forKey: "storage_widget.used")),
            limit: Int64(d.integer(forKey: "storage_widget.limit"))
        )
    }
}

// MARK: - Timeline

struct StorageEntry: TimelineEntry {
    let date: Date
    let snapshot: StorageSnapshot
}

struct StorageProvider: TimelineProvider {
    func placeholder(in context: Context) -> StorageEntry {
        StorageEntry(date: .now, snapshot: .sample)
    }

    func getSnapshot(in context: Context, completion: @escaping (StorageEntry) -> Void) {
        let snap = context.isPreview ? .sample : StorageSnapshot.current()
        completion(StorageEntry(date: .now, snapshot: snap))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<StorageEntry>) -> Void) {
        let entry = StorageEntry(date: .now, snapshot: .current())
        // Перечитываем раз в час на случай, если приложение обновило квоту, но не
        // успело дёрнуть reload (главный канал — `reloadTimelines` из main app).
        let next = Calendar.current.date(byAdding: .hour, value: 1, to: .now) ?? .now.addingTimeInterval(3600)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }
}

// MARK: - Палитра

private let accentOrange = Color(red: 1.0, green: 0.46, blue: 0.16)
private let accentOrangeLight = Color(red: 1.0, green: 0.62, blue: 0.27)
private let warnRed = Color(red: 0.96, green: 0.26, blue: 0.21)
private let warnRedLight = Color(red: 1.0, green: 0.42, blue: 0.38)

/// Цвет заполнения зависит от того, насколько диск полон: оранжевый в норме,
/// красный при ≥ 90 % — чтобы «почти полно» считывалось мгновенно.
private func barColors(_ fraction: Double) -> [Color] {
    fraction >= 0.9 ? [warnRedLight, warnRed] : [accentOrangeLight, accentOrange]
}

private func formatBytes(_ bytes: Int64) -> String {
    let f = ByteCountFormatter()
    f.countStyle = .binary
    f.allowedUnits = [.useKB, .useMB, .useGB, .useTB]
    return f.string(fromByteCount: max(0, bytes))
}

// MARK: - Кнопка обновления

/// Полупрозрачная кнопка ручного обновления квоты в углу виджета. Запускает
/// `RefreshStorageIntent` (macOS 14+ interactive widget) — реальный фетч по gRPC.
private struct RefreshButton: View {
    var body: some View {
        Button(intent: RefreshStorageIntent()) {
            Image(systemName: "arrow.clockwise")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(accentOrange.opacity(0.9))
                .frame(width: 24, height: 24)
                .background(accentOrange.opacity(0.12), in: Circle())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Прогресс-бар

/// Капсульный индикатор: мягкий трек + градиентная заливка со скруглёнными
/// торцами. Минимальная видимая ширина — чтобы при 0–1 % не схлопывался в точку.
private struct CapsuleProgressBar: View {
    let fraction: Double

    var body: some View {
        GeometryReader { geo in
            let full = geo.size.width
            let filled = max(fraction > 0 ? geo.size.height : 0, full * CGFloat(min(1, max(0, fraction))))
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Color.primary.opacity(0.1))
                Capsule()
                    .fill(
                        LinearGradient(
                            colors: barColors(fraction),
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
                    .frame(width: filled)
            }
        }
    }
}

// MARK: - Представления

struct StorageWidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: StorageSnapshot

    var body: some View {
        switch family {
        case .systemMedium: MediumStorageView(snapshot: snapshot)
        default: SmallStorageView(snapshot: snapshot)
        }
    }
}

private struct SmallStorageView: View {
    let snapshot: StorageSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Image(systemName: "cloud.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(accentOrange)
                Text("BarkCloud")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                RefreshButton()
            }
            Spacer(minLength: 6)
            if snapshot.hasData {
                Text("\(snapshot.percent)%")
                    .font(.system(size: 36, weight: .bold, design: .rounded))
                    .foregroundStyle(.primary)
                    .minimumScaleFactor(0.7)
                    .lineLimit(1)
                Spacer(minLength: 8)
                CapsuleProgressBar(fraction: snapshot.fraction)
                    .frame(height: 9)
                Spacer(minLength: 6)
                Text("\(formatBytes(snapshot.used)) из \(formatBytes(snapshot.limit))")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            } else {
                Spacer()
                Text("Откройте приложение")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer()
            }
        }
    }
}

private struct MediumStorageView: View {
    let snapshot: StorageSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: "cloud.fill")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(accentOrange)
                Text("Хранилище BarkCloud")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(.primary)
                Spacer()
                if snapshot.hasData {
                    Text("\(snapshot.percent)%")
                        .font(.system(size: 22, weight: .bold, design: .rounded))
                        .foregroundStyle(barColors(snapshot.fraction).last ?? accentOrange)
                }
                RefreshButton()
            }
            if snapshot.hasData {
                CapsuleProgressBar(fraction: snapshot.fraction)
                    .frame(height: 14)
                HStack(alignment: .top) {
                    metric("Занято", formatBytes(snapshot.used))
                    Spacer()
                    metric("Свободно", formatBytes(snapshot.free))
                    Spacer()
                    metric("Всего", formatBytes(snapshot.limit))
                }
            } else {
                Spacer()
                Text("Откройте BarkCloud Drive, чтобы показать заполнение диска")
                    .font(.system(size: 14))
                    .foregroundStyle(.secondary)
                Spacer()
            }
        }
    }

    private func metric(_ title: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Text(value)
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(.primary)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
    }
}

#Preview("Small", as: .systemSmall) {
    StorageWidget()
} timeline: {
    StorageEntry(date: .now, snapshot: .sample)
    StorageEntry(date: .now, snapshot: StorageSnapshot(used: 50_400_000_000, limit: 53_687_091_200))
}

#Preview("Medium", as: .systemMedium) {
    StorageWidget()
} timeline: {
    StorageEntry(date: .now, snapshot: .sample)
}
