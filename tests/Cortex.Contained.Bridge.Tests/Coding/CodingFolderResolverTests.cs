using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodingFolderResolverTests
{
    [Fact]
    public void IsPathInsideAny_EmptyAllowList_ReturnsFalse()
    {
        Assert.False(CodingFolderResolver.IsPathInsideAny("C:\\foo\\bar", []));
    }

    [Fact]
    public void IsPathInsideAny_ExactMatch_ReturnsTrue()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\foo", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_DescendantMatch_ReturnsTrue()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\foo\\bar\\baz", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_PrefixButNotChild_ReturnsFalse()
    {
        // C:\foobar should NOT match C:\foo
        Assert.False(CodingFolderResolver.IsPathInsideAny("C:\\foobar", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_DifferentRoot_ReturnsFalse()
    {
        Assert.False(CodingFolderResolver.IsPathInsideAny("D:\\foo", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_TrailingSlashOnRoot_StillMatches()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\foo\\bar", ["C:\\foo\\"]));
    }

    [Fact]
    public void IsPathInsideAny_TrailingSlashOnCandidate_StillMatches()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\foo\\bar\\", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_DriveLetterCasing_Insensitive()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("c:\\Foo\\Bar", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_MultipleRoots_AnyMatchSucceeds()
    {
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\b\\inner", ["C:\\a", "C:\\b"]));
    }

    [Fact]
    public void IsPathInsideAny_EmptyCandidate_Throws()
    {
        Assert.Throws<ArgumentException>(() => CodingFolderResolver.IsPathInsideAny("", ["C:\\foo"]));
    }

    [Fact]
    public void IsPathInsideAny_NullEntryInRoots_IgnoredNotThrown()
    {
        // The resolver tolerates whitespace/empty entries in the list (e.g. from an
        // edited config file with stray blank lines) — they are skipped, not validated.
        var roots = new List<string> { string.Empty, "C:\\foo" };
        Assert.True(CodingFolderResolver.IsPathInsideAny("C:\\foo\\sub", roots));
    }
}
