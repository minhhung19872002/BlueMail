using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Antispam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "spam");

            migrationBuilder.CreateTable(
                name: "bayes_tokens",
                schema: "spam",
                columns: table => new
                {
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<long>(type: "bigint", nullable: false),
                    spam = table.Column<int>(type: "integer", nullable: false),
                    ham = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bayes_tokens", x => new { x.scope, x.scope_id, x.token });
                });

            migrationBuilder.CreateTable(
                name: "bayes_totals",
                schema: "spam",
                columns: table => new
                {
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spam_messages = table.Column<int>(type: "integer", nullable: false),
                    ham_messages = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bayes_totals", x => new { x.scope, x.scope_id });
                });

            migrationBuilder.CreateTable(
                name: "dnsbl_sources",
                schema: "spam",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    zone = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    license_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dnsbl_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "greylist",
                schema: "spam",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ip_network = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sender = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    passed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_greylist", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quarantine",
                schema: "admin",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    blob_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    envelope_from = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    recipients = table.Column<string>(type: "jsonb", nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    spam_score = table.Column<double>(type: "double precision", nullable: true),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quarantine", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "spam",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spam_threshold = table.Column<double>(type: "double precision", nullable: false),
                    reject_threshold = table.Column<double>(type: "double precision", nullable: false),
                    greylisting = table.Column<bool>(type: "boolean", nullable: false),
                    suspicious_attachments = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phishing_report_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "training",
                schema: "spam",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spam = table.Column<bool>(type: "boolean", nullable: false),
                    learned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training", x => new { x.tenant_id, x.user_id, x.message_id });
                });

            migrationBuilder.InsertData(
                schema: "spam",
                table: "dnsbl_sources",
                columns: new[] { "id", "enabled", "kind", "license_note", "name", "score", "tenant_id", "zone" },
                values: new object[,]
                {
                    { 1, false, "IP", "Commercial use requires a Spamhaus DQS subscription; the zone needs your DQS key prefix.", "Spamhaus ZEN (DQS)", 4.0, null, "zen.dq.spamhaus.net" },
                    { 2, false, "IP", "Free, requires registering the querying resolver; check terms for service providers.", "Barracuda BRBL", 2.5, null, "b.barracudacentral.org" },
                    { 3, false, "IP", "Free, use at own risk; higher false-positive rate.", "SpamCop", 1.5, null, "bl.spamcop.net" },
                    { 4, false, "IP", "Free.", "PSBL", 1.0, null, "psbl.surriel.com" },
                    { 5, false, "DOMAIN", "Commercial use requires a Spamhaus DQS subscription.", "Spamhaus DBL (DQS)", 3.0, null, "dbl.dq.spamhaus.net" },
                    { 6, false, "URI", "Commercial use requires a licence above a small free threshold.", "SURBL", 3.0, null, "multi.surbl.org" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_dnsbl_sources_tenant_id_zone",
                schema: "spam",
                table: "dnsbl_sources",
                columns: new[] { "tenant_id", "zone" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_greylist_first_seen",
                schema: "spam",
                table: "greylist",
                column: "first_seen");

            migrationBuilder.CreateIndex(
                name: "ix_greylist_tenant_id_ip_network_sender_recipient",
                schema: "spam",
                table: "greylist",
                columns: new[] { "tenant_id", "ip_network", "sender", "recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_tenant_id_status_created_at",
                schema: "admin",
                table: "quarantine",
                columns: new[] { "tenant_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bayes_tokens",
                schema: "spam");

            migrationBuilder.DropTable(
                name: "bayes_totals",
                schema: "spam");

            migrationBuilder.DropTable(
                name: "dnsbl_sources",
                schema: "spam");

            migrationBuilder.DropTable(
                name: "greylist",
                schema: "spam");

            migrationBuilder.DropTable(
                name: "quarantine",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "spam");

            migrationBuilder.DropTable(
                name: "training",
                schema: "spam");
        }
    }
}
