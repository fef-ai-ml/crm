using Crm.Profiles.Infrastructure.Consumers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilesConsumerOptionsTests
{
    [Fact]
    public void Defaults_are_the_spec_values()
    {
        var options = new ProfilesConsumerOptions();

        options.Enabled.Should().BeTrue();
        options.GroupId.Should().Be("profiles");
        options.Instances.Should().Be(4);
        options.RetryDelays.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Configured_delays_replace_the_defaults_instead_of_being_appended_to_them()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Profiles:Consumer:RetryDelaysMs:0"] = "50",
            ["Profiles:Consumer:RetryDelaysMs:1"] = "100",
        }).Build();
        var options = new ProfilesConsumerOptions();

        configuration.GetSection(ProfilesConsumerOptions.SectionName).Bind(options);

        options.RetryDelays.Should().Equal(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
    }
}
