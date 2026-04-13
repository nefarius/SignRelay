using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Services;

namespace SignRelay.Server.Auth;

public sealed class SignRelayAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SignRelay";

    private readonly IOptionsMonitor<Options.SignRelayOptions> _options;

    public SignRelayAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<Options.SignRelayOptions> options)
        : base(schemeOptions, logger, encoder) =>
        _options = options;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var auth = authHeader.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = auth["Bearer ".Length..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.Fail("Missing bearer token.");

        var opt = _options.CurrentValue;
        if (!string.IsNullOrEmpty(opt.CiToken) && token == opt.CiToken)
        {
            var id = new ClaimsIdentity(SchemeName);
            id.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Ci));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
        }

        if (!string.IsNullOrEmpty(opt.AgentToken) && token == opt.AgentToken)
        {
            var id = new ClaimsIdentity(SchemeName);
            id.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Agent));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
        }

        var hash = CryptoUtil.Sha256Hex(token);
        var db = Context.RequestServices.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobTokenHash == hash, Context.RequestAborted)
            .ConfigureAwait(false);

        if (job is null)
            return AuthenticateResult.Fail("Invalid token.");

        var jobIdentity = new ClaimsIdentity(SchemeName);
        jobIdentity.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Job));
        jobIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, job.Id));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(jobIdentity), Scheme.Name));
    }
}
