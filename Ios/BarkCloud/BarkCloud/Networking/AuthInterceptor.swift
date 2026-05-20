import Foundation
import GRPCCore

struct AuthInterceptor: ClientInterceptor {
    let tokenProvider: @Sendable () async -> String?

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
        if let token = await tokenProvider(), !token.isEmpty {
            request.metadata.replaceOrAddString(token, forKey: "x-auth-token")
        }
        return try await next(request, context)
    }
}
