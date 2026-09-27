using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SystemAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogActorAndNodeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorUserName",
                table: "AuditLogs",
                column: "ActorUserName");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_NodeName",
                table: "AuditLogs",
                column: "NodeName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ActorUserName",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_NodeName",
                table: "AuditLogs");
        }
    }
}
