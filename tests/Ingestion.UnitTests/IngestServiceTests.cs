using System.Diagnostics.Metrics;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

[Collection("IngestMetrics")]
public sealed class IngestServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly RecordingPublisher _publisher = new();
    private readonly IngestMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, string? Tag)> _measurements = [];
    private readonly IngestService _service;

    public IngestServiceTests()
    {
        var clock = new FakeClock(Now);
        _service = new IngestService(
            new TrackRequestValidator(clock), new IdentifyRequestValidator(),
            new EventEnvelopeFactory(new UuidV7IdGenerator(), clock), _publisher, _metrics);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == IngestMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, tags.Length > 0 ? tags[0].Value?.ToString() : null));
            }
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task A_valid_track_is_published_and_counted()
    {
        var result = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "purchase", null, null), default);

        result.IsSuccess.Should().BeTrue();
        _publisher.Published.Should().ContainSingle().Which.EventId.Should().Be(result.Value.EventId);
        _measurements.Should().ContainSingle(m => m.Tag == "track" && m.Value == 1);
    }

    [Fact]
    public async Task An_invalid_track_is_a_validation_error_naming_the_first_field_and_is_counted_as_rejected()
    {
        var result = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "bad name!", null, null), default);

        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("ingest.invalid");
        result.Error.Message.Should().Be("event must match [A-Za-z0-9_]{1,64}");
        result.Error.Fields.Should().ContainKey("event");
        _publisher.Published.Should().BeEmpty();
        _measurements.Should().ContainSingle(m => m.Tag == "validation" && m.Value == 1);
    }

    [Fact]
    public async Task A_missing_body_is_a_validation_error()
    {
        (await _service.TrackAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("body").WhoseValue.Should().Equal("is required");
        (await _service.IdentifyAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("body");
    }

    [Fact]
    public async Task A_broker_failure_is_Unavailable_and_nothing_is_counted_as_accepted()
    {
        _publisher.Fail = true;

        var track = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "purchase", null, null), default);
        var batch = await _service.BatchAsync(Tenant, new BatchRequest([new BatchItem("identify", "u1", null, null, null, null, null)]), default);

        track.Error!.Kind.Should().Be(ErrorKind.Unavailable);
        track.Error.Code.Should().Be("ingest.unavailable");
        batch.Error!.Kind.Should().Be(ErrorKind.Unavailable);
        _measurements.Should().NotContain(m => m.Tag == "track" || m.Tag == "identify");
        _measurements.Where(m => m.Tag == "unavailable").Sum(m => m.Value).Should().Be(2);
    }

    [Fact]
    public async Task Identify_is_published_with_its_own_type()
    {
        var result = await _service.IdentifyAsync(Tenant, new IdentifyRequest("u2", "a1", null), default);

        result.IsSuccess.Should().BeTrue();
        _publisher.Published.Should().ContainSingle().Which.MessageType.Should().Be("IdentifyRaw");
        _measurements.Should().ContainSingle(m => m.Tag == "identify");
    }
}
