SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND ((p.Attr_plan <> @p0 OR p.Attr_plan IS NULL))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'pro'
