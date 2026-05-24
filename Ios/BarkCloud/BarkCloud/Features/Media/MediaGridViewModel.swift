import Foundation
import Observation

struct MediaGridUiState {
    var items: [MediaItem] = []
    /// Пока `true` — сетка рисуется скелетонами (.redacted).
    var isPlaceholder: Bool = true
}

@MainActor
@Observable
final class MediaGridViewModel {
    var state: MediaGridUiState

    private let kind: MediaKind

    init(kind: MediaKind) {
        self.kind = kind
        self.state = MediaGridUiState(
            items: MediaItem.placeholders(count: 12, isVideo: kind.isVideo),
            isPlaceholder: true
        )
    }

    /// TODO: подключить `CloudApi.ListUserImages` (cursor-пагинация); для видео —
    /// фильтр по типу превью. После загрузки: state.items = ..., isPlaceholder = false.
    /// Пока получение с сервера не реализовано — остаёмся в скелетон-режиме.
    func load() async {
        // no-op (server retrieval not implemented yet)
    }
}
