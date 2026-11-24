using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BulkMailJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_jobs",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snooze_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scope_label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    done = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_jobs_status_created_at",
                schema: "mail",
                table: "bulk_jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_jobs_user_id_created_at",
                schema: "mail",
                table: "bulk_jobs",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_jobs",
                schema: "mail");
        }
    }
}
