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

    private var columns: Int { family == .systemLarge ? 4 : 4 }
    private var maxCount: Int { family == .systemLarge ? 8 : 4 }

    var body: some View {
        if entries.isEmpty {
            emptyState
        } else {
            let shown = Array(entries.prefix(maxCount))
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: columns), spacing: 4) {
                ForEach(shown, id: \.id) { entry in
                    cell(entry)
                }
            }
        }
    }

    private func cell(_ entry: RecentMediaEntry) -> some View {
        ZStack(alignment: .bottomTrailing) {
            if let image = RecentMediaStore.image(for: entry) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                Color(uiColor: .secondarySystemBackground)
                    .overlay {
                        Image(systemName: "photo")
                            .foregroundStyle(.secondary)
                    }
            }
            if entry.isVideo {
                Image(systemName: "play.circle.fill")
                    .font(.system(size: 12))
                    .foregroundStyle(.white)
                    .padding(3)
            }
        }
        .aspectRatio(1, contentMode: .fill)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
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
