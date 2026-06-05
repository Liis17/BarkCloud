import AppIntents
import BarkCloudKit
import WidgetKit

/// Интерактивная кнопка на виджете корзины (iOS 17+). По образцу
/// `RefreshStorageIntent`: поднимает временный gRPC-стек прямо в процессе виджета
/// (адреса — из `ServerConfig` App Group, токены — из общего keychain), тянет
/// первый лист корзины, пишет счётчик в App Group и перерисовывает виджет.
/// `openAppWhenRun = false` — приложение не выводится на передний план.
struct RefreshTrashIntent: AppIntent {
    static var title: LocalizedStringResource = "Обновить корзину"
    static var description = IntentDescription("Подтянуть число файлов в корзине BarkCloud.")
    static var openAppWhenRun: Bool = false

    @MainActor
    func perform() async throws -> some IntentResult {
        let grpc = GrpcManager(session: SessionStore())
        let cloud = CloudRepository(grpc: grpc, transfer: FileTransferService(grpc: grpc))
        if let page = try? await cloud.listTrash(limit: 50) {
            let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud")
            d?.set(page.items.count, forKey: "trash_widget.count")
            d?.set(page.hasMore, forKey: "trash_widget.hasMore")
            let nearest = page.hasMore
                ? nil
                : page.items.map(\.purgeAt).filter { $0.timeIntervalSince1970 > 0 }.min()
            d?.set(nearest?.timeIntervalSince1970 ?? 0, forKey: "trash_widget.purgeAt")
        }
        await grpc.shutdown()
        WidgetCenter.shared.reloadTimelines(ofKind: "TrashWidget")
        return .result()
    }
}
