package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CloudCopy

/**
 * Удаление фото устройства. [KEEP_CLOUD] — «удалить с устройства»: облачная копия сохраняется,
 * поэтому фото без подтверждённой копии в облаке не удаляется. [EVERYWHERE] — обычное удаление:
 * устройство и все живые облачные копии с тем же хешем; фото только на устройстве удаляется как есть.
 */
internal enum class DeviceDeleteMode { KEEP_CLOUD, EVERYWHERE }

/** Фото с уже определённым состоянием облачной копии (хеш и ответ сервера). */
internal data class DeviceDeleteItem(val mediaKey: String, val cloud: CloudCopy)

internal data class DeviceDeletePlan(
    val mode: DeviceDeleteMode,
    /** Фото, которые удаляются с устройства. */
    val devicePhotos: List<String>,
    /** Облачные копии для каждого фото (только [DeviceDeleteMode.EVERYWHERE]). */
    val cloudCopies: Map<String, List<String>>,
    /** Фото, которые не участвуют: состояние облачной копии неизвестно или условие режима не выполнено. */
    val skipped: List<String>,
) {
    /** Число облачных копий, которые будут удалены: показывается в подтверждении. */
    val cloudCopyCount: Int get() = cloudCopies.values.sumOf { it.size }
}

/**
 * Строит план по актуальным состояниям. Неизвестное состояние не даёт ни удалить фото, ни
 * пропустить облачные копии: такое фото попадает в [DeviceDeletePlan.skipped].
 */
internal fun planDeviceDelete(mode: DeviceDeleteMode, items: List<DeviceDeleteItem>): DeviceDeletePlan {
    val devicePhotos = mutableListOf<String>()
    val cloudCopies = linkedMapOf<String, List<String>>()
    val skipped = mutableListOf<String>()
    for (item in items) {
        when (mode) {
            DeviceDeleteMode.KEEP_CLOUD -> when (item.cloud) {
                is CloudCopy.Present -> devicePhotos += item.mediaKey
                else -> skipped += item.mediaKey
            }
            DeviceDeleteMode.EVERYWHERE -> when (val cloud = item.cloud) {
                is CloudCopy.Present -> {
                    devicePhotos += item.mediaKey
                    cloudCopies[item.mediaKey] = cloud.fileIds
                }
                CloudCopy.Absent -> devicePhotos += item.mediaKey
                CloudCopy.Unknown -> skipped += item.mediaKey
            }
        }
    }
    return DeviceDeletePlan(mode, devicePhotos, cloudCopies, skipped)
}

/** Облачные копии, которые пересекаются с набором скрытых (vault) ID: для них нужна разблокировка. */
internal fun DeviceDeletePlan.hiddenCopyIds(vaultIds: Set<String>): Set<String> =
    cloudCopies.values.flatten().filter { it in vaultIds }.toSet()
