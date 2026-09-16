using Xunit;

namespace Crm.IntegrationTests.Fixtures;

[CollectionDefinition(Name)]
public sealed class StackCollectionDefinition : ICollectionFixture<CrmStackFixture>
{
    public const string Name = "stack";
}
