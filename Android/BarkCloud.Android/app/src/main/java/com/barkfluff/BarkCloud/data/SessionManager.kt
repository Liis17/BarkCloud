package com.barkfluff.BarkCloud.data

import android.content.Context
import coil3.SingletonImageLoader
import com.barkfluff.BarkCloud.grpc.GrpcManager

/**
 * Централизованный выход из аккаунта (зеркалит iOS `AppEnvironment.signOut`).
 * [signOut] отзывает сессию на сервере и чистит локальное состояние; [resetLocalState]
 * используется при удалении аккаунта (сервер уже инвалидировал сессию).
 */
class SessionManager(
    private val appContext: Context,
    private val authRepository: AuthRepository,
    private val globalParam: GlobalParam,
    private val grpcManager: GrpcManager,
) {

    suspend fun signOut() {
        authRepository.logout()
        resetLocalState()
    }

    fun resetLocalState() {
        globalParam.clearSession()
        grpcManager.shutdown()
        val loader = SingletonImageLoader.get(appContext)
        loader.memoryCache?.clear()
        loader.diskCache?.clear()
    }
}
