using NAudio.CoreAudioApi;

namespace Hotshot.Recording;

public static class AudioDevices
{
    /// <summary>Lists active capture endpoints. Returns an empty list when the audio stack is unavailable.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetMicrophones(Action<string>? failure = null)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var defaultDevice))
            {
                using (defaultDevice)
                {
                    defaultId = defaultDevice.ID;
                }
            }

            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }
        }
        catch (Exception ex)
        {
            if (failure is null) throw new InvalidOperationException("Microphone enumeration failed.", ex);
            failure("Microphone enumeration failed: " + ex.Message);
        }

        return result;
    }
}
