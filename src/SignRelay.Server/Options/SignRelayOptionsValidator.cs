using Microsoft.Extensions.Options;

namespace SignRelay.Server.Options;

public sealed class SignRelayOptionsValidator : IValidateOptions<SignRelayOptions>
{
    private const int MinTokenLength = 32;

    private readonly IHostEnvironment _env;
    private readonly ILogger<SignRelayOptionsValidator> _log;

    public SignRelayOptionsValidator(IHostEnvironment env, ILogger<SignRelayOptionsValidator> log)
    {
        _env = env;
        _log = log;
    }

    public ValidateOptionsResult Validate(string? name, SignRelayOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.CiToken) || options.CiToken.Length < MinTokenLength)
            errors.Add($"SignRelay:CiToken must be at least {MinTokenLength} characters.");

        if (string.IsNullOrWhiteSpace(options.AgentToken) || options.AgentToken.Length < MinTokenLength)
            errors.Add($"SignRelay:AgentToken must be at least {MinTokenLength} characters.");

        // Duration sanity — fail in every environment (including Development).
        if (options.ArtifactCleanupDelay < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                "SignRelay:ArtifactCleanupDelay must not be negative.");
        }

        if (options.JobRecordRetention < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                "SignRelay:JobRecordRetention must not be negative.");
        }

        // Retention must never be shorter than artifact cleanup — fail in every environment.
        if (options.JobRecordRetention < options.ArtifactCleanupDelay)
        {
            return ValidateOptionsResult.Fail(
                "SignRelay:JobRecordRetention must be greater than or equal to SignRelay:ArtifactCleanupDelay " +
                $"({options.ArtifactCleanupDelay}).");
        }

        if (errors.Count == 0)
            return ValidateOptionsResult.Success;

        foreach (var err in errors)
            _log.LogWarning("Token configuration warning: {Message}", err);

        // Only suppress token failures in the Development environment. Any other environment
        // (Staging, QA, Production, custom) is treated as production-grade and fails fast.
        if (_env.IsDevelopment())
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(errors);
    }
}
