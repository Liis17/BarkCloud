package com.barkfluff.BarkCloud.data

import com.barkfluff.BarkCloud.grpc.errorCode
import io.grpc.Status
import io.grpc.StatusRuntimeException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class TokenRefresher(
    private val session: GlobalParam,
    private val refreshCall: suspend (String) -> RefreshedAccessToken,
) {
    private val mutex = Mutex()
    @Volatile private var lastRefreshAtMillis = 0L

    /**
     * Сервер отверг [rejectedToken], хотя локально он ещё не истёк (часы устройства, отзыв, смена ключа).
     * Помечаем его истёкшим, чтобы следующий вызов выполнил refresh. Свежевыпущенный токен не сбрасываем
     * чаще [MIN_INVALIDATE_INTERVAL_MILLIS], иначе отвергнутый сервером новый токен вызвал бы шторм refresh.
     */
    fun invalidate(rejectedToken: String, nowMillis: Long = System.currentTimeMillis()) {
        if (nowMillis - lastRefreshAtMillis < MIN_INVALIDATE_INTERVAL_MILLIS) return
        session.expireAccessToken(rejectedToken)
    }

    suspend fun validAccessToken(): String? {
        validStoredAccessToken()?.let { return it }
        return mutex.withLock {
            validStoredAccessToken()?.let { return@withLock it }
            val refresh = session.refreshToken?.takeIf { it.isNotBlank() } ?: return@withLock null
            try {
                val refreshed = refreshCall(refresh)
                session.saveRefreshedAccessToken(refreshed.value, refreshed.expiresAtMillis)
                lastRefreshAtMillis = System.currentTimeMillis()
                refreshed.value
            } catch (error: StatusRuntimeException) {
                if (error.status.code == Status.Code.UNAUTHENTICATED ||
                    error.status.code == Status.Code.PERMISSION_DENIED ||
                    error.errorCode() == INVALID_REFRESH_TOKEN
                ) {
                    session.clearSession()
                }
                throw error
            }
        }
    }

    private fun validStoredAccessToken(): String? {
        val token = session.accessToken?.takeIf { it.isNotBlank() } ?: return null
        return token.takeIf { session.accessTokenExpiresAt > System.currentTimeMillis() + REFRESH_THRESHOLD_MILLIS }
    }

    data class RefreshedAccessToken(val value: String, val expiresAtMillis: Long)

    private companion object {
        const val REFRESH_THRESHOLD_MILLIS = 60_000L
        const val MIN_INVALIDATE_INTERVAL_MILLIS = 30_000L
        const val INVALID_REFRESH_TOKEN = "7E6A31C5-3C4D-412E-87BC-0A387617A5D3"
    }
}
