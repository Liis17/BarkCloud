import CryptoKit
import Foundation
import GRPCCore

struct UploadPartRequest: Sendable {
    let sessionID: String
    let partNumber: Int
    let fileURL: URL
    let token: String
    let start: Int64
    let size: Int64
    let totalSize: Int64
}

struct UploadPartAcknowledgement: Sendable, Equatable {
    let partNumber: Int
    let size: Int64
}

protocol UploadSessionTransport: Sendable {
    func create(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String,
        fileType: Barkcloud_Files_UploadFileType,
        timeout: Duration?
    ) async throws -> FileUploadSession
    func get(sessionID: String, timeout: Duration?) async throws -> FileUploadSession
    func resume(sessionID: String, timeout: Duration?) async throws -> FileUploadSession
    func complete(sessionID: String, timeout: Duration?) async throws -> FileUploadSession
    func cancel(sessionID: String, timeout: Duration?) async throws -> FileUploadSession
    func uploadPart(_ request: UploadPartRequest) async throws -> UploadPartAcknowledgement
}

protocol UploadClock: Sendable {
    func now() -> Date
    func sleep(for duration: Duration) async throws
}

struct SystemUploadClock: UploadClock {
    func now() -> Date { Date() }
    func sleep(for duration: Duration) async throws { try await Task.sleep(for: duration) }
}

struct GrpcUploadSessionTransport: UploadSessionTransport {
    private let grpc: GrpcManager

    init(grpc: GrpcManager) {
        self.grpc = grpc
    }

    func create(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String,
        fileType: Barkcloud_Files_UploadFileType,
        timeout: Duration?
    ) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var request = Barkcloud_Files_CreateUploadSessionRequest()
        request.idempotencyKey = idempotencyKey
        request.fileName = fileName
        request.fileSize = fileSize
        request.contentType = contentType
        request.sha256 = sha256
        request.fileType = fileType
        return FileTransferService.mapUploadSession(
            try await stub.createUploadSession(request, options: Self.options(timeout))
        )
    }

    func get(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var request = Barkcloud_Files_UploadSessionIdRequest()
        request.sessionID = sessionID
        return FileTransferService.mapUploadSession(
            try await stub.getUploadSession(request, options: Self.options(timeout))
        )
    }

    func resume(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var request = Barkcloud_Files_UploadSessionIdRequest()
        request.sessionID = sessionID
        return FileTransferService.mapUploadSession(
            try await stub.resumeUploadSession(request, options: Self.options(timeout))
        )
    }

    func complete(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var request = Barkcloud_Files_UploadSessionIdRequest()
        request.sessionID = sessionID
        return FileTransferService.mapUploadSession(
            try await stub.completeUploadSession(request, options: Self.options(timeout))
        )
    }

    func cancel(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var request = Barkcloud_Files_UploadSessionIdRequest()
        request.sessionID = sessionID
        return FileTransferService.mapUploadSession(
            try await stub.cancelUploadSession(request, options: Self.options(timeout))
        )
    }

    func uploadPart(_ request: UploadPartRequest) async throws -> UploadPartAcknowledgement {
        let urlRequest = try Self.makePartURLRequest(request, webHost: GrpcEndpoint.webHost)
        let (data, response) = try await InsecureHTTP.session.upload(for: urlRequest, fromFile: request.fileURL)
        guard let http = response as? HTTPURLResponse else { throw FileTransferError.badUploadResponse }
        return try Self.parsePartAcknowledgement(data, statusCode: http.statusCode)
    }

    static func makePartURLRequest(_ request: UploadPartRequest, webHost: String) throws -> URLRequest {
        guard let url = URL(string: "\(webHost)/file-upload/\(request.sessionID)/parts/\(request.partNumber)") else {
            throw FileTransferError.badURL
        }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "PUT"
        urlRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue(
            "bytes \(request.start)-\(request.start + request.size - 1)/\(request.totalSize)",
            forHTTPHeaderField: "Content-Range"
        )
        urlRequest.setValue(String(request.size), forHTTPHeaderField: "Content-Length")
        urlRequest.setValue(request.token, forHTTPHeaderField: "X-Upload-Token")
        return urlRequest
    }

    static func parsePartAcknowledgement(_ data: Data, statusCode: Int) throws -> UploadPartAcknowledgement {
        guard (200..<300).contains(statusCode) else {
            let body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            throw FileTransferError.http(
                status: statusCode,
                errorCode: body?["code"] as? String,
                message: body?["error"] as? String
            )
        }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let partNumber = json["partNumber"] as? Int,
              let size = (json["size"] as? NSNumber)?.int64Value else {
            throw FileTransferError.badPartAcknowledgement
        }
        return UploadPartAcknowledgement(partNumber: partNumber, size: size)
    }

    private static func options(_ timeout: Duration?) -> CallOptions {
        var options = CallOptions.defaults
        options.timeout = timeout
        return options
    }
}

