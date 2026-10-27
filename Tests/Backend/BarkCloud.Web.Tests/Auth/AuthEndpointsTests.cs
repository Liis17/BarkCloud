using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using BarkCloud.Proto.Configuration;
using BarkCloud.Proto.Identity;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Web.Tests.Auth;

public sealed class AuthEndpointsTests
{
    [Fact]
    public async Task HtmlBootstrap_UsesSafeStateJsonAndTestManifestAssets()
    {
        const string hostileServerName = "</script>\"{{ app.name }}";
        await using var fixture = await Fixture.CreateAsync(serverName: hostileServerName);

        using var response = await fixture.Client.GetAsync("/login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var html = await response.Content.ReadAsStringAsync();
        var stateTag = Regex.Match(html,
            "<script type=\"application/json\" id=\"auth-page-state\">(.*?)</script>", RegexOptions.Singleline);
        stateTag.Success.Should().BeTrue();
        using var state = JsonDocument.Parse(stateTag.Groups[1].Value);
        state.RootElement.GetProperty("serverName").GetString().Should().Be(hostileServerName);
        html.Should().NotContain(hostileServerName);
        html.Should().Contain("{{ app.name }}");
        Regex.Matches(html, "<link rel=\"stylesheet\" href=\"([^\"]+)\" />")
            .Select(match => match.Groups[1].Value)
            .Should().Equal("/assets/shared-nested.css", "/assets/shared-first.css", "/assets/auth-entry.css");
        Regex.Matches(html, "<link rel=\"modulepreload\" href=\"([^\"]+)\" />")
            .Select(match => match.Groups[1].Value)
            .Should().Equal("/assets/shared-nested.js", "/assets/shared-first.js");
        Regex.Match(html, "<script type=\"module\" src=\"([^\"]+)\"></script>")
            .Groups[1].Value.Should().Be("/assets/auth-entry.js");
        html.IndexOf("localStorage.getItem('bark_theme')", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingAntiforgeryTokenRejectsPostBeforeIdentityCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.PostFormAsync("/login", new Dictionary<string, string>
        {
            ["login"] = "demo-user",
            ["password"] = "secret-password"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Identity.Verify(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TwoLoginTabsRestoreIndependentFlowsAndForeignDeviceCannotReadThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired("sent", 60))
            .Throws(OtpRequired("sent", 60));

        using var initial = await fixture.GetStateAsync("/login");
        var csrf = initial.RootElement.GetProperty("csrfToken").GetString()!;
        using var first = await fixture.PostStateAsync("/login", csrf,
            Fields(("login", "first@example.test"), ("password", "first-password"), ("remember", "true")));
        using var second = await fixture.PostStateAsync("/login", csrf,
            Fields(("login", "second@example.test"), ("password", "second-password")));
        var firstId = first.Json.RootElement.GetProperty("flowId").GetString()!;
        var secondId = second.Json.RootElement.GetProperty("flowId").GetString()!;
        firstId.Should().NotBe(secondId);

        using var restoredFirst = await fixture.GetStateAsync($"/login?flow={Uri.EscapeDataString(firstId)}");
        using var restoredSecond = await fixture.GetStateAsync($"/login?flow={Uri.EscapeDataString(secondId)}");
        restoredFirst.RootElement.GetProperty("flowId").GetString().Should().Be(firstId);
        restoredFirst.RootElement.GetProperty("values").GetProperty("login").GetString().Should().Be("first@example.test");
        restoredSecond.RootElement.GetProperty("flowId").GetString().Should().Be(secondId);
        restoredSecond.RootElement.GetProperty("values").GetProperty("login").GetString().Should().Be("second@example.test");

        using var bare = await fixture.GetStateAsync("/login");
        bare.RootElement.GetProperty("screen").GetString().Should().Be("login");
        bare.RootElement.TryGetProperty("flowId", out _).Should().BeFalse();

        using var foreign = await fixture.GetStateWithoutCookiesAsync($"/login?flow={Uri.EscapeDataString(firstId)}");
        foreign.RootElement.GetProperty("screen").GetString().Should().Be("login");
        foreign.RootElement.GetProperty("formError").GetString().Should().Contain("истёк или недоступен");
        foreign.RootElement.TryGetProperty("flowId", out _).Should().BeFalse();
        foreign.RootElement.GetRawText().Should().NotContain(firstId).And.NotContain(secondId);
    }

    [Fact]
    public async Task LostSessionPendingMarkerExplainsAccountWasCreated()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var lostMarker = await fixture.GetStateAsync("/register?flow=missing-flow");
        using var pendingMarker = await fixture.GetStateAsync("/register?flow=missing-flow&stage=account-created");

        lostMarker.RootElement.GetProperty("formError").GetString().Should().Contain("истёк или недоступен");
        pendingMarker.RootElement.GetProperty("formMessage").GetString().Should().Contain("Регистрация уже была создана");
        pendingMarker.RootElement.TryGetProperty("flowId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DisabledRegistrationBlocksEveryActionIncludingSessionPendingRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var refreshExpiresAt = DateTime.UtcNow.AddDays(30);
        fixture.Identity.Setup(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateAccountResponse
            {
                RefreshToken = new Token
                {
                    Value = "private-refresh-token",
                    ExpirationDate = Timestamp.FromDateTime(refreshExpiresAt)
                }
            }));
        fixture.Identity.Setup(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(RpcFailure("temporary-session-failure"));

        using var initial = await fixture.GetStateAsync("/register");
        using var pending = await fixture.PostStateAsync("/register", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("username", "demo-user"), ("email", "demo@example.test"), ("password", "secret-password")));
        pending.Json.RootElement.GetProperty("screen").GetString().Should().Be("session-pending");
        var flowId = pending.Json.RootElement.GetProperty("flowId").GetString()!;
        fixture.SetRegistrationEnabled(false);

        using var getPending = await fixture.GetResponseJsonAsync($"/register?flow={Uri.EscapeDataString(flowId)}");
        using var retryPending = await fixture.PostStateAsync("/register/confirm",
            pending.Json.RootElement.GetProperty("csrfToken").GetString()!, Fields(("flow_id", flowId)));
        using var resendPending = await fixture.PostStateAsync("/register/resend",
            pending.Json.RootElement.GetProperty("csrfToken").GetString()!, Fields(("flow_id", flowId)));

        getPending.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        retryPending.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        resendPending.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        fixture.Identity.Verify(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Identity.Verify(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisabledRegistrationAndResetRejectTheirGetAndPostRoutes()
    {
        await using var registrationFixture = await Fixture.CreateAsync(registrationEnabled: false);
        using var login = await registrationFixture.GetStateAsync("/login");
        using var register = await registrationFixture.GetResponseJsonAsync("/register");
        using var beginRegistration = await registrationFixture.PostStateAsync("/register",
            login.RootElement.GetProperty("csrfToken").GetString()!, Fields(("username", "demo"),
                ("email", "demo@example.test"), ("password", "secret-password")));
        using var confirmRegistration = await registrationFixture.PostStateAsync("/register/confirm",
            login.RootElement.GetProperty("csrfToken").GetString()!, Fields(("flow_id", "foreign-flow"), ("otp", "123456")));
        register.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        beginRegistration.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        confirmRegistration.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/login");
        registrationFixture.Identity.Verify(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

        await using var resetFixture = await Fixture.CreateAsync(emailEnabled: false);
        using var resetLogin = await resetFixture.GetStateAsync("/login");
        using var forgot = await resetFixture.GetResponseJsonAsync("/forgot");
        using var beginReset = await resetFixture.PostStateAsync("/forgot",
            resetLogin.RootElement.GetProperty("csrfToken").GetString()!, Fields(("login", "known@example.test")));
        using var confirmReset = await resetFixture.PostStateAsync("/forgot/confirm",
            resetLogin.RootElement.GetProperty("csrfToken").GetString()!, Fields(("flow_id", "foreign-flow"),
                ("otp", "123456"), ("password", "new-password")));
        using var resendReset = await resetFixture.PostStateAsync("/forgot/resend",
            resetLogin.RootElement.GetProperty("csrfToken").GetString()!, Fields(("flow_id", "foreign-flow")));
        foreach (var response in new[] { forgot.Json.RootElement, beginReset.Json.RootElement,
                     confirmReset.Json.RootElement, resendReset.Json.RootElement })
            response.GetProperty("redirectTo").GetString().Should().Be("/login");
        resetFixture.Identity.Verify(x => x.ResetPasswordAsync(It.IsAny<ResetPasswordRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        resetFixture.Identity.Verify(x => x.ConfirmResetPasswordAsync(It.IsAny<ConfirmResetPasswordRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentRegistrationConfirmAndResendDoNotDuplicateIdentityMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var refreshExpiresAt = DateTime.UtcNow.AddDays(30);
        var confirmStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConfirm = new TaskCompletionSource<ConfirmAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Identity.Setup(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateAccountResponse { CodeId = "private-code-id" }));
        fixture.Identity.Setup(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => DelayedUnary(WaitForConfirmAsync()));
        fixture.Identity.Setup(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(RpcFailure("temporary-session-failure"));

        async Task<ConfirmAccountResponse> WaitForConfirmAsync()
        {
            confirmStarted.TrySetResult();
            return await allowConfirm.Task;
        }

        using var initial = await fixture.GetStateAsync("/register");
        using var form = await fixture.PostStateAsync("/register", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("username", "demo-user"), ("email", "demo@example.test"), ("password", "secret-password")));
        var flowId = form.Json.RootElement.GetProperty("flowId").GetString()!;
        var csrf = form.Json.RootElement.GetProperty("csrfToken").GetString()!;
        var confirmTask = fixture.PostStateAsync("/register/confirm", csrf,
            Fields(("flow_id", flowId), ("otp", "123456")));
        await confirmStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resendTask = fixture.PostStateAsync("/register/resend", csrf, Fields(("flow_id", flowId)));
        await Task.Delay(40);
        fixture.Identity.Verify(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);

        allowConfirm.SetResult(new ConfirmAccountResponse
        {
            RefreshToken = new Token
            {
                Value = "private-refresh-token",
                ExpirationDate = Timestamp.FromDateTime(refreshExpiresAt)
            }
        });
        using var confirmed = await confirmTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var resend = await resendTask.WaitAsync(TimeSpan.FromSeconds(5));

        confirmed.Json.RootElement.GetProperty("screen").GetString().Should().Be("session-pending");
        resend.Json.RootElement.GetProperty("screen").GetString().Should().Be("register");
        fixture.Identity.Verify(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Identity.Verify(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WebAuthnRequiresAntiforgeryAndPassesRememberAndObjectAssertion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var noToken = new HttpRequestMessage(HttpMethod.Post, "/login/webauthn/begin");
        noToken.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using (var rejected = await fixture.Client.SendAsync(noToken))
        {
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            rejected.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        fixture.Identity.Verify(x => x.BeginWebAuthnAssertionAsync(It.IsAny<BeginWebAuthnAssertionRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

        fixture.Identity.Setup(x => x.BeginWebAuthnAssertionAsync(It.IsAny<BeginWebAuthnAssertionRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new BeginWebAuthnAssertionResponse
                { OptionsJson = "{\"challenge\":\"test\"}", ChallengeId = "private-challenge-id" }));
        fixture.Identity.SetupSequence(x => x.CompleteWebAuthnAssertionAsync(It.IsAny<CompleteWebAuthnAssertionRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(SuccessAuthResponse()))
            .Returns(GrpcCallHelpers.AsyncUnary(SuccessAuthResponse()));
        using var initial = await fixture.GetStateAsync("/login");
        var csrf = initial.RootElement.GetProperty("csrfToken").GetString()!;
        using var beginRequest = new HttpRequestMessage(HttpMethod.Post, "/login/webauthn/begin");
        beginRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        beginRequest.Headers.Add("X-CSRF-TOKEN", csrf);
        using var begin = await fixture.Client.SendAsync(beginRequest);
        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        begin.Headers.CacheControl!.NoStore.Should().BeTrue();

        foreach (var remember in new[] { true, false })
        {
            using var completeRequest = new HttpRequestMessage(HttpMethod.Post, "/login/webauthn/complete")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    challengeId = "private-challenge-id",
                    assertion = new { id = "assertion-id", response = new { signature = "signature" } },
                    remember
                }), Encoding.UTF8, "application/json")
            };
            completeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            completeRequest.Headers.Add("X-CSRF-TOKEN", csrf);
            using var complete = await fixture.Client.SendAsync(completeRequest);
            complete.StatusCode.Should().Be(HttpStatusCode.OK);
            using var result = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
            result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            var cookies = complete.Headers.GetValues("Set-Cookie").ToArray();
            cookies.Single(cookie => cookie.StartsWith(AuthGateway.AccessCookie + "=", StringComparison.Ordinal))
                .ToLowerInvariant().Should().NotContain("expires=");
            cookies.Single(cookie => cookie.StartsWith(AuthGateway.RefreshCookie + "=", StringComparison.Ordinal))
                .ToLowerInvariant().Contains("expires=").Should().Be(remember);
        }

        fixture.Identity.Verify(x => x.CompleteWebAuthnAssertionAsync(
            It.Is<CompleteWebAuthnAssertionRequest>(request => request.AssertionJson.Contains("assertion-id")),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LegacyLogoutRouteAlsoDisablesCaching()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = fixture.BaseAddress
        };
        using var response = await client.GetAsync("/logout");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login");
    }

    [Fact]
    public async Task LoginOtpFlowHidesSecretsAndSurvivesOtpAndResendFailures()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired("sent", 60))
            .Throws(RpcFailure(new NotValidOtpCodeException().ErrorCode))
            .Throws(RpcFailure("temporary-service-failure"))
            .Throws(RateLimited(45))
            .Throws(OtpRequired("existing", 38));

        using var initial = await fixture.GetStateAsync("/login");
        using var requested = await fixture.PostStateAsync("/login", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "demo@example.test"), ("password", "secret-password"), ("remember", "true")));
        var flowId = requested.Json.RootElement.GetProperty("flowId").GetString()!;
        requested.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        requested.Json.RootElement.GetProperty("values").GetProperty("login").GetString().Should().Be("demo@example.test");
        requested.Json.RootElement.GetProperty("otpMethod").GetString().Should().Be("email");
        requested.Json.RootElement.TryGetProperty("codeExpiresAt", out _).Should().BeFalse();
        var publicState = requested.Json.RootElement.GetRawText();
        publicState.Should().NotContain("secret-password").And.NotContain("password").And.NotContain("codeId");

        using var confirmWrong = await fixture.PostStateAsync("/login", requested.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456")));
        confirmWrong.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        confirmWrong.Json.RootElement.GetProperty("flowId").GetString().Should().Be(flowId);
        confirmWrong.Json.RootElement.GetProperty("otpMethod").GetString().Should().Be("email");
        confirmWrong.Json.RootElement.GetProperty("formError").GetString().Should().NotBeNullOrEmpty();
        confirmWrong.Json.RootElement.GetProperty("clearOtp").GetBoolean().Should().BeTrue();

        using var confirmError = await fixture.PostStateAsync("/login", confirmWrong.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "654321")));
        confirmError.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        confirmError.Json.RootElement.GetProperty("flowId").GetString().Should().Be(flowId);
        confirmError.Json.RootElement.GetProperty("otpMethod").GetString().Should().Be("email");
        confirmError.Json.RootElement.GetProperty("formError").GetString().Should().NotBeNullOrEmpty();
        confirmError.Json.RootElement.TryGetProperty("clearOtp", out _).Should().BeFalse();

        using var resendError = await fixture.PostStateAsync("/login/resend", confirmError.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId)));
        resendError.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        resendError.Json.RootElement.GetProperty("flowId").GetString().Should().Be(flowId);
        resendError.Json.RootElement.GetProperty("formError").GetString().Should().NotBeNullOrEmpty();

        using var resendExisting = await fixture.PostStateAsync("/login/resend", resendError.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId)));
        resendExisting.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        resendExisting.Json.RootElement.GetProperty("flowId").GetString().Should().Be(flowId);
        resendExisting.Json.RootElement.GetProperty("otpMailStatus").GetString().Should().Be("existing");
        resendExisting.Json.RootElement.TryGetProperty("codeExpiresAt", out _).Should().BeFalse();
        fixture.Identity.Verify(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginResendCompletesLoginWhenOtpIsNoLongerRequired(bool remember)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired("sent", 60))
            .Returns(GrpcCallHelpers.AsyncUnary(SuccessAuthResponse()));

        using var initial = await fixture.GetStateAsync("/login");
        using var requested = await fixture.PostStateAsync("/login", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "demo@example.test"), ("password", "secret-password"), ("remember", remember.ToString())));
        var flowId = requested.Json.RootElement.GetProperty("flowId").GetString()!;

        using var resend = await fixture.PostStateAsync("/login/resend", requested.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId)));

        resend.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/photos");
        fixture.CookieValue(resend.Response, AuthGateway.AccessCookie).Should().Be("private-access-token");
        fixture.CookieValue(resend.Response, AuthGateway.RefreshCookie).Should().Be("private-refresh-token");
        resend.Response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(AuthGateway.AccessCookie + "=", StringComparison.Ordinal))
            .ToLowerInvariant().Should().NotContain("expires=");
        resend.Response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(AuthGateway.RefreshCookie + "=", StringComparison.Ordinal))
            .ToLowerInvariant().Contains("expires=").Should().Be(remember);

        using var completedFlow = await fixture.PostStateAsync("/login", requested.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456")));
        completedFlow.Json.RootElement.GetProperty("screen").GetString().Should().Be("login");
        completedFlow.Json.RootElement.GetProperty("formError").GetString().Should().Contain("истёк или недоступен");

        fixture.Identity.Verify(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Identity.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthenticatorOtpWrongCodeRetainsItsMethod()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired("", 0, "authenticator"))
            .Throws(RpcFailure(new NotValidOtpCodeException().ErrorCode));

        using var initial = await fixture.GetStateAsync("/login");
        using var requested = await fixture.PostStateAsync("/login", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "demo-user"), ("password", "secret-password")));
        using var wrong = await fixture.PostStateAsync("/login", requested.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", requested.Json.RootElement.GetProperty("flowId").GetString()!), ("otp", "123456")));

        wrong.Json.RootElement.GetProperty("screen").GetString().Should().Be("login-otp");
        wrong.Json.RootElement.GetProperty("otpMethod").GetString().Should().Be("authenticator");
        wrong.Json.RootElement.GetProperty("clearOtp").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RegistrationStateHidesPasswordAndCodeIdAndDoesNotInventCodeExpiry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.Setup(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAccountRequest, Metadata, DateTime?, CancellationToken>((_, metadata, _, _) =>
            {
                var rawId = metadata.Single(item => item.Key == MetadataKeys.DeviceId).Value;
                fixture.SetObservedDeviceId(Encoding.UTF8.GetString(Convert.FromBase64String(rawId)));
            })
            .Returns((CreateAccountRequest request, Metadata _, DateTime? _, CancellationToken _) =>
            {
                request.InitialPassword.Should().Be("secret-password");
                return GrpcCallHelpers.AsyncUnary(new CreateAccountResponse { CodeId = "private-code-id" });
            });

        using var initial = await fixture.GetStateAsync("/register");
        fixture.RemoveDeviceCookie();
        using var pending = await fixture.PostStateAsync("/register", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("username", "demo-user"), ("email", "demo@example.test"), ("password", "secret-password")));

        pending.Json.RootElement.GetProperty("screen").GetString().Should().Be("register-otp");
        pending.Json.RootElement.GetProperty("values").GetProperty("username").GetString().Should().Be("demo-user");
        pending.Json.RootElement.TryGetProperty("codeExpiresAt", out _).Should().BeFalse();
        pending.Json.RootElement.TryGetProperty("formMessage", out _).Should().BeFalse();
        pending.Json.RootElement.GetRawText().Should().NotContain("secret-password").And.NotContain("private-code-id");
        var returnedDevice = fixture.CookieValue(pending.Response, AuthGateway.DeviceCookie);
        fixture.ObservedDeviceId.Should().Be(returnedDevice);
    }

    [Fact]
    public async Task RegistrationSessionPendingRetryUsesOnlyCreateTokenAndPreservesRefreshExpiry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var refreshExpiresAt = DateTime.UtcNow.AddDays(30);
        fixture.Identity.Setup(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateAccountResponse { CodeId = "private-code-id" }));
        fixture.Identity.Setup(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((ConfirmAccountRequest request, Metadata _, DateTime? _, CancellationToken _) =>
            {
                request.InitialPassword.Should().Be("secret-password");
                return GrpcCallHelpers.AsyncUnary(new ConfirmAccountResponse
                {
                    RefreshToken = new Token
                    {
                        Value = "private-refresh-token",
                        ExpirationDate = Timestamp.FromDateTime(refreshExpiresAt)
                    }
                });
            });
        fixture.Identity.SetupSequence(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(RpcFailure("temporary-session-failure"))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateTokenResponse
            {
                AccessToken = new Token
                {
                    Value = "private-access-token",
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1))
                }
            }));

        using var initial = await fixture.GetStateAsync("/register");
        using var form = await fixture.PostStateAsync("/register", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("username", "demo-user"), ("email", "demo@example.test"), ("password", "secret-password")));
        var flowId = form.Json.RootElement.GetProperty("flowId").GetString()!;
        using var sessionPending = await fixture.PostStateAsync("/register/confirm", form.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456")));

        sessionPending.Json.RootElement.GetProperty("screen").GetString().Should().Be("session-pending");
        sessionPending.Json.RootElement.GetRawText().Should().NotContain("private-refresh-token");
        sessionPending.Json.RootElement.GetRawText().Should().NotContain("private-access-token");
        fixture.Identity.Verify(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);

        using var retry = await fixture.PostStateAsync("/register/confirm", sessionPending.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId)));
        retry.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/photos");
        fixture.Identity.Verify(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Identity.Verify(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Identity.Verify(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var accessCookie = fixture.CookieValue(retry.Response, AuthGateway.AccessCookie);
        var refreshCookie = fixture.CookieValue(retry.Response, AuthGateway.RefreshCookie);
        accessCookie.Should().Be("private-access-token");
        refreshCookie.Should().Be("private-refresh-token");
        var refreshLine = retry.Response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(AuthGateway.RefreshCookie + "=", StringComparison.Ordinal));
        refreshLine.Should().Contain(refreshExpiresAt.ToUniversalTime().ToString("ddd, dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        retry.Response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(AuthGateway.AccessCookie + "=", StringComparison.Ordinal))
            .ToLowerInvariant().Should().NotContain("expires=");
    }

    [Fact]
    public async Task RegistrationCanSucceedWithEmailDisabledWhenFeatureIsEnabled()
    {
        await using var fixture = await Fixture.CreateAsync(emailEnabled: false);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(30);
        fixture.Identity.Setup(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateAccountResponse
            {
                RefreshToken = new Token
                {
                    Value = "private-refresh-token",
                    ExpirationDate = Timestamp.FromDateTime(refreshExpiresAt)
                }
            }));
        fixture.Identity.Setup(x => x.CreateTokenAsync(It.IsAny<CreateTokenRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateTokenResponse
            {
                AccessToken = new Token
                {
                    Value = "private-access-token",
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1))
                }
            }));

        using var initial = await fixture.GetStateAsync("/register");
        initial.RootElement.GetProperty("features").GetProperty("registrationEnabled").GetBoolean().Should().BeTrue();
        initial.RootElement.GetProperty("features").GetProperty("emailEnabled").GetBoolean().Should().BeFalse();
        using var result = await fixture.PostStateAsync("/register", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("username", "demo-user"), ("email", "demo@example.test"), ("password", "secret-password")));

        result.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/photos");
        fixture.Identity.Verify(x => x.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Identity.Verify(x => x.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetNotFoundAndInvalidCodeHaveTheSamePublicState()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.ResetPasswordAsync(It.IsAny<ResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResetPasswordResponse { ResetId = "private-fake-reset-id" }))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResetPasswordResponse { ResetId = "private-real-reset-id" }));
        fixture.Identity.SetupSequence(x => x.ConfirmResetPasswordAsync(It.IsAny<ConfirmResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(RpcFailure(new ResetIdNotFoundException().ErrorCode))
            .Throws(RpcFailure(new NotValidOtpCodeException().ErrorCode));

        using var initial = await fixture.GetStateAsync("/forgot");
        using var fake = await fixture.PostStateAsync("/forgot", initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "unknown-account")));
        using var fakeInvalid = await fixture.PostStateAsync("/forgot/confirm", fake.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", fake.Json.RootElement.GetProperty("flowId").GetString()!),
                ("otp", "123456"), ("password", "new-secret-password")));
        using var real = await fixture.PostStateAsync("/forgot", fake.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "known-account")));
        using var realInvalid = await fixture.PostStateAsync("/forgot/confirm", real.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", real.Json.RootElement.GetProperty("flowId").GetString()!),
                ("otp", "123456"), ("password", "new-secret-password")));

        foreach (var state in new[] { fakeInvalid.Json.RootElement, realInvalid.Json.RootElement })
        {
            state.GetProperty("screen").GetString().Should().Be("reset");
            state.GetProperty("formError").GetString().Should().Be("Код недействителен или истёк. Запросите новый.");
            state.GetProperty("fieldErrors").GetProperty("otp").GetString()
                .Should().Be("Код недействителен или истёк. Запросите новый.");
            state.TryGetProperty("codeExpiresAt", out _).Should().BeFalse();
            state.GetRawText().Should().NotContain("private-fake-reset-id").And.NotContain("private-real-reset-id");
            state.GetProperty("values").GetProperty("revoke_sessions").GetBoolean().Should().BeFalse();
        }
        fixture.Identity.Verify(x => x.ConfirmResetPasswordAsync(
            It.Is<ConfirmResetPasswordRequest>(request => !request.RevokeOtherSessions),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResetWithAuthenticatorRequiredShowsTotpFieldAndCompletesWithBothCodes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.Setup(x => x.ResetPasswordAsync(It.IsAny<ResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResetPasswordResponse { ResetId = "reset-with-2fa" }));
        fixture.Identity.SetupSequence(x => x.ConfirmResetPasswordAsync(It.IsAny<ConfirmResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired(string.Empty, 0, method: "authenticator"))
            .Throws(RpcFailure(new NotValidOtpCodeException().ErrorCode))
            .Returns(GrpcCallHelpers.AsyncUnary(new ConfirmResetPasswordResponse
            {
                AccessToken = new Token { Value = "private-access-token" },
                RefreshToken = new Token
                {
                    Value = "private-refresh-token",
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(30))
                }
            }));

        using var initial = await fixture.GetStateAsync("/forgot");
        var csrf = initial.RootElement.GetProperty("csrfToken").GetString()!;
        using var begin = await fixture.PostStateAsync("/forgot", csrf,
            Fields(("login", "known@example.test")));
        var flowId = begin.Json.RootElement.GetProperty("flowId").GetString()!;

        using var needsTotp = await fixture.PostStateAsync("/forgot/confirm",
            begin.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456"), ("password", "new-secret-password")));
        var state = needsTotp.Json.RootElement;
        state.GetProperty("screen").GetString().Should().Be("reset");
        state.GetProperty("authenticatorRequired").GetBoolean().Should().BeTrue();
        state.GetProperty("formError").GetString().Should().Be("Введите код из приложения-аутентификатора, чтобы завершить восстановление.");
        state.GetProperty("fieldErrors").GetProperty("authenticator").GetString().Should().NotBeNullOrEmpty();
        state.TryGetProperty("clearOtp", out _).Should().BeFalse("email-код уже проверен и не очищается");
        state.TryGetProperty("fieldErrors", out var errors).Should().BeTrue();
        errors.TryGetProperty("otp", out _).Should().BeFalse();

        // Неверный TOTP: состояние сохраняется, email-код не сбрасывается, cooldown не запускается.
        using var wrongTotp = await fixture.PostStateAsync("/forgot/confirm",
            needsTotp.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456"), ("authenticator_code", "000000"),
                ("password", "new-secret-password")));
        var invalidState = wrongTotp.Json.RootElement;
        invalidState.GetProperty("screen").GetString().Should().Be("reset");
        invalidState.GetProperty("authenticatorRequired").GetBoolean().Should().BeTrue();
        invalidState.TryGetProperty("clearOtp", out _).Should().BeFalse();
        invalidState.GetProperty("fieldErrors").TryGetProperty("otp", out _).Should().BeFalse();
        invalidState.GetProperty("fieldErrors").GetProperty("authenticator").GetString().Should().NotBeNullOrEmpty();

        using var done = await fixture.PostStateAsync("/forgot/confirm",
            wrongTotp.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456"), ("authenticator_code", "654321"),
                ("password", "new-secret-password")));
        done.Json.RootElement.GetProperty("redirectTo").GetString().Should().Be("/photos");

        fixture.Identity.Verify(x => x.ConfirmResetPasswordAsync(
            It.Is<ConfirmResetPasswordRequest>(request =>
                request.OtpCode == "123456" && request.AuthenticatorCode == "654321"
                && request.ResetId == "reset-with-2fa"),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetResendClearsAuthenticatorRequirement()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.SetupSequence(x => x.ResetPasswordAsync(It.IsAny<ResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResetPasswordResponse { ResetId = "reset-a" }))
            .Returns(GrpcCallHelpers.AsyncUnary(new ResetPasswordResponse { ResetId = "reset-b" }));
        fixture.Identity.Setup(x => x.ConfirmResetPasswordAsync(It.IsAny<ConfirmResetPasswordRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(OtpRequired(string.Empty, 0, method: "authenticator"));

        using var initial = await fixture.GetStateAsync("/forgot");
        using var begin = await fixture.PostStateAsync("/forgot",
            initial.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("login", "known@example.test")));
        var flowId = begin.Json.RootElement.GetProperty("flowId").GetString()!;

        using var needsTotp = await fixture.PostStateAsync("/forgot/confirm",
            begin.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId), ("otp", "123456"), ("password", "new-secret-password")));
        needsTotp.Json.RootElement.GetProperty("authenticatorRequired").GetBoolean().Should().BeTrue();

        using var resend = await fixture.PostStateAsync("/forgot/resend",
            needsTotp.Json.RootElement.GetProperty("csrfToken").GetString()!,
            Fields(("flow_id", flowId)));
        var resent = resend.Json.RootElement;
        resent.GetProperty("screen").GetString().Should().Be("reset");
        resent.TryGetProperty("authenticatorRequired", out _).Should().BeFalse("новый код начинает email-этап заново");
    }

    private static AuthResponse SuccessAuthResponse() => new()
    {
        AccessToken = new Token { Value = "private-access-token", ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)) },
        RefreshToken = new Token { Value = "private-refresh-token", ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(30)) }
    };

    private static AsyncUnaryCall<T> DelayedUnary<T>(Task<T> response) where T : class
        => new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static RpcException OtpRequired(string mailStatus, int retryAfter, string method = "email")
    {
        var trailers = new Metadata
        {
            { "x-error-code", new BarkCloud.Shared.Exceptions.Identity.OtpCodeNeedException().ErrorCode },
            { "x-otp-method", method }
        };
        if (method == "email")
        {
            trailers.Add("x-otp-retry-after-seconds", retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(mailStatus)) trailers.Add("x-otp-mail-status", mailStatus);
        }
        return new RpcException(new Status(StatusCode.Unauthenticated, "OTP required"), trailers);
    }

    private static RpcException RateLimited(int retryAfter)
    {
        var trailers = new Metadata
        {
            { "x-error-code", new BarkCloud.Shared.Exceptions.Identity.TooManyRequestsException().ErrorCode },
            { "x-retry-after-seconds", retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        return new RpcException(new Status(StatusCode.ResourceExhausted, "rate limit"), trailers);
    }

    private static RpcException RpcFailure(string code)
        => new(new Status(StatusCode.Unavailable, "mock backend failure"), new Metadata { { "x-error-code", code } });

    private static Dictionary<string, string> Fields(params (string Name, string Value)[] fields)
        => fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string TestAssetManifest = """
            {
              "src/auth/main.tsx": {
                "file": "assets/auth-entry.js",
                "name": "auth",
                "src": "src/auth/main.tsx",
                "isEntry": true,
                "imports": ["_shared/first.js"],
                "css": ["assets/auth-entry.css"]
              },
              "_shared/first.js": {
                "file": "assets/shared-first.js",
                "imports": ["_shared/nested.js"],
                "css": ["assets/shared-first.css"]
              },
              "_shared/nested.js": {
                "file": "assets/shared-nested.js",
                "css": ["assets/shared-nested.css"]
              }
            }
            """;
        private readonly CookieContainer _cookies;
        private readonly Uri _baseAddress;
        private readonly string _temporaryWebRoot;
        private readonly RsaJwtTestKeys _keys;

        private Fixture(WebApplication app, HttpClient client, CookieContainer cookies, Uri baseAddress,
            Mock<IdentityApi.IdentityApiClient> identity, Mock<ConfigurationApi.ConfigurationApiClient> configuration,
            string serverName, bool registrationEnabled, string temporaryWebRoot, RsaJwtTestKeys keys)
        {
            App = app;
            Client = client;
            _cookies = cookies;
            _baseAddress = baseAddress;
            Identity = identity;
            Configuration = configuration;
            ServerName = serverName;
            RegistrationEnabled = registrationEnabled;
            _temporaryWebRoot = temporaryWebRoot;
            _keys = keys;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Uri BaseAddress => _baseAddress;
        public Mock<IdentityApi.IdentityApiClient> Identity { get; }
        public Mock<ConfigurationApi.ConfigurationApiClient> Configuration { get; }
        public string ServerName { get; }
        public bool RegistrationEnabled { get; set; }
        public string? ObservedDeviceId { get; private set; }
        public void SetObservedDeviceId(string id) => ObservedDeviceId = id;

        public void SetRegistrationEnabled(bool enabled)
        {
            RegistrationEnabled = enabled;
            Configuration.Setup(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                    It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns(GrpcCallHelpers.AsyncUnary(new GetConfigurationResponse
                {
                    Configurations =
                    {
                        new ConfigurationItem
                        {
                            Section = "Features",
                            Key = "RegistrationEnabled",
                            Value = enabled ? "true" : "false"
                        }
                    }
                }));
        }

        public static async Task<Fixture> CreateAsync(bool emailEnabled = true,
            string serverName = "BarkCloud test", bool registrationEnabled = true)
        {
            var webProject = FindWebProject();
            var temporaryWebRoot = Path.Combine(Path.GetTempPath(), $"BarkCloud.Web.Tests.Auth.{Guid.NewGuid():N}");
            WebApplication? app = null;
            HttpClient? client = null;
            var keys = new RsaJwtTestKeys();
            try
            {
                Directory.CreateDirectory(temporaryWebRoot);
                await File.WriteAllTextAsync(Path.Combine(temporaryWebRoot, "asset-manifest.json"), TestAssetManifest);

                var cookies = new CookieContainer();
                var options = new WebApplicationOptions
                {
                    ApplicationName = typeof(AuthEndpoints).Assembly.FullName,
                    ContentRootPath = webProject,
                    WebRootPath = temporaryWebRoot,
                    EnvironmentName = Environments.Production
                };
                var builder = WebApplication.CreateBuilder(options);
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [RsaJwtKeys.PublicKeyConfigKey] = keys.PublicKeyFile,
                    ["JwtSettings:Issuer"] = "bark",
                    ["JwtSettings:Audience"] = "bark",
                    ["App:PublicHost"] = serverName,
                    ["App:Version"] = "1.10.0",
                    ["Features:RegistrationEnabled"] = registrationEnabled.ToString(),
                    ["Features:EmailEnabled"] = emailEnabled.ToString()
                });

                var identity = new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict);
                var configuration = new Mock<ConfigurationApi.ConfigurationApiClient>(MockBehavior.Strict);
                var fixture = new Fixture(null!, null!, cookies, null!, identity, configuration,
                    serverName, registrationEnabled, temporaryWebRoot, keys);
                configuration.Setup(x => x.GetConfigurationAsync(It.IsAny<GetConfigurationRequest>(),
                        It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                    .Returns(() => GrpcCallHelpers.AsyncUnary(new GetConfigurationResponse
                    {
                        Configurations =
                        {
                            new ConfigurationItem
                            {
                                Section = "Features",
                                Key = "RegistrationEnabled",
                                Value = fixture.RegistrationEnabled ? "true" : "false"
                            }
                        }
                    }));
                identity.Setup(x => x.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(),
                        It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                    .Callback<AuthRequest, Metadata, DateTime?, CancellationToken>((_, metadata, _, _) =>
                    {
                        var rawId = metadata.Single(item => item.Key == MetadataKeys.DeviceId).Value;
                        fixture.ObservedDeviceId = Encoding.UTF8.GetString(Convert.FromBase64String(rawId));
                    })
                    .Throws(RpcFailure("not-configured"));

                builder.Services.AddAntiforgery(antiforgery =>
                {
                    antiforgery.FormFieldName = "__RequestVerificationToken";
                    antiforgery.HeaderName = "X-CSRF-TOKEN";
                });
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddSingleton<TokenRevocationCache>();
                builder.Services.AddSingleton(identity.Object);
                builder.Services.AddSingleton(configuration.Object);
                builder.Services.AddSingleton<FeatureConfigurationGateway>();
                builder.Services.AddSingleton<AuthFlowStore>();
                builder.Services.AddSingleton<AuthPageService>();
                builder.Services.AddScoped<AuthGateway>();
                builder.Services.AddScoped<RegistrationGateway>();
                builder.Services.AddScoped<PasswordResetGateway>();

                app = builder.Build();
                app.MapAuthEndpoints();
                await app.StartAsync();
                var baseAddress = new Uri(app.Urls.Single());
                var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
                client = new HttpClient(handler) { BaseAddress = baseAddress };
                return new Fixture(app, client, cookies, baseAddress, identity, configuration,
                    serverName, registrationEnabled, temporaryWebRoot, keys);
            }
            catch
            {
                keys.Dispose();
                client?.Dispose();
                try
                {
                    if (app is not null)
                        await app.DisposeAsync();
                }
                finally
                {
                    if (Directory.Exists(temporaryWebRoot))
                        Directory.Delete(temporaryWebRoot, recursive: true);
                }
                throw;
            }
        }

        public async Task<JsonDocument> GetStateAsync(string path)
        {
            var response = await GetResponseJsonAsync(path);
            try
            {
                response.Response.StatusCode.Should().Be(HttpStatusCode.OK);
                return response.Json;
            }
            finally
            {
                response.Response.Dispose();
            }
        }

        public async Task<StateResponse> GetResponseJsonAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await Client.SendAsync(request);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            return new StateResponse(response, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
        }

        public async Task<JsonDocument> GetStateWithoutCookiesAsync(string path)
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = _baseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        public async Task<StateResponse> PostStateAsync(
            string path, string csrfToken, Dictionary<string, string> fields)
        {
            var values = new Dictionary<string, string>(fields, StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = csrfToken
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(values)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await Client.SendAsync(request);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return new StateResponse(response, json);
        }

        public Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> fields)
            => Client.PostAsync(path, new FormUrlEncodedContent(fields));

        public void RemoveDeviceCookie()
            => _cookies.Add(_baseAddress, new Cookie(AuthGateway.DeviceCookie, "", "/", _baseAddress.Host)
            {
                Expires = DateTime.UtcNow.AddDays(-1)
            });

        public string CookieValue(HttpResponseMessage response, string cookieName)
            => response.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith(cookieName + "=", StringComparison.Ordinal))
                .Split(';', 2)[0][(cookieName.Length + 1)..];

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await App.DisposeAsync();
            }
            finally
            {
                _keys.Dispose();
                if (Directory.Exists(_temporaryWebRoot))
                    Directory.Delete(_temporaryWebRoot, recursive: true);
            }
        }

        private static string FindWebProject()
        {
            foreach (var root in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(typeof(AuthEndpoints).Assembly.Location)! })
            {
                var current = new DirectoryInfo(root);
                while (current is not null)
                {
                    var project = Path.Combine(current.FullName, "Backend", "BarkCloud.Web");
                    if (File.Exists(Path.Combine(project, "BarkCloud.Web.csproj"))
                        && File.Exists(Path.Combine(project, "Pages", "Login Page Full.html")))
                        return project;
                    current = current.Parent;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the BarkCloud.Web content root for auth endpoint tests.");
        }
    }

    private sealed class StateResponse(HttpResponseMessage response, JsonDocument json) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public JsonDocument Json { get; } = json;
        public void Dispose()
        {
            Json.Dispose();
            Response.Dispose();
        }
    }
}
