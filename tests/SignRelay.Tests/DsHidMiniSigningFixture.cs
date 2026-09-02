namespace SignRelay.Tests;

/// <summary>
/// Synthetic stand-in for
/// https://github.com/nefarius/DsHidMini/actions/runs/33572516285/job/100075423146
/// — same nested paths, distinct bytes so index order is observable.
/// </summary>
internal static class DsHidMiniSigningFixture
{
    public const string X64RelativePath = "bin/x64/dshidmini/dshidmini.dll";
    public const string Arm64RelativePath = "bin/ARM64/dshidmini/dshidmini.dll";

    public static readonly byte[] X64Bytes = [0x4D, 0x5A, 0x00, 0x64];
    public static readonly byte[] Arm64Bytes = [0x4D, 0x5A, 0x00, 0xA6];
    public static readonly byte[] X64SignedBytes = [0x4D, 0x5A, 0x53, 0x64];
    public static readonly byte[] Arm64SignedBytes = [0x4D, 0x5A, 0x53, 0xA6];
}
