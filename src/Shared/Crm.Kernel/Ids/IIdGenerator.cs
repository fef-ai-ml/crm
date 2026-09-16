namespace Crm.Kernel.Ids;

public interface IIdGenerator
{
    Guid NewId();
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
