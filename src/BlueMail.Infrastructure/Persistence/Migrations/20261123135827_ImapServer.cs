using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImapServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written: mail.message_labels is outside the EF model's migrations (partitioned table).
            migrationBuilder.Sql("ALTER TABLE mail.message_labels ADD COLUMN imap_deleted boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_label_changes_label_modseq ON mail.label_changes (tenant_id, label_id, modseq);");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE mail.message_labels DROP COLUMN imap_deleted;");

        }
    }
}
