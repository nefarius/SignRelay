using SignRelay.Cli.Commands;

namespace SignRelay.Tests;

public sealed class SubmitCommandDryRunTests
{
    [Fact]
    public async Task DryRun_prints_manifest_and_exits_zero_without_network()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "artifact.exe");
            await File.WriteAllBytesAsync(file, [0x4D, 0x5A]);

            var (exit, stdout, stderr) = await InvokeAsync(
                "submit",
                "--server", "https://relay.example.com",
                "--token", "dry-run-token-0123456789abcdef",
                "--output", Path.Combine(dir, "signed"),
                "--dry-run",
                file);

            Assert.Equal(0, exit);
            Assert.Empty(stderr);
            Assert.Contains("Dry run — no network request will be made.", stdout);
            Assert.Contains("https://relay.example.com/", stdout);
            Assert.Contains("artifact.exe", stdout);
            Assert.Contains("\"relativePath\"", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task DryRun_rejects_missing_file()
    {
        var dir = CreateTempDir();
        try
        {
            var missing = Path.Combine(dir, "missing.exe");
            var (exit, _, stderr) = await InvokeAsync(
                "submit",
                "--server", "https://relay.example.com",
                "--token", "dry-run-token-0123456789abcdef",
                "--in-place",
                "--dry-run",
                missing);

            Assert.Equal(2, exit);
            Assert.Contains("File not found", stderr);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task DryRun_rejects_output_and_inplace_together()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "a.exe");
            await File.WriteAllBytesAsync(file, [1]);

            var (exit, _, stderr) = await InvokeAsync(
                "submit",
                "--server", "https://relay.example.com",
                "--token", "dry-run-token-0123456789abcdef",
                "--output", dir,
                "--in-place",
                "--dry-run",
                file);

            Assert.Equal(2, exit);
            Assert.Contains("either --in-place or --output", stderr);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task DryRun_rejects_missing_token()
    {
        var dir = CreateTempDir();
        var previous = Environment.GetEnvironmentVariable("SIGN_RELAY_CI_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SIGN_RELAY_CI_TOKEN", null);
            var file = Path.Combine(dir, "a.exe");
            await File.WriteAllBytesAsync(file, [1]);

            var (exit, _, stderr) = await InvokeAsync(
                "submit",
                "--server", "https://relay.example.com",
                "--output", Path.Combine(dir, "out"),
                "--dry-run",
                file);

            Assert.Equal(2, exit);
            Assert.Contains("Missing CI token", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIGN_RELAY_CI_TOKEN", previous);
            TryDelete(dir);
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> InvokeAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await SubmitCommand.Build().Parse(args).InvokeAsync().ConfigureAwait(false);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "signrelay-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
