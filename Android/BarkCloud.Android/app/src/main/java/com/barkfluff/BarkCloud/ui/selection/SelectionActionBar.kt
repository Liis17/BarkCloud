package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalFloatingToolbar
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp

/**
 * Действие панели выбора: иконка с [label] в роли contentDescription.
 * Неактивное действие остаётся видимым, но не нажимается; [loading] заменяет иконку индикатором.
 */
internal data class SelectionAction(
    val icon: ImageVector,
    val label: String,
    val enabled: Boolean = true,
    val loading: Boolean = false,
    val onClick: () -> Unit,
)

/** Плавающая панель действий над выбранными элементами (тот же тулбар, что в просмотрщике). */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
internal fun SelectionActionBar(
    actions: List<SelectionAction>,
    modifier: Modifier = Modifier,
) {
    Box(modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 12.dp), contentAlignment = Alignment.Center) {
        HorizontalFloatingToolbar(expanded = true) {
            actions.forEach { action ->
                IconButton(
                    modifier = Modifier.width(56.dp),
                    onClick = action.onClick,
                    enabled = action.enabled,
                ) {
                    if (action.loading) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(action.icon, contentDescription = action.label)
                    }
                }
            }
        }
    }
}
