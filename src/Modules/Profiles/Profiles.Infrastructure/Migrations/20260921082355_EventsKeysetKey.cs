using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EventsKeysetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // profiles.Events is partitioned: EF cannot say ON PS_Events_Monthly, so this is SQL. DROP_EXISTING rebuilds the
            // clustered index (and the nonclustered ones that carry its key) once; the rename makes the name match the key.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt, Id)
                    WITH (DROP_EXISTING = ON) ON PS_Events_Monthly (OccurredAt);
                EXEC sp_rename N'profiles.Events.CX_Events_TenantId_ProfileId_OccurredAt', N'CX_Events_TenantId_ProfileId_OccurredAt_Id', N'INDEX';
                ALTER TABLE profiles.Events SET (LOCK_ESCALATION = AUTO);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE profiles.Events SET (LOCK_ESCALATION = TABLE);
                EXEC sp_rename N'profiles.Events.CX_Events_TenantId_ProfileId_OccurredAt_Id', N'CX_Events_TenantId_ProfileId_OccurredAt', N'INDEX';
                CREATE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt)
                    WITH (DROP_EXISTING = ON) ON PS_Events_Monthly (OccurredAt);
                """);
        }
    }
}
