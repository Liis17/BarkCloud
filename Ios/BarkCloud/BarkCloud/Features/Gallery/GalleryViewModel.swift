import Foundation
import Observation
import Photos
import CryptoKit

enum GalleryError: Error { case noResource }

/// Состояние вкладки «Галерея»: медиатека устройства (фото+видео через PhotoKit),
/// режим выбора и загрузка выбранных ассетов в облако.
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

    /// localIdentifier → есть ли это фото/видео уже в облаке (по SHA256-хешу).
    /// Заполняется лениво по мере появления ячеек на экране.
    var cloudPresence: [String: Bool] = [:]

    private let cloud: CloudRepository
    private var didLoad = false

    // Кэши и очереди для пассивной индикации «уже в облаке».
    private var hashByAsset: [String: String] = [:]     // localId → sha256
    private var existsByHash: [String: Bool] = [:]       // sha256 → есть в облаке
    private var hashingInFlight: Set<String> = []        // localId, для которых считается хеш
    private var pendingByHash: [String: [String]] = [:]  // sha256 → localId, ждущие запроса
    private var queryScheduled = false

    init(cloud: CloudRepository) { self.cloud = cloud }

    var hasSelection: Bool { !selection.isEmpty }

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
        var anyFailed = false
        for asset in targets {
            do {
                let (data, name) = try await Self.originalData(for: asset)
                _ = try await cloud.uploadFile(data: data, fileName: name)
                // Файл теперь в облаке — сразу показываем иконку.
                cloudPresence[asset.localIdentifier] = true
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

    // MARK: - Индикация «уже в облаке» (по SHA256)

    /// Вызывать при появлении ячейки. Лениво считает SHA256 оригинала и
    /// пакетно спрашивает у сервера, есть ли файл с таким хешем в облаке.
    func observeCloudPresence(for asset: PHAsset) {
        let id = asset.localIdentifier
        if cloudPresence[id] != nil { return }              // уже знаем результат
        if let hash = hashByAsset[id] {                     // хеш посчитан
            if let exists = existsByHash[hash] {
                cloudPresence[id] = exists
            } else {
                enqueue(hash: hash, id: id)
            }
            return
        }
        guard !hashingInFlight.contains(id) else { return }
        hashingInFlight.insert(id)
        Task { [weak self] in
            let hash = await Self.streamingSHA256(for: asset)
            guard let self else { return }
            self.hashingInFlight.remove(id)
            guard let hash else { return }
            self.hashByAsset[id] = hash
            if let exists = self.existsByHash[hash] {
                self.cloudPresence[id] = exists
            } else {
                self.enqueue(hash: hash, id: id)
            }
        }
    }

    private func enqueue(hash: String, id: String) {
        pendingByHash[hash, default: []].append(id)
        scheduleHashQuery()
    }

    /// Дебаунс: накапливаем хеши и отправляем одним пакетом.
    private func scheduleHashQuery() {
        guard !queryScheduled else { return }
        queryScheduled = true
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            self?.queryScheduled = false
            await self?.flushHashQuery()
        }
    }

    private func flushHashQuery() async {
        guard !pendingByHash.isEmpty else { return }
        let snapshot = pendingByHash
        pendingByHash.removeAll()

        // Бэкенд ограничивает пакет; режем на части по 500 хешей.
        let allHashes = Array(snapshot.keys)
        for chunk in stride(from: 0, to: allHashes.count, by: 500).map({ Array(allHashes[$0..<min($0 + 500, allHashes.count)]) }) {
            do {
                let results = try await cloud.checkFileHashes(chunk)
                for hash in chunk {
                    let exists = results[hash] ?? false
                    existsByHash[hash] = exists
                    for id in snapshot[hash] ?? [] { cloudPresence[id] = exists }
                }
            } catch {
                // Не удалось — вернём хеши в очередь для следующей попытки.
                for hash in chunk { pendingByHash[hash] = snapshot[hash] }
            }
        }
    }

    // MARK: - Чтение оригинала ассета

    /// Потокобезопасный аккумулятор чанков (PHAssetResourceManager отдаёт данные
    /// частями, возможно с фонового потока).
    private final class DataBuffer: @unchecked Sendable {
        private var data = Data()
        private let lock = NSLock()
        func append(_ chunk: Data) { lock.lock(); data.append(chunk); lock.unlock() }
        var value: Data { lock.lock(); defer { lock.unlock() }; return data }
    }

    /// Оригинальные байты ассета и его имя файла (для загрузки в облако).
    private static func originalData(for asset: PHAsset) async throws -> (Data, String) {
        let resources = PHAssetResource.assetResources(for: asset)
        let preferred: [PHAssetResourceType] = [.photo, .video, .fullSizePhoto, .fullSizeVideo]
        let resource = preferred.compactMap { type in resources.first { $0.type == type } }.first
            ?? resources.first
        guard let resource else { throw GalleryError.noResource }

        let fileName = resource.originalFilename
        let buffer = DataBuffer()
        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true

        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            PHAssetResourceManager.default().requestData(
                for: resource,
                options: options,
                dataReceivedHandler: { chunk in buffer.append(chunk) },
                completionHandler: { error in
                    if let error { cont.resume(throwing: error) } else { cont.resume() }
                }
            )
        }
        return (buffer.value, fileName)
    }

    /// Потокобезопасный SHA256-хешер (обновляется чанками с фонового потока).
    private final class HasherBox: @unchecked Sendable {
        private var hasher = SHA256()
        private let lock = NSLock()
        func update(_ chunk: Data) { lock.lock(); hasher.update(data: chunk); lock.unlock() }
        func hexDigest() -> String {
            lock.lock(); defer { lock.unlock() }
            return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        }
    }

    /// SHA256 оригинала ассета в hex (lowercase) — считается потоково, без
    /// удержания всего файла в памяти. Должен совпадать с хешем, который бэкенд
    /// вычисляет при загрузке (тот же приоритет ресурсов, что и в `originalData`).
    private static func streamingSHA256(for asset: PHAsset) async -> String? {
        let resources = PHAssetResource.assetResources(for: asset)
        let preferred: [PHAssetResourceType] = [.photo, .video, .fullSizePhoto, .fullSizeVideo]
        let resource = preferred.compactMap { type in resources.first { $0.type == type } }.first
            ?? resources.first
        guard let resource else { return nil }

        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true
        let hasher = HasherBox()
        do {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                PHAssetResourceManager.default().requestData(
                    for: resource,
                    options: options,
                    dataReceivedHandler: { chunk in hasher.update(chunk) },
                    completionHandler: { error in
                        if let error { cont.resume(throwing: error) } else { cont.resume() }
                    }
                )
            }
        } catch {
            return nil
        }
        return hasher.hexDigest()
    }
}
