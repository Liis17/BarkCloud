import Foundation

public enum MultipartBodyBuilderError: Error {
    case sourceFileMissing
    case destinationDirectoryFailed
    case destinationFileFailed
}

/// Готовит `multipart/form-data`-тело как файл на диске. Background URLSession
/// принимает только файл (`uploadTask(with:fromFile:)`), а Data-вариант недоступен —
/// поэтому тело собирается стримом: header → байты оригинала чанками → footer.
/// Это позволяет грузить большие видео без раздувания RAM.
public enum MultipartBodyBuilder {
    /// Записать multipart body в `destination`. Возвращает суммарный размер
    /// результирующего файла (header + payload + footer) для статистики прогресса.
    @discardableResult
    public static func writeMultipartFile(
        boundary: String,
        fieldName: String = "file",
        fileName: String,
        mimeType: String,
        sourceFile: URL,
        destination: URL
    ) throws -> Int64 {
        let fm = FileManager.default
        guard fm.fileExists(atPath: sourceFile.path) else {
            throw MultipartBodyBuilderError.sourceFileMissing
        }
        let destDir = destination.deletingLastPathComponent()
        do {
            try fm.createDirectory(at: destDir, withIntermediateDirectories: true)
        } catch {
            throw MultipartBodyBuilderError.destinationDirectoryFailed
        }
        try? fm.removeItem(at: destination)
        guard fm.createFile(atPath: destination.path, contents: nil) else {
            throw MultipartBodyBuilderError.destinationFileFailed
        }

        let output = try FileHandle(forWritingTo: destination)
        defer { try? output.close() }

        let header = makeHeader(boundary: boundary, fieldName: fieldName, fileName: fileName, mimeType: mimeType)
        try output.write(contentsOf: header)

        let input = try FileHandle(forReadingFrom: sourceFile)
        defer { try? input.close() }

        let chunkSize = 64 * 1024
        while true {
            let chunk = input.readData(ofLength: chunkSize)
            if chunk.isEmpty { break }
            try output.write(contentsOf: chunk)
        }

        let footer = makeFooter(boundary: boundary)
        try output.write(contentsOf: footer)

        let attrs = try fm.attributesOfItem(atPath: destination.path)
        return (attrs[.size] as? Int64) ?? 0
    }

    private static func makeHeader(boundary: String, fieldName: String, fileName: String, mimeType: String) -> Data {
        var s = ""
        s += "--\(boundary)\r\n"
        s += "Content-Disposition: form-data; name=\"\(fieldName)\"; filename=\"\(escape(fileName))\"\r\n"
        s += "Content-Type: \(mimeType)\r\n"
        s += "\r\n"
        return Data(s.utf8)
    }

    private static func makeFooter(boundary: String) -> Data {
        Data("\r\n--\(boundary)--\r\n".utf8)
    }

    /// Простое экранирование двойных кавычек и переводов строк в filename,
    /// чтобы не сломать формат заголовка.
    private static func escape(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
    }
}
