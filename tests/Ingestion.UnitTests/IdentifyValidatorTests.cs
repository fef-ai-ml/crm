using System.Text.Json;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class IdentifyValidatorTests
{
    private readonly IdentifyRequestValidator _validator = new();

    private IReadOnlyDictionary<string, string[]> Errors(IdentifyRequest request) => ValidationFields.From(_validator.Validate(request));

    [Fact]
    public void UT005_identify_requires_userId_and_nothing_else()
    {
        Errors(new IdentifyRequest(null, "a1", null)).Should().ContainKey("userId").WhoseValue.Should().Equal("is required");
        Errors(new IdentifyRequest(" ", null, null)).Should().ContainKey("userId");
        Errors(new IdentifyRequest("u1", null, null)).Should().BeEmpty();
        Errors(new IdentifyRequest("u1", "a1", Json("{\"plan\":\"pro\"}"))).Should().BeEmpty();
    }

    [Fact]
    public void Traits_follow_the_same_shape_and_size_rules_as_properties()
    {
        Errors(new IdentifyRequest("u1", null, Json("\"pro\""))).Should().ContainKey("traits")
            .WhoseValue.Should().Equal("must be a JSON object");
        var big = Json("{\"k\":\"" + new string('a', 32_768) + "\"}");
        Errors(new IdentifyRequest("u1", null, big)).Should().ContainKey("traits")
            .WhoseValue.Should().Equal("must be at most 32 KB");
    }

    [Fact]
    public void Paths_can_be_prefixed_for_batch_items()
    {
        ValidationFields.From(_validator.Validate(new IdentifyRequest(null, null, null)), "events[3].")
            .Should().ContainKey("events[3].userId");
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
