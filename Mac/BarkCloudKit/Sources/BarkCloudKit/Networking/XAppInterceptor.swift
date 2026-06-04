import Foundation
import GRPCCore

struct XAppInterceptor: ClientInterceptor {
    let appName: String
    let appVersion: String

    init() {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.0"
        self.appName = Base64Header.encode("BarkCloud v.\(version)")
        self.appVersion = Base64Header.encode(version)
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
        request.metadata.replaceOrAddString(appName, forKey: "x-app-name")
        request.metadata.replaceOrAddString(appVersion, forKey: "x-app-version")
        return try await next(request, context)
    }
}
