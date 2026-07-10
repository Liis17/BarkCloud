package com.barkfluff.BarkCloud.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class GlobalParam(context: Context) {
    private val store = TokenStore(context)
    private val _sessionActive = MutableStateFlow(hasValidRefreshToken())
    val sessionActive: StateFlow<Boolean> = _sessionActive.asStateFlow()

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

    fun saveTokens(
        accessToken: String,
        accessTokenExpiresAt: Long,
        refreshToken: String,
        refreshTokenExpiresAt: Long,
    ) {
        store.save(TokenBundle(accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt))
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
        store.clear()
        publishSessionState()
    }

    private fun update(transform: (TokenBundle) -> TokenBundle) {
        val current = store.read() ?: TokenBundle("", 0L, "", 0L)
        store.save(transform(current))
        publishSessionState()
    }

    private fun publishSessionState() {
        _sessionActive.value = hasValidRefreshToken()
    }
}
