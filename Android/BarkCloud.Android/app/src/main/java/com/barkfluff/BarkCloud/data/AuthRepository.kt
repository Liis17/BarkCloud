package com.barkfluff.BarkCloud.data

import barkcloud.identity.IdentityApiOuterClass.AuthRequest
import barkcloud.identity.IdentityApiOuterClass.AuthResponse
import barkcloud.identity.IdentityApiOuterClass.LogoutRequest
import com.barkfluff.BarkCloud.grpc.AuthErrorCodes
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.grpc.grpcFailureOrNull
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

sealed class AuthResult {
    data object Success : AuthResult()
    data object OtpRequired : AuthResult()
    data object InvalidCredentials : AuthResult()
    data class OtherError(val message: String, val retryAfterSeconds: Int? = null) : AuthResult()
}

class AuthRepository(
    private val grpcManager: GrpcManager,
    private val globalParam: GlobalParam,
) {

    suspend fun auth(
        login: String,
        password: String,
        otpCode: String? = null,
    ): AuthResult = withContext(Dispatchers.IO) {
        val request = buildAuthRequest(login, password, otpCode)
        try {
            val response: AuthResponse = grpcManager.identityStub().withDeadlineAfter(15, TimeUnit.SECONDS).auth(request)
            persist(response)
            AuthResult.Success
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            val failure = e.grpcFailureOrNull()
            if (failure == null) AuthResult.OtherError(e.message ?: e::class.java.simpleName)
            else when (failure.errorCode()) {
                AuthErrorCodes.OTP_REQUIRED -> AuthResult.OtpRequired
                AuthErrorCodes.INVALID_CREDENTIALS, "A4DAB334-1067-4838-A782-C4257DC838F7" -> AuthResult.InvalidCredentials
                else -> failure.toAuthFlowException().let { AuthResult.OtherError(it.message, it.retryAfterSeconds) }
            }
        }
    }

    /** Отзыв текущей сессии на сервере (best-effort: ошибки игнорируются). */
    suspend fun logout() = withContext(Dispatchers.IO) {
        runCatching { grpcManager.identityStub().logout(LogoutRequest.getDefaultInstance()) }
        Unit
    }

    private fun buildAuthRequest(
        login: String,
        password: String,
        otpCode: String?,
    ): AuthRequest {
        val builder = AuthRequest.newBuilder()
            .setPassword(password)
        if (login.contains('@')) {
            builder.setEmail(login)
        } else {
            builder.setUsername(login)
        }
        if (!otpCode.isNullOrBlank()) {
            builder.setOtpCode(otpCode)
        }
        return builder.build()
    }

    private fun persist(response: AuthResponse) {
        val access = response.accessToken
        val refresh = response.refreshToken
        globalParam.saveTokens(
            accessToken = access.value,
            accessTokenExpiresAt = access.expirationDate.seconds * 1000L,
            refreshToken = refresh.value,
            refreshTokenExpiresAt = refresh.expirationDate.seconds * 1000L,
        )
    }
}
