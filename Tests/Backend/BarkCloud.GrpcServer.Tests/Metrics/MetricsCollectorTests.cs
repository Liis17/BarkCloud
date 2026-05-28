using BarkCloud.GrpcServer.Metrics;

namespace BarkCloud.GrpcServer.Tests.Metrics;

public class MetricsCollectorTests
{
    [Fact]
    public void Increment_NewMetric_StartsAtOne()
    {
        var sut = new MetricsCollector();

        sut.Increment("requests");

        sut.SnapshotAndReset().Should().ContainKey("requests").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Increment_CalledMultipleTimes_Accumulates()
    {
        var sut = new MetricsCollector();

        for (var i = 0; i < 5; i++) sut.Increment("requests");

        sut.SnapshotAndReset()["requests"].Should().Be(5);
    }

    [Fact]
    public void Add_AccumulatesProvidedValue()
    {
        var sut = new MetricsCollector();

        sut.Add("duration", 100);
        sut.Add("duration", 250);

        sut.SnapshotAndReset()["duration"].Should().Be(350);
    }

    [Fact]
    public void Set_GaugeSurvivesSnapshotAndReset()
    {
        var sut = new MetricsCollector();

        sut.Set("active_connections", 7);
        sut.SnapshotAndReset();

        sut.SnapshotAndReset()["active_connections"].Should().Be(7);
    }

    [Fact]
    public void SnapshotAndReset_ResetsCountersButKeepsGauges()
    {
        var sut = new MetricsCollector();
        sut.Increment("counter");
        sut.Set("gauge", 42);

        var first = sut.SnapshotAndReset();
        var second = sut.SnapshotAndReset();

        first["counter"].Should().Be(1);
        first["gauge"].Should().Be(42);
        second.Should().NotContainKey("counter");
        second["gauge"].Should().Be(42);
    }

    [Fact]
    public void SnapshotAndReset_OmitsZeroCounters()
    {
        var sut = new MetricsCollector();
        sut.Add("counter", 5);
        sut.Add("counter", -5);

        var snap = sut.SnapshotAndReset();

        snap.Should().NotContainKey("counter");
    }
}
