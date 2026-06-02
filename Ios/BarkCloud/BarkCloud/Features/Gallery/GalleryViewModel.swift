import Foundation
import Observation
import Photos
import UIKit

extension Notification.Name {
    /// MainScreen → GalleryScreen: пользователь только что переключился на
    /// вкладку Галерея. GalleryScreen ловит и зовёт `vm.reload()`, чтобы
    /// пересобрать список ассетов из PhotoKit.
    static let galleryDidFocus = Notification.Name("BarkCloud.galleryDidFocus")
}

/// Состояние вкладки «Галерея»: медиатека устройства (фото+видео через PhotoKit),
/// режим выбора и загрузка выбранных ассетов в облако. Индикация «уже в облаке» и
/// чтение оригиналов вынесены в [[CloudPresenceTracker]] и [[DeviceAssetResource]].
@MainActor
@Observable
final class GalleryViewModel {
    enum Access: Equatable { case undetermined, authorized, limited, denied }

    var access: Access = .undetermined
    var assets: [PHAsset] = []
    var selection: Set<String> = []   // localIdentifier выбранных
    var isSelecting = false
    var isUploading = false
    var uploadDone = 0
    var uploadTotal = 0
    var snackbar: String?
    /// Открыть sheet «Поделиться с пользователем» когда непусто. Резолв
    /// fileID идёт в `prepareShareWithUser(asset:)` (как `copyLink`/`makePublic`),
    /// чтобы загрузить device-ассет в облако если он там ещё не лежит.
    var pendingShareWithUser: ShareWithUserContext?
    /// URL созданной публичной ссылки → системный Share Sheet (Telegram/Mail/
    /// AirDrop/копировать). Заменяет старое прямое копирование в `UIPasteboard`.
    var pendingShareURL: ShareableURL?

    /// Трекер наличия файлов в облаке (по SHA256-хешу) — общий с пикером загрузки.
    let presence: CloudPresenceTracker

    private let cloud: CloudRepository
    private let albums: AlbumRepository
    private var didLoad = false

    /// Текущая выборка ассетов из PhotoKit — нужна, чтобы наблюдатель медиатеки
    /// (`PhotoLibraryObserver`) умел посчитать дельту изменений.
    private var fetchResult: PHFetchResult<PHAsset>?
    private var libraryObserver: PhotoLibraryObserver?

    init(cloud: CloudRepository, albums: AlbumRepository) {
        self.cloud = cloud
        self.albums = albums
        self.presence = CloudPresenceTracker(cloud: cloud)
    }

    var hasSelection: Bool { !selection.isEmpty }

    /// localIdentifier → есть ли это фото/видео уже в облаке. Passthrough к трекеру,
    /// чтобы не менять обращения экрана.
    var cloudPresence: [String: Bool] { presence.presence }

