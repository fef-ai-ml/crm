using System.Text.Json;
using Xunit;
using FluentAssertions;

namespace Crm.Contracts.UnitTests;

[Trait("Category", "Contract")]
public class ContractSnapshotTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid EventId1 = Guid.Parse("0192b1c4-0000-7000-8000-000000000001");
    private static readonly Guid EventId2 = Guid.Parse("0192b1c4-0000-7000-8000-000000000002");
    private static readonly Guid Winner = Guid.Parse("0192b1c4-0000-7000-8000-00000000aaaa");
    private static readonly Guid Loser = Guid.Parse("0192b1c4-0000-7000-8000-00000000bbbb");
    private static readonly DateTime T0 = new(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static readonly ProfileSnapshot Snapshot = new("u-123", "a@b.com", null, Json("""{"plan":"pro"}"""));

    public static TheoryData<string, object> Cases => new()
    {
        { "TrackEventRaw", new TrackEventRaw(EventId1, Tenant, "u-123", null, "purchase", Json("""{"amount":49.9,"sku":"A1"}"""), T0, T0.AddSeconds(1)) },
        { "IdentifyRaw", new IdentifyRaw(EventId2, Tenant, "u-123", "anon-9f1", Json("""{"plan":"pro","country":"ID"}"""), T0.AddSeconds(1)) },
        { "EventTracked", new EventTracked(EventId1, Tenant, Winner, "purchase", Json("""{"amount":49.9,"sku":"A1"}"""), T0, Snapshot) },
        { "ProfileUpdated", new ProfileUpdated(Tenant, Winner, ["plan", "country"], Snapshot with { Attributes = Json("""{"plan":"pro","country":"ID"}""") }, T0.AddSeconds(1)) },
        { "ProfileMerged", new ProfileMerged(Tenant, Winner, Loser, T0.AddSeconds(2)) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Serialized_message_matches_committed_snapshot(string name, object message)
    {
        var path = Path.Combine(FixturesRoot(), "contracts", $"{name}.json");
        var expected = Normalize(File.ReadAllText(path));

        var actual = Normalize(JsonSerializer.Serialize(message, message.GetType(), ContractJson.IndentedOptions));

        actual.Should().Be(expected, $"regenerate deliberately if the contract changed: {path}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Snapshot_round_trips_through_deserialization(string name, object message)
    {
        var path = Path.Combine(FixturesRoot(), "contracts", $"{name}.json");

        var roundTripped = JsonSerializer.Deserialize(File.ReadAllText(path), message.GetType(), ContractJson.Options);

        JsonSerializer.Serialize(roundTripped, message.GetType(), ContractJson.Options)
            .Should().Be(JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options));
    }

    [Fact]
    public void Topic_and_header_constants_match_the_data_dictionary()
    {
        KafkaTopics.EventsRaw.Should().Be("events.raw");
        KafkaTopics.EventsProcessed.Should().Be("events.processed");
        KafkaTopics.EventsDlq.Should().Be("events.dlq");
        KafkaTopics.Partitions.Should().Be(12);
        KafkaHeaders.TenantId.Should().Be("tenant-id");
        KafkaHeaders.MessageType.Should().Be("message-type");
        KafkaHeaders.TraceParent.Should().Be("traceparent");
        KafkaHeaders.DlqError.Should().Be("error");
        KafkaHeaders.DlqAttempt.Should().Be("attempt");
        KafkaHeaders.DlqSourceOffset.Should().Be("source-offset");
    }

    private static string Normalize(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string FixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("tests/fixtures not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "tests", "fixtures");
    }
}
