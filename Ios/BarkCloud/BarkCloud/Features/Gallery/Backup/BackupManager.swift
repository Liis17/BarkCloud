import Foundation
import Observation
import Photos

/// Управляет резервным копированием медиатеки устройства в облако: показывает квоту,
/// ведёт прогрессивный скан (что уже в облаке — по SHA256 оригиналов), автозагрузку
/// недостающего и освобождение места.
///
/// Живёт в `AppEnvironment` (а не во вью), поэтому переживает закрытие модалки и
/// ре-рендеры. Фоновые `Task`'и хранятся внутри менеджера — если вешать их на
/// `.task`/вью, ре-рендер их отменит (это и есть класс ошибки «the transport threw
/// an unexpected error», с которым уже сталкивались в pull-to-refresh).
@MainActor
@Observable
final class BackupManager {
    // Хранилище (байты).
    var usedStorage: Int64 = 0
    var storageLimit: Int64 = 0

    // Скан медиатеки.
    var isScanning = false
    var scannedCount = 0
    var totalAssets = 0

    // Очередь автозагрузки.
    private(set) var pendingUpload: [PHAsset] = []
    var uploadDone = 0
    var uploadFailed = 0
    var currentAsset: PHAsset?

    // Освобождение места.
    private(set) var reclaimable: [PHAsset] = []
    var reclaimableBytes: Int64 = 0
    var isFreeing = false
    /// Ненил → показать благодарственную анимацию с этим числом освобождённых байт.
    var lastFreedBytes: Int64?

    /// Зеркало флага из `settings`, но наблюдаемое — чтобы тогл перерисовывал UI.
    private(set) var autoUploadEnabled: Bool

    private let cloud: CloudRepository
    private let settings: AutoUploadSettings

    private var scanTask: Task<Void, Never>?
    private var uploadTask: Task<Void, Never>?
    private var didStartScan = false

    init(cloud: CloudRepository, settings: AutoUploadSettings) {
        self.cloud = cloud
        self.settings = settings
        self.autoUploadEnabled = settings.autoUploadEnabled
    }

    /// Текущий загружаемый + следующие 2 в очереди (как в Google Photos).
    var queuePreview: [PHAsset] {
        var result: [PHAsset] = []
        if let currentAsset { result.append(currentAsset) }
        result.append(contentsOf: pendingUpload.prefix(2))
        return result
    }

    /// Сколько ещё осталось загрузить (включая текущий).
    var remainingCount: Int { pendingUpload.count + (currentAsset != nil ? 1 : 0) }

    // MARK: - Открытие модалки / возобновление при старте

    /// Вызывать при открытии модалки: подтянуть квоту и запустить скан.
    func onOpen() async {
        await loadStorageInfo()
        startScanIfNeeded()
    }

    /// Вызывать при старте приложения: если автозагрузка включена — продолжить
    /// скан и докачку на переднем плане (низкий приоритет, чтобы не мешать старту).
    func resumeIfEnabled() {
        guard autoUploadEnabled else { return }
        startScanIfNeeded()
        startUploadLoop()
    }

    func loadStorageInfo() async {
        if let info = try? await cloud.transfer.storageInfo() {
            usedStorage = info.used
            storageLimit = info.limit
        }
    }

    // MARK: - Скан медиатеки (по SHA256)

    func startScanIfNeeded() {
        guard !didStartScan else { return }
        didStartScan = true
        isScanning = true
        scanTask = Task(priority: .utility) { [weak self] in
            await self?.scan()
            self?.isScanning = false
        }
    }

    private func scan() async {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = NSPredicate(
            format: "mediaType == %d OR mediaType == %d",
            PHAssetMediaType.image.rawValue, PHAssetMediaType.video.rawValue
        )
        let fetch = PHAsset.fetchAssets(with: options)
        var all: [PHAsset] = []
        all.reserveCapacity(fetch.count)
        fetch.enumerateObjects { asset, _, _ in all.append(asset) }
        totalAssets = all.count

        // Последовательно (конкурентность 1): хеш видео читает каждый байт — не
        // плодим параллельные чтения, чтобы не раздувать память.
        var batch: [(asset: PHAsset, hash: String)] = []
        for asset in all {
            if Task.isCancelled { return }
            let hash = await DeviceAssetResource.cachedSHA256(for: asset)
            scannedCount += 1
            guard let hash else { continue }
            batch.append((asset, hash))
            if batch.count >= 100 {
                await classify(batch)
                batch.removeAll(keepingCapacity: true)
            }
        }
        if !batch.isEmpty { await classify(batch) }
    }

    /// Спросить сервер про пачку хешей и разложить ассеты: уже в облаке → можно
    /// освободить место; иначе → в очередь автозагрузки.
    private func classify(_ batch: [(asset: PHAsset, hash: String)]) async {
        let results = (try? await cloud.checkFileHashes(batch.map { $0.hash })) ?? [:]
        for item in batch {
            if results[item.hash] == true {
                reclaimable.append(item.asset)
                reclaimableBytes += DeviceAssetResource.originalByteSize(for: item.asset)
            } else {
                pendingUpload.append(item.asset)
            }
        }
        if autoUploadEnabled { startUploadLoop() }
    }

    // MARK: - Автозагрузка

    func setAutoUpload(_ on: Bool) {
        autoUploadEnabled = on
        settings.autoUploadEnabled = on
        if on {
            startScanIfNeeded()
            startUploadLoop()
        } else {
            uploadTask?.cancel()
            uploadTask = nil
        }
    }

    private func startUploadLoop() {
        guard autoUploadEnabled, uploadTask == nil else { return }
        uploadTask = Task(priority: .utility) { [weak self] in
            await self?.uploadLoop()
            self?.uploadTask = nil
        }
    }

    private func uploadLoop() async {
        let folderID = try? await cloud.ensureRecentUploadsFolder()
        while autoUploadEnabled, !Task.isCancelled {
            guard !pendingUpload.isEmpty else {
                // Очередь пуста: если скан ещё идёт — подождём и проверим снова.
                if isScanning {
                    try? await Task.sleep(nanoseconds: 500_000_000)
                    continue
                }
                break
            }
            let asset = pendingUpload.removeFirst()
            currentAsset = asset
            do {
                let (data, name) = try await DeviceAssetResource.originalData(for: asset)
                _ = try await cloud.uploadFile(data: data, fileName: name, toDirectory: folderID)
                uploadDone += 1
                reclaimable.append(asset)
                reclaimableBytes += DeviceAssetResource.originalByteSize(for: asset)
            } catch {
                uploadFailed += 1
            }
        }
        currentAsset = nil
        await loadStorageInfo()
    }

    // MARK: - Освобождение места

    func freeSpace() async {
        guard !reclaimable.isEmpty, !isFreeing else { return }
        isFreeing = true
        let assets = reclaimable
        let bytes = reclaimableBytes
        do {
            // iOS сам показывает системное подтверждение удаления; отмена → throw.
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest.deleteAssets(assets as NSArray)
            }
            reclaimable.removeAll()
            reclaimableBytes = 0
            lastFreedBytes = bytes
            await loadStorageInfo()
        } catch {
            // Пользователь отменил или удаление не удалось — оставляем как есть.
        }
        isFreeing = false
    }

    func dismissCelebration() { lastFreedBytes = nil }
}
