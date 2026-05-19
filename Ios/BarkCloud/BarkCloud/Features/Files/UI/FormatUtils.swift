import Foundation

enum FormatUtils {
    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "dd.MM.yyyy"
        return f
    }()

    static func formatSize(_ bytes: Int64) -> String {
        if bytes < 1024 {
            return String(localized: "files_size_b").replacingOccurrences(of: "%lld", with: "\(bytes)")
        }
        let kb = Double(bytes) / 1024.0
        if kb < 1024 {
            return String(format: NSLocalizedString("files_size_kb", comment: ""), kb)
        }
        let mb = kb / 1024.0
        if mb < 1024 {
            return String(format: NSLocalizedString("files_size_mb", comment: ""), mb)
        }
        let gb = mb / 1024.0
        return String(format: NSLocalizedString("files_size_gb", comment: ""), gb)
    }

    static func formatChildCount(_ count: Int) -> String {
        if count == 0 { return String(localized: "files_items_count_zero") }
        let mod10 = count % 10
        let mod100 = count % 100
        let key: String
        if mod100 >= 11 && mod100 <= 14 {
            key = "files_items_count_many"
        } else if mod10 == 1 {
            key = "files_items_count_one"
        } else if mod10 >= 2 && mod10 <= 4 {
            key = "files_items_count_few"
        } else {
            key = "files_items_count_many"
        }
        return String(format: NSLocalizedString(key, comment: ""), count)
    }

    static func formatDate(_ date: Date) -> String {
        dateFormatter.string(from: date)
    }
}
