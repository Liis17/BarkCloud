import Foundation
import Observation
import BarkCloudKit

@MainActor
@Observable
final class FilesRootViewModel {
    struct UiState {
        var isLoading = true
        var folderCount = 0
        var fileCount = 0
        var failed = false
    }

    var state = UiState()
    private var didLoad = false

    /// Краткая сводка по корню облака (для подписи карточки-входа).
    func loadSummary(cloud: CloudRepository) async {
        guard !didLoad else { return }
        didLoad = true
        do {
            let listing = try await cloud.listDirectory("")
            state.folderCount = listing.subdirs.count
            state.fileCount = listing.files.count
        } catch {
            state.failed = true
        }
        state.isLoading = false
    }
}
