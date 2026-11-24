using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_shares",
                schema: "cal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_shares", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_shares_grantee_user_id",
                schema: "cal",
                table: "calendar_shares",
                column: "grantee_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_shares_owner_user_id_grantee_user_id",
                schema: "cal",
                table: "calendar_shares",
                columns: new[] { "owner_user_id", "grantee_user_id" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_shares",
                schema: "cal");
        }
    }
}
