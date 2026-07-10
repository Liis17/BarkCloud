package com.barkfluff.BarkCloud.grpc

import io.grpc.CallOptions
import io.grpc.Channel
import io.grpc.ClientCall
import io.grpc.ClientInterceptor
import io.grpc.ForwardingClientCall
import io.grpc.Metadata
import io.grpc.MethodDescriptor
import kotlinx.coroutines.runBlocking

class AuthInterceptor(
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
                if (!token.isNullOrBlank()) {
                    headers.put(AUTH_TOKEN_KEY, token)
                }
                super.start(responseListener, headers)
            }
        }
    }

    private companion object {
        val UNAUTHENTICATED_METHODS = setOf("Auth", "CreateToken")
        val AUTH_TOKEN_KEY: Metadata.Key<String> =
            Metadata.Key.of("x-auth-token", Metadata.ASCII_STRING_MARSHALLER)
    }
}
