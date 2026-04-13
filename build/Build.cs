using Nuke.Common;
using Nuke.Common.IO;
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

    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    static AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";
    static AbsolutePath PublishRoot => ArtifactsDirectory / "publish";

    static AbsolutePath Src => RootDirectory / "src";
    static AbsolutePath ContractsProj => Src / "SignRelay.Contracts" / "SignRelay.Contracts.csproj";
    static AbsolutePath CliProj => Src / "SignRelay.Cli" / "SignRelay.Cli.csproj";
    static AbsolutePath ServerProj => Src / "SignRelay.Server" / "SignRelay.Server.csproj";
    static AbsolutePath AgentProj => Src / "SignRelay.Agent" / "SignRelay.Agent.csproj";

    static AbsolutePath ServerDockerfile => RootDirectory / "docker" / "Dockerfile";

    /// <summary>Builds the relay server image (<c>docker build</c> with context at repo root, same as compose).</summary>
    Target DockerServer => _ => _
        .Executes(() =>
        {
            DockerBuild(s => s
                .SetPath(RootDirectory)
                .SetFile(ServerDockerfile)
                .SetTag(new[] { ServerDockerImage }));
        });

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

    Target PublishAgent => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            var outDir = PublishRoot / "SignRelay.Agent";
            DotNetPublish(s => s
                .SetProject(AgentProj)
                .SetConfiguration(Configuration)
                .SetOutput(outDir));
        });

    /// <summary>Packs Contracts + Cli NuGet packages and publishes Server + Agent.</summary>
    Target Publish => _ => _
        .DependsOn(PackContracts, PackCli, PublishServer, PublishAgent);

    /// <summary>Same outputs as <see cref="Publish"/> (aggregate alias).</summary>
    Target All => _ => _
        .DependsOn(Publish);
}
