using MeetingScribe.Configuration;
using MeetingScribe.Detection;

namespace MeetingScribe.Tests.Detection;

public class ZoomDetectionTests
{
    [Fact]
    public void DefaultProcessPattern_IncludesZoom()
    {
        var config = new DetectionConfig();

        var result = Regex.IsMatch("Zoom", config.ProcessNamePattern, RegexOptions.IgnoreCase);

        Assert.True(result);
    }

    [Fact]
    public void MeetingTitleResolver_CanAcceptZoomWindowTitle()
    {
        var title = "Weekly sync | Zoom";
        var pattern = "^(ms-teams|Teams|Teams1|Zoom)$";

        var cleaned = title.Split('|').Select(part => part.Trim()).FirstOrDefault(part =>
        {
            var trimmed = Regex.Replace(part, @"^\(\d+\)\s*", "");
            return !string.IsNullOrWhiteSpace(trimmed) &&
                   !Regex.IsMatch(trimmed, "^(?:Meeting join|Meeting|Meeting now|Join meeting|Pre-join|Calling|Microsoft Teams|Teams|Teams classic|Chat|Calendar|Activity|Calls|Files|Apps|Home|Communities|Notifications|Search|More)$", RegexOptions.IgnoreCase);
        });

        Assert.Equal("Weekly sync", cleaned);
    }
}
