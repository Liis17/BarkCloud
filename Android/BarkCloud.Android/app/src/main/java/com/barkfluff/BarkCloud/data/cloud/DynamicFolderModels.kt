package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.DfCombinator
import barkcloud.files.FilesApiOuterClass.DfField
import barkcloud.files.FilesApiOuterClass.DfOperator
import barkcloud.files.FilesApiOuterClass.DfRule
import barkcloud.files.FilesApiOuterClass.DfViewMode
import barkcloud.files.FilesApiOuterClass.DynamicFolderInfo

data class DynamicFolderRule(
    val field: DfField,
    val operator: DfOperator,
    val value: String,
) {
    fun toProto(): DfRule = DfRule.newBuilder()
        .setField(field)
        .setOperator(operator)
        .setValue(value)
        .build()

    companion object {
        fun from(rule: DfRule): DynamicFolderRule =
            DynamicFolderRule(rule.field, rule.operator, rule.value)
    }
}

data class DynamicFolderCard(
    val id: String,
    val name: String,
    val isSystem: Boolean,
    val combinator: DfCombinator,
    val rules: List<DynamicFolderRule>,
    val iconKey: String,
    val coverColor: String,
    val coverPreviewUrl: String?,
    val itemsCount: Int,
    val viewMode: DfViewMode,
) {
    companion object {
        fun from(info: DynamicFolderInfo): DynamicFolderCard = DynamicFolderCard(
            id = info.id,
            name = info.name,
            isSystem = info.isSystem,
            combinator = info.combinator,
            rules = info.rulesList.map(DynamicFolderRule::from),
            iconKey = info.iconKey,
            coverColor = info.coverColor,
            coverPreviewUrl = info.coverPreviewUrl.ifEmpty { null },
            itemsCount = info.itemsCount,
            viewMode = info.viewMode,
        )
    }
}

data class DynamicFolderItemsPage(
    val items: List<MediaAsset>,
    val nextCursorCreatedAtMillis: Long?,
    val nextCursorFileId: String,
) {
    val hasMore: Boolean get() = nextCursorCreatedAtMillis != null
}
