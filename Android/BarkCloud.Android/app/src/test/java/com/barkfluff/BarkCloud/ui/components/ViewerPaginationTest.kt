package com.barkfluff.BarkCloud.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Эффект подгрузки в composable перезапускается по ключам (позиция, число медиа, ViewerPagination) и
 * передаёт состояние трекеру. Тесты проводят последовательности таких наблюдений, а не отдельные предикаты.
 */
class ViewerPaginationTest {
    private val edge = 98          // у края: до конца 100 элементов меньше трёх страниц
    private val size = 100

    @Test
    fun fastEmptyDocumentPageContinuesPagingEvenWhenLoadingFlagIsConflated() {
        val tracker = ViewerPageRequestTracker()
        assertTrue(tracker.shouldRequest(edge, size, ViewerPagination(hasMore = true, isLoading = false, pagesCompleted = 0)))

        // Страница из одних документов завершилась очень быстро: наблюдатель видит только итоговое состояние.
        // Число медиа не менялось, но прогресс вырос, поэтому подгрузка продолжается.
        assertTrue(tracker.shouldRequest(edge, size, ViewerPagination(hasMore = true, isLoading = false, pagesCompleted = 1)))
    }

    @Test
    fun duplicateOnlyPageAlsoContinuesPaging() {
        val tracker = ViewerPageRequestTracker()
        tracker.shouldRequest(edge, size, ViewerPagination(true, false, 0))

        // Дубликаты не добавляют элементов; успешная страница всё равно двигает прогресс.
        assertTrue(tracker.shouldRequest(edge, size, ViewerPagination(true, false, 1)))
        assertFalse(tracker.shouldRequest(edge, size, ViewerPagination(true, false, 1)))
    }

    @Test
    fun repeatedFailuresDoNotRetryAutomatically() {
        val tracker = ViewerPageRequestTracker()
        var requests = 0
        if (tracker.shouldRequest(edge, size, ViewerPagination(true, false, 0))) requests++

        // Запрос провалился: загрузка сбросилась (isLoading=false), прогресс не изменился, курсор тот же.
        // Каждое наблюдение эффекта — без автоматического повтора.
        repeat(5) {
            assertFalse(tracker.shouldRequest(edge, size, ViewerPagination(true, true, 0)))
            if (tracker.shouldRequest(edge, size, ViewerPagination(true, false, 0))) requests++
        }

        assertEquals(1, requests)
    }

    @Test
    fun explicitUserNavigationAfterFailureRetriesOnce() {
        val tracker = ViewerPageRequestTracker()
        assertTrue(tracker.shouldRequest(edge, size, ViewerPagination(true, false, 0)))
        assertFalse(tracker.shouldRequest(edge, size, ViewerPagination(true, false, 0)))

        // Пользователь листает на соседнюю страницу у края: это явный повтор.
        assertTrue(tracker.shouldRequest(edge - 1, size, ViewerPagination(true, false, 0)))
        assertFalse(tracker.shouldRequest(edge - 1, size, ViewerPagination(true, false, 0)))
    }

    @Test
    fun initiallyBusyLoadIsRequestedOnceItCompletes() {
        val tracker = ViewerPageRequestTracker()
        assertFalse(tracker.shouldRequest(edge, size, ViewerPagination(true, isLoading = true, pagesCompleted = 0)))
        assertTrue(tracker.shouldRequest(edge, size, ViewerPagination(true, isLoading = false, pagesCompleted = 1)))
    }

    @Test
    fun paginationStopsWhenSourceIsExhaustedOrAwayFromTheEdge() {
        val tracker = ViewerPageRequestTracker()
        assertFalse(tracker.shouldRequest(edge, size, ViewerPagination(hasMore = false, isLoading = false, pagesCompleted = 3)))
        assertFalse(tracker.shouldRequest(0, size, ViewerPagination(hasMore = true, isLoading = false, pagesCompleted = 3)))
        assertFalse(tracker.shouldRequest(edge, 0, ViewerPagination(hasMore = true, isLoading = false, pagesCompleted = 3)))
    }

    @Test
    fun successfulPagesContinueUntilTheSourceIsExhausted() {
        val tracker = ViewerPageRequestTracker()
        var progress = 0
        var requests = 0
        // Каждая страница успешна и приносит новые элементы; на четвёртой источник исчерпан.
        for (page in 1..4) {
            val hasMore = page < 4
            if (tracker.shouldRequest(edge, size, ViewerPagination(hasMore, false, progress))) requests++
            progress++
        }
        assertEquals(3, requests)
    }
}
