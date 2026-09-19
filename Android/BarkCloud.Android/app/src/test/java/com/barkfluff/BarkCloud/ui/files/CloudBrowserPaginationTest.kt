package com.barkfluff.BarkCloud.ui.files

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CloudBrowserPaginationTest {

    @Test
    fun `folder with a cursor is not considered empty while more files are available`() {
        val state = CloudBrowserUiState(canLoadMore = true)

        assertFalse(state.isEmpty)
    }

    @Test
    fun `loading more keeps a folder out of the empty state`() {
        val state = CloudBrowserUiState(isLoadingMore = true)

        assertFalse(state.isEmpty)
    }

    @Test
    fun `folder is empty only after loading completes and no cursor remains`() {
        val state = CloudBrowserUiState(isLoading = false)

        assertTrue(state.isEmpty)
    }
}
