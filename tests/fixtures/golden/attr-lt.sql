SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv')) < @p0)

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 50
