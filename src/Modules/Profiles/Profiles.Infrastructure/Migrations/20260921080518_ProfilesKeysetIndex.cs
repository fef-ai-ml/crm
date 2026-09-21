using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProfilesKeysetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_TenantId_LastSeenAt",
                schema: "profiles",
                table: "Profiles");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_LastSeenAt_Id",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "LastSeenAt", "Id" },
                descending: new[] { false, true, true },
                filter: "[MergedIntoId] IS NULL")
                .Annotation("SqlServer:Include", new[] { "ExternalId", "Email", "Phone", "EventCount", "MergedIntoId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_TenantId_LastSeenAt_Id",
                schema: "profiles",
                table: "Profiles");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TenantId_LastSeenAt",
                schema: "profiles",
                table: "Profiles",
                columns: new[] { "TenantId", "LastSeenAt" },
                descending: new[] { false, true });
        }
    }
}
