using System.Diagnostics;
using System.Text;

namespace QmTui.Player;

/// <summary>
/// Exercises the same native playback path as the TUI without network access,
/// credentials, an interactive terminal, or audible output.
/// </summary>
internal static class GStreamerDiagnostics
{
    internal static async Task<int> RunAsync()
    {
        var previousSink = Environment.GetEnvironmentVariable("GST_AUDIO_SINK");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"qmtui audio check {Guid.NewGuid():N}");
        try
        {
            GStreamerNative.Initialize();
            Environment.SetEnvironmentVariable("GST_AUDIO_SINK", "fakesink sync=true");
            Directory.CreateDirectory(temporaryDirectory);
            var wavePath = Path.Combine(temporaryDirectory, "test audio.wav");
            WriteSilentWave(wavePath);

            using var player = new GstPlayer();
            player.Initialize();
            Console.WriteLine($"GStreamer library: {GStreamerNative.LoadedGStreamerPath}");
            if (!player.IsAvailable)
            {
                throw new InvalidOperationException($"GStreamer could not initialize its playback pipeline.{Environment.NewLine}" +
                    (player.InitializationError ?? "No additional initialization error was reported."));
            }
            if (OperatingSystem.IsMacOS() && !GStreamerNative.HasElementFactory("osxaudiosink"))
            {
                throw new InvalidOperationException("The macOS CoreAudio plugin (osxaudiosink) is missing.");
            }

            // Passing duration=0 requires the native decoder to discover the WAV duration.
            await player.PlayAsync(wavePath, duration: 0).ConfigureAwait(false);
            await WaitForAsync(() => player.CurrentPositionSeconds >= 0.2 &&
                                     Math.Abs(player.TotalDurationSeconds - 4) < 0.1,
                "WAV decoding or playback did not advance").ConfigureAwait(false);

            await player.TogglePauseAsync().ConfigureAwait(false);
            await player.SeekAsync(2.5).ConfigureAwait(false);
            // Query the native pipeline while paused: the managed SeekAsync method
            // updates its cached position even when a native seek fails.
            await WaitForAsync(() => player.TryGetNativePosition(out var position) && Math.Abs(position - 2.5) < 0.05,
                "The native pipeline did not seek to the requested position").ConfigureAwait(false);
            await player.TogglePauseAsync().ConfigureAwait(false);
            await WaitForAsync(() => player.CurrentPositionSeconds >= 2.7 && player.CurrentPositionSeconds < 4,
                "Native playback did not resume after seeking").ConfigureAwait(false);
            await player.StopAsync().ConfigureAwait(false);

            Console.WriteLine("PASS: GStreamer initialized, decoded a WAV, advanced playback, and resumed after seeking.");
            Console.WriteLine("Audio check used a silent fakesink; physical speakers/headphones were not tested.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Audio check: {ex.Message}");
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GST_AUDIO_SINK", previousSink);
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string failureMessage)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failureMessage);
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static void WriteSilentWave(string path)
    {
        const int sampleRate = 48_000;
        const int bytesPerSample = 2;
        const int dataSize = sampleRate * bytesPerSample * 4;
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(dataSize + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytesPerSample);
        writer.Write((short)bytesPerSample);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }
}
