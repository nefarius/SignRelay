using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.Docker.DockerTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

public sealed class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Publish);

    [Parameter("Configuration to build — Default is 'Release'.")]
    readonly string Configuration = "Release";

    [Parameter("Docker image name:tag for docker/Dockerfile (SignRelay.Server). Default is 'signrelay/server:latest'.")]
    readonly string ServerDockerImage = "signrelay/server:latest";

    [Parameter("If set, passed to docker build as MINVERVERSIONOVERRIDE instead of computing Version via dotnet msbuild on the host (Git/MinVer).")]
    readonly string MinVerVersionOverride = "";

    [Parameter("Container engine for image builds: 'docker' or 'podman'. Empty = auto-detect (docker, then podman).")]
    readonly string ContainerEngine = "";

    [Parameter("If set, pushes the built image to the registry after a successful build.")]
    readonly bool PushImage;

    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    static AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";
    static AbsolutePath PublishRoot => ArtifactsDirectory / "publish";
    static AbsolutePath ReleaseDirectory => ArtifactsDirectory / "release";

    static AbsolutePath Src => RootDirectory / "src";
    static AbsolutePath ContractsProj => Src / "SignRelay.Contracts" / "SignRelay.Contracts.csproj";
    static AbsolutePath CliProj => Src / "SignRelay.Cli" / "SignRelay.Cli.csproj";
    static AbsolutePath MsBuildProj => Src / "SignRelay.MSBuild" / "SignRelay.MSBuild.csproj";
    static AbsolutePath ServerProj => Src / "SignRelay.Server" / "SignRelay.Server.csproj";
    static AbsolutePath AgentProj => Src / "SignRelay.Agent" / "SignRelay.Agent.csproj";

    static AbsolutePath ServerDockerfile => RootDirectory / "docker" / "Dockerfile";

    /// <summary>Builds the relay server image (<c>docker build</c> with context at repo root, same as compose).</summary>
    Target DockerServer => _ => _
        .Executes(() =>
        {
            var minVer = string.IsNullOrWhiteSpace(MinVerVersionOverride)
                ? QueryMsBuildVersion(ServerProj)
                : MinVerVersionOverride.Trim();
            if (string.IsNullOrWhiteSpace(minVer))
                throw new InvalidOperationException("Could not resolve a version for MINVERVERSIONOVERRIDE (set MinVerVersionOverride or ensure MinVer can compute Version from Git tags).");

            var enginePath = ResolveContainerEnginePath(ContainerEngine);
            DockerBuild(s => s
                .SetProcessToolPath(enginePath)
                .SetPath(RootDirectory)
                .SetFile(ServerDockerfile)
                .SetTag(new[] { ServerDockerImage })
                .SetBuildArg(new[] { $"MINVERVERSIONOVERRIDE={minVer}" }));

            if (PushImage)
                DockerPush(s => s
                    .SetProcessToolPath(enginePath)
                    .SetName(ServerDockerImage));
        });

    /// <summary>Resolves the container engine executable path. Tries <paramref name="preferred"/> first; falls back to <c>docker</c> then <c>podman</c> when empty.</summary>
    static string ResolveContainerEnginePath(string preferred)
    {
        var candidates = string.IsNullOrWhiteSpace(preferred)
            ? new[] { "docker", "podman" }
            : new[] { preferred.Trim() };

        foreach (var name in candidates)
        {
            try { return ToolPathResolver.GetPathExecutable(name); }
            catch { /* try next */ }
        }

        throw new InvalidOperationException(
            $"No container engine found on PATH. Tried: {string.Join(", ", candidates)}. Install Docker or Podman, or pass --ContainerEngine.");
    }

    /// <summary>Reads <see cref="Version"/> from MSBuild/MinVer (requires <c>.git</c> on the host unless overridden).</summary>
    static string QueryMsBuildVersion(AbsolutePath projectPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{projectPath}\" -restore -getProperty:Version -nologo -verbosity:quiet",
            WorkingDirectory = RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet msbuild.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dotnet msbuild -getProperty:Version failed (exit {p.ExitCode}): {stderr}");

        return stdout.Trim();
    }

    Target Clean => _ => _
        .Executes(() =>
        {
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target PackContracts => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(ContractsProj)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackagesDirectory));
        });

    Target PackCli => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(CliProj)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackagesDirectory));
        });

    /// <summary>Packs the MSBuild targets package (Nefarius.Tools.SignRelay.MSBuild).</summary>
    Target PackMsBuild => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(MsBuildProj)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackagesDirectory));
        });

    Target PublishServer => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            var outDir = PublishRoot / "SignRelay.Server";
            DotNetPublish(s => s
                .SetProject(ServerProj)
                .SetConfiguration(Configuration)
                .SetOutput(outDir));
        });

    /// <summary>
    /// Publishes a self-contained win-x64 agent (no .NET runtime required on the signing machine).
    /// Not single-file — keeps P/Invoke and service install paths simple.
    /// </summary>
    Target PublishAgent => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            var outDir = PublishRoot / "SignRelay.Agent";
            DotNetPublish(s => s
                .SetProject(AgentProj)
                .SetConfiguration(Configuration)
                .SetRuntime("win-x64")
                .SetSelfContained(true)
                .SetOutput(outDir));
        });

    /// <summary>Packs Contracts + Cli + MSBuild NuGet packages and publishes Server + Agent.</summary>
    Target Publish => _ => _
        .DependsOn(PackContracts, PackCli, PackMsBuild, PublishServer, PublishAgent);

    /// <summary>Same outputs as <see cref="Publish"/> (aggregate alias).</summary>
    Target All => _ => _
        .DependsOn(Publish);

    /// <summary>
    /// Produces release archives under <c>artifacts/release</c>:
    /// SignRelay.Agent-&lt;version&gt;-win-x64.zip, SignRelay.Server-&lt;version&gt;.zip, checksums.txt.
    /// </summary>
    Target Release => _ => _
        .DependsOn(PublishAgent, PublishServer)
        .Executes(() =>
        {
            var version = string.IsNullOrWhiteSpace(MinVerVersionOverride)
                ? QueryMsBuildVersion(AgentProj)
                : MinVerVersionOverride.Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("Could not resolve a version for release archives.");

            // Strip a leading 'v' if someone passed a git tag as MinVerVersionOverride
            if (version.StartsWith('v') || version.StartsWith('V'))
                version = version[1..];

            ReleaseDirectory.CreateOrCleanDirectory();

            var agentZip = ReleaseDirectory / $"SignRelay.Agent-{version}-win-x64.zip";
            var serverZip = ReleaseDirectory / $"SignRelay.Server-{version}.zip";

            ZipFile.CreateFromDirectory(PublishRoot / "SignRelay.Agent", agentZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            ZipFile.CreateFromDirectory(PublishRoot / "SignRelay.Server", serverZip, CompressionLevel.Optimal, includeBaseDirectory: false);

            var checksumPath = ReleaseDirectory / "checksums.txt";
            File.WriteAllText(checksumPath,
                $"{Sha256Hex(agentZip)}  {agentZip.Name}{Environment.NewLine}" +
                $"{Sha256Hex(serverZip)}  {serverZip.Name}{Environment.NewLine}");

            Serilog.Log.Information("Release archives written to {Dir}", ReleaseDirectory);
        });

    static string Sha256Hex(AbsolutePath file)
    {
        using var stream = File.OpenRead(file);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
