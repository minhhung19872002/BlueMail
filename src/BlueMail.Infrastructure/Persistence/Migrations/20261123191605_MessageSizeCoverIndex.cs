using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessageSizeCoverIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mailbox size (shown with the labels on every page load) from an index-only scan instead of the wide rows.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_messages_size_cover ON mail.messages (tenant_id, mailbox_id)
                    INCLUDE (size) WHERE deleted_at IS NULL AND expunged_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS mail.ix_messages_size_cover;");
        }
    }
}
