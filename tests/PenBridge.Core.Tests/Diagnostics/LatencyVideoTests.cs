using PenBridge.Core.Diagnostics;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>Screen mirroring's capture-to-render latency rows (<see cref="LatencyRecordKind.Video"/>).</summary>
public class LatencyVideoTests
{
    private const long Frequency = 1_000_000_000; // ns
    private const uint Session = 9;
    private const double ClientStart = 77_000_000;

    // True time τ in µs: server clock S = τ + 5 s (ns timestamps), client clock C = start + τ (µs).
    private static long Server(double tau) => (long)((tau + 5_000_000) * 1000);

    private static ulong Client(double tau) => (ulong)(ClientStart + tau);

    /// <summary>Pings with exact 800 µs one-way minima (so the offset is exact) and 10 video frames of 20..29 ms.</summary>
    private static List<LatencyRecord> Session_(bool pings)
    {
        var records = new List<LatencyRecord>();
        if (pings)
        {
            for (int i = 0; i < 5; i++)
            {
                double tau = 1_000_000 + (i * 2_000_000);
                records.Add(new LatencyRecord(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, Session, Client(tau), Server(tau + 800), Server(tau + 800)));
                records.Add(new LatencyRecord(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, Session, 0, Server(tau + 100), Server(tau + 1700)));
            }
        }

        for (int i = 0; i < 10; i++)
        {
            double captured = 2_000_000 + (i * 500_000);
            double rendered = captured + 20_000 + (i * 1000);
            records.Add(new LatencyRecord(LatencyRecordKind.Video, LatencyFrameInfo.None, Session, Client(rendered), Server(captured), Server(rendered + 3000)));
        }

        return records;
    }

    [Fact]
    public void Capture_to_render_latency_is_offset_corrected()
    {
        LatencyReport report = LatencyAnalyzer.Analyze(Session_(pings: true).ToArray(), Frequency);

        Assert.Equal(10, report.Video.Count);
        Assert.Equal(20_000, report.Video.MinUs, 1);
        Assert.Equal(24_000, report.Video.P50Us, 1);
        Assert.Equal(29_000, report.Video.P95Us, 1);
        Assert.Equal(29_000, report.Video.MaxUs, 1);
        Assert.Equal(0, report.UnmeasuredVideo);
        Assert.Equal(0, report.Inject.Count);
    }

    [Fact]
    public void Video_rows_without_client_pings_are_unmeasured()
    {
        LatencyReport report = LatencyAnalyzer.Analyze(Session_(pings: false).ToArray(), Frequency);

        Assert.Equal(0, report.Video.Count);
        Assert.Equal(10, report.UnmeasuredVideo);
    }

    [Fact]
    public void The_dump_prints_the_video_line_and_rows()
    {
        LatencyRecord[] records = [.. Session_(pings: true)];
        using var writer = new StringWriter();
        LatencyDump.Write(writer, records, Frequency);
        string dump = writer.ToString();

        Assert.Contains("# video_capture_to_render: count=10 p50_ms=24.000 p95_ms=29.000 max_ms=29.000 min_ms=20.000", dump, StringComparison.Ordinal);
        Assert.Contains("\nvideo\t9\t0\t", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dump_without_video_has_no_video_line()
    {
        LatencyRecord[] records = [.. Session_(pings: true).Where(r => r.Kind != LatencyRecordKind.Video)];
        using var writer = new StringWriter();
        LatencyDump.Write(writer, records, Frequency, includeRecords: false);

        Assert.DoesNotContain("video", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_recorder_keeps_video_rows()
    {
        var recorder = new LatencyRecorder(8);
        recorder.RecordVideo(Session, 123, 456, 789);

        Assert.Equal(new LatencyRecord(LatencyRecordKind.Video, LatencyFrameInfo.None, Session, 123, 456, 789), Assert.Single(recorder.Snapshot()));
    }
}
