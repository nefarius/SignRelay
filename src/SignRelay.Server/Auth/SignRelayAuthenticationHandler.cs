using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

        if (ConstantTimeEquals(token, opt.CiToken))
        {
            var id = new ClaimsIdentity(SchemeName);
            id.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Ci));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
        }

        if (ConstantTimeEquals(token, opt.AgentToken))
        {
            var id = new ClaimsIdentity(SchemeName);
            id.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Agent));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
        }

        var db = Context.RequestServices.GetRequiredService<AppDbContext>();
        var hash = CryptoUtil.Sha256Hex(token);

        // Check per-job CI token (CI submitter polling their own job)
        var jobByToken = await db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobTokenHash == hash, Context.RequestAborted)
            .ConfigureAwait(false);

        if (jobByToken is not null)
        {
            var jobIdentity = new ClaimsIdentity(SchemeName);
            jobIdentity.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Job));
            jobIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, jobByToken.Id));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(jobIdentity), Scheme.Name));
        }

        // Check per-job lease token (agent operating on a specific job)
        var now = DateTimeOffset.UtcNow;
        var jobByLease = await db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.LeaseTokenHash == hash, Context.RequestAborted)
            .ConfigureAwait(false);

        if (jobByLease is not null)
        {
            if (jobByLease.LeaseExpiresUtc <= now)
                return AuthenticateResult.Fail("Lease token has expired.");

            var leaseIdentity = new ClaimsIdentity(SchemeName);
            leaseIdentity.AddClaim(new Claim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Lease));
            leaseIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, jobByLease.Id));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(leaseIdentity), Scheme.Name));
        }

        return AuthenticateResult.Fail("Invalid token.");
    }

    /// <summary>
    /// Compares <paramref name="presented"/> against <paramref name="configured"/> in constant time.
    /// Returns <c>false</c> immediately (without comparing) when <paramref name="configured"/> is
    /// null/empty so that empty server tokens never accidentally match anything.
    /// </summary>
    private static bool ConstantTimeEquals(string presented, string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(configured);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
