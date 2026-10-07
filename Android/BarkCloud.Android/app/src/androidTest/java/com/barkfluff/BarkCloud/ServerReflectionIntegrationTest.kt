package com.barkfluff.BarkCloud

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.grpc.*
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Opt-in network check: pass -e testServerHost https://host to the runner. */
@RunWith(AndroidJUnit4::class)
class ServerReflectionIntegrationTest {
    @Test fun selectedServerExposesAllExpectedApis() = runBlocking {
        val args = InstrumentationRegistry.getArguments()
        val host = args.getString("testServerHost")
        assumeTrue("No external test server supplied", host != null)
        val config = validateServerConfig(host!!, args.getString("identityPort", "8000"),
            args.getString("usersPort", "8001"), args.getString("filesPort", "8005"), args.getString("allowSelfSigned") == "true").config!!
        val probe = ReflectionServerConnectionProbe()
        listOf(config.identityPort to "barkcloud.identity.IdentityApi", config.usersPort to "barkcloud.users.UsersApi", config.filesPort to "barkcloud.files.FilesApi")
            .map { (port, api) -> async { port to probe.check(config, port, api) } }.awaitAll()
            .forEach { (port, result) -> assertTrue("$port: ${result.exceptionOrNull()}", result.isSuccess) }
    }
    @Test fun publicUsernameAndInvalidLoginUseClientMetadata() = runBlocking {
        val args = InstrumentationRegistry.getArguments()
        val host = args.getString("testServerHost")
        assumeTrue("Selected server must match the explicit network test target", host != null && host == ServerSettings.host)
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as BarkCloudApplication
        val username = "android_probe_${System.nanoTime()}"
        assertTrue(app.authFlowRepository.usernameAvailable(username))
        org.junit.Assert.assertEquals(com.barkfluff.BarkCloud.data.AuthResult.InvalidCredentials, app.authRepository.auth(username, "probe"))
    }

}
