SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND ((p.Attr_plan = @p0 AND (p.Attr_country IN (@p1, @p2) OR NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p3 AND e.OccurredAt >= @p4))))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'pro'
@p1 = 'ID'
@p2 = 'SG'
@p3 = 'app_open'
@p4 = 2026-09-09T00:00:00Z
