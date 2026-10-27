package com.barkfluff.BarkCloud.ui.gallery

internal const val DEVICE_SELECTION_PREFIX = "device:"
internal const val CLOUD_SELECTION_PREFIX = "cloud:"

internal data class PhotoSelection(val keys: Set<String> = emptySet()) {
    val isSelecting: Boolean get() = keys.isNotEmpty()

    fun start(key: String): PhotoSelection = copy(keys = keys + key)

    fun toggle(key: String): PhotoSelection = copy(
        keys = if (key in keys) keys - key else keys + key,
    )

    fun remove(keysToRemove: Set<String>): PhotoSelection = copy(keys = keys - keysToRemove)

    fun clear(): PhotoSelection = PhotoSelection()
}

internal data class PhotoSelectionActions(
    val deviceCount: Int,
    val cloudCount: Int,
) {
    val uploadCount: Int get() = deviceCount
    val favoriteCount: Int get() = cloudCount
    val deleteCount: Int get() = deviceCount + cloudCount
}

internal fun photoSelectionActions(keys: Set<String>): PhotoSelectionActions = PhotoSelectionActions(
    deviceCount = keys.count { it.startsWith(DEVICE_SELECTION_PREFIX) },
    cloudCount = keys.count { it.startsWith(CLOUD_SELECTION_PREFIX) },
)

internal enum class PhotoDeleteAction {
    DEVICE,
    CLOUD,
    BOTH,
}

internal fun photoDeleteActions(actions: PhotoSelectionActions): List<PhotoDeleteAction> = buildList {
    if (actions.deviceCount > 0) add(PhotoDeleteAction.DEVICE)
    if (actions.cloudCount > 0) add(PhotoDeleteAction.CLOUD)
    if (actions.deviceCount > 0 && actions.cloudCount > 0) add(PhotoDeleteAction.BOTH)
}