    /// Вызывать при появлении ячейки — лениво считает хеш и спрашивает сервер.
    func observeCloudPresence(for asset: PHAsset) { presence.observe(asset) }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await requestAndLoad()
    }

    func requestAndLoad() async {
        let status = await PHPhotoLibrary.requestAuthorization(for: .readWrite)
        apply(status)
        if access == .authorized || access == .limited {
            loadAssets()
        }
    }

    /// Пересобрать список ассетов из PhotoKit без повторного запроса
    /// разрешений. Зовётся при возврате на таб Галереи — если пока приложение
    /// было на другой вкладке появились новые фото, PHPhotoLibraryChangeObserver
    /// мог не дойти (suspended-процесс), и мы вытаскиваем актуальный набор сами.
    func reload() {
        guard access == .authorized || access == .limited else { return }
        loadAssets()
    }

    private func apply(_ status: PHAuthorizationStatus) {
        switch status {
        case .authorized: access = .authorized
        case .limited: access = .limited
        case .denied, .restricted: access = .denied
        case .notDetermined: access = .undetermined
        @unknown default: access = .denied
        }
    }

    private func loadAssets() {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = NSPredicate(
            format: "mediaType == %d OR mediaType == %d",
            PHAssetMediaType.image.rawValue, PHAssetMediaType.video.rawValue
        )
        let result = PHAsset.fetchAssets(with: options)
        fetchResult = result
        var list: [PHAsset] = []
        list.reserveCapacity(result.count)
        result.enumerateObjects { asset, _, _ in list.append(asset) }
        assets = list
        registerLibraryObserverIfNeeded()
    }

    // MARK: - Наблюдение за медиатекой

    /// Регистрируем наблюдателя один раз. При любом изменении медиатеки (например,
    /// после удаления фото/видео в «Освободить место») PhotoKit пришлёт уведомление,
    /// и мы пересоберём список — иначе в сетке остаются мёртвые превью, открытие
    /// которых падает с ошибкой.
    private func registerLibraryObserverIfNeeded() {
        guard libraryObserver == nil else { return }
        libraryObserver = PhotoLibraryObserver { [weak self] change in
            Task { @MainActor in self?.handleLibraryChange(change) }
        }
    }

    private func handleLibraryChange(_ change: PHChange) {
        guard let fetchResult,
              let details = change.changeDetails(for: fetchResult) else { return }
        let after = details.fetchResultAfterChanges
        self.fetchResult = after
        var list: [PHAsset] = []
        list.reserveCapacity(after.count)
        after.enumerateObjects { asset, _, _ in list.append(asset) }
        assets = list
        selection = selection.filter { id in list.contains { $0.localIdentifier == id } }
    }

    // MARK: - Режим выбора

    func toggleSelecting() {
        isSelecting.toggle()
        if !isSelecting { selection.removeAll() }
    }

    func toggle(_ asset: PHAsset) {
        let id = asset.localIdentifier
        if selection.contains(id) { selection.remove(id) } else { selection.insert(id) }
    }

    func isSelected(_ asset: PHAsset) -> Bool { selection.contains(asset.localIdentifier) }

    // MARK: - Загрузка в облако

    func uploadSelected() async {
        let ids = Array(selection)
        guard !ids.isEmpty, !isUploading else { return }

        let fetched = PHAsset.fetchAssets(withLocalIdentifiers: ids, options: nil)
        var targets: [PHAsset] = []
        fetched.enumerateObjects { asset, _, _ in targets.append(asset) }

        isUploading = true
        uploadDone = 0
        uploadTotal = targets.count
        var anyFailed = false
        for asset in targets {
            do {
                let (data, name) = try await DeviceAssetResource.originalData(for: asset)
                // Без явной папки: сервер раскладывает по системным «Фото»/«Видео»/
                // «Другие документы» по типу медиа (route_by_media_kind).
                _ = try await cloud.uploadFile(data: data, fileName: name, routeByMediaKind: true)
                // Файл теперь в облаке — сразу показываем иконку.
                presence.markPresent(asset.localIdentifier)
            } catch {
                anyFailed = true
            }
            uploadDone += 1
        }
        isUploading = false
        selection.removeAll()
        isSelecting = false
        snackbar = anyFailed
            ? String(localized: "gallery_upload_failed")
            : String(localized: "gallery_upload_done")
    }

    // MARK: - Одиночные действия (контекстное меню по удержанию)

    /// `file_id` ассета устройства в облаке. Сначала резолвим по SHA256-хешу
    /// (`CheckFileHash`); если файла нет — заливаем оригинал и привязываем по типу
    /// медиа в системную папку (route_by_media_kind). Помечаем как в облаке.
    func ensureCloudFileID(for asset: PHAsset) async throws -> String {
        if let hash = await DeviceAssetResource.cachedSHA256(for: asset),
           let existing = try await cloud.checkFileHash(hash) {
            presence.markPresent(asset.localIdentifier)
            return existing
        }
        let (data, name) = try await DeviceAssetResource.originalData(for: asset)
        let id = try await cloud.uploadFile(data: data, fileName: name, routeByMediaKind: true)
        presence.markPresent(asset.localIdentifier)
        return id
    }

    /// Обёртка: резолвим `file_id` (с индикатором), затем выполняем действие.
    private func resolveAndRun(_ asset: PHAsset, _ action: (String) async throws -> Void) async {
        guard !isUploading else { return }
        isUploading = true
        uploadTotal = 1
        uploadDone = 0
        do {
            let fileID = try await ensureCloudFileID(for: asset)
            try await action(fileID)
        } catch {
            snackbar = domainErrorMessage(error)
        }
        isUploading = false
        uploadTotal = 0
    }

    func copyLink(asset: PHAsset) async {
        await resolveAndRun(asset) { fileID in
            let urls = try await cloud.transfer.tempDownloadURLs(fileIDs: [fileID])
            guard let url = urls[fileID] else { throw CloudActionError.noLink }
            UIPasteboard.general.url = url
            snackbar = String(localized: "snack_link_copied")
        }
    }

    func makePublic(asset: PHAsset) async {
        await resolveAndRun(asset) { fileID in
            let name = PHAssetResource.assetResources(for: asset).first?.originalFilename ?? "file"
            let link = try await cloud.createShare(fileID: fileID, name: name)
            guard let url = link.url else { throw CloudActionError.noLink }
            pendingShareURL = ShareableURL(url: url)
        }
    }

    /// Подготовить контекст для sheet «Поделиться с пользователем». Если ассет
    /// ещё не в облаке — `resolveAndRun` загрузит его (как `makePublic`),
    /// после чего выставит `pendingShareWithUser` → screen откроет sheet через
    /// `.sheet(item:)`.
    func prepareShareWithUser(asset: PHAsset) async {
        await resolveAndRun(asset) { fileID in
            let name = PHAssetResource.assetResources(for: asset).first?.originalFilename ?? "file"
            pendingShareWithUser = ShareWithUserContext(fileID: fileID, fileName: name)
        }
    }

    func addToAlbum(asset: PHAsset, albumID: String) async {
        await resolveAndRun(asset) { fileID in
            try await albums.addItems(albumID: albumID, fileIDs: [fileID])
            snackbar = String(localized: "media_added_to_album")
        }
    }

    func createAlbumAndAdd(asset: PHAsset) async {
        await resolveAndRun(asset) { fileID in
            let name = "\(String(localized: "albums_create_title")) \(Self.randomSuffix())"
            let album = try await albums.createAlbum(name: name)
            try await albums.addItems(albumID: album.id, fileIDs: [fileID])
            snackbar = String(localized: "media_added_to_album")
        }
    }

    /// Удалить ассет с устройства. iOS сам показывает системное подтверждение;
    /// отмена → throw, тогда оставляем как есть.
    func deleteFromDevice(asset: PHAsset) async {
        do {
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest.deleteAssets([asset] as NSArray)
            }
            assets.removeAll { $0.localIdentifier == asset.localIdentifier }
        } catch {
            // Пользователь отменил удаление — не ошибка.
        }
    }

    private static func randomSuffix(_ length: Int = 5) -> String {
        let alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
        return String((0..<length).compactMap { _ in alphabet.randomElement() })
    }

    func snackbarShown() { snackbar = nil }
}

/// Мост к PhotoKit: `PHPhotoLibraryChangeObserver` требует `NSObject`, поэтому держим
/// его отдельным классом. Регистрируется при создании и снимается в `deinit` (когда
/// `GalleryViewModel` освобождает ссылку). Колбэк прилетает с фонового потока — вызов
/// на MainActor обеспечивает уже сам потребитель.
private final class PhotoLibraryObserver: NSObject, PHPhotoLibraryChangeObserver {
    private let onChange: (PHChange) -> Void

    init(onChange: @escaping (PHChange) -> Void) {
        self.onChange = onChange
        super.init()
        PHPhotoLibrary.shared().register(self)
    }

    deinit {
        PHPhotoLibrary.shared().unregisterChangeObserver(self)
    }

    func photoLibraryDidChange(_ changeInstance: PHChange) {
        onChange(changeInstance)
    }
}
