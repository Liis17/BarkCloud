package com.barkfluff.BarkCloud.data

import android.content.Context
import coil3.SingletonImageLoader
import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.grpc.GrpcManager
import kotlinx.coroutines.runBlocking

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
    private val fileCache: FileCacheService,
    private val uploadQueue: UploadQueueStore,
) {

    suspend fun signOut() {
        authRepository.logout()
        resetLocalState()
    }

    fun resetLocalState() {
        globalParam.clearSession()
        AutoUploadScheduler.disable(appContext)
        grpcManager.shutdown()
        runBlocking {
            fileCache.clearAll()
            uploadQueue.clear()
            BarkCloudDatabase.get(appContext).mediaCloudStateDao().deleteAll()
        }
        val loader = SingletonImageLoader.get(appContext)
        loader.memoryCache?.clear()
        loader.diskCache?.clear()
    }
}
