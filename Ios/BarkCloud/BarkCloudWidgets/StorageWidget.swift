import SwiftUI
import UIKit
import WidgetKit

/// Виджет заполнения физического диска BarkCloud — размеры `.systemSmall` (1×1) и
/// `.systemMedium` (1×2). Данные кладёт main app через `StorageWidgetBridge` в
/// App Group `UserDefaults`; здесь они только читаются (виджет в gRPC не ходит).
struct StorageWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "StorageWidget", provider: StorageProvider()) { entry in
            StorageWidgetEntryView(snapshot: entry.snapshot)
                .containerBackground(for: .widget) {
                    LinearGradient(
                        colors: [Color(uiColor: .systemBackground), Color(uiColor: .secondarySystemBackground)],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                }
        }
        .configurationDisplayName("Хранилище BarkCloud")
        .description("Сколько места занято на диске сервера.")
        .supportedFamilies([
            .systemSmall, .systemMedium,
            .accessoryCircular, .accessoryRectangular, .accessoryInline
        ])
    }
}

// MARK: - Снимок хранилища

/// Контракт с main app (`StorageWidgetBridge`): физический диск сервера плюс
/// старые `used/limit` как fallback для уже сохранённых снимков.
struct StorageSnapshot {
    let used: Int64
    let limit: Int64
    let diskTotal: Int64
    let diskOther: Int64
    let diskS3: Int64

    var hasData: Bool { total > 0 }
    var total: Int64 { diskTotal > 0 ? diskTotal : limit }
    var other: Int64 { diskTotal > 0 ? max(0, diskOther) : 0 }
    var s3: Int64 { diskTotal > 0 ? max(0, diskS3) : max(0, used) }
    var usedOnDisk: Int64 { other + s3 }
    var free: Int64 { max(0, total - usedOnDisk) }
    var fraction: Double {
        guard total > 0 else { return 0 }
        return min(1, max(0, Double(usedOnDisk) / Double(total)))
    }
    var percent: Int { Int((fraction * 100).rounded()) }
    var otherShare: Double { share(other) }
    var s3Share: Double { share(s3) }

    static let empty = StorageSnapshot(used: 0, limit: 0, diskTotal: 0, diskOther: 0, diskS3: 0)
    static let sample = StorageSnapshot(
        used: 33_285_996_544,
        limit: 53_687_091_200,
        diskTotal: 128_849_018_880,
        diskOther: 41_943_040_000,
        diskS3: 33_285_996_544
    )

    static func current() -> StorageSnapshot {
        guard let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud"),
              d.object(forKey: "storage_widget.diskTotal") != nil || d.object(forKey: "storage_widget.limit") != nil else { return .empty }
        return StorageSnapshot(
            used: Int64(d.integer(forKey: "storage_widget.used")),
            limit: Int64(d.integer(forKey: "storage_widget.limit")),
            diskTotal: Int64(d.integer(forKey: "storage_widget.diskTotal")),
            diskOther: Int64(d.integer(forKey: "storage_widget.diskOther")),
            diskS3: Int64(d.integer(forKey: "storage_widget.diskS3"))
        )
    }

