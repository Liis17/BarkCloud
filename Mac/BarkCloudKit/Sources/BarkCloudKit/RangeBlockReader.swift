import Foundation

/// Поблочное чтение файлов облака по HTTP **Range** — порт логики
/// `Drive/BarkCloud.Drive.Engine/CloudGateway.cs` (`ReadBlocks`/`EnsureBlock`/
/// `FetchBlockAsync`/`ReadWhole`) на Swift для FSKit-тома macOS.
///
/// Чтение НЕ гидрирует файл целиком: запрошенный диапазон режется на блоки по
/// 1 МиБ, недостающие блоки тянутся `Range`-GET'ом по временной download-URL и
/// кэшируются на диске файлами `{fileID}.blocks/{N}.blk`. Если сервер не отвечает
/// `206 Partial Content` (Range не поддержан) — разовый откат на скачивание файла
/// целиком, как в Windows-движке.
///
/// Бэкенд готов: `Backend/BarkCloud.Files/Host/FilesController.cs` отдаёт 206 с
/// `Content-Range`/`Accept-Ranges` (`S3Uploader.DownloadRangeAsync` → нативный
/// ByteRange MinIO/S3); temp-URL живёт ~60 мин и не одноразовая.
public actor RangeBlockReader {
    /// Размер блока чтения. Совпадает с Windows-движком (`CloudGateway`).
    private static let blockSize = 1 << 20 // 1 МиБ

    /// TTL кэша временной download-URL. Серверная ссылка живёт ~60 мин и не
    /// одноразовая — по ней можно слать много Range-запросов; берём с запасом 50.
    private static let urlTTL: TimeInterval = 50 * 60

    private let transfer: FileTransferService
    private let cacheDir: URL

    /// Кэш временных download-URL: fileID → (url, когда получена).
    private var tempURLs: [String: (url: URL, fetchedAt: Date)] = [:]
    /// Файлы, для которых сервер не поддержал Range — читаем целиком.
    private var wholeMode: Set<String> = []
    /// Дедуп параллельных загрузок одного блока: ключ "fileID#index" → задача.
    private var blockFetches: [String: Task<URL, Error>] = [:]
    /// Дедуп параллельной гидрации целиком: fileID → задача со ссылкой на копию.
    private var wholeFetches: [String: Task<URL, Error>] = [:]

    public init(transfer: FileTransferService, cacheDir: URL) {
        self.transfer = transfer
        self.cacheDir = cacheDir
    }

    /// Прочитать до `length` байт файла `fileID` (полный размер `fileLength`,
    /// берётся из листинга — `CloudFile.fileSize`) начиная с `offset`. Возвращает
    /// фактически прочитанные байты (у конца файла может быть меньше `length`).
    public func read(fileID: String, fileLength: Int64, offset: Int64, length: Int) async throws -> Data {
        guard offset < fileLength, length > 0 else { return Data() }
        let end = min(offset + Int64(length), fileLength) // эксклюзивная граница

        if wholeMode.contains(fileID) {
            return try await readWhole(fileID: fileID, offset: offset, end: end)
        }

        do {
            return try await readBlocks(fileID: fileID, fileLength: fileLength, offset: offset, end: end)
        } catch is RangeUnsupportedError {
            wholeMode.insert(fileID)
            return try await readWhole(fileID: fileID, offset: offset, end: end)
        }
    }

    // MARK: - Поблочное чтение

    private func readBlocks(fileID: String, fileLength: Int64, offset: Int64, end: Int64) async throws -> Data {
        var result = Data(capacity: Int(end - offset))
        var pos = offset
        while pos < end {
            let index = Int(pos / Int64(Self.blockSize))
            let blockStart = Int64(index) * Int64(Self.blockSize)
            let blockURL = try await ensureBlock(fileID: fileID, index: index, fileLength: fileLength)
            let within = Int(pos - blockStart)
            let block = try Data(contentsOf: blockURL, options: .mappedIfSafe)
            guard within < block.count else { break } // защита от рассинхрона размеров
            let take = min(block.count - within, Int(end - pos))
            result.append(block.subdata(in: within ..< within + take))
            pos += Int64(take)
        }
        return result
    }

    private func ensureBlock(fileID: String, index: Int, fileLength: Int64) async throws -> URL {
        let blockURL = blockPath(fileID: fileID, index: index)
        if FileManager.default.fileExists(atPath: blockURL.path) { return blockURL }

        let key = "\(fileID)#\(index)"
        if let inflight = blockFetches[key] { return try await inflight.value }

        let task = Task<URL, Error> {
            try await self.fetchBlock(fileID: fileID, index: index, fileLength: fileLength, dest: blockURL)
            return blockURL
        }
        blockFetches[key] = task
        defer { blockFetches[key] = nil }
        return try await task.value
    }

    private func fetchBlock(fileID: String, index: Int, fileLength: Int64, dest: URL) async throws {
        let start = Int64(index) * Int64(Self.blockSize)
        let last = min(start + Int64(Self.blockSize), fileLength) - 1 // включительно
        guard start <= last else { throw RangeUnsupportedError() }

        let url = try await tempURL(for: fileID)
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("bytes=\(start)-\(last)", forHTTPHeaderField: "Range")
        if let token = await transfer.validAccessToken(), !token.isEmpty {
            request.setValue(token, forHTTPHeaderField: "x-auth-token")
        }

        let (data, response) = try await InsecureHTTP.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw FileTransferError.downloadFailed }
        // 206 — сервер уважил Range. Любой другой код (в т.ч. 200 = отдал файл
        // целиком) означает, что поблочно читать нельзя → откат на гидрацию.
        guard http.statusCode == 206 else { throw RangeUnsupportedError() }

        try FileManager.default.createDirectory(
            at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
        let part = dest.appendingPathExtension("part")
        try data.write(to: part, options: .atomic)
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: part, to: dest)
    }

    // MARK: - Откат: чтение целиком

    private func readWhole(fileID: String, offset: Int64, end: Int64) async throws -> Data {
        let fileURL = try await ensureWhole(fileID: fileID)
        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        try handle.seek(toOffset: UInt64(offset))
        return try handle.read(upToCount: Int(end - offset)) ?? Data()
    }

    private func ensureWhole(fileID: String) async throws -> URL {
        let dest = cacheDir.appendingPathComponent("\(fileID).whole")
        if FileManager.default.fileExists(atPath: dest.path) { return dest }
        if let inflight = wholeFetches[fileID] { return try await inflight.value }

        let task = Task<URL, Error> {
            let url = try await self.tempURL(for: fileID)
            let tmp = try await self.transfer.download(from: url, suggestedName: "\(fileID).whole")
            try FileManager.default.createDirectory(at: self.cacheDir, withIntermediateDirectories: true)
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            return dest
        }
        wholeFetches[fileID] = task
        defer { wholeFetches[fileID] = nil }
        return try await task.value
    }

    // MARK: - Временная download-URL (TTL-кэш)

    private func tempURL(for fileID: String) async throws -> URL {
        if let cached = tempURLs[fileID], Date().timeIntervalSince(cached.fetchedAt) < Self.urlTTL {
            return cached.url
        }
        let urls = try await transfer.tempDownloadURLs(fileIDs: [fileID])
        guard let url = urls[fileID] else { throw FileTransferError.badURL }
        tempURLs[fileID] = (url, Date())
        return url
    }

    private func blockPath(fileID: String, index: Int) -> URL {
        cacheDir.appendingPathComponent("\(fileID).blocks").appendingPathComponent("\(index).blk")
    }

    /// Сбросить in-memory кэш (URL/режимы/in-flight). Файлы на диске не трогает.
    /// Зовётся при logout/смене сессии (аналог инвалидации кэша на Windows).
    public func resetMemory() {
        tempURLs.removeAll()
        wholeMode.removeAll()
        blockFetches.removeAll()
        wholeFetches.removeAll()
    }
}

/// Сервер ответил не `206` на Range-запрос — поблочное чтение невозможно,
/// нужен разовый откат на скачивание файла целиком.
struct RangeUnsupportedError: Error {}
