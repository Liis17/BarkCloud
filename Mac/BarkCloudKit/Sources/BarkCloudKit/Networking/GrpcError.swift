import Foundation
import GRPCCore

public extension RPCError {
    var errorCode: String? {
        for (key, value) in metadata {
            guard key.lowercased() == "x-error-code" else { continue }
            switch value {
            case .string(let s): return s
            case .binary(let bytes): return String(data: Data(bytes), encoding: .utf8)
            }
        }
        return nil
    }
}
