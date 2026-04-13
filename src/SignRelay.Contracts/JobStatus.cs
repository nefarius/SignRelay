namespace SignRelay.Contracts;

public enum JobStatus
{
    Pending = 0,
    Leased = 1,
    Signing = 2,
    Succeeded = 3,
    Failed = 4,
    TimedOut = 5
}
