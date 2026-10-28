package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.FilePreviewInfo
import barkcloud.files.FilesApiOuterClass.UploadFileInfo
import com.barkfluff.BarkCloud.data.cache.CloudImageModel
import com.barkfluff.BarkCloud.data.cache.CloudImageVariant
import org.junit.Assert.assertEquals
import org.junit.Test

class CloudModelsTest {
    @Test
    fun uploadFileInfoMapsPreviewAndJpegViewIdentifiers() {
        val info = UploadFileInfo.newBuilder()
            .setId("original-file")
            .setFileName("photo.jpg")
            .addPreviews(
                FilePreviewInfo.newBuilder()
                    .setPreviewFileId("preview-file")
                    .setTargetWidth(512)
                    .setPreviewUrl("https://cdn/preview")
                    .build(),
            )
            .setJpegViewFileId("jpeg-view-file")
            .setJpegViewUrl("https://cdn/jpeg-view")
            .build()

        val asset = MediaAsset.from(info)

        assertEquals("preview-file", asset.previews.single().previewFileId)
        assertEquals("jpeg-view-file", asset.jpegViewFileId)
        assertEquals("https://cdn/jpeg-view", asset.jpegViewUrl)
        assertEquals(
            CloudImageModel("https://cdn/preview", "original-file", "preview-file"),
            asset.previewModel(512),
        )
        assertEquals(
            CloudImageModel(
                "https://cdn/jpeg-view",
                "original-file",
                "jpeg-view-file",
                CloudImageVariant.JPEG_VIEW,
            ),
            asset.jpegViewModel(),
        )
    }
}
