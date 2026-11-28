using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessagePriorityAndReadReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // mail.messages is hand-made (partitioned): the sender's importance (1 high, 3 normal, 5 low) and the
            // Disposition-Notification-To address of a read receipt request (RFC 8098). Constant defaults: no table rewrite.
            migrationBuilder.Sql("ALTER TABLE mail.messages ADD COLUMN priority smallint NOT NULL DEFAULT 3");
            migrationBuilder.Sql("ALTER TABLE mail.messages ADD COLUMN mdn_to text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE mail.messages DROP COLUMN mdn_to");
            migrationBuilder.Sql("ALTER TABLE mail.messages DROP COLUMN priority");
        }
    }
}
