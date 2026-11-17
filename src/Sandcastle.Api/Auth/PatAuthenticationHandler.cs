using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Auth;

/// <summary>
/// SPEC-158 RF-001: Bearer <c>sc_pat_*</c> authentication. Valida o PAT via
/// <see cref="IPersonalAccessTokenService"/> e emite principal com user_id,
/// <c>auth_type=pat</c>, <c>pat_id</c> e escopos (<c>scope</c> claim). O
/// middleware <c>PatScopeEnforcement</c> bloqueia métodos mutantes quando o
/// token não tem escopo <c>write</c>.
/// </summary>
public sealed class PatAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Scheme name used in <c>AuthenticationSchemes</c>.</summary>
    public const string SchemeName = "pat";

    /// <summary>Claim com o id do PAT.</summary>
    public const string PatIdClaim = "pat_id";

    /// <summary>Claim com os escopos separados por espaço.</summary>
    public const string ScopesClaim = "scope";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(IPersonalAccessTokenService.TokenPrefix,
                StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var pats = Context.RequestServices
            .GetRequiredService<IPersonalAccessTokenService>();
        var resolution = await pats.ValidateAsync(token, Context.RequestAborted);
        if (resolution is null)
            return AuthenticateResult.Fail("invalid or expired personal access token");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, resolution.UserId.ToString()),
            new("auth_type", "pat"),
            new(PatIdClaim, resolution.TokenId.ToString()),
            new(ScopesClaim, string.Join(' ', resolution.Scopes)),
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName));
    }
}