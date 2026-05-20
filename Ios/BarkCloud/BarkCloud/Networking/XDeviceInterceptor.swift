import Foundation
import UIKit
import GRPCCore

struct XDeviceInterceptor: ClientInterceptor {
    let deviceId: String
    let deviceName: String

    init() {
        let vendorId = UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString
        self.deviceId = Base64Header.encode(vendorId)
        self.deviceName = Base64Header.encode(Self.modelName())
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
}
