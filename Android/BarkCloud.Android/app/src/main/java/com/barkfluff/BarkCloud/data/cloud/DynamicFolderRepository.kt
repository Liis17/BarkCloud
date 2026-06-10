package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.CreateDynamicFolderRequest
import barkcloud.files.FilesApiOuterClass.DeleteDynamicFolderRequest
import barkcloud.files.FilesApiOuterClass.DfCombinator
import barkcloud.files.FilesApiOuterClass.DfViewMode
import barkcloud.files.FilesApiOuterClass.ListDynamicFolderItemsRequest
import barkcloud.files.FilesApiOuterClass.ListDynamicFoldersRequest
import barkcloud.files.FilesApiOuterClass.MediaKind
import barkcloud.files.FilesApiOuterClass.UpdateDynamicFolderRequest
import com.barkfluff.BarkCloud.grpc.GrpcManager

class DynamicFolderRepository(
    private val grpc: GrpcManager,
) {

    suspend fun listFolders(): List<DynamicFolderCard> {
        val resp = grpc.dynamicFolderStub()
            .listDynamicFolders(ListDynamicFoldersRequest.getDefaultInstance())
        return resp.foldersList.map(DynamicFolderCard::from)
    }

    suspend fun listItems(
        folderId: String,
        limit: Int = 60,
        cursorCreatedAtMillis: Long? = null,
        cursorFileId: String = "",
    ): DynamicFolderItemsPage {
        val req = ListDynamicFolderItemsRequest.newBuilder()
            .setFolderId(folderId)
            .setLimit(limit)
            .setCursorFileId(cursorFileId)
        if (cursorCreatedAtMillis != null) req.setCursorCreatedAt(cursorCreatedAtMillis.toTimestamp())
        val resp = grpc.dynamicFolderStub().listDynamicFolderItems(req.build())
        return DynamicFolderItemsPage(
            items = resp.itemsList.map { MediaAsset.from(it.file) },
            nextCursorCreatedAtMillis = if (resp.hasNextCursorCreatedAt()) resp.nextCursorCreatedAt.toEpochMillis() else null,
            nextCursorFileId = resp.nextCursorFileId,
        )
    }

    suspend fun create(
        name: String,
        combinator: DfCombinator,
        rules: List<DynamicFolderRule>,
        viewMode: DfViewMode,
    ): DynamicFolderCard {
        val resp = grpc.dynamicFolderStub().createDynamicFolder(
            CreateDynamicFolderRequest.newBuilder()
                .setName(name)
                .setCombinator(combinator)
                .addAllRules(rules.map { it.toProto() })
                .setViewMode(viewMode)
                .build()
        )
        return DynamicFolderCard.from(resp)
    }

    suspend fun update(
        folderId: String,
        name: String,
        combinator: DfCombinator,
        rules: List<DynamicFolderRule>,
        viewMode: DfViewMode,
    ): DynamicFolderCard {
        val resp = grpc.dynamicFolderStub().updateDynamicFolder(
            UpdateDynamicFolderRequest.newBuilder()
                .setFolderId(folderId)
                .setName(name)
                .setCombinator(combinator)
                .addAllRules(rules.map { it.toProto() })
                .setViewMode(viewMode)
                .build()
        )
        return DynamicFolderCard.from(resp)
    }

    suspend fun delete(folderId: String) {
        grpc.dynamicFolderStub().deleteDynamicFolder(
            DeleteDynamicFolderRequest.newBuilder().setFolderId(folderId).build()
        )
    }
}
