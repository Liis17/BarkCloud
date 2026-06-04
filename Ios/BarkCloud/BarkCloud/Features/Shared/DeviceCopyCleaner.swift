import Foundation
import Photos

/// Удаление копий облачных файлов на устройстве: по `file_id` находим связанные
/// `localIdentifier` (через `CloudDeviceLinkStore`) и убираем соответствующие
/// ассеты из медиатеки. Зовётся при удалении файлов в облаке (Альбомы, облачный
/// браузер, корзина), чтобы копия на телефоне уходила вместе с облаком.
///
/// iOS показывает системное подтверждение удаления из медиатеки — «тихой» чистка
/// быть не может. Если связь неизвестна или ассета уже нет на устройстве — тихо
/// ничего не делаем (best-effort, см. ограничения локального индекса).
@MainActor
enum DeviceCopyCleaner {
    /// Удалить с устройства копии облачных файлов с переданными `file_id` —
    /// одним системным диалогом на всю пачку. Связь и кеш хешей по затронутым
    /// ассетам очищаются.
    static func deleteDeviceCopies(forCloudFileIDs fileIDs: [String]) async {
        guard !fileIDs.isEmpty else { return }

        var localIds: [String] = []
        for fileID in fileIDs {
            if let localId = await CloudDeviceLinkStore.shared.localIdentifier(forFileID: fileID) {
                localIds.append(localId)
            }
        }
        guard !localIds.isEmpty else { return }

        let fetch = PHAsset.fetchAssets(withLocalIdentifiers: localIds, options: nil)
        var assets: [PHAsset] = []
        fetch.enumerateObjects { asset, _, _ in assets.append(asset) }
        guard !assets.isEmpty else {
            // Ассетов уже нет на устройстве — просто чистим осиротевшие связи.
            await CloudDeviceLinkStore.shared.remove(fileIDs: fileIDs)
            return
        }

        do {
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest.deleteAssets(assets as NSArray)
            }
            let removedLocalIds = assets.map(\.localIdentifier)
            await CloudDeviceLinkStore.shared.remove(fileIDs: fileIDs)
            await CloudDeviceLinkStore.shared.remove(localIds: removedLocalIds)
            await AssetHashStore.shared.remove(localIds: removedLocalIds)
        } catch {
            // Пользователь отменил системное удаление — копия на устройстве остаётся.
        }
    }

    /// Удобная обёртка для одного файла.
    static func deleteDeviceCopy(forCloudFileID fileID: String) async {
        await deleteDeviceCopies(forCloudFileIDs: [fileID])
    }
}
