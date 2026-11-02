package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.InlineTextContent
import androidx.compose.foundation.text.appendInlineContent
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material.icons.automirrored.outlined.OpenInNew
import androidx.compose.material.icons.outlined.Commit
import androidx.compose.material.icons.outlined.MergeType
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.Placeholder
import androidx.compose.ui.text.PlaceholderVerticalAlign
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.barkfluff.BarkCloud.R
import kotlin.math.ceil

internal enum class UpdateBadgeKind { VERSION, AUTHOR, PULL, COMMIT, NEUTRAL }

/** Заметки релиза в виде MD3-блоков: заголовки, список изменений с бейджами, ссылка на сравнение. */
@Composable
internal fun ReleaseNotesContent(blocks: List<ReleaseNotesBlock>, modifier: Modifier = Modifier) {
    val firstContribution = stringResource(R.string.android_updates_notes_first_contribution)
    Column(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        blocks.forEachIndexed { index, block ->
            when (block) {
                is ReleaseNotesBlock.Heading -> Text(
                    text = localizedHeading(block.text),
                    style = if (block.level <= 2) MaterialTheme.typography.titleSmall else MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.padding(top = if (index == 0) 0.dp else 8.dp),
                )
                is ReleaseNotesBlock.Paragraph -> RichText(block.inlines)
                is ReleaseNotesBlock.Bullet -> BulletRow(depth = block.depth) { RichText(block.inlines) }
                is ReleaseNotesBlock.Change -> BulletRow(depth = 0) {
                    RichText(block.title + listOf(ReleaseNotesInline.Text(" "), block.author, ReleaseNotesInline.Text(" "), block.pull))
                }
                is ReleaseNotesBlock.Contributor -> BulletRow(depth = 0) {
                    RichText(listOf(block.author, ReleaseNotesInline.Text(" $firstContribution "), block.pull))
                }
                is ReleaseNotesBlock.Compare -> CompareCard(block, Modifier.padding(top = 4.dp))
                ReleaseNotesBlock.Divider -> HorizontalDivider()
            }
        }
    }
}

@Composable
private fun localizedHeading(text: String): String = when (text.trim().lowercase()) {
    "what's changed", "what’s changed" -> stringResource(R.string.android_updates_notes_whats_changed)
    "new contributors" -> stringResource(R.string.android_updates_notes_new_contributors)
    else -> text
}

@Composable
private fun BulletRow(depth: Int, content: @Composable () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(start = (depth * 16).dp)) {
        Box(
            Modifier
                .padding(top = 8.dp, end = 12.dp)
                .size(6.dp)
                .background(
                    if (depth == 0) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
                    CircleShape,
                ),
        )
        Box(Modifier.weight(1f)) { content() }
    }
}

