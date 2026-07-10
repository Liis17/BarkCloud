package com.barkfluff.BarkCloud

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.lifecycle.lifecycleScope
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.net.queryFileName
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import kotlinx.coroutines.launch

class ShareActivity : ComponentActivity() {
    private var total by mutableIntStateOf(0)
    private var done by mutableIntStateOf(0)
    private var failed by mutableIntStateOf(0)
    private var isRunning by mutableStateOf(false)
    private var message by mutableStateOf("")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = streamUris(intent)
        total = uris.size

        setContent {
            BarkCloudTheme {
                Column(
                    modifier = Modifier.fillMaxSize().padding(24.dp),
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineSmall)
                    Text(message, modifier = Modifier.padding(top = 12.dp))
                    if (isRunning) {
                        CircularProgressIndicator(modifier = Modifier.padding(top = 20.dp))
                        LinearProgressIndicator(
                            progress = { if (total > 0) done.toFloat() / total else 0f },
                            modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
                        )
                        Text("$done/$total", modifier = Modifier.padding(top = 8.dp))
                    }
                    if (!isRunning) {
                        Button(onClick = { finish() }, modifier = Modifier.padding(top = 20.dp)) {
                            Text(stringResource(R.string.common_done))
                        }
                    }
                }
            }
        }

        if (uris.isEmpty()) {
            message = getString(R.string.share_no_files)
            return
        }
        val app = applicationContext as BarkCloudApplication
        if (!app.globalParam.hasValidRefreshToken()) {
            message = getString(R.string.share_login_required)
            return
        }
        enqueue(uris, app)
    }

    private fun enqueue(uris: List<Uri>, app: BarkCloudApplication) {
        isRunning = true
        message = getString(R.string.share_staging)
        lifecycleScope.launch {
            uris.forEach { uri ->
                runCatching {
                    app.uploadQueue.enqueue(
                        uri,
                        queryFileName(this@ShareActivity, uri),
                        source = com.barkfluff.BarkCloud.data.upload.UploadSource.SHARE,
                    )
                }.onFailure {
                    failed++
                }
                done++
            }
            isRunning = false
            if (failed < uris.size) UploadScheduler.enqueue(this@ShareActivity)
            message = if (failed == 0) {
                getString(R.string.share_queued)
            } else {
                getString(R.string.share_upload_failed, failed)
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun streamUris(intent: Intent?): List<Uri> = when (intent?.action) {
        Intent.ACTION_SEND -> {
            val uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            } else {
                intent.getParcelableExtra(Intent.EXTRA_STREAM)
            }
            listOfNotNull(uri)
        }
        Intent.ACTION_SEND_MULTIPLE -> {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
            } else {
                intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)
            }.orEmpty()
        }
        else -> emptyList()
    }
}
