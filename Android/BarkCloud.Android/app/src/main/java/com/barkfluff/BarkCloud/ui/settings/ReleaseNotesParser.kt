package com.barkfluff.BarkCloud.ui.settings

/**
 * Разбор заметок релиза GitHub (Markdown, генерируемый `gh release create --generate-notes`)
 * в блоки для экрана обновлений. Поддерживается только используемое подмножество Markdown;
 * версии, авторы, PR и коммиты выделяются в отдельные токены, чтобы показать их бейджами.
 */
sealed interface ReleaseNotesInline {
    data class Text(val text: String, val bold: Boolean = false) : ReleaseNotesInline
    data class Code(val text: String) : ReleaseNotesInline
    data class Link(val label: String, val url: String) : ReleaseNotesInline
    data class Mention(val user: String) : ReleaseNotesInline {
        /** Профиль GitHub; у ботов (`name[bot]`) отдельной страницы пользователя нет. */
        val url: String? get() = if (user.endsWith(BOT_SUFFIX)) null else "$GITHUB_URL/$user"
    }
    data class PullRef(val number: Int, val url: String?) : ReleaseNotesInline
    data class Version(val name: String) : ReleaseNotesInline
    data class Commit(val sha: String, val url: String?) : ReleaseNotesInline
}

sealed interface ReleaseNotesBlock {
    data class Heading(val level: Int, val text: String) : ReleaseNotesBlock
    data class Paragraph(val inlines: List<ReleaseNotesInline>) : ReleaseNotesBlock
    data class Bullet(val inlines: List<ReleaseNotesInline>, val depth: Int) : ReleaseNotesBlock

    /** Строка `* <title> by @<author> in <PR url>` из автоматических заметок GitHub. */
    data class Change(
        val title: List<ReleaseNotesInline>,
        val author: ReleaseNotesInline.Mention,
        val pull: ReleaseNotesInline.PullRef,
    ) : ReleaseNotesBlock

    /** Строка `* @<author> made their first contribution in <PR url>`. */
    data class Contributor(
        val author: ReleaseNotesInline.Mention,
        val pull: ReleaseNotesInline.PullRef,
    ) : ReleaseNotesBlock

    /** Строка `**Full Changelog**: <compare url>`. */
    data class Compare(val from: String, val to: String, val url: String) : ReleaseNotesBlock

    data object Divider : ReleaseNotesBlock
}

fun parseReleaseNotes(text: String): List<ReleaseNotesBlock> {
    val blocks = mutableListOf<ReleaseNotesBlock>()
    val paragraph = mutableListOf<String>()

    fun flushParagraph() {
        if (paragraph.isEmpty()) return
        blocks += ReleaseNotesBlock.Paragraph(parseInlines(paragraph.joinToString(" ")))
        paragraph.clear()
    }

    for (rawLine in text.replace(HTML_COMMENT, "").lines()) {
        val line = rawLine.trimEnd()
        val trimmed = line.trim()
        if (trimmed.isEmpty()) {
            flushParagraph()
            continue
        }
        if (DIVIDER.matches(trimmed)) {
            flushParagraph()
            blocks += ReleaseNotesBlock.Divider
            continue
        }
        val heading = HEADING.matchEntire(trimmed)
        val compare = COMPARE.matchEntire(trimmed)
        val bullet = BULLET.matchEntire(line)
        if (heading == null && compare == null && bullet == null) {
            paragraph += trimmed
            continue
        }
        flushParagraph()
        blocks += when {
            heading != null -> ReleaseNotesBlock.Heading(heading.groupValues[1].length, heading.groupValues[2].trim())
            compare != null -> ReleaseNotesBlock.Compare(
                from = decodeTag(compare.groupValues[2]),
                to = decodeTag(compare.groupValues[3]),
                url = compare.groupValues[1],
            )
            else -> parseBullet(bullet!!.groupValues[2].trim(), depth = bullet.groupValues[1].length / 2)
        }
    }
    flushParagraph()
    return blocks
}

private fun parseBullet(content: String, depth: Int): ReleaseNotesBlock {
    CHANGE.matchEntire(content)?.let { match ->
        val pull = pullRefOf(match.groupValues[3])
        if (pull != null) {
            return ReleaseNotesBlock.Change(
                title = parseInlines(match.groupValues[1].trim()),
                author = ReleaseNotesInline.Mention(match.groupValues[2]),
                pull = pull,
            )
        }
    }
    CONTRIBUTOR.matchEntire(content)?.let { match ->
        val pull = pullRefOf(match.groupValues[2])
        if (pull != null) {
            return ReleaseNotesBlock.Contributor(ReleaseNotesInline.Mention(match.groupValues[1]), pull)
        }
    }
    return ReleaseNotesBlock.Bullet(parseInlines(content), depth)
}

