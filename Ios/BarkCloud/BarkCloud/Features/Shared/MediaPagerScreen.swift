import SwiftUI
import QuickLook
import UIKit
import Photos
import BarkCloudKit

/// Действия над текущим файлом вьювера (для облачных коллекций). Передаётся в
/// `MediaPagerScreen`: включает плавающую панель внизу (поделиться / в альбом /
/// свойства / удалить) и меню «⋯» в тулбаре (скачать оригинал / скопировать в
/// буфер обмена).
struct MediaPagerActions {
    let albums: AlbumRepository
    /// `MediaItem` по id текущей страницы — имя файла, isVideo, метаданные для свойств.
    let item: (String) -> MediaItem?
    /// URL оригинала файла (без подмены на JPEG-вид) — для share и «Скачать оригинал».
    let resolveOriginal: (String) async -> URL?
    /// Удалить файл (вызывается после подтверждения; вьювер закрывается сам).
    let delete: (MediaItem) -> Void
    /// Добавить в существующий альбом. `true` — успех.
    let addToAlbum: (MediaItem, _ albumID: String) async -> Bool
    /// Создать новый альбом и добавить. `true` — успех.
    let createAlbumAndAdd: (MediaItem) async -> Bool
}

/// Действия вьювера галереи устройства (таб «Галерея», id = localIdentifier
/// ассета): плавающая панель внизу — в альбом (с загрузкой в облако при
/// необходимости), загрузка в облако, удаление.
struct MediaPagerDeviceActions {
    let albums: AlbumRepository
    /// Загрузить в облако (дефолтная папка по типу медиа; дедуп по хешу). `true` — успех.
    let upload: (String) async -> Bool
    /// Загрузить (при необходимости) и добавить в существующий альбом. `true` — успех.
    let addToAlbum: (_ id: String, _ albumID: String) async -> Bool
    /// Загрузить (при необходимости), создать альбом и добавить. `true` — успех.
    let createAlbumAndAdd: (String) async -> Bool
    /// Удалить с устройства (и из облака, если файл там уже есть). `false` —
    /// пользователь отменил системный диалог, вьювер остаётся открытым.
    let delete: (String) async -> Bool
}

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
    /// Действия над текущим облачным файлом. `nil` — без облачной панели.
    var actions: MediaPagerActions? = nil
    /// Действия над текущим ассетом устройства (таб «Галерея»). `nil` — без панели.
    var deviceActions: MediaPagerDeviceActions? = nil
    let onClose: () -> Void

    /// id текущей страницы пейджера (репортит координатор QuickLook).
    @State private var currentID: String?
    /// Идёт скачивание для share / копирования / сохранения оригинала.
    @State private var isBusy = false
    @State private var snackbarText: String?
    @State private var shareItem: ShareableURL?
    @State private var showAlbumPicker = false
    @State private var showDeleteConfirm = false
    @State private var propertiesTarget: FilePropertiesTarget?

    var body: some View {
        NavigationStack {
            MediaPager(
                ids: ids,
                startIndex: startIndex,
                resolve: resolve,
                loadMore: loadMore,
                onCurrentID: (actions == nil && deviceActions == nil) ? nil : { currentID = $0 }
            )
            .ignoresSafeArea()
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_close"), action: onClose)
                }
                if let actions {
                    ToolbarItem(placement: .topBarTrailing) { moreMenu(actions) }
                }
            }
            .overlay(alignment: .bottom) {
                if let actions {
                    actionBar(actions)
                } else if let deviceActions {
                    deviceActionBar(deviceActions)
                }
            }
            .overlay(alignment: .bottom) { snackbar }
        }
        .sheet(item: $shareItem) { item in
            ActivityViewController(activityItems: [item.url])
        }
        .sheet(item: $propertiesTarget) { FilePropertiesSheet(target: $0) }
        .sheet(isPresented: $showAlbumPicker) { albumPicker }
        .confirmationDialog(
            String(localized: "media_delete_title"),
            isPresented: $showDeleteConfirm,
            titleVisibility: .visible
        ) {
            Button(String(localized: "action_delete"), role: .destructive) {
                if let actions, let item = currentItem {
                    actions.delete(item)
                    onClose()
                }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
    }

    private var currentItem: MediaItem? {
        guard let actions, let currentID else { return nil }
        return actions.item(currentID)
    }

    // MARK: - Панель действий

    /// Плавающая панель внизу (облако): поделиться / в альбом / свойства / удалить.
    /// На время скачивания (share) заменяется спиннером.
    private func actionBar(_ actions: MediaPagerActions) -> some View {
        barContainer {
            HStack(spacing: 4) {
                barButton("square.and.arrow.up", labelKey: "files_action_share") {
                    Task { await share(actions) }
                }
                barButton("rectangle.stack.badge.plus", labelKey: "ctx_add_to_album") {
                    showAlbumPicker = true
                }
                barButton("info.circle", labelKey: "ctx_properties") {
                    if let asset = currentItem?.asset { propertiesTarget = .cloud(asset) }
                }
                barButton("trash", labelKey: "action_delete", tint: AppColors.error) {
                    showDeleteConfirm = true
                }
            }
            .disabled(currentItem == nil)
        }
    }

    /// Плавающая панель внизу (галерея устройства): в альбом / загрузить в облако /
    /// удалить. На время загрузки/удаления заменяется спиннером.
    private func deviceActionBar(_ actions: MediaPagerDeviceActions) -> some View {
        barContainer {
            HStack(spacing: 4) {
                barButton("rectangle.stack.badge.plus", labelKey: "ctx_add_to_album") {
                    showAlbumPicker = true
                }
                barButton("icloud.and.arrow.up", labelKey: "share_action_upload") {
                    Task { await uploadDevice(actions) }
                }
                barButton("trash", labelKey: "action_delete", tint: AppColors.error) {
                    Task { await deleteDevice(actions) }
                }
            }
            .disabled(currentID == nil)
        }
    }

    /// Общая капсула панели; при `isBusy` вместо кнопок — спиннер.
    private func barContainer<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        Group {
            if isBusy {
                ProgressView()
                    .frame(width: 56, height: 50)
            } else {
                content()
            }
        }
        .padding(.horizontal, 8)
        .background(.regularMaterial, in: Capsule())
        .shadow(color: .black.opacity(0.15), radius: 8, y: 2)
        .padding(.bottom, 12)
    }

    private func barButton(
        _ icon: String,
        labelKey: String.LocalizationValue,
        tint: Color = AppColors.accent,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 19, weight: .medium))
                .frame(width: 56, height: 50)
        }
        .tint(tint)
        .accessibilityLabel(Text(String(localized: labelKey)))
    }

    /// Меню «⋯» в тулбаре: скачать оригинал / скопировать в буфер обмена.
    private func moreMenu(_ actions: MediaPagerActions) -> some View {
        Menu {
            Button {
                Task { await downloadOriginal(actions) }
            } label: {
                Label(String(localized: "viewer_download_original"), systemImage: "square.and.arrow.down")
            }
            Button {
                Task { await copyToClipboard() }
            } label: {
                Label(String(localized: "viewer_copy_clipboard"), systemImage: "doc.on.doc")
            }
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .disabled(isBusy || currentItem == nil)
    }

    @ViewBuilder
    private var albumPicker: some View {
        if let actions, let item = currentItem {
            AlbumPickerSheet(
                albums: actions.albums,
                onPickExisting: { albumID in
                    Task {
                        snackbarText = await actions.addToAlbum(item, albumID)
                            ? String(localized: "media_added_to_album")
                            : String(localized: "viewer_action_failed")
                    }
                },
                onCreateNew: {
                    Task {
                        snackbarText = await actions.createAlbumAndAdd(item)
                            ? String(localized: "media_added_to_album")
                            : String(localized: "viewer_action_failed")
                    }
                }
            )
        } else if let deviceActions, let id = currentID {
            // Ассет устройства: перед добавлением может идти загрузка в облако —
            // на это время панель показывает спиннер (isBusy).
            AlbumPickerSheet(
                albums: deviceActions.albums,
                onPickExisting: { albumID in
                    Task {
                        isBusy = true
                        let ok = await deviceActions.addToAlbum(id, albumID)
                        isBusy = false
                        snackbarText = ok
                            ? String(localized: "media_added_to_album")
                            : String(localized: "viewer_action_failed")
                    }
                },
                onCreateNew: {
                    Task {
                        isBusy = true
                        let ok = await deviceActions.createAlbumAndAdd(id)
                        isBusy = false
                        snackbarText = ok
                            ? String(localized: "media_added_to_album")
                            : String(localized: "viewer_action_failed")
                    }
                }
            )
        }
    }

    @ViewBuilder
    private var snackbar: some View {
        if let text = snackbarText {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, (actions == nil && deviceActions == nil) ? 16 : 86)
                .task(id: text) {
                    try? await Task.sleep(nanoseconds: 2_000_000_000)
                    snackbarText = nil
                }
        }
    }

    // MARK: - Действия

    /// Системный Share Sheet с самим файлом: скачиваем оригинал (для видео он уже
    /// в кеше после просмотра) и отдаём под оригинальным именем.
    private func share(_ actions: MediaPagerActions) async {
        guard let item = currentItem, !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        guard let url = await actions.resolveOriginal(item.id) else {
            snackbarText = String(localized: "viewer_action_failed")
            return
        }
        shareItem = ShareableURL(url: Self.namedCopy(of: url, fileName: item.fileName))
    }

    /// «Скачать оригинал»: качаем оригинальный файл и сохраняем в медиатеку Фото.
    /// Сохранённую копию связываем с облачным файлом (как при загрузке) — для
    /// синхронного удаления и индикации «в облаке».
    private func downloadOriginal(_ actions: MediaPagerActions) async {
        guard let item = currentItem, !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        guard let url = await actions.resolveOriginal(item.id) else {
            snackbarText = String(localized: "viewer_action_failed")
            return
        }
        let status = await PHPhotoLibrary.requestAuthorization(for: .readWrite)
        guard status == .authorized || status == .limited else {
            snackbarText = String(localized: "viewer_action_failed")
            return
        }
        var placeholderID: String?
        do {
            try await PHPhotoLibrary.shared().performChanges {
                let request = PHAssetCreationRequest.forAsset()
                request.addResource(with: item.isVideo ? .video : .photo, fileURL: url, options: nil)
                placeholderID = request.placeholderForCreatedAsset?.localIdentifier
            }
            if let placeholderID {
                await CloudDeviceLinkStore.shared.link(fileID: item.id, localIdentifier: placeholderID)
            }
            snackbarText = String(localized: "viewer_saved_to_photos")
        } catch {
            snackbarText = String(localized: "viewer_action_failed")
        }
    }

    /// Скопировать в буфер обмена: фото — как изображение (показанный JPEG уже в
    /// кеше), видео — файлом через `NSItemProvider` (без загрузки байтов в память).
    private func copyToClipboard() async {
        guard let item = currentItem, !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        guard let url = await resolve(item.id) else {
            snackbarText = String(localized: "viewer_action_failed")
            return
        }
        if item.isVideo {
            guard let provider = NSItemProvider(contentsOf: Self.namedCopy(of: url, fileName: item.fileName)) else {
                snackbarText = String(localized: "viewer_action_failed")
                return
            }
            UIPasteboard.general.itemProviders = [provider]
        } else if let data = try? Data(contentsOf: url), let image = UIImage(data: data) {
            UIPasteboard.general.image = image
        } else {
            snackbarText = String(localized: "viewer_action_failed")
            return
        }
        snackbarText = String(localized: "viewer_copied")
    }

    /// Загрузить текущий ассет устройства в облако (дефолтная папка по типу медиа).
    private func uploadDevice(_ actions: MediaPagerDeviceActions) async {
        guard let id = currentID, !isBusy else { return }
        isBusy = true
        defer { isBusy = false }
        snackbarText = await actions.upload(id)
            ? String(localized: "viewer_uploaded")
            : String(localized: "viewer_action_failed")
    }

    /// Удалить текущий ассет устройства (и облачную копию, если она есть).
    /// Подтверждение показывает сама система (PhotoKit); при успехе вьювер
    /// закрывается, при отмене — остаётся открытым.
    private func deleteDevice(_ actions: MediaPagerDeviceActions) async {
        guard let id = currentID, !isBusy else { return }
        isBusy = true
        let ok = await actions.delete(id)
        isBusy = false
        if ok { onClose() }
    }

    /// Жёсткая ссылка (или копия) кеш-файла `original.<ext>` под оригинальным
    /// именем — чтобы Share Sheet и буфер обмена показывали настоящее имя файла.
    private static func namedCopy(of url: URL, fileName: String) -> URL {
        let name = (fileName as NSString).lastPathComponent
        guard !name.isEmpty else { return url }
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("viewer-share", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let dest = dir.appendingPathComponent(name)
            do { try FileManager.default.linkItem(at: url, to: dest) }
            catch { try FileManager.default.copyItem(at: url, to: dest) }
            return dest
        } catch {
            return url
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
    /// Репорт id текущей страницы (для панели действий). `nil` — не отслеживать.
    var onCurrentID: ((String) -> Void)? = nil

    func makeCoordinator() -> Coordinator {
        Coordinator(ids: ids, resolve: resolve, loadMore: loadMore, onCurrentID: onCurrentID)
    }

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
        context.coordinator.startIndexTracking()
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
        private let onCurrentID: ((String) -> Void)?
        weak var controller: QLPreviewController?

        private var resolved: [Int: URL] = [:]
        private var inFlight: Set<Int> = []
        private var isLoadingMore = false
        private var exhausted = false
        private var lastReportedIndex = -1
        private var indexTimer: Timer?

        init(
            ids: [String],
            resolve: @escaping (String) async -> URL?,
            loadMore: (() async -> [String])?,
            onCurrentID: ((String) -> Void)?
        ) {
            self.ids = ids
            self.resolve = resolve
            self.loadMore = loadMore
            self.onCurrentID = onCurrentID
        }

        deinit { indexTimer?.invalidate() }

        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { ids.count }

        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            // Текущий + соседи: гарантируем, что соседний элемент будет готов к свайпу.
            ensure(index)
            ensure(index - 1)
            ensure(index + 1)
            maybeLoadMore(around: index)
            // Индекс текущей страницы к этому моменту ещё может не обновиться —
            // репортим после завершения цикла раскладки.
            DispatchQueue.main.async { [weak self] in self?.reportCurrentIndex() }
            return (resolved[index] ?? Self.placeholderURL) as NSURL
        }

        /// Отслеживание текущей страницы для панели действий. У `QLPreviewController`
        /// нет колбэка смены элемента, а `previewItemAt` не вызывается, когда сосед
        /// уже закеширован (например, свайп назад у края) — поэтому дополнительно
        /// опрашиваем `currentPreviewItemIndex` лёгким таймером.
        func startIndexTracking() {
            guard onCurrentID != nil else { return }
            DispatchQueue.main.async { [weak self] in self?.reportCurrentIndex() }
            indexTimer = Timer.scheduledTimer(withTimeInterval: 0.4, repeats: true) { [weak self] _ in
                self?.reportCurrentIndex()
            }
        }

        private func reportCurrentIndex() {
            guard let onCurrentID, let controller else { return }
            let index = controller.currentPreviewItemIndex
            guard index != lastReportedIndex, index >= 0, index < ids.count else { return }
            lastReportedIndex = index
            onCurrentID(ids[index])
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
