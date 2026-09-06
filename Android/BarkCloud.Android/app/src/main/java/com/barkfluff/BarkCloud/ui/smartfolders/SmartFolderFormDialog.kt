package com.barkfluff.BarkCloud.ui.smartfolders

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import barkcloud.files.FilesApiOuterClass.DfCombinator
import barkcloud.files.FilesApiOuterClass.DfField
import barkcloud.files.FilesApiOuterClass.DfOperator
import barkcloud.files.FilesApiOuterClass.DfViewMode
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderCard
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRule

@Composable
fun SmartFolderFormDialog(
    folder: DynamicFolderCard?,
    onSave: (name: String, combinator: DfCombinator, rules: List<DynamicFolderRule>, viewMode: DfViewMode) -> Unit,
    onDismiss: () -> Unit,
) {
    var name by remember(folder?.id) { mutableStateOf(folder?.name.orEmpty()) }
    var combinator by remember(folder?.id) { mutableStateOf(folder?.combinator ?: DfCombinator.DF_ALL) }
    var viewMode by remember(folder?.id) { mutableStateOf(folder?.viewMode ?: DfViewMode.DF_VIEW_GRID) }
    var rules by remember(folder?.id) {
        mutableStateOf(folder?.rules?.takeIf { it.isNotEmpty() } ?: listOf(defaultRule()))
    }
    val cleanRules = rules.filter { it.value.isNotBlank() }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                if (folder == null) {
                    stringResource(R.string.smart_folder_create_title)
                } else {
                    stringResource(R.string.smart_folder_edit_title)
                }
            )
        },
        text = {
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text(stringResource(R.string.smart_folder_name)) },
                    singleLine = true,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    AssistChip(
                        onClick = {
                            combinator = if (combinator == DfCombinator.DF_ALL) {
                                DfCombinator.DF_ANY
                            } else {
                                DfCombinator.DF_ALL
                            }
                        },
                        label = { Text(combinator.title()) },
                    )
                    AssistChip(
                        onClick = {
                            viewMode = if (viewMode == DfViewMode.DF_VIEW_GRID) {
                                DfViewMode.DF_VIEW_LIST
                            } else {
                                DfViewMode.DF_VIEW_GRID
                            }
                        },
                        label = { Text(viewMode.title()) },
                    )
                }
                rules.forEachIndexed { index, rule ->
                    RuleRow(
                        rule = rule,
                        canRemove = rules.size > 1,
                        onChange = { updated ->
                            rules = rules.toMutableList().also { it[index] = updated }
                        },
                        onRemove = {
                            rules = rules.toMutableList().also { it.removeAt(index) }
                        },
                    )
                }
                TextButton(onClick = { rules = rules + defaultRule() }) {
                    Icon(Icons.Outlined.Add, contentDescription = null)
                    Text(stringResource(R.string.smart_folder_add_rule), modifier = Modifier.padding(start = 6.dp))
                }
                if (name.isBlank() || cleanRules.isEmpty()) {
                    Text(
                        text = stringResource(R.string.smart_folder_validation),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = name.isNotBlank() && cleanRules.isNotEmpty(),
                onClick = {
                    onSave(name, combinator, cleanRules, viewMode)
                    onDismiss()
                },
            ) {
                Text(stringResource(R.string.common_save))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
        },
    )
}

