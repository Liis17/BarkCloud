import XCTest
@testable import BarkCloud

final class CloudBrowserPaginationTests: XCTestCase {
    private func makeState() -> CloudBrowserUiState {
        CloudBrowserUiState(directoryID: "", title: "Test")
    }

    func testFolderWithMorePagesIsNotEmpty() {
        var state = makeState()
        state.canLoadMore = true

        XCTAssertFalse(state.isEmpty)
    }

    func testLoadingMoreIsNotEmpty() {
        var state = makeState()
        state.isLoadingMore = true

        XCTAssertFalse(state.isEmpty)
    }

    func testEmptyStateRequiresNoMorePages() {
        var state = makeState()
        state.isLoading = false

        XCTAssertTrue(state.isEmpty)
    }
}
