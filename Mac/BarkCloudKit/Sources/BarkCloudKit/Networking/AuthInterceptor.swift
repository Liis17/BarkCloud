import Foundation
import GRPCCore

/// Прикрепляет `x-auth-token` к запросам. Сам токен поставляет `GrpcManager`
/// через замыкание по имени метода: он проактивно обновляет токен, если тот
/// истекает, а публичным RPC (например `CreateToken`) отдаёт `nil` — они уходят
/// без токена, что заодно исключает рекурсию обновления.
struct AuthInterceptor: ClientInterceptor {
    let accessTokenForMethod: @Sendable (_ method: String, _ timeout: Duration?) async throws -> String?

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
        let timeout = Self.rpcTimeout(in: request.metadata)
        let token: String?
        if let timeout {
            token = try await AsyncDeadline.withTimeout(timeout) {
                try await self.accessTokenForMethod(context.descriptor.method, timeout)
            }
        } else {
            token = try await accessTokenForMethod(context.descriptor.method, nil)
        }
        if let token, !token.isEmpty {
            request.metadata.replaceOrAddString(token, forKey: "x-auth-token")
        }
        return try await next(request, context)
    }

    private static func rpcTimeout(in metadata: Metadata) -> Duration? {
        guard let encoded = metadata[stringValues: "grpc-timeout"].first(where: { _ in true }),
              encoded.count >= 2,
              let amount = Int64(encoded.dropLast()), amount >= 0 else { return nil }
        switch encoded.last {
        case "H": return .seconds(amount * 3_600)
        case "M": return .seconds(amount * 60)
        case "S": return .seconds(amount)
        case "m": return .milliseconds(amount)
        case "u": return .microseconds(amount)
        case "n": return .nanoseconds(amount)
        default: return nil
        }
    }
}
