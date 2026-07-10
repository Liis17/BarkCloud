package com.barkfluff.BarkCloud.ui.shared

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.OutgoingFolderShareGroup
import com.barkfluff.BarkCloud.data.cloud.OutgoingFolderShareRaw
import com.barkfluff.BarkCloud.data.cloud.OutgoingRecipient
import com.barkfluff.BarkCloud.data.cloud.OutgoingShareGroup
import com.barkfluff.BarkCloud.data.cloud.OutgoingShareRaw
import com.barkfluff.BarkCloud.data.cloud.SharedRepository
import com.barkfluff.BarkCloud.data.users.UserRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class MyOutgoingSharesUiState(
    val isLoading: Boolean = true,
    val groups: List<OutgoingShareGroup> = emptyList(),
    val folderGroups: List<OutgoingFolderShareGroup> = emptyList(),
    val users: Map<Long, User> = emptyMap(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val isRefreshing: Boolean = false,
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && groups.isEmpty() && folderGroups.isEmpty()
}

/**
 * Таб «Я поделился»: файловые гранты — cursor-paginated ([SharedRepository.listMyOutgoingSharesAll]),
 * папочные — best-effort без курсора. Группируются по файлу/папке (несколько получателей —
 * одна карточка). Резолв имён получателей — батчем через [UserRepository.listByIds].
 */
class MyOutgoingSharesViewModel(
    private val repo: SharedRepository,
    private val users: UserRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(MyOutgoingSharesUiState())
    val state: StateFlow<MyOutgoingSharesUiState> = _state.asStateFlow()

    private var raw: List<OutgoingShareRaw> = emptyList()
    private var folderRaw: List<OutgoingFolderShareRaw> = emptyList()
    private var cursorSharedAt: Long? = null
    private var cursorGrantId: String = ""

    fun loadIfNeeded() {
        if (raw.isEmpty() && folderRaw.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorSharedAt = null
        cursorGrantId = ""
        viewModelScope.launch {
            _state.update {
                it.copy(
                    isRefreshing = it.groups.isNotEmpty() || it.folderGroups.isNotEmpty(),
                    isLoading = it.groups.isEmpty() && it.folderGroups.isEmpty(),
                )
            }
            try {
                val page = repo.listMyOutgoingSharesAll(limit = 60)
                raw = page.items
                cursorSharedAt = page.nextCursorSharedAtMillis
                cursorGrantId = page.nextCursorGrantId
                folderRaw = runCatching { repo.listMyOutgoingFolderShares() }.getOrDefault(emptyList())
                val ids = (raw.map { it.recipientUserId } + folderRaw.map { it.recipientUserId }).distinct()
                val resolved = runCatching { users.listByIds(ids) }.getOrDefault(emptyList()).associateBy { it.id }
                _state.update {
                    it.copy(
                        isLoading = false,
                        isRefreshing = false,
                        groups = regroup(raw),
                        folderGroups = regroupFolders(folderRaw),
                        users = it.users + resolved,
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, isRefreshing = false, snackbar = e.message) }
            }
        }
    }

    fun loadMore() {
        val s = _state.value
        if (!s.canLoadMore || s.isLoadingMore || s.isLoading) return
        _state.update { it.copy(isLoadingMore = true) }
        viewModelScope.launch {
            try {
                val page = repo.listMyOutgoingSharesAll(
                    limit = 60,
                    cursorSharedAtMillis = cursorSharedAt,
                    cursorGrantId = cursorGrantId,
                )
                raw = raw + page.items
                cursorSharedAt = page.nextCursorSharedAtMillis
                cursorGrantId = page.nextCursorGrantId
                val newIds = page.items.map { it.recipientUserId }.distinct()
                    .filterNot { _state.value.users.containsKey(it) }
                val resolved = runCatching { users.listByIds(newIds) }.getOrDefault(emptyList()).associateBy { it.id }
                _state.update {
                    it.copy(
                        isLoadingMore = false,
                        groups = regroup(raw),
                        users = it.users + resolved,
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun revoke(grantId: String) {
        val removed = raw.firstOrNull { it.grantId == grantId } ?: return
        raw = raw.filterNot { it.grantId == grantId }
        _state.update { it.copy(groups = regroup(raw)) }
        viewModelScope.launch {
            try {
                repo.revokeUserShare(grantId)
            } catch (e: Exception) {
                raw = raw + removed
                _state.update { it.copy(groups = regroup(raw), snackbar = e.message) }
            }
        }
    }

    fun revokeFolder(grantId: String) {
        val removed = folderRaw.firstOrNull { it.grantId == grantId } ?: return
        folderRaw = folderRaw.filterNot { it.grantId == grantId }
        _state.update { it.copy(folderGroups = regroupFolders(folderRaw)) }
        viewModelScope.launch {
            try {
                repo.revokeFolderUserShare(grantId)
            } catch (e: Exception) {
                folderRaw = folderRaw + removed
                _state.update { it.copy(folderGroups = regroupFolders(folderRaw), snackbar = e.message) }
            }
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    private fun regroup(items: List<OutgoingShareRaw>): List<OutgoingShareGroup> =
        items.groupBy { it.asset.id }
            .map { (_, entries) ->
                OutgoingShareGroup(
                    file = entries.first().asset,
                    recipients = entries.map { OutgoingRecipient(it.grantId, it.recipientUserId, it.sharedAtMillis) },
                )
            }
            .sortedByDescending { g -> g.recipients.maxOf { it.sharedAtMillis } }

    private fun regroupFolders(items: List<OutgoingFolderShareRaw>): List<OutgoingFolderShareGroup> =
        items.groupBy { it.directoryId }
            .map { (dirId, entries) ->
                OutgoingFolderShareGroup(
                    directoryId = dirId,
                    name = entries.first().name,
                    recipients = entries.map { OutgoingRecipient(it.grantId, it.recipientUserId, it.sharedAtMillis) },
                )
            }
            .sortedByDescending { g -> g.recipients.maxOf { it.sharedAtMillis } }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return MyOutgoingSharesViewModel(app.sharedRepository, app.userRepository) as T
            }
        }
    }
}
