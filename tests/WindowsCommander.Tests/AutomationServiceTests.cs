using WindowsCommander.Core.Models;
using WindowsCommander.Core.Safety;
using WindowsCommander.Safety.Policy;
using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class AutomationServiceTests
{
    [Fact]
    public void ControlIndicatorService_StoresConfigurationAndStatus()
    {
        var service = new ControlIndicatorService();
        var config = new ControlIndicatorConfig(true, false, "Blue", 2, 440, 100);

        var configured = service.ConfigureControlIndicators(config);
        var shown = service.ShowControlIndicator("Testing", new RectBounds(1, 2, 3, 4));
        var hidden = service.HideControlIndicator();

        Assert.Equal(config, configured.Config);
        Assert.True(shown.IsVisible);
        Assert.Equal("Testing", shown.Message);
        Assert.False(hidden.IsVisible);
    }

    [Fact]
    public void VisionService_ResolveCaptureGlowBounds_FullScreenFallsBackToAllScreens()
    {
        var service = new VisionService();

        // full_screen spans the whole virtual desktop; the glow should frame
        // every monitor (null) rather than one oversized rectangle.
        Assert.Null(service.ResolveCaptureGlowBounds("full_screen", null));
    }

    [Fact]
    public void VisionService_ResolveCaptureGlowBounds_PrimaryScreenFramesThatScreen()
    {
        var service = new VisionService();

        var bounds = service.ResolveCaptureGlowBounds("primary_screen", null);

        Assert.NotNull(bounds);
        Assert.True(bounds!.Width > 0 && bounds.Height > 0);
    }

    [Fact]
    public void InputService_MouseActionRejectsMissingCoordinates()
    {
        var service = new InputService();

        var exception = Assert.Throws<ArgumentException>(() => service.MouseAction("move", null, null, null, null));

        Assert.Contains("x and y", exception.Message);
    }

    [Fact]
    public void RiskPolicy_ClassifiesAutomationTools()
    {
        var policy = new RiskPolicyService();

        Assert.Equal(RiskLevel.Medium, policy.Classify("mouse_action", new Dictionary<string, object?>()));
        Assert.Equal(RiskLevel.Medium, policy.Classify("capture_screen", new Dictionary<string, object?>()));
        Assert.Equal(RiskLevel.High, policy.Classify("manage_process", new Dictionary<string, object?>()));
    }
}
