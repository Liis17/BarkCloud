package com.barkfluff.BarkCloud.data.users

import android.content.Context
import android.net.Uri
import androidx.lifecycle.viewModelScope
import barkcloud.files.FilesApiGrpcKt
import barkcloud.files.FilesApiOuterClass.CreateUploadSessionRequest
import barkcloud.files.FilesApiOuterClass.UploadFileType
import barkcloud.files.FilesApiOuterClass.UploadSessionResponse
import barkcloud.files.FilesApiOuterClass.UploadSessionStatus
import barkcloud.users.UsersApiGrpcKt
import barkcloud.users.UsersApiOuterClass.SetProfilePictureRequest
import barkcloud.users.UsersApiOuterClass.SetProfilePictureResponse
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.ui.settings.EditProfileViewModel
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import io.mockk.verify
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.test.resetMain
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.util.concurrent.TimeUnit

@OptIn(ExperimentalCoroutinesApi::class)
class UserRepositoryAvatarTest {

    @Test
    fun `avatar control RPC sends USER_AVATAR in create request`() = runTest {
        val grpc = mockk<GrpcManager>()
        val files = mockk<FilesApiGrpcKt.FilesApiCoroutineStub>()
        val request = slot<CreateUploadSessionRequest>()
        val response = UploadSessionResponse.newBuilder()
            .setSessionId("session-id")
            .setFileId("file-id")
            .setStatus(UploadSessionStatus.UPLOAD_SESSION_STATUS_UPLOADING)
            .setFileSize(3)
            .setPartSize(16 * 1024 * 1024L)
            .setUploadToken("upload-token")
            .build()
        coEvery { grpc.validAccessToken() } returns "access-token"
        every { grpc.filesStub("access-token") } returns files
        every { files.withDeadlineAfter(any(), any()) } returns files
        coEvery { files.createUploadSession(capture(request), any()) } returns response
        val service = FileTransferService(mockk(), grpc, mockk()) { 0L }

        val created = service.GrpcAvatarUploadApi().createUploadSession(
            "idempotency", "photo.png", 3, "image/png", "sha256", 15_000,
        )

        assertEquals(UploadFileType.USER_AVATAR, request.captured.fileType)
        assertTrue(request.captured.hasFileType())
        assertEquals("session-id", created.sessionId)
    }

    @Test
    fun `profile mutation follows completed avatar upload and uses fixed token stub`() = runTest {
        val uri = mockk<Uri>()
        val grpc = mockk<GrpcManager>()
        val transfer = mockk<FileTransferService>()
        val users = mockk<UsersApiGrpcKt.UsersApiCoroutineStub>()
        val order = mutableListOf<String>()
        val request = slot<SetProfilePictureRequest>()
        coEvery { transfer.uploadAvatar(uri) } coAnswers { order += "ready-upload"; "ready-file-id" }
        coEvery { grpc.validAccessToken() } coAnswers { order += "token"; "access-token" }
        every { grpc.usersStub("access-token") } answers { order += "fixed-stub"; users }
        every { users.withDeadlineAfter(any(), any()) } answers { order += "deadline"; users }
        coEvery { users.setProfilePicture(capture(request), any()) } coAnswers {
            order += "profile-rpc"
            SetProfilePictureResponse.getDefaultInstance()
        }

        UserRepository(grpc, transfer) { 0L }.setAvatar(uri)

        assertEquals(listOf("ready-upload", "token", "fixed-stub", "deadline", "profile-rpc"), order)
        assertEquals("ready-file-id", request.captured.fileId)
        verify(exactly = 0) { grpc.usersStub() }
    }

    @Test
    fun `cancellation after READY before profile RPC skips user and cloud calls`() = runTest {
        val uri = mockk<Uri>()
        val grpc = mockk<GrpcManager>()
        val transfer = mockk<FileTransferService>()
        val users = mockk<UsersApiGrpcKt.UsersApiCoroutineStub>()
        val order = mutableListOf<String>()
        coEvery { transfer.uploadAvatar(uri) } coAnswers { order += "ready"; "ready-file-id" }
        coEvery { grpc.validAccessToken() } coAnswers {
            order += "token"
            currentCoroutineContext()[Job]!!.cancel(CancellationException("cancel before profile RPC"))
            "access-token"
        }
        every { grpc.usersStub("access-token") } returns users

        val update = async { UserRepository(grpc, transfer) { 0L }.setAvatar(uri) }
        runCurrent()
        val error = runCatching { update.await() }.exceptionOrNull()

        assertTrue(update.isCancelled)
        assertTrue(error is CancellationException)
        assertEquals(listOf("ready", "token"), order)
        verify(exactly = 0) { grpc.usersStub("access-token") }
        verify(exactly = 0) { grpc.cloudStub() }
        coVerify(exactly = 0) { users.setProfilePicture(any(), any()) }
    }

    @Test
    fun `profile token resolution is bounded to fifteen seconds before RPC`() = runTest {
        val uri = mockk<Uri>()
        val grpc = mockk<GrpcManager>()
        val transfer = mockk<FileTransferService>()
        coEvery { transfer.uploadAvatar(uri) } returns "ready-file-id"
        coEvery { grpc.validAccessToken() } coAnswers { awaitCancellation() }

        val error = runCatching { UserRepository(grpc, transfer) { 0L }.setAvatar(uri) }.exceptionOrNull()

        assertTrue(error is IOException)
        assertEquals(15_000L, testScheduler.currentTime)
        verify(exactly = 0) { grpc.usersStub(any<String>()) }
    }