struct Upload2Uploader: Sendable {
    static let controlRPCDeadline: Duration = .seconds(15)
    static let cancellationDeadline: Duration = .seconds(5)
    static let partBufferSize = 4 * 1024 * 1024
    static let invalidUploadTokenCode = "EBCEDE5E-6BC0-44F3-8966-6E71C136CE6B"
    static let incompletePartsCode = "09BF4D7B-7DB9-4284-BDB0-85457B22A589"
    static let maximumAvatarSize: Int64 = 20 * 1024 * 1024

    private let transport: any UploadSessionTransport
    private let clock: any UploadClock
    private let rpcDeadline: Duration
    private let cancelDeadline: Duration

    init(
        transport: any UploadSessionTransport,
        clock: any UploadClock = SystemUploadClock(),
        controlRPCDeadline: Duration = Self.controlRPCDeadline,
        cancellationDeadline: Duration = Self.cancellationDeadline
    ) {
        self.transport = transport
        self.clock = clock
        self.rpcDeadline = controlRPCDeadline
        self.cancelDeadline = cancellationDeadline
    }

    func upload(
        fileURL: URL,
        fileName: String,
        contentType: String,
        fileType: Barkcloud_Files_UploadFileType
    ) async throws -> FileUploadSession {
        let tempDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("BarkCloudUpload-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tempDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDirectory) }

        try Task.checkCancellation()
        let (size, digest) = try Self.measure(fileURL)
        guard size > 0 else { throw FileTransferError.emptyFile }
        if fileType == .userAvatar, size > Self.maximumAvatarSize {
            throw FileTransferError.avatarTooLarge
        }

        let idempotencyKey = UUID().uuidString
        var recoveryCount = 0
        var session: FileUploadSession
        while true {
            do {
                session = try await control { timeout in
                    try await transport.create(
                        idempotencyKey: idempotencyKey,
                        fileName: fileName,
                        fileSize: size,
                        contentType: contentType,
                        sha256: digest,
                        fileType: fileType,
                        timeout: timeout
                    )
                }
                try Self.validate(session, expectedFileSize: size)
                break
            } catch {
                guard Self.isRecoverable(error) else { throw error }
                try await waitForRecovery(&recoveryCount)
            }
        }

        var knownSession: FileUploadSession? = session
        var avatarProcessingDeadline: Date?
        do {
            while true {
                try Task.checkCancellation()
                try Self.validate(session, expectedFileSize: size)
                switch session.status {
                case .ready:
                    return session
                case .uploading:
                    guard let token = session.uploadToken, !token.isEmpty,
                          session.partSize > 0 else {
                        throw FileTransferError.invalidSessionResponse("Upload-сессия не содержит токен или размер части")
                    }
                    let partCount64 = (size - 1) / session.partSize + 1
                    guard partCount64 <= Int64(Int.max) else {
                        throw FileTransferError.invalidSessionResponse("Слишком много частей файла")
                    }
                    let receivedParts = Dictionary(
                        session.uploadedParts.map { ($0.partNumber, $0) },
                        uniquingKeysWith: { first, _ in first }
                    )
                    var needsResume = false
                    for partNumber in 1...Int(partCount64) {
                        try Task.checkCancellation()
                        let start = Int64(partNumber - 1) * session.partSize
                        let partSize = min(session.partSize, size - start)
                        if let received = receivedParts[partNumber], received.hasETag, received.size == partSize {
                            continue
                        }

                        let partURL = tempDirectory.appendingPathComponent("part-\(partNumber)-\(UUID().uuidString)")
                        defer { try? FileManager.default.removeItem(at: partURL) }
                        try Self.writePart(source: fileURL, to: partURL, offset: start, size: partSize)
                        let request = UploadPartRequest(
                            sessionID: session.sessionID,
                            partNumber: partNumber,
                            fileURL: partURL,
                            token: token,
                            start: start,
                            size: partSize,
                            totalSize: size
                        )
                        do {
                            let acknowledgement = try await transport.uploadPart(request)
                            guard acknowledgement.partNumber == partNumber,
                                  acknowledgement.size == partSize else {
                                throw FileTransferError.badPartAcknowledgement
                            }
                        } catch {
                            guard Self.isRecoverable(error) else { throw error }
                            try await waitForRecovery(&recoveryCount)
                            session = try await resumeWithRecovery(sessionID: session.sessionID, count: &recoveryCount)
                            knownSession = session
                            try Self.validate(session, expectedFileSize: size)
                            if session.status == .uploading, fileType == .userAvatar {
                                avatarProcessingDeadline = nil
                            }
                            needsResume = true
                            break
                        }
                    }
                    if needsResume { continue }

                    let completeStartedAt = clock.now()
                    do {
                        let sessionID = session.sessionID
                        session = try await control { timeout in
                            try await transport.complete(sessionID: sessionID, timeout: timeout)
                        }
                        knownSession = session
                        try Self.validate(session, expectedFileSize: size)
                        if fileType == .userAvatar, session.status == .processing {
                            avatarProcessingDeadline = completeStartedAt.addingTimeInterval(120)
                        }
                    } catch {
                        guard Self.isRecoverable(error) else { throw error }
                        // Complete может успешно завершиться на сервере, а ответ потеряться.
                        // Сначала узнаём фактическое состояние, чтобы не повторять его вслепую.
                        let sessionID = session.sessionID
                        if fileType == .userAvatar, avatarProcessingDeadline == nil {
                            avatarProcessingDeadline = completeStartedAt.addingTimeInterval(120)
                        }
                        session = try await getWithRecovery(sessionID: sessionID, count: &recoveryCount)
                        knownSession = session
                        try Self.validate(session, expectedFileSize: size)
                        if session.status == .uploading {
                            if fileType == .userAvatar { avatarProcessingDeadline = nil }
                            try await waitForRecovery(&recoveryCount)
                            let resumeStartedAt = clock.now()
                            session = try await resumeWithRecovery(sessionID: session.sessionID, count: &recoveryCount)
                            knownSession = session
                            try Self.validate(session, expectedFileSize: size)
                            if fileType == .userAvatar, session.status == .processing {
                                avatarProcessingDeadline = resumeStartedAt.addingTimeInterval(120)
                            }
                        }
                    }
                case .processing:
                    if fileType == .userAvatar, avatarProcessingDeadline == nil {
                        avatarProcessingDeadline = clock.now().addingTimeInterval(120)
                    }
                    var pollTimeout = rpcDeadline
                    if let deadline = avatarProcessingDeadline {
                        let remaining = deadline.timeIntervalSince(clock.now())
                        guard remaining > 0 else { throw FileTransferError.processingTimedOut }
                        let remainingNanoseconds = Int64(remaining * 1_000_000_000)
                        guard remainingNanoseconds > 0 else { throw FileTransferError.processingTimedOut }
                        pollTimeout = min(pollTimeout, .nanoseconds(remainingNanoseconds))
                    }
                    try await clock.sleep(for: min(.seconds(2), pollTimeout))
                    try Task.checkCancellation()
                    if let deadline = avatarProcessingDeadline {
                        let remaining = deadline.timeIntervalSince(clock.now())
                        guard remaining > 0 else { throw FileTransferError.processingTimedOut }
                        let remainingNanoseconds = Int64(remaining * 1_000_000_000)
                        guard remainingNanoseconds > 0 else { throw FileTransferError.processingTimedOut }
                        pollTimeout = min(rpcDeadline, .nanoseconds(remainingNanoseconds))
                    }
                    do {
                        let sessionID = session.sessionID
                        session = try await control(timeout: pollTimeout) { timeout in
                            try await transport.get(sessionID: sessionID, timeout: timeout)
                        }
                        knownSession = session
                        try Self.validate(session, expectedFileSize: size)
                    } catch {
                        guard Self.isRecoverable(error) else { throw error }
                        try await waitForRecovery(&recoveryCount, until: avatarProcessingDeadline)
                        continue
                    }
                case .failed, .cancelled, .expired, .unspecified:
                    throw Self.serverError(for: session)
                }
            }
        } catch {
            if Self.isCancellation(error), let knownSession, knownSession.status == .uploading {
                await bestEffortCancel(sessionID: knownSession.sessionID)
            }
            throw error
        }
    }

    private func control<Value: Sendable>(
        timeout requestedTimeout: Duration? = nil,
        operation: @escaping @Sendable (Duration?) async throws -> Value
    ) async throws -> Value {
        let timeout = requestedTimeout ?? rpcDeadline
        return try await AsyncDeadline.withTimeout(timeout) {
            try await operation(timeout)
        }
    }

    private func waitForRecovery(_ count: inout Int) async throws {
        try Task.checkCancellation()
        guard count < 3 else { throw FileTransferError.recoveryLimitReached }
        let delay = Duration.seconds(Int64(1 << count))
        count += 1
        try await clock.sleep(for: delay)
        try Task.checkCancellation()
    }

    private func waitForRecovery(_ count: inout Int, until deadline: Date?) async throws {
        try Task.checkCancellation()
        guard count < 3 else { throw FileTransferError.recoveryLimitReached }
        let delay = Duration.seconds(Int64(1 << count))
        count += 1
        if let deadline {
            let remaining = deadline.timeIntervalSince(clock.now())
            guard remaining > 0 else { throw FileTransferError.processingTimedOut }
            try await clock.sleep(for: min(delay, .milliseconds(Int64(remaining * 1_000))))
            guard clock.now() < deadline else { throw FileTransferError.processingTimedOut }
        } else {
            try await clock.sleep(for: delay)
        }
        try Task.checkCancellation()
    }

    private func getWithRecovery(sessionID: String, count: inout Int) async throws -> FileUploadSession {
        while true {
            do {
                return try await control { timeout in
                    try await transport.get(sessionID: sessionID, timeout: timeout)
                }
            } catch {
                guard Self.isRecoverable(error) else { throw error }
                try await waitForRecovery(&count)
            }
        }
    }

    private func resumeWithRecovery(sessionID: String, count: inout Int) async throws -> FileUploadSession {
        while true {
            do {
                return try await control { timeout in
                    try await transport.resume(sessionID: sessionID, timeout: timeout)
                }
            } catch {
                guard Self.isRecoverable(error) else { throw error }
                try await waitForRecovery(&count)
            }
        }
    }

    private func bestEffortCancel(sessionID: String) async {
        let transport = self.transport
        let cleanup = Task.detached(priority: .utility) {
            try await AsyncDeadline.withTimeout(self.cancelDeadline) {
                try await transport.cancel(sessionID: sessionID, timeout: self.cancelDeadline)
            }
        }
        let _ = try? await cleanup.value
    }

    private static func validate(_ session: FileUploadSession, expectedFileSize: Int64) throws {
        guard !session.sessionID.isEmpty, !session.fileID.isEmpty,
              session.fileSize > 0, session.fileSize == expectedFileSize else {
            throw FileTransferError.invalidSessionResponse("Ответ upload-сессии содержит пустые ID или неверный размер файла")
        }
    }

    private static func serverError(for session: FileUploadSession) -> FileTransferError {
        FileTransferError.server(errorCode: session.errorCode, message: session.errorMessage)
    }

    static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        if let rpc = error as? RPCError, rpc.code == .cancelled { return true }
        return false
    }

