import Foundation
import SwiftUI

struct MediaDateSection<Item>: Identifiable {
    let day: Date
    let title: String
    var items: [Item]

    var id: Date { day }
}

enum MediaDateSections {
    static func make<Item>(
        from items: [Item],
        calendar: Calendar = .current,
        date: (Item) -> Date
    ) -> [MediaDateSection<Item>] {
        var sections: [MediaDateSection<Item>] = []
        var indexByDay: [Date: Int] = [:]
        let now = Date()

        for item in items {
            let day = calendar.startOfDay(for: date(item))
            if let index = indexByDay[day] {
                sections[index].items.append(item)
            } else {
                indexByDay[day] = sections.count
                sections.append(MediaDateSection(
                    day: day,
                    title: title(for: day, calendar: calendar, now: now),
                    items: [item]
                ))
            }
        }

        return sections
    }

    private static func title(for day: Date, calendar: Calendar, now: Date) -> String {
        if calendar.isDateInToday(day)
            || calendar.isDateInYesterday(day)
            || calendar.isDateInTomorrow(day) {
            let formatter = DateFormatter()
            formatter.calendar = calendar
            formatter.locale = .current
            formatter.dateStyle = .medium
            formatter.doesRelativeDateFormatting = true
            return capitalizedFirst(formatter.string(from: day))
        }

        return day.formatted(.dateTime.day().month(.wide))
    }

    private static func capitalizedFirst(_ text: String) -> String {
        guard let first = text.first else { return text }
        return String(first).uppercased(with: Locale.current) + text.dropFirst()
    }
}

struct MediaDateSectionHeader: View {
    let title: String

    var body: some View {
        Text(verbatim: title)
            .font(AppTypography.titleSmall)
            .foregroundStyle(AppColors.onSurface)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 6)
    }
}
