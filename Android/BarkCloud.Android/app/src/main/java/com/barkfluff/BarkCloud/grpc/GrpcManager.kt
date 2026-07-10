package com.barkfluff.BarkCloud.grpc

import barkcloud.files.AlbumApiGrpcKt
import barkcloud.files.CloudApiGrpcKt
import barkcloud.files.DynamicFolderApiGrpcKt
import barkcloud.files.FilesApiGrpcKt
import barkcloud.identity.IdentityApiGrpcKt
import barkcloud.identity.IdentityApiOuterClass.CreateTokenRequest
import barkcloud.users.UsersApiGrpcKt
import com.barkfluff.BarkCloud.BuildConfig
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.TokenRefresher
import com.barkfluff.BarkCloud.net.InsecureTls
import io.grpc.Channel
import io.grpc.ClientInterceptors
import io.grpc.ManagedChannel
import io.grpc.okhttp.OkHttpChannelBuilder
import java.util.concurrent.ConcurrentHashMap

/**
 * Управляет gRPC-каналами ко всем сервисам. На каждый адрес — один кэшированный
 * канал (OkHttp-транспорт + интерсепторы [AuthInterceptor] и [ClientMetadataInterceptor]),
 * поверх которого создаются типизированные стабы. FilesApi / CloudApi / AlbumApi
 * живут на одном адресе (:7025) и делят канал.
 */
class GrpcManager(
    private val globalParam: GlobalParam,
    private val metadataInterceptor: ClientMetadataInterceptor,
) {

    private val interceptedChannels = ConcurrentHashMap<String, Channel>()
    private val publicChannels = ConcurrentHashMap<String, Channel>()
    private val managedChannels = ConcurrentHashMap<String, ManagedChannel>()

    private val tokenRefresher = TokenRefresher(globalParam) { refreshToken ->
        val response = publicIdentityStub().createToken(
            CreateTokenRequest.newBuilder().setRefreshToken(refreshToken).build(),
        )
        TokenRefresher.RefreshedAccessToken(
            value = response.accessToken.value,
            expiresAtMillis = response.accessToken.expirationDate.seconds * 1000L,
        )
    }

    fun identityStub(): IdentityApiGrpcKt.IdentityApiCoroutineStub =
        IdentityApiGrpcKt.IdentityApiCoroutineStub(channelFor(BuildConfig.IDENTITY_API_ADDRESS))

    fun usersStub(): UsersApiGrpcKt.UsersApiCoroutineStub =
        UsersApiGrpcKt.UsersApiCoroutineStub(channelFor(BuildConfig.USERS_API_ADDRESS))

    fun filesStub(): FilesApiGrpcKt.FilesApiCoroutineStub =
        FilesApiGrpcKt.FilesApiCoroutineStub(channelFor(BuildConfig.FILES_API_ADDRESS))

    fun cloudStub(): CloudApiGrpcKt.CloudApiCoroutineStub =
        CloudApiGrpcKt.CloudApiCoroutineStub(channelFor(BuildConfig.FILES_API_ADDRESS))

    fun albumStub(): AlbumApiGrpcKt.AlbumApiCoroutineStub =
        AlbumApiGrpcKt.AlbumApiCoroutineStub(channelFor(BuildConfig.FILES_API_ADDRESS))

    fun dynamicFolderStub(): DynamicFolderApiGrpcKt.DynamicFolderApiCoroutineStub =
        DynamicFolderApiGrpcKt.DynamicFolderApiCoroutineStub(channelFor(BuildConfig.FILES_API_ADDRESS))

    suspend fun validAccessToken(): String? = tokenRefresher.validAccessToken()

    private fun channelFor(address: String): Channel =
        interceptedChannels.computeIfAbsent(address) {
            val managed = createChannel(it)
            managedChannels[it] = managed
            ClientInterceptors.intercept(managed, AuthInterceptor { tokenRefresher.validAccessToken() }, metadataInterceptor)
        }

    private fun publicIdentityStub(): IdentityApiGrpcKt.IdentityApiCoroutineStub =
        IdentityApiGrpcKt.IdentityApiCoroutineStub(publicChannelFor(BuildConfig.IDENTITY_API_ADDRESS))

    private fun publicChannelFor(address: String): Channel =
        publicChannels.computeIfAbsent(address) {
            val managed = createChannel(it)
            managedChannels["public:$it"] = managed
            ClientInterceptors.intercept(managed, metadataInterceptor)
        }

    fun shutdown() {
        managedChannels.values.forEach { it.shutdownNow() }
        managedChannels.clear()
        interceptedChannels.clear()
        publicChannels.clear()
    }

    private fun createChannel(address: String): ManagedChannel {
        val url = ensureScheme(address)
        val useTls = url.startsWith("https://")
        val hostPort = url.removePrefix("http://").removePrefix("https://")
        val parts = hostPort.split(":")
        val host = parts[0]
        val port = parts.getOrNull(1)?.toIntOrNull() ?: if (useTls) 443 else 80

        val builder = OkHttpChannelBuilder.forAddress(host, port)
        if (useTls) {
            // Сервер использует самоподписанный сертификат — доверяем всем.
            builder.sslSocketFactory(InsecureTls.socketFactory())
        } else {
            builder.usePlaintext()
        }
        return builder.build()
    }

    private fun ensureScheme(url: String): String =
        if (url.startsWith("http://") || url.startsWith("https://")) url else "https://$url"
}
