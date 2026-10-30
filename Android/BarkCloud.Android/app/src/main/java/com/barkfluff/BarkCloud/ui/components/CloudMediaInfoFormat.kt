package com.barkfluff.BarkCloud.ui.components

import barkcloud.files.FilesApiOuterClass.FileMetadataInfo

/**
 * Плоский список «подпись → значение» только по заданным полям метаданных.
 * Форматирование повторяет веб-версию (PropertiesModal.buildMetadataRows).
 */
internal fun metadataRows(m: FileMetadataInfo): List<Pair<String, String>> = buildList {
    if (m.hasTakenAt()) add("Дата съёмки" to formatTimestamp(m.takenAt.seconds))
    val device = listOf(m.cameraMake, m.cameraModel).filter { it.isNotBlank() }.joinToString(" ")
    if (device.isNotEmpty()) add("Устройство" to device)
    if (m.lensModel.isNotBlank()) add("Объектив" to m.lensModel)
    if (m.creatorTool.isNotBlank()) add("Программа" to m.creatorTool)
    if (m.hasFocalLengthMm()) add("Фокусное расстояние" to "%.1f мм".format(m.focalLengthMm))
    if (m.hasFNumber()) add("Диафрагма" to "f/%.1f".format(m.fNumber))
    if (m.hasExposureTimeSeconds()) add("Выдержка" to formatExposure(m.exposureTimeSeconds))
    if (m.hasIso()) add("ISO" to m.iso.toString())
    if (m.hasFlash()) add("Вспышка" to if (m.flash) "Сработала" else "Не сработала")
    if (m.hasAltitude()) add("Высота" to "%.1f м".format(m.altitude))
    if (m.hasDurationSeconds()) add("Длительность" to formatDuration(m.durationSeconds))
    if (m.videoCodec.isNotBlank()) add("Видеокодек" to m.videoCodec)
    if (m.audioCodec.isNotBlank()) add("Аудиокодек" to m.audioCodec)
    if (m.hasFrameRate()) add("Частота кадров" to "%.2f fps".format(m.frameRate))
    if (m.hasBitrate()) add("Битрейт" to formatBitrate(m.bitrate))
    if (m.documentAuthor.isNotBlank()) add("Автор" to m.documentAuthor)
    if (m.documentTitle.isNotBlank()) add("Заголовок" to m.documentTitle)
    if (m.documentSubject.isNotBlank()) add("Тема" to m.documentSubject)
    if (m.hasDocumentPageCount()) add("Страниц" to m.documentPageCount.toString())
}

/** Координаты GPS, если камера записала место съёмки. */
internal fun coordinatesOf(m: FileMetadataInfo): Pair<Double, Double>? =
    if (m.hasLatitude() && m.hasLongitude()) m.latitude to m.longitude else null

/** Встроенная карта OpenStreetMap, как в веб-версии (PropertiesModal.FileLocationMap). */
internal fun osmEmbedUrl(lat: Double, lon: Double): String {
    val d = 0.008 // полуразмер видимой области в градусах (~900 м)
    val bbox = "${lon - d},${lat - d},${lon + d},${lat + d}"
    return "https://www.openstreetmap.org/export/embed.html?bbox=$bbox&layer=mapnik&marker=$lat,$lon"
}

/** Страница OpenStreetMap для открытия в браузере по тапу. */
internal fun osmPageUrl(lat: Double, lon: Double): String =
    "https://www.openstreetmap.org/?mlat=$lat&mlon=$lon#map=15/$lat/$lon"

private fun formatExposure(seconds: Double): String = when {
    seconds <= 0 -> "—"
    seconds >= 1 -> "%.1f с".format(seconds)
    // Короткие выдержки — как 1/N, округление по обратной величине.
    else -> "1/${Math.round(1 / seconds)} с"
}

private fun formatDuration(seconds: Double): String {
    val s = Math.round(seconds)
    val h = s / 3600
    val m = (s % 3600) / 60
    val sec = s % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, sec) else "%d:%02d".format(m, sec)
}

private fun formatBitrate(bps: Long): String = when {
    bps >= 1_000_000 -> "%.2f Мбит/с".format(bps / 1_000_000.0)
    bps >= 1_000 -> "%d Кбит/с".format(bps / 1_000)
    else -> "$bps бит/с"
}

internal fun formatTimestamp(epochSeconds: Long): String =
    java.text.SimpleDateFormat("dd.MM.yyyy HH:mm", java.util.Locale.getDefault())
        .format(java.util.Date(epochSeconds * 1000L))
