import Foundation
import XCTest
@testable import BarkCloud

final class FileCleanupTests: XCTestCase {

    private let fileManager = FileManager.default

    private func makeDirectory() throws -> URL {
        let directory = fileManager.temporaryDirectory
            .appendingPathComponent("BarkCloudFileCleanupTests-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    func testUploadArtifactsAreRemovedTogether() throws {
        let directory = try makeDirectory()
        defer { try? fileManager.removeItem(at: directory) }

        let staging = directory.appendingPathComponent("UploadStaging", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
        let source = staging.appendingPathComponent("source.bin")
        let multipart = staging.appendingPathComponent("multipart.body")
        try Data(repeating: 1, count: 2).write(to: source)
        try Data(repeating: 2, count: 3).write(to: multipart)

        UploadArtifactCleanup.remove(
            sourcePath: source.path,
            multipartPath: multipart.path,
            within: directory
        )

        XCTAssertFalse(fileManager.fileExists(atPath: source.path))
        XCTAssertFalse(fileManager.fileExists(atPath: multipart.path))
    }

    func testUploadArtifactCleanupDoesNotDeleteOutsideRoot() throws {
        let directory = try makeDirectory()
        let outside = fileManager.temporaryDirectory
            .appendingPathComponent("BarkCloudOutside-\(UUID().uuidString)")
        defer {
            try? fileManager.removeItem(at: directory)
            try? fileManager.removeItem(at: outside)
        }
        let staging = directory.appendingPathComponent("UploadStaging", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
        let multipart = staging.appendingPathComponent("multipart.body")
        try Data([1]).write(to: outside)
        try Data([2]).write(to: multipart)

        UploadArtifactCleanup.remove(
            sourcePath: outside.path,
            multipartPath: multipart.path,
            within: directory
        )

        XCTAssertTrue(fileManager.fileExists(atPath: outside.path))
        XCTAssertFalse(fileManager.fileExists(atPath: multipart.path))
    }

    func testUploadArtifactCleanupDoesNotDeleteDirectories() throws {
        let directory = try makeDirectory()
        defer { try? fileManager.removeItem(at: directory) }

        let nested = directory
            .appendingPathComponent("UploadStaging", isDirectory: true)
            .appendingPathComponent("nested", isDirectory: true)
        try fileManager.createDirectory(at: nested, withIntermediateDirectories: true)

        UploadArtifactCleanup.remove(
            sourcePath: nested.path,
            multipartPath: "",
            within: directory
        )

        XCTAssertTrue(fileManager.fileExists(atPath: nested.path))
    }

    func testUploadOrphanSweepKeepsReferencedAndFreshFiles() throws {
        let directory = try makeDirectory()
        defer { try? fileManager.removeItem(at: directory) }

        let now = Date()
        let referenced = directory.appendingPathComponent("referenced.body")
        let orphan = directory.appendingPathComponent("orphan.body")
        let fresh = directory.appendingPathComponent("fresh.body")
        try Data([1]).write(to: referenced)
        try Data([2]).write(to: orphan)
        try Data([3]).write(to: fresh)
        try fileManager.setAttributes(
            [.modificationDate: now.addingTimeInterval(-2 * 3600)],
            ofItemAtPath: orphan.path
        )

        UploadArtifactCleanup.purgeOrphans(
            in: directory,
            referencedPaths: [referenced.path],
            olderThan: 3600,
            now: now
        )

        XCTAssertTrue(fileManager.fileExists(atPath: referenced.path))
        XCTAssertTrue(fileManager.fileExists(atPath: fresh.path))
        XCTAssertFalse(fileManager.fileExists(atPath: orphan.path))
    }

    func testTemporarySweepRemovesOldEntriesAndKeepsFreshEntries() throws {
        let directory = try makeDirectory()
        defer { try? fileManager.removeItem(at: directory) }

        let now = Date()
        let old = directory.appendingPathComponent("old.tmp")
        let fresh = directory.appendingPathComponent("fresh.tmp")
        try Data([1]).write(to: old)
        try Data([2]).write(to: fresh)
        try fileManager.setAttributes(
            [.modificationDate: now.addingTimeInterval(-2 * 3600)],
            ofItemAtPath: old.path
        )

        TemporaryFileCleanup.purgeStale(in: directory, olderThan: 3600, now: now)

        XCTAssertFalse(fileManager.fileExists(atPath: old.path))
        XCTAssertTrue(fileManager.fileExists(atPath: fresh.path))
    }

    func testTemporaryFileRemovalAlsoRemovesItsEmptyTemporaryParent() throws {
        let directory = try makeDirectory()
        defer { try? fileManager.removeItem(at: directory) }

        let nested = directory.appendingPathComponent("export", isDirectory: true)
        let file = nested.appendingPathComponent("photo.dat")
        try fileManager.createDirectory(at: nested, withIntermediateDirectories: true)
        try Data([1]).write(to: file)

        TemporaryFileCleanup.removeFileAndEmptyParent(at: file, within: directory)

        XCTAssertFalse(fileManager.fileExists(atPath: file.path))
        XCTAssertFalse(fileManager.fileExists(atPath: nested.path))
    }
}
