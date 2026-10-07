package com.barkfluff.BarkCloud.data

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.receiveAsFlow

/** Requests originate only from a user action that enables notification-backed work. */
object NotificationPermissionRequests {
    private val requests = Channel<Unit>(Channel.CONFLATED)
    val events = requests.receiveAsFlow()
    fun request() { requests.trySend(Unit) }
}
