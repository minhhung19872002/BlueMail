using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SendingScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sending_lock_reason",
                schema: "identity",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sending_locked_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "confidential_messages",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rfc_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    blob_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    require_code = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_confidential_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feedback_events",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rcpt = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reporter_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feedback_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ip_pools",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ip_pools", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tls_results",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    policy_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    policy_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mx_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    receiving_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    sending_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    failure_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    count = table.Column<int>(type: "integer", nullable: false),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tls_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "confidential_recipients",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    confidential_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    code_attempts = table.Column<int>(type: "integer", nullable: false),
                    first_opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opens = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_confidential_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_confidential_recipients_confidential_messages_confidential_",
                        column: x => x.confidential_message_id,
                        principalSchema: "mail",
                        principalTable: "confidential_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sending_ips",
                schema: "mta",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    helo = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    warmup_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ptr_ok = table.Column<bool>(type: "boolean", nullable: true),
                    ptr_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sending_ips", x => x.id);
                    table.ForeignKey(
                        name: "fk_sending_ips_ip_pools_pool_id",
                        column: x => x.pool_id,
                        principalSchema: "mta",
                        principalTable: "ip_pools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sending_ip_days",
                schema: "mta",
                columns: table => new
                {
                    sending_ip_id = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    sent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sending_ip_days", x => new { x.sending_ip_id, x.day });
                    table.ForeignKey(
                        name: "fk_sending_ip_days_sending_ips_sending_ip_id",
                        column: x => x.sending_ip_id,
                        principalSchema: "mta",
                        principalTable: "sending_ips",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_confidential_messages_expires_at",
                schema: "mail",
                table: "confidential_messages",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_confidential_messages_tenant_id_rfc_message_id",
                schema: "mail",
                table: "confidential_messages",
                columns: new[] { "tenant_id", "rfc_message_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_confidential_recipients_confidential_message_id_address",
                schema: "mail",
                table: "confidential_recipients",
                columns: new[] { "confidential_message_id", "address" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_confidential_recipients_token_hash",
                schema: "mail",
                table: "confidential_recipients",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feedback_events_outbound_message_id_rcpt_kind",
                schema: "mta",
                table: "feedback_events",
                columns: new[] { "outbound_message_id", "rcpt", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feedback_events_sender_user_id_received_at",
                schema: "mta",
                table: "feedback_events",
                columns: new[] { "sender_user_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ip_pools_is_default",
                schema: "mta",
                table: "ip_pools",
                column: "is_default",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_ip_pools_name",
                schema: "mta",
                table: "ip_pools",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sending_ips_address",
                schema: "mta",
                table: "sending_ips",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sending_ips_pool_id",
                schema: "mta",
                table: "sending_ips",
                column: "pool_id");

            migrationBuilder.CreateIndex(
                name: "ix_tls_results_day_policy_domain_policy_type_mx_host_receiving",
                schema: "mta",
                table: "tls_results",
                columns: new[] { "day", "policy_domain", "policy_type", "mx_host", "receiving_ip", "sending_ip", "failure_type" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "confidential_recipients",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "feedback_events",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "sending_ip_days",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "tls_results",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "confidential_messages",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "sending_ips",
                schema: "mta");

            migrationBuilder.DropTable(
                name: "ip_pools",
                schema: "mta");

            migrationBuilder.DropColumn(
                name: "sending_lock_reason",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "sending_locked_at",
                schema: "identity",
                table: "users");
        }
    }
}
