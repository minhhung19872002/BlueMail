using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminMailLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.CreateTable(
                name: "mail_log",
                schema: "admin",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rfc_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    envelope_from = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    rcpt = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    remote_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    remote_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_response = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    spam_score = table.Column<double>(type: "double precision", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_log_tenant_id_occurred_at",
                schema: "admin",
                table: "mail_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_log_tenant_id_rcpt_occurred_at",
                schema: "admin",
                table: "mail_log",
                columns: new[] { "tenant_id", "rcpt", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_log_tenant_id_rfc_message_id",
                schema: "admin",
                table: "mail_log",
                columns: new[] { "tenant_id", "rfc_message_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_log",
                schema: "admin");
        }
    }
}
