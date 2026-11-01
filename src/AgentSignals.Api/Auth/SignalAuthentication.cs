using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace AgentSignals.Api.Auth;

/// <summary>App roles declared on the Entra ID registration (see the AppHost).</summary>
public static class SignalRoles
{
    public const string Write = "Signals.Write";
    public const string Read = "Signals.Read";
}

public static class SignalPolicies
{
    public const string Write = "signals-write";
    public const string Read = "signals-read";
    public const string Any = "signals-any";
}

public static class SignalAuthentication
{
    private const string LocalDevelopmentMode = "LocalDevelopment";

    /// <summary>
    /// Entra ID bearer tokens through Microsoft.Identity.Web. The only alternative is a local
    /// development mode, which refuses to start outside the Development environment and only
    /// accepts loopback callers. Missing Entra configuration fails startup rather than opening the API.
    /// </summary>
    public static WebApplicationBuilder AddSignalAuthentication(this WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Auth:Mode"];
        if (string.Equals(mode, LocalDevelopmentMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("Auth:Mode=LocalDevelopment is only allowed in the Development environment.");
            }

            builder.Services.AddAuthentication(LocalDevelopmentHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, LocalDevelopmentHandler>(LocalDevelopmentHandler.SchemeName, _ => { });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]))
            {
                throw new InvalidOperationException(
                    "AzureAd:ClientId is not configured. Reference the Entra ID application from the AppHost, or set Auth:Mode=LocalDevelopment for local runs.");
            }

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(builder.Configuration);
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(SignalPolicies.Write, p => p.RequireRole(SignalRoles.Write))
            .AddPolicy(SignalPolicies.Read, p => p.RequireRole(SignalRoles.Read))
            .AddPolicy(SignalPolicies.Any, p => p.RequireRole(SignalRoles.Write, SignalRoles.Read));

        return builder;
    }

    /// <summary>
    /// Identifies the calling application (azp/appid), never the signed-in person: the protocol
    /// forbids storing developer identity.
    /// </summary>
    public static string Submitter(ClaimsPrincipal user) =>
        user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value ?? "unknown";
}

/// <summary>Grants both roles to loopback callers. Development environment only.</summary>
public sealed class LocalDevelopmentHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remote = Context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("azp", "local-development"),
            new Claim(ClaimTypes.Role, SignalRoles.Write),
            new Claim(ClaimTypes.Role, SignalRoles.Read),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
