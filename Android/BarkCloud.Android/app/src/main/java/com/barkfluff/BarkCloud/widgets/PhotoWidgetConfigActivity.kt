package com.barkfluff.BarkCloud.widgets

import android.appwidget.AppWidgetManager
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ListItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import kotlinx.coroutines.CancellationException

/** Экран выбора альбома, который открывается при добавлении фото-виджета. */
class PhotoWidgetConfigActivity : ComponentActivity() {
    private var widgetId = AppWidgetManager.INVALID_APPWIDGET_ID

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        widgetId = intent?.extras?.getInt(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID)
            ?: AppWidgetManager.INVALID_APPWIDGET_ID
        // Без RESULT_OK лаунчер удалит виджет, поэтому отмена — явный RESULT_CANCELED.
        setResult(RESULT_CANCELED)
        if (widgetId == AppWidgetManager.INVALID_APPWIDGET_ID) {
            finish()
            return
        }
        enableEdgeToEdge()
        setContent {
            BarkCloudTheme {
                AlbumPickerScreen(
                    signedIn = WidgetRefresh.isSignedIn(this),
                    loadAlbums = { (application as BarkCloudApplication).albumRepository.listAlbums(limit = 100).albums },
                    onPick = ::pickAlbum,
                )
            }
        }
    }

    private fun pickAlbum(album: AlbumCard) {
        PhotoWidgetStore.saveAlbum(this, widgetId, album.id, album.name)
        PhotoWidgetProvider.refreshAll(this)
        WidgetRefresh.refreshNow(this)
        setResult(RESULT_OK, Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId))
        finish()
    }
}

private sealed interface PickerState {
    data object Loading : PickerState
    data object LoginRequired : PickerState
    data object Empty : PickerState
    data object Failed : PickerState
    data class Loaded(val albums: List<AlbumCard>) : PickerState
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AlbumPickerScreen(
    signedIn: Boolean,
    loadAlbums: suspend () -> List<AlbumCard>,
    onPick: (AlbumCard) -> Unit,
) {
    var state by remember { mutableStateOf<PickerState>(PickerState.Loading) }
    LaunchedEffect(Unit) {
        state = if (!signedIn) {
            PickerState.LoginRequired
        } else {
            try {
                val albums = loadAlbums()
                if (albums.isEmpty()) PickerState.Empty else PickerState.Loaded(albums)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                PickerState.Failed
            }
        }
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text(stringResource(R.string.widget_photo_pick_title)) }) },
    ) { padding ->
        when (val current = state) {
            PickerState.Loading -> Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            PickerState.LoginRequired -> Text(
                stringResource(R.string.widget_photo_pick_login),
                modifier = Modifier.padding(padding).padding(16.dp),
            )
            PickerState.Empty, PickerState.Failed -> Text(
                stringResource(R.string.widget_photo_pick_empty),
                modifier = Modifier.padding(padding).padding(16.dp),
            )
            is PickerState.Loaded -> LazyColumn(contentPadding = padding) {
                items(current.albums, key = { it.id }) { album ->
                    ListItem(
                        headlineContent = { Text(album.name) },
                        modifier = Modifier.clickable { onPick(album) },
                    )
                }
            }
        }
    }
}
