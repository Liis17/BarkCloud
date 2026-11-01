package com.barkfluff.BarkCloud.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ViewerVideoSeekAndFitTest {
    @Test
    fun liveTicksDoNotOverwriteTheSliderWhileDragging() {
        var seek = VideoSeekState(livePositionMs = 10_000L)
        seek = seek.dragTo(40_000L)

        seek = seek.withLivePosition(10_250L)
        seek = seek.withLivePosition(10_500L)

        assertEquals(40_000L, seek.displayPositionMs)
    }

    @Test
    fun finishingADragCommitsOnlyTheLastUserTargetOnce() {
        var seek = VideoSeekState(livePositionMs = 10_000L)
        seek = seek.dragTo(20_000L).dragTo(55_000L)
        seek = seek.withLivePosition(10_300L)

        assertEquals(55_000L, seek.dragTarget())
        seek = seek.finishDrag()

        assertNull(seek.dragTarget())
        assertEquals(55_000L, seek.displayPositionMs)
        // После отпускания живые тики снова обновляют позицию.
        assertEquals(56_000L, seek.withLivePosition(56_000L).displayPositionMs)
    }

    @Test
    fun pausedSeekKeepsTheReleasedTargetOnScreenWithoutTicks() {
        // Пауза: тиков нет. Перемотка на 40 с из 10 с должна остаться на 40 с после отпускания.
        val seek = VideoSeekState(livePositionMs = 10_000L)
            .dragTo(40_000L)
            .finishDrag()

        assertEquals(40_000L, seek.displayPositionMs)
        assertEquals(40_000L, seek.livePositionMs)
        assertNull(seek.dragTarget())
    }

    @Test
    fun finishingWithoutDragDoesNotMoveThePosition() {
        val seek = VideoSeekState(livePositionMs = 10_000L).finishDrag()

        assertEquals(10_000L, seek.displayPositionMs)
    }

    @Test
    fun noDragMeansNoSeekTarget() {
        val seek = VideoSeekState(livePositionMs = 5_000L).withLivePosition(6_000L)

        assertNull(seek.dragTarget())
        assertEquals(6_000L, seek.displayPositionMs)
    }

    @Test
    fun wideVideoFitsTheContainerWidth() {
        val (w, h) = fitVideoSize(400f, 800f, 1920, 1080, pixelRatio = 1f, rotationDegrees = 0)

        assertEquals(400f, w, 0.01f)
        assertEquals(225f, h, 0.01f)
    }

    @Test
    fun tallVideoFitsTheContainerHeight() {
        val (w, h) = fitVideoSize(400f, 400f, 1080, 1920, pixelRatio = 1f, rotationDegrees = 0)

        assertEquals(225f, w, 0.01f)
        assertEquals(400f, h, 0.01f)
    }

    @Test
    fun pixelAspectRatioChangesTheDisplayedWidth() {
        // Ширина 720 при пиксельном соотношении 16:15 даёт 768 px по ширине, то есть 4:3.
        val (w, h) = fitVideoSize(768f, 768f, 720, 576, pixelRatio = 16f / 15f, rotationDegrees = 0)

        assertEquals(768f, w, 0.1f)
        assertEquals(576f, h, 0.1f)
    }

    @Test
    fun rotatedVideoSwapsDisplayDimensions() {
        val (w, h) = fitVideoSize(400f, 800f, 1920, 1080, pixelRatio = 1f, rotationDegrees = 90)

        assertEquals(400f, w, 0.01f)
        assertEquals(711.11f, h, 0.05f)
    }

    @Test
    fun unknownSizeFallsBackToTheContainer() {
        val (w, h) = fitVideoSize(400f, 800f, 0, 0, pixelRatio = 1f, rotationDegrees = 0)

        assertEquals(400f, w, 0f)
        assertEquals(800f, h, 0f)
    }
}
