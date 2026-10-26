using System.Diagnostics;
using QmTui.Models;

namespace QmTui.Player;

/// <summary>
/// Runs on the process main thread and exercises the public native MediaPlayer
/// bridge. No account, network request, audio output, or UI automation is used.
/// </summary>
internal static class MacMediaSessionDiagnostics
{
    internal static int Run()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("The media-session check is available on macOS only.");
            return 2;
        }

        MacMediaSessionService? session = null;
        string? temporaryDirectory = null;
        try
        {
            if (!MacMainLoop.Initialize())
            {
                throw new InvalidOperationException(MacMainLoop.InitializationError ?? "Cocoa main-loop initialization failed.");
            }
            Require(MacMainLoop.IsMainThread, "The diagnostic is not running on the native main thread.");

            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"qmtui media check {Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var coverPath = Path.Combine(temporaryDirectory, "test cover.png");
            WriteCover(coverPath);
            session = new MacMediaSessionService();

            // Updates originate on a worker just as playback/cover tasks do in
            // the app. Only the real process main thread pumps Cocoa.
            var checks = Task.Run(() => CheckSessionAsync(session, coverPath));
            var timeout = Stopwatch.StartNew();
            while (!checks.IsCompleted)
            {
                MacMainLoop.Pump();
                if (timeout.Elapsed > TimeSpan.FromSeconds(25))
                {
                    throw new TimeoutException("The native main loop did not deliver the media-session work within 25 seconds.");
                }
                Thread.Sleep(5);
            }
            checks.GetAwaiter().GetResult();
            Require(MacMainLoop.PumpCount > 0, "The native main loop was never pumped.");

            Console.WriteLine("PASS: macOS native main queue, Now Playing metadata/artwork, play/pause/stop, track controls, seek, and cleanup.");
            Console.WriteLine("The check briefly published synthetic metadata and cleared it. No audio was played.");
            Console.WriteLine("This validates the native API and registered command callbacks; it does not automate Control Center or physical media keys.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: macOS media session: {ex.Message}");
            return 1;
        }
        finally
        {
            session?.Dispose();
            if (MacMainLoop.IsInitialized) MacMainLoop.Pump();
            if (temporaryDirectory != null && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task CheckSessionAsync(MacMediaSessionService session, string coverPath)
    {
        bool callbacksOnMain = true;
        bool playing = true;
        var calls = new Dictionary<string, int>();
        var song = new Song("qmtui-media-diagnostic", "qmtui 媒体会话测试", "Test Artist", "Test Album", 0);

        Task Callback(string name, Action action)
        {
            callbacksOnMain &= MacMainLoop.IsMainThread;
            calls[name] = calls.GetValueOrDefault(name) + 1;
            action();
            return Task.CompletedTask;
        }

        session.PlayHandler = () => Callback("play", () =>
        {
            playing = true;
            session.UpdatePlaybackStatus(true);
        });
        session.PauseHandler = () => Callback("pause", () =>
        {
            playing = false;
            session.UpdatePlaybackStatus(false);
        });
        session.PlayPauseHandler = () => Callback("toggle", () =>
        {
            playing = !playing;
            session.UpdatePlaybackStatus(playing);
        });
        session.StopHandler = () => Callback("stop", () =>
        {
            playing = false;
            session.UpdatePosition(0, force: true);
            session.UpdateStopped();
        });
        session.NextHandler = () => Callback("next", () =>
        {
            session.UpdateSong(song with { Mid = "next", Title = "Next diagnostic track", Duration = 180 }, coverPath);
        });
        session.PreviousHandler = () => Callback("previous", () => session.UpdateSong(song, coverPath));
        session.SetPositionHandler = position => Callback("seek", () => session.UpdatePosition(position, force: true));

        // Restore-before-start is a real startup path. A worker must be able to
        // queue the complete state before the native service is ready.
        session.UpdateSong(song, coverPath);
        session.UpdatePosition(12.5, force: true, durationSeconds: 150);
        session.UpdatePlaybackStatus(true);
        await session.StartAsync().ConfigureAwait(false);
        var snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(snapshot.IsReady, snapshot.InitializationError ?? "The native media session did not start.");
        Require(snapshot.Title == song.Title && snapshot.Artist == song.Artist && snapshot.Album == song.Album,
            "Now Playing did not preserve the restored Unicode title, artist, and album.");
        Near(snapshot.DurationSeconds, 150, "duration from the decoder");
        Near(snapshot.ElapsedSeconds, 12.5, "restored playback position");
        Require(snapshot.HasArtwork, "The supported macOS artwork initializer did not publish the cover.");
        Require(snapshot.ArtworkImageAvailable, $"The retained artwork request handler did not return a valid image: {snapshot.ArtworkError}");
        Require(snapshot.PlayEnabled && snapshot.PauseEnabled && snapshot.ToggleEnabled && snapshot.StopEnabled &&
                snapshot.NextEnabled && snapshot.PreviousEnabled && snapshot.SeekEnabled,
            "One or more remote commands were not enabled.");
        State(snapshot, 1, 1, "initial playing state");

        await SendAsync(session, "pause").ConfigureAwait(false);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        State(snapshot, 2, 0, "pause command");
        await SendAsync(session, "seek", 42.25).ConfigureAwait(false);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Near(snapshot.ElapsedSeconds, 42.25, "immediate paused seek");
        State(snapshot, 2, 0, "paused state after seek");

        // Metadata enrichment must not reset an already playing/paused track.
        song.Duration = 151;
        session.UpdateSong(song, coverPath);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Near(snapshot.ElapsedSeconds, 42.25, "position after same-track metadata refresh");
        Near(snapshot.DurationSeconds, 151, "updated track duration");
        Require(snapshot.HasArtwork, "A metadata refresh removed the current cover.");
        Require(snapshot.ArtworkImageAvailable, "The artwork request handler stopped working after a metadata refresh.");

        await SendAsync(session, "play").ConfigureAwait(false);
        State(await CaptureAsync(session).ConfigureAwait(false), 1, 1, "play command");
        await SendAsync(session, "toggle").ConfigureAwait(false);
        State(await CaptureAsync(session).ConfigureAwait(false), 2, 0, "toggle-to-pause command");
        await SendAsync(session, "toggle").ConfigureAwait(false);
        State(await CaptureAsync(session).ConfigureAwait(false), 1, 1, "toggle-to-play command");

        await SendAsync(session, "next").ConfigureAwait(false);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(snapshot.Title == "Next diagnostic track", "Next-track callback did not publish the new title.");
        Near(snapshot.ElapsedSeconds, 0, "next-track initial position");
        State(snapshot, 1, 1, "next-track command");
        await SendAsync(session, "previous").ConfigureAwait(false);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(snapshot.Title == song.Title, "Previous-track callback did not publish the previous title.");
        State(snapshot, 1, 1, "previous-track command");

        await SendAsync(session, "stop").ConfigureAwait(false);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        State(snapshot, 3, 0, "explicit stop command");
        Near(snapshot.ElapsedSeconds, 0, "stopped position");
        Require(callbacksOnMain, "A native command callback ran outside the process main thread.");
        foreach (string command in new[] { "play", "pause", "stop", "toggle", "next", "previous", "seek" })
        {
            Require(calls.GetValueOrDefault(command) == (command == "toggle" ? 2 : 1),
                $"The registered native {command} callback was not called exactly as requested.");
        }

        session.UpdateSong(null);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(string.IsNullOrEmpty(snapshot.Title) && !snapshot.HasArtwork, "Clearing the song left stale Now Playing metadata.");
        // Dispose must also clear a live session, not merely flip managed flags
        // after metadata was already removed by UpdateSong(null).
        session.UpdateSong(song, coverPath);
        session.UpdatePlaybackStatus(true);
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(snapshot.Title == song.Title && snapshot.HasArtwork, "The cleanup test did not republish its live track.");
        session.Dispose();
        snapshot = await CaptureAsync(session).ConfigureAwait(false);
        Require(!snapshot.IsReady, "The disposed media session remained ready.");
        Require(string.IsNullOrEmpty(snapshot.Title) && !snapshot.HasArtwork && snapshot.PlaybackState == 3,
            "Disposal left active metadata or playback state in the native Now Playing center.");
        Require(!snapshot.PlayEnabled && !snapshot.PauseEnabled && !snapshot.ToggleEnabled && !snapshot.StopEnabled &&
                !snapshot.NextEnabled && !snapshot.PreviousEnabled && !snapshot.SeekEnabled,
            "Disposal left native remote commands enabled.");
    }

    private static async Task<MacMediaSessionService.NativeSnapshot> CaptureAsync(MacMediaSessionService session)
    {
        MacMediaSessionService.NativeSnapshot? snapshot = null;
        await MacMainLoop.InvokeAsync(() => snapshot = session.CaptureNativeSnapshot()).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("The native state readback did not run.");
    }

    private static async Task SendAsync(MacMediaSessionService session, string command, double position = 0)
    {
        long result = -1;
        // The same native main queue used by MediaPlayer must actually be
        // serviced. A direct managed-handler invocation would miss the bug.
        await MacMainLoop.PostToNativeQueueAsync(() => result = session.SendDiagnosticCommand(command, position)).ConfigureAwait(false);
        Require(result == 0, $"The native {command} command returned status {result}.");
    }

    private static void State(MacMediaSessionService.NativeSnapshot snapshot, long state, double rate, string description)
    {
        Require(snapshot.PlaybackState == state, $"Incorrect macOS playback state after {description}: {snapshot.PlaybackState}.");
        Near(snapshot.PlaybackRate, rate, $"playback rate after {description}");
    }

    private static void Near(double actual, double expected, string description) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < 0.01,
            $"Incorrect {description}: expected {expected}, got {actual}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteCover(string path)
    {
        var pixels = new byte[24 * 24 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 48;
            pixels[i + 1] = 180;
            pixels[i + 2] = 160;
        }
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, 24, 24,
            StbImageWriteSharp.ColorComponents.RedGreenBlue, stream);
    }
}
