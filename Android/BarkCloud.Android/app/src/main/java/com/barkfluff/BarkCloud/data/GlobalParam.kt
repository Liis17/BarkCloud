package com.barkfluff.BarkCloud.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class GlobalParam(context: Context) {
    private val store = TokenStore(context)
    private val identityLock = Any()
    @Volatile private var cachedUserId = store.read()?.userId?.takeIf { it.isNotBlank() }
    private val _sessionActive = MutableStateFlow(hasValidRefreshToken())
    val sessionActive: StateFlow<Boolean> = _sessionActive.asStateFlow()
    var onCacheIdentityChanging: ((previousAccountId: String?, nextAccountId: String?) -> Unit)? = null
    var onCacheIdentityChanged: ((previousAccountId: String?, nextAccountId: String?) -> Unit)? = null

    var accessToken: String?
        get() = store.read()?.accessToken?.takeIf { it.isNotBlank() }
        set(value) = update { it.copy(accessToken = value.orEmpty()) }

    var accessTokenExpiresAt: Long
        get() = store.read()?.accessTokenExpiresAt ?: 0L
        set(value) = update { it.copy(accessTokenExpiresAt = value) }

    var refreshToken: String?
        get() = store.read()?.refreshToken?.takeIf { it.isNotBlank() }
        set(value) = update { it.copy(refreshToken = value.orEmpty()) }

    var refreshTokenExpiresAt: Long
        get() = store.read()?.refreshTokenExpiresAt ?: 0L
        set(value) = update { it.copy(refreshTokenExpiresAt = value) }

    var userId: String?
        get() = cachedUserId
        set(value) {
            val next = value.orEmpty()
            val previous: String?
            val nextId = next.takeIf { it.isNotBlank() }
            synchronized(identityLock) {
                previous = cachedUserId
                if (previous.orEmpty() == next) return
                update { it.copy(userId = next) }
                onCacheIdentityChanging?.invoke(previous, nextId)
                cachedUserId = nextId
            }
            onCacheIdentityChanged?.invoke(previous, nextId)
        }

    fun saveTokens(
        accessToken: String,
        accessTokenExpiresAt: Long,
        refreshToken: String,
        refreshTokenExpiresAt: Long,
    ) {
        val previous: String?
        synchronized(identityLock) {
            previous = cachedUserId
            store.save(TokenBundle(accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt))
            if (previous != null) onCacheIdentityChanging?.invoke(previous, null)
            cachedUserId = null
        }
        if (previous != null) onCacheIdentityChanged?.invoke(previous, null)
        publishSessionState()
    }

    fun saveRefreshedAccessToken(accessToken: String, accessTokenExpiresAt: Long) {
        update { it.copy(accessToken = accessToken, accessTokenExpiresAt = accessTokenExpiresAt) }
    }

    fun hasValidRefreshToken(): Boolean {
        val token = refreshToken ?: return false
        if (token.isBlank()) return false
        val expiresAt = refreshTokenExpiresAt
        return expiresAt == 0L || expiresAt > System.currentTimeMillis()
    }

    fun clearSession() {
        val previous: String?
        synchronized(identityLock) {
            previous = cachedUserId
            store.clear()
            onCacheIdentityChanging?.invoke(previous, null)
            cachedUserId = null
        }
        onCacheIdentityChanged?.invoke(previous, null)
        publishSessionState()
    }

    private fun update(transform: (TokenBundle) -> TokenBundle) {
        synchronized(identityLock) {
            val current = store.read() ?: TokenBundle("", 0L, "", 0L)
            store.save(transform(current))
        }
        publishSessionState()
    }

    private fun publishSessionState() {
        _sessionActive.value = hasValidRefreshToken()
    }
}
