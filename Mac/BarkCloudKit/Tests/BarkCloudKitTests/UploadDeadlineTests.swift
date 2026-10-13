import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import GRPCProtobuf
import SwiftProtobuf
import XCTest
@testable import BarkCloudKit

@MainActor
final class UploadDeadlineTests: XCTestCase {
    func testBoundedCallerTimeoutDoesNotCancelSharedRefresh() async throws {
        let probe = DeadlineIntegrationProbe()
        let running = try await startServer(probe: probe)
        let session = makeSession()
        let grpc = makeGrpcManager(session: session, address: running.address)
        var cleanedUp = false
        defer {
            if !cleanedUp { scheduleCleanup(running: running, grpc: grpc, probe: probe) }
        }

        let users = try await grpc.usersStub()
        var options = CallOptions.defaults
        options.timeout = .milliseconds(200)
        let boundedCaller = Task {
            try await users.getUser(Barkcloud_Users_GetUserRequest(), options: options)
        }
        try await probe.waitForTokenCallCount(1)
        let unboundedWaiter = Task { await grpc.accessToken(forMethod: "GetUser") }

        do {
            _ = try await boundedCaller.value
            XCTFail("The bounded RPC should time out while refresh is held")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }

        let heldRefresh = await probe.snapshot()
        XCTAssertEqual(heldRefresh.createTokenTimeouts, [nil])
        XCTAssertEqual(heldRefresh.createTokenCount, 1)
        await probe.releaseToken()

        let freshToken = await unboundedWaiter.value
        XCTAssertEqual(freshToken, "fresh-token")
        let completedRefresh = await probe.snapshot()
        XCTAssertEqual(completedRefresh.createTokenCount, 1)
        session.clearSession()
        await cleanup(running: running, grpc: grpc, probe: probe)
        cleanedUp = true
    }

    func testBoundedWaiterOnExistingRefreshTimesOutWithoutCancellingIt() async throws {
        let probe = DeadlineIntegrationProbe()
        let running = try await startServer(probe: probe)
        let session = makeSession()
        let grpc = makeGrpcManager(session: session, address: running.address)
        var cleanedUp = false
        defer {
            if !cleanedUp { scheduleCleanup(running: running, grpc: grpc, probe: probe) }
        }

        let unboundedWaiter = Task { await grpc.accessToken(forMethod: "GetUser") }
        try await probe.waitForTokenCallCount(1)
        let users = try await grpc.usersStub()
        var options = CallOptions.defaults
        options.timeout = .milliseconds(200)
        do {
            _ = try await users.getUser(Barkcloud_Users_GetUserRequest(), options: options)
            XCTFail("The bounded waiter should time out on the existing refresh")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }

        await probe.releaseToken()
        let freshToken = await unboundedWaiter.value
        XCTAssertEqual(freshToken, "fresh-token")
        let snapshot = await probe.snapshot()
        XCTAssertEqual(snapshot.createTokenCount, 1)
        XCTAssertEqual(snapshot.createTokenTimeouts, [nil])
        session.clearSession()
        await cleanup(running: running, grpc: grpc, probe: probe)
        cleanedUp = true
    }

    func testUploadControlDeadlineBoundsARealGrpcCall() async throws {
        XCTAssertEqual(Upload2Uploader.controlRPCDeadline, .seconds(15))
        let probe = DeadlineIntegrationProbe()
        let running = try await startServer(probe: probe)
        let session = SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)")
        let grpc = makeGrpcManager(session: session, address: running.address)
        var cleanedUp = false
        defer {
            if !cleanedUp { scheduleCleanup(running: running, grpc: grpc, probe: probe) }
        }

        let source = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data("x".utf8).write(to: source)
        defer { try? FileManager.default.removeItem(at: source) }
        let uploader = Upload2Uploader(
            transport: GrpcUploadSessionTransport(grpc: grpc),
            clock: DeadlineTestClock(),
            controlRPCDeadline: .milliseconds(250)
        )

