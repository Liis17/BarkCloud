package com.barkfluff.BarkCloud.grpc

import com.barkfluff.BarkCloud.net.InsecureTls
import grpc.reflection.v1alpha.Reflection.ServerReflectionRequest
import grpc.reflection.v1alpha.ServerReflectionGrpcKt
import io.grpc.okhttp.OkHttpChannelBuilder
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOf
import java.util.concurrent.TimeUnit

fun interface ServerConnectionProbe {
    suspend fun check(config: ServerConfig, port: Int, expectedService: String): Result<Unit>
}

class ReflectionServerConnectionProbe : ServerConnectionProbe {
    override suspend fun check(config: ServerConfig, port: Int, expectedService: String): Result<Unit> {
        val builder = OkHttpChannelBuilder.forAddress(config.hostname, port)
        if (!config.usesTls) builder.usePlaintext()
        else if (config.allowSelfSigned) builder.sslSocketFactory(InsecureTls.socketFactory())
        val channel = builder.build()
        return try {
            val request = ServerReflectionRequest.newBuilder().setListServices("").build()
            val response = ServerReflectionGrpcKt.ServerReflectionCoroutineStub(channel)
                .withDeadlineAfter(5, TimeUnit.SECONDS)
                .serverReflectionInfo(flowOf(request)).first()
            check(response.listServicesResponse.serviceList.any { it.name == expectedService }) {
                "На этом порту не найден нужный сервис"
            }
            Result.success(Unit)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Result.failure(e)
        } finally {
            channel.shutdownNow()
        }
    }
}
