using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QmTui.Models;
using QmTui.Services;
using QmTui.UI;
using Xunit;

namespace QmTui.Tests;

[CollectionDefinition("Audio export", DisableParallelization = true)]
public sealed class AudioExportCollection { }

[Collection("Audio export")]
public class AudioExportServiceTests
{
    private const string CoverPath = "/music/photo_new/T002R300x300M000fixture_album.jpg?max_age=2592000";
    private const string Cover1200Path = "/music/photo_new/T002R1200x1200M000fixture_album.jpg?max_age=2592000";
    private const string Cover800Path = "/music/photo_new/T002R800x800M000fixture_album.jpg?max_age=2592000";
    private const string RawCoverPath = "/music/photo_new/T002M000fixture_album.jpg?max_age=2592000";
    private static readonly Lazy<byte[]> s_rawCover = new(() => CreateBitmapCover(3000, 3000));

    [Theory]
    [InlineData("Artist/Name", "Artist_Name")]
    [InlineData("Title: Test*?\"<>|", "Title_ Test______")]
    [InlineData("Clean Song Title", "Clean Song Title")]
    public void SanitizeFileName_RemovesInvalidCharacters(string input, string expected)
    {
        var result = AudioExportService.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void InferAudioExtension_FlacMagicBytes_ReturnsDotFlac()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // fLaC header
            File.WriteAllBytes(tempFile, [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.Standard);
            Assert.Equal(".flac", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_Id3MagicBytes_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // ID3 header
            File.WriteAllBytes(tempFile, [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_MpegSyncword_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // MPEG 1 Layer III syncword: 0xFF, 0xFB
            File.WriteAllBytes(tempFile, [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void SanitizeCoverArt_WithinThreshold_PreservesOriginalBytes()
    {
        var safeBytes = new byte[1024 * 1024]; // 1 MB
        safeBytes[0] = 0x42;
        var result = AudioExportService.SanitizeCoverArt(safeBytes);
        Assert.Same(safeBytes, result);
    }

    [Fact]
    public void SanitizeCoverArt_CorruptedBytesAboveThreshold_GracefullyReturnsRawBytes()
    {
        var largeInvalidBytes = new byte[3 * 1024 * 1024]; // 3 MB invalid
        largeInvalidBytes[0] = 0x11;
        var result = AudioExportService.SanitizeCoverArt(largeInvalidBytes);
        Assert.Same(largeInvalidBytes, result);
    }

    [Fact]
    public void SanitizeCoverArt_ValidLargeImage_CompressesAndMaintainsResolution()
    {
        // 未压缩 BMP 保证超过门限，避免高度可压缩的 PNG 让测试跳过所有断言。
        int width = 1200;
        int height = 1200;
        var bitmapBytes = CreateBitmapCover(width, height);
        Assert.True(bitmapBytes.Length > AudioExportService.MaxRawCoverBytes);

        var sanitized = AudioExportService.SanitizeCoverArt(bitmapBytes);
        Assert.True(sanitized.Length < bitmapBytes.Length);

        var decoded = StbImageSharp.ImageResult.FromMemory(sanitized, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
    }

    [Theory]
    [InlineData(AudioQualityTier.SQ)]
    [InlineData(AudioQualityTier.HiRes)]
    [InlineData(AudioQualityTier.Master)]
    public async Task ResolveCoverArtBytesAsync_AboveHqTier_RetrievesRawCoverAndCompressesWithQuality88(AudioQualityTier tier)
    {
        Assert.True(AudioExportService.IsAboveHq(tier));
        Assert.Equal(88, AudioExportService.CoverCompressionQuality);

        var rawCover = s_rawCover.Value;
        Assert.True(rawCover.Length > AudioExportService.MaxRawCoverBytes);
        await using var server = new CoverServer(new Dictionary<string, byte[]> { [RawCoverPath] = rawCover });
        var song = new Song("test_song", "Test Title", "Test Artist", "Test Album", 180)
        {
            CoverUrl = server.BaseUrl + CoverPath
        };

        // 使用本地 HTTP 夹具验证实际候选 URL 和原图处理，不依赖 QQ CDN 可用性。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var rawBytes = await AudioExportService.ResolveCoverArtBytesAsync(song, tier, timeout.Token);
        Assert.NotNull(rawBytes);
        Assert.NotEmpty(rawBytes);
        Assert.Equal(new[] { RawCoverPath }, server.Requests.ToArray());
        Assert.Equal(0xff, rawBytes[0]);
        Assert.Equal(0xd8, rawBytes[1]); // 真正触发了 JPEG 重压缩

        // 超大原图通过高质量 JPEG 88 压缩至 2.5MB 内，且保持 3000x3000 原始分辨率不缩小
        var sanitized = AudioExportService.SanitizeCoverArt(rawBytes);
        Assert.True(sanitized.Length <= AudioExportService.MaxRawCoverBytes);

        var decoded = StbImageSharp.ImageResult.FromMemory(sanitized, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(3000, decoded.Width);
        Assert.Equal(3000, decoded.Height);
    }

    [Theory]
    [InlineData(AudioQualityTier.Standard)]
    [InlineData(AudioQualityTier.HQ)]
    public async Task ResolveCoverArtBytesAsync_StandardAndHqTier_Retrieves1200Cover(AudioQualityTier tier)
    {
        Assert.False(AudioExportService.IsAboveHq(tier));

        var cover = CreatePngCover(1200, 1200);
        await using var server = new CoverServer(new Dictionary<string, byte[]> { [Cover1200Path] = cover });
        var song = new Song("test_song", "Test Title", "Test Artist", "Test Album", 180)
        {
            CoverUrl = server.BaseUrl + CoverPath
        };

        // 标准与 HQ 音质拉取 1200 高清封面
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = await AudioExportService.ResolveCoverArtBytesAsync(song, tier, timeout.Token);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        Assert.Equal(new[] { Cover1200Path }, server.Requests.ToArray());
        Assert.Equal(cover, bytes);

        var decoded = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(1200, decoded.Width);
        Assert.Equal(1200, decoded.Height);
    }

    [Theory]
    [InlineData(AudioQualityTier.Standard)]
    [InlineData(AudioQualityTier.HQ)]
    public async Task ResolveCoverArtBytesAsync_Missing1200Cover_FallsBackTo800(AudioQualityTier tier)
    {
        var cover = CreatePngCover(800, 800);
        await using var server = new CoverServer(new Dictionary<string, byte[]> { [Cover800Path] = cover });
        var song = new Song("test_song", "Test Title", "Test Artist", "Test Album", 180)
        {
            CoverUrl = server.BaseUrl + CoverPath
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = await AudioExportService.ResolveCoverArtBytesAsync(song, tier, timeout.Token);

        Assert.NotNull(bytes);
        Assert.Equal(new[] { Cover1200Path, Cover800Path }, server.Requests.ToArray());
        Assert.Equal(cover, bytes);
        var decoded = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(800, decoded.Width);
        Assert.Equal(800, decoded.Height);
    }

    private static byte[] CreatePngCover(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, (byte)96);
        using var stream = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(
            pixels, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlue, stream);
        return stream.ToArray();
    }

    private static byte[] CreateBitmapCover(int width, int height)
    {
        int rowBytes = (width * 3 + 3) & ~3;
        int pixelBytes = rowBytes * height;
        using var stream = new MemoryStream(54 + pixelBytes);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0x4d42); // BM, uncompressed 24-bit Windows bitmap
        writer.Write(54 + pixelBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        var row = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = 128;
                row[x * 3 + 1] = (byte)(y * 255 / (height - 1));
                row[x * 3 + 2] = (byte)(x * 255 / (width - 1));
            }
            writer.Write(row);
        }
        return stream.ToArray();
    }

    private sealed class CoverServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly IReadOnlyDictionary<string, byte[]> _responses;
        private readonly Task _worker;

        public string BaseUrl { get; }
        public ConcurrentQueue<string> Requests { get; } = new();

        public CoverServer(IReadOnlyDictionary<string, byte[]> responses)
        {
            _responses = responses;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _worker = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    var path = request?.Split(' ')[1] ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    Requests.Enqueue(path);
                    bool found = _responses.TryGetValue(path, out var body);
                    body ??= Array.Empty<byte>();
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _worker; }
            finally { _stop.Dispose(); }
        }
    }
}
