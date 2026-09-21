SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (p.Attr_country IN (@p0, @p1))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'ID'
@p1 = 'SG'
