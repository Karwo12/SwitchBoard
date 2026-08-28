using SwitchBoard.Themes;

namespace SwitchBoard.RuntimeTests.Themes;

public sealed class BackgroundAssetKindsTests
{
    [Theory]
    [InlineData(null, BackgroundAssetKind.None)]
    [InlineData("background.png", BackgroundAssetKind.Image)]
    [InlineData("background.JPG", BackgroundAssetKind.Image)]
    [InlineData("background.gif", BackgroundAssetKind.Gif)]
    [InlineData("background.MP4", BackgroundAssetKind.Video)]
    public void BackgroundAssetKinds_RoutesEachSupportedAssetToItsDedicatedPlayer(string? path, BackgroundAssetKind expected)
    {
        Assert.Equal(expected, BackgroundAssetKinds.Detect(path));
    }
}
