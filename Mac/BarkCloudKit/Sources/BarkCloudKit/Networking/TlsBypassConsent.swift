import Foundation

/// Tracks explicit consent to bypass TLS certificate validation for a server address set.
public struct TlsBypassConsent: Equatable, Sendable {
    public private(set) var isTLSEnabled: Bool
    public private(set) var isBypassEnabled: Bool

    private var addresses: AddressSet
    private var confirmedAddresses: AddressSet?

    public init(configuration: ServerConfig) {
        let addresses = AddressSet(configuration: configuration)
        self.addresses = addresses
        self.isTLSEnabled = configuration.useTLS
        self.isBypassEnabled = configuration.useTLS && configuration.allowSelfSigned
        self.confirmedAddresses = isBypassEnabled ? addresses : nil
    }

    /// Loads saved configuration without treating its initial addresses as edits.
    public mutating func load(_ configuration: ServerConfig) {
        addresses = AddressSet(configuration: configuration)
        isTLSEnabled = configuration.useTLS
        isBypassEnabled = configuration.useTLS && configuration.allowSelfSigned
        confirmedAddresses = isBypassEnabled ? addresses : nil
    }

    /// Updates the in-progress form addresses and invalidates consent after any normalized change.
    public mutating func updateAddresses(
        identityHost: String,
        identityPort: String,
        usersHost: String,
        usersPort: String,
        filesHost: String,
        filesPort: String
    ) {
        let updated = AddressSet(
            identityHost: identityHost,
            identityPort: identityPort,
            usersHost: usersHost,
            usersPort: usersPort,
            filesHost: filesHost,
            filesPort: filesPort
        )
        guard updated != addresses else { return }
        addresses = updated
        disableBypass()
    }

    public mutating func setTLSEnabled(_ enabled: Bool) {
        isTLSEnabled = enabled
        if !enabled { disableBypass() }
    }

    /// Call only after the user confirms the warning shown by the setup form.
    public mutating func confirmBypass() {
        guard isTLSEnabled else { return }
        isBypassEnabled = true
        confirmedAddresses = addresses
    }

    public mutating func disableBypass() {
        isBypassEnabled = false
        confirmedAddresses = nil
    }

    /// Rechecks the saved candidate against both the active and confirmed address sets.
    public func allowsBypass(for configuration: ServerConfig) -> Bool {
        guard configuration.useTLS,
              isTLSEnabled,
              isBypassEnabled,
              let confirmedAddresses,
              confirmedAddresses == addresses,
              confirmedAddresses == AddressSet(configuration: configuration) else {
            return false
        }
        return true
    }

    private struct AddressSet: Equatable, Sendable {
        let identityHost: String
        let identityPort: String
        let usersHost: String
        let usersPort: String
        let filesHost: String
        let filesPort: String

        init(configuration: ServerConfig) {
            self.init(
                identityHost: configuration.identityHost,
                identityPort: String(configuration.identityPort),
                usersHost: configuration.usersHost,
                usersPort: String(configuration.usersPort),
                filesHost: configuration.filesHost,
                filesPort: String(configuration.filesPort)
            )
        }

        init(
            identityHost: String,
            identityPort: String,
            usersHost: String,
            usersPort: String,
            filesHost: String,
            filesPort: String
        ) {
            self.identityHost = Self.normalizeHost(identityHost)
            self.identityPort = Self.normalizePort(identityPort)
            self.usersHost = Self.normalizeHost(usersHost)
            self.usersPort = Self.normalizePort(usersPort)
            self.filesHost = Self.normalizeHost(filesHost)
            self.filesPort = Self.normalizePort(filesPort)
        }

        private static func normalizeHost(_ raw: String) -> String {
            var host = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            for scheme in ["https://", "http://"] where host.lowercased().hasPrefix(scheme) {
                host.removeFirst(scheme.count)
                break
            }
            host = host.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            return host.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        }

        private static func normalizePort(_ raw: String) -> String {
            let port = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            return Int(port).map(String.init) ?? port
        }
    }
}
