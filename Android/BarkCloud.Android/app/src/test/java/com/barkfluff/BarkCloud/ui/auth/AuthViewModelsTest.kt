package com.barkfluff.BarkCloud.ui.auth

import androidx.lifecycle.SavedStateHandle
import com.barkfluff.BarkCloud.data.*
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.*
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class AuthViewModelsTest {
    @get:Rule val main = MainDispatcherRule()
    private val repository = mockk<AuthFlowRepository> { every { pendingRegistration() } returns null }
    @Test fun `username debounce cancels old input`() = runTest {
        coEvery { repository.usernameAvailable(any()) } returns true
        val vm = RegistrationViewModel(repository)
        vm.usernameChanged("alice"); advanceTimeBy(399)
        coVerify(exactly = 0) { repository.usernameAvailable(any()) }
        vm.usernameChanged("bob"); advanceTimeBy(400); runCurrent()
        coVerify(exactly = 1) { repository.usernameAvailable("bob") }
        coVerify(exactly = 0) { repository.usernameAvailable("alice") }
        assertTrue(vm.state.value.usernameAvailable == true)
    }
    @Test fun `resumed registration asks password and saves no secrets to saved state`() = runTest {
        val pending = PendingRegistration("server", RegistrationDetails("Alice", "", "alice", "a@example.com"), "refresh", 0)
        every { repository.pendingRegistration() } returns pending
        val saved = SavedStateHandle()
        val vm = RegistrationViewModel(repository, saved)
        assertEquals(2, vm.state.value.step); assertTrue(vm.state.value.pendingPassword)
        vm.passwordChanged("secret123"); vm.codeChanged("123456")
        assertFalse(saved.keys().any { it.contains("password", true) || it.contains("code", true) })
        val restored = RegistrationViewModel(repository, saved)
        assertEquals("", restored.state.value.password); assertEquals("", restored.state.value.code)
        coEvery { repository.finishRegistration("secret123", true) } returns Unit
        vm.next(); advanceUntilIdle()
        coVerify { repository.finishRegistration("secret123", true) }
        assertEquals(4, vm.state.value.step); assertEquals("", vm.state.value.password)
    }
    @Test fun `reset code page does not contact server until new password`() = runTest {
        coEvery { repository.requestPasswordReset("alice") } returns "reset"
        coEvery { repository.confirmPasswordReset(any(), any(), any(), any()) } throws AuthFlowException(AuthFailureKind.INVALID_CODE, "bad code")
        val saved = SavedStateHandle()
        val vm = ResetPasswordViewModel(repository, saved) { 1000 }
        vm.loginChanged("alice"); vm.next(); advanceUntilIdle()
        assertEquals(1, vm.state.value.step)
        vm.resend(); advanceUntilIdle()
        coVerify(exactly = 1) { repository.requestPasswordReset(any()) }
        vm.codeChanged("123456"); vm.next()
        assertEquals(2, vm.state.value.step)
        coVerify(exactly = 0) { repository.confirmPasswordReset(any(), any(), any(), any()) }
        vm.passwordChanged("password123"); vm.repeatChanged("password123"); vm.next(); advanceUntilIdle()
        coVerify { repository.confirmPasswordReset("reset", "123456", "password123", true) }
        assertEquals(1, vm.state.value.step)
        assertEquals("password123", vm.state.value.password)
        val restored = ResetPasswordViewModel(repository, saved)
        assertEquals("alice", restored.state.value.login); assertEquals("", restored.state.value.code); assertEquals("", restored.state.value.password)
    }
    @Test fun `server retry deadline prevents repeat reset requests`() = runTest {
        coEvery { repository.requestPasswordReset(any()) } throws AuthFlowException(AuthFailureKind.RATE_LIMIT, "later", 120)
        var time = 1000L
        val vm = ResetPasswordViewModel(repository, now = { time })
        vm.loginChanged("alice"); vm.next(); advanceUntilIdle(); vm.next(); advanceUntilIdle()
        coVerify(exactly = 1) { repository.requestPasswordReset(any()) }
        assertEquals(121000L, vm.state.value.retryAt)
        time = 121001; vm.next(); advanceUntilIdle()
        coVerify(exactly = 2) { repository.requestPasswordReset(any()) }
    }
    private suspend fun kotlinx.coroutines.test.TestScope.readyRegistration(): RegistrationViewModel {
        coEvery { repository.usernameAvailable("alice") } returns true
        val vm = RegistrationViewModel(repository)
        vm.firstNameChanged("Alice"); vm.next(); vm.usernameChanged("alice")
        advanceTimeBy(400); runCurrent(); vm.next()
        vm.emailChanged("a@example.com"); vm.passwordChanged("password123")
        return vm
    }
    @Test fun `mail signup keeps code challenge and installs password after confirmation`() = runTest {
        val vm = readyRegistration()
        coEvery { repository.createAccount(any()) } returns RegistrationChallenge("challenge", false)
        coEvery { repository.confirmAccount(any(), "challenge", "123456") } returns Unit
        coEvery { repository.finishRegistration("password123", false) } returns Unit
        vm.next(); advanceUntilIdle()
        assertEquals(3, vm.state.value.step)
        vm.resend(); advanceUntilIdle()
        coVerify(exactly = 1) { repository.createAccount(any()) }
        vm.codeChanged("123456"); vm.next(); advanceUntilIdle()
        coVerifyOrder {
            repository.createAccount(any())
            repository.confirmAccount(any(), "challenge", "123456")
            repository.finishRegistration("password123", false)
        }
        assertEquals(4, vm.state.value.step); assertEquals("", vm.state.value.password)
    }
    @Test fun `email disabled skips code and finishes signup`() = runTest {
        val vm = readyRegistration()
        coEvery { repository.createAccount(any()) } returns RegistrationChallenge(null, true)
        coEvery { repository.finishRegistration("password123", false) } returns Unit
        vm.next(); advanceUntilIdle()
        assertEquals(4, vm.state.value.step)
        coVerify(exactly = 0) { repository.confirmAccount(any(), any(), any()) }
    }
    @Test fun `final username conflict returns to username page`() = runTest {
        val vm = readyRegistration()
        coEvery { repository.createAccount(any()) } throws AuthFlowException(AuthFailureKind.USERNAME, "taken")
        vm.next(); advanceUntilIdle()
        assertEquals(1, vm.state.value.step); assertFalse(vm.state.value.loading)
        assertEquals(false, vm.state.value.usernameAvailable)
        assertEquals("password123", vm.state.value.password)
    }

}
