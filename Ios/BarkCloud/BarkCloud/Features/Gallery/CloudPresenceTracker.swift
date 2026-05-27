import Foundation
import Observation
import Photos

/// Отслеживает, есть ли ассеты устройства уже в облаке (по SHA256-хешу оригинала).
/// Хеши считаются лениво (по мере появления ячеек), результат кэшируется, а запросы
/// к серверу пакетируются с дебаунсом через `CloudRepository.checkFileHashes`.
///
/// Переиспользуется вкладкой «Галерея» и кастомным пикером загрузки, чтобы и там, и
/// там показывать одну и ту же индикацию «уже загружено» и не плодить дубликаты.
@MainActor
@Observable
final class CloudPresenceTracker {
    /// localIdentifier → есть ли это фото/видео уже в облаке (по SHA256-хешу).
    private(set) var presence: [String: Bool] = [:]

    private let cloud: CloudRepository

    private var hashByAsset: [String: String] = [:]     // localId → sha256
    private var existsByHash: [String: Bool] = [:]       // sha256 → есть в облаке
    private var hashingInFlight: Set<String> = []        // localId, для которых считается хеш
    private var pendingByHash: [String: [String]] = [:]  // sha256 → localId, ждущие запроса
    private var queryScheduled = false

    init(cloud: CloudRepository) { self.cloud = cloud }

    func isInCloud(_ id: String) -> Bool { presence[id] == true }

    /// Пометить ассет как точно загруженный — вызывать после успешной загрузки в
    /// облако, чтобы сразу показать иконку без повторного запроса.
    func markPresent(_ id: String) { presence[id] = true }

    /// Вызывать при появлении ячейки. Лениво считает SHA256 оригинала и пакетно
    /// спрашивает у сервера, есть ли файл с таким хешем в облаке.
    func observe(_ asset: PHAsset) {
        let id = asset.localIdentifier
        if presence[id] != nil { return }                   // уже знаем результат
        if let hash = hashByAsset[id] {                     // хеш посчитан
            if let exists = existsByHash[hash] {
                presence[id] = exists
            } else {
                enqueue(hash: hash, id: id)
            }
            return
        }
        guard !hashingInFlight.contains(id) else { return }
        hashingInFlight.insert(id)
        Task { [weak self] in
            let hash = await DeviceAssetResource.streamingSHA256(for: asset)
            guard let self else { return }
            self.hashingInFlight.remove(id)
            guard let hash else { return }
            self.hashByAsset[id] = hash
            if let exists = self.existsByHash[hash] {
                self.presence[id] = exists
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
                    for id in snapshot[hash] ?? [] { presence[id] = exists }
                }
            } catch {
                // Не удалось — вернём хеши в очередь для следующей попытки.
                for hash in chunk { pendingByHash[hash] = snapshot[hash] }
            }
        }
    }
}
