using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RuneshapeRegionProfilesTests
{
    [Fact]
    public void Resolve_UsesKnownClientProfile()
    {
        var region = RuneshapeRegionProfiles.Resolve(new Rectangle(100, 200, 1920, 1080));

        Assert.Equal(new Rectangle(152, 354, 497, 536), region);
    }

    [Fact]
    public void Resolve_CapturesPanelProfile_NotWholeClientTopLeft()
    {
        var client = new Rectangle(100, 200, 1920, 1080);

        var region = RuneshapeRegionProfiles.Resolve(client);

        Assert.True(region.Left > client.Left);
        Assert.True(region.Top > client.Top);
        Assert.True(region.Right < client.Right);
        Assert.True(region.Bottom < client.Bottom);
    }

    [Fact]
    public void ResolveProfile_InterpolatesUnknownResolution()
    {
        var profile = RuneshapeRegionProfiles.ResolveProfile(2240, 1260);

        Assert.InRange(profile.OffsetX, 52, 69);
        Assert.InRange(profile.OffsetY, 154, 205);
        Assert.InRange(profile.Width, 497, 663);
        Assert.InRange(profile.Height, 536, 715);
    }

    [Fact]
    public void Resolve_ClipsToClientRect()
    {
        var region = RuneshapeRegionProfiles.Resolve(new Rectangle(0, 0, 300, 220));

        Assert.True(region.Right <= 300);
        Assert.True(region.Bottom <= 220);
    }
}
