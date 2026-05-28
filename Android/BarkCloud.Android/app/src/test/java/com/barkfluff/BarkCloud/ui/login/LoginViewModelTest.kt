package com.barkfluff.BarkCloud.ui.login

import app.cash.turbine.test
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.AuthResult
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class LoginViewModelTest {

    @get:Rule
    val mainRule = MainDispatcherRule()

    private val authRepository = mockk<AuthRepository>()

    private fun createSut() = LoginViewModel(authRepository)

    @Test
    fun `onLoginChange updates login and clears credentials error`() {
        val sut = createSut()

        sut.onLoginChange("john")

        assertEquals("john", sut.state.value.login)
        assertNull(sut.state.value.credentialsError)
    }

    @Test
    fun `onPasswordVisibilityToggle flips flag`() {
        val sut = createSut()
        assertFalse(sut.state.value.passwordVisible)

        sut.onPasswordVisibilityToggle()

        assertTrue(sut.state.value.passwordVisible)
    }

    @Test
    fun `onOtpChange filters non-digits and truncates to length`() {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.Success
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.onOtpChange("12a3b45c6789")

        assertEquals("123456", sut.state.value.otp)
    }

    @Test
    fun `submit does nothing when login is blank`() = runTest {
        val sut = createSut()
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        coVerify(exactly = 0) { authRepository.auth(any(), any(), any()) }
    }

    @Test
    fun `submit success emits NavigateToMain and clears loading`() = runTest {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.Success
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.events.test {
            sut.submit()
            assertEquals(LoginViewModel.LoginEvent.NavigateToMain, awaitItem())
        }
        assertFalse(sut.state.value.isLoading)
    }

    @Test
    fun `submit OtpRequired sets otpRequired and resets otp`() = runTest {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.OtpRequired
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        assertTrue(sut.state.value.otpRequired)
        assertEquals("", sut.state.value.otp)
        assertFalse(sut.state.value.isLoading)
    }

    @Test
    fun `submit InvalidCredentials sets credentialsError`() = runTest {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.InvalidCredentials
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        assertEquals("Неверный логин или пароль", sut.state.value.credentialsError)
    }

    @Test
    fun `submit OtherError sets snackbar message`() = runTest {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.OtherError("timeout")
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        assertEquals("timeout", sut.state.value.snackbarMessage)
    }

    @Test
    fun `submit OtherError with blank message falls back to NETWORK_ERROR`() = runTest {
        coEvery { authRepository.auth(any(), any(), any()) } returns AuthResult.OtherError("")
        val sut = createSut()
        sut.onLoginChange("john")
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        assertEquals("Не удалось связаться с сервером", sut.state.value.snackbarMessage)
    }

    @Test
    fun `submit trims login before sending`() = runTest {
        val captured = slot<String>()
        coEvery { authRepository.auth(capture(captured), any(), any()) } returns AuthResult.Success
        val sut = createSut()
        sut.onLoginChange("  john  ")
        sut.onPasswordChange("pwd")

        sut.submit()
        advanceUntilIdle()

        assertEquals("john", captured.captured)
    }
}
