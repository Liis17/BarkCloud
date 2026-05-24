package com.barkfluff.BarkCloud

import android.app.Application
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.request.crossfade
import coil3.video.VideoFrameDecoder
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.files.data.LocalFileRepository
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager

class BarkCloudApplication : Application(), SingletonImageLoader.Factory {

    lateinit var globalParam: GlobalParam
        private set

    lateinit var grpcManager: GrpcManager
        private set

    lateinit var authRepository: AuthRepository
        private set

    lateinit var localFileRepository: LocalFileRepository
        private set

    override fun onCreate() {
        super.onCreate()
        globalParam = GlobalParam(this)
        grpcManager = GrpcManager(globalParam, ClientMetadataInterceptor.create(this))
        authRepository = AuthRepository(grpcManager, globalParam)
        localFileRepository = LocalFileRepository()
    }

    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(VideoFrameDecoder.Factory()) }
            .crossfade(true)
            .build()

    override fun onTerminate() {
        grpcManager.shutdown()
        super.onTerminate()
    }
}
