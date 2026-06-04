import Foundation
import Observation
import Photos
import BarkCloudKit

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
    /// Имя файла текущего загружаемого ассета — для баннера прогресса над TabBar
    /// ([[UploadProgressObserver]]), который зеркалит эти счётчики.
    var currentFileName = ""

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
    /// localIdentifier ассетов, которые уже прошли классификацию (либо в
    /// pendingUpload, либо в reclaimable). При повторном сканировании (когда
    /// пользователь возвращается на вкладку Галереи) пропускаем — это и SHA256
    /// не считаем второй раз, и в `pendingUpload` дубликаты не плодим.
    private var processedAssetIDs: Set<String> = []
    /// localIdentifier ассетов, ПОДТВЕРЖДЁННЫХ на сервере (classify увидел
    /// exists==true). При возврате на передний план только их безусловно пропускаем
    /// при пере-сверке — всё прочее сверяем с облаком заново, чтобы подобрать
    /// передачи, прервавшиеся в фоне.
    private var confirmedInCloudIDs: Set<String> = []
    /// Наблюдатель медиатеки: новое фото/видео сразу запускает повторный скан и
    /// автозагрузку — без перезапуска приложения или смены вкладки.
    private var libraryObserver: BackupPhotoLibraryObserver?

    init(cloud: CloudRepository, settings: AutoUploadSettings) {
        self.cloud = cloud
        self.settings = settings
        self.autoUploadEnabled = settings.autoUploadEnabled
        self.libraryObserver = BackupPhotoLibraryObserver { [weak self] in
            Task { @MainActor in await self?.refreshScanForNewAssets() }
        }
    }

    /// Текущий загружаемый + следующие 3 в очереди (как в Google Photos).
    var queuePreview: [PHAsset] {
        var result: [PHAsset] = []
        if let currentAsset { result.append(currentAsset) }
        result.append(contentsOf: pendingUpload.prefix(3))
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

    /// Вызывать при возврате приложения на передний план. Чинит «зависшую»
    /// автозагрузку после сворачивания: цикл подачи мог завершиться (вся очередь
    /// уже подана в URLSession), а часть фоновых передач — прерваться. Сначала
    /// оживляем цикл (если он умер, а в памяти ещё остались ассеты). Затем, если
    /// фоновых backup-передач сейчас нет (значит загрузка действительно встала),
    /// заново сверяем медиатеку с облаком: из «обработанных» выкидываем всё, что
    /// ещё НЕ подтверждено на сервере (кроме того, что прямо сейчас в очереди),
    /// и пере-сканируем — недостающее уйдёт в загрузку снова (бэкенд дедуплицирует
    /// по SHA256). Если передачи идут — не пере-сверяем, чтобы не задублировать
    /// ещё не дозагруженные ассеты.
    func resumeOnForeground() async {
        guard autoUploadEnabled else { return }
        startUploadLoop()
        let hasActiveBackup = await UploadQueueStore.shared.activeJobs().contains { $0.source == .backup }
        if !hasActiveBackup {
            var keep = confirmedInCloudIDs
            keep.formUnion(pendingUpload.map(\.localIdentifier))
            if let currentAsset { keep.insert(currentAsset.localIdentifier) }
            processedAssetIDs = keep
        }
        await refreshScanForNewAssets()
    }

    /// Вызывать при возврате на таб «Галерея»: пересканировать медиатеку на
    /// предмет новых ассетов (которые могли появиться, пока приложение было
    /// открыто на другой вкладке). Если предыдущий скан ещё не закончился —
    /// ничего не делаем (он сам подхватит новые в текущем проходе). Если
    /// закончился — запускаем повторный, который пропустит уже виденные ассеты
    /// (`processedAssetIDs`). Автозагрузка стартует автоматически в `classify`.
    func refreshScanForNewAssets() async {
        guard scanTask == nil else { return }
        isScanning = true
        scanTask = Task(priority: .utility) { [weak self] in
            await self?.scan()
            self?.isScanning = false
            self?.scanTask = nil
        }
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
            self?.scanTask = nil
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
        // Фильтруем уже виденные ассеты — на повторном сканировании (при возврате
        // на таб Галереи) это избавит от пересчёта SHA256 у тысяч уже известных.
        let fresh = all.filter { !processedAssetIDs.contains($0.localIdentifier) }
        totalAssets = all.count
        scannedCount = all.count - fresh.count

        // Последовательно (конкурентность 1): хеш видео читает каждый байт — не
        // плодим параллельные чтения, чтобы не раздувать память.
        var batch: [(asset: PHAsset, hash: String)] = []
        for asset in fresh {
            if Task.isCancelled { return }
            let hash = await DeviceAssetResource.cachedSHA256(for: asset)
            scannedCount += 1
            guard let hash else {
                processedAssetIDs.insert(asset.localIdentifier)
                continue
            }
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
            processedAssetIDs.insert(item.asset.localIdentifier)
            if results[item.hash] == true {
                // insert(...).inserted защищает reclaimable от дублей при пере-сверке.
                if confirmedInCloudIDs.insert(item.asset.localIdentifier).inserted {
                    reclaimable.append(item.asset)
                    reclaimableBytes += DeviceAssetResource.originalByteSize(for: item.asset)
                }
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
            // Сбрасываем кеш processedAssetIDs — при предыдущем выключении мы
            // могли потерять pendingUpload (отмена in-flight jobs) и теперь
            // нужно дать scan'у снова разложить ассеты по бакетам. Иначе
            // pendingUpload останется пуст и uploadLoop сразу же выйдет.
            processedAssetIDs.removeAll()
            didStartScan = false
            startScanIfNeeded()
            startUploadLoop()
        } else {
            // Останавливаем продюсера и отменяем уже поданные в URLSession
            // backup-jobs (manual/share не трогаем). pendingUpload оставляем —
            // он переживёт re-toggle, чтобы при повторном включении не ждать
            // полного re-scan'a.
            uploadTask?.cancel()
            uploadTask = nil
            currentAsset = nil
            currentFileName = ""
            Task { await BackgroundUploadCoordinator.shared.cancelActiveJobs(source: .backup) }
        }
    }

    private func startUploadLoop() {
        guard autoUploadEnabled, uploadTask == nil else { return }
        uploadTask = Task(priority: .utility) { [weak self] in
            await self?.uploadLoop()
            self?.uploadTask = nil
        }
    }

    /// Максимум одновременных background-задач, которые мы держим в очереди — чтобы
    /// диск не забивался multipart-копиями. Демон iOS сам решает, сколько грузить
    /// параллельно (обычно 2–4); мы лишь регулируем поток постановки.
    private let inFlightLimit = 5

    private func uploadLoop() async {
        while autoUploadEnabled, !Task.isCancelled {
            guard !pendingUpload.isEmpty else {
                if isScanning {
                    try? await Task.sleep(nanoseconds: 500_000_000)
                    continue
                }
                break
            }
            // Не подавать новые job'ы, пока их слишком много в run-state.
            let active = await UploadQueueStore.shared.activeJobs().filter { $0.source == .backup }.count
            if active >= inFlightLimit {
                try? await Task.sleep(nanoseconds: 500_000_000)
                continue
            }
            let asset = pendingUpload.removeFirst()
            currentAsset = asset
            currentFileName = PHAssetResource.assetResources(for: asset).first?.originalFilename ?? ""
            do {
                try await enqueueAssetForBackup(asset)
                uploadDone += 1
                // В reclaimable ассет попадёт, только когда следующий скан
                // подтвердит его на сервере (classify) — до подтверждения
                // предлагать удалить оригинал нельзя.
                await loadStorageInfo()
            } catch {
                uploadFailed += 1
            }
        }
        currentAsset = nil
        currentFileName = ""
        await loadStorageInfo()
    }

    /// Подготовить файл оригинала ассета в App Group container стримом (без RAM),
    /// получить uploadURL и поставить UploadJob в координатор. Фактическая
    /// передача идёт в фоне — переживает сворачивание и kill main app.
    private func enqueueAssetForBackup(_ asset: PHAsset) async throws {
        guard let stagingDir = UploadConstants.stagingDirectory else {
            throw DeviceAssetError.noResource
        }
        let originalPath = stagingDir.appendingPathComponent("\(UUID().uuidString)")
        let fileName = try await DeviceAssetResource.writeOriginal(asset: asset, to: originalPath)
        let renamed = originalPath.deletingLastPathComponent().appendingPathComponent("\(UUID().uuidString)-\(fileName)")
        try? FileManager.default.moveItem(at: originalPath, to: renamed)
        let sourcePath = FileManager.default.fileExists(atPath: renamed.path) ? renamed : originalPath
        // Без явной папки: сервер разложит по системным «Фото»/«Видео»/«Другие
        // документы» по типу медиа (route_by_media_kind) при attach в main app.
        _ = try await cloud.enqueueBackgroundUpload(
            sourceFile: sourcePath,
            fileName: fileName,
            mimeType: nil,
            toDirectory: nil,
            source: .backup
        )
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
            // Ассеты удалены с устройства — выкидываем их из кеша хешей, чтобы он
            // не держал мёртвые localIdentifier. Список в галерее обновится сам через
            // PHPhotoLibraryChangeObserver (GalleryViewModel).
            await AssetHashStore.shared.remove(localIds: assets.map(\.localIdentifier))
            await loadStorageInfo()
        } catch {
            // Пользователь отменил или удаление не удалось — оставляем как есть.
        }
        isFreeing = false
    }

    func dismissCelebration() { lastFreedBytes = nil }
}

/// Мост к PhotoKit для автозагрузки: `PHPhotoLibraryChangeObserver` требует
/// `NSObject`. При изменении медиатеки дёргает повторный скан, чтобы новые
/// фото/видео уходили в облако без перезапуска и смены вкладки. Колбэк прилетает
/// с фонового потока — потребитель сам уходит на MainActor.
private final class BackupPhotoLibraryObserver: NSObject, PHPhotoLibraryChangeObserver {
    private let onChange: @Sendable () -> Void

    init(onChange: @escaping @Sendable () -> Void) {
        self.onChange = onChange
        super.init()
        PHPhotoLibrary.shared().register(self)
    }

    deinit {
        PHPhotoLibrary.shared().unregisterChangeObserver(self)
    }

    func photoLibraryDidChange(_ changeInstance: PHChange) {
        onChange()
    }
}
