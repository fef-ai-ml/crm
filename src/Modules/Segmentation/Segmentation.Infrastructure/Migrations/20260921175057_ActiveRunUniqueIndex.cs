using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Segmentation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActiveRunUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_SegmentRuns_TenantId_SegmentId_Active",
                schema: "segments",
                table: "SegmentRuns",
                columns: new[] { "TenantId", "SegmentId" },
                unique: true,
                filter: "[Status] IN ('Queued','Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SegmentRuns_TenantId_SegmentId_Active",
                schema: "segments",
                table: "SegmentRuns");
        }
    }
}
