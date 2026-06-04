import Foundation
import BarkCloudKit

/// Сетевые сервисы тома, поднятые из общей конфигурации (App Group UserDefaults —
/// адрес сервера) и Keychain (refresh-токен). Только Sendable-части — `SessionStore`
/// (@MainActor) удерживается внутри `GrpcManager`, наружу не отдаётся.
struct BarkCloudServices: Sendable {
    let grpc: GrpcManager
    let transfer: FileTransferService
    let cloud: CloudRepository
    let reader: RangeBlockReader
}

/// Ленивая инициализация сетевого слоя в процессе FSKit-расширения. Живёт, пока
/// том примонтирован. `SessionStore` создаётся на MainActor (он @MainActor),
/// дальше всё работает на акторах `GrpcManager`/`RangeBlockReader`.
@MainActor
enum BarkCloudSession {
    private static var services: BarkCloudServices?

    static func current() -> BarkCloudServices {
        if let s = services { return s }
        let store = SessionStore()
        let grpc = GrpcManager(session: store)
        let transfer = FileTransferService(grpc: grpc)
        let cloud = CloudRepository(grpc: grpc, transfer: transfer)
        let cacheDir = FileManager.default
            .urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("BarkCloud.Drive", isDirectory: true)
        let reader = RangeBlockReader(transfer: transfer, cacheDir: cacheDir)
        let s = BarkCloudServices(grpc: grpc, transfer: transfer, cloud: cloud, reader: reader)
        services = s
        return s
    }
}
