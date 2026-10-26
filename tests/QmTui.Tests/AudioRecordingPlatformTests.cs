using QmTui.Services;
using QmTui.Services.AudioRecognition;
using Xunit;

namespace QmTui.Tests;

public class AudioRecordingPlatformTests
{
    [Fact]
    public void UnsupportedPlatform_CannotStartBackgroundPreRoll()
    {
        if (OperatingSystem.IsLinux()) return;

        // MainWindow starts pre-roll by default. Enabling it must not launch a
        // worker that fails with an unhandled PulseAudio DllNotFoundException.
        AudioPreRollManager.IsEnabled = true;
        AudioPreRollManager.EnsureStarted();

        Assert.False(AudioPreRollManager.IsEnabled);
        Assert.False(AudioPreRollManager.HasReadyPreRoll());
        Assert.Empty(AudioPreRollManager.TakePreRollBytes());
    }

    [Theory]
    [InlineData(AudioRecordSource.SystemInternal)]
    [InlineData(AudioRecordSource.Microphone)]
    public void UnsupportedPlatform_RecordingSessionRemainsInactive(AudioRecordSource source)
    {
        if (OperatingSystem.IsLinux()) return;

        using var session = AudioRecordingService.StartRecordingSession(source);

        Assert.False(session.IsRunning);
        Assert.Empty(session.GetSnapshotSamples());
    }
}
