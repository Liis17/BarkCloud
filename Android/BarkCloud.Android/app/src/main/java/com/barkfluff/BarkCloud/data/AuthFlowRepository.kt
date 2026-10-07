package com.barkfluff.BarkCloud.data

import barkcloud.identity.IdentityApiOuterClass.*
import barkcloud.users.UsersApiOuterClass.CheckExistUsernameRequest
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.grpc.ServerSettings
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.grpc.grpcFailureOrNull
import io.grpc.Metadata
import io.grpc.Status
import io.grpc.StatusRuntimeException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit

/** Domain outcomes shared by signup/reset forms, including server retry deadlines. */
enum class AuthFailureKind { NETWORK, INVALID_CODE, EXPIRED_CODE, USERNAME, EMAIL, REGISTRATION_DISABLED, EMAIL_DISABLED, RATE_LIMIT, OTHER }
class AuthFlowException(
    val kind: AuthFailureKind,
    override val message: String,
    val retryAfterSeconds: Int? = null,
    cause: Throwable? = null,
) : Exception(message, cause)

data class RegistrationChallenge(val codeId: String?, val needsPassword: Boolean)

class AuthFlowRepository(
    private val grpc: GrpcManager,
    private val global: GlobalParam,
    private val pendingStore: PendingRegistrationStorage,
    private val serverKey: () -> String = { ServerSettings.config.key },
    private val now: () -> Long = System::currentTimeMillis,
) {
    fun pendingRegistration(): PendingRegistration? {
        val pending = pendingStore.read() ?: return null
        if (pending.serverKey != serverKey() || (pending.expiresAtMillis != 0L && pending.expiresAtMillis <= now())) {
            pendingStore.clear()
            return null
        }
        return pending
    }

    fun discardPendingRegistration() = pendingStore.clear()

    suspend fun usernameAvailable(username: String): Boolean = request {
        !grpc.publicUsersStub().withDeadlineAfter(15, TimeUnit.SECONDS).checkExistUsername(
            CheckExistUsernameRequest.newBuilder().setUsername(username).build(),
        ).exist
    }

    suspend fun createAccount(details: RegistrationDetails): RegistrationChallenge = request {
        val response = identity().createAccount(CreateAccountRequest.newBuilder()
            .setFirstName(details.firstName).setLastName(details.lastName)
            .setUsername(details.username).setEmail(details.email).build())
        if (response.refreshToken.value.isNotBlank()) {
            savePending(details, response.refreshToken)
            RegistrationChallenge(null, true)
        } else {
            check(response.codeId.isNotBlank()) { "Сервер не вернул идентификатор подтверждения" }
            RegistrationChallenge(response.codeId, false)
        }
    }

    suspend fun confirmAccount(details: RegistrationDetails, codeId: String, code: String) = request {
        val response = identity().confirmAccount(ConfirmAccountRequest.newBuilder()
            .setCodeId(codeId).setCodeValue(code).build())
        savePending(details, response.refreshToken)
    }

    /** Resume verifies a previously attempted password before retrying the consumed operation. */
    suspend fun finishRegistration(password: String, verifyFirst: Boolean = false): Unit = request {
        val pending = pendingRegistration() ?: throw AuthFlowException(AuthFailureKind.OTHER,
            "Регистрация уже подтверждена. Войдите с выбранным паролем или восстановите его по почте.")
        if (verifyFirst && tryExistingPassword(pending.details.email, password)) return@request
        val access = identity().createToken(CreateTokenRequest.newBuilder().setRefreshToken(pending.refreshToken).build()).accessToken
        try {
            grpc.registrationIdentityStub(access.value).withDeadlineAfter(15, TimeUnit.SECONDS)
                .setPassword(SetPasswordRequest.newBuilder().setPassword(password).build())
        } catch (error: Exception) {
            val e = error.grpcFailureOrNull() ?: throw error
            if (e.errorCode() == "A7E3F1B2-9C4D-4E8A-B5F6-2D1A3C7E9F04" ||
                e.status.code in setOf(Status.Code.UNAVAILABLE, Status.Code.DEADLINE_EXCEEDED, Status.Code.UNKNOWN)
            ) {
                if (tryExistingPassword(pending.details.email, password)) return@request
            }
            throw e
        }
        persist(access, Token.newBuilder().setValue(pending.refreshToken)
            .setExpirationDate(com.google.protobuf.Timestamp.newBuilder().setSeconds(pending.expiresAtMillis / 1000L)).build())
        pendingStore.clear()
    }

    suspend fun requestPasswordReset(login: String): String = request {
        val builder = ResetPasswordRequest.newBuilder().setOtpType(OtpTypeId.Email)
        if ('@' in login) builder.email = login else builder.username = login
        identity().resetPassword(builder.build()).resetId
    }

    suspend fun confirmPasswordReset(resetId: String, code: String, password: String, revokeOthers: Boolean) = request {
        val response = identity().confirmResetPassword(ConfirmResetPasswordRequest.newBuilder()
            .setResetId(resetId).setOtpCode(code).setNewPassword(password).setRevokeOtherSessions(revokeOthers).build())
        persist(response.accessToken, response.refreshToken)
        pendingStore.clear()
    }

    private fun identity() = grpc.publicIdentityStub().withDeadlineAfter(15, TimeUnit.SECONDS)

    private fun savePending(details: RegistrationDetails, refresh: Token) {
        check(refresh.value.isNotBlank()) { "Сервер не вернул токен регистрации" }
        pendingStore.save(PendingRegistration(serverKey(), details, refresh.value, refresh.expirationDate.seconds * 1000L))
    }

    private suspend fun tryExistingPassword(email: String, password: String): Boolean {
        try {
            val response = identity().auth(AuthRequest.newBuilder().setEmail(email).setPassword(password).build())
            persist(response.accessToken, response.refreshToken)
            pendingStore.clear()
            return true
        } catch (error: Exception) {
            val e = error.grpcFailureOrNull() ?: throw error
            if (e.errorCode() == "21BFB9B5-C377-45D1-9B15-6B7F3432B397") return false
            if (e.errorCode() == "C1576884-12D8-4722-A7EE-9F9789AD1265") {
                throw AuthFlowException(AuthFailureKind.OTHER, "Пароль установлен. Вернитесь ко входу и подтвердите код двухфакторной аутентификации.")
            }
            throw e
        }
    }

    private fun persist(access: Token, refresh: Token) = global.saveTokens(
        access.value, access.expirationDate.seconds * 1000L,
        refresh.value, refresh.expirationDate.seconds * 1000L,
    )

    private suspend fun <T> request(block: suspend () -> T): T = withContext(Dispatchers.IO) {
        try { block() }
        catch (e: CancellationException) { throw e }
        catch (e: AuthFlowException) { throw e }
        catch (e: Exception) {
            e.grpcFailureOrNull()?.let { throw it.toAuthFlowException() }
            throw AuthFlowException(AuthFailureKind.OTHER, "Не удалось завершить операцию. Попробуйте ещё раз.", cause = e)
        }
    }
}

