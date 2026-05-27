package com.barkfluff.BarkCloud

import android.app.Application
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.request.crossfade
import coil3.video.VideoFrameDecoder
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.SessionManager
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.users.UserRepository
import com.barkfluff.BarkCloud.files.data.LocalFileRepository
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp

class BarkCloudApplication : Application(), SingletonImageLoader.Factory {

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

    lateinit var userRepository: UserRepository
        private set

    lateinit var sessionManager: SessionManager
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
        userRepository = UserRepository(grpcManager, fileTransfer)
        sessionManager = SessionManager(this, authRepository, globalParam, grpcManager)
    }

    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components {
                // Превью облака отдаются по self-signed TLS — тянем их через тот же
                // доверяющий клиент, что и upload/download.
                add(OkHttpNetworkFetcherFactory(callFactory = { InsecureHttp.client }))
                add(VideoFrameDecoder.Factory())
            }
            .crossfade(true)
            .build()

    override fun onTerminate() {
        grpcManager.shutdown()
        super.onTerminate()
    }
}
