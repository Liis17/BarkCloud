package com.barkfluff.BarkCloud

import android.app.Application
import android.database.ContentObserver
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import androidx.lifecycle.ProcessLifecycleOwner
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.disk.DiskCache
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.request.crossfade
import coil3.video.VideoFrameDecoder
import com.barkfluff.BarkCloud.data.AppLockManager
import com.barkfluff.BarkCloud.data.AppLockStore
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.SessionManager
import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.cache.FileCacheSettings
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRepository
import com.barkfluff.BarkCloud.data.cloud.SharedRepository
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.users.UserRepository
import com.barkfluff.BarkCloud.files.data.LocalFileRepository
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File
import okio.Path.Companion.toOkioPath

class BarkCloudApplication : Application(), SingletonImageLoader.Factory {

    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var mediaChangeJob: Job? = null
    private var mediaObserver: ContentObserver? = null

    lateinit var globalParam: GlobalParam
        private set

    lateinit var grpcManager: GrpcManager
        private set

    lateinit var authRepository: AuthRepository
        private set

    lateinit var localFileRepository: LocalFileRepository
        private set

    lateinit var fileTransfer: FileTransferService
        private set

    lateinit var cloudRepository: CloudRepository
        private set

    lateinit var albumRepository: AlbumRepository
        private set

    lateinit var dynamicFolderRepository: DynamicFolderRepository
        private set

    lateinit var sharedRepository: SharedRepository
        private set

    lateinit var userRepository: UserRepository
        private set

    lateinit var sessionManager: SessionManager
        private set

    lateinit var fileCacheSettings: FileCacheSettings
        private set

    lateinit var fileCache: FileCacheService
        private set

    lateinit var autoUploadSettings: AutoUploadSettings
        private set

    lateinit var uploadQueue: UploadQueueStore
        private set

    lateinit var appLockStore: AppLockStore
        private set

    lateinit var appLockManager: AppLockManager
        private set

    override fun onCreate() {
        super.onCreate()
        globalParam = GlobalParam(this)
        grpcManager = GrpcManager(globalParam, ClientMetadataInterceptor.create(this))
        authRepository = AuthRepository(grpcManager, globalParam)
        localFileRepository = LocalFileRepository()
        fileTransfer = FileTransferService(this, grpcManager, globalParam, InsecureHttp.client)
        cloudRepository = CloudRepository(grpcManager, fileTransfer)
        albumRepository = AlbumRepository(grpcManager)
        dynamicFolderRepository = DynamicFolderRepository(grpcManager)
        sharedRepository = SharedRepository(grpcManager)
        userRepository = UserRepository(grpcManager, fileTransfer)
        fileCacheSettings = FileCacheSettings(this)
        fileCache = FileCacheService(this, fileTransfer, fileCacheSettings)
        autoUploadSettings = AutoUploadSettings(this)
        uploadQueue = UploadQueueStore(this)
        sessionManager = SessionManager(this, authRepository, globalParam, grpcManager, fileCache, uploadQueue)
        appLockStore = AppLockStore(this)
        appLockManager = AppLockManager(appLockStore)
        ProcessLifecycleOwner.get().lifecycle.addObserver(appLockManager)
        appScope.launch {
            fileCache.runStartupSweepIfNeeded()
            uploadQueue.initialize()
        }
        if (autoUploadSettings.enabled) {
            AutoUploadScheduler.apply(this, autoUploadSettings.policy)
        }
        observeMediaStoreChanges()
    }

    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components {
                // Превью облака отдаются по self-signed TLS — тянем их через тот же
                // доверяющий клиент, что и upload/download.
                add(OkHttpNetworkFetcherFactory(callFactory = { InsecureHttp.client }))
                add(VideoFrameDecoder.Factory())
            }
            .diskCache {
                DiskCache.Builder()
                    .directory(File(context.cacheDir, "BarkCloudFiles/previews").toOkioPath())
                    .maxSizeBytes(PREVIEW_CACHE_MAX_BYTES)
                    .build()
            }
            .crossfade(true)
            .build()

    override fun onTerminate() {
        mediaObserver?.let(contentResolver::unregisterContentObserver)
        grpcManager.shutdown()
        super.onTerminate()
    }

    private fun observeMediaStoreChanges() {
        val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) {
                super.onChange(selfChange)
                mediaChangeJob?.cancel()
                mediaChangeJob = appScope.launch {
                    delay(2_000)
                    if (autoUploadSettings.enabled) {
                        AutoUploadScheduler.runOnce(this@BarkCloudApplication, autoUploadSettings.policy)
                    }
                }
            }
        }
        mediaObserver = observer
        contentResolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, observer)
        contentResolver.registerContentObserver(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, true, observer)
    }

    private companion object {
        const val PREVIEW_CACHE_MAX_BYTES = 256L * 1024L * 1024L
    }
}