        let ready = try await uploader.upload(
            fileURL: source,
            fileName: "payload.bin",
            contentType: "application/octet-stream",
            fileType: .cloudFile
        )

        XCTAssertEqual(ready.status, .ready)
        let snapshot = await probe.snapshot()
        XCTAssertEqual(snapshot.createUploadCount, 2)
        let createTimeout = Self.decodeGRPCTimeout(snapshot.createUploadTimeouts.first ?? nil)
        XCTAssertNotNil(createTimeout)
        if let createTimeout {
            XCTAssertGreaterThan(createTimeout, .zero)
            XCTAssertLessThanOrEqual(createTimeout, .milliseconds(250))
        }
        XCTAssertNotNil(snapshot.createUploadTimeouts.last ?? nil)
        await probe.releaseControl()
        await cleanup(running: running, grpc: grpc, probe: probe)
        cleanedUp = true
    }

    func testSetProfilePictureDeadlineIncludesAuthRefresh() async throws {
        XCTAssertEqual(UserRepository.avatarDeadline, .seconds(15))
        let probe = DeadlineIntegrationProbe()
        let running = try await startServer(probe: probe)
        let session = makeSession()
        let grpc = makeGrpcManager(session: session, address: running.address)
        var cleanedUp = false
        defer {
            if !cleanedUp { scheduleCleanup(running: running, grpc: grpc, probe: probe) }
        }
        let transfer = FileTransferService(
            grpc: grpc,
            uploadTransport: ReadyUploadTransport(),
            clock: SystemUploadClock()
        )
        let repository = UserRepository(
            grpc: grpc,
            transfer: transfer,
            profilePictureDeadline: .milliseconds(600)
        )
        let startedAt = Date()
        let settingAvatar = Task {
            try await repository.setAvatar(imageData: Data("avatar".utf8), fileName: "avatar.png")
        }

        try await probe.waitForTokenCallCount(1)
        try await Task.sleep(for: .milliseconds(400))
        await probe.releaseToken()
        try await probe.waitForProfilePictureCallCount(1)
        do {
            try await settingAvatar.value
            XCTFail("SetProfilePicture should time out at the original upload deadline")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }

        XCTAssertLessThan(Date().timeIntervalSince(startedAt), 0.9)
        let snapshot = await probe.snapshot()
        XCTAssertEqual(snapshot.createTokenTimeouts, [nil])
        XCTAssertEqual(snapshot.profilePictureTimeouts.count, 1)
        let profilePictureTimeout = Self.decodeGRPCTimeout(snapshot.profilePictureTimeouts.first ?? nil)
        XCTAssertNotNil(profilePictureTimeout)
        if let profilePictureTimeout {
            XCTAssertGreaterThan(profilePictureTimeout, .zero)
            XCTAssertLessThanOrEqual(profilePictureTimeout, .milliseconds(600))
        }
        await probe.releaseProfilePicture()
        let refreshedToken = await grpc.validAccessToken()
        XCTAssertEqual(refreshedToken, "fresh-token")
        session.clearSession()
        await cleanup(running: running, grpc: grpc, probe: probe)
        cleanedUp = true
    }

    func testAttachDeadlineIncludesAuthRefresh() async throws {
        XCTAssertEqual(CloudRepository.uploadAttachDeadline, .seconds(15))
        let probe = DeadlineIntegrationProbe()
        let running = try await startServer(probe: probe)
        let session = makeSession()
        let grpc = makeGrpcManager(session: session, address: running.address)
        var cleanedUp = false
        defer {
            if !cleanedUp { scheduleCleanup(running: running, grpc: grpc, probe: probe) }
        }
        let transfer = FileTransferService(
            grpc: grpc,
            uploadTransport: ReadyUploadTransport(),
            clock: SystemUploadClock()
        )
        let repository = CloudRepository(
            grpc: grpc,
            transfer: transfer,
            uploadAttachDeadline: .milliseconds(600)
        )
        let startedAt = Date()
        let uploading = Task {
            try await repository.uploadFile(data: Data("body".utf8), fileName: "note.txt", toDirectory: "directory-1")
        }

        try await probe.waitForTokenCallCount(1)
        try await Task.sleep(for: .milliseconds(400))
        await probe.releaseToken()
        try await probe.waitForAttachCallCount(1)
        do {
            _ = try await uploading.value
            XCTFail("AttachFile should time out at the original upload deadline")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }

        XCTAssertLessThan(Date().timeIntervalSince(startedAt), 0.9)
        let snapshot = await probe.snapshot()
        XCTAssertEqual(snapshot.createTokenTimeouts, [nil])
        XCTAssertEqual(snapshot.attachTimeouts.count, 1)
        let attachTimeout = Self.decodeGRPCTimeout(snapshot.attachTimeouts.first ?? nil)
        XCTAssertNotNil(attachTimeout)
        if let attachTimeout {
            XCTAssertGreaterThan(attachTimeout, .zero)
            XCTAssertLessThanOrEqual(attachTimeout, .milliseconds(600))
        }
        await probe.releaseAttach()
        let refreshedToken = await grpc.validAccessToken()
        XCTAssertEqual(refreshedToken, "fresh-token")
        session.clearSession()
        await cleanup(running: running, grpc: grpc, probe: probe)
        cleanedUp = true
    }

    private func makeSession() -> SessionStore {
        let session = SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)")
        session.accessToken = "stale-token"
        session.accessTokenExpiresAt = Date(timeIntervalSinceNow: -60)
        session.refreshToken = "refresh-token"
        return session
    }

    private func makeGrpcManager(
        session: SessionStore,
        address: (host: String, port: Int)
    ) -> GrpcManager {
        GrpcManager(
            session: session,
            serverConfig: ServerConfig(
                identityHost: address.host,
                identityPort: address.port,
                usersHost: address.host,
                usersPort: address.port,
                filesHost: address.host,
                filesPort: address.port,
                useTLS: false,
                allowSelfSigned: false
            )
        )
    }

    private func startServer(probe: DeadlineIntegrationProbe) async throws -> RunningDeadlineServer {
        let transport = HTTP2ServerTransport.Posix(
            address: .ipv4(host: "127.0.0.1", port: 0),
            transportSecurity: .plaintext
        )
        let server = GRPCServer(transport: transport, services: [DeadlineIntegrationService(probe: probe)])
        let serverTask = Task { try await server.serve() }
        let address = try await waitForListeningAddress(transport)
        return RunningDeadlineServer(server: server, serverTask: serverTask, address: address)
    }

    private func scheduleCleanup(
        running: RunningDeadlineServer,
        grpc: GrpcManager,
        probe: DeadlineIntegrationProbe
    ) {
        Task {
            await cleanup(running: running, grpc: grpc, probe: probe)
        }
    }

    private func cleanup(
        running: RunningDeadlineServer,
        grpc: GrpcManager,
        probe: DeadlineIntegrationProbe
    ) async {
        await probe.releaseAll()
        await grpc.shutdown()
        running.server.beginGracefulShutdown()
        _ = try? await running.serverTask.value
    }

    private static func decodeGRPCTimeout(_ value: String?) -> Duration? {
        guard let value, value.count >= 2,
              let amount = Int64(value.dropLast()), amount >= 0 else { return nil }
        switch value.last {
        case "H": return .seconds(amount * 3_600)
        case "M": return .seconds(amount * 60)
        case "S": return .seconds(amount)
        case "m": return .milliseconds(amount)
        case "u": return .microseconds(amount)
        case "n": return .nanoseconds(amount)
        default: return nil
        }
    }
}

