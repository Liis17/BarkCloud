import AppIntents
import BarkCloudKit
import WidgetKit

/// Интерактивная кнопка на виджете хранилища (iOS 17+). В отличие от
/// `reloadTimelines`, реально тянет свежий снимок диска: поднимает временный gRPC-стек
/// прямо в процессе виджета (адреса — из `ServerConfig` App Group, токены — из
/// общего keychain), пишет снимок в App Group и просит систему перерисовать
/// виджет. `openAppWhenRun = false` — приложение не выводится на передний план.
struct RefreshStorageIntent: AppIntent {
    static var title: LocalizedStringResource = "Обновить хранилище"
    static var description = IntentDescription("Подтянуть свежие данные о заполнении диска BarkCloud.")
    static var openAppWhenRun: Bool = false

    @MainActor
    func perform() async throws -> some IntentResult {
        let grpc = GrpcManager(session: SessionStore())
        let transfer = FileTransferService(grpc: grpc)
        if let info = try? await transfer.storageInfo() {
            let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud")
            d?.set(info.used, forKey: "storage_widget.used")
            d?.set(info.limit, forKey: "storage_widget.limit")
            d?.set(info.diskTotal, forKey: "storage_widget.diskTotal")
            d?.set(info.diskOther, forKey: "storage_widget.diskOther")
            d?.set(info.diskS3, forKey: "storage_widget.diskS3")
            d?.set(Date().timeIntervalSince1970, forKey: "storage_widget.updatedAt")
        }
        await grpc.shutdown()
        WidgetCenter.shared.reloadTimelines(ofKind: "StorageWidget")
        return .result()
    }
}
