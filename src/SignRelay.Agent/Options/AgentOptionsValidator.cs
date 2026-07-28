using Microsoft.Extensions.Options;

namespace SignRelay.Agent.Options;

public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AgentToken))
            errors.Add("SignRelayAgent:AgentToken must be configured.");

        if (string.IsNullOrWhiteSpace(options.RelayUrl)
            || !Uri.TryCreate(options.RelayUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("SignRelayAgent:RelayUrl must be an absolute http or https URL.");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            errors.Add(
                "SignRelayAgent:RelayUrl must use https for non-loopback hosts " +
                "(http is only allowed for localhost / loopback).");
        }

        if (options.PollInterval <= TimeSpan.Zero)
            errors.Add("SignRelayAgent:PollInterval must be positive.");

        if (options.LeaseDuration <= TimeSpan.Zero)
            errors.Add("SignRelayAgent:LeaseDuration must be positive.");

        if (!string.IsNullOrWhiteSpace(options.JobStagingRoot)
            && !Path.IsPathRooted(options.JobStagingRoot))
        {
            errors.Add("SignRelayAgent:JobStagingRoot must be an absolute path when set.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
