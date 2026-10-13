import CryptoKit
import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import GRPCProtobuf
import SwiftProtobuf
import XCTest
@testable import BarkCloudKit

final class Upload2UploaderTests: XCTestCase {
    func testDefaultCloudFileUploadStreamsPartsAndWaitsForReady() async throws {
        let source = try makeTemporaryFile(contents: Data("abcdefghij".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4, completeStatus: .processing)
        await transport.enqueueGet(makeSession(status: .ready, fileSize: 10, partSize: 4, token: nil))
        let service = try await makeTransfer(transport: transport)

        let ready = try await service.uploadFile(fileURL: source, contentType: "text/plain")
        let calls = await transport.snapshot()

        XCTAssertEqual(ready.status, .ready)
        XCTAssertEqual(ready.fileID, "file-1")
        XCTAssertEqual(ready.sessionID, "session-1")
        XCTAssertEqual(calls.fileTypes, [.cloudFile])
        XCTAssertEqual(calls.fileSizes, [10])
        XCTAssertEqual(calls.hashes, [sha256(Data("abcdefghij".utf8))])
        XCTAssertEqual(calls.parts.map(\.partNumber), [1, 2, 3])
        XCTAssertEqual(calls.partContents, [Data("abcd".utf8), Data("efgh".utf8), Data("ij".utf8)])
        XCTAssertEqual(calls.completeCount, 1)
        XCTAssertEqual(calls.getCount, 1)
        XCTAssertEqual(calls.controlTimeouts, [.seconds(15), .seconds(15), .seconds(15)])
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
    }

    func testDataUploadUsesExplicitUserAvatarType() async throws {
        let transport = FakeUploadTransport(partSize: 8)
        let service = try await makeTransfer(transport: transport)
        let stagingBefore = Self.stagingDirectories()

        let ready = try await service.uploadFile(
            data: Data("avatar".utf8),
            fileName: "portrait.png",
            contentType: "image/png",
            fileType: .userAvatar
        )

        let calls = await transport.snapshot()
        XCTAssertEqual(ready.status, .ready)
        XCTAssertEqual(calls.fileTypes, [.userAvatar])
        XCTAssertEqual(Self.stagingDirectories(), stagingBefore)
    }

    func testStreamingHashAndSizeCrossFourMiBReadBoundary() async throws {
        let contents = Data(repeating: 0x5A, count: Upload2Uploader.partBufferSize + 31)
        let source = try makeTemporaryFile(contents: contents)
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: Int64(contents.count) + 1)
        let service = try await makeTransfer(transport: transport)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.fileSizes, [Int64(contents.count)])
        XCTAssertEqual(calls.hashes, [sha256(contents)])
    }

    func testPutRequestUsesUploadTokenAndExactRangeWithoutAccessToken() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("part-test")
        let uploadPart = UploadPartRequest(
            sessionID: "session-id",
            partNumber: 2,
            fileURL: file,
            token: "session-token",
            start: 8,
            size: 4,
            totalSize: 10
        )

        let request = try GrpcUploadSessionTransport.makePartURLRequest(uploadPart, webHost: "https://files.example")

        XCTAssertEqual(request.httpMethod, "PUT")
        XCTAssertEqual(request.url?.absoluteString, "https://files.example/file-upload/session-id/parts/2")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/octet-stream")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Range"), "bytes 8-11/10")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Length"), "4")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-Upload-Token"), "session-token")
        XCTAssertNil(request.value(forHTTPHeaderField: "x-auth-token"))

        let ack = try GrpcUploadSessionTransport.parsePartAcknowledgement(
            Data(#"{"partNumber":2,"size":4}"#.utf8),
            statusCode: 200
        )
        XCTAssertEqual(ack, UploadPartAcknowledgement(partNumber: 2, size: 4))
        XCTAssertThrowsError(try GrpcUploadSessionTransport.parsePartAcknowledgement(Data("{}".utf8), statusCode: 200))
        XCTAssertThrowsError(try GrpcUploadSessionTransport.parsePartAcknowledgement(
            Data(#"{"code":"quota","error":"full"}"#.utf8),
            statusCode: 413
        )) { error in
            guard case let FileTransferError.http(status, errorCode, message) = error else {
                XCTFail("Unexpected error: \(error)")
                return
            }
            XCTAssertEqual(status, 413)
            XCTAssertEqual(errorCode, "quota")
            XCTAssertEqual(message, "full")
        }
    }

    func testResumeRotatesTokenAndSkipsPartConfirmedAfterLostPut() async throws {
        let source = try makeTemporaryFile(contents: Data("abcdefghijkl".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        await transport.setInitialParts([FileUploadPart(partNumber: 1, size: 4, hasETag: true)])
        await transport.enqueuePartFailure(.invalidToken)
        await transport.enqueueResume(makeSession(
            status: .uploading,
            fileSize: 12,
            partSize: 4,
            token: "rotated-token",
            parts: [FileUploadPart(partNumber: 1, size: 4, hasETag: true),
                    FileUploadPart(partNumber: 2, size: 4, hasETag: true)]
        ))
        let service = try await makeTransfer(transport: transport)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.resumeCount, 1)
        XCTAssertEqual(calls.parts.map(\.partNumber), [2, 3])
        XCTAssertEqual(calls.tokens, ["initial-token", "rotated-token"])
    }

    func testCreateRetryReusesOneIdempotencyKey() async throws {
        let source = try makeTemporaryFile(contents: Data("x".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        await transport.enqueueCreateFailure(.lostCreate)
        let clock = TestUploadClock()
        let service = try await makeTransfer(transport: transport, clock: clock)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.createCount, 2)
        XCTAssertEqual(Set(calls.idempotencyKeys).count, 1)
        XCTAssertEqual(Set(calls.createdSessionIDs).count, 1)
        XCTAssertEqual(clock.sleeps, [.seconds(1)])
    }

    func testLostPartAcknowledgementResumesAndSkipsConfirmedPart() async throws {
        let source = try makeTemporaryFile(contents: Data("single-part".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 64)
        await transport.enqueuePartFailure(.lostPartAcknowledgement)
        let service = try await makeTransfer(transport: transport)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.parts.map(\.partNumber), [1])
        XCTAssertEqual(calls.resumeCount, 1)
        XCTAssertEqual(calls.completeCount, 1)
    }

    func testLostCompleteGetsServerStateBeforeAnyRetry() async throws {
        let source = try makeTemporaryFile(contents: Data("x".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        await transport.enqueueCompleteFailure(.deadlineAfterAccept)
        await transport.enqueueGet(makeSession(status: .ready, fileSize: 1, partSize: 4, token: nil))
        let service = try await makeTransfer(transport: transport)

        let ready = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(ready.status, .ready)
        XCTAssertEqual(calls.completeCount, 1)
        XCTAssertEqual(calls.getCount, 1)
        XCTAssertEqual(calls.resumeCount, 0)
    }

    func testResumeToProcessingIsPolledUntilReady() async throws {
        let source = try makeTemporaryFile(contents: Data("x".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        await transport.enqueueCompleteFailure(.deadlineAfterAccept)
        await transport.enqueueGet(makeSession(status: .uploading, fileSize: 1, partSize: 4, token: "initial-token"))
        await transport.enqueueResume(makeSession(status: .processing, fileSize: 1, partSize: 4, token: nil))
        let service = try await makeTransfer(transport: transport)

        let ready = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(ready.status, .ready)
        XCTAssertEqual(calls.completeCount, 1)
        XCTAssertEqual(calls.resumeCount, 1)
        XCTAssertEqual(calls.getCount, 2)
    }

    func testIncompletePartsRecoveryResumesAndUploadsMissingPart() async throws {
        let source = try makeTemporaryFile(contents: Data("abcdefgh".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        await transport.enqueueCompleteFailure(.incompleteParts)
        await transport.enqueueGet(makeSession(status: .uploading, fileSize: 8, partSize: 4, token: "initial-token"))
        await transport.enqueueResume(makeSession(
            status: .uploading,
            fileSize: 8,
            partSize: 4,
            token: "rotated-token",
            parts: [FileUploadPart(partNumber: 1, size: 4, hasETag: true)]
        ))
        let service = try await makeTransfer(transport: transport)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.resumeCount, 1)
        XCTAssertEqual(calls.parts.map(\.partNumber), [1, 2, 2])
        XCTAssertEqual(calls.tokens.last, "rotated-token")
    }

    func testTransientGetAndResumeUseSharedThreeRetryBudget() async throws {
        let source = try makeTemporaryFile(contents: Data("abcdefgh".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        let clock = TestUploadClock()
        await transport.enqueueCompleteFailure(.incompleteParts)
        await transport.enqueueGetFailure(.network)
        await transport.enqueueGet(makeSession(status: .uploading, fileSize: 8, partSize: 4, token: "initial-token"))
        await transport.enqueueResumeFailure(.network)
        await transport.enqueueResume(makeSession(
            status: .uploading,
            fileSize: 8,
            partSize: 4,
            token: "rotated-token",
            parts: [FileUploadPart(partNumber: 1, size: 4, hasETag: true)]
        ))
        let service = try await makeTransfer(transport: transport, clock: clock)

        _ = try await service.uploadFile(fileURL: source)
        let calls = await transport.snapshot()

        XCTAssertEqual(calls.completeCount, 2)
        XCTAssertEqual(calls.getCount, 2)
        XCTAssertEqual(calls.resumeCount, 2)
        XCTAssertEqual(clock.sleeps, [.seconds(1), .seconds(2), .seconds(4)])
    }

    func testRecoveryExhaustionStopsAfterThreeDelays() async throws {
        let source = try makeTemporaryFile(contents: Data("x".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 4)
        let clock = TestUploadClock()
        for _ in 0..<4 { await transport.enqueueCreateFailure(.network) }
        let service = try await makeTransfer(transport: transport, clock: clock)

        do {
            _ = try await service.uploadFile(fileURL: source)
            XCTFail("The shared recovery budget should be exhausted")
        } catch let error as FileTransferError {
            if case .recoveryLimitReached = error {} else { XCTFail("Unexpected error: \(error)") }
        }

        let calls = await transport.snapshot()
        XCTAssertEqual(calls.createCount, 4)
        XCTAssertEqual(clock.sleeps, [.seconds(1), .seconds(2), .seconds(4)])
    }

    func testTerminalInvalidAndQuotaResponsesDoNotRetry() async throws {
        let source = try makeTemporaryFile(contents: Data("x".utf8))
        defer { try? FileManager.default.removeItem(at: source) }

        for status in [FileUploadSessionStatus.failed, .cancelled, .expired, .unspecified] {
            let transport = FakeUploadTransport(partSize: 4)
            await transport.setNextCreateSession(makeSession(
                status: status,
                fileSize: 1,
                partSize: 4,
                token: nil,
                errorCode: "terminal-code",
                errorMessage: "terminal message"
            ))
            let service = try await makeTransfer(transport: transport)
            do {
                _ = try await service.uploadFile(fileURL: source)
                XCTFail("Terminal session status should fail")
            } catch let error as FileTransferError {
                guard case let .server(errorCode, message) = error else {
                    XCTFail("Unexpected error: \(error)")
                    continue
                }
                XCTAssertEqual(errorCode, "terminal-code")
                XCTAssertEqual(message, "terminal message")
            }
            let terminalCalls = await transport.snapshot()
            XCTAssertEqual(terminalCalls.createCount, 1)
        }

        let invalidTransport = FakeUploadTransport(partSize: 4)
        await invalidTransport.setNextCreateSession(FileUploadSession(
            sessionID: "",
            fileID: "file-1",
            status: .uploading,
            fileSize: 1,
            partSize: 4,
            expiresAt: nil,
            uploadToken: "token",
            uploadedParts: [],
            errorCode: nil,
            errorMessage: nil
        ))
        let invalidService = try await makeTransfer(transport: invalidTransport)
        do {
            _ = try await invalidService.uploadFile(fileURL: source)
            XCTFail("Invalid response should fail before uploading parts")
        } catch let error as FileTransferError {
            if case .invalidSessionResponse = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        let invalidCalls = await invalidTransport.snapshot()
        XCTAssertEqual(invalidCalls.createCount, 1)

        let sizeMismatchTransport = FakeUploadTransport(partSize: 4)
        await sizeMismatchTransport.setNextCreateSession(makeSession(status: .uploading, fileSize: 2, partSize: 4, token: "token"))
        let sizeMismatchService = try await makeTransfer(transport: sizeMismatchTransport)
        do {
            _ = try await sizeMismatchService.uploadFile(fileURL: source)
            XCTFail("Mismatched server size must be rejected")
        } catch let error as FileTransferError {
            if case .invalidSessionResponse = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        let mismatchCalls = await sizeMismatchTransport.snapshot()
        XCTAssertEqual(mismatchCalls.createCount, 1)

        let quotaTransport = FakeUploadTransport(partSize: 4)
        let quotaClock = TestUploadClock()
        await quotaTransport.enqueueCreateFailure(.quota)
        let quotaService = try await makeTransfer(transport: quotaTransport, clock: quotaClock)
        do {
            _ = try await quotaService.uploadFile(fileURL: source)
            XCTFail("Quota errors are permanent")
        } catch let error as FileTransferError {
            XCTAssertEqual(error.serverErrorCode, "64E94D14-CA45-4827-876C-701FE21986B2")
        }
        let quotaCalls = await quotaTransport.snapshot()
        XCTAssertEqual(quotaCalls.createCount, 1)
        XCTAssertTrue(quotaClock.sleeps.isEmpty)

        let localTransport = FakeUploadTransport(partSize: 4)
        let localClock = TestUploadClock()
        await localTransport.enqueueCreateFailure(.localFailure)
        let localService = try await makeTransfer(transport: localTransport, clock: localClock)
        do {
            _ = try await localService.uploadFile(fileURL: source)
            XCTFail("A local file error must not be retried")
        } catch let error as URLError {
            XCTAssertEqual(error.code, .fileDoesNotExist)
        }
        let localCalls = await localTransport.snapshot()
        XCTAssertEqual(localCalls.createCount, 1)
        XCTAssertTrue(localClock.sleeps.isEmpty)
    }

    func testEmptyAndOversizedAvatarFilesAreRejectedBeforeCreate() async throws {
        let empty = try makeTemporaryFile(contents: Data())
        defer { try? FileManager.default.removeItem(at: empty) }
        let transport = FakeUploadTransport(partSize: 4)
        let service = try await makeTransfer(transport: transport)

        do {
            _ = try await service.uploadFile(fileURL: empty)
            XCTFail("Empty file should be rejected")
        } catch let error as FileTransferError {
            if case .emptyFile = error {} else { XCTFail("Unexpected error: \(error)") }
        }

        let oversized = try makeTemporaryFile(contents: Data(repeating: 0, count: Int(Upload2Uploader.maximumAvatarSize + 1)))
        defer { try? FileManager.default.removeItem(at: oversized) }
        do {
            _ = try await service.uploadFile(fileURL: oversized, fileType: .userAvatar)
            XCTFail("Oversized avatar should be rejected")
        } catch let error as FileTransferError {
            if case .avatarTooLarge = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        let calls = await transport.snapshot()
        XCTAssertEqual(calls.createCount, 0)
    }

    func testAvatarProcessingHas120SecondDeadline() async throws {
        let source = try makeTemporaryFile(contents: Data("avatar".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let transport = FakeUploadTransport(partSize: 8, completeStatus: .processing)
        await transport.setGetDefaultStatus(.processing)
        let clock = TestUploadClock()
        let uploader = Upload2Uploader(transport: transport, clock: clock)

        do {
            _ = try await uploader.upload(fileURL: source, fileName: "avatar.png", contentType: "image/png", fileType: .userAvatar)
            XCTFail("Processing should time out")
        } catch let error as FileTransferError {
            if case .processingTimedOut = error {} else { XCTFail("Unexpected error: \(error)") }
        }

        XCTAssertEqual(clock.now().timeIntervalSince1970, Date(timeIntervalSince1970: 0).timeIntervalSince1970 + 120, accuracy: 0.01)
        let calls = await transport.snapshot()
        XCTAssertTrue(calls.getTimeouts.allSatisfy { ($0 ?? .zero) > .zero && ($0 ?? .zero) <= .seconds(15) })
        XCTAssertFalse(calls.getTimeouts.isEmpty)
        if let lastTimeout = calls.getTimeouts.last ?? nil {
            XCTAssertLessThanOrEqual(lastTimeout, .seconds(2))
        } else {
            XCTFail("Avatar polling must apply the remaining upload deadline")
        }
    }

    @MainActor
    func testAvatarRepositoryOnlySetsProfileAfterReadyAndKeepsOldValueOnUploadError() async throws {
        let session = SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)")
        let grpc = GrpcManager(session: session, serverConfig: .production)
        let uploader = FakeAvatarUpload()
        let profile = FakeProfilePictureTransport()
        let repository = UserRepository(grpc: grpc, avatarUpload: uploader, profilePicture: profile)
        let ready = makeSession(status: .ready, fileSize: 4, partSize: 4, token: nil)
        await uploader.setResult(ready)

        try await repository.setAvatar(imageData: Data("test".utf8), fileName: "portrait.png")
        let firstIDs = await profile.fileIDs()
        let receivedType = await uploader.receivedFileType()
        let receivedContentType = await uploader.receivedContentType()
        XCTAssertEqual(firstIDs, ["file-1"])
        XCTAssertEqual(receivedType, .userAvatar)
        XCTAssertEqual(receivedContentType, "image/png")

        await uploader.setFailure(FileTransferError.server(errorCode: "quota", message: "quota"))
        do {
            try await repository.setAvatar(imageData: Data("test".utf8), fileName: "portrait.png")
            XCTFail("Upload error should be propagated")
        } catch {}
        let finalIDs = await profile.fileIDs()
        XCTAssertEqual(finalIDs, ["file-1"])

        await uploader.clearFailure()
        await uploader.setResult(makeSession(status: .processing, fileSize: 4, partSize: 4, token: nil))
        do {
            try await repository.setAvatar(imageData: Data("test".utf8), fileName: "portrait.png")
            XCTFail("Processing avatar must not be installed")
        } catch let error as FileTransferError {
            if case .invalidSessionResponse = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        let idsAfterProcessing = await profile.fileIDs()
        XCTAssertEqual(idsAfterProcessing, ["file-1"])
        session.clearSession()
    }

    @MainActor
    func testAvatarRepositoryRejectsEmptyAndOversizedDataBeforeUpload() async throws {
        let session = SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)")
        let uploader = FakeAvatarUpload()
        let isolatedRepository = UserRepository(
            grpc: GrpcManager(session: session, serverConfig: .production),
            avatarUpload: uploader,
            profilePicture: FakeProfilePictureTransport()
        )
        do {
            try await isolatedRepository.setAvatar(imageData: Data(), fileName: "portrait.png")
            XCTFail("Empty avatar data must be rejected")
        } catch let error as FileTransferError {
            if case .emptyFile = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        do {
            let oversized = Data(repeating: 0, count: Int(Upload2Uploader.maximumAvatarSize + 1))
            try await isolatedRepository.setAvatar(imageData: oversized, fileName: "portrait.png")
            XCTFail("Oversized avatar data must be rejected")
        } catch let error as FileTransferError {
            if case .avatarTooLarge = error {} else { XCTFail("Unexpected error: \(error)") }
        }

        let uploadCount = await uploader.uploadCount()
        XCTAssertEqual(uploadCount, 0)
        session.clearSession()
    }

    @MainActor
    func testCloudRepositoryAttachesOnlyReadySessionWithSessionID() async throws {
        let grpc = GrpcManager(session: SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)"), serverConfig: .production)
        let transport = FakeUploadTransport(partSize: 8)
        let transfer = FileTransferService(grpc: grpc, uploadTransport: transport, clock: TestUploadClock())
        let attach = FakeCloudAttachTransport()
        let repository = CloudRepository(grpc: grpc, transfer: transfer, attachTransport: attach)

        let fileID = try await repository.uploadFile(
            data: Data("body".utf8),
            fileName: "note.txt",
            toDirectory: "directory-1"
        )
        let request = await attach.request()

        XCTAssertEqual(fileID, "file-1")
        XCTAssertEqual(request?.fileID, "file-1")
        XCTAssertEqual(request?.uploadSessionID, "session-1")
        XCTAssertEqual(request?.directoryID, "directory-1")
        XCTAssertFalse(request?.routeByMediaKind ?? true)

        let failedTransport = FakeUploadTransport(partSize: 8, completeStatus: .failed)
        let failedTransfer = FileTransferService(grpc: grpc, uploadTransport: failedTransport, clock: TestUploadClock())
        let failedAttach = FakeCloudAttachTransport()
        let failedRepository = CloudRepository(grpc: grpc, transfer: failedTransfer, attachTransport: failedAttach)
        do {
            _ = try await failedRepository.uploadFile(data: Data("body".utf8), fileName: "note.txt", toDirectory: "directory-1")
            XCTFail("Failed upload must not attach")
        } catch let error as FileTransferError {
            if case .server = error {} else { XCTFail("Unexpected error: \(error)") }
        }
        let failedRequest = await failedAttach.request()
        XCTAssertNil(failedRequest)
    }

    func testCancellationCancelsKnownSessionAndCleansTempsWithoutDeletingSource() async throws {
        let source = try makeTemporaryFile(contents: Data("payload".utf8))
        defer { try? FileManager.default.removeItem(at: source) }
        let tempBefore = Self.stagingDirectories()
        let transport = FakeUploadTransport(partSize: 4)
        await transport.setHangsDuringPart()
        let service = try await makeTransfer(transport: transport)
        let start = Date()
        let task = Task { try await service.uploadFile(fileURL: source) }

        for _ in 0..<200 {
            if await transport.hasStartedPart() { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        let partStarted = await transport.hasStartedPart()
        XCTAssertTrue(partStarted)
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled upload should throw")
        } catch {}

        let elapsed = Date().timeIntervalSince(start)
        let calls = await transport.snapshot()
        XCTAssertEqual(calls.cancelCount, 1)
        XCTAssertLessThan(elapsed, 5.0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
        XCTAssertEqual(Self.stagingDirectories(), tempBefore)
    }

    @MainActor
    func testProductionGrpcRefreshUsesRpcDeadlineAndBoundsAnAlreadyRunningRefresh() async throws {
        let recorder = HangingTokenRecorder()
        let transport = HTTP2ServerTransport.Posix(
            address: .ipv4(host: "127.0.0.1", port: 0),
            transportSecurity: .plaintext
        )
        let server = GRPCServer(transport: transport, services: [HangingCreateTokenService(recorder: recorder)])
        let serverTask = Task { try await server.serve() }
        defer { server.beginGracefulShutdown() }
        let address = try await waitForListeningAddress(transport)
        let session = SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)")
        session.accessToken = "stale-token"
        session.accessTokenExpiresAt = Date(timeIntervalSinceNow: -60)
        session.refreshToken = "refresh-token"
        let grpc = GrpcManager(
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

        let stub = try await grpc.usersStub()
        var options = CallOptions.defaults
        options.timeout = .milliseconds(200)
        let firstBoundedStart = Date()
        do {
            _ = try await stub.getUser(Barkcloud_Users_GetUserRequest(), options: options)
            XCTFail("Hanging refresh should stay within the real RPC deadline")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }
        XCTAssertLessThan(Date().timeIntervalSince(firstBoundedStart), 1.0)
        try await recorder.waitForCallCount(1)
        try await Task.sleep(for: .milliseconds(50))

        let unboundedRefresh = Task { await grpc.accessToken(forMethod: "GetUser") }
        try await recorder.waitForCallCount(2)
        let start = Date()
        do {
            _ = try await stub.getUser(Barkcloud_Users_GetUserRequest(), options: options)
            XCTFail("Waiting on an existing refresh should stay within the caller deadline")
        } catch let rpc as RPCError {
            XCTAssertEqual(rpc.code, .deadlineExceeded)
        }
        let elapsed = Date().timeIntervalSince(start)
        let capturedTimeouts = await recorder.timeouts()
        let freshToken = await unboundedRefresh.value

        XCTAssertLessThan(elapsed, 1.0)
        XCTAssertEqual(freshToken, "fresh-token")
        XCTAssertEqual(capturedTimeouts.count, 2)
        XCTAssertNotNil(capturedTimeouts[0])
        XCTAssertNil(capturedTimeouts[1])
        session.clearSession()
        await grpc.shutdown()
        server.beginGracefulShutdown()
        _ = try? await serverTask.value
    }

    private func makeTransfer(
        transport: FakeUploadTransport,
        clock: TestUploadClock = TestUploadClock()
    ) async throws -> FileTransferService {
        let session = await MainActor.run { SessionStore(service: "BarkCloudKitTests-\(UUID().uuidString)") }
        let grpc = GrpcManager(session: session, serverConfig: .production)
        return FileTransferService(grpc: grpc, uploadTransport: transport, clock: clock)
    }

    private func makeTemporaryFile(contents: Data) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try contents.write(to: url)
        return url
    }

    private static func stagingDirectories() -> Set<String> {
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: FileManager.default.temporaryDirectory,
            includingPropertiesForKeys: nil
        )) ?? []
        return Set(urls.map(\.lastPathComponent).filter { $0.hasPrefix("BarkCloudUpload-") || $0.hasPrefix("BarkCloudStage-") })
    }
}

private final class TestUploadClock: UploadClock, @unchecked Sendable {
    private let lock = NSLock()
    private var instant = Date(timeIntervalSince1970: 0)
    private var recordedSleeps: [Duration] = []

    func now() -> Date {
        lock.lock()
        defer { lock.unlock() }
        return instant
    }

    func sleep(for duration: Duration) async throws {
        try Task.checkCancellation()
        recordSleep(duration)
    }

    private func recordSleep(_ duration: Duration) {
        lock.lock()
        defer { lock.unlock() }
        recordedSleeps.append(duration)
        let components = duration.components
        instant.addTimeInterval(Double(components.seconds) + Double(components.attoseconds) / 1e18)
    }

    var sleeps: [Duration] {
        lock.lock()
        defer { lock.unlock() }
        return recordedSleeps
    }
}

private enum FakeFailure: Sendable, Equatable {
    case network
    case lostCreate
    case lostPartAcknowledgement
    case invalidToken
    case incompleteParts
    case deadlineAfterAccept
    case deadline
    case quota
    case localFailure
}

private actor FakeUploadTransport: UploadSessionTransport {
    struct Snapshot {
        var createCount = 0
        var idempotencyKeys: [String] = []
        var createdSessionIDs: [String] = []
        var fileTypes: [Barkcloud_Files_UploadFileType] = []
        var fileSizes: [Int64] = []
        var hashes: [String] = []
        var parts: [UploadPartRequest] = []
        var partContents: [Data] = []
        var tokens: [String] = []
        var completeCount = 0
        var getCount = 0
        var getTimeouts: [Duration?] = []
        var controlTimeouts: [Duration?] = []
        var resumeCount = 0
        var cancelCount = 0
    }

    private let partSize: Int64
    private var completeStatus: FileUploadSessionStatus
    private var getDefaultStatus: FileUploadSessionStatus = .ready
    private var current: FileUploadSession?
    private var nextCreateFailures: [FakeFailure] = []
    private var nextPartFailures: [FakeFailure] = []
    private var nextCompleteFailures: [FakeFailure] = []
    private var nextGetFailures: [FakeFailure] = []
    private var nextResumeFailures: [FakeFailure] = []
    private var nextCreateSessions: [FileUploadSession] = []
    private var sessionsByIdempotencyKey: [String: FileUploadSession] = [:]
    private var getResults: [FileUploadSession] = []
    private var resumeResults: [FileUploadSession] = []
    private var initialParts: [FileUploadPart] = []
    private var snapshotValue = Snapshot()
    private var hangParts = false
    private var partStarted = false

    init(partSize: Int64, completeStatus: FileUploadSessionStatus = .ready) {
        self.partSize = partSize
        self.completeStatus = completeStatus
    }

    func enqueueCreateFailure(_ failure: FakeFailure) { nextCreateFailures.append(failure) }
    func enqueuePartFailure(_ failure: FakeFailure) { nextPartFailures.append(failure) }
    func enqueueCompleteFailure(_ failure: FakeFailure) { nextCompleteFailures.append(failure) }
    func enqueueGetFailure(_ failure: FakeFailure) { nextGetFailures.append(failure) }
    func enqueueResumeFailure(_ failure: FakeFailure) { nextResumeFailures.append(failure) }
    func enqueueGet(_ session: FileUploadSession) { getResults.append(session) }
    func enqueueResume(_ session: FileUploadSession) { resumeResults.append(session) }
    func setNextCreateSession(_ session: FileUploadSession) { nextCreateSessions.append(session) }
    func setInitialParts(_ parts: [FileUploadPart]) { initialParts = parts }
    func setGetDefaultStatus(_ status: FileUploadSessionStatus) { getDefaultStatus = status }
    func setHangsDuringPart() { hangParts = true }
    func hasStartedPart() -> Bool { partStarted }
    func snapshot() -> Snapshot { snapshotValue }

    func create(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String,
        fileType: Barkcloud_Files_UploadFileType,
        timeout: Duration?
    ) async throws -> FileUploadSession {
        snapshotValue.createCount += 1
        snapshotValue.controlTimeouts.append(timeout)
        snapshotValue.idempotencyKeys.append(idempotencyKey)
        snapshotValue.fileTypes.append(fileType)
        snapshotValue.fileSizes.append(fileSize)
        snapshotValue.hashes.append(sha256)
        if !nextCreateFailures.isEmpty {
            let failure = nextCreateFailures.removeFirst()
            if failure == .lostCreate {
                let accepted = makeSession(status: .uploading, fileSize: fileSize, partSize: partSize, token: "initial-token", parts: initialParts)
                sessionsByIdempotencyKey[idempotencyKey] = accepted
                current = accepted
                snapshotValue.createdSessionIDs.append(accepted.sessionID)
                throw URLError(.networkConnectionLost)
            }
            try throwFailure(failure)
        }
        if let existing = sessionsByIdempotencyKey[idempotencyKey] {
            current = existing
            snapshotValue.createdSessionIDs.append(existing.sessionID)
            return existing
        }
        let created = nextCreateSessions.isEmpty
            ? makeSession(status: .uploading, fileSize: fileSize, partSize: partSize, token: "initial-token", parts: initialParts)
            : nextCreateSessions.removeFirst()
        sessionsByIdempotencyKey[idempotencyKey] = created
        current = created
        snapshotValue.createdSessionIDs.append(created.sessionID)
        return created
    }

    func get(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        snapshotValue.getCount += 1
        snapshotValue.getTimeouts.append(timeout)
        snapshotValue.controlTimeouts.append(timeout)
        if !nextGetFailures.isEmpty { try throwFailure(nextGetFailures.removeFirst()) }
        if !getResults.isEmpty {
            current = getResults.removeFirst()
            return current!
        }
        guard let current else { throw FileTransferError.badUploadResponse }
        if getDefaultStatus == .ready {
            let ready = makeSession(status: .ready, fileSize: current.fileSize, partSize: current.partSize, token: nil)
            self.current = ready
            return ready
        }
        let session = makeSession(
            status: getDefaultStatus,
            fileSize: current.fileSize,
            partSize: current.partSize,
            token: current.uploadToken,
            parts: current.uploadedParts
        )
        self.current = session
        return session
    }

    func resume(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        snapshotValue.resumeCount += 1
        snapshotValue.controlTimeouts.append(timeout)
        if !nextResumeFailures.isEmpty { try throwFailure(nextResumeFailures.removeFirst()) }
        if !resumeResults.isEmpty {
            current = resumeResults.removeFirst()
            return current!
        }
        guard let current else { throw FileTransferError.badUploadResponse }
        let resumed = makeSession(
            status: current.status,
            fileSize: current.fileSize,
            partSize: current.partSize,
            token: "rotated-token",
            parts: current.uploadedParts
        )
        self.current = resumed
        return resumed
    }

    func complete(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        snapshotValue.completeCount += 1
        snapshotValue.controlTimeouts.append(timeout)
        guard let current else { throw FileTransferError.badUploadResponse }
        if !nextCompleteFailures.isEmpty {
            let failure = nextCompleteFailures.removeFirst()
            if failure == .deadlineAfterAccept {
                self.current = makeSession(status: .processing, fileSize: current.fileSize, partSize: current.partSize, token: nil)
            }
            try throwFailure(failure)
        }
        let complete = makeSession(status: completeStatus, fileSize: current.fileSize, partSize: current.partSize, token: nil)
        self.current = complete
        return complete
    }

    func cancel(sessionID: String, timeout: Duration?) async throws -> FileUploadSession {
        snapshotValue.cancelCount += 1
        snapshotValue.controlTimeouts.append(timeout)
        guard let current else { throw FileTransferError.badUploadResponse }
        let cancelled = makeSession(status: .cancelled, fileSize: current.fileSize, partSize: current.partSize, token: nil)
        self.current = cancelled
        return cancelled
    }

    func uploadPart(_ request: UploadPartRequest) async throws -> UploadPartAcknowledgement {
        partStarted = true
        if hangParts { try await Task.sleep(for: .seconds(60)) }
        snapshotValue.parts.append(request)
        snapshotValue.tokens.append(request.token)
        snapshotValue.partContents.append(try Data(contentsOf: request.fileURL))
        if !nextPartFailures.isEmpty {
            let failure = nextPartFailures.removeFirst()
            if failure == .lostPartAcknowledgement {
                try recordAcceptedPart(request)
                throw URLError(.networkConnectionLost)
            }
            try throwFailure(failure)
        }
        try recordAcceptedPart(request)
        return UploadPartAcknowledgement(partNumber: request.partNumber, size: request.size)
    }

    private func recordAcceptedPart(_ request: UploadPartRequest) throws {
        guard let current else { throw FileTransferError.badUploadResponse }
        let part = FileUploadPart(partNumber: request.partNumber, size: request.size, hasETag: true)
        self.current = makeSession(
            status: current.status,
            fileSize: current.fileSize,
            partSize: current.partSize,
            token: current.uploadToken,
            parts: current.uploadedParts.filter { $0.partNumber != request.partNumber } + [part]
        )
    }

    private func throwFailure(_ failure: FakeFailure) throws -> Never {
        switch failure {
        case .network, .lostCreate, .lostPartAcknowledgement: throw URLError(.networkConnectionLost)
        case .localFailure: throw URLError(.fileDoesNotExist)
        case .quota:
            throw FileTransferError.http(
                status: 413,
                errorCode: "64E94D14-CA45-4827-876C-701FE21986B2",
                message: "quota"
            )
        case .invalidToken:
            throw FileTransferError.http(status: 401, errorCode: Upload2Uploader.invalidUploadTokenCode, message: "invalid token")
        case .incompleteParts:
            var metadata = Metadata()
            metadata.addString(Upload2Uploader.incompletePartsCode, forKey: "x-error-code")
            throw RPCError(code: .failedPrecondition, message: "missing parts", metadata: metadata)
        case .deadlineAfterAccept, .deadline:
            throw RPCError(code: .deadlineExceeded, message: "deadline")
        }
    }
}

private actor FakeAvatarUpload: AvatarUploadTransport {
    private var result = makeSession(status: .ready, fileSize: 4, partSize: 4, token: nil)
    private var failure: FileTransferError?
    private var fileType: Barkcloud_Files_UploadFileType?
    private var contentType: String?
    private var uploads = 0

    func setResult(_ result: FileUploadSession) { self.result = result }
    func setFailure(_ error: FileTransferError) { failure = error }
    func clearFailure() { failure = nil }
    func receivedFileType() -> Barkcloud_Files_UploadFileType? { fileType }
    func receivedContentType() -> String? { contentType }
    func uploadCount() -> Int { uploads }

    func upload(data: Data, fileName: String, contentType: String, fileType: Barkcloud_Files_UploadFileType) async throws -> FileUploadSession {
        uploads += 1
        self.fileType = fileType
        self.contentType = contentType
        if let failure { throw failure }
        return result
    }
}

private actor FakeProfilePictureTransport: ProfilePictureTransport {
    private var ids: [String] = []
    func setProfilePicture(fileID: String, timeout: Duration?) async throws { ids.append(fileID) }
    func fileIDs() -> [String] { ids }
}

private actor FakeCloudAttachTransport: CloudAttachTransport {
    private var lastRequest: Barkcloud_Files_AttachFileRequest?
    func attach(_ request: Barkcloud_Files_AttachFileRequest, timeout: Duration?) async throws {
        lastRequest = request
    }
    func request() -> Barkcloud_Files_AttachFileRequest? { lastRequest }
}

private func makeSession(
    status: FileUploadSessionStatus,
    fileSize: Int64,
    partSize: Int64,
    token: String?,
    parts: [FileUploadPart] = [],
    errorCode: String? = nil,
    errorMessage: String? = nil
) -> FileUploadSession {
    FileUploadSession(
        sessionID: "session-1",
        fileID: "file-1",
        status: status,
        fileSize: fileSize,
        partSize: partSize,
        expiresAt: nil,
        uploadToken: token,
        uploadedParts: parts,
        errorCode: errorCode,
        errorMessage: errorMessage
    )
}

private func sha256(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

private actor HangingTokenRecorder {
    private var timeoutValues: [String?] = []

    func record(timeout: String?) {
        timeoutValues.append(timeout)
    }

    func waitForCallCount(_ count: Int) async throws {
        for _ in 0..<200 {
            if timeoutValues.count >= count { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw FileTransferError.badUploadResponse
    }

    func timeouts() -> [String?] { timeoutValues }
}

private struct HangingCreateTokenService: RegistrableRPCService {
    let recorder: HangingTokenRecorder

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        router.registerHandler(
            forMethod: Barkcloud_Identity_IdentityApi.Method.CreateToken.descriptor,
            deserializer: ProtobufDeserializer<Barkcloud_Identity_CreateTokenRequest>(),
            serializer: ProtobufSerializer<Barkcloud_Identity_CreateTokenResponse>()
        ) { request, _ in
            let timeout = request.metadata[stringValues: "grpc-timeout"].first(where: { _ in true })
            await recorder.record(timeout: timeout)
            try? await Task.sleep(for: .seconds(2))
            var token = Barkcloud_Identity_Token()
            token.value = "fresh-token"
            token.expirationDate = SwiftProtobuf.Google_Protobuf_Timestamp(date: Date(timeIntervalSinceNow: 3600))
            var response = Barkcloud_Identity_CreateTokenResponse()
            response.accessToken = token
            return StreamingServerResponse(single: ServerResponse(accepted: .success(.init(message: response))))
        }
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
