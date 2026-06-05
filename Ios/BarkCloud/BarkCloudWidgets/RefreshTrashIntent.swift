import AppIntents
import BarkCloudKit
import WidgetKit

/// Интерактивная кнопка на виджете корзины (iOS 17+). По образцу
/// `RefreshStorageIntent`: поднимает временный gRPC-стек прямо в процессе виджета
/// (адреса — из `ServerConfig` App Group, токены — из общего keychain), тянет
/// сводку корзины (точный счётчик + ближайший дедлайн), пишет её в App Group и
/// перерисовывает виджет. `openAppWhenRun = false` — приложение не выводится вперёд.
struct RefreshTrashIntent: AppIntent {
    static var title: LocalizedStringResource = "Обновить корзину"
    static var description = IntentDescription("Подтянуть число файлов в корзине BarkCloud.")
    static var openAppWhenRun: Bool = false

    @MainActor
    func perform() async throws -> some IntentResult {
        let grpc = GrpcManager(session: SessionStore())
        let cloud = CloudRepository(grpc: grpc, transfer: FileTransferService(grpc: grpc))
        if let summary = try? await cloud.trashSummary() {
            let d = UserDefaults(suiteName: "group.com.barkfluff.BarkCloud")
            d?.set(summary.count, forKey: "trash_widget.count")
            d?.set(summary.oldestPurgeAt?.timeIntervalSince1970 ?? 0, forKey: "trash_widget.purgeAt")
        }
        await grpc.shutdown()
        WidgetCenter.shared.reloadTimelines(ofKind: "TrashWidget")
        return .result()
    }
}
