package com.barkfluff.BarkCloud.grpc

import io.grpc.StatusRuntimeException

/**
 * Человекочитаемое сообщение из gRPC-ошибки. Доменные GUID-коды приходят в трейлере
 * `x-error-code` (см. [errorCode]); пока используем описание статуса как запасной
 * вариант — точечный маппинг кодов добавляется по мере появления экранов.
 */
fun StatusRuntimeException.domainMessage(): String =
    status.description?.takeIf { it.isNotBlank() } ?: message ?: status.code.name
