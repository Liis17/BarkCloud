import SwiftUI
import UIKit
import WidgetKit

/// Виджет недавних облачных фото. Размеры `.systemMedium` (до 4) и `.systemLarge`
/// (до 8). Миниатюры кэширует main app через `RecentMediaWidgetBridge` в App Group
/// (`recent_widget/*.jpg` + манифест); виджет грузит JPEG с диска, в сеть не ходит.
/// Тап открывает таб облачных медиа (`barkcloud://albums`). Превью защищённых
/// файлов сюда не попадают — main app фильтрует их по `VaultStore` до записи.
struct RecentMediaWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "RecentMediaWidget", provider: RecentMediaProvider()) { entry in
            RecentMediaWidgetEntryView(entries: entry.entries)
                .widgetURL(URL(string: "barkcloud://albums"))
                .containerBackground(for: .widget) {
                    Color(uiColor: .systemBackground)
                }
        }
        .configurationDisplayName("Недавние фото")
        .description("Последние фото, загруженные в облако BarkCloud.")
        .supportedFamilies([.systemMedium, .systemLarge])
    }
}

// MARK: - Манифест (контракт с `RecentMediaWidgetBridge`)

struct RecentMediaEntry: Codable {
    let id: String
    let fileName: String
    let isVideo: Bool
    let file: String
}

private enum RecentMediaStore {
    static func current() -> [RecentMediaEntry] {
        guard let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud"),
              let data = d.data(forKey: "recent_widget.manifest"),
              let entries = try? JSONDecoder().decode([RecentMediaEntry].self, from: data)
        else { return [] }
        return entries
    }

    static func directory() -> URL? {
        FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: "group.com.barkfluff.BarkCloud")?
            .appendingPathComponent("recent_widget", isDirectory: true)
    }

    static func image(for entry: RecentMediaEntry) -> UIImage? {
        guard let url = directory()?.appendingPathComponent(entry.file) else { return nil }
        return UIImage(contentsOfFile: url.path)
    }
}

// MARK: - Timeline

struct RecentMediaTimelineEntry: TimelineEntry {
    let date: Date
    let entries: [RecentMediaEntry]
}

struct RecentMediaProvider: TimelineProvider {
    func placeholder(in context: Context) -> RecentMediaTimelineEntry {
        RecentMediaTimelineEntry(date: .now, entries: [])
    }

    func getSnapshot(in context: Context, completion: @escaping (RecentMediaTimelineEntry) -> Void) {
        completion(RecentMediaTimelineEntry(date: .now, entries: RecentMediaStore.current()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<RecentMediaTimelineEntry>) -> Void) {
        let entry = RecentMediaTimelineEntry(date: .now, entries: RecentMediaStore.current())
        let next = Calendar.current.date(byAdding: .hour, value: 2, to: .now) ?? .now.addingTimeInterval(7200)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }
}

// MARK: - Представления

private let accentOrange = Color(red: 1.0, green: 0.46, blue: 0.16)

struct RecentMediaWidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    let entries: [RecentMediaEntry]

    private var maxCount: Int { family == .systemLarge ? 8 : 4 }
    private let fallbackURL = URL(string: "barkcloud://albums")!

    var body: some View {
        if entries.isEmpty {
            emptyState
        } else {
            collage(Array(entries.prefix(maxCount)))
        }
    }

    /// Коллаж равными ячейками, заполняющий весь виджет: medium — один ряд,
    /// large — два сбалансированных ряда (например, 5 фото → 3+2). Размер ячейки
    /// задаёт контейнер (HStack делит ширину поровну), фото вписывается через
    /// `scaledToFill` в overlay и обрезается по ячейке — наезды исключены.
    private func collage(_ shown: [RecentMediaEntry]) -> some View {
        let rows: [[RecentMediaEntry]]
        if family == .systemLarge && shown.count > 1 {
            let top = (shown.count + 1) / 2
            rows = [Array(shown.prefix(top)), Array(shown.dropFirst(top))]
        } else {
            rows = [shown]
        }
        return VStack(spacing: 4) {
            ForEach(rows.indices, id: \.self) { rowIndex in
                HStack(spacing: 4) {
                    ForEach(rows[rowIndex], id: \.id) { entry in
                        Link(destination: URL(string: "barkcloud://media/\(entry.id)") ?? fallbackURL) {
                            cell(entry)
                        }
                    }
                }
            }
        }
    }

    private func cell(_ entry: RecentMediaEntry) -> some View {
        Color(uiColor: .secondarySystemBackground)
            .overlay {
                if let image = RecentMediaStore.image(for: entry) {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                } else {
                    Image(systemName: "photo")
                        .foregroundStyle(.secondary)
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if entry.isVideo {
                    Image(systemName: "play.fill")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.white)
                        .padding(5)
                        .background(.black.opacity(0.45), in: Circle())
                        .padding(4)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "photo.on.rectangle.angled")
                .font(.system(size: 28))
                .foregroundStyle(accentOrange)
            Text("Загрузите фото в облако")
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
