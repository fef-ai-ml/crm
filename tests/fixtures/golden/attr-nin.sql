SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND ((JSON_VALUE(p.Attributes, '$.tier') NOT IN (@p0, @p1) OR JSON_VALUE(p.Attributes, '$.tier') IS NULL))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'gold'
@p1 = 'silver'
