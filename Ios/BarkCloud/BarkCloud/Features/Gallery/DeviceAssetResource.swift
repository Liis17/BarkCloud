import Foundation
import Photos
import CryptoKit

enum DeviceAssetError: Error { case noResource }

/// Общие утилиты чтения оригиналов ассетов устройства через PhotoKit.
/// Используются и вкладкой «Галерея», и кастомным пикером загрузки: оба должны
/// читать байты и считать SHA256 одинаково (тот же приоритет ресурсов), чтобы
/// хеш совпадал с тем, что бэкенд вычисляет при загрузке (дедупликация).
enum DeviceAssetResource {
    /// Приоритет ресурсов: сначала исходник, потом полноразмерные варианты.
    private static let preferredTypes: [PHAssetResourceType] = [.photo, .video, .fullSizePhoto, .fullSizeVideo]

    private static func bestResource(for asset: PHAsset) -> PHAssetResource? {
        let resources = PHAssetResource.assetResources(for: asset)
        return preferredTypes.compactMap { type in resources.first { $0.type == type } }.first
            ?? resources.first
    }

    /// Потокобезопасный аккумулятор чанков (PHAssetResourceManager отдаёт данные
    /// частями, возможно с фонового потока).
    private final class DataBuffer: @unchecked Sendable {
        private var data = Data()
        private let lock = NSLock()
        func append(_ chunk: Data) { lock.lock(); data.append(chunk); lock.unlock() }
        var value: Data { lock.lock(); defer { lock.unlock() }; return data }
    }

    /// Размер оригинала ассета в байтах (для оценки «сколько освободится»). Берётся
    /// из приватного `fileSize` того же ресурса, что идёт в загрузку — широко
    /// используемый KVC-приём PhotoKit. `0`, если размер недоступен.
    static func originalByteSize(for asset: PHAsset) -> Int64 {
        guard let resource = bestResource(for: asset) else { return 0 }
        return (resource.value(forKey: "fileSize") as? Int64) ?? 0
    }

    /// Оригинальные байты ассета и его имя файла (для загрузки в облако).
    static func originalData(for asset: PHAsset) async throws -> (Data, String) {
        guard let resource = bestResource(for: asset) else { throw DeviceAssetError.noResource }

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
    static func streamingSHA256(for asset: PHAsset) async -> String? {
        guard let resource = bestResource(for: asset) else { return nil }

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