@Composable
private fun CompareCard(block: ReleaseNotesBlock.Compare, modifier: Modifier = Modifier) {
    val uriHandler = LocalUriHandler.current
    val clickable = block.url.isTrustedGitHubUrl()
    OutlinedCard(
        modifier = modifier
            .fillMaxWidth()
            .clip(MaterialTheme.shapes.medium)
            .clickable(enabled = clickable, role = Role.Button) { uriHandler.openUri(block.url) },
        shape = MaterialTheme.shapes.medium,
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(R.string.android_updates_notes_full_changelog),
                    style = MaterialTheme.typography.labelLarge,
                    modifier = Modifier.weight(1f),
                )
                if (clickable) {
                    Icon(
                        Icons.AutoMirrored.Outlined.OpenInNew,
                        contentDescription = null,
                        modifier = Modifier.size(18.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            VersionTransition(from = block.from, to = block.to)
        }
    }
}

/** Пара бейджей «было → стало»; переносится на следующую строку на узких экранах. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun VersionTransition(from: String, to: String, modifier: Modifier = Modifier) {
    FlowRow(
        modifier = modifier,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
        itemVerticalAlignment = Alignment.CenterVertically,
    ) {
        UpdateBadge(from, UpdateBadgeKind.NEUTRAL)
        Icon(
            Icons.AutoMirrored.Outlined.ArrowForward,
            contentDescription = null,
            modifier = Modifier.size(16.dp),
            tint = LocalContentColor.current,
        )
        UpdateBadge(to, UpdateBadgeKind.VERSION)
    }
}

/** Самостоятельный бейдж по содержимому (вне текста). */
@Composable
internal fun UpdateBadge(
    label: String,
    kind: UpdateBadgeKind,
    modifier: Modifier = Modifier,
    url: String? = null,
) {
    BadgeSurface(label, kind, url, modifier.heightIn(min = BADGE_HEIGHT))
}

@Composable
private fun BadgeSurface(label: String, kind: UpdateBadgeKind, url: String?, modifier: Modifier) {
    val uriHandler = LocalUriHandler.current
    val (container, content) = kind.colors(MaterialTheme.colorScheme)
    val clickableUrl = url?.takeIf(String::isTrustedGitHubUrl)
    Row(
        modifier = modifier
            .clip(BadgeShape)
            .background(container)
            .then(
                if (clickableUrl != null) {
                    Modifier.clickable(role = Role.Button) { uriHandler.openUri(clickableUrl) }
                } else {
                    Modifier
                },
            )
            .padding(horizontal = BADGE_HORIZONTAL_PADDING, vertical = BADGE_VERTICAL_PADDING),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(BADGE_ICON_GAP),
    ) {
        kind.icon()?.let { icon ->
            Icon(icon, contentDescription = null, tint = content, modifier = Modifier.size(BADGE_ICON_SIZE))
        }
        Text(
            text = label,
            style = kind.textStyle(MaterialTheme.typography.labelMedium),
            color = content,
            maxLines = 1,
            softWrap = false,
            overflow = TextOverflow.Clip,
        )
    }
}

/**
 * Текст с бейджами внутри строки. Бейджи — `InlineTextContent`: ширина плейсхолдера
 * измеряется заранее, поэтому бейдж не переносится и не обрезается; высота строки
 * увеличивается, чтобы соседние строки не наезжали на бейджи.
 */
@Composable
internal fun RichText(
    inlines: List<ReleaseNotesInline>,
    modifier: Modifier = Modifier,
    style: TextStyle = MaterialTheme.typography.bodyMedium,
) {
    val colors = MaterialTheme.colorScheme
    val labelStyle = MaterialTheme.typography.labelMedium
    val measurer = rememberTextMeasurer()
    val density = LocalDensity.current

    val content = remember(inlines, colors, labelStyle, density) {
        val badges = mutableListOf<BadgeModel>()
        val text = buildAnnotatedString {
            inlines.forEach { inline ->
                when (inline) {
                    is ReleaseNotesInline.Text -> if (inline.bold) {
                        withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(inline.text) }
                    } else {
                        append(inline.text)
                    }
                    is ReleaseNotesInline.Code -> withStyle(
                        SpanStyle(fontFamily = FontFamily.Monospace, background = colors.surfaceContainerHighest),
                    ) { append(inline.text) }
                    is ReleaseNotesInline.Link -> if (inline.url.startsWith("https://")) {
                        withLink(
                            LinkAnnotation.Url(
                                inline.url,
                                TextLinkStyles(SpanStyle(color = colors.primary, textDecoration = TextDecoration.Underline)),
                            ),
                        ) { append(inline.label) }
                    } else {
                        append(inline.label)
                    }
                    else -> inline.badge()?.let { badge ->
                        val id = "badge-${badges.size}"
                        badges += badge
                        appendInlineContent(id, badge.label)
                    }
                }
            }
        }
        val minBadgeHeightPx = with(density) { BADGE_HEIGHT.toPx() }
        var lineHeightPx = 0f
        val inlineContent = badges.mapIndexed { index, badge ->
            val measured = measurer.measure(
                AnnotatedString(badge.label),
                style = badge.kind.textStyle(labelStyle),
                maxLines = 1,
                softWrap = false,
            ).size
            val widthPx = with(density) {
                measured.width + 2 * BADGE_HORIZONTAL_PADDING.toPx() +
                    (if (badge.kind.hasIcon) (BADGE_ICON_SIZE + BADGE_ICON_GAP).toPx() else 0f)
            }
            val heightPx = with(density) { maxOf(minBadgeHeightPx, measured.height + 2 * BADGE_VERTICAL_PADDING.toPx()) }
            lineHeightPx = maxOf(lineHeightPx, heightPx + with(density) { INLINE_BADGE_LINE_GAP.toPx() })
            "badge-$index" to InlineTextContent(
                Placeholder(
                    width = with(density) { ceil(widthPx + 1f).toSp() },
                    height = with(density) { heightPx.toSp() },
                    placeholderVerticalAlign = PlaceholderVerticalAlign.TextCenter,
                ),
            ) {
                BadgeSurface(badge.label, badge.kind, badge.url, Modifier.fillMaxSize())
            }
        }.toMap()
        Triple(text, inlineContent, with(density) { lineHeightPx.toSp() })
    }
    val (text, inlineContent, badgeLineHeight) = content
    Text(
        text = text,
        inlineContent = inlineContent,
        style = if (inlineContent.isEmpty() || style.lineHeight.value >= badgeLineHeight.value) {
            style
        } else {
            style.copy(lineHeight = badgeLineHeight)
        },
        modifier = modifier,
    )
}

