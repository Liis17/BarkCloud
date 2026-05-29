import Foundation
import Observation
import Photos

struct MediaGridUiState {
    var items: [MediaItem] = []
    /// Пока `true` — сетка рисуется скелетонами (.redacted).
    var isPlaceholder: Bool = true
    var isLoadingMore: Bool = false
    var canLoadMore: Bool = false
    var isUploading: Bool = false
    var snackbar: String?

    /// Режим мультивыбора активен (кнопка «Выбрать»).
    var isSelecting: Bool = false
    /// file_id выбранных элементов.
    var selection: Set<String> = []
    /// Идёт блокирующая операция выбора (удаление / добавление в альбом).
    var isProcessing: Bool = false
    /// Прогресс последовательного удаления: сделано / всего (0/0 — не идёт).
    var deleteDone: Int = 0
    var deleteTotal: Int = 0

    fileprivate var cursorCreatedAt: Date?
    fileprivate var cursorFileID: String = ""
}

@MainActor
@Observable
final class MediaGridViewModel {
    var state: MediaGridUiState

    private let kind: MediaKind
    private let cloud: CloudRepository
    private let albums: AlbumRepository
    private let vault: VaultStore
    private var didLoad = false

    init(kind: MediaKind, cloud: CloudRepository, albums: AlbumRepository, vault: VaultStore) {
        self.kind = kind
        self.cloud = cloud
        self.albums = albums
        self.vault = vault
        self.state = MediaGridUiState(
            items: MediaItem.placeholders(count: 12, isVideo: kind.isVideo),
            isPlaceholder: true
        )
    }

    private var apiKind: CloudMediaKind { kind.isVideo ? .video : .photo }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        do {
            let page = try await cloud.listUserMedia(kind: apiKind, limit: 60)
            let hidden = vault.protectedIDs
            state.items = page.items.map(MediaItem.init(asset:)).filter { !hidden.contains($0.id) }
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorFileID = page.nextCursorFileID
            state.canLoadMore = page.hasMore
        } catch {
            state.items = []
            state.snackbar = domainErrorMessage(error)
        }
        state.isPlaceholder = false
        // Убрать из выбора исчезнувшие элементы.
        let present = Set(state.items.map(\.id))
        state.selection.formIntersection(present)
    }

    func loadMoreIfNeeded(current item: MediaItem) async {
        guard state.canLoadMore, !state.isLoadingMore, !state.isPlaceholder,
              item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listUserMedia(
                kind: apiKind, limit: 60,
                cursorCreatedAt: state.cursorCreatedAt, cursorFileID: state.cursorFileID
            )
            let hidden = vault.protectedIDs
            state.items.append(contentsOf: page.items.map(MediaItem.init(asset:)).filter { !hidden.contains($0.id) })
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorFileID = page.nextCursorFileID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoadingMore = false
    }

    /// Загрузить выбранные в кастомном пикере ассеты устройства: читаем оригинал
    /// (`DeviceAssetResource`) и грузим в облако. Дубликаты бэкенд отсекает по хешу,
    /// а пикер уже не даёт выбрать то, что заведомо загружено.
    func uploadAssets(_ assets: [PHAsset]) async {
        guard !assets.isEmpty else { return }
        state.isUploading = true
        // Привязываем медиа к авто-папке «Недавно загруженные» (как веб-клиент),
        // чтобы у него была запись каталога. Best-effort: если папку не получить —
        // файл всё равно попадёт в галерею (ListUserMedia по uploader'у).
        let folderID = try? await cloud.ensureRecentUploadsFolder()
        var anyFailed = false
        for asset in assets {
            do {
                let (data, name) = try await DeviceAssetResource.originalData(for: asset)
                _ = try await cloud.uploadFile(data: data, fileName: name, toDirectory: folderID)
            } catch {
                anyFailed = true
            }
        }
        state.isUploading = false
        if anyFailed { state.snackbar = String(localized: "upload_failed") }
        await reload()
    }

    // MARK: - Мультивыбор

    func enterSelection() {
        guard !state.isPlaceholder else { return }
        state.isSelecting = true
    }

    func exitSelection() {
        state.isSelecting = false
        state.selection = []
    }

    func toggleSelection(_ item: MediaItem) {
        if state.selection.contains(item.id) {
            state.selection.remove(item.id)
        } else {
            state.selection.insert(item.id)
        }
    }

    var hasSelection: Bool { !state.selection.isEmpty }

    /// Выбранные элементы в порядке сетки.
    private func selectedItems() -> [MediaItem] {
        state.items.filter { state.selection.contains($0.id) }
    }

    /// Удалить выбранные медиа из галереи через `DeleteUserMedia(file_id)` —
    /// последовательно, с прогрессом (живые записи каталога → в корзину; если
    /// записей нет — сервер снимает владельца).
    func deleteSelected() async {
        let items = selectedItems()
        guard !items.isEmpty else { return }
        state.isProcessing = true
        state.deleteTotal = items.count
        state.deleteDone = 0
        var anyFailed = false
        for item in items {
            do { try await cloud.deleteUserMedia(fileID: item.id) }
            catch { anyFailed = true }
            state.deleteDone += 1
        }
        state.isProcessing = false
        state.deleteTotal = 0
        state.deleteDone = 0
        exitSelection()
        if anyFailed { state.snackbar = String(localized: "media_delete_failed") }
        await reload()
    }

    /// Переместить выбранные медиа в локальный сейф: сохраняем снимок (с превью)
    /// и сразу убираем из сетки. Сервер о защите не знает.
    func moveSelectedToVault() {
        let chosen = selectedItems()
        guard !chosen.isEmpty else { return }
        vault.add(chosen.map {
            VaultItem(id: $0.id, thumbnailURL: $0.thumbnailURL, previewWidth: $0.previewWidth, isVideo: $0.isVideo, fileName: $0.fileName)
        })
        let ids = Set(chosen.map(\.id))
        state.items.removeAll { ids.contains($0.id) }
        exitSelection()
        state.snackbar = String(localized: "vault_added")
    }

    /// Добавить выбранные медиа в существующий альбом.
    func addSelectedToAlbum(albumID: String) async {
        let fileIDs = selectedItems().map(\.id)
        guard !fileIDs.isEmpty else { return }
        state.isProcessing = true
        do {
            try await albums.addItems(albumID: albumID, fileIDs: fileIDs)
            state.snackbar = String(localized: "media_added_to_album")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isProcessing = false
        exitSelection()
    }

    /// Создать новый альбом «Новый альбом XXXXX» и добавить в него выбранные медиа.
    func createAlbumAndAddSelected() async {
        let fileIDs = selectedItems().map(\.id)
        guard !fileIDs.isEmpty else { return }
        state.isProcessing = true
        do {
            let name = "\(String(localized: "albums_create_title")) \(Self.randomSuffix())"
            let album = try await albums.createAlbum(name: name)
            try await albums.addItems(albumID: album.id, fileIDs: fileIDs)
            state.snackbar = String(localized: "media_added_to_album")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isProcessing = false
        exitSelection()
    }

    /// 5 случайных символов (буквы + цифры) для имени нового альбома.
    private static func randomSuffix(_ length: Int = 5) -> String {
        let alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
        return String((0..<length).compactMap { _ in alphabet.randomElement() })
    }

    func snackbarShown() { state.snackbar = nil }
}
