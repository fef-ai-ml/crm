namespace Crm.Kernel.Paging;

/// <summary>`limit` of every list endpoint: default 50, 1 to 200; anything else is a 400, never a silent clamp (ruling F3).</summary>
public static class PageLimit
{
    public const int Default = 50;
    public const int Max = 200;

    public static Result<int> Resolve(int? limit)
    {
        if (limit is null)
        {
            return Default;
        }

        if (limit is < 1 or > Max)
        {
            return PagingErrors.InvalidLimit;
        }

        return limit.Value;
    }
}
