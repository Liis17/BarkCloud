package com.barkfluff.BarkCloud.ui.server

import com.barkfluff.BarkCloud.grpc.*
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ServerViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val config = ServerConfig("https://example.com")
    @Test fun `probes run in parallel and save only after all succeed`() = runTest {
        val gates = mutableMapOf<Int, CompletableDeferred<Result<Unit>>>()
        val saved = mutableListOf<ServerConfig>()
        val vm = ServerViewModel(config, config, ServerConnectionProbe { _, port, _ ->
            gates.getOrPut(port) { CompletableDeferred() }.await()
        }, saved::add)
        vm.connect(); vm.connect(); runCurrent()
        assertEquals(3, gates.size)
        gates[8000]!!.complete(Result.success(Unit)); gates[8001]!!.complete(Result.success(Unit)); runCurrent()
        assertTrue(saved.isEmpty())
        gates[8005]!!.complete(Result.success(Unit)); advanceUntilIdle()
        assertEquals(listOf(config), saved)
        assertEquals(List(3) { ServiceStatus.SUCCESS }, vm.state.value.status)
    }
    @Test fun `partial failure does not mutate settings`() = runTest {
        val saved = mutableListOf<ServerConfig>()
        val vm = ServerViewModel(config, config, ServerConnectionProbe { _, port, _ ->
            if (port == 8001) Result.failure(Exception()) else Result.success(Unit)
        }, saved::add)
        vm.connect(); advanceUntilIdle()
        assertTrue(saved.isEmpty()); assertNotNull(vm.state.value.error)
        assertEquals(ServiceStatus.FAILED, vm.state.value.status[1])
    }
    @Test fun `editing cancels stale probes and resets trust`() = runTest {
        val gate = CompletableDeferred<Result<Unit>>()
        val saved = mutableListOf<ServerConfig>()
        val vm = ServerViewModel(config.copy(allowSelfSigned = true), config, ServerConnectionProbe { _, _, _ -> gate.await() }, saved::add)
        vm.connect(); runCurrent(); vm.hostChanged("new.example.com"); gate.complete(Result.success(Unit)); advanceUntilIdle()
        assertTrue(saved.isEmpty()); assertFalse(vm.state.value.allowSelfSigned); assertFalse(vm.state.value.isLoading)
        assertEquals(List(3) { ServiceStatus.IDLE }, vm.state.value.status)
    }
    @Test fun `invalid ports never start requests`() = runTest {
        var calls = 0
        val vm = ServerViewModel(config, config, ServerConnectionProbe { _, _, _ -> calls++; Result.success(Unit) }, {})
        vm.portChanged(0, "0"); vm.connect(); advanceUntilIdle()
        assertEquals(0, calls); assertNotNull(vm.state.value.error)
    }
}
