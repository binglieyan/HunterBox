using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class ModLinksTests
{
    [Fact]
    public void NexusUrl_BuildsPageForPositiveId()
    {
        Assert.Equal(
            "https://www.nexusmods.com/monsterhunterwilds/mods/12345",
            ModLinks.NexusUrl(12345));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public void NexusUrl_ReturnsNullForInvalidId(int? id)
    {
        Assert.Null(ModLinks.NexusUrl(id));
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///c:/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpUrl_OnlyAcceptsWebUrls(string? url, bool expected)
    {
        Assert.Equal(expected, ModLinks.IsHttpUrl(url));
    }
}