    @Test
    fun `files token resolution is bounded to supplied RPC timeout`() = runTest {
        val grpc = mockk<GrpcManager>()
        val service = FileTransferService(mockk(), grpc, mockk()) { 0L }
        coEvery { grpc.validAccessToken() } coAnswers { awaitCancellation() }

        val error = runCatching {
            service.GrpcAvatarUploadApi().createUploadSession(
                "key", "photo.png", 3, "image/png", "hash", 15_000,
            )
        }.exceptionOrNull()

        assertTrue(error is IOException)
        assertEquals(15_000L, testScheduler.currentTime)
        verify(exactly = 0) { grpc.filesStub(any<String>()) }
    }

    @Test
    fun `cleanup uses last token without refresh and returns after five second bound`() = runTest {
        val grpc = mockk<GrpcManager>()
        val files = mockk<FilesApiGrpcKt.FilesApiCoroutineStub>()
        every { grpc.filesStub("expired-access-token") } returns files
        every { files.withDeadlineAfter(5_000, TimeUnit.MILLISECONDS) } returns files
        coEvery { files.cancelUploadSession(any(), any()) } coAnswers { awaitCancellation() }
        val service = FileTransferService(mockk(), grpc, mockk())

        service.GrpcAvatarUploadApi().cancelUploadSession("session-id", "expired-access-token", 5_000)

        assertEquals(5_000L, testScheduler.currentTime)
        verify { grpc.filesStub("expired-access-token") }
        coVerify { files.cancelUploadSession(match { it.sessionId == "session-id" }, any()) }
        coVerify(exactly = 0) { grpc.validAccessToken() }
    }

    @Test
    fun `profile RPC failure keeps previous avatar and view model reports error`() = runTest {
        Dispatchers.setMain(kotlinx.coroutines.test.StandardTestDispatcher(testScheduler))
        try {
            val uri = mockk<Uri>()
            val users = mockk<UserRepository>()
            val oldUser = User.newBuilder().setProfilePicture("old-avatar-url").build()
            coEvery { users.getUser() } returns oldUser
            coEvery { users.setAvatar(uri) } throws IOException("profile RPC failed")
            val viewModel = EditProfileViewModel(users, mockk())

            viewModel.load()
            runCurrent()
            viewModel.setAvatar(uri)
            runCurrent()

            assertEquals("old-avatar-url", viewModel.state.value.avatarUrl)
            assertTrue(viewModel.state.value.hasAvatar)
            assertEquals("profile RPC failed", viewModel.state.value.snackbar)
            assertFalse(viewModel.state.value.isUpdatingAvatar)
        } finally {
            Dispatchers.resetMain()
        }
    }

    @Test
    fun `overlapping remove is ignored and cancellation resets indicator without snackbar`() = runTest {
        Dispatchers.setMain(kotlinx.coroutines.test.StandardTestDispatcher(testScheduler))
        try {
            val uri = mockk<Uri>()
            val users = mockk<UserRepository>(relaxed = true)
            val entered = CompletableDeferred<Unit>()
            coEvery { users.setAvatar(uri) } coAnswers { entered.complete(Unit); awaitCancellation() }
            val viewModel = EditProfileViewModel(users, mockk())

            viewModel.setAvatar(uri)
            runCurrent()
            assertTrue(entered.isCompleted)
            assertTrue(viewModel.state.value.isUpdatingAvatar)
            viewModel.removeAvatar()
            runCurrent()
            coVerify(exactly = 0) { users.removeAvatar() }

            viewModel.viewModelScope.coroutineContext[Job]?.cancel()
            runCurrent()

            assertFalse(viewModel.state.value.isUpdatingAvatar)
            assertNull(viewModel.state.value.snackbar)
        } finally {
            Dispatchers.resetMain()
        }
    }
    @Test
    fun `avatar refresh preserves unsaved profile fields and releases prepared file`() = runTest {
        Dispatchers.setMain(kotlinx.coroutines.test.StandardTestDispatcher(testScheduler))
        try {
            val uri = mockk<Uri>()
            val users = mockk<UserRepository>(relaxed = true)
            coEvery { users.getUser() } returns User.newBuilder().setFirstName("Original").setProfilePicture("old").build()
            val vm = EditProfileViewModel(users, mockk())
            vm.load()
            runCurrent()
            vm.onFirstNameChange("Unsaved")
            vm.onBioChange("Unsaved bio")
            coEvery { users.getUser() } returns User.newBuilder().setFirstName("Original").setProfilePicture("new").build()
            var released = 0
            vm.setAvatar(uri) { released++ }
            runCurrent()
            assertEquals("Unsaved", vm.state.value.firstName)
            assertEquals("Unsaved bio", vm.state.value.bio)
            assertEquals("new", vm.state.value.avatarUrl)
            assertEquals(1, released)
            assertFalse(vm.state.value.isUpdatingAvatar)
        } finally {
            Dispatchers.resetMain()
        }
    }

}
