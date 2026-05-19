import Foundation
import UniformTypeIdentifiers

actor LocalFileRepository {
    struct OpError: Error, Equatable {
        var messageKey: String
        var detail: String?
    }

    private let fm = FileManager.default

    func documentsRoot() -> URL {
        fm.urls(for: .documentDirectory, in: .userDomainMask).first!
    }

    func list(at path: String, includeHidden: Bool) async throws -> [FsEntry] {
        let url = URL(fileURLWithPath: path)
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey, .nameKey]
        let options: FileManager.DirectoryEnumerationOptions = includeHidden ? [] : [.skipsHiddenFiles]
        let contents = try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: Array(keys), options: options)
        return contents.compactMap { entryURL -> FsEntry? in
            let values = try? entryURL.resourceValues(forKeys: keys)
            let name = values?.name ?? entryURL.lastPathComponent
            let modified = values?.contentModificationDate ?? Date()
            let isDir = values?.isDirectory ?? false
            if isDir {
                let childCount = (try? fm.contentsOfDirectory(atPath: entryURL.path).count) ?? 0
                return .directory(.init(path: entryURL.path, name: name, lastModified: modified, childCount: childCount))
            } else {
                let size = Int64(values?.fileSize ?? 0)
                let mime = MimeIcon.mime(forFileName: name)
                return .file(.init(path: entryURL.path, name: name, lastModified: modified, sizeBytes: size, mimeType: mime))
            }
        }
    }

    func createDir(parentPath: String, name: String) async throws {
        try validate(name: name)
        let target = URL(fileURLWithPath: parentPath).appendingPathComponent(name)
        if fm.fileExists(atPath: target.path) {
            throw OpError(messageKey: "files_op_error_exists", detail: nil)
        }
        try fm.createDirectory(at: target, withIntermediateDirectories: false)
    }

    func rename(entry: FsEntry, newName: String) async throws {
        try validate(name: newName)
        let src = URL(fileURLWithPath: entry.path)
        let dst = src.deletingLastPathComponent().appendingPathComponent(newName)
        if fm.fileExists(atPath: dst.path) {
            throw OpError(messageKey: "files_op_error_exists", detail: nil)
        }
        try fm.moveItem(at: src, to: dst)
    }

    func delete(entries: [FsEntry]) async throws {
        for entry in entries {
            try fm.removeItem(atPath: entry.path)
        }
    }

    func copy(entries: [FsEntry], to targetDir: String, onProgress: @Sendable (Double) -> Void) async throws {
        for (idx, entry) in entries.enumerated() {
            let src = URL(fileURLWithPath: entry.path)
            let dst = URL(fileURLWithPath: targetDir).appendingPathComponent(entry.name)
            if src.path == dst.path { continue }
            if dst.path.hasPrefix(src.path + "/") {
                throw OpError(messageKey: "files_op_error_generic", detail: "loop")
            }
            try fm.copyItem(at: src, to: dst)
            onProgress(Double(idx + 1) / Double(entries.count))
        }
    }

    func move(entries: [FsEntry], to targetDir: String, onProgress: @Sendable (Double) -> Void) async throws {
        for (idx, entry) in entries.enumerated() {
            let src = URL(fileURLWithPath: entry.path)
            let dst = URL(fileURLWithPath: targetDir).appendingPathComponent(entry.name)
            if src.path == dst.path { continue }
            if dst.path.hasPrefix(src.path + "/") {
                throw OpError(messageKey: "files_op_error_generic", detail: "loop")
            }
            do {
                try fm.moveItem(at: src, to: dst)
            } catch {
                try fm.copyItem(at: src, to: dst)
                try fm.removeItem(at: src)
            }
            onProgress(Double(idx + 1) / Double(entries.count))
        }
    }

    private func validate(name: String) throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed.contains("/") || trimmed.contains("\\") {
            throw OpError(messageKey: "files_op_error_invalid_name", detail: nil)
        }
    }
}
