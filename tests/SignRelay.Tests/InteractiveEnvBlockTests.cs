using System.Runtime.InteropServices;
using System.Text;
using SignRelay.Agent;

namespace SignRelay.Tests;

public sealed class InteractiveEnvBlockTests
{
    [Fact]
    public void ParseEnvironmentBlock_ParsesKeyValuePairs()
    {
        var block = AllocEnvBlock("PATH=C:\\Tools;C:\\Bin", "USERPROFILE=C:\\Users\\Test", "FOO=bar");
        try
        {
            var env = InteractiveUserProcessLauncher.ParseEnvironmentBlock(block);
            Assert.Equal(@"C:\Tools;C:\Bin", env["PATH"]);
            Assert.Equal(@"C:\Users\Test", env["USERPROFILE"]);
            Assert.Equal("bar", env["FOO"]);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Fact]
    public void ParseEnvironmentBlock_SkipsDriveCurrentDirectoryEntries()
    {
        // "=C:=C:\\Windows" style entries start with '=' and must be ignored.
        var block = AllocEnvBlock("=C:=C:\\Windows", "PATH=C:\\Windows\\System32");
        try
        {
            var env = InteractiveUserProcessLauncher.ParseEnvironmentBlock(block);
            Assert.Single(env);
            Assert.Equal(@"C:\Windows\System32", env["PATH"]);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [Fact]
    public void ParseEnvironmentBlock_EmptyBlock_ReturnsEmpty()
    {
        var block = AllocEnvBlock();
        try
        {
            var env = InteractiveUserProcessLauncher.ParseEnvironmentBlock(block);
            Assert.Empty(env);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>Allocates a double-NUL-terminated UTF-16 environment block.</summary>
    private static IntPtr AllocEnvBlock(params string[] entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e);
            sb.Append('\0');
        }

        sb.Append('\0'); // final terminator
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}
