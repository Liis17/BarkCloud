import Foundation
#if os(iOS)
import UIKit
#endif
import GRPCCore

struct XOsInterceptor: ClientInterceptor {
    let osName: String

    init() {
        #if os(iOS)
        let device = UIDevice.current
        self.osName = Base64Header.encode("\(device.systemName) \(device.systemVersion)")
        #else
        let v = ProcessInfo.processInfo.operatingSystemVersion
        self.osName = Base64Header.encode("macOS \(v.majorVersion).\(v.minorVersion)")
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
        request.metadata.replaceOrAddString(osName, forKey: "x-os-name")
        return try await next(request, context)
    }
}
