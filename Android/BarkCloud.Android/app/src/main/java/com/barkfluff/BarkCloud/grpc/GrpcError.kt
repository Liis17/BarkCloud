package com.barkfluff.BarkCloud.grpc

import io.grpc.Metadata
import io.grpc.StatusException
import io.grpc.StatusRuntimeException

private val X_ERROR_CODE: Metadata.Key<String> =
    Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER)

fun StatusRuntimeException.errorCode(): String? = trailers?.get(X_ERROR_CODE)

/** Coroutine stubs throw checked StatusException; preserve the same trailers as Java stubs. */
fun Throwable.grpcFailureOrNull(): StatusRuntimeException? = when (this) {
    is StatusRuntimeException -> this
    is StatusException -> StatusRuntimeException(status, trailers)
    else -> null
}
