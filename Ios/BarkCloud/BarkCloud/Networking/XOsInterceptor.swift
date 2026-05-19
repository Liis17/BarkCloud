import Foundation
import UIKit
import GRPCCore

struct XOsInterceptor: ClientInterceptor {
    let osName: String

    init() {
        let device = UIDevice.current
        self.osName = Base64Header.encode("\(device.systemName) \(device.systemVersion)")
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
