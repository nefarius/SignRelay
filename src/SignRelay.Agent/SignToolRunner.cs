using System.Diagnostics;

namespace SignRelay.Agent;

public sealed class SignToolRunner
{
    private readonly ILogger<SignToolRunner> _log;

    public SignToolRunner(ILogger<SignToolRunner> log) => _log = log;

    public async Task<int> SignAsync(string signToolPath, string filePath, string? thumbprint, string? timestampUrl, string? extraArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = signToolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("sign");
        psi.ArgumentList.Add("/v");
        psi.ArgumentList.Add("/fd");
        psi.ArgumentList.Add("sha256");

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            psi.ArgumentList.Add("/sha1");
            psi.ArgumentList.Add(thumbprint);
        }

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            psi.ArgumentList.Add("/tr");
            psi.ArgumentList.Add(timestampUrl);
            psi.ArgumentList.Add("/td");
            psi.ArgumentList.Add("sha256");
        }

        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            foreach (var part in extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(part);
        }

        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new InvalidOperationException("Failed to start signtool.");

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
            _log.LogInformation("{Out}", stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.LogWarning("{Err}", stderr.Trim());

        return proc.ExitCode;
    }
}
