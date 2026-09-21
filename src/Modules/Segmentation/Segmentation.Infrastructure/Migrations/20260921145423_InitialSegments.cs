using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Segmentation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "segments");

            migrationBuilder.CreateTable(
                name: "MembershipChanges",
                schema: "segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Change = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Source = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipChanges", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_MembershipChanges_Change", "[Change] IN ('Entered','Exited')");
                    table.CheckConstraint("CK_MembershipChanges_Source", "[Source] IN ('Batch','Realtime')");
                });

            migrationBuilder.CreateTable(
                name: "SegmentRunMembers",
                schema: "segments",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentRunMembers", x => new { x.RunId, x.ProfileId });
                });

            migrationBuilder.CreateTable(
                name: "SegmentRuns",
                schema: "segments",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    MemberCount = table.Column<int>(type: "int", nullable: true),
                    CompiledSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Trigger = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentRuns", x => x.RunId);
                    table.CheckConstraint("CK_SegmentRuns_Status", "[Status] IN ('Queued','Running','Succeeded','Failed')");
                    table.CheckConstraint("CK_SegmentRuns_Trigger", "[Trigger] IN ('Manual','Schedule')");
                });

            migrationBuilder.CreateTable(
                name: "Segments",
                schema: "segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Definition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "int", nullable: false),
                    DefinitionHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    IsRealtimeEligible = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CurrentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MemberCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LastRunAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Segments_Definition_IsJson", "ISJSON([Definition]) = 1");
                    table.CheckConstraint("CK_Segments_Status", "[Status] IN ('Draft','Active','Archived')");
                    table.ForeignKey(
                        name: "FK_Segments_CurrentRunId",
                        column: x => x.CurrentRunId,
                        principalSchema: "segments",
                        principalTable: "SegmentRuns",
                        principalColumn: "RunId");
                });

            migrationBuilder.CreateIndex(
                name: "CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id",
                schema: "segments",
                table: "MembershipChanges",
                columns: new[] { "TenantId", "SegmentId", "OccurredAt", "Id" },
                unique: true,
                descending: new[] { false, false, true, true })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipChanges_TenantId_ProfileId",
                schema: "segments",
                table: "MembershipChanges",
                columns: new[] { "TenantId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_SegmentRunMembers_TenantId_ProfileId",
                schema: "segments",
                table: "SegmentRunMembers",
                columns: new[] { "TenantId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_SegmentRuns_SegmentId",
                schema: "segments",
                table: "SegmentRuns",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId",
                schema: "segments",
                table: "SegmentRuns",
                columns: new[] { "TenantId", "SegmentId", "CreatedAt", "RunId" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "CX_Segments_TenantId_Id",
                schema: "segments",
                table: "Segments",
                columns: new[] { "TenantId", "Id" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Segments_CurrentRunId",
                schema: "segments",
                table: "Segments",
                column: "CurrentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_TenantId_CreatedAt_Id",
                schema: "segments",
                table: "Segments",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, true })
                .Annotation("SqlServer:Include", new[] { "Name", "Status", "MemberCount", "LastRunAt", "IsRealtimeEligible" });

            migrationBuilder.CreateIndex(
                name: "UX_Segments_TenantId_Name",
                schema: "segments",
                table: "Segments",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SegmentRunMembers_RunId",
                schema: "segments",
                table: "SegmentRunMembers",
                column: "RunId",
                principalSchema: "segments",
                principalTable: "SegmentRuns",
                principalColumn: "RunId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SegmentRuns_SegmentId",
                schema: "segments",
                table: "SegmentRuns",
                column: "SegmentId",
                principalSchema: "segments",
                principalTable: "Segments",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Segments_CurrentRunId",
                schema: "segments",
                table: "Segments");

            migrationBuilder.DropTable(
                name: "MembershipChanges",
                schema: "segments");

            migrationBuilder.DropTable(
                name: "SegmentRunMembers",
                schema: "segments");

            migrationBuilder.DropTable(
                name: "SegmentRuns",
                schema: "segments");

            migrationBuilder.DropTable(
                name: "Segments",
                schema: "segments");
        }
    }
}
