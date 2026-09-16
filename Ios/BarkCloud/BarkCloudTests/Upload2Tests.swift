import CryptoKit
import Foundation
import XCTest
@testable import BarkCloud

final class Upload2Tests: XCTestCase {
    func testStreamingHasherHandlesSeveralFourMiBBlocks() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("barkcloud-upload2-\(UUID().uuidString).bin")
        defer { try? FileManager.default.removeItem(at: url) }

        let handle = try FileHandle(forWritingTo: url, createIfNeeded: true)
        var expected = SHA256()
        let block = Data((0..<4 * 1024 * 1024).map { UInt8($0 & 0xff) })
        for _ in 0..<3 {
            try handle.write(contentsOf: block)
            expected.update(data: block)
        }
        try handle.write(contentsOf: Data("tail".utf8))
        expected.update(data: Data("tail".utf8))
        try handle.close()

        let result = try UploadFileHasher.hashAndSize(at: url)
        let expectedHex = expected.finalize().map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(result.size, Int64(block.count * 3 + 4))
        XCTAssertEqual(result.sha256, expectedHex)
    }

    func testPersistedUploadJobContainsNoUploadTokenAndKeepsPostReadyIntent() {
        let job = UploadJob(
            id: "job",
            sourceKind: UploadJobSource.manual.rawValue,
            sourceFilePath: "/tmp/file",
            fileName: "photo.jpg",
            mimeType: "image/jpeg",
            directoryID: "directory",
            stateRaw: UploadJobState.uploading.rawValue,
            bytesSent: 4,
            totalBytes: 8,
            idempotencyKey: "idem",
            sessionID: "session",
            fileID: "file",
            sha256: String(repeating: "a", count: 64),
            partSize: 16 * 1024 * 1024,
            intentRaw: UploadPostReadyIntent.attachDirectory.rawValue
        )
        let labels = Set(Mirror(reflecting: job).children.compactMap(\.label))

        XCTAssertFalse(labels.contains { $0.localizedCaseInsensitiveContains("token") })
        XCTAssertFalse(labels.contains { $0.localizedCaseInsensitiveContains("multipart") })
        XCTAssertFalse(labels.contains { $0.localizedCaseInsensitiveContains("uploadURL") })
        XCTAssertEqual(UploadJobSnapshot(job).intent, .attachDirectory)
        XCTAssertTrue(UploadJobState.uploading.isActive)
        XCTAssertTrue(UploadJobState.uploadedNotAttached.isActive)
        XCTAssertFalse(UploadJobState.uploadedNotAttached.isTerminal)
        XCTAssertFalse(UploadJobState.uploading.isTerminal)
        XCTAssertTrue(UploadJobState.completed.isTerminal)
    }
}

private extension FileHandle {
    convenience init(forWritingTo url: URL, createIfNeeded: Bool) throws {
        if createIfNeeded {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
        try self.init(forWritingTo: url)
    }
}
