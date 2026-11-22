using HunterBox.Domain.Models;

namespace HunterBox.Tests;

public class ModelIdRewriterTests
{
    private const string ModelPath = "natives/stm/art/model/character/ch03/012/body.mod3";

    [Fact]
    public void TryRewritePath_ReplacesIdSegment()
    {
        Assert.True(ModelIdRewriter.TryRewritePath(ModelPath, "012", "015", out string rewritten));
        Assert.Equal("natives/stm/art/model/character/ch03/015/body.mod3", rewritten);
    }

    [Fact]
    public void TryRewritePath_DoesNotTouchLongerSegments()
    {
        const string path = "natives/stm/art/model/character/ch03/0123/body.mod3";

        Assert.False(ModelIdRewriter.TryRewritePath(path, "012", "015", out string rewritten));
        Assert.Equal(path, rewritten);
    }

    [Fact]
    public void TryRewritePath_NotFound_ReturnsOriginal()
    {
        Assert.False(ModelIdRewriter.TryRewritePath(ModelPath, "999", "001", out string rewritten));
        Assert.Equal(ModelPath, rewritten);
    }

    [Fact]
    public void TryRewritePath_HandlesBackslashes_NormalizingToForwardSlash()
    {
        Assert.True(ModelIdRewriter.TryRewritePath(
            @"natives\stm\art\model\character\ch03\012\body.mod3",
            "012",
            "015",
            out string rewritten));
        Assert.Equal("natives/stm/art/model/character/ch03/015/body.mod3", rewritten);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("abc")]
    [InlineData("")]
    public void IsValidId_OnlyAcceptsThreeDigits(string id)
    {
        Assert.False(ModelIdRewriter.IsValidId(id));
    }

}
