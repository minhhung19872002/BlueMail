using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mta");

            migrationBuilder.CreateTable(
                name: "destination_limits",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    domain_pattern = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    max_connections = table.Column<int>(type: "integer", nullable: false),
                    max_messages_per_minute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_destination_limits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "drafts",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drafts", x => x.id);
                    table.ForeignKey(
                        name: "fk_drafts_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mail",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "large_attachments",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    download_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_large_attachments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outgoing_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    blob_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    envelope_from = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    dkim_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    rfc_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    smtp_utf8 = table.Column<bool>(type: "boolean", nullable: false),
                    eight_bit = table.Column<bool>(type: "boolean", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_notification = table.Column<bool>(type: "boolean", nullable: false),
                    sender_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outgoing",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    send_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rfc_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    archive_thread = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outgoing", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "signatures",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    is_default_new = table.Column<bool>(type: "boolean", nullable: false),
                    is_default_reply = table.Column<bool>(type: "boolean", nullable: false),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signatures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suppressions",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppressions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "uploads",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    offset = table.Column<long>(type: "bigint", nullable: false),
                    chunks = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scan_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_recipients",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rcpt = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_mx = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    last_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    last_response = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_tls = table.Column<bool>(type: "boolean", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delay_notice_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dsn_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbound_recipients_outbound_messages_outbound_message_id",
                        column: x => x.outbound_message_id,
                        principalSchema: "mta",
                        principalTable: "outbound_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "mta",
                table: "destination_limits",
                columns: new[] { "id", "domain_pattern", "max_connections", "max_messages_per_minute" },
                values: new object[,]
                {
                    { 1, "gmail.com", 5, 300 },
                    { 2, "outlook.com", 3, 120 },
                    { 3, "hotmail.com", 3, 120 },
                    { 4, "yahoo.com", 3, 120 },
                    { 5, "*", 10, 600 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_destination_limits_domain_pattern",
                schema: "mta",
                table: "destination_limits",
                column: "domain_pattern",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_drafts_mailbox_id_updated_at",
                schema: "mail",
                table: "drafts",
                columns: new[] { "mailbox_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_drafts_message_id",
                schema: "mail",
                table: "drafts",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_large_attachments_expires_at",
                schema: "mail",
                table: "large_attachments",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_large_attachments_token_hash",
                schema: "mail",
                table: "large_attachments",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_outgoing_id",
                schema: "mta",
                table: "outbound_messages",
                column: "outgoing_id",
                unique: true,
                filter: "outgoing_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_recipients_outbound_message_id_rcpt",
                schema: "mta",
                table: "outbound_recipients",
                columns: new[] { "outbound_message_id", "rcpt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_recipients_status_next_attempt_at",
                schema: "mta",
                table: "outbound_recipients",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outgoing_mailbox_id_status",
                schema: "mail",
                table: "outgoing",
                columns: new[] { "mailbox_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_outgoing_status_send_at",
                schema: "mail",
                table: "outgoing",
                columns: new[] { "status", "send_at" });

            migrationBuilder.CreateIndex(
                name: "ux_outgoing_draft_pending",
                schema: "mail",
                table: "outgoing",
                column: "draft_id",
                unique: true,
                filter: "status IN ('UNDO_WINDOW','SCHEDULED','SUBMITTING') AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_signatures_user_id_name",
                schema: "mail",
                table: "signatures",
                columns: new[] { "user_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_suppressions_tenant_id_address",
                schema: "mta",
                table: "suppressions",
                columns: new[] { "tenant_id", "address" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_templates_user_id_name",
                schema: "mail",
                table: "templates",
                columns: new[] { "user_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_uploads_expires_at",
                schema: "mail",
                table: "uploads",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "destination_limits",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "drafts",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "large_attachments",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "outbound_recipients",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "outgoing",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "signatures",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "suppressions",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "templates",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "uploads",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "outbound_messages",
                schema: "mta");
        }
    }
}
