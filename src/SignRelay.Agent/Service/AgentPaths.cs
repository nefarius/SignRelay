namespace SignRelay.Agent.Service;

/// <summary>Machine-wide paths for SignRelay Agent configuration, logs, and staging.</summary>
public static class AgentPaths
{
    public const string DefaultServiceName = "SignRelayAgent";
    public const string DefaultServiceDisplayName = "SignRelay Agent";
    public const string EventLogSourceName = "SignRelay Agent";

    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SignRelay", "Agent");

    public static string MachineSettingsFile => Path.Combine(RootDirectory, "agent.settings.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string DefaultJobStagingRoot => Path.Combine(RootDirectory, "jobs");
}
