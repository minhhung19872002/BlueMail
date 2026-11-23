using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Organize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contact");

            migrationBuilder.CreateTable(
                name: "category_rules",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match = table.Column<string>(type: "jsonb", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contact_group_members",
                schema: "contact",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_group_members", x => new { x.group_id, x.contact_id });
                });

            migrationBuilder.CreateTable(
                name: "contact_groups",
                schema: "contact",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "contact",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    emails = table.Column<string>(type: "jsonb", nullable: false),
                    phones = table.Column<string>(type: "jsonb", nullable: false),
                    org = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    photo_sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    photo_content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_auto_saved = table.Column<bool>(type: "boolean", nullable: false),
                    last_contacted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    contact_count = table.Column<int>(type: "integer", nullable: false),
                    email_keys = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "directory_overrides",
                schema: "contact",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hide_from_directory = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_overrides", x => new { x.tenant_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "snoozes",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    until_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    woken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snoozes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                schema: "mail",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_settings", x => x.user_id);
                });

            migrationBuilder.InsertData(
                schema: "mail",
                table: "category_rules",
                columns: new[] { "id", "category", "is_enabled", "match", "sort_order", "tenant_id" },
                values: new object[,]
                {
                    { 1, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"facebookmail.com\"}", 1, null },
                    { 2, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"facebook.com\"}", 2, null },
                    { 3, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"linkedin.com\"}", 3, null },
                    { 4, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"x.com\"}", 4, null },
                    { 5, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"twitter.com\"}", 5, null },
                    { 6, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"instagram.com\"}", 6, null },
                    { 7, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"zalo.me\"}", 7, null },
                    { 8, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"tiktok.com\"}", 8, null },
                    { 9, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"youtube.com\"}", 9, null },
                    { 10, "CATEGORY_SOCIAL", true, "{\"fromDomain\":\"pinterest.com\"}", 10, null },
                    { 11, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"mailchimp.com\"}", 11, null },
                    { 12, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"mcsv.net\"}", 12, null },
                    { 13, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"sendgrid.net\"}", 13, null },
                    { 14, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"shopee.vn\"}", 14, null },
                    { 15, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"lazada.vn\"}", 15, null },
                    { 16, "CATEGORY_PROMOTIONS", true, "{\"fromDomain\":\"tiki.vn\"}", 16, null }
                });

            migrationBuilder.CreateIndex(
                name: "ix_category_rules_tenant_id_sort_order",
                schema: "mail",
                table: "category_rules",
                columns: new[] { "tenant_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_groups_tenant_id_user_id_name",
                schema: "contact",
                table: "contact_groups",
                columns: new[] { "tenant_id", "user_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_contacts_email_keys",
                schema: "contact",
                table: "contacts",
                column: "email_keys")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tenant_id_user_id_display_name",
                schema: "contact",
                table: "contacts",
                columns: new[] { "tenant_id", "user_id", "display_name" });

            migrationBuilder.CreateIndex(
                name: "ix_snoozes_status_until_at",
                schema: "mail",
                table: "snoozes",
                columns: new[] { "status", "until_at" });

            migrationBuilder.CreateIndex(
                name: "ux_snoozes_active",
                schema: "mail",
                table: "snoozes",
                columns: new[] { "user_id", "thread_id" },
                unique: true,
                filter: "status = 'ACTIVE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_rules",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "contact_group_members",
                schema: "contact");

            migrationBuilder.DropTable(
                name: "contact_groups",
                schema: "contact");

            migrationBuilder.DropTable(
                name: "contacts",
                schema: "contact");

            migrationBuilder.DropTable(
                name: "directory_overrides",
                schema: "contact");

            migrationBuilder.DropTable(
                name: "snoozes",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "user_settings",
                schema: "mail");
        }
    }
}
