import Foundation
#if os(iOS)
import UIKit
#endif
import GRPCCore

struct XDeviceInterceptor: ClientInterceptor {
    let deviceId: String
    let deviceName: String

    init() {
        #if os(iOS)
        let vendorId = UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString
        self.deviceId = Base64Header.encode(vendorId)
        self.deviceName = Base64Header.encode(Self.modelName())
        #else
        self.deviceId = Base64Header.encode(Self.macInstallID())
        self.deviceName = Base64Header.encode(Self.macDeviceName())
        #endif
    }

    @concurrent
    func intercept<Input: Sendable, Output: Sendable>(
        request: StreamingClientRequest<Input>,
        context: ClientContext,
        next: @concurrent (
            _ request: StreamingClientRequest<Input>,
            _ context: ClientContext
        ) async throws -> StreamingClientResponse<Output>
    ) async throws -> StreamingClientResponse<Output> {
        var request = request
        request.metadata.replaceOrAddString(deviceId, forKey: "x-device-id")
        request.metadata.replaceOrAddString(deviceName, forKey: "x-device-name")
        return try await next(request, context)
    }

    #if os(iOS)
    private static func modelName() -> String {
        if let simModel = ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] {
            return simModel
        }
        var size: Int = 0
        sysctlbyname("hw.machine", nil, &size, nil, 0)
        var machine = [CChar](repeating: 0, count: size)
        sysctlbyname("hw.machine", &machine, &size, nil, 0)
        return String(cString: machine)
    }
    #else
    /// Имя устройства на macOS — локализованное имя Mac («MacBook Pro Лизы»),
    /// с откатом на сетевое hostname.
    private static func macDeviceName() -> String {
        Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    }

    /// Стабильный per-install идентификатор устройства (аналог iOS
    /// `identifierForVendor`): UUID, сохранённый при первом запуске. Общий для
    /// контейнер-app и FSKit-расширения, если они пишут в один App Group suite —
    /// suite задаётся через `XDeviceInterceptor.defaults` (по умолчанию standard).
    private static func macInstallID() -> String {
        let key = "barkcloud.device-install-id"
        let store = defaults
        if let existing = store.string(forKey: key) { return existing }
        let fresh = UUID().uuidString
        store.set(fresh, forKey: key)
        return fresh
    }

    /// Хранилище для per-install id. macOS-расширение/app подменяют на App Group
    /// suite (см. план 1.5/1.6), чтобы device-id совпадал между процессами.
    nonisolated(unsafe) static var defaults: UserDefaults = .standard
    #endif
}
