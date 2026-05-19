import Foundation

struct BrowserUiState {
    var currentPath: String
    var rootPath: String
    var title: String
    var entries: [FsEntry] = []
    var sort: FsSort = .nameAsc
    var showHidden: Bool = false
    var selection: Set<String> = []
    var isLoading: Bool = false
    var pendingOp: PendingOp? = nil

    var selectionActive: Bool { !selection.isEmpty }
    var canGoUp: Bool { currentPath != rootPath }
}

struct PendingOp: Equatable {
    enum Kind: Equatable { case copy, move, delete }
    var kind: Kind
    var progress: Double
}

enum BrowserEvent: Equatable {
    case toast(String)
    case openFile(URL)
    case shareFiles([URL])
}
