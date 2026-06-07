using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("file.exe", "file.exe")]
    [InlineData("sub/file.exe", "sub" + "/" + "file.exe")] // normalized to OS sep in-test
    [InlineData("sub\\file.exe", "sub" + "/" + "file.exe")]
    public void NormalizeRelativePath_ValidPaths_Succeed(string input, string _)
    {
        // Should not throw
        var result = PathSafety.NormalizeRelativePath(input);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.exe")]
    [InlineData("sub/../escape.exe")]
    [InlineData("./current.exe")]
    public void NormalizeRelativePath_InvalidPaths_Throw(string input)
    {
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeRelativePath(input));
    }

    [Theory]
    [InlineData("/C:/Windows/evil.exe")]
    [InlineData("C:/Windows/evil.exe")]
    public void NormalizeRelativePath_RootedLikeNames_Throw(string input)
    {
        // Drive letters contain ':' which is in InvalidSegmentChars — always rejected
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeRelativePath(input));
    }

    [Fact]
    public void IsUnderRoot_PathDirectlyUnderRoot_ReturnsTrue()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root"));
        var child = Path.Combine(root, "file.exe");
        Assert.True(PathSafety.IsUnderRoot(child, root));
    }

    [Fact]
    public void IsUnderRoot_NestedPath_ReturnsTrue()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root"));
        var child = Path.Combine(root, "sub", "file.exe");
        Assert.True(PathSafety.IsUnderRoot(child, root));
    }

    [Fact]
    public void IsUnderRoot_RootItself_ReturnsFalse()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root"));
        Assert.False(PathSafety.IsUnderRoot(root, root));
    }

    [Fact]
    public void IsUnderRoot_PrefixCollisionPath_ReturnsFalse()
    {
        // /tmp/root-evil should NOT match root=/tmp/root
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root"));
        var sibling = root + "-evil" + Path.DirectorySeparatorChar + "file.exe";
        Assert.False(PathSafety.IsUnderRoot(sibling, root));
    }

    [Fact]
    public void IsUnderRoot_ParentPath_ReturnsFalse()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root", "sub"));
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "root", "other.exe"));
        Assert.False(PathSafety.IsUnderRoot(parent, root));
    }
}