fun StatusRuntimeException.toAuthFlowException(): AuthFlowException {
    val retry = trailers?.get(Metadata.Key.of("x-retry-after-seconds", Metadata.ASCII_STRING_MARSHALLER))?.toIntOrNull()?.coerceAtLeast(1)
    val (kind, text) = when (errorCode()) {
        "4396D597-D605-4040-AF0F-D9168F0CA034", "803B632C-4457-4B05-9435-9C3DD0F41E00" -> AuthFailureKind.INVALID_CODE to "Неверный код. Проверьте цифры и попробуйте снова."
        "7AABF347-1210-4B14-A93B-2BA8574D74E7", "56D9BB63-DA40-40DE-9C56-7487A1A437D0", "9F3D1B82-8E55-4C71-BD2A-3D7FAC2E6AE1", "5B9A8269-617E-4D4C-9696-A554C59E3A86", "BE708516-BF40-44F9-A6D1-A7F30AB02BED" -> AuthFailureKind.EXPIRED_CODE to "Код истёк или уже использован. Запросите новый. Если аккаунт подтверждён, перейдите ко входу."
        "DB157CD8-98A3-4A35-9857-33821813D422", "A3F1B2C4-7D8E-4F5A-9B6C-1E2D3F4A5B6C" -> AuthFailureKind.USERNAME to "Это имя пользователя уже занято. Выберите другое."
        "7599F3F1-C2EC-4D05-BF38-A1A60D40BA4E" -> AuthFailureKind.EMAIL to "Эта почта уже используется. Войдите или восстановите пароль."
        "C46C2E13-9838-4935-A88F-D6E0F62F4D23" -> AuthFailureKind.REGISTRATION_DISABLED to "Администратор отключил регистрацию на этом сервере."
        "A1F0C3E2-5B47-4E8A-9C21-7D6F0B2E9A14" -> AuthFailureKind.EMAIL_DISABLED to "На этом сервере отключена почта. Для восстановления обратитесь к администратору."
        "8F2B6D41-5A93-4C7E-B0D8-1E4A7C9F3B26", "3C8E5A17-6D42-4B90-A1F3-7E2B9D0C4A58" -> AuthFailureKind.RATE_LIMIT to "Слишком много попыток. Попробуйте позже."
        "A7E3F1B2-9C4D-4E8A-B5F6-2D1A3C7E9F04" -> AuthFailureKind.OTHER to "Пароль уже установлен. Войдите с ним или восстановите по почте."
        "7E6A31C5-3C4D-412E-87BC-0A387617A5D3" -> AuthFailureKind.OTHER to "Срок регистрации истёк. Войдите или восстановите пароль."
        "730737E2-64C9-492B-BE0C-459191C13F76" -> AuthFailureKind.OTHER to "Новый пароль должен отличаться от прежнего."
        else -> if (status.code == Status.Code.RESOURCE_EXHAUSTED) AuthFailureKind.RATE_LIMIT to "Слишком много попыток. Попробуйте позже."
        else if (status.code in setOf(Status.Code.UNAVAILABLE, Status.Code.DEADLINE_EXCEEDED))
            AuthFailureKind.NETWORK to "Не удалось связаться с сервером. Проверьте подключение и повторите."
        else AuthFailureKind.OTHER to "Не удалось завершить операцию. Попробуйте ещё раз."
    }
    return AuthFlowException(kind, text, retry ?: if (kind == AuthFailureKind.RATE_LIMIT) 60 else null, this)
}

fun newPasswordError(password: String): String? = when {
    password.codePointCount(0, password.length) < 8 -> "Минимум 8 символов"
    password.encodeToByteArray().size > 72 -> "Пароль должен занимать не больше 72 байт UTF-8"
    else -> null
}

fun verificationDigits(value: String): String = value.filter { it in '0'..'9' }.take(6)
