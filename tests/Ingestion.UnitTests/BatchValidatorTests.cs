using Crm.Contracts;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

// Same collection as IngestServiceTests: a MeterListener is process-wide, so classes that record on the
// Crm.Ingestion meter must not run in parallel with the one that listens to it.
[Collection("IngestMetrics")]
public class BatchValidatorTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly RecordingPublisher _publisher = new();
    private readonly IngestService _service;

    public BatchValidatorTests()
    {
        var clock = new FakeClock(Now);
        _service = new IngestService(
            new TrackRequestValidator(clock), new IdentifyRequestValidator(),
            new EventEnvelopeFactory(new UuidV7IdGenerator(), clock), _publisher, new IngestMetrics());
    }

    private static BatchItem Track(string @event = "app_open") => new("track", "u1", null, @event, null, null, null);

    [Fact]
    public async Task UT006_batch_of_500_passes_and_501_fails()
    {
        var ok = await _service.BatchAsync(Tenant, new BatchRequest(Enumerable.Repeat(Track(), 500).ToList()), default);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Accepted.Should().Be(500);

        var tooMany = await _service.BatchAsync(Tenant, new BatchRequest(Enumerable.Repeat(Track(), 501).ToList()), default);
        tooMany.Error!.Kind.Should().Be(ErrorKind.Validation);
        tooMany.Error.Fields.Should().ContainKey("events").WhoseValue.Should().Equal("at most 500 items");
        _publisher.Published.Should().HaveCount(500, "a rejected envelope publishes nothing");
    }

    [Fact]
    public async Task UT006_items_are_validated_independently()
    {
        var batch = new BatchRequest([Track(), Track("bad name!"), new BatchItem("identify", "u2", null, null, null, null, null)]);

        var result = await _service.BatchAsync(Tenant, batch, default);

        result.Value.Accepted.Should().Be(2);
        result.Value.Rejected.Should().Be(1);
        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1, 2);
        result.Value.Results[0].EventId.Should().NotBeNull();
        result.Value.Results[1].EventId.Should().BeNull();
        result.Value.Results[1].Errors.Should().ContainKey("events[1].event").WhoseValue.Should().Equal("must match [A-Za-z0-9_]{1,64}");
        result.Value.Results[2].EventId.Should().NotBeNull();
        _publisher.Published.Select(m => m.MessageType).Should().BeEquivalentTo([nameof(TrackEventRaw), nameof(IdentifyRaw)]);
        _publisher.Published.Select(m => m.EventId).Should().BeEquivalentTo(new[] { result.Value.Results[0].EventId!.Value, result.Value.Results[2].EventId!.Value });
    }

    [Fact]
    public async Task Missing_empty_or_null_envelopes_are_rejected()
    {
        (await _service.BatchAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("events");
        (await _service.BatchAsync(Tenant, new BatchRequest(null), default)).Error!.Fields.Should().ContainKey("events")
            .WhoseValue.Should().Equal("at least 1 item");
        (await _service.BatchAsync(Tenant, new BatchRequest([]), default)).Error!.Fields.Should().ContainKey("events");
    }

    [Fact]
    public async Task Unknown_type_and_null_items_are_item_errors()
    {
        var batch = new BatchRequest([new BatchItem("page", "u1", null, "x", null, null, null), null, new BatchItem(null, "u1", null, "x", null, null, null)]);

        var result = await _service.BatchAsync(Tenant, batch, default);

        result.Value.Accepted.Should().Be(0);
        result.Value.Rejected.Should().Be(3);
        result.Value.Results[0].Errors.Should().ContainKey("events[0].type").WhoseValue.Should().Equal("must be track or identify");
        result.Value.Results[1].Errors.Should().ContainKey("events[1]").WhoseValue.Should().Equal("must be an object");
        result.Value.Results[2].Errors.Should().ContainKey("events[2].type");
        _publisher.Published.Should().BeEmpty();
    }
}
