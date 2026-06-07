using Microsoft.Extensions.Options;

namespace SignRelay.Server.Options;

public sealed class SignRelayOptionsValidator : IValidateOptions<SignRelayOptions>
{
    private readonly IHostEnvironment _env;

    public SignRelayOptionsValidator(IHostEnvironment env) => _env = env;

    public ValidateOptionsResult Validate(string? name, SignRelayOptions options)
    {
        if (!_env.IsProduction())
            return ValidateOptionsResult.Success;

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.CiToken) || options.CiToken.Length < 16)
            errors.Add("SignRelay:CiToken must be at least 16 characters in Production.");

        if (string.IsNullOrWhiteSpace(options.AgentToken) || options.AgentToken.Length < 16)
            errors.Add("SignRelay:AgentToken must be at least 16 characters in Production.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