    private func share(_ value: Int64) -> Double {
        let denominator = max(total, usedOnDisk, 1)
        return min(1, max(0, Double(value) / Double(denominator)))
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
        // Перечитываем раз в час на случай, если приложение обновило хранилище, но не
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
private let diskOtherColor = Color(uiColor: .secondaryLabel)
private let diskS3Color = Color(red: 0.604, green: 0.310, blue: 0.118)
private let diskFreeColor = Color.primary.opacity(0.1)

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

/// Полупрозрачная кнопка ручного обновления хранилища в углу виджета. Запускает
/// `RefreshStorageIntent` (iOS 17+ interactive widget) — реальный фетч по gRPC.
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

/// Сегментированный индикатор физического диска: другие данные, S3 и свободное.
private struct SegmentedStorageBar: View {
    let snapshot: StorageSnapshot

    var body: some View {
        GeometryReader { geo in
            let full = geo.size.width
            HStack(spacing: 0) {
                Rectangle()
                    .fill(diskOtherColor)
                    .frame(width: full * CGFloat(snapshot.otherShare))
                Rectangle()
                    .fill(diskS3Color)
                    .frame(width: full * CGFloat(snapshot.s3Share))
                Spacer(minLength: 0)
            }
        }
        .background(diskFreeColor)
        .clipShape(Capsule())
    }
}

// MARK: - Представления

struct StorageWidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: StorageSnapshot

    var body: some View {
        switch family {
        case .systemMedium: MediumStorageView(snapshot: snapshot)
        case .accessoryCircular: AccessoryCircularStorageView(snapshot: snapshot)
        case .accessoryRectangular: AccessoryRectangularStorageView(snapshot: snapshot)
        case .accessoryInline: AccessoryInlineStorageView(snapshot: snapshot)
        default: SmallStorageView(snapshot: snapshot)
        }
    }
}

// MARK: - Lock Screen / accessory представления

/// Круговой замочный аксессуар (Lock Screen): кольцо-`Gauge` с процентом внутри.
/// Система сама применяет vibrant-тонирование, поэтому собственные цвета не задаём.
private struct AccessoryCircularStorageView: View {
    let snapshot: StorageSnapshot

    var body: some View {
        Gauge(value: snapshot.fraction) {
            Image(systemName: "cloud.fill")
        } currentValueLabel: {
            Text(snapshot.hasData ? "\(snapshot.percent)" : "–")
        }
        .gaugeStyle(.accessoryCircularCapacity)
    }
}

/// Прямоугольный аксессуар: заголовок + процент + полоса заполнения.
private struct AccessoryRectangularStorageView: View {
    let snapshot: StorageSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Label("BarkCloud", systemImage: "cloud.fill")
                .font(.caption2)
                .widgetAccentable()
            if snapshot.hasData {
                Text("\(snapshot.percent)% занято")
                    .font(.headline)
                Gauge(value: snapshot.fraction) { EmptyView() }
                    .gaugeStyle(.accessoryLinearCapacity)
                Text("S3 \(formatBytes(snapshot.s3)) · своб. \(formatBytes(snapshot.free))")
                    .font(.caption2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            } else {
                Text("Откройте приложение")
                    .font(.caption)
            }
        }
    }
}

/// Строчный аксессуар (над часами): иконка + короткий процент.
private struct AccessoryInlineStorageView: View {
    let snapshot: StorageSnapshot

    var body: some View {
        if snapshot.hasData {
            Label("BarkCloud \(snapshot.percent)%", systemImage: "cloud.fill")
        } else {
            Label("BarkCloud", systemImage: "cloud.fill")
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
                SegmentedStorageBar(snapshot: snapshot)
                    .frame(height: 9)
                Spacer(minLength: 6)
                Text("Др. \(formatBytes(snapshot.other)) · S3 \(formatBytes(snapshot.s3))")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                Text("Своб. \(formatBytes(snapshot.free))")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
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
                Text("\(formatBytes(snapshot.usedOnDisk)) из \(formatBytes(snapshot.total)) на диске")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                SegmentedStorageBar(snapshot: snapshot)
                    .frame(height: 12)
                HStack(alignment: .top) {
                    metric("Другие", formatBytes(snapshot.other))
                    Spacer()
                    metric("S3", formatBytes(snapshot.s3))
                    Spacer()
                    metric("Свободно", formatBytes(snapshot.free))
                }
            } else {
                Spacer()
                Text("Откройте BarkCloud, чтобы показать заполнение диска")
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
    StorageEntry(
        date: .now,
        snapshot: StorageSnapshot(
            used: 50_400_000_000,
            limit: 53_687_091_200,
            diskTotal: 128_849_018_880,
            diskOther: 58_100_000_000,
            diskS3: 50_400_000_000
        )
    )
}

#Preview("Medium", as: .systemMedium) {
    StorageWidget()
} timeline: {
    StorageEntry(date: .now, snapshot: .sample)
}
