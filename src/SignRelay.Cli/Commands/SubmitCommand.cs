using System.CommandLine;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SignRelay.Contracts;

namespace SignRelay.Cli.Commands;

/// <summary>
/// Exit codes:
///   0 — success
///   1 — unexpected error
///   2 — invalid arguments / bad input
///   3 — server rejected the submit (4xx/5xx)
///   4 — signing failed on the agent side
///   5 — timeout or connection lost before signing completed
///   6 — SSE stream ended without a terminal event (server/proxy issue)
/// </summary>
public static class SubmitCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static RootCommand Build()
    {
        // System.CommandLine has no built-in Uri converter; without CustomParser,
        // GetValue throws InvalidOperationException for any --server value.
        var server = new Option<Uri>("--server")
        {
            Description = "Base URL of the SignRelay server (e.g. https://relay.example.com)",
            Required = true,
            CustomParser = result =>
            {
                var raw = result.Tokens.SingleOrDefault()?.Value;
                if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    result.AddError($"Cannot parse argument '{raw}' for option '--server' as an absolute http(s) URL.");
                    return null!;
                }

                return uri;
            }
        };
        var token = new Option<string>("--token", "-t")
        {
            Description = "CI bearer token (or set SIGN_RELAY_CI_TOKEN).",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SIGN_RELAY_CI_TOKEN") ?? ""
        };
        var output = new Option<DirectoryInfo?>("--output")
        {
            Description = "Write signed files under this directory (preserves relative paths)."
        };
        var inplace = new Option<bool>("--in-place")
        {
            Description = "Overwrite input files with signed copies.",
            DefaultValueFactory = _ => false
        };
        var timeout = new Option<TimeSpan>("--timeout")
        {
            Description = "Maximum time to wait for signing to complete.",
            DefaultValueFactory = _ => TimeSpan.FromMinutes(45)
        };
        var allowInsecure = new Option<bool>("--allow-insecure")
        {
            Description = "Allow http:// server URLs (not recommended; bearer tokens will be sent in cleartext).",
            DefaultValueFactory = _ => false
        };
        var files = new Argument<List<string>>("files")
        {
            Description = "Paths to files to sign",
            Arity = ArgumentArity.OneOrMore
        };

        var cmd = new Command("submit", "Submit files to the relay, wait for signing, then download signed outputs.")
        {
            server,
            token,
            output,
            inplace,
            timeout,
            allowInsecure,
            files
        };

        cmd.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(server)!,
            parseResult.GetValue(token) ?? "",
            parseResult.GetValue(output),
            parseResult.GetValue(inplace),
            parseResult.GetValue(timeout),
            parseResult.GetValue(allowInsecure),
            parseResult.GetValue(files)!,
            ct));

        var root = new RootCommand("SignRelay CI client");
        root.Subcommands.Add(cmd);
        return root;
    }

    private static async Task<int> RunAsync(
        Uri server,
        string token,
        DirectoryInfo? outputDir,
        bool inPlace,
        TimeSpan timeout,
        bool allowInsecure,
        List<string> filePaths,
        CancellationToken frameworkToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await Console.Error.WriteLineAsync("Missing CI token: pass --token or set SIGN_RELAY_CI_TOKEN.").ConfigureAwait(false);
            return 2;
        }

        if (!allowInsecure && server.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync(
                $"Server URL uses http:// — bearer tokens would be sent in cleartext. Pass --allow-insecure to override.").ConfigureAwait(false);
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

        if (outputDir is not null && outputDir.Exists && !outputDir.Attributes.HasFlag(FileAttributes.Directory))
        {
            await Console.Error.WriteLineAsync($"--output path exists and is not a directory: {outputDir.FullName}").ConfigureAwait(false);
            return 2;
        }

        // Normalise input paths and validate
        List<string> normalized;
        JobManifestDto manifest;
        try
        {
            var cwd = Environment.CurrentDirectory;
            var full = filePaths.Select(Path.GetFullPath).ToList();

            var duplicates = full.GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                await Console.Error.WriteLineAsync($"Duplicate input file(s): {string.Join(", ", duplicates)}").ConfigureAwait(false);
                return 2;
            }

            normalized = full;
            foreach (var p in normalized)
            {
                if (!File.Exists(p))
                {
                    await Console.Error.WriteLineAsync($"File not found: {p}").ConfigureAwait(false);
                    return 2;
                }
            }

            var relPaths = new List<string>(normalized.Count);
            foreach (var p in normalized)
            {
                string rel;
                try
                {
                    rel = Path.GetRelativePath(cwd, p);
                }
                catch (ArgumentException)
                {
                    await Console.Error.WriteLineAsync(
                        $"Cannot compute a relative path for '{p}' (different drive from working directory). Move the file or run from the same drive.").ConfigureAwait(false);
                    return 2;
                }

                // Validate and normalise client-side: reject paths escaping cwd
                try
                {
                    rel = PathSafety.NormalizeRelativePath(rel);
                }
                catch (InvalidOperationException ex)
                {
                    await Console.Error.WriteLineAsync($"File path '{p}' would produce an unsafe manifest entry: {ex.Message}").ConfigureAwait(false);
                    return 2;
                }

                relPaths.Add(rel);
            }

            manifest = new JobManifestDto
            {
                Files = normalized.Select((_, i) => new JobFileEntry { RelativePath = relPaths[i] }).ToList()
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Path error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        // Link CancellationToken to Ctrl+C, the wall-clock timeout, and the framework token
        using var ctrlC = new CancellationTokenSource();
        ConsoleCancelEventHandler ctrlCHandler = (_, e) => { e.Cancel = true; ctrlC.Cancel(); };
        Console.CancelKeyPress += ctrlCHandler;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctrlC.Token, frameworkToken);
        cts.CancelAfter(timeout);

        using var http = new HttpClient { BaseAddress = TrimServer(server), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var phase = "submit";
        try
        {
            // --- Upload phase ---
            SubmitJobResponse submitResponse;
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

                using var post = await http.PostAsync(ApiRoutes.Jobs, content, cts.Token).ConfigureAwait(false);
                // MultipartFormDataContent disposes here, closing all file handles before we need them again
                var postBody = await post.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!post.IsSuccessStatusCode)
                {
                    await Console.Error.WriteLineAsync($"Submit failed: {(int)post.StatusCode} {post.ReasonPhrase}\n{postBody}").ConfigureAwait(false);
                    return 3;
                }

                var parsed = JsonSerializer.Deserialize<SubmitJobResponse>(postBody, Json);
                if (parsed is null)
                {
                    await Console.Error.WriteLineAsync("Invalid submit response from server.").ConfigureAwait(false);
                    return 3;
                }
                submitResponse = parsed;
            }
            // Input file handles are now closed (MultipartFormDataContent disposed above)

            // Cap timeout to the server-reported expiry
            var serverExpiry = submitResponse.ExpiresAtUtc;
            var remainingToExpiry = serverExpiry - DateTimeOffset.UtcNow;
            if (remainingToExpiry > TimeSpan.Zero && remainingToExpiry < timeout)
                cts.CancelAfter(remainingToExpiry);

            // Switch to the per-job token for SSE and downloads
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", submitResponse.JobToken);

            // --- SSE phase ---
            phase = "waiting for signing";
            var done = false;
            await foreach (var ev in ReadEventsAsync(http, submitResponse.JobId, cts.Token))
            {
                if (ev.Type == "done")
                {
                    if (ev.Status != JobStatus.Succeeded)
                    {
                        await Console.Error.WriteLineAsync($"Signing failed: {ev.Status} {ev.Error}").ConfigureAwait(false);
                        return 4;
                    }
                    done = true;
                    break;
                }
            }

            if (!done)
            {
                // Stream ended without a terminal event
                await Console.Error.WriteLineAsync("SSE stream closed before signing completed (proxy timeout or server error).").ConfigureAwait(false);
                return 6;
            }

            // --- Download phase ---
            phase = "downloading";
            // Download all signed files to temp locations first, then atomically commit
            var temps = new List<(string TmpPath, string FinalPath)>();
            try
            {
                for (var i = 0; i < normalized.Count; i++)
                {
                    var rel = manifest.Files[i].RelativePath;
                    var originalPath = normalized[i];

                    string finalPath;
                    if (outputDir is not null)
                    {
                        var dest = Path.Combine(outputDir.FullName, rel);
                        var destDir = Path.GetDirectoryName(dest)!;
                        Directory.CreateDirectory(destDir);
                        finalPath = dest;
                    }
                    else
                    {
                        finalPath = originalPath;
                    }

                    var tmpPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await DownloadToTempAsync(http, submitResponse.JobId, rel, tmpPath, cts.Token).ConfigureAwait(false);
                    temps.Add((tmpPath, finalPath));
                }

                // All downloads succeeded.
                // Commit: back up any existing output files first so we can restore them if a
                // later move fails. Note: this is a sequential overwrite, NOT an atomic operation
                // across multiple files — filesystem atomicity is impossible here.
                var backups = new List<(string BackupPath, string FinalPath)>();
                var committed = new List<string>();
                try
                {
                    foreach (var (_, final) in temps)
                    {
                        if (File.Exists(final))
                        {
                            var bak = final + "." + Guid.NewGuid().ToString("N") + ".bak";
                            File.Move(final, bak);
                            backups.Add((bak, final));
                        }
                    }

                    foreach (var (tmp, final) in temps)
                    {
                        File.Move(tmp, final);
                        committed.Add(final);
                    }

                    foreach (var (bak, _) in backups)
                        try { File.Delete(bak); } catch { }

                    return 0;
                }
                catch
                {
                    // Undo committed moves and restore backups
                    foreach (var final in committed)
                        try { if (File.Exists(final)) File.Delete(final); } catch { }
                    foreach (var (bak, final) in backups)
                        try { if (File.Exists(bak)) File.Move(bak, final, overwrite: true); } catch { }
                    throw;
                }
            }
            catch
            {
                // Rollback: clean up any temp files that were not yet committed
                foreach (var (tmp, _) in temps)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (ctrlC.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Cancelled by user.").ConfigureAwait(false);
            return 5;
        }
        catch (OperationCanceledException) when (frameworkToken.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync($"Cancelled by host during {phase}.").ConfigureAwait(false);
            return 5;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"Timed out during {phase}.").ConfigureAwait(false);
            return 5;
        }
        catch (HttpRequestException ex)
        {
            var statusPrefix = ex.StatusCode.HasValue ? $"{(int)ex.StatusCode} " : "";
            await Console.Error.WriteLineAsync($"HTTP error during {phase}: {statusPrefix}{ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error during {phase}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= ctrlCHandler;
        }
    }

    private static Uri TrimServer(Uri server)
    {
        var s = server.ToString().TrimEnd('/') + "/";
        return new Uri(s);
    }

    private static async IAsyncEnumerable<JobEventPayload> ReadEventsAsync(
        HttpClient http,
        string jobId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.JobEvents(jobId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break; // stream closed — caller checks whether done=true

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = line["data:".Length..].Trim();
            if (json.Length == 0)
                continue;

            JobEventPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<JobEventPayload>(json, Json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to deserialize SSE event: {ex.Message}\nRaw: {json}", ex);
            }

            if (payload is null)
                throw new InvalidOperationException($"SSE event deserialised to null. Raw: {json}");

            yield return payload;
        }
    }

    private static async Task DownloadToTempAsync(
        HttpClient http,
        string jobId,
        string relativeName,
        string tmpPath,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(
            ApiRoutes.JobSignedFile(jobId, relativeName),
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(tmpPath);
        await input.CopyToAsync(fs, ct).ConfigureAwait(false);
    }
}
