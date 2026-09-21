SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p0 AND e.OccurredAt >= @p1))

@tenant = 00000000-0000-0000-0000-000000000001
@p0 = 'app_open'
@p1 = 2026-09-09T00:00:00Z
