using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine;

using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class ConnectionSecurityTests
{
    [Fact]
    public void Missing_server_certificate_option_defaults_to_strict_validation()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        JsonSerializer.Deserialize<ServerConfig>("{}", options)!.AcceptAnyCert.Should().BeFalse();
        JsonSerializer.Deserialize<global::EngineConfig>("{}", options)!.DangerousAcceptAnyServerCert.Should().BeFalse();
    }

    [Fact]
    public void Explicit_server_certificate_option_still_accepts_any_certificate()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        JsonSerializer.Deserialize<ServerConfig>("""{"acceptAnyCert":true}""", options)!.AcceptAnyCert.Should().BeTrue();
        JsonSerializer.Deserialize<global::EngineConfig>("""{"dangerousAcceptAnyServerCert":true}""", options)!
            .DangerousAcceptAnyServerCert.Should().BeTrue();
    }

    [Fact]
    public void Tls_authentication_errors_are_classified_through_connection_wrappers()
    {
        var tlsError = new AuthenticationException("certificate rejected");
        Exception[] errors =
        [
            tlsError,
            new HttpRequestException("HTTP request failed", tlsError),
            new RpcException(new Status(StatusCode.Unavailable, "service unavailable", tlsError)),
            new RpcException(new Status(StatusCode.Unavailable, "service unavailable",
                new HttpRequestException("HTTP request failed", tlsError))),
            new AggregateException("parallel connection failures", new IOException("offline"),
                new RpcException(new Status(StatusCode.Unavailable, "service unavailable", tlsError))),
        ];

        foreach (var error in errors)
        {
            ConnectionErrors.IsTls(error).Should().BeTrue();
            ConnectionErrors.GetDisplayMessage(error).Should().Be(Loc.T("Eng_TlsConnectionError"));
        }
    }

    [Fact]
    public void Non_tls_connection_errors_are_not_classified_as_tls()
    {
        var httpError = new HttpRequestException("HTTP request failed", new IOException("offline"));
        var rpcError = new RpcException(new Status(StatusCode.Unavailable, "service unavailable"));

        ConnectionErrors.IsTls(httpError).Should().BeFalse();
        ConnectionErrors.IsTls(rpcError).Should().BeFalse();
        ConnectionErrors.GetDisplayMessage(httpError).Should().Be("HTTP request failed");
        ConnectionErrors.GetDisplayMessage(rpcError).Should().Be("service unavailable");
    }

    [Fact]
    public void Empty_rpc_detail_falls_back_to_exception_message()
    {
        var error = new RpcException(new Status(StatusCode.Unavailable, string.Empty));

        ConnectionErrors.GetDisplayMessage(error).Should().Be(error.Message);
    }
}
