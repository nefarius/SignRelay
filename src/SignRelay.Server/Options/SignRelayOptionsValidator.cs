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

        if (errors.Count == 0)
            return ValidateOptionsResult.Success;

        if (!_env.IsProduction())
        {
            // Warn in non-production but allow startup to continue
            foreach (var err in errors)
                _log.LogWarning("Token configuration warning: {Message}", err);
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(errors);
    }
}
