package com.barkfluff.BarkCloud.ui.settings

import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Bullet
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Change
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Compare
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Contributor
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Heading
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesBlock.Paragraph
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Code
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Commit
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Link
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Mention
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.PullRef
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Text
import com.barkfluff.BarkCloud.ui.settings.ReleaseNotesInline.Version
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ReleaseNotesParserTest {
    @Test
    fun generatedGitHubNotesBecomeStructuredBlocks() {
        val notes = """
            <!-- Release notes generated using configuration in .github/release.yml -->
            ## What's Changed
            * Добавить виброотклик обновления и выделения на Android by @Liis17 in https://github.com/Liis17/BarkCloud/pull/148
            * Bump deps by @dependabot[bot] in https://github.com/Liis17/BarkCloud/pull/150

            ## New Contributors
            * @octo-cat made their first contribution in https://github.com/Liis17/BarkCloud/pull/149


            **Full Changelog**: https://github.com/Liis17/BarkCloud/compare/v1.0.5-dev.17.1+8f3e2f9...v1.0.5-dev.18.1%2Be33f852
        """.trimIndent()

        val pull148 = PullRef(148, "https://github.com/Liis17/BarkCloud/pull/148")
        assertEquals(
            listOf(
                Heading(2, "What's Changed"),
                Change(listOf(Text("Добавить виброотклик обновления и выделения на Android")), Mention("Liis17"), pull148),
                Change(listOf(Text("Bump deps")), Mention("dependabot[bot]"), PullRef(150, "https://github.com/Liis17/BarkCloud/pull/150")),
                Heading(2, "New Contributors"),
                Contributor(Mention("octo-cat"), PullRef(149, "https://github.com/Liis17/BarkCloud/pull/149")),
                Compare(
                    from = "v1.0.5-dev.17.1+8f3e2f9",
                    to = "v1.0.5-dev.18.1+e33f852",
                    url = "https://github.com/Liis17/BarkCloud/compare/v1.0.5-dev.17.1+8f3e2f9...v1.0.5-dev.18.1%2Be33f852",
                ),
            ),
            parseReleaseNotes(notes),
        )
        assertEquals("https://github.com/Liis17", Mention("Liis17").url)
        assertEquals(null, Mention("dependabot[bot]").url)
    }

    @Test
    fun inlineTokensBecomeBadgesLinksAndStyles() {
        assertEquals(
            listOf(
                Text("Обновить до "),
                Version("v1.2.3-dev.4+abc1234"),
                Text(", см. "),
                PullRef(12, url = null),
                Text(" и "),
                Commit("e33f852", url = null),
                Text(". "),
                Text("Важно", bold = true),
                Text(": "),
                Code("gradlew"),
                Text(" "),
                Link("доки", "https://example.com/a"),
                Text(", "),
                Link("https://example.com/b", "https://example.com/b"),
                Text("."),
            ),
            parseInlines(
                "Обновить до v1.2.3-dev.4+abc1234, см. #12 и e33f852ab. **Важно**: `gradlew` [доки](https://example.com/a), https://example.com/b.",
            ),
        )
    }

    @Test
    fun githubUrlsAreRecognizedInsideText() {
        assertEquals(
            listOf(
                Text("Фикс "),
                Commit("e33f852", "https://github.com/Liis17/BarkCloud/commit/e33f852abcdef"),
                Text(" из "),
                PullRef(7, "https://github.com/Liis17/BarkCloud/issues/7"),
            ),
            parseInlines("Фикс https://github.com/Liis17/BarkCloud/commit/e33f852abcdef из https://github.com/Liis17/BarkCloud/issues/7"),
        )
    }

    @Test
    fun ordinaryWordsAndNumbersStayText() {
        val text = "Версия 2 вышла, deadbeef и 1234567 — не коммиты, mail@example.com не автор, 1.2.3.4 не semver, C# тоже."
        assertEquals(listOf(Text(text)), parseInlines(text))
    }

    @Test
    fun versionDoesNotSwallowSentencePunctuation() {
        assertEquals(
            listOf(Text("Вышла "), Version("1.0.5-dev"), Text(".")),
            parseInlines("Вышла 1.0.5-dev."),
        )
    }

    @Test
    fun paragraphsBulletsAndFallbacks() {
        val blocks = parseReleaseNotes(
            """
            Первая строка
            вторая строка

            - пункт
              - вложенный пункт by @someone in https://example.com/x
            ---
            """.trimIndent(),
        )

        assertEquals(
            listOf(
                Paragraph(listOf(Text("Первая строка вторая строка"))),
                Bullet(listOf(Text("пункт")), depth = 0),
                Bullet(
                    listOf(
                        Text("вложенный пункт by "),
                        Mention("someone"),
                        Text(" in "),
                        Link("https://example.com/x", "https://example.com/x"),
                    ),
                    depth = 1,
                ),
                ReleaseNotesBlock.Divider,
            ),
            blocks,
        )
    }

    @Test
    fun blankNotesProduceNoBlocks() {
        assertTrue(parseReleaseNotes(" \n\n<!-- only comment -->\n").isEmpty())
    }
}
