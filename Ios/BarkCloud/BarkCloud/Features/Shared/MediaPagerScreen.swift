import SwiftUI
import QuickLook
import UIKit
import BarkCloudKit

/// Полноэкранный просмотрщик с листанием влево/вправо между файлами коллекции.
///
/// Построен на **многоэлементном** `QLPreviewController` — он сам реализует
/// горизонтальный свайп между элементами, нижнюю ленту превью и зум (в отличие
/// от обёртки во внешний пейджер, где внутренний скролл QuickLook перехватывал бы
/// горизонтальные жесты). URL каждого элемента резолвится **лениво** через
/// `resolve(id)` (докачка оригинала / экспорт ассета), с предзагрузкой соседей;
/// когда URL текущего элемента готов — вызывается `refreshCurrentPreviewItem()`.
struct MediaPagerScreen: View {
    /// Идентификаторы элементов в порядке коллекции (fileID для облака,
    /// localIdentifier для устройства).
    let ids: [String]
    let startIndex: Int
    /// Резолвер: id → локальный URL файла (оригинал/экспорт). `nil` — не удалось.
    let resolve: (String) async -> URL?
    /// Догрузка следующей страницы коллекции при подходе к концу. Возвращает
    /// полный обновлённый список id. `nil` — пагинации нет (напр. медиатека
    /// устройства грузится целиком).
    var loadMore: (() async -> [String])? = nil
    let onClose: () -> Void

    var body: some View {
        NavigationStack {
            MediaPager(ids: ids, startIndex: startIndex, resolve: resolve, loadMore: loadMore)
                .ignoresSafeArea()
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button(String(localized: "action_close"), action: onClose)
                    }
                }
        }
    }
}

/// Резолверы URL для `MediaPagerScreen`.
enum MediaPagerResolver {
    /// Облачный файл: скачать через дисковый кеш (тот же путь, что
    /// `RemoteFilePreviewScreen`). Для фото с JpegView качаем именно его
    /// (`viewIDByFileID`: file_id оригинала → file_id JPEG-вида) — браузеро-/
    /// QuickLook-дружелюбный JPEG вместо тяжёлого HEIC-оригинала. Видео и файлы
    /// без вида резолвятся по своему оригинальному id.
    static func cloud(
        transfer: FileTransferService,
        cache: FileCacheService,
        viewIDByFileID: [String: String] = [:]
    ) -> (String) async -> URL? {
        { fileID in
            let downloadID = viewIDByFileID[fileID] ?? fileID
            return try? await cache.loadFile(fileId: downloadID, variant: .original) {
                let urls = try await transfer.tempDownloadURLs(fileIDs: [downloadID])
                guard let remote = urls[downloadID] else { throw FileTransferError.downloadFailed }
                return remote
            }
        }
    }

    /// Карта «file_id оригинала → file_id JPEG-вида» для фото-элементов. Видео и
    /// элементы без вида пропускаются (резолвятся по своему оригиналу).
    static func jpegViewMap(_ items: [MediaItem]) -> [String: String] {
        Dictionary(
            items.compactMap { item -> (String, String)? in
                guard !item.isVideo,
                      let view = item.asset?.jpegViewFileID,
                      !view.isEmpty else { return nil }
                return (item.id, view)
            },
            uniquingKeysWith: { first, _ in first }
        )
    }
}

private struct MediaPager: UIViewControllerRepresentable {
    let ids: [String]
    let startIndex: Int
    let resolve: (String) async -> URL?
    let loadMore: (() async -> [String])?

    func makeCoordinator() -> Coordinator { Coordinator(ids: ids, resolve: resolve, loadMore: loadMore) }

    func makeUIViewController(context: Context) -> QLPreviewController {
        let controller = QLPreviewController()
        controller.dataSource = context.coordinator
        context.coordinator.controller = controller
        let start = min(max(startIndex, 0), max(ids.count - 1, 0))
        controller.currentPreviewItemIndex = start
        // Сразу резолвим стартовый элемент и его соседей.
        context.coordinator.ensure(start)
        context.coordinator.ensure(start - 1)
        context.coordinator.ensure(start + 1)
        return controller
    }

    func updateUIViewController(_ controller: QLPreviewController, context: Context) {
        // Стартовый индекс, выставленный в makeUIViewController до загрузки view,
        // QuickLook иногда игнорирует — применяем его ещё раз однократно.
        if !context.coordinator.didApplyStart {
            context.coordinator.didApplyStart = true
            controller.currentPreviewItemIndex = min(max(startIndex, 0), max(ids.count - 1, 0))
        }
    }

    final class Coordinator: NSObject, QLPreviewControllerDataSource {
        var didApplyStart = false

        /// Прозрачный 1×1 PNG как заглушка для ещё не скачанных элементов — на
        /// чёрном фоне QuickLook выглядит как пустой экран загрузки.
        private static let placeholderURL: URL = {
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("ql-pager-placeholder.png")
            if !FileManager.default.fileExists(atPath: url.path) {
                let img = UIGraphicsImageRenderer(size: CGSize(width: 1, height: 1)).image { _ in }
                try? img.pngData()?.write(to: url)
            }
            return url
        }()

        private var ids: [String]
        private let resolve: (String) async -> URL?
        private let loadMore: (() async -> [String])?
        weak var controller: QLPreviewController?

        private var resolved: [Int: URL] = [:]
        private var inFlight: Set<Int> = []
        private var isLoadingMore = false
        private var exhausted = false

        init(ids: [String], resolve: @escaping (String) async -> URL?, loadMore: (() async -> [String])?) {
            self.ids = ids
            self.resolve = resolve
            self.loadMore = loadMore
        }

        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { ids.count }

        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            // Текущий + соседи: гарантируем, что соседний элемент будет готов к свайпу.
            ensure(index)
            ensure(index - 1)
            ensure(index + 1)
            maybeLoadMore(around: index)
            return (resolved[index] ?? Self.placeholderURL) as NSURL
        }

        /// При подходе к концу загруженного списка догружаем следующую страницу
        /// коллекции и пересобираем QuickLook (`reloadData`), сохраняя текущую
        /// позицию — так можно листать до самого конца без выхода к сетке.
        private func maybeLoadMore(around index: Int) {
            guard let loadMore, !isLoadingMore, !exhausted else { return }
            guard index >= ids.count - 2 else { return }
            isLoadingMore = true
            Task { @MainActor in
                let updated = await loadMore()
                isLoadingMore = false
                guard updated.count > ids.count else { exhausted = true; return }
                let current = controller?.currentPreviewItemIndex
                ids = updated
                controller?.reloadData()
                if let current { controller?.currentPreviewItemIndex = current }
            }
        }

        /// Лениво резолвит URL элемента; по готовности обновляет текущий элемент,
        /// если он всё ещё показан.
        func ensure(_ index: Int) {
            guard index >= 0, index < ids.count else { return }
            guard resolved[index] == nil, !inFlight.contains(index) else { return }
            inFlight.insert(index)
            let id = ids[index]
            Task { @MainActor in
                let url = await resolve(id)
                inFlight.remove(index)
                guard let url else { return }
                resolved[index] = url
                if controller?.currentPreviewItemIndex == index {
                    controller?.refreshCurrentPreviewItem()
                }
            }
        }
    }
}