    private static func isRecoverable(_ error: Error) -> Bool {
        if isCancellation(error) { return false }
        if let rpc = error as? RPCError {
            if rpc.code == .unavailable || rpc.code == .deadlineExceeded { return true }
            if let errorCode = rpc.errorCode, isRecoverableCode(errorCode) { return true }
        }
        if let urlError = error as? URLError {
            return [
                .timedOut,
                .cannotFindHost,
                .cannotConnectToHost,
                .networkConnectionLost,
                .dnsLookupFailed,
                .notConnectedToInternet,
                .internationalRoamingOff,
                .dataNotAllowed,
                .callIsActive,
                .resourceUnavailable
            ].contains(urlError.code)
        }
        if case let FileTransferError.http(status, errorCode, _) = error {
            return status == 408 || status == 429 || (500..<600).contains(status)
                || errorCode.map(isRecoverableCode) == true
        }
        if case let FileTransferError.server(errorCode, _) = error, let errorCode {
            return isRecoverableCode(errorCode)
        }
        return false
    }

    private static func isRecoverableCode(_ code: String) -> Bool {
        let normalized = code.uppercased()
        return normalized == invalidUploadTokenCode || normalized == incompletePartsCode
    }

    private static func measure(_ fileURL: URL) throws -> (Int64, String) {
        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        var hasher = SHA256()
        var size: Int64 = 0
        while true {
            try Task.checkCancellation()
            guard let data = try handle.read(upToCount: partBufferSize), !data.isEmpty else { break }
            hasher.update(data: data)
            size += Int64(data.count)
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        return (size, digest)
    }

    private static func writePart(source: URL, to destination: URL, offset: Int64, size: Int64) throws {
        guard FileManager.default.createFile(atPath: destination.path, contents: nil) else {
            throw FileTransferError.stagingFailed
        }
        let input = try FileHandle(forReadingFrom: source)
        let output = try FileHandle(forWritingTo: destination)
        defer {
            try? input.close()
            try? output.close()
        }
        try input.seek(toOffset: UInt64(offset))
        var remaining = size
        while remaining > 0 {
            try Task.checkCancellation()
            let readCount = Int(min(Int64(partBufferSize), remaining))
            guard let data = try input.read(upToCount: readCount), !data.isEmpty else {
                throw FileTransferError.sourceChangedWhileReading
            }
            try output.write(contentsOf: data)
            remaining -= Int64(data.count)
        }
    }
}
