import Foundation
import GRPCCore
import XCTest
@testable import BarkCloudKit

final class CloudErrorCodesTests: XCTestCase {
    func testKnownUploadServerCodeUsesDomainLocalization() {
        let code = DomainErrorCodes.profilePictureHasNotValidType
        let expected = DomainErrorCodes.localizedMessage(for: code)
        XCTAssertNotNil(expected)

        XCTAssertEqual(
            domainErrorMessage(FileTransferError.server(errorCode: code, message: "server text")),
            expected
        )
        XCTAssertEqual(
            domainErrorMessage(FileTransferError.http(status: 415, errorCode: code, message: "server text")),
            expected
        )
    }

    func testUploadErrorsUseTheirDescriptionWhenNoDomainMessageExists() {
        let errors: [FileTransferError] = [
            .avatarTooLarge,
            .emptyFile,
            .server(errorCode: "unknown", message: "server text"),
            .server(errorCode: nil, message: nil),
            .http(status: 422, errorCode: nil, message: "http text"),
            .http(status: 422, errorCode: nil, message: nil)
        ]

        for error in errors {
            XCTAssertEqual(domainErrorMessage(error), error.errorDescription)
        }
    }

    func testRPCAndArbitraryErrorBehaviorRemainsUnchanged() {
        var metadata = Metadata()
        metadata.addString(DomainErrorCodes.profilePictureHasNotValidType, forKey: "x-error-code")
        let knownRPC = RPCError(code: .failedPrecondition, message: "server text", metadata: metadata)
        XCTAssertEqual(
            domainErrorMessage(knownRPC),
            DomainErrorCodes.localizedMessage(for: DomainErrorCodes.profilePictureHasNotValidType)
        )

        let unknownRPC = RPCError(code: .unavailable, message: "network text")
        XCTAssertEqual(domainErrorMessage(unknownRPC), "network text")

        let emptyRPC = RPCError(code: .unavailable, message: "")
        let generic = String(localized: "error_generic")
        XCTAssertEqual(domainErrorMessage(emptyRPC), generic)
        XCTAssertEqual(domainErrorMessage(NSError(domain: "test", code: 1)), generic)
    }
}
