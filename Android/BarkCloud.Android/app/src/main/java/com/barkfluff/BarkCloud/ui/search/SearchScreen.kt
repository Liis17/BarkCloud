package com.barkfluff.BarkCloud.ui.search

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import barkcloud.files.FilesApiOuterClass.SearchHit
import barkcloud.files.FilesApiOuterClass.SearchHitKind
import barkcloud.files.FilesApiOuterClass.SearchSection
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.cloud.MediaPreview
import com.barkfluff.BarkCloud.data.cloud.toEpochMillis
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.ViewerPagination
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.ui.components.rememberRemoteOpener
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SearchScreen(
    onOpenAlbum: (String, String) -> Unit,
    onOpenFolder: (String, String) -> Unit,
    onOpenSmartFolder: (String, String) -> Unit,
    menu: @Composable () -> Unit = {},
    viewModel: SearchViewModel = viewModel(factory = SearchViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val openRemote = rememberRemoteOpener()
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    var viewerSection by remember { mutableStateOf<SearchSection?>(null) }
    var viewerAsset by remember { mutableStateOf<MediaAsset?>(null) }
    val viewerAssets = remember(state.sections, viewerSection) {
        state.sections.firstOrNull { it.section == viewerSection }?.hitsList.orEmpty()
            .filter(SearchHit::isViewerMediaHit)
            .map(SearchHit::toViewerAsset)
    }
    val viewerSectionResult = state.sections.firstOrNull { it.section == viewerSection }
    CloudMediaViewer(
        assets = viewerAssets,
        selectedAsset = viewerAsset,
        onDismiss = { viewerAsset = null },
        pagination = ViewerPagination(hasMore = viewerSectionResult?.hasMore == true, isLoading = state.isLoading),
        onPageNearEnd = {
            state.sections.firstOrNull { it.section == viewerSection }?.let { viewModel.loadMore(it) }
        },
    )
    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text(stringResource(R.string.tab_search)) },
                actions = { menu() },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            OutlinedTextField(
                value = state.query,
                onValueChange = viewModel::setQuery,
                label = { Text(stringResource(R.string.search_hint)) },
                leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            )
            if (state.isLoading) LinearProgressIndicator(Modifier.fillMaxWidth())
            LazyColumn(Modifier.fillMaxSize()) {
                if (state.query.trim().length < 2) {
                    item { Text(stringResource(R.string.search_prompt), Modifier.padding(24.dp)) }
                } else if (!state.isLoading && state.error == null && state.sections.all { it.hitsCount == 0 }) {
                    item { Text(stringResource(R.string.search_empty), Modifier.padding(24.dp)) }
                }
                state.error?.let { error ->
                    item {
                        Text(error, Modifier.padding(16.dp), color = MaterialTheme.colorScheme.error)
                        TextButton(onClick = { viewModel.setQuery(state.query) }) {
                            Text(stringResource(R.string.common_refresh))
                        }
                    }
                }
                state.sections.forEach { section ->
                    if (section.hitsCount > 0) {
                        item {
                            Text(
                                stringResource(sectionLabel(section.section)),
                                Modifier.padding(start = 16.dp, top = 20.dp, bottom = 4.dp),
                                style = MaterialTheme.typography.titleSmall,
                            )
                        }
                        items(section.hitsList, key = { "${section.section}:${it.kind}:${it.id}" }) { hit ->
                            ListItem(
                                headlineContent = { Text(hit.title) },
                                supportingContent = {
                                    Text(listOf(hit.subtitle, hit.matchValue).filter { it.isNotBlank() }.distinct().joinToString(" · "))
                                },
                                modifier = Modifier.clickable {
                                    when (hit.kind) {
                                        SearchHitKind.SEARCH_HIT_KIND_ALBUM -> onOpenAlbum(hit.id, hit.title)
                                        SearchHitKind.SEARCH_HIT_KIND_DYNAMIC_FOLDER -> onOpenSmartFolder(hit.id, hit.title)
                                        SearchHitKind.SEARCH_HIT_KIND_FOLDER -> onOpenFolder(hit.id, hit.title)
                                        else -> if (hit.isViewerMediaHit) {
                                            viewerSection = section.section
                                            viewerAsset = hit.toViewerAsset()
                                        } else if (hit.fileId.isNotBlank()) openRemote(hit.fileId, hit.title) {
                                            scope.launch { snackbar.showSnackbar(it) }
                                        }
                                    }
                                },
                            )
                        }
                    }
                    if (section.hasMore) {
                        item {
                            TextButton(onClick = { viewModel.loadMore(section) }, enabled = !state.isLoading) {
                                Text(stringResource(R.string.common_load_more))
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun sectionLabel(section: SearchSection): Int = when (section) {
    SearchSection.SEARCH_SECTION_PHOTOS -> R.string.media_segment_photos
    SearchSection.SEARCH_SECTION_VIDEOS -> R.string.media_segment_videos
    SearchSection.SEARCH_SECTION_ALBUMS -> R.string.search_albums
    SearchSection.SEARCH_SECTION_FOLDERS -> R.string.search_folders
    else -> R.string.tab_files
}

private const val SEARCH_PREVIEW_WIDTH_PX = 512

/** Фото и видео результатов открываются в просмотрщике; остальные результаты открываются как раньше. */
private val SearchHit.isViewerMediaHit: Boolean
    get() = kind == SearchHitKind.SEARCH_HIT_KIND_PHOTO || kind == SearchHitKind.SEARCH_HIT_KIND_VIDEO

/** Собственный файл поиска: `file_id` хита — идентификатор блоба, как в `openRemote`. */
private fun SearchHit.toViewerAsset(): MediaAsset = MediaAsset(
    id = fileId,
    fileName = title,
    fileSize = size,
    kind = if (kind == SearchHitKind.SEARCH_HIT_KIND_VIDEO) CloudMediaKind.VIDEO else CloudMediaKind.PHOTO,
    previews = previewUrl.takeIf { it.isNotEmpty() }
        ?.let { listOf(MediaPreview(it, width = SEARCH_PREVIEW_WIDTH_PX)) }
        .orEmpty(),
    createdAtMillis = if (hasCreatedAt()) createdAt.toEpochMillis() else 0L,
    jpegViewUrl = jpegViewUrl.ifEmpty { null },
)