private struct RunningDeadlineServer {
    let server: GRPCServer<HTTP2ServerTransport.Posix>
    let serverTask: Task<Void, Error>
    let address: (host: String, port: Int)
}

private actor RPCGate {
    private var released = false
    private var continuation: CheckedContinuation<Void, Never>?

    func wait() async {
        guard !released else { return }
        await withCheckedContinuation { continuation in
            self.continuation = continuation
        }
    }

    func release() {
        released = true
        continuation?.resume()
        continuation = nil
    }
}

private actor DeadlineIntegrationProbe {
    struct Snapshot {
        var createTokenCount = 0
        var createTokenTimeouts: [String?] = []
        var createUploadCount = 0
        var createUploadTimeouts: [String?] = []
        var profilePictureTimeouts: [String?] = []
        var attachTimeouts: [String?] = []
    }

    private var value = Snapshot()
    private let tokenGate = RPCGate()
    private let controlGate = RPCGate()
    private let profilePictureGate = RPCGate()
    private let attachGate = RPCGate()

    func holdCreateToken(timeout: String?) async {
        value.createTokenCount += 1
        value.createTokenTimeouts.append(timeout)
        await tokenGate.wait()
    }

    func createUploadSession(timeout: String?, fileSize: Int64) async -> Barkcloud_Files_UploadSessionResponse {
        value.createUploadCount += 1
        value.createUploadTimeouts.append(timeout)
        if value.createUploadCount == 1 { await controlGate.wait() }
        var response = Barkcloud_Files_UploadSessionResponse()
        response.sessionID = "session-1"
        response.fileID = "file-1"
        response.status = .ready
        response.fileSize = fileSize
        response.partSize = max(1, fileSize)
        return response
    }

    func holdProfilePicture(timeout: String?) async {
        value.profilePictureTimeouts.append(timeout)
        await profilePictureGate.wait()
    }

    func holdAttach(timeout: String?) async {
        value.attachTimeouts.append(timeout)
        await attachGate.wait()
    }

    func waitForTokenCallCount(_ count: Int) async throws {
        for _ in 0..<200 {
            if value.createTokenCount >= count { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw FileTransferError.badUploadResponse
    }

    func waitForCreateUploadCallCount(_ count: Int) async throws {
        for _ in 0..<200 {
            if value.createUploadCount >= count { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw FileTransferError.badUploadResponse
    }

    func waitForProfilePictureCallCount(_ count: Int) async throws {
        for _ in 0..<200 {
            if value.profilePictureTimeouts.count >= count { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw FileTransferError.badUploadResponse
    }

    func waitForAttachCallCount(_ count: Int) async throws {
        for _ in 0..<200 {
            if value.attachTimeouts.count >= count { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw FileTransferError.badUploadResponse
    }

    func snapshot() -> Snapshot { value }
    func releaseToken() async { await tokenGate.release() }
    func releaseControl() async { await controlGate.release() }
    func releaseProfilePicture() async { await profilePictureGate.release() }
    func releaseAttach() async { await attachGate.release() }

    func releaseAll() async {
        await tokenGate.release()
        await controlGate.release()
        await profilePictureGate.release()
        await attachGate.release()
    }
}

private struct DeadlineIntegrationService: RegistrableRPCService {
    let probe: DeadlineIntegrationProbe

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        router.registerHandler(
            forMethod: Barkcloud_Identity_IdentityApi.Method.CreateToken.descriptor,
            deserializer: ProtobufDeserializer<Barkcloud_Identity_CreateTokenRequest>(),
            serializer: ProtobufSerializer<Barkcloud_Identity_CreateTokenResponse>()
        ) { request, _ in
            let timeout = request.metadata[stringValues: "grpc-timeout"].first(where: { _ in true })
            await probe.holdCreateToken(timeout: timeout)
            var token = Barkcloud_Identity_Token()
            token.value = "fresh-token"
            token.expirationDate = SwiftProtobuf.Google_Protobuf_Timestamp(date: Date(timeIntervalSinceNow: 3600))
            var response = Barkcloud_Identity_CreateTokenResponse()
            response.accessToken = token
            return StreamingServerResponse(single: ServerResponse(accepted: .success(.init(message: response))))
        }
        router.registerHandler(
            forMethod: Barkcloud_Files_FilesApi.Method.CreateUploadSession.descriptor,
            deserializer: ProtobufDeserializer<Barkcloud_Files_CreateUploadSessionRequest>(),
            serializer: ProtobufSerializer<Barkcloud_Files_UploadSessionResponse>()
        ) { request, _ in
            let timeout = request.metadata[stringValues: "grpc-timeout"].first(where: { _ in true })
            let response = await probe.createUploadSession(timeout: timeout, fileSize: 1)
            return StreamingServerResponse(single: ServerResponse(accepted: .success(.init(message: response))))
        }
        router.registerHandler(
            forMethod: Barkcloud_Users_UsersApi.Method.SetProfilePicture.descriptor,
            deserializer: ProtobufDeserializer<Barkcloud_Users_SetProfilePictureRequest>(),
            serializer: ProtobufSerializer<Barkcloud_Users_SetProfilePictureResponse>()
        ) { request, _ in
            let timeout = request.metadata[stringValues: "grpc-timeout"].first(where: { _ in true })
            await probe.holdProfilePicture(timeout: timeout)
            return StreamingServerResponse(single: ServerResponse(
                accepted: .success(.init(message: Barkcloud_Users_SetProfilePictureResponse()))
            ))
        }
        router.registerHandler(
            forMethod: Barkcloud_Files_CloudApi.Method.AttachFile.descriptor,
            deserializer: ProtobufDeserializer<Barkcloud_Files_AttachFileRequest>(),
            serializer: ProtobufSerializer<Barkcloud_Files_CloudEmpty>()
        ) { request, _ in
            let timeout = request.metadata[stringValues: "grpc-timeout"].first(where: { _ in true })
            await probe.holdAttach(timeout: timeout)
            return StreamingServerResponse(single: ServerResponse(
                accepted: .success(.init(message: Barkcloud_Files_CloudEmpty()))
            ))
        }
    }
}

private actor ReadyUploadTransport: UploadSessionTransport {
    func create(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String,
        fileType: Barkcloud_Files_UploadFileType,
        timeout: Duration?
    ) async throws -> FileUploadSession {
        FileUploadSession(
            sessionID: "session-1",
            fileID: "file-1",
            status: .ready,
            fileSize: fileSize,
            partSize: max(1, fileSize),
            expiresAt: nil,
            uploadToken: nil,
            uploadedParts: [],
            errorCode: nil,
            errorMessage: nil
        )
    }

    func get(sessionID: String, timeout: Duration?) async throws -> FileUploadSession { throw FileTransferError.badUploadResponse }
    func resume(sessionID: String, timeout: Duration?) async throws -> FileUploadSession { throw FileTransferError.badUploadResponse }
    func complete(sessionID: String, timeout: Duration?) async throws -> FileUploadSession { throw FileTransferError.badUploadResponse }
    func cancel(sessionID: String, timeout: Duration?) async throws -> FileUploadSession { throw FileTransferError.badUploadResponse }
    func uploadPart(_ request: UploadPartRequest) async throws -> UploadPartAcknowledgement { throw FileTransferError.badUploadResponse }
}

private final class DeadlineTestClock: UploadClock, @unchecked Sendable {
    private let lock = NSLock()
    private var instant = Date(timeIntervalSince1970: 0)

    func now() -> Date {
        lock.lock()
        defer { lock.unlock() }
        return instant
    }

    func sleep(for duration: Duration) async throws {
        try Task.checkCancellation()
        advance(by: duration)
    }

    private func advance(by duration: Duration) {
        lock.lock()
        defer { lock.unlock() }
        let components = duration.components
        instant.addTimeInterval(Double(components.seconds) + Double(components.attoseconds) / 1e18)
    }
}

private func waitForListeningAddress(
    _ transport: HTTP2ServerTransport.Posix
) async throws -> (host: String, port: Int) {
    for _ in 0..<100 {
        if let address = try? await transport.listeningAddress, let ipv4 = address.ipv4 {
            return (ipv4.host, ipv4.port)
        }
        try await Task.sleep(for: .milliseconds(10))
    }
    throw FileTransferError.badURL
}
