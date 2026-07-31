using System.Runtime.Versioning;
using MeetingScribe.Infrastructure;
using NAudio.CoreAudioApi;

namespace MeetingScribe.Recording;

/// <summary>Resolves the playback/capture endpoints to record from.</summary>
[SupportedOSPlatform("windows")]
internal static class DeviceLocator
{
    public static MMDevice? FindRender(string nameFilter) =>
        Find(DataFlow.Render, Role.Multimedia, nameFilter);

    public static MMDevice? FindCapture(string nameFilter) =>
        Find(DataFlow.Capture, Role.Communications, nameFilter);

    private static MMDevice? Find(DataFlow flow, Role role, string nameFilter)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    if (device.FriendlyName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) return device;
                    device.Dispose();
                }

                Log.Warn($"No {flow} device matching '{nameFilter}'; falling back to the default device.");
            }

            return enumerator.HasDefaultAudioEndpoint(flow, role)
                ? enumerator.GetDefaultAudioEndpoint(flow, role)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not resolve a {flow} device", ex);
            return null;
        }
    }

    public static IEnumerable<string> ListDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                yield return device.FriendlyName;
            }
        }
    }
}
