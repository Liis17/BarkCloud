package com.barkfluff.BarkCloud.grpc

import io.grpc.CallOptions
import io.grpc.Channel
import io.grpc.ClientCall
import io.grpc.ClientInterceptor
import io.grpc.ForwardingClientCall
import io.grpc.ForwardingClientCallListener
import io.grpc.Metadata
import io.grpc.MethodDescriptor
import io.grpc.Status
import kotlinx.coroutines.runBlocking

class AuthInterceptor(
    /** Сервер отверг отправленный access-token (UNAUTHENTICATED): его нужно перевыпустить. */
    private val onUnauthenticated: (rejectedToken: String) -> Unit = {},
    private val tokenProvider: suspend (String) -> String?,
) : ClientInterceptor {

    override fun <ReqT : Any, RespT : Any> interceptCall(
        method: MethodDescriptor<ReqT, RespT>,
        callOptions: CallOptions,
        next: Channel,
    ): ClientCall<ReqT, RespT> {
        return object : ForwardingClientCall.SimpleForwardingClientCall<ReqT, RespT>(
            next.newCall(method, callOptions)
        ) {
            override fun start(responseListener: Listener<RespT>, headers: Metadata) {
                val methodName = method.fullMethodName.substringAfterLast('/')
                val token = if (methodName in UNAUTHENTICATED_METHODS) null else runBlocking {
                    tokenProvider(methodName)
                }
                if (token.isNullOrBlank()) {
                    super.start(responseListener, headers)
                    return
                }
                headers.put(AUTH_TOKEN_KEY, token)
                super.start(object : ForwardingClientCallListener.SimpleForwardingClientCallListener<RespT>(responseListener) {
                    override fun onClose(status: Status, trailers: Metadata) {
                        if (status.code == Status.Code.UNAUTHENTICATED) onUnauthenticated(token)
                        super.onClose(status, trailers)
                    }
                }, headers)
            }
        }
    }

    private companion object {
        val UNAUTHENTICATED_METHODS = setOf("Auth", "CreateToken")
        val AUTH_TOKEN_KEY: Metadata.Key<String> =
            Metadata.Key.of("x-auth-token", Metadata.ASCII_STRING_MARSHALLER)
    }
}

/** Добавляет уже разрешённый access-token без синхронного вызова refresher. */
class FixedTokenInterceptor(
    private val accessToken: String,
) : ClientInterceptor {

    override fun <ReqT : Any, RespT : Any> interceptCall(
        method: MethodDescriptor<ReqT, RespT>,
        callOptions: CallOptions,
        next: Channel,
    ): ClientCall<ReqT, RespT> = object : ForwardingClientCall.SimpleForwardingClientCall<ReqT, RespT>(
        next.newCall(method, callOptions)
    ) {
        override fun start(responseListener: Listener<RespT>, headers: Metadata) {
            if (accessToken.isNotBlank()) headers.put(AUTH_TOKEN_KEY, accessToken)
            super.start(responseListener, headers)
        }
    }

    private companion object {
        val AUTH_TOKEN_KEY: Metadata.Key<String> =
            Metadata.Key.of("x-auth-token", Metadata.ASCII_STRING_MARSHALLER)
    }
}
