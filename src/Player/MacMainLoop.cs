using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// Hosts Cocoa on the original macOS main thread while Terminal.Gui owns the UI
/// loop. Program initializes this synchronously and pumps it from a UI timeout.
/// </summary>
internal static unsafe partial class MacMainLoop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string LibDispatch = "/usr/lib/system/libdispatch.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static nint s_defaultRunLoopMode;
    private static nint s_mainQueue;
    private static bool s_initialized;
    private static int s_pendingCount;
    private static long s_pumpCount;
    private static bool s_isPumping;

    internal static bool IsInitialized => Volatile.Read(ref s_initialized);
    internal static bool IsMainThread => OperatingSystem.IsMacOS() && pthread_main_np() != 0;
    internal static string? InitializationError { get; private set; }
    internal static int PendingCount => Volatile.Read(ref s_pendingCount);
    internal static long PumpCount => Interlocked.Read(ref s_pumpCount);

    internal static bool Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (IsInitialized) return true;
        if (!IsMainThread)
        {
            InitializationError = "Cocoa must be initialized on the original macOS main thread.";
            AppLogger.Error("MacMainLoop", InitializationError);
            return false;
        }

        nint pool = 0;
        try
        {
            NativeLibrary.Load(AppKit);
            pool = CreateAutoreleasePool();
            var coreFoundation = NativeLibrary.Load(CoreFoundation);
            s_defaultRunLoopMode = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFRunLoopDefaultMode"));
            var dispatch = NativeLibrary.Load(LibDispatch);
            // dispatch_get_main_queue() is an inline accessor for this object.
            s_mainQueue = NativeLibrary.GetExport(dispatch, "_dispatch_main_q");
            var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (application == 0 || s_defaultRunLoopMode == 0 || s_mainQueue == 0)
            {
                throw new InvalidOperationException("The Cocoa application or main run loop is unavailable.");
            }
            if (!SendPolicy(application, sel_registerName("setActivationPolicy:"), 1))
            {
                throw new InvalidOperationException("Cocoa refused the accessory application activation policy.");
            }
            Send(application, sel_registerName("finishLaunching"));
            InitializationError = null;
            Volatile.Write(ref s_initialized, true);
            AppLogger.Info("MacMainLoop", "Cocoa accessory application initialized on the native main thread.");
            return true;
        }
        catch (Exception ex)
        {
            InitializationError = ex.ToString();
            AppLogger.Error("MacMainLoop", "Cocoa initialization failed; terminal playback remains available", ex);
            return false;
        }
        finally
        {
            Release(pool);
        }
    }

    internal static void Pump()
    {
        if (!IsInitialized) return;
        EnsureMainThread();
        if (s_isPumping) return;
        s_isPumping = true;
        nint pool = 0;
        try
        {
            pool = CreateAutoreleasePool();
            // Run ready Cocoa/GCD sources without blocking terminal input. Bound
            // the drain so a busy native source cannot monopolize the TUI loop.
            for (var i = 0; i < 8; i++)
            {
                if (CFRunLoopRunInMode(s_defaultRunLoopMode, 0, true) != 4) break;
            }
            Interlocked.Increment(ref s_pumpCount);
        }
        finally
        {
            Release(pool);
            s_isPumping = false;
        }
    }

    internal static void EnsureMainThread()
    {
        if (!IsMainThread) throw new InvalidOperationException("This Cocoa operation must run on the native main thread.");
    }

    internal static void Post(Action action)
    {
        var task = InvokeAsync(action);
        if (!task.IsCompletedSuccessfully) ReportFailure(task);
    }

    internal static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsInitialized) return Task.FromException(new InvalidOperationException(InitializationError ?? "Cocoa main loop has not been initialized."));
        if (!IsMainThread) return PostToNativeQueueAsync(action);
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    // Always enqueue, including on the main thread, so diagnostics verify that
    // the real native dispatch queue is serviced by the production event pump.
    internal static Task PostToNativeQueueAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsInitialized) return Task.FromException(new InvalidOperationException(InitializationError ?? "Cocoa main loop has not been initialized."));
        var work = new QueuedWork(action);
        var handle = GCHandle.Alloc(work);
        Interlocked.Increment(ref s_pendingCount);
        try
        {
            dispatch_async_f(s_mainQueue, GCHandle.ToIntPtr(handle), &ExecuteWork);
            return work.Completion.Task;
        }
        catch
        {
            Interlocked.Decrement(ref s_pendingCount);
            handle.Free();
            throw;
        }
    }

    private sealed class QueuedWork(Action action)
    {
        internal Action Action { get; } = action;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ExecuteWork(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var work = (QueuedWork)handle.Target!;
        nint pool = 0;
        try
        {
            EnsureMainThread();
            pool = CreateAutoreleasePool();
            work.Action();
            work.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            work.Completion.TrySetException(ex);
        }
        finally
        {
            Release(pool);
            handle.Free();
            Interlocked.Decrement(ref s_pendingCount);
        }
    }

    private static void ReportFailure(Task task)
    {
        _ = task.ContinueWith(completed =>
            AppLogger.Error("MacMainLoop", "Queued Cocoa operation failed", completed.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static nint CreateAutoreleasePool() =>
        Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));

    internal static void Release(nint obj)
    {
        if (obj != 0) Send(obj, sel_registerName("release"));
    }

    [LibraryImport(LibSystem)]
    private static partial int pthread_main_np();

    [LibraryImport(CoreFoundation)]
    private static partial int CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);

    [LibraryImport(LibDispatch)]
    private static partial void dispatch_async_f(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendPolicy(nint receiver, nint selector, long policy);
}
