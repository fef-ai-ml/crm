SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND ((p.Attr_plan = @p0 AND EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p1 AND e.OccurredAt >= @p2 HAVING COUNT(*) >= @p3) AND (p.Attr_country IN (@p4, @p5) OR NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p6 AND e.OccurredAt >= @p7))))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'pro'
@p1 = 'purchase'
@p2 = 2026-08-17T00:00:00Z
@p3 = 3
@p4 = 'ID'
@p5 = 'SG'
@p6 = 'app_open'
@p7 = 2026-09-09T00:00:00Z
