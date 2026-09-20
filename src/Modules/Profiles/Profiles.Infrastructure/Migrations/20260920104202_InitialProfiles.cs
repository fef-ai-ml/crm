using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "profiles");

            // EF cannot express partitioning. RANGE RIGHT: each boundary is the first instant of a month.
            // EventPartitionMaintenanceService adds later months; these two years make a fresh database usable at once.
            migrationBuilder.Sql(
                """
                CREATE PARTITION FUNCTION PF_Events_Monthly (datetime2(3)) AS RANGE RIGHT FOR VALUES (
                    '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
                    '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
                    '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01',
                    '2027-07-01', '2027-08-01', '2027-09-01', '2027-10-01', '2027-11-01', '2027-12-01');
                CREATE PARTITION SCHEME PS_Events_Monthly AS PARTITION PF_Events_Monthly ALL TO ([PRIMARY]);
                """);

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.CheckConstraint("CK_Events_Properties_IsJson", "ISJSON([Properties]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                schema: "profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Attributes = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    EventCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MergedIntoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Attr_city = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, computedColumnSql: "CAST(JSON_VALUE([Attributes], '$.city') AS nvarchar(256))", stored: true),
                    Attr_country = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, computedColumnSql: "CAST(JSON_VALUE([Attributes], '$.country') AS nvarchar(256))", stored: true),
                    Attr_plan = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, computedColumnSql: "CAST(JSON_VALUE([Attributes], '$.plan') AS nvarchar(256))", stored: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Profiles_Attributes_IsJson", "ISJSON([Attributes]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "UserIdentities",
                schema: "profiles",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentities", x => new { x.TenantId, x.Kind, x.Value });
                    table.CheckConstraint("CK_UserIdentities_Kind", "[Kind] IN ('external_id','anonymous_id','email','phone')");
                    table.ForeignKey(
                        name: "FK_UserIdentities_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "profiles",
                        principalTable: "Profiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "CX_Profiles_TenantId_Id",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Id" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_Attr_city",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Attr_city" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_Attr_country",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Attr_country" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_Attr_plan",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Attr_plan" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_LastSeenAt",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "LastSeenAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UX_Profiles_TenantId_Email",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Email" },
                unique: true,
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Profiles_TenantId_ExternalId",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true,
                filter: "[ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Profiles_TenantId_Phone",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "Phone" },
                unique: true,
                filter: "[Phone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_ProfileId",
                schema: "profiles",
                table: "UserIdentities",
                column: "ProfileId");

            // Creating the clustered index on the scheme moves the table onto it; the other two are aligned explicitly.
            migrationBuilder.Sql(
                """
                CREATE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt) ON PS_Events_Monthly (OccurredAt);
                CREATE UNIQUE NONCLUSTERED INDEX UX_Events_Id
                    ON profiles.Events (Id, OccurredAt) ON PS_Events_Monthly (OccurredAt);
                CREATE NONCLUSTERED INDEX IX_Events_TenantId_Name_OccurredAt
                    ON profiles.Events (TenantId, Name, OccurredAt) INCLUDE (ProfileId) ON PS_Events_Monthly (OccurredAt);
                ALTER TABLE profiles.Events ADD CONSTRAINT FK_Events_ProfileId
                    FOREIGN KEY (ProfileId) REFERENCES profiles.Profiles (Id);
                ALTER TABLE profiles.Profiles ADD CONSTRAINT FK_Profiles_MergedIntoId
                    FOREIGN KEY (MergedIntoId) REFERENCES profiles.Profiles (Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events",
                schema: "profiles");

            migrationBuilder.DropTable(
                name: "UserIdentities",
                schema: "profiles");

            migrationBuilder.DropTable(
                name: "Profiles",
                schema: "profiles");

            migrationBuilder.Sql(
                """
                DROP PARTITION SCHEME PS_Events_Monthly;
                DROP PARTITION FUNCTION PF_Events_Monthly;
                """);
        }
    }
}
