namespace SignRelay.Contracts;

/// <summary>
/// Job lifecycle states. Serialises as a numeric integer on the wire (System.Text.Json default).
/// Wire values: Pending=0, Leased=1, Signing=2, Succeeded=3, Failed=4, TimedOut=5.
/// </summary>
public enum JobStatus
{
    Pending = 0,
    Leased = 1,
    Signing = 2,
    Succeeded = 3,
    Failed = 4,
    TimedOut = 5,
}
