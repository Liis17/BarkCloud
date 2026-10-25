import Foundation
import XCTest
@testable import BarkCloudKit

final class ServerConfigAndTlsBypassTests: XCTestCase {
    func testProductionConfigurationKeepsCertificateVerificationEnabled() {
        XCTAssertFalse(ServerConfig.production.allowSelfSigned)
    }

    func testMissingSavedBypassKeyDefaultsToFalse() {
        withIsolatedStore { defaults in
            defaults.set(true, forKey: "BarkCloud.server.configured")
            XCTAssertFalse(ServerConfig.current.allowSelfSigned)
        }
    }

    func testExplicitlySavedBypassValuesArePreserved() {
        withIsolatedStore { _ in
            var config = ServerConfig.production
            config.allowSelfSigned = true
            config.persist()
            XCTAssertTrue(ServerConfig.current.allowSelfSigned)

            config.allowSelfSigned = false
            config.persist()
            XCTAssertFalse(ServerConfig.current.allowSelfSigned)
        }
    }

    func testPersistAndCurrentRoundTrip() {
        withIsolatedStore { _ in
            let config = ServerConfig(
                identityHost: "identity.example.test", identityPort: 7440,
                usersHost: "users.example.test", usersPort: 7441,
                filesHost: "files.example.test", filesPort: 7445,
                useTLS: true, allowSelfSigned: true
            )

            config.persist()

            XCTAssertEqual(ServerConfig.current, config)
        }
    }

    func testClearRemovesConfigurationAndRestoresProductionDefaults() {
        withIsolatedStore { _ in
            var config = ServerConfig.production
            config.filesHost = "files.example.test"
            config.allowSelfSigned = true
            config.persist()

            ServerConfig.clear()

            XCTAssertFalse(ServerConfig.isConfigured)
            XCTAssertEqual(ServerConfig.current, .production)
        }
    }

    func testSavedBypassIsAcceptedForInitiallyLoadedAddresses() {
        let config = configuration(allowSelfSigned: true)
        let consent = TlsBypassConsent(configuration: config)

        XCTAssertTrue(consent.isBypassEnabled)
        XCTAssertTrue(consent.allowsBypass(for: config))
    }

    func testNormalizedEquivalentAddressesDoNotResetConsent() {
        let config = configuration(allowSelfSigned: true)
        var consent = TlsBypassConsent(configuration: config)

        consent.updateAddresses(
            identityHost: " HTTPS://IDENTITY.EXAMPLE.TEST/ ", identityPort: "07020",
            usersHost: "http://USERS.EXAMPLE.TEST/", usersPort: "07021",
            filesHost: "FILES.EXAMPLE.TEST", filesPort: "07025"
        )

        XCTAssertTrue(consent.isBypassEnabled)
        XCTAssertTrue(consent.allowsBypass(for: config))
    }

    func testAnyAddressChangeClearsConsentAndRevertingDoesNotRestoreIt() {
        let original = configuration(allowSelfSigned: true)
        let changes: [(String, (inout ServerConfig) -> Void)] = [
            ("identity host", { $0.identityHost = "other-identity.example.test" }),
            ("identity port", { $0.identityPort += 1 }),
            ("users host", { $0.usersHost = "other-users.example.test" }),
            ("users port", { $0.usersPort += 1 }),
            ("files host", { $0.filesHost = "other-files.example.test" }),
            ("files port", { $0.filesPort += 1 })
        ]

        for (label, change) in changes {
            var changed = original
            change(&changed)
            var consent = TlsBypassConsent(configuration: original)
            update(&consent, using: changed)

            XCTAssertFalse(consent.isBypassEnabled, "\(label) should reset the switch")
            XCTAssertFalse(consent.allowsBypass(for: changed), "\(label) should invalidate consent")

            update(&consent, using: original)
            XCTAssertFalse(consent.isBypassEnabled, "\(label) reverting must not restore consent")
            XCTAssertFalse(consent.allowsBypass(for: original), "\(label) reverting must not authorize bypass")
        }
    }

    func testSaveRechecksCandidateAddressesAgainstConfirmedAddresses() {
        let original = configuration(allowSelfSigned: true)
        var candidate = original
        candidate.filesPort += 1
        let consent = TlsBypassConsent(configuration: original)

        XCTAssertFalse(consent.allowsBypass(for: candidate))
    }

    func testBypassRequiresConfirmationAndDisablingTLSDoesNotRestoreIt() {
        let config = configuration(allowSelfSigned: false)
        var consent = TlsBypassConsent(configuration: config)

        XCTAssertFalse(consent.isBypassEnabled)
        consent.confirmBypass()
        XCTAssertTrue(consent.isBypassEnabled)

        consent.setTLSEnabled(false)
        XCTAssertFalse(consent.isTLSEnabled)
        XCTAssertFalse(consent.isBypassEnabled)

        consent.setTLSEnabled(true)
        XCTAssertTrue(consent.isTLSEnabled)
        XCTAssertFalse(consent.isBypassEnabled)
        XCTAssertFalse(consent.allowsBypass(for: config))
    }

    private func configuration(allowSelfSigned: Bool) -> ServerConfig {
        ServerConfig(
            identityHost: "identity.example.test", identityPort: 7020,
            usersHost: "users.example.test", usersPort: 7021,
            filesHost: "files.example.test", filesPort: 7025,
            useTLS: true, allowSelfSigned: allowSelfSigned
        )
    }

    private func update(_ consent: inout TlsBypassConsent, using config: ServerConfig) {
        consent.updateAddresses(
            identityHost: config.identityHost,
            identityPort: String(config.identityPort),
            usersHost: config.usersHost,
            usersPort: String(config.usersPort),
            filesHost: config.filesHost,
            filesPort: String(config.filesPort)
        )
    }

    private func withIsolatedStore(_ body: (UserDefaults) -> Void) {
        let suiteName = "BarkCloudKitTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        ServerConfig.storeOverride = defaults
        defer {
            defaults.removePersistentDomain(forName: suiteName)
            ServerConfig.storeOverride = nil
        }
        body(defaults)
    }
}
