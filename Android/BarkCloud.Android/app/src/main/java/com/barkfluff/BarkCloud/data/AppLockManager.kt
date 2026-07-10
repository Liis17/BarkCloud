package com.barkfluff.BarkCloud.data

import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Состояние блокировки приложения — grace-period 30 сек после ухода в фон
 * (зеркалит iOS `AppLockManager`/`scenePhase`). Регистрируется на
 * `ProcessLifecycleOwner` (app-wide foreground/background, не per-Activity).
 */
class AppLockManager(private val store: AppLockStore) : DefaultLifecycleObserver {

    private val _shouldShowLock = MutableStateFlow(store.isEnabled)
    val shouldShowLock: StateFlow<Boolean> = _shouldShowLock.asStateFlow()

    private var backgroundedAtMillis: Long? = null

    override fun onStop(owner: LifecycleOwner) {
        if (store.isEnabled) backgroundedAtMillis = System.currentTimeMillis()
    }

    override fun onStart(owner: LifecycleOwner) {
        if (!store.isEnabled) return
        val backgroundedAt = backgroundedAtMillis
        if (backgroundedAt != null && System.currentTimeMillis() - backgroundedAt > GRACE_PERIOD_MILLIS) {
            _shouldShowLock.value = true
        }
    }

    fun unlock() {
        _shouldShowLock.value = false
        backgroundedAtMillis = null
    }

    private companion object {
        const val GRACE_PERIOD_MILLIS = 30_000L
    }
}
