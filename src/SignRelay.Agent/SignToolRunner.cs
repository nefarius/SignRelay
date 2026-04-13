using CliWrap;
using CliWrap.Buffered;

namespace SignRelay.Agent;

public sealed class SignToolRunner
{
    private readonly ILogger<SignToolRunner> _log;

    public SignToolRunner(ILogger<SignToolRunner> log) => _log = log;

    public async Task<int> SignAsync(string signToolPath, string filePath, string? thumbprint, string? timestampUrl, string? extraArgs, CancellationToken ct)
    {
        var args = new List<string> { "sign", "/v", "/fd", "sha256" };

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            args.Add("/sha1");
            args.Add(thumbprint);
        }

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            args.Add("/tr");
            args.Add(timestampUrl);
            args.Add("/td");
            args.Add("sha256");
        }

        if (!string.IsNullOrWhiteSpace(extraArgs))
            args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        args.Add(filePath);

        var result = await Cli.Wrap(signToolPath)
            .WithArguments(args)
            .ExecuteBufferedAsync(ct);

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            _log.LogInformation("{Out}", result.StandardOutput.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            _log.LogWarning("{Err}", result.StandardError.Trim());

        return result.ExitCode;
    }
}
