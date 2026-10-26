using System.Diagnostics;

namespace QmTui.Utils;

/// <summary>
/// 使用当前平台的默认浏览器打开网页登录与 Web 播放器地址。
/// </summary>
internal static class BrowserLauncher
{
    public static void TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(url);
            using var process = Process.Start(startInfo);
        }
        catch
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Debug("BrowserLauncher", $"无法打开默认浏览器: {ex.Message}");
            }
        }
    }
}
