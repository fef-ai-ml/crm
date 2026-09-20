namespace Crm.Profiles.Domain;

/// <summary>The four identity kinds of FR-08; the strings are the values of profiles.UserIdentities.Kind.</summary>
public static class IdentityKinds
{
    public const string ExternalId = "external_id";
    public const string AnonymousId = "anonymous_id";
    public const string Email = "email";
    public const string Phone = "phone";
}
