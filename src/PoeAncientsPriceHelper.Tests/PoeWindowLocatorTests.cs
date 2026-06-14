using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class PoeWindowLocatorTests
{
    [Fact]
    public void LooksLikePoeWindow_AcceptsRealPoeProcess()
    {
        Assert.True(PoeWindowLocator.LooksLikePoeWindow("PathOfExileSteam", "Path of Exile 2"));
    }

    [Fact]
    public void LooksLikePoeWindow_AcceptsExactPoeTitleFallback()
    {
        Assert.True(PoeWindowLocator.LooksLikePoeWindow("unknown", "Path of Exile 2"));
    }

    [Fact]
    public void LooksLikePoeWindow_RejectsArchiveWindowWithPoePathInTitle()
    {
        Assert.False(PoeWindowLocator.LooksLikePoeWindow(
            "7zFM",
            @"C:\Users\richa\Downloads\Path of Exile 2 helper.zip"));
    }

    [Fact]
    public void LooksLikePoeWindow_RejectsPoe1Title()
    {
        Assert.False(PoeWindowLocator.LooksLikePoeWindow("PathOfExileSteam", "Path of Exile"));
    }

    [Fact]
    public void LooksLikePoeWindow_RejectsPoeProcessWithoutPoe2Title()
    {
        Assert.False(PoeWindowLocator.LooksLikePoeWindow("PathOfExileSteam", ""));
    }
}
