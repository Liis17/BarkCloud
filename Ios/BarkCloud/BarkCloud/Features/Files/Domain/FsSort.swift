import Foundation

enum FsSort: String, CaseIterable, Sendable {
    case nameAsc, nameDesc
    case sizeAsc, sizeDesc
    case dateAsc, dateDesc

    var labelKey: LocalizedStringResource {
        switch self {
        case .nameAsc: return "files_sort_name_asc"
        case .nameDesc: return "files_sort_name_desc"
        case .sizeAsc: return "files_sort_size_asc"
        case .sizeDesc: return "files_sort_size_desc"
        case .dateAsc: return "files_sort_date_asc"
        case .dateDesc: return "files_sort_date_desc"
        }
    }
}

func applySort(_ entries: [FsEntry], by sort: FsSort) -> [FsEntry] {
    let directories = entries.filter { $0.isDirectory }
    let files = entries.filter { !$0.isDirectory }
    let dirComparator = directoryComparator(for: sort)
    let fileComparator = fileComparator(for: sort)
    return directories.sorted(by: dirComparator) + files.sorted(by: fileComparator)
}

private func directoryComparator(for sort: FsSort) -> (FsEntry, FsEntry) -> Bool {
    switch sort {
    case .nameAsc, .sizeAsc:
        return { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    case .nameDesc, .sizeDesc:
        return { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedDescending }
    case .dateAsc:
        return { $0.lastModified < $1.lastModified }
    case .dateDesc:
        return { $0.lastModified > $1.lastModified }
    }
}

private func fileComparator(for sort: FsSort) -> (FsEntry, FsEntry) -> Bool {
    switch sort {
    case .nameAsc:
        return { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    case .nameDesc:
        return { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedDescending }
    case .sizeAsc:
        return { sizeOf($0) < sizeOf($1) }
    case .sizeDesc:
        return { sizeOf($0) > sizeOf($1) }
    case .dateAsc:
        return { $0.lastModified < $1.lastModified }
    case .dateDesc:
        return { $0.lastModified > $1.lastModified }
    }
}

private func sizeOf(_ entry: FsEntry) -> Int64 {
    if case .file(let f) = entry { return f.sizeBytes }
    return 0
}
