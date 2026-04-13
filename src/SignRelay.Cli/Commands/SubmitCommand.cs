using System.CommandLine;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SignRelay.Contracts;

namespace SignRelay.Cli.Commands;

public static class SubmitCommand
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static RootCommand Build()
    {
        var server = new Option<Uri>("--server", "Base URL of the SignRelay server (e.g. https://relay.example.com)") { IsRequired = true };
        var token = new Option<string>(
            aliases: ["--token", "-t"],
            getDefaultValue: () => Environment.GetEnvironmentVariable("SIGN_RELAY_CI_TOKEN") ?? "",
            description: "CI bearer token (or set SIGN_RELAY_CI_TOKEN).");
        token.IsRequired = false;

        var output = new Option<DirectoryInfo?>("--output", "Write signed files under this directory (preserves relative paths).");
        var inplace = new Option<bool>("--in-place", () => false, "Overwrite input files with signed copies.");
        var timeout = new Option<TimeSpan>(
            "--timeout",
            () => TimeSpan.FromMinutes(45),
            "Maximum time to wait for signing to complete.");

        var files = new Argument<List<string>>("files", "Paths to files to sign") { Arity = ArgumentArity.OneOrMore };

        var cmd = new Command("submit", "Submit files to the relay, wait for signing, then download signed outputs.")
        {
            server,
            token,
            output,
            inplace,
            timeout,
            files
        };

        cmd.SetHandler(RunAsync, server, token, output, inplace, timeout, files);

        var root = new RootCommand("SignRelay CI client");
        root.AddCommand(cmd);
        return root;
    }

    private static async Task<int> RunAsync(
        Uri server,
        string token,
        DirectoryInfo? outputDir,
        bool inPlace,
        TimeSpan timeout,
        List<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await Console.Error.WriteLineAsync("Missing CI token: pass --token or set SIGN_RELAY_CI_TOKEN.").ConfigureAwait(false);
            return 2;
        }

        if (inPlace && outputDir is not null)
        {
            await Console.Error.WriteLineAsync("Use either --in-place or --output, not both.").ConfigureAwait(false);
            return 2;
        }

        if (!inPlace && outputDir is null)
        {
            await Console.Error.WriteLineAsync("Specify --output <dir> or use --in-place to overwrite inputs.").ConfigureAwait(false);
            return 2;
        }

        var cwd = Environment.CurrentDirectory;
        var normalized = filePaths.Select(Path.GetFullPath).Distinct().ToList();
        foreach (var p in normalized)
        {
            if (!File.Exists(p))
            {
                await Console.Error.WriteLineAsync($"File not found: {p}").ConfigureAwait(false);
                return 2;
            }
        }

        var manifest = new JobManifestDto
        {
            Files = normalized.Select(f => new JobFileEntry { RelativePath = Path.GetRelativePath(cwd, f) }).ToList()
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(timeout);

        using var http = new HttpClient { BaseAddress = TrimServer(server), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var content = new MultipartFormDataContent();
            var manifestJson = JsonSerializer.Serialize(manifest, Json);
            content.Add(new StringContent(manifestJson, Encoding.UTF8, "application/json"), "manifest");

            for (var i = 0; i < normalized.Count; i++)
            {
                var stream = File.OpenRead(normalized[i]);
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileContent, $"file_{i}", manifest.Files[i].RelativePath);
            }

            using var post = await http.PostAsync("api/v1/jobs", content, cts.Token).ConfigureAwait(false);
            var postBody = await post.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!post.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Submit failed: {(int)post.StatusCode} {post.ReasonPhrase}\n{postBody}").ConfigureAwait(false);
                return 3;
            }

            var submitResponse = JsonSerializer.Deserialize<SubmitJobResponse>(postBody, Json);
            if (submitResponse is null)
            {
                await Console.Error.WriteLineAsync("Invalid submit response.").ConfigureAwait(false);
                return 3;
            }

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", submitResponse.JobToken);

            await foreach (var ev in ReadEventsAsync(http, submitResponse.JobId, cts.Token))
            {
                if (ev.Type == "done")
                {
                    if (ev.Status != JobStatus.Succeeded)
                    {
                        await Console.Error.WriteLineAsync($"Signing failed: {ev.Status} {ev.Error}").ConfigureAwait(false);
                        return 4;
                    }

                    break;
                }
            }

            for (var i = 0; i < normalized.Count; i++)
            {
                var rel = manifest.Files[i].RelativePath;
                var path = normalized[i];
                await DownloadSignedAsync(http, submitResponse.JobId, rel, path, outputDir?.FullName, cts.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Timed out waiting for signing.").ConfigureAwait(false);
            return 5;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static Uri TrimServer(Uri server)
    {
        var s = server.ToString().TrimEnd('/') + "/";
        return new Uri(s);
    }

    private static async IAsyncEnumerable<JobEventPayload> ReadEventsAsync(HttpClient http, string jobId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/jobs/{jobId}/events");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = line["data:".Length..].Trim();
            if (json.Length == 0)
                continue;

            var payload = JsonSerializer.Deserialize<JobEventPayload>(json, Json);
            if (payload is not null)
                yield return payload;
        }
    }

    private static async Task DownloadSignedAsync(
        HttpClient http,
        string jobId,
        string relativeName,
        string originalPath,
        string? outputBase,
        CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(relativeName);
        using var response = await http.GetAsync($"api/v1/jobs/{jobId}/signed/{escaped}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        if (outputBase is not null)
        {
            var dest = Path.Combine(outputBase, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await WriteFileAtomicAsync(dest, input, ct).ConfigureAwait(false);
            return;
        }

        var dir = Path.GetDirectoryName(originalPath)!;
        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await WriteFileAtomicAsync(tmp, input, ct).ConfigureAwait(false);
            File.Move(tmp, originalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    private static async Task WriteFileAtomicAsync(string path, Stream input, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        await input.CopyToAsync(fs, ct).ConfigureAwait(false);
    }
}