fun parseInlines(text: String): List<ReleaseNotesInline> {
    val result = mutableListOf<ReleaseNotesInline>()
    val plain = StringBuilder()

    fun flushText() {
        if (plain.isEmpty()) return
        result += ReleaseNotesInline.Text(plain.toString())
        plain.clear()
    }

    fun add(token: ReleaseNotesInline) {
        flushText()
        result += token
    }

    var cursor = 0
    for (match in INLINE.findAll(text)) {
        plain.append(text, cursor, match.range.first)
        cursor = match.range.last + 1
        val groups = match.groups
        when {
            groups[1] != null -> add(ReleaseNotesInline.Code(groups[1]!!.value))
            groups[2] != null -> parseInlines(groups[2]!!.value).forEach { inline ->
                add(if (inline is ReleaseNotesInline.Text) inline.copy(bold = true) else inline)
            }
            groups[3] != null -> add(ReleaseNotesInline.Link(groups[3]!!.value, groups[4]!!.value))
            groups[5] != null -> {
                val raw = groups[5]!!.value
                val url = raw.trimEnd(*URL_TRAILING_PUNCTUATION)
                add(classifyUrl(url))
                plain.append(raw, url.length, raw.length)
            }
            groups[6] != null -> add(ReleaseNotesInline.Mention(groups[6]!!.value))
            groups[7] != null -> add(ReleaseNotesInline.PullRef(groups[7]!!.value.toInt(), url = null))
            groups[8] != null -> {
                val raw = groups[8]!!.value
                val version = raw.trimEnd('.', '-', '+')
                add(ReleaseNotesInline.Version(version))
                plain.append(raw, version.length, raw.length)
            }
            groups[9] != null -> {
                val sha = groups[9]!!.value
                if (sha.any(Char::isDigit) && sha.any(Char::isLetter)) {
                    add(ReleaseNotesInline.Commit(sha.take(SHORT_SHA_LENGTH), url = null))
                } else {
                    plain.append(sha)
                }
            }
        }
    }
    plain.append(text, cursor, text.length)
    flushText()
    return result
}

private fun classifyUrl(url: String): ReleaseNotesInline {
    pullRefOf(url)?.let { return it }
    GITHUB_COMMIT.matchEntire(url)?.let { match ->
        return ReleaseNotesInline.Commit(match.groupValues[1].take(SHORT_SHA_LENGTH), url)
    }
    GITHUB_COMPARE.matchEntire(url)?.let { match ->
        return ReleaseNotesInline.Link("${decodeTag(match.groupValues[1])}…${decodeTag(match.groupValues[2])}", url)
    }
    return ReleaseNotesInline.Link(url, url)
}

private fun pullRefOf(url: String): ReleaseNotesInline.PullRef? =
    GITHUB_PULL.matchEntire(url)?.let { match ->
        match.groupValues[1].toIntOrNull()?.let { ReleaseNotesInline.PullRef(it, url) }
    }

/** GitHub может кодировать `+` из метаданных semver в ссылке сравнения. */
private fun decodeTag(tag: String): String = tag.replace("%2B", "+").replace("%2b", "+")

private const val GITHUB_URL = "https://github.com"
private const val BOT_SUFFIX = "[bot]"
private const val SHORT_SHA_LENGTH = 7
private val URL_TRAILING_PUNCTUATION = charArrayOf('.', ',', ';', ':', '!', '?', '\'', '"')

private const val USER = "[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:\\[bot])?"
private const val REPO = "https://github\\.com/[\\w.-]+/[\\w.-]+"

private val HTML_COMMENT = Regex("<!--[\\s\\S]*?-->")
private val DIVIDER = Regex("^(?:-{3,}|\\*{3,}|_{3,})$")
private val HEADING = Regex("^(#{1,6})\\s+(.+?)\\s*#*$")
private val BULLET = Regex("^(\\s*)[*+-]\\s+(.+)$")
private val COMPARE = Regex("^\\*\\*Full Changelog\\*\\*:?\\s*($REPO/compare/(\\S+?)\\.\\.\\.(\\S+))$")
private val CHANGE = Regex("^(.+?)\\s+by\\s+@($USER)\\s+in\\s+(\\S+)$")
private val CONTRIBUTOR = Regex("^@($USER)\\s+made their first contribution in\\s+(\\S+)$")
private val GITHUB_PULL = Regex("^$REPO/(?:pull|issues)/(\\d+)(?:[/#?]\\S*)?$")
private val GITHUB_COMMIT = Regex("^$REPO/commit/([0-9a-fA-F]{7,40})(?:[/#?]\\S*)?$")
private val GITHUB_COMPARE = Regex("^$REPO/compare/(\\S+?)\\.\\.\\.(\\S+)$")

private val INLINE = Regex(
    "`([^`]+)`" + // 1: код
        "|\\*\\*(.+?)\\*\\*" + // 2: жирный
        "|\\[([^\\]]+)]\\((https?://[^)\\s]+)\\)" + // 3, 4: [label](url)
        "|(https?://[^\\s<>()]+)" + // 5: ссылка
        "|(?<![\\w/@.])@($USER)" + // 6: упоминание
        "|(?<![\\w&/#])#(\\d{1,7})(?!\\w)" + // 7: #PR
        "|(?<![\\w.+/-])(v?\\d+\\.\\d+\\.\\d+(?!\\.\\d)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\\+[0-9A-Za-z][0-9A-Za-z.-]*)?)" + // 8: версия
        "|(?<![\\w.+/-])([0-9a-f]{7,40})(?!\\w)", // 9: коммит
)
