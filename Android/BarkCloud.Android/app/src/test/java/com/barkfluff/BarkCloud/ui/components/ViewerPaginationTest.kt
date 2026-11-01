package com.barkfluff.BarkCloud.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewerPaginationTest {
    @Test
    fun documentsOnlyPageKeepsTheSameSizeButStillContinuesLoading() {
        var pagination = ViewerPagination(hasMore = true, isLoading = false)
        assertTrue(shouldRequestNextPage(settledIndex = 97, itemCount = 100, pagination = pagination))

        pagination = pagination.copy(isLoading = true)
        assertFalse(shouldRequestNextPage(97, 100, pagination))

        // Загруженная страница из документов не меняет число медиа (100), но запрос должен повториться.
        pagination = pagination.copy(isLoading = false)
        assertTrue(shouldRequestNextPage(97, 100, pagination))
    }

    @Test
    fun duplicateOnlyPageIsReevaluatedAfterCompletion() {
        val before = listOf("a", "b", "c")
        assertEquals(3, before.distinct().size)
        var pagination = ViewerPagination(hasMore = true, isLoading = true)
        assertFalse(shouldRequestNextPage(settledIndex = 1, itemCount = 3, pagination = pagination))

        // Дубликаты не добавляют элементов; после окончания загрузки запрос снова допустим.
        pagination = pagination.copy(isLoading = false)
        assertTrue(shouldRequestNextPage(settledIndex = 1, itemCount = 3, pagination = pagination))
    }

    @Test
    fun requestBlockedWhileInitialLoadIsBusyIsSentOnceItCompletes() {
        assertFalse(shouldRequestNextPage(settledIndex = 98, itemCount = 100, pagination = ViewerPagination(true, true)))
        assertTrue(shouldRequestNextPage(settledIndex = 98, itemCount = 100, pagination = ViewerPagination(true, false)))
    }

    @Test
    fun exhaustedSourceNeverRequestsAndEmptyListNeverRequests() {
        assertFalse(shouldRequestNextPage(settledIndex = 99, itemCount = 100, pagination = ViewerPagination(false, false)))
        assertFalse(shouldRequestNextPage(settledIndex = 0, itemCount = 0, pagination = ViewerPagination(true, false)))
    }
}
