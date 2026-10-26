using QmTui.Player;
using Xunit;

namespace QmTui.Tests;

[CollectionDefinition("GStreamer integration", DisableParallelization = true)]
public sealed class GStreamerIntegrationCollection { }

public sealed class GStreamerIntegrationFactAttribute : FactAttribute
{
    public GStreamerIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QMTUI_GSTREAMER_TEST") != "1")
        {
            Skip = "Set QMTUI_GSTREAMER_TEST=1 on a machine with GStreamer to exercise native decoding and sink interop.";
        }
    }
}

[Collection("GStreamer integration")]
[Trait("Category", "GStreamer")]
public sealed class GStreamerIntegrationTests
{
    [GStreamerIntegrationFact]
    public async Task NativePipeline_DecodesWaveAndSeeks_WithoutPhysicalAudioOutput()
    {
        Assert.Equal(0, await GStreamerDiagnostics.RunAsync());
    }

    [GStreamerIntegrationFact]
    public void InvalidExplicitSink_DoesNotSilentlyUsePhysicalAudioOutput()
    {
        var previousSink = Environment.GetEnvironmentVariable("GST_AUDIO_SINK");
        try
        {
            Environment.SetEnvironmentVariable("GST_AUDIO_SINK", "qmtui_nonexistent_audio_sink");
            using var player = new GstPlayer();
            player.Initialize();
            Assert.False(player.IsAvailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GST_AUDIO_SINK", previousSink);
        }
    }
}
