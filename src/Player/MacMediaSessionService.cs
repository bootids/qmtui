using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// Native macOS Now Playing integration. Managed state may arrive on any thread;
/// AppKit, MediaPlayer and their Objective-C objects are used on the Cocoa main loop.
/// </summary>
public sealed unsafe partial class MacMediaSessionService : IDisposable
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string MediaPlayerFramework = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const long CommandSuccess = 0;
    private const long CommandFailed = 200;

    private static MacMediaSessionService? s_instance;
    private static nint s_stackBlockClass;
    private static ArtworkBlockDescriptor* s_artworkBlockDescriptor;
    private static double s_diagnosticPosition;

    private readonly Lock _stateLock = new();
    private readonly List<(string Command, string Action)> _registeredCommands = [];
    private MediaState _state = MediaState.Empty;
    private Task? _startTask;
    private bool _disposed;
    private bool _isReady;
    private string? _initializationError;
    private string? _artworkError;
    private long _lastPositionUpdateTicks;

    // Native fields are owned exclusively by the Cocoa main thread.
    private nint _mediaPlayerHandle;
    private nint _commandCenter;
    private nint _commandTarget;
    private nint _nowPlayingCenter;
    private nint _currentArtwork;
    private string? _loadedArtworkPath;

    public bool IsReady => Volatile.Read(ref _isReady);
    public string? InitializationError => Volatile.Read(ref _initializationError);

    public Func<Task>? PlayPauseHandler { get; set; }
    public Func<Task>? PlayHandler { get; set; }
    public Func<Task>? PauseHandler { get; set; }
    public Func<Task>? StopHandler { get; set; }
    public Func<Task>? NextHandler { get; set; }
    public Func<Task>? PreviousHandler { get; set; }
    public Func<double, Task>? SetPositionHandler { get; set; }

    private sealed record MediaState(string? TrackKey, string Title, string Artist, string Album,
        double Duration, double Position, long PlaybackState, string? CoverPath)
    {
        internal static MediaState Empty { get; } = new(null, "", "", "", 0, 0, 3, null);
        internal bool HasSong => TrackKey != null;
    }

    public Task StartAsync()
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;
        lock (_stateLock)
        {
            if (_disposed) return Task.CompletedTask;
            if (_startTask != null) return _startTask;
            if (!MacMainLoop.IsInitialized)
            {
                _initializationError = MacMainLoop.InitializationError ?? "Initialize the Cocoa main loop on the original main thread before starting the media session.";
                AppLogger.Error("MacMediaSession", _initializationError);
                return Task.CompletedTask;
            }
            _startTask = MacMainLoop.InvokeAsync(StartOnMainThread);
            return _startTask;
        }
    }

    private void StartOnMainThread()
    {
        MacMainLoop.EnsureMainThread();
        lock (_stateLock) { if (_disposed) return; }
        var pool = MacMainLoop.CreateAutoreleasePool();
        try
        {
            if (s_instance != null && s_instance != this)
                throw new InvalidOperationException("Another qmtui macOS media session is already active.");

            _mediaPlayerHandle = NativeLibrary.Load(MediaPlayerFramework);
            _nowPlayingCenter = Send(Class("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
            _commandCenter = Send(Class("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
            _commandTarget = CreateCommandTarget();
            if (_nowPlayingCenter == 0 || _commandCenter == 0 || _commandTarget == 0)
                throw new InvalidOperationException("MediaPlayer did not expose its command or Now Playing center.");

            AddCommand("playCommand", "qmPlay:");
            AddCommand("pauseCommand", "qmPause:");
            AddCommand("stopCommand", "qmStop:");
            AddCommand("togglePlayPauseCommand", "qmToggle:");
            AddCommand("nextTrackCommand", "qmNext:");
            AddCommand("previousTrackCommand", "qmPrevious:");
            AddCommand("changePlaybackPositionCommand", "qmSetPosition:");
            foreach (var unsupported in new[] { "skipForwardCommand", "skipBackwardCommand", "seekForwardCommand",
                         "seekBackwardCommand", "changePlaybackRateCommand", "changeRepeatModeCommand", "changeShuffleModeCommand",
                         "likeCommand", "dislikeCommand", "bookmarkCommand", "ratingCommand" })
                SetCommandEnabled(unsupported, false);

            s_instance = this;
            _initializationError = null;
            Volatile.Write(ref _isReady, true);
            PublishLatestState(); // Replay everything received before startup completed.
            AppLogger.Info("MacMediaSession", "macOS Now Playing media session started on the native main thread.");
        }
        catch (Exception ex)
        {
            _initializationError = ex.ToString();
            AppLogger.Error("MacMediaSession", "Failed to start macOS media session", ex);
            ReleaseNativeState();
        }
        finally
        {
            MacMainLoop.Release(pool);
        }
    }

    public void UpdatePlaybackStatus(bool isPlaying)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _state = _state with { PlaybackState = isPlaying ? 1 : 2 };
        }
        QueuePublish();
    }

    public void UpdateStopped()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _state = _state with { PlaybackState = 3, Position = 0 };
        }
        QueuePublish();
    }

    public void UpdateSong(Song? song, string? coverPath = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            var key = song == null ? null : GetTrackKey(song);
            var sameTrack = key != null && key == _state.TrackKey;
            _state = song == null ? MediaState.Empty : new MediaState(key, song.Title, song.Artist, song.Album,
                song.Duration > 0 ? song.Duration : sameTrack ? _state.Duration : 0,
                sameTrack ? _state.Position : 0, _state.PlaybackState,
                coverPath ?? (sameTrack ? _state.CoverPath : null));
        }
        QueuePublish();
    }

    private static string GetTrackKey(Song song)
    {
        if (!string.IsNullOrEmpty(song.LocalFilePath)) return $"file:{song.LocalFilePath}";
        if (!string.IsNullOrEmpty(song.WebDavHref)) return $"webdav:{song.WebDavServerId}:{song.WebDavHref}";
        if (!string.IsNullOrEmpty(song.Mid)) return $"mid:{song.Mid}";
        if (song.Id != 0) return $"id:{song.Id}";
        return $"track:{song.Title}\0{song.Artist}\0{song.Album}";
    }

    public void UpdateCover(string? coverPath)
    {
        lock (_stateLock)
        {
            if (_disposed || string.Equals(_state.CoverPath, coverPath, StringComparison.Ordinal)) return;
            _state = _state with { CoverPath = coverPath };
        }
        QueuePublish();
    }

    public void UpdatePosition(double seconds, bool force = false, double durationSeconds = 0)
    {
        var publish = force;
        lock (_stateLock)
        {
            if (_disposed) return;
            var duration = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : _state.Duration;
            _state = _state with { Position = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0, Duration = duration };
            var now = Environment.TickCount64;
            if (force || now - _lastPositionUpdateTicks >= 5_000)
            {
                _lastPositionUpdateTicks = now;
                publish = true;
            }
        }
        if (publish) QueuePublish();
    }

    private void QueuePublish()
    {
        if (IsReady && MacMainLoop.IsInitialized) MacMainLoop.Post(PublishLatestState);
    }

    private void PublishLatestState()
    {
        MacMainLoop.EnsureMainThread();
        MediaState state;
        lock (_stateLock)
        {
            if (_disposed || !IsReady || _nowPlayingCenter == 0) return;
            state = _state;
        }
        var pool = MacMainLoop.CreateAutoreleasePool();
        try
        {
            UpdateCommandAvailability(state);
            ReplaceArtwork(state.CoverPath);
            if (!state.HasSong)
            {
                SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), 0);
                SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), 3);
                return;
            }

            var dictionary = Send(Class("NSMutableDictionary"), Sel("dictionary"));
            AddString(dictionary, "MPMediaItemPropertyTitle", state.Title);
            AddString(dictionary, "MPMediaItemPropertyArtist", state.Artist);
            AddString(dictionary, "MPMediaItemPropertyAlbumTitle", state.Album);
            AddNumber(dictionary, "MPMediaItemPropertyPlaybackDuration", state.Duration);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyElapsedPlaybackTime", state.Position);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyPlaybackRate", state.PlaybackState == 1 ? 1 : 0);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyDefaultPlaybackRate", 1);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyMediaType", 1); // Audio
            if (_currentArtwork != 0) AddObject(dictionary, "MPMediaItemPropertyArtwork", _currentArtwork);

            SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), dictionary);
            // macOS requires explicit state in addition to the metadata/rate.
            SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), state.PlaybackState);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Failed to publish Now Playing state", ex);
        }
        finally
        {
            MacMainLoop.Release(pool);
        }
    }

    private void UpdateCommandAvailability(MediaState state)
    {
        SetCommandEnabled("playCommand", state.HasSong && PlayHandler != null);
        SetCommandEnabled("pauseCommand", state.HasSong && PauseHandler != null);
        SetCommandEnabled("stopCommand", state.HasSong && StopHandler != null);
        SetCommandEnabled("togglePlayPauseCommand", state.HasSong && PlayPauseHandler != null);
        SetCommandEnabled("nextTrackCommand", state.HasSong && NextHandler != null);
        SetCommandEnabled("previousTrackCommand", state.HasSong && PreviousHandler != null);
        SetCommandEnabled("changePlaybackPositionCommand", state.HasSong && state.Duration > 0 && SetPositionHandler != null);
    }

    private void ReplaceArtwork(string? coverPath)
    {
        if (string.Equals(_loadedArtworkPath, coverPath, StringComparison.Ordinal)) return;
        MacMainLoop.Release(_currentArtwork);
        _currentArtwork = 0;
        _loadedArtworkPath = coverPath;
        _artworkError = null;
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return;

        try
        {
            var image = SendPtr(Send(Class("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), NativeString(Path.GetFullPath(coverPath)));
            if (image == 0) throw new InvalidOperationException("NSImage could not decode the cover image.");
            try
            {
                var size = SendSizeReturn(image, Sel("size"));
                if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("The cover image has no valid size.");
                var artwork = Send(Class("MPMediaItemArtwork"), Sel("alloc"));
                if (!SendBoolReturnPtr(artwork, Sel("respondsToSelector:"), Sel("initWithBoundsSize:requestHandler:")))
                {
                    MacMainLoop.Release(artwork);
                    throw new InvalidOperationException("MediaPlayer does not support macOS artwork initialization.");
                }
                EnsureArtworkBlockDescriptor();
                var block = new ArtworkBlock
                {
                    Isa = s_stackBlockClass,
                    Flags = (1 << 25) | (1 << 30), // BLOCK_HAS_COPY_DISPOSE | BLOCK_HAS_SIGNATURE
                    Invoke = &GetArtworkImage,
                    Descriptor = s_artworkBlockDescriptor,
                    Image = image
                };
                // initWithImage: is an iOS-only API. The macOS initializer copies
                // this block; its copy/dispose helpers own the NSImage reference.
                _currentArtwork = SendSizeBlock(artwork, Sel("initWithBoundsSize:requestHandler:"), size, (nint)(&block));
                if (_currentArtwork == 0) throw new InvalidOperationException("MediaPlayer could not create artwork.");
            }
            finally
            {
                MacMainLoop.Release(image);
            }
        }
        catch (Exception ex)
        {
            _artworkError = ex.ToString();
            AppLogger.Error("MacMediaSession", "Could not publish artwork; text and playback controls remain available", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArtworkBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged[Cdecl]<ArtworkBlock*, NativeSize, nint> Invoke;
        public ArtworkBlockDescriptor* Descriptor;
        public nint Image;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArtworkBlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public delegate* unmanaged[Cdecl]<ArtworkBlock*, ArtworkBlock*, void> Copy;
        public delegate* unmanaged[Cdecl]<ArtworkBlock*, void> Dispose;
        public nint Signature;
    }

    private static void EnsureArtworkBlockDescriptor()
    {
        if (s_artworkBlockDescriptor != null) return;
        s_stackBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock");
        var descriptor = (ArtworkBlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(ArtworkBlockDescriptor));
        descriptor->Size = (nuint)sizeof(ArtworkBlock);
        descriptor->Copy = &CopyArtworkBlock;
        descriptor->Dispose = &DisposeArtworkBlock;
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8("@24@?0{CGSize=dd}8");
        // Process-lifetime descriptor: copied Objective-C blocks can outlive a song.
        s_artworkBlockDescriptor = descriptor;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CopyArtworkBlock(ArtworkBlock* destination, ArtworkBlock* source)
    {
        destination->Image = Send(source->Image, Sel("retain"));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DisposeArtworkBlock(ArtworkBlock* block) => MacMainLoop.Release(block->Image);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetArtworkImage(ArtworkBlock* block, NativeSize requestedSize) => block->Image;

    private static nint CreateCommandTarget()
    {
        const string targetClassName = "QmRemoteCommandTarget";
        var targetClass = Class(targetClassName);
        if (targetClass == 0)
        {
            targetClass = objc_allocateClassPair(Class("NSObject"), targetClassName, 0);
            if (targetClass == 0) throw new InvalidOperationException("Could not allocate the remote command target class.");
            AddMethod(targetClass, "qmPlay:", &OnPlay);
            AddMethod(targetClass, "qmPause:", &OnPause);
            AddMethod(targetClass, "qmStop:", &OnStop);
            AddMethod(targetClass, "qmToggle:", &OnToggle);
            AddMethod(targetClass, "qmNext:", &OnNext);
            AddMethod(targetClass, "qmPrevious:", &OnPrevious);
            AddMethod(targetClass, "qmSetPosition:", &OnSetPosition);
            objc_registerClassPair(targetClass);
        }
        return Send(Send(targetClass, Sel("alloc")), Sel("init"));
    }

    private static void AddMethod(nint targetClass, string selector, delegate* unmanaged[Cdecl]<nint, nint, nint, nint> implementation)
    {
        if (!class_addMethod(targetClass, Sel(selector), (nint)implementation, "q@:@"))
            throw new InvalidOperationException($"Could not register Objective-C selector {selector}.");
    }

    private void AddCommand(string commandSelector, string actionSelector)
    {
        var command = Send(_commandCenter, Sel(commandSelector));
        if (command == 0) throw new InvalidOperationException($"MediaPlayer command {commandSelector} is unavailable.");
        SendTwoPtr(command, Sel("addTarget:action:"), _commandTarget, Sel(actionSelector));
        _registeredCommands.Add((commandSelector, actionSelector));
    }

    private void SetCommandEnabled(string selector, bool enabled)
    {
        var command = Send(_commandCenter, Sel(selector));
        if (command != 0) SendBool(command, Sel("setEnabled:"), enabled);
    }

    private void AddString(nint dictionary, string keySymbol, string value)
    {
        if (!string.IsNullOrEmpty(value)) AddObject(dictionary, keySymbol, NativeString(value));
    }

    private void AddNumber(nint dictionary, string keySymbol, double value) =>
        AddObject(dictionary, keySymbol, SendDouble(Class("NSNumber"), Sel("numberWithDouble:"), value));

    private void AddObject(nint dictionary, string keySymbol, nint value) =>
        SendTwoPtr(dictionary, Sel("setObject:forKey:"), value, FrameworkConstant(keySymbol));

    private nint FrameworkConstant(string symbol)
    {
        if (!NativeLibrary.TryGetExport(_mediaPlayerHandle, symbol, out var address))
            throw new EntryPointNotFoundException($"MediaPlayer metadata constant {symbol} is unavailable.");
        var value = Marshal.ReadIntPtr(address);
        return value != 0 ? value : throw new InvalidOperationException($"MediaPlayer metadata constant {symbol} is null.");
    }

    private static nint NativeString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return SendPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);

    private static nint Invoke(Func<Task>? handler)
    {
        var instance = Volatile.Read(ref s_instance);
        if (instance == null || !instance.IsReady || handler == null) return (nint)CommandFailed;
        try
        {
            if (MacMainLoop.IsMainThread) return InvokeOnMainThread(instance, handler);
            // MediaPlayer may deliver callbacks on another queue. Accept the
            // command here and execute all application work on the native main loop.
            MacMainLoop.Post(() => InvokeOnMainThread(instance, handler));
            return (nint)CommandSuccess;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Remote command handler failed", ex);
            return (nint)CommandFailed;
        }
    }

    private static nint InvokeOnMainThread(MacMediaSessionService instance, Func<Task> handler)
    {
        MacMainLoop.EnsureMainThread();
        lock (instance._stateLock)
        {
            if (instance._disposed || s_instance != instance || !instance.IsReady) return (nint)CommandFailed;
        }
        try
        {
            var task = handler();
            if (!task.IsCompletedSuccessfully) ObserveCommand(task);
            return (nint)CommandSuccess;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Remote command handler failed", ex);
            return (nint)CommandFailed;
        }
    }

    private static void ObserveCommand(Task task)
    {
        _ = task.ContinueWith(completed =>
            AppLogger.Error("MacMediaSession", "Asynchronous remote command failed", completed.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPlay(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PlayHandler);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPause(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PauseHandler);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnStop(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.StopHandler);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnToggle(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PlayPauseHandler);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnNext(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.NextHandler);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPrevious(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PreviousHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnSetPosition(nint self, nint command, nint mediaEvent)
    {
        var instance = Volatile.Read(ref s_instance);
        var handler = instance?.SetPositionHandler;
        if (instance == null || handler == null || !instance.IsReady || mediaEvent == 0) return (nint)CommandFailed;
        try
        {
            if (MacMainLoop.IsMainThread)
                return InvokeOnMainThread(instance, () => handler(SendDoubleReturn(mediaEvent, Sel("positionTime"))));

            // Retain the event before the native callback returns; read its
            // position only after dispatch reaches the Cocoa main thread.
            var retainedEvent = Send(mediaEvent, Sel("retain"));
            try
            {
                MacMainLoop.Post(() =>
                {
                    try { InvokeOnMainThread(instance, () => handler(SendDoubleReturn(retainedEvent, Sel("positionTime")))); }
                    finally { MacMainLoop.Release(retainedEvent); }
                });
            }
            catch
            {
                MacMainLoop.Release(retainedEvent);
                throw;
            }
            return (nint)CommandSuccess;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Position command handler failed", ex);
            return (nint)CommandFailed;
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _state = MediaState.Empty;
        }
        if (OperatingSystem.IsMacOS() && MacMainLoop.IsInitialized) MacMainLoop.Post(ReleaseNativeState);
    }

    private void ReleaseNativeState()
    {
        MacMainLoop.EnsureMainThread();
        var pool = MacMainLoop.CreateAutoreleasePool();
        try
        {
            var ownedSession = s_instance == this;
            Volatile.Write(ref _isReady, false);
            if (ownedSession) s_instance = null;
            foreach (var registration in _registeredCommands)
            {
                var command = Send(_commandCenter, Sel(registration.Command));
                if (command == 0) continue;
                SendTwoPtr(command, Sel("removeTarget:action:"), _commandTarget, Sel(registration.Action));
                SendBool(command, Sel("setEnabled:"), false);
            }
            _registeredCommands.Clear();
            if (ownedSession && _nowPlayingCenter != 0)
            {
                SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), 0);
                SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), 3);
            }
            MacMainLoop.Release(_currentArtwork);
            _currentArtwork = 0;
            _loadedArtworkPath = null;
            MacMainLoop.Release(_commandTarget);
            _commandTarget = 0;
            // Keep borrowed singleton handles for diagnostic readback after disposal.
        }
        finally
        {
            MacMainLoop.Release(pool);
        }
    }

    internal sealed record NativeSnapshot(bool IsReady, string? InitializationError, string? Title, string? Artist,
        string? Album, double DurationSeconds, double ElapsedSeconds, double PlaybackRate, long PlaybackState,
        bool HasArtwork, bool ArtworkImageAvailable, bool PlayEnabled, bool PauseEnabled, bool ToggleEnabled, bool NextEnabled,
        bool PreviousEnabled, bool StopEnabled, bool SeekEnabled, string? ArtworkError);

    internal NativeSnapshot CaptureNativeSnapshot()
    {
        MacMainLoop.EnsureMainThread();
        var pool = MacMainLoop.CreateAutoreleasePool();
        try
        {
            var dictionary = _nowPlayingCenter == 0 ? 0 : Send(_nowPlayingCenter, Sel("nowPlayingInfo"));
            nint Value(string symbol) => dictionary == 0 ? 0 : SendPtr(dictionary, Sel("objectForKey:"), FrameworkConstant(symbol));
            string? Text(string symbol)
            {
                var value = Value(symbol);
                return value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String")));
            }
            double Number(string symbol)
            {
                var value = Value(symbol);
                return value == 0 ? 0 : SendDoubleReturn(value, Sel("doubleValue"));
            }
            bool Enabled(string selector)
            {
                var command = _commandCenter == 0 ? 0 : Send(_commandCenter, Sel(selector));
                return command != 0 && SendBoolReturn(command, Sel("isEnabled"));
            }
            var artwork = Value("MPMediaItemPropertyArtwork");
            // Exercise the escaping request-handler block after the publication
            // autorelease pool has drained, not merely the artwork dictionary key.
            var artworkImage = artwork == 0 ? 0 : SendSize(artwork, Sel("imageWithSize:"), new NativeSize { Width = 24, Height = 24 });
            var artworkImageSize = artworkImage == 0 ? default : SendSizeReturn(artworkImage, Sel("size"));
            return new NativeSnapshot(IsReady, InitializationError, Text("MPMediaItemPropertyTitle"),
                Text("MPMediaItemPropertyArtist"), Text("MPMediaItemPropertyAlbumTitle"),
                Number("MPMediaItemPropertyPlaybackDuration"), Number("MPNowPlayingInfoPropertyElapsedPlaybackTime"),
                Number("MPNowPlayingInfoPropertyPlaybackRate"), _nowPlayingCenter == 0 ? 0 : (long)Send(_nowPlayingCenter, Sel("playbackState")),
                artwork != 0, artworkImage != 0 && artworkImageSize.Width > 0 && artworkImageSize.Height > 0,
                Enabled("playCommand"), Enabled("pauseCommand"),
                Enabled("togglePlayPauseCommand"), Enabled("nextTrackCommand"), Enabled("previousTrackCommand"),
                Enabled("stopCommand"), Enabled("changePlaybackPositionCommand"), _artworkError);
        }
        finally
        {
            MacMainLoop.Release(pool);
        }
    }

    // Tests the Objective-C selectors actually registered with MPRemoteCommandCenter.
    // It does not synthesize a system media-key event or claim Control Center UI coverage.
    internal long SendDiagnosticCommand(string command, double position = 0)
    {
        MacMainLoop.EnsureMainThread();
        if (!IsReady || _commandTarget == 0) return CommandFailed;
        var selector = command switch
        {
            "play" => "qmPlay:", "pause" => "qmPause:", "stop" => "qmStop:", "toggle" => "qmToggle:",
            "next" => "qmNext:", "previous" => "qmPrevious:", "seek" => "qmSetPosition:",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        nint mediaEvent = 0;
        try
        {
            if (command == "seek")
            {
                const string eventClassName = "QmDiagnosticPlaybackPositionEvent";
                var eventClass = Class(eventClassName);
                if (eventClass == 0)
                {
                    // Framework command events have no public initializer. A
                    // synthetic NSObject implements the one selector our handler
                    // consumes, without invoking unsupported framework init paths.
                    eventClass = objc_allocateClassPair(Class("NSObject"), eventClassName, 0);
                    if (eventClass == 0 || !class_addMethod(eventClass, Sel("positionTime"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, double>)&DiagnosticPosition, "d@:"))
                        throw new InvalidOperationException("Could not create a diagnostic position-event subclass.");
                    objc_registerClassPair(eventClass);
                }
                s_diagnosticPosition = position;
                mediaEvent = Send(Send(eventClass, Sel("alloc")), Sel("init"));
                if (mediaEvent == 0) throw new InvalidOperationException("Could not create a diagnostic position event.");
            }
            return (long)SendPtr(_commandTarget, Sel(selector), mediaEvent);
        }
        finally
        {
            MacMainLoop.Release(mediaEvent);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double DiagnosticPosition(nint self, nint selector) => s_diagnosticPosition;

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);
    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);
    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
    [LibraryImport(LibObjC)]
    private static partial void objc_registerClassPair(nint cls);
    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector, nint value);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendTwoPtr(nint receiver, nint selector, nint first, nint second);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendDouble(nint receiver, nint selector, double value);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial double SendDoubleReturn(nint receiver, nint selector);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendLong(nint receiver, nint selector, long value);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBoolReturn(nint receiver, nint selector);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBoolReturnPtr(nint receiver, nint selector, nint value);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial NativeSize SendSizeReturn(nint receiver, nint selector);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendSize(nint receiver, nint selector, NativeSize size);
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendSizeBlock(nint receiver, nint selector, NativeSize size, nint block);
}
