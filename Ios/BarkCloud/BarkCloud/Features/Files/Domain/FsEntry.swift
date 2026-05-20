import Foundation

enum FsEntry: Hashable, Identifiable, Sendable {
    case directory(Directory)
    case file(File)

    struct Directory: Hashable, Sendable {
        var path: String
        var name: String
        var lastModified: Date
        var childCount: Int
    }

    struct File: Hashable, Sendable {
        var path: String
        var name: String
        var lastModified: Date
        var sizeBytes: Int64
        var mimeType: String
    }

    var id: String { path }

    var path: String {
        switch self {
        case .directory(let d): return d.path
        case .file(let f): return f.path
        }
    }

    var name: String {
        switch self {
        case .directory(let d): return d.name
        case .file(let f): return f.name
        }
    }

    var lastModified: Date {
        switch self {
        case .directory(let d): return d.lastModified
        case .file(let f): return f.lastModified
        }
    }

    var isDirectory: Bool {
        if case .directory = self { return true }
        return false
    }
}
