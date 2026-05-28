import Foundation
import Observation
import Photos

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

    /// Трекер наличия файлов в облаке (по SHA256-хешу) — общий с пикером загрузки.
    let presence: CloudPresenceTracker

    private let cloud: CloudRepository
    private var didLoad = false

    init(cloud: CloudRepository) {
        self.cloud = cloud
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
        var list: [PHAsset] = []
        list.reserveCapacity(result.count)
        result.enumerateObjects { asset, _, _ in list.append(asset) }
        assets = list
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
        // Привязываем медиа к авто-папке «Недавно загруженные» (как веб-клиент),
        // чтобы у него была запись каталога. Best-effort: без папки файл всё равно
        // попадёт в галерею.
        let folderID = try? await cloud.ensureRecentUploadsFolder()
        var anyFailed = false
        for asset in targets {
            do {
                let (data, name) = try await DeviceAssetResource.originalData(for: asset)
                _ = try await cloud.uploadFile(data: data, fileName: name, toDirectory: folderID)
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

    func snackbarShown() { snackbar = nil }
}