@Composable
private fun RuleRow(
    rule: DynamicFolderRule,
    canRemove: Boolean,
    onChange: (DynamicFolderRule) -> Unit,
    onRemove: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            AssistChip(
                onClick = {
                    val next = nextField(rule.field)
                    onChange(DynamicFolderRule(next, operatorsFor(next).first(), defaultValue(next)))
                },
                label = { Text(rule.field.title()) },
                modifier = Modifier.weight(1f),
            )
            AssistChip(
                onClick = {
                    val ops = operatorsFor(rule.field)
                    val next = ops[(ops.indexOf(rule.operator).coerceAtLeast(0) + 1) % ops.size]
                    onChange(rule.copy(operator = next))
                },
                label = { Text(rule.operator.title()) },
                modifier = Modifier.weight(1f),
            )
            if (canRemove) {
                IconButton(onClick = onRemove) {
                    Icon(Icons.Outlined.Delete, contentDescription = null)
                }
            }
        }
        OutlinedTextField(
            value = rule.value,
            onValueChange = { onChange(rule.copy(value = it)) },
            label = { Text(valueHint(rule.field)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

private val fields = listOf(
    DfField.DF_DATE,
    DfField.DF_TAKEN_AT,
    DfField.DF_SIZE,
    DfField.DF_NAME,
    DfField.DF_MEDIA_KIND,
    DfField.DF_EXTENSION,
    DfField.DF_IMG_WIDTH,
    DfField.DF_IMG_HEIGHT,
    DfField.DF_DEVICE,
    DfField.DF_METADATA_DEVICE,
)

private fun defaultRule() =
    DynamicFolderRule(DfField.DF_NAME, DfOperator.DF_CONTAINS, "")

private fun nextField(current: DfField): DfField =
    fields[(fields.indexOf(current).coerceAtLeast(0) + 1) % fields.size]

private fun operatorsFor(field: DfField): List<DfOperator> = when (field) {
    DfField.DF_DATE,
    DfField.DF_TAKEN_AT -> listOf(DfOperator.DF_WITHIN_LAST_DAYS, DfOperator.DF_BEFORE, DfOperator.DF_AFTER)
    DfField.DF_SIZE,
    DfField.DF_IMG_WIDTH,
    DfField.DF_IMG_HEIGHT -> listOf(DfOperator.DF_GT, DfOperator.DF_LT, DfOperator.DF_EQUALS)
    DfField.DF_NAME,
    DfField.DF_DEVICE,
    DfField.DF_METADATA_DEVICE -> listOf(DfOperator.DF_CONTAINS, DfOperator.DF_STARTS_WITH, DfOperator.DF_ENDS_WITH, DfOperator.DF_EQUALS)
    DfField.DF_EXTENSION -> listOf(DfOperator.DF_ENDS_WITH)
    DfField.DF_MEDIA_KIND -> listOf(DfOperator.DF_EQUALS)
    else -> listOf(DfOperator.DF_EQUALS)
}

private fun defaultValue(field: DfField): String =
    if (field == DfField.DF_MEDIA_KIND) "1" else ""

@Composable
private fun DfCombinator.title(): String =
    stringResource(if (this == DfCombinator.DF_ANY) R.string.smart_folder_match_any else R.string.smart_folder_match_all)

@Composable
private fun DfViewMode.title(): String =
    stringResource(if (this == DfViewMode.DF_VIEW_LIST) R.string.smart_folder_view_list else R.string.smart_folder_view_grid)

@Composable
private fun DfField.title(): String = stringResource(
    when (this) {
        DfField.DF_DATE -> R.string.smart_folder_field_date
        DfField.DF_TAKEN_AT -> R.string.smart_folder_field_taken
        DfField.DF_SIZE -> R.string.smart_folder_field_size
        DfField.DF_NAME -> R.string.smart_folder_field_name
        DfField.DF_MEDIA_KIND -> R.string.smart_folder_field_kind
        DfField.DF_EXTENSION -> R.string.smart_folder_field_ext
        DfField.DF_IMG_WIDTH -> R.string.smart_folder_field_width
        DfField.DF_IMG_HEIGHT -> R.string.smart_folder_field_height
        DfField.DF_DEVICE -> R.string.smart_folder_field_device
        DfField.DF_METADATA_DEVICE -> R.string.smart_folder_field_metadata_device
        else -> R.string.smart_folder_field_name
    }
)

@Composable
private fun DfOperator.title(): String = stringResource(
    when (this) {
        DfOperator.DF_WITHIN_LAST_DAYS -> R.string.smart_folder_op_days
        DfOperator.DF_BEFORE -> R.string.smart_folder_op_before
        DfOperator.DF_AFTER -> R.string.smart_folder_op_after
        DfOperator.DF_GT -> R.string.smart_folder_op_gt
        DfOperator.DF_LT -> R.string.smart_folder_op_lt
        DfOperator.DF_CONTAINS -> R.string.smart_folder_op_contains
        DfOperator.DF_EQUALS -> R.string.smart_folder_op_equals
        DfOperator.DF_ENDS_WITH -> R.string.smart_folder_op_ends
        DfOperator.DF_STARTS_WITH -> R.string.smart_folder_op_starts
        else -> R.string.smart_folder_op_equals
    }
)

@Composable
private fun valueHint(field: DfField): String = stringResource(
    when (field) {
        DfField.DF_DATE,
        DfField.DF_TAKEN_AT -> R.string.smart_folder_value_date_hint
        DfField.DF_SIZE -> R.string.smart_folder_value_size_hint
        DfField.DF_MEDIA_KIND -> R.string.smart_folder_value_kind_hint
        DfField.DF_EXTENSION -> R.string.smart_folder_value_ext_hint
        else -> R.string.smart_folder_value_text_hint
    }
)
