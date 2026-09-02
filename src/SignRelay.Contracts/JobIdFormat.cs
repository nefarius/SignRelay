namespace SignRelay.Contracts;

/// <summary>
/// Generated job identifiers are 32 hexadecimal characters
/// (<see cref="Guid.ToString(string)"/> format <c>N</c>).
/// </summary>
public static class JobIdFormat
{
    public const int Length = 32;

    public static bool IsValid(string? jobId)
    {
        if (jobId is not { Length: Length })
            return false;

        foreach (var c in jobId)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}
