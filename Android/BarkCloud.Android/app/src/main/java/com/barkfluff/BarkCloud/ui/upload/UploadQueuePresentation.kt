package com.barkfluff.BarkCloud.ui.upload

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import java.io.File

internal fun UploadJob.thumbnailModel(): Any? = mediaKey
    ?.let(DeviceMediaStore::uriForKey)
    ?: stagedFilePath?.let(::File)

@Composable
internal fun UploadJobThumbnail(
    job: UploadJob,
    modifier: Modifier = Modifier,
) {
    val placeholder = painterResource(R.drawable.ic_upload_media_placeholder)
    Box(
        modifier = modifier
            .aspectRatio(1f)
            .clip(RoundedCornerShape(8.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant),
    ) {
        AsyncImage(
            model = job.thumbnailModel(),
            contentDescription = null,
            modifier = Modifier.fillMaxSize(),
            placeholder = placeholder,
            error = placeholder,
            fallback = placeholder,
            contentScale = ContentScale.Crop,
        )
        if (job.mimeType?.startsWith("video/") == true) {
            Icon(
                imageVector = Icons.Filled.PlayCircle,
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.align(Alignment.BottomEnd).size(20.dp),
            )
        }
    }
}

@Composable
internal fun UploadJobProgressIndicator(job: UploadJob) {
    if (!job.phase.isExecuting()) return
    if (job.bytesTotal > 0) {
        LinearProgressIndicator(
            progress = { (job.bytesSent.toFloat() / job.bytesTotal).coerceIn(0f, 1f) },
            modifier = Modifier.fillMaxWidth(),
        )
    } else {
        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
    }
}

internal fun UploadPhase.isExecuting(): Boolean = when (this) {
    UploadPhase.HASHING,
    UploadPhase.CREATING_SESSION,
    UploadPhase.UPLOADING,
    UploadPhase.COMPLETING,
    UploadPhase.PROCESSING,
    UploadPhase.ATTACHING -> true
    else -> false
}

internal fun UploadPhase.label(): String = when (this) {
    UploadPhase.QUEUED -> "В очереди"
    UploadPhase.HASHING, UploadPhase.CREATING_SESSION -> "Подготовка"
    UploadPhase.UPLOADING -> "Отправляется"
    UploadPhase.COMPLETING, UploadPhase.PROCESSING -> "Обработка на сервере"
    UploadPhase.UPLOADED, UploadPhase.ATTACHING -> "Размещается в облаке"
    UploadPhase.UPLOADED_NOT_ATTACHED -> "Ожидает размещения"
    UploadPhase.PAUSED -> "На паузе"
    UploadPhase.FAILED -> "Ошибка"
    UploadPhase.COMPLETED -> "Готово"
    UploadPhase.CANCELLED -> "Отменено"
}
