package com.barkfluff.BarkCloud.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PhotoRecoveryTest {
    private val jpeg = ViewerRepresentation.JPEG_VIEW
    private val original = ViewerRepresentation.ORIGINAL

    /** Эмуляция эффекта загрузки: каждый запуск берёт текущее представление, исход сбоя переводит автомат дальше. */
    /** Каждая попытка проваливается: генерация должна завершиться конечным числом запросов. */
    private fun runGeneration(recovery: PhotoRecovery): Pair<List<ViewerRepresentation>, PhotoRecovery> {
        var state = recovery
        val requests = mutableListOf<ViewerRepresentation>()
        while (true) {
            val attempt = state.current ?: break
            requests += attempt
            // Сбой любого вида (сеть или декодирование) одинаково проваливает представление.
            state = state.afterFailure(attempt)
        }
        return requests to state
    }

    @Test
    fun networkFailureOfExpiredJpegThenUnsupportedOriginalEndsInTerminalErrorWithFiniteRequests() {
        // HEIC с JPEG-видом: JPEG первым. Сеть падает (протухшая ссылка), затем оригинал не декодируется.
        var recovery = photoRecoveryFor(requiresJpegView = true, hasJpegView = true)
        val requests = mutableListOf<ViewerRepresentation>()

        requests += requireNotNull(recovery.current)
        assertEquals(jpeg, requests.last())
        recovery = recovery.afterFailure(jpeg)                 // IOException: протухшая JPEG-ссылка
        requests += requireNotNull(recovery.current)
        assertEquals(original, requests.last())
        recovery = recovery.afterFailure(original)             // ошибка декодирования оригинала

        assertNull(recovery.current)                           // терминальная ошибка, превью остаётся
        assertEquals(listOf(jpeg, original), requests)
    }

    @Test
    fun repeatedFailuresOfTheSameRepresentationNeverRevisitIt() {
        var recovery = photoRecoveryFor(requiresJpegView = false, hasJpegView = true)
        recovery = recovery.afterFailure(original).afterFailure(original).afterFailure(original)

        assertEquals(jpeg, recovery.current)                   // только JPEG, повторов оригинала нет
        recovery = recovery.afterFailure(jpeg)
        assertNull(recovery.current)
    }

    @Test
    fun wholeGenerationIsBoundedByTheNumberOfRepresentations() {
        val (requests, end) = runGeneration(photoRecoveryFor(requiresJpegView = false, hasJpegView = true))

        assertEquals(listOf(original, jpeg), requests)
        assertNull(end.current)
    }

    @Test
    fun explicitRetryStartsAFreshBoundedGeneration() {
        val exhausted = runGeneration(photoRecoveryFor(true, true)).second
        assertNull(exhausted.current)

        // Явный Retry в composable создаёт новое состояние восстановления: снова не более двух попыток.
        val fresh = photoRecoveryFor(requiresJpegView = true, hasJpegView = true)
        assertEquals(jpeg, fresh.current)
        val (requests, _) = runGeneration(fresh)
        assertEquals(listOf(jpeg, original), requests)
    }

    @Test
    fun withoutJpegViewOnlyTheOriginalIsEverTried() {
        var recovery = photoRecoveryFor(requiresJpegView = false, hasJpegView = false)
        assertEquals(original, recovery.current)
        recovery = recovery.afterFailure(original)

        assertNull(recovery.current)
    }
}
