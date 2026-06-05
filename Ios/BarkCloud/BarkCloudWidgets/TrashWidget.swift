import AppIntents
import SwiftUI
import WidgetKit

/// Виджет корзины облака BarkCloud: сколько файлов ждёт авто-удаления. Размеры
/// `.systemSmall`, `.accessoryRectangular`, `.accessoryCircular`. Данные кладёт
/// main app через `TrashWidgetBridge` (или `RefreshTrashIntent`) в App Group
/// `UserDefaults`; виджет в gRPC не ходит. Тап открывает таб «Корзина»
/// (`barkcloud://trash`). Если корзина помещается в одну страницу, показываем
/// реальный отсчёт до ближайшего удаления (`min(purgeAt)`); иначе — статичную
/// подсказку про 14-дневный срок хранения (`TrashPurgeService.Retention`).
struct TrashWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "TrashWidget", provider: TrashProvider()) { entry in
            TrashWidgetEntryView(snapshot: entry.snapshot)
                .widgetURL(URL(string: "barkcloud://trash"))
                .containerBackground(for: .widget) {
                    LinearGradient(
                        colors: [Color(uiColor: .systemBackground), Color(uiColor: .secondarySystemBackground)],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                }
        }
        .configurationDisplayName("Корзина BarkCloud")
        .description("Сколько файлов лежит в корзине облака.")
        .supportedFamilies([.systemSmall, .accessoryRectangular, .accessoryCircular])
    }
}

// MARK: - Снимок

/// Контракт с main app (`TrashWidgetBridge`): два ключа в App Group UserDefaults.
/// Данные приходят из `CloudApi.GetTrashSummary` — счётчик точный, дедлайн всегда
/// соответствует самому истекающему файлу.
struct TrashSnapshot {
    let count: Int
    /// Ближайшая дата авто-удаления (самый старый элемент). `nil` = корзина пуста.
    let nearestPurgeAt: Date?

    var isEmpty: Bool { count == 0 }
    var countLabel: String { "\(count)" }

    /// Отсчёт до ближайшего удаления, если известен. Считается при рендере таймлайна.
    var deadlineText: String? {
        guard let purge = nearestPurgeAt else { return nil }
        let secs = purge.timeIntervalSinceNow
        guard secs > 0 else { return "Удаляются сейчас" }
        let days = max(1, Int(ceil(secs / 86_400)))
        return "Удалятся через \(days) дн."
    }

    static let sample = TrashSnapshot(count: 7, nearestPurgeAt: Date().addingTimeInterval(3 * 86_400))

    static func current() -> TrashSnapshot {
        guard let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud") else {
            return TrashSnapshot(count: 0, nearestPurgeAt: nil)
        }
        let ts = d.double(forKey: "trash_widget.purgeAt")
        return TrashSnapshot(
            count: d.integer(forKey: "trash_widget.count"),
            nearestPurgeAt: ts > 0 ? Date(timeIntervalSince1970: ts) : nil
        )
    }
}

// MARK: - Timeline

struct TrashEntry: TimelineEntry {
    let date: Date
    let snapshot: TrashSnapshot
}

struct TrashProvider: TimelineProvider {
    func placeholder(in context: Context) -> TrashEntry {
        TrashEntry(date: .now, snapshot: .sample)
    }

    func getSnapshot(in context: Context, completion: @escaping (TrashEntry) -> Void) {
        let snap = context.isPreview ? .sample : TrashSnapshot.current()
        completion(TrashEntry(date: .now, snapshot: snap))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<TrashEntry>) -> Void) {
        let entry = TrashEntry(date: .now, snapshot: .current())
        let next = Calendar.current.date(byAdding: .hour, value: 6, to: .now) ?? .now.addingTimeInterval(21_600)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }
}

// MARK: - Представления

private let accentOrange = Color(red: 1.0, green: 0.46, blue: 0.16)

struct TrashWidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: TrashSnapshot

    var body: some View {
        switch family {
        case .accessoryCircular: AccessoryCircularTrashView(snapshot: snapshot)
        case .accessoryRectangular: AccessoryRectangularTrashView(snapshot: snapshot)
        default: SmallTrashView(snapshot: snapshot)
        }
    }
}

private struct SmallTrashView: View {
    let snapshot: TrashSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Image(systemName: "trash.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(accentOrange)
                Text("Корзина")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                Button(intent: RefreshTrashIntent()) {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(accentOrange.opacity(0.9))
                        .frame(width: 24, height: 24)
                        .background(accentOrange.opacity(0.12), in: Circle())
                }
                .buttonStyle(.plain)
            }
            Spacer(minLength: 6)
            if snapshot.isEmpty {
                Spacer()
                Text("Корзина пуста")
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(.secondary)
                Spacer()
            } else {
                Text(snapshot.countLabel)
                    .font(.system(size: 40, weight: .bold, design: .rounded))
                    .foregroundStyle(.primary)
                    .minimumScaleFactor(0.7)
                    .lineLimit(1)
                Text(snapshot.count == 1 ? "файл" : "файлов")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                Spacer(minLength: 8)
                Text(snapshot.deadlineText ?? "Удаляются через 14 дней")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
        }
    }
}

private struct AccessoryCircularTrashView: View {
    let snapshot: TrashSnapshot

    var body: some View {
        VStack(spacing: 1) {
            Image(systemName: "trash.fill")
                .font(.system(size: 12, weight: .semibold))
            Text(snapshot.isEmpty ? "0" : snapshot.countLabel)
                .font(.system(size: 16, weight: .bold, design: .rounded))
        }
        .widgetAccentable()
    }
}

private struct AccessoryRectangularTrashView: View {
    let snapshot: TrashSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Label("Корзина", systemImage: "trash.fill")
                .font(.caption2)
                .widgetAccentable()
            if snapshot.isEmpty {
                Text("Пусто")
                    .font(.headline)
            } else {
                Text("\(snapshot.countLabel) файлов")
                    .font(.headline)
                Text(snapshot.deadlineText ?? "Удаляются через 14 дней")
                    .font(.caption2)
            }
        }
    }
}

#Preview("Small", as: .systemSmall) {
    TrashWidget()
} timeline: {
    TrashEntry(date: .now, snapshot: .sample)
    TrashEntry(date: .now, snapshot: TrashSnapshot(count: 0, nearestPurgeAt: nil))
}
