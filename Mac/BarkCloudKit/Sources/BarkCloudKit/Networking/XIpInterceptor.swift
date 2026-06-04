import Foundation
import GRPCCore

struct XIpInterceptor: ClientInterceptor {
    let ipAddress: String

    init() {
        self.ipAddress = Base64Header.encode(Self.firstNonLoopbackIPv4() ?? "")
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
        request.metadata.replaceOrAddString(ipAddress, forKey: "x-ip-address")
        return try await next(request, context)
    }

    private static func firstNonLoopbackIPv4() -> String? {
        var ifaddrPtr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddrPtr) == 0, let first = ifaddrPtr else { return nil }
        defer { freeifaddrs(ifaddrPtr) }
        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = ptr {
            defer { ptr = entry.pointee.ifa_next }
            let flags = Int32(entry.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0, (flags & IFF_LOOPBACK) == 0 else { continue }
            guard let sa = entry.pointee.ifa_addr, sa.pointee.sa_family == sa_family_t(AF_INET) else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let r = getnameinfo(sa, socklen_t(entry.pointee.ifa_addr.pointee.sa_len),
                                &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST)
            if r == 0 {
                return String(cString: host)
            }
        }
        return nil
    }
}