private data class BadgeModel(val label: String, val kind: UpdateBadgeKind, val url: String?)

private fun ReleaseNotesInline.badge(): BadgeModel? = when (this) {
    is ReleaseNotesInline.Mention -> BadgeModel("@$user", UpdateBadgeKind.AUTHOR, url)
    is ReleaseNotesInline.PullRef -> BadgeModel("#$number", UpdateBadgeKind.PULL, url)
    is ReleaseNotesInline.Version -> BadgeModel(name, UpdateBadgeKind.VERSION, null)
    is ReleaseNotesInline.Commit -> BadgeModel(sha, UpdateBadgeKind.COMMIT, url)
    else -> null
}

private fun UpdateBadgeKind.colors(scheme: ColorScheme): Pair<Color, Color> = when (this) {
    UpdateBadgeKind.VERSION -> scheme.primaryContainer to scheme.onPrimaryContainer
    UpdateBadgeKind.AUTHOR -> scheme.secondaryContainer to scheme.onSecondaryContainer
    UpdateBadgeKind.PULL -> scheme.tertiaryContainer to scheme.onTertiaryContainer
    UpdateBadgeKind.COMMIT, UpdateBadgeKind.NEUTRAL -> scheme.surfaceContainerHighest to scheme.onSurfaceVariant
}

private fun UpdateBadgeKind.icon(): ImageVector? = when (this) {
    UpdateBadgeKind.AUTHOR -> Icons.Outlined.Person
    UpdateBadgeKind.PULL -> Icons.Outlined.MergeType
    UpdateBadgeKind.COMMIT -> Icons.Outlined.Commit
    UpdateBadgeKind.VERSION, UpdateBadgeKind.NEUTRAL -> null
}

private val UpdateBadgeKind.hasIcon: Boolean get() = icon() != null

private fun UpdateBadgeKind.textStyle(base: TextStyle): TextStyle = when (this) {
    UpdateBadgeKind.VERSION, UpdateBadgeKind.COMMIT, UpdateBadgeKind.NEUTRAL ->
        base.copy(fontFamily = FontFamily.Monospace, letterSpacing = 0.sp)
    UpdateBadgeKind.AUTHOR, UpdateBadgeKind.PULL -> base
}

/** Бейджи открывают только страницы GitHub: заметки приходят из внешнего источника. */
private fun String.isTrustedGitHubUrl(): Boolean = startsWith("https://github.com/")

private val BadgeShape = RoundedCornerShape(50)
private val BADGE_HEIGHT: Dp = 22.dp
private val BADGE_HORIZONTAL_PADDING: Dp = 8.dp
private val BADGE_ICON_SIZE: Dp = 14.dp
private val BADGE_ICON_GAP: Dp = 4.dp
private val BADGE_VERTICAL_PADDING: Dp = 2.dp
private val INLINE_BADGE_LINE_GAP: Dp = 6.dp
