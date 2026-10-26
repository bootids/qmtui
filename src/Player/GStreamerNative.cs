using System.Reflection;
using System.Runtime.InteropServices;

namespace QmTui.Player;

/// <summary>
/// Locates GStreamer's native libraries without requiring a global DYLD_LIBRARY_PATH.
/// Linux keeps its normal shared-library lookup; macOS supports the official framework
/// and the standard Apple Silicon / Intel Homebrew prefixes.
/// </summary>
internal static partial class GStreamerNative
{
    internal const string GstLibrary = "libgstreamer-1.0.so.0";
    internal const string GObjectLibrary = "libgobject-2.0.so.0";
    internal const string GLibLibrary = "libglib-2.0.so.0";

    private const string FrameworkRoot = "/Library/Frameworks/GStreamer.framework/Versions/1.0";
    private static readonly Lock s_libraryLock = new();
    private static readonly Dictionary<string, (nint Handle, string Path)> s_libraries = new(StringComparer.Ordinal);

    static GStreamerNative()
    {
        if (!OperatingSystem.IsMacOS()) return;

        NativeLibrary.SetDllImportResolver(typeof(GStreamerNative).Assembly, ResolveLibrary);

        // Also cover subprocesses such as AudioSliceDecoder's gst-launch-1.0. The
        // official framework contains its own plugin scanner and compiled-in paths.
        var executableDirectories = new[] { $"{FrameworkRoot}/bin", "/opt/homebrew/bin", "/usr/local/bin" };
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var directory in executableDirectories.Reverse())
        {
            if (Directory.Exists(directory) && !path.Contains(directory, StringComparer.Ordinal))
            {
                path.Insert(0, directory);
            }
        }
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, path));
    }

    // The explicit static constructor makes registration thread-safe and once-only.
    // Call at program entry too, so --recognize gets the framework tools on PATH.
    internal static void Initialize() { }

    internal static string LoadedGStreamerPath
    {
        get
        {
            lock (s_libraryLock)
            {
                return s_libraries.TryGetValue(GstLibrary, out var library)
                    ? library.Path
                    : OperatingSystem.IsMacOS() ? "not loaded" : $"system library: {GstLibrary}";
            }
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var fileName = libraryName switch
        {
            GstLibrary => "libgstreamer-1.0.0.dylib",
            GObjectLibrary => "libgobject-2.0.0.dylib",
            GLibLibrary => "libglib-2.0.0.dylib",
            _ => null
        };
        if (fileName == null) return 0;

        lock (s_libraryLock)
        {
            if (s_libraries.TryGetValue(libraryName, out var existing)) return existing.Handle;

            string[] directories = [$"{FrameworkRoot}/lib", "/opt/homebrew/lib", "/usr/local/lib"];
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, fileName);
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    s_libraries.Add(libraryName, (handle, path));
                    return handle;
                }
            }

            if (NativeLibrary.TryLoad(fileName, out var fallbackHandle))
            {
                s_libraries.Add(libraryName, (fallbackHandle, fileName));
                return fallbackHandle;
            }
        }

        return 0;
    }

    // GStreamer must already have been initialized before querying its registry.
    internal static bool HasElementFactory(string name)
    {
        var factory = gst_element_factory_find(name);
        if (factory == 0) return false;
        gst_object_unref(factory);
        return true;
    }

    [LibraryImport(GstLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_element_factory_find(string name);

    [LibraryImport(GstLibrary)]
    private static partial void gst_object_unref(nint obj);
}
