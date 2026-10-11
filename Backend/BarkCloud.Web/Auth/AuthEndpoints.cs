using System.Text.Json;

using BarkCloud.GrpcServer;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using Microsoft.AspNetCore.Antiforgery;

namespace BarkCloud.Web.Auth;

public static class AuthEndpoints
{
    private const string RegistrationRestartMessage = "Регистрация уже была создана. Войдите с выбранными логином и паролем.";
    private const string ExpiredFlowMessage = "Этот шаг истёк или недоступен. Начните заново.";
    private const string RegistrationCodeMessage = "Если письма нет, проверьте папку «Спам» или запросите код повторно.";
    private const int DefaultResendCooldownSeconds = 60;

    public sealed record WebAuthnLoginCompleteBody(string? ChallengeId, JsonElement Assertion, bool Remember);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var antiforgery = app.Services.GetRequiredService<IAntiforgery>();
        var group = app.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            SetNoStore(http.Response);

            if (HttpMethods.IsPost(http.Request.Method))
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(http);
                }
                catch (AntiforgeryValidationException)
                {
                    return WantsJson(http)
                        ? Results.Json(new { message = "Форма устарела. Обновите страницу и попробуйте ещё раз." },
                            statusCode: StatusCodes.Status400BadRequest)
                        : Results.BadRequest("Форма устарела. Обновите страницу и попробуйте ещё раз.");
                }
            }

            return await next(context);
        });

        group.MapGet("/login", GetLoginAsync);
        group.MapGet("/logout", LogoutAsync);
        group.MapPost("/login", PostLoginAsync);
        group.MapPost("/login/resend", ResendLoginCodeAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/login/webauthn/begin", BeginWebAuthnAsync);
        group.MapPost("/login/webauthn/complete", CompleteWebAuthnAsync);

        group.MapGet("/register", GetRegistrationAsync);
        group.MapPost("/register", BeginRegistrationAsync);
        group.MapPost("/register/confirm", ConfirmRegistrationAsync);
        group.MapPost("/register/resend", ResendRegistrationCodeAsync);

        group.MapGet("/forgot", GetPasswordResetAsync);
        group.MapPost("/forgot", BeginPasswordResetAsync);
        group.MapPost("/forgot/confirm", ConfirmPasswordResetAsync);
        group.MapPost("/forgot/resend", ResendPasswordResetCodeAsync);
    }

    private static async Task<IResult> GetLoginAsync(HttpContext http, AuthGateway auth,
        FeatureConfigurationGateway features, IAntiforgery antiforgery, AuthPageService pages,
        IConfiguration configuration, AuthFlowStore flows)
    {
        var deviceId = auth.GetOrCreateDeviceId(http);
        if (await auth.AuthenticateAsync(http) is not null)
            return Redirect(http, "/photos");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        var flowId = http.Request.Query["flow"].ToString();
        if (!string.IsNullOrEmpty(flowId))
        {
            using var lease = await flows.AcquireAsync(flowId, deviceId, AuthFlowKind.Login, http.RequestAborted);
            if (lease is null || lease.Flow.Screen != "login-otp")
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "login", formError: ExpiredFlowMessage));

            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "login-otp", lease.Flow));
        }

        return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "login", values: new AuthPageValues(Remember: true)));
    }

    private static async Task<IResult> PostLoginAsync(HttpContext http, AuthGateway auth,
        AuthFlowStore flows, FeatureConfigurationGateway features, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration)
    {
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var flowId = form["flow_id"].ToString();
        if (!string.IsNullOrEmpty(flowId))
        {
            using var lease = await flows.AcquireAsync(flowId, deviceId, AuthFlowKind.Login, http.RequestAborted);
            if (lease is null || lease.Flow.Screen != "login-otp")
                return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                    ExpiredFlowMessage);

            var flow = lease.Flow;
            if (!IsSixDigitCode(form["otp"].ToString()))
            {
                flow.FormError = "Введите шестизначный код.";
                flow.FieldErrors.Clear();
                flow.FieldErrors["otp"] = flow.FormError;
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flow, clearOtp: true));
            }

            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await auth.LoginAsync(http, flow.Login!, flow.Password!, form["otp"].ToString(),
                flow.Remember, cancellationToken: cancellation.Token);
            if (result.Outcome == LoginOutcome.Success)
            {
                flows.Complete(flow);
                return Redirect(http, "/photos");
            }

            if (result.Outcome is LoginOutcome.WrongOtp or LoginOutcome.NeedsOtp)
            {
                flow.FormError = result.Outcome == LoginOutcome.WrongOtp
                    ? result.Message
                    : "Код недействителен или истёк. Запросите новый код.";
                flow.FieldErrors.Clear();
                flow.FieldErrors["otp"] = flow.FormError ?? "Код неверен.";
                if (result.Outcome == LoginOutcome.NeedsOtp)
                    UpdateLoginOtpMetadata(flow, result);
                else if (result.RetryAfterSeconds is { } wrongOtpRetry)
                    flow.RetryAt = DateTimeOffset.UtcNow.AddSeconds(wrongOtpRetry);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flow, clearOtp: true));
            }

            if (result.Outcome is LoginOutcome.TooManyAttempts or LoginOutcome.Error)
            {
                flow.FormError = result.Message ?? "Не удалось проверить код. Попробуйте позже.";
                flow.FieldErrors.Clear();
                if (result.RetryAfterSeconds is { } retryAfter)
                    flow.RetryAt = DateTimeOffset.UtcNow.AddSeconds(retryAfter);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flow));
            }

            flows.Complete(flow);
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                result.Message ?? "Не удалось войти. Попробуйте позже.", flow.Login, flow.Remember);
        }

        var login = form["login"].ToString().Trim();
        var password = form["password"].ToString();
        var remember = IsChecked(form, "remember");
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            var errors = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(login)) errors["login"] = "Укажите логин или почту.";
            if (string.IsNullOrEmpty(password)) errors["password"] = "Укажите пароль.";
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                null, login, remember, errors);
        }

        if (!string.IsNullOrEmpty(form["otp"].ToString()))
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                ExpiredFlowMessage, login, remember);

        if (!flows.TryCreate(AuthFlowKind.Login, deviceId, out var newFlow))
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                "Сейчас много попыток входа. Попробуйте ещё раз позже.", login, remember);

        var flowToComplete = newFlow!;
        flowToComplete.Login = login;
        flowToComplete.Password = password;
        flowToComplete.Remember = remember;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                http.RequestAborted, flowToComplete.LifetimeCancellation.Token);
            var result = await auth.LoginAsync(http, login, password, null, remember,
                cancellationToken: cancellation.Token);

            if (result.Outcome == LoginOutcome.Success)
            {
                flows.Complete(flowToComplete);
                return Redirect(http, "/photos");
            }

            if (result.Outcome == LoginOutcome.NeedsOtp)
            {
                flowToComplete.Screen = "login-otp";
                UpdateLoginOtpMetadata(flowToComplete, result);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flowToComplete));
            }

            flows.Complete(flowToComplete);
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                result.Message ?? "Не удалось войти. Попробуйте позже.", login, remember);
        }
        catch (OperationCanceledException)
        {
            flows.Complete(flowToComplete);
            throw;
        }
    }

    private static async Task<IResult> ResendLoginCodeAsync(HttpContext http, AuthGateway auth,
        AuthFlowStore flows, FeatureConfigurationGateway features, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration)
    {
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var lease = await flows.AcquireAsync(form["flow_id"].ToString(), deviceId, AuthFlowKind.Login,
            http.RequestAborted);
        if (lease is null || lease.Flow.Screen != "login-otp" || lease.Flow.OtpMethod != "email"
            || string.IsNullOrEmpty(lease.Flow.Login) || string.IsNullOrEmpty(lease.Flow.Password))
        {
            lease?.Dispose();
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features, ExpiredFlowMessage);
        }

        using (lease)
        {
            var flow = lease.Flow;
            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await auth.LoginAsync(http, flow.Login!, flow.Password!, null, flow.Remember,
                issueSessionOnSuccess: false, cancellationToken: cancellation.Token);
            if (result.Outcome == LoginOutcome.NeedsOtp)
            {
                flow.FormError = result.OtpMethod == "email"
                    ? null
                    : "Для этого аккаунта используйте код приложения-аутентификатора.";
                flow.FieldErrors.Clear();
                UpdateLoginOtpMetadata(flow, result);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flow));
            }

            if (result.Outcome is LoginOutcome.TooManyAttempts or LoginOutcome.Error or LoginOutcome.WrongOtp)
            {
                flow.FormError = result.Message ?? "Не удалось запросить новый код. Попробуйте позже.";
                if (result.RetryAfterSeconds is { } retryAfter)
                    flow.RetryAt = DateTimeOffset.UtcNow.AddSeconds(retryAfter);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    await features.RegistrationEnabledAsync(http.RequestAborted), "login-otp", flow));
            }

            // If Identity no longer requires OTP, discard the retained password instead of completing login here.
            flows.Complete(flow);
            var message = result.Outcome == LoginOutcome.Success
                ? "Условия входа изменились. Начните вход заново."
                : result.Message ?? "Не удалось запросить новый код. Начните вход заново.";
            return await RenderLoginErrorAsync(http, antiforgery, pages, configuration, features,
                message, flow.Login, flow.Remember);
        }
    }

    private static async Task<IResult> BeginWebAuthnAsync(HttpContext http, AuthGateway auth)
    {
        auth.GetOrCreateDeviceId(http);
        var result = await auth.BeginWebAuthnAsync(http, http.RequestAborted);
        return result is null
            ? Results.BadRequest(new { message = "Вход по ключу временно недоступен" })
            : Results.Ok(new { optionsJson = result.Value.OptionsJson, challengeId = result.Value.ChallengeId });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, AuthGateway auth)
    {
        var user = await auth.AuthenticateAsync(http);
        await auth.LogoutAsync(http, user);
        return Redirect(http, "/login");
    }

    private static async Task<IResult> CompleteWebAuthnAsync(HttpContext http, AuthGateway auth,
        WebAuthnLoginCompleteBody body)
    {
        auth.GetOrCreateDeviceId(http);
        if (body.Assertion.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { message = "Не удалось войти по ключу. Попробуйте ещё раз." });

        var result = await auth.CompleteWebAuthnAsync(http, body.ChallengeId ?? string.Empty,
            body.Assertion.GetRawText(), body.Remember, http.RequestAborted);
        return result.Outcome == LoginOutcome.Success
            ? Results.Ok(new { ok = true, redirectTo = "/photos" })
            : Results.BadRequest(new { message = result.Message ?? "Не удалось войти по ключу" });
    }

    private static async Task<IResult> GetRegistrationAsync(HttpContext http, AuthGateway auth,
        AuthFlowStore flows, FeatureConfigurationGateway features, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration)
    {
        var deviceId = auth.GetOrCreateDeviceId(http);
        if (await auth.AuthenticateAsync(http) is not null)
            return Redirect(http, "/photos");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        if (!registrationEnabled)
            return Redirect(http, "/login");

        var flowId = http.Request.Query["flow"].ToString();
        if (!string.IsNullOrEmpty(flowId))
        {
            using var registrationLease = await flows.AcquireAsync(flowId, deviceId,
                AuthFlowKind.Registration, http.RequestAborted);
            if (registrationLease is not null)
            {
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, registrationLease.Flow.Screen, registrationLease.Flow));
            }

            using var pendingLease = await flows.AcquireAsync(flowId, deviceId,
                AuthFlowKind.SessionPending, http.RequestAborted);
            if (pendingLease is not null)
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "session-pending", pendingLease.Flow));

            var marker = http.Request.Query["stage"].ToString() == "account-created";
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "register",
                formError: marker ? null : ExpiredFlowMessage,
                formMessage: marker ? RegistrationRestartMessage : null));
        }

        var lostPendingMarker = http.Request.Query["stage"].ToString() == "account-created";
        return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "register", formMessage: lostPendingMarker ? RegistrationRestartMessage : null));
    }

    private static async Task<IResult> BeginRegistrationAsync(HttpContext http, AuthGateway auth,
        RegistrationGateway registration, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration)
    {
        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        if (!registrationEnabled)
            return Redirect(http, "/login");

        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var flowId = form["flow_id"].ToString();
        AuthFlowLease? lease = null;
        AuthFlow? flow = null;
        if (!string.IsNullOrEmpty(flowId))
        {
            lease = await flows.AcquireAsync(flowId, deviceId, AuthFlowKind.Registration, http.RequestAborted);
            if (lease is null)
                return await RenderRegistrationErrorAsync(http, antiforgery, pages, configuration,
                    features, ExpiredFlowMessage);
            flow = lease.Flow;
            if (flow.Screen != "register")
            {
                using (lease)
                    return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                        registrationEnabled, flow.Screen, flow));
            }
        }
        else if (!flows.TryCreate(AuthFlowKind.Registration, deviceId, out flow))
        {
            return await RenderRegistrationErrorAsync(http, antiforgery, pages, configuration,
                features, "Регистрация временно перегружена. Попробуйте позже.",
                ValuesForRegistration(form));
        }
        else
        {
            lease = await flows.AcquireAsync(flow!.FlowId, deviceId, AuthFlowKind.Registration,
                http.RequestAborted);
            if (lease is null)
                return await RenderRegistrationErrorAsync(http, antiforgery, pages, configuration,
                    features, ExpiredFlowMessage);
            flow = lease.Flow;
        }

        using (lease)
        {
            var firstName = form["first_name"].ToString().Trim();
            var lastName = form["last_name"].ToString().Trim();
            var username = form["username"].ToString().Trim();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            flow!.FirstName = firstName;
            flow.LastName = lastName;
            flow.Username = username;
            flow.Email = email;
            flow.Password = password;
            flow.FieldErrors.Clear();
            flow.FormError = null;
            flow.FormMessage = null;

            using var cancellation = LinkRequestAndFlow(http, lease);
            RegistrationResult result;
            try
            {
                result = await registration.BeginAsync(http, firstName, lastName, username, email,
                    password, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                flows.Complete(flow);
                throw;
            }

            if (result.Outcome == RegistrationOutcome.PendingConfirmation && !string.IsNullOrEmpty(result.CodeId))
            {
                flow.CodeId = result.CodeId;
                flow.Screen = "register-otp";
                flow.CodeExpiresAt = null;
                flow.RetryAt = DateTimeOffset.UtcNow.AddSeconds(DefaultResendCooldownSeconds);
                flow.OtpMailStatus = null;
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "register-otp", flow,
                    formMessage: RegistrationCodeMessage));
            }

            if (result.Outcome == RegistrationOutcome.SessionPending)
                return await CompleteRegistrationToPendingAsync(http, flow, result, flows, registration,
                    antiforgery, pages, configuration, registrationEnabled, lease);

            flow.Password = null;
            flow.Screen = "register";
            if (!string.IsNullOrEmpty(result.Field))
                flow.FieldErrors[result.Field] = result.Message ?? "Проверьте введённые данные.";
            flow.FormError = result.Field is null ? result.Message : null;
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "register", flow));
        }
    }

    private static async Task<IResult> ConfirmRegistrationAsync(HttpContext http, AuthGateway auth,
        RegistrationGateway registration, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration)
    {
        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        if (!registrationEnabled)
            return Redirect(http, "/login");

        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var flowId = form["flow_id"].ToString();
        var lease = await flows.AcquireAsync(flowId, deviceId, AuthFlowKind.Registration, http.RequestAborted);
        if (lease is null)
        {
            using var pendingLease = await flows.AcquireAsync(flowId, deviceId,
                AuthFlowKind.SessionPending, http.RequestAborted);
            if (pendingLease is not null)
            {
                // Only the dedicated session-pending form (flow_id and CSRF token) may retry token creation.
                if (!string.IsNullOrEmpty(form["otp"].ToString()))
                    return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                        await features.RegistrationEnabledAsync(http.RequestAborted), "session-pending",
                        pendingLease.Flow, formError: "Аккаунт уже создан. Повторите проверку сессии."));
                return await RetryPendingSessionAsync(http, pendingLease, flows, registration, antiforgery,
                    pages, configuration, await features.RegistrationEnabledAsync(http.RequestAborted));
            }

            return await RenderRegistrationErrorAsync(http, antiforgery, pages, configuration,
                features, ExpiredFlowMessage);
        }

        using (lease)
        {
            var flow = lease.Flow;
            if (flow.Screen != "register-otp" || string.IsNullOrEmpty(flow.CodeId)
                || string.IsNullOrEmpty(flow.Password))
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, flow.Screen, flow, formError: "Начните регистрацию заново."));

            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await registration.ConfirmAsync(http, flow.CodeId,
                form["otp"].ToString(), flow.Password, cancellation.Token);
            if (result.Outcome == RegistrationOutcome.SessionPending)
                return await CompleteRegistrationToPendingAsync(http, flow, result, flows, registration,
                    antiforgery, pages, configuration, registrationEnabled, lease);

            flow.FieldErrors.Clear();
            flow.FormError = null;
            if (result.Outcome is RegistrationOutcome.CodeInvalid or RegistrationOutcome.CodeExpired)
            {
                flow.FormError = result.Message;
                flow.FieldErrors["otp"] = result.Message ?? "Код недействителен или истёк.";
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "register-otp", flow, clearOtp: true));
            }

            if (result.Field is not null)
                flow.FieldErrors[result.Field] = result.Message ?? "Проверьте введённые данные.";
            else
                flow.FormError = result.Message;
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "register-otp", flow));
        }
    }

    private static async Task<IResult> ResendRegistrationCodeAsync(HttpContext http, AuthGateway auth,
        RegistrationGateway registration, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration)
    {
        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        if (!registrationEnabled)
            return Redirect(http, "/login");
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var lease = await flows.AcquireAsync(form["flow_id"].ToString(), deviceId,
            AuthFlowKind.Registration, http.RequestAborted);
        if (lease is null)
            return await RenderRegistrationErrorAsync(http, antiforgery, pages, configuration,
                features, ExpiredFlowMessage);

        using (lease)
        {
            var flow = lease.Flow;
            if (flow.Screen != "register-otp" || string.IsNullOrEmpty(flow.Password))
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, flow.Screen, flow, formError: "Начните регистрацию заново."));

            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await registration.BeginAsync(http, flow.FirstName ?? "", flow.LastName ?? "",
                flow.Username ?? "", flow.Email ?? "", flow.Password, cancellation.Token);
            if (result.Outcome == RegistrationOutcome.PendingConfirmation && !string.IsNullOrEmpty(result.CodeId))
            {
                flow.CodeId = result.CodeId;
                flow.CodeExpiresAt = null;
                flow.RetryAt = DateTimeOffset.UtcNow.AddSeconds(DefaultResendCooldownSeconds);
                flow.FormError = null;
                flow.FormMessage = "Код запрошен.";
                flow.FieldErrors.Clear();
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "register-otp", flow));
            }

            if (result.Outcome == RegistrationOutcome.SessionPending)
                return await CompleteRegistrationToPendingAsync(http, flow, result, flows, registration,
                    antiforgery, pages, configuration, registrationEnabled, lease);

            flow.FormError = result.Message ?? "Не удалось запросить новый код. Попробуйте позже.";
            flow.FormMessage = null;
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "register-otp", flow));
        }
    }

    private static async Task<IResult> GetPasswordResetAsync(HttpContext http, AuthGateway auth,
        AuthFlowStore flows, FeatureConfigurationGateway features, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration)
    {
        if (!configuration.EmailEnabled())
            return Redirect(http, "/login");

        var deviceId = auth.GetOrCreateDeviceId(http);
        if (await auth.AuthenticateAsync(http) is not null)
            return Redirect(http, "/photos");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        var flowId = http.Request.Query["flow"].ToString();
        if (!string.IsNullOrEmpty(flowId))
        {
            using var lease = await flows.AcquireAsync(flowId, deviceId,
                AuthFlowKind.PasswordReset, http.RequestAborted);
            if (lease is null || lease.Flow.Screen != "reset" && lease.Flow.Screen != "forgot")
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "forgot", formError: ExpiredFlowMessage));
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, lease.Flow.Screen, lease.Flow));
        }

        return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "forgot"));
    }

    private static async Task<IResult> BeginPasswordResetAsync(HttpContext http, AuthGateway auth,
        PasswordResetGateway reset, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration,
        TimeProvider timeProvider)
    {
        if (!configuration.EmailEnabled())
            return Redirect(http, "/login");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var flowId = form["flow_id"].ToString();
        AuthFlowLease? lease = null;
        AuthFlow? flow = null;
        if (!string.IsNullOrEmpty(flowId))
        {
            lease = await flows.AcquireAsync(flowId, deviceId, AuthFlowKind.PasswordReset, http.RequestAborted);
            if (lease is null)
                return await RenderResetErrorAsync(http, antiforgery, pages, configuration,
                    registrationEnabled, ExpiredFlowMessage);
            flow = lease.Flow;
            if (flow.Screen != "forgot")
            {
                using (lease)
                    return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                        registrationEnabled, flow.Screen, flow));
            }
        }
        else if (!flows.TryCreate(AuthFlowKind.PasswordReset, deviceId, out flow))
        {
            return await RenderResetErrorAsync(http, antiforgery, pages, configuration,
                registrationEnabled, "Восстановление временно перегружено. Попробуйте позже.",
                new AuthPageValues(Login: form["login"].ToString()));
        }
        else
        {
            lease = await flows.AcquireAsync(flow!.FlowId, deviceId, AuthFlowKind.PasswordReset,
                http.RequestAborted);
            if (lease is null)
                return await RenderResetErrorAsync(http, antiforgery, pages, configuration,
                    registrationEnabled, ExpiredFlowMessage);
            flow = lease.Flow;
        }

        using (lease)
        {
            var login = form["login"].ToString().Trim();
            flow!.Login = login;
            flow.Screen = "forgot";
            flow.FormError = null;
            flow.FormMessage = null;
            flow.FieldErrors.Clear();
            flow.RevokeOtherSessions = true;
            using var cancellation = lease is null
                ? CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, flow.LifetimeCancellation.Token)
                : LinkRequestAndFlow(http, lease);
            var result = await reset.BeginAsync(http, login, cancellation.Token);
            if (result.Outcome == PasswordResetOutcome.PendingConfirmation)
            {
                flow.ResetId = result.ResetId;
                flow.Screen = "reset";
                flow.CodeExpiresAt = null;
                flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(DefaultResendCooldownSeconds);
                flow.FormMessage = "Если аккаунт существует, мы отправим письмо с кодом для восстановления.";
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "reset", flow));
            }

            flow.Screen = "forgot";
            flow.FormError = result.Message;
            if (result.Outcome == PasswordResetOutcome.ValidationError)
                flow.FieldErrors["login"] = result.Message ?? "Укажите почту или логин.";
            if (result.RetryAfterSeconds is { } retryAfter)
                flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(retryAfter);
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "forgot", flow));
        }
    }

    private static async Task<IResult> ConfirmPasswordResetAsync(HttpContext http, AuthGateway auth,
        PasswordResetGateway reset, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration,
        TimeProvider timeProvider)
    {
        if (!configuration.EmailEnabled())
            return Redirect(http, "/login");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var lease = await flows.AcquireAsync(form["flow_id"].ToString(), deviceId,
            AuthFlowKind.PasswordReset, http.RequestAborted);
        if (lease is null)
            return await RenderResetErrorAsync(http, antiforgery, pages, configuration,
                registrationEnabled, ExpiredFlowMessage);

        using (lease)
        {
            var flow = lease.Flow;
            if (flow.Screen != "reset" || string.IsNullOrEmpty(flow.ResetId))
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, flow.Screen, flow, formError: "Начните восстановление заново."));

            var revokeSessions = IsChecked(form, "revoke_sessions");
            flow.RevokeOtherSessions = revokeSessions;
            flow.FormError = null;
            flow.FormMessage = null;
            flow.FieldErrors.Clear();
            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await reset.ConfirmAsync(http, flow.ResetId, form["otp"].ToString(),
                form["password"].ToString(), revokeSessions, cancellation.Token);
            if (result.Outcome == PasswordResetOutcome.Success)
            {
                flows.Complete(flow);
                return Redirect(http, "/photos");
            }

            flow.FormError = result.Message;
            if (result.Outcome is PasswordResetOutcome.CodeInvalid or PasswordResetOutcome.CodeExpired)
            {
                flow.FieldErrors["otp"] = result.Message ?? "Код недействителен или истёк.";
                flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(DefaultResendCooldownSeconds);
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, "reset", flow, clearOtp: true));
            }

            if (result.Outcome == PasswordResetOutcome.ValidationError)
                flow.FieldErrors["password"] = result.Message ?? "Проверьте новый пароль.";
            if (result.Outcome == PasswordResetOutcome.TooManyAttempts && result.RetryAfterSeconds is { } retryAfter)
                flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(retryAfter);
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "reset", flow));
        }
    }

    private static async Task<IResult> ResendPasswordResetCodeAsync(HttpContext http, AuthGateway auth,
        PasswordResetGateway reset, AuthFlowStore flows, FeatureConfigurationGateway features,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration,
        TimeProvider timeProvider)
    {
        if (!configuration.EmailEnabled())
            return Redirect(http, "/login");

        var registrationEnabled = await features.RegistrationEnabledAsync(http.RequestAborted);
        var deviceId = auth.GetOrCreateDeviceId(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var lease = await flows.AcquireAsync(form["flow_id"].ToString(), deviceId,
            AuthFlowKind.PasswordReset, http.RequestAborted);
        if (lease is null)
            return await RenderResetErrorAsync(http, antiforgery, pages, configuration,
                registrationEnabled, ExpiredFlowMessage);

        using (lease)
        {
            var flow = lease.Flow;
            if (flow.Screen != "reset" || string.IsNullOrEmpty(flow.ResetId) || string.IsNullOrEmpty(flow.Login))
                return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                    registrationEnabled, flow.Screen, flow, formError: "Начните восстановление заново."));

            using var cancellation = LinkRequestAndFlow(http, lease);
            var result = await reset.BeginAsync(http, flow.Login, cancellation.Token);
            if (result.Outcome == PasswordResetOutcome.PendingConfirmation)
            {
                // Reset ids may refer to an older still-valid code. Never infer or extend its expiry here.
                flow.ResetId = result.ResetId ?? flow.ResetId;
                flow.CodeExpiresAt = null;
                flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(DefaultResendCooldownSeconds);
                flow.FormError = null;
                flow.FormMessage = "Если аккаунт существует, мы отправим письмо с кодом для восстановления.";
                flow.FieldErrors.Clear();
            }
            else
            {
                flow.FormError = result.Message ?? "Не удалось запросить новый код. Попробуйте позже.";
                flow.FormMessage = null;
                if (result.RetryAfterSeconds is { } retryAfter)
                    flow.RetryAt = timeProvider.GetUtcNow().AddSeconds(retryAfter);
            }

            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "reset", flow));
        }
    }

    private static async Task<IResult> CompleteRegistrationToPendingAsync(HttpContext http, AuthFlow flow,
        RegistrationResult result, AuthFlowStore flows, RegistrationGateway registration,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration,
        bool registrationEnabled, AuthFlowLease lease)
    {
        flow.Kind = AuthFlowKind.SessionPending;
        flow.Screen = "session-pending";
        flow.Password = null;
        flow.CodeId = null;
        flow.RefreshToken = result.PendingRefreshToken;
        flow.RefreshTokenExpiresAt = result.PendingRefreshExpiresAt;
        flow.OtpMethod = null;
        flow.OtpMailStatus = null;
        flow.CodeExpiresAt = null;
        flow.RetryAt = null;

        if (flow.RefreshToken is not null && flow.RefreshTokenExpiresAt is { } expiresAt)
        {
            using var cancellation = LinkRequestAndFlow(http, lease);
            if (await registration.IssueSessionFromRefreshAsync(http, flow.RefreshToken, expiresAt,
                cancellation.Token))
            {
                flows.Complete(flow);
                return Redirect(http, "/photos");
            }
        }

        flow.FormError = "Аккаунт уже создан, но вход пока не завершён. Попробуйте ещё раз.";
        return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "session-pending", flow));
    }

    private static async Task<IResult> RetryPendingSessionAsync(HttpContext http, AuthFlowLease lease,
        AuthFlowStore flows, RegistrationGateway registration, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration, bool registrationEnabled)
    {
        var flow = lease.Flow;
        if (flow.RefreshToken is null || flow.RefreshTokenExpiresAt is not { } expiresAt)
            return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
                registrationEnabled, "session-pending", flow,
                formError: "Не удалось восстановить срок сессии. Начните вход заново."));

        using var cancellation = LinkRequestAndFlow(http, lease);
        if (await registration.IssueSessionFromRefreshAsync(http, flow.RefreshToken, expiresAt,
            cancellation.Token))
        {
            flows.Complete(flow);
            return Redirect(http, "/photos");
        }

        flow.FormError = "Аккаунт уже создан, но вход пока не завершён. Попробуйте ещё раз.";
        return await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "session-pending", flow));
    }

    private static async Task<IResult> RenderLoginErrorAsync(HttpContext http, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration, FeatureConfigurationGateway features,
        string? message, string? login = null, bool remember = false,
        IReadOnlyDictionary<string, string>? fieldErrors = null)
        => await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            await features.RegistrationEnabledAsync(http.RequestAborted), "login",
            values: new AuthPageValues(Login: login, Remember: remember),
            fieldErrors: fieldErrors, formError: message));

    private static async Task<IResult> RenderRegistrationErrorAsync(HttpContext http,
        IAntiforgery antiforgery, AuthPageService pages, IConfiguration configuration,
        FeatureConfigurationGateway features, string message, AuthPageValues? values = null)
        => await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            await features.RegistrationEnabledAsync(http.RequestAborted), "register",
            values: values, formError: message));

    private static async Task<IResult> RenderResetErrorAsync(HttpContext http, IAntiforgery antiforgery,
        AuthPageService pages, IConfiguration configuration, bool registrationEnabled,
        string message, AuthPageValues? values = null)
        => await RenderStateAsync(http, pages, State(http, antiforgery, configuration,
            registrationEnabled, "forgot", values: values, formError: message));

    private static AuthPageState State(HttpContext http, IAntiforgery antiforgery,
        IConfiguration configuration, bool registrationEnabled, string screen,
        AuthFlow? flow = null, AuthPageValues? values = null,
        IReadOnlyDictionary<string, string>? fieldErrors = null,
        string? formError = null, string? formMessage = null, bool? clearOtp = null)
    {
        var requestToken = antiforgery.GetAndStoreTokens(http).RequestToken ?? string.Empty;
        values ??= ValuesFromFlow(flow);
        var errors = new Dictionary<string, string>(fieldErrors ?? flow?.FieldErrors ?? EmptyErrors,
            StringComparer.Ordinal);
        return new AuthPageState(
            screen,
            configuration["App:PublicHost"] ?? http.Request.Host.Host,
            configuration["App:Version"] ?? AppVersion.Current,
            requestToken,
            flow?.FlowId,
            flow?.ExpiresAt,
            flow?.CodeExpiresAt,
            flow?.RetryAt,
            flow?.OtpMethod,
            flow?.OtpMailStatus,
            values,
            errors,
            formError ?? flow?.FormError,
            formMessage ?? flow?.FormMessage,
            new AuthPageFeatures(registrationEnabled, configuration.EmailEnabled()),
            clearOtp);
    }

    private static AuthPageValues ValuesFromFlow(AuthFlow? flow)
    {
        if (flow is null) return new AuthPageValues();
        return flow.Kind switch
        {
            AuthFlowKind.Login => new AuthPageValues(Login: flow.Login, Remember: flow.Remember),
            AuthFlowKind.Registration => new AuthPageValues(
                Username: flow.Username, Email: flow.Email, FirstName: flow.FirstName, LastName: flow.LastName),
            AuthFlowKind.PasswordReset => new AuthPageValues(
                Login: flow.Login, RevokeSessions: flow.RevokeOtherSessions),
            AuthFlowKind.SessionPending => new AuthPageValues(
                Username: flow.Username, Email: flow.Email, FirstName: flow.FirstName, LastName: flow.LastName),
            _ => new AuthPageValues()
        };
    }

    private static AuthPageValues ValuesForRegistration(IFormCollection form)
        => new(Username: form["username"].ToString(), Email: form["email"].ToString(),
            FirstName: form["first_name"].ToString(), LastName: form["last_name"].ToString());

    private static async Task<IResult> RenderStateAsync(HttpContext http, AuthPageService pages, AuthPageState state)
    {
        if (WantsJson(http))
            return Results.Json(state);
        var html = await pages.RenderAsync(state, http.RequestAborted);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult Redirect(HttpContext http, string url)
        => WantsJson(http) ? Results.Json(new { redirectTo = url }) : Results.Redirect(url);

    private static bool WantsJson(HttpContext http)
        => http.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsChecked(IFormCollection form, string name)
        => form.TryGetValue(name, out var value)
           && (string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.ToString(), "on", StringComparison.OrdinalIgnoreCase));

    private static bool IsSixDigitCode(string? value)
        => value is { Length: 6 } && value.All(character => character is >= '0' and <= '9');

    private static void UpdateLoginOtpMetadata(AuthFlow flow, LoginResult result)
    {
        flow.OtpMethod = result.OtpMethod;
        flow.OtpMailStatus = result.OtpMailStatus;
        flow.CodeExpiresAt = null;
        flow.RetryAt = result.RetryAfterSeconds is { } seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
    }

    private static CancellationTokenSource LinkRequestAndFlow(HttpContext http, AuthFlowLease lease)
        => CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lease.CancellationToken);

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyErrors =
        new Dictionary<string, string>();
}
