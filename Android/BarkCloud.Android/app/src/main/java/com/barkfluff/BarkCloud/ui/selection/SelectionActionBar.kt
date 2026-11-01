package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.layout.PaddingValues

/** Действие панели выбора; неактивное действие остаётся видимым, но не нажимается. */
internal data class SelectionAction(
    val label: String,
    val enabled: Boolean = true,
    val onClick: () -> Unit,
)

/** Плавающая панель действий над выбранными элементами (стиль панели галереи). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun SelectionActionBar(
    actions: List<SelectionAction>,
    modifier: Modifier = Modifier,
) {
    Box(modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 12.dp), contentAlignment = Alignment.Center) {
        Surface(
            shape = MaterialTheme.shapes.extraLarge,
            color = MaterialTheme.colorScheme.surfaceContainerHigh,
            tonalElevation = 6.dp,
        ) {
            Column(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
                FlowRow(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Start) {
                    actions.forEach { action ->
                        TextButton(
                            onClick = action.onClick,
                            enabled = action.enabled,
                            contentPadding = PaddingValues(horizontal = 4.dp, vertical = 8.dp),
                        ) {
                            Text(action.label)
                        }
                    }
                }
            }
        }
    }
}
