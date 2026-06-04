import Foundation
import GRPCCore

/// Прикрепляет `x-auth-token` к запросам. Сам токен поставляет `GrpcManager`
/// через замыкание по имени метода: он проактивно обновляет токен, если тот
/// истекает, а публичным RPC (например `CreateToken`) отдаёт `nil` — они уходят
/// без токена, что заодно исключает рекурсию обновления.
struct AuthInterceptor: ClientInterceptor {
    let accessTokenForMethod: @Sendable (_ method: String) async -> String?

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
        if let token = await accessTokenForMethod(context.descriptor.method), !token.isEmpty {
            request.metadata.replaceOrAddString(token, forKey: "x-auth-token")
        }
        return try await next(request, context)
    }
}
