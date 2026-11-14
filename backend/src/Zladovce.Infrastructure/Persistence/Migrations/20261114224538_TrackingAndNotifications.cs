using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zladovce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackingAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedup_key = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient = table.Column<string>(type: "citext", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    encrypted_token = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox", x => x.id);
                    table.CheckConstraint("ck_email_outbox_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_email_outbox_sent", "(status = 'Sent') = (sent_at IS NOT NULL)");
                    table.CheckConstraint("ck_email_outbox_status", "status IN ('Pending', 'Processing', 'Sent', 'Dead')");
                    table.CheckConstraint("ck_email_outbox_template", "template IN ('OrderReceived', 'OrderAccepted', 'OrderPreparing', 'OrderShipped', 'OrderDelivered', 'OrderCancelled')");
                    table.ForeignKey(
                        name: "fk_email_outbox_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_tracking_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_tracking_tokens", x => x.id);
                    table.CheckConstraint("ck_order_tracking_tokens_token_hash", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_order_tracking_tokens_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "email_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ses_message_id = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_logs", x => x.id);
                    table.CheckConstraint("ck_email_logs_attempt_number", "attempt_number > 0");
                    table.ForeignKey(
                        name: "fk_email_logs_email_outbox_outbox_id",
                        column: x => x.outbox_id,
                        principalTable: "email_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_logs_outbox_id",
                table: "email_logs",
                column: "outbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_dedup_key",
                table: "email_outbox",
                column: "dedup_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_due",
                table: "email_outbox",
                column: "next_attempt_at",
                filter: "status IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_order_id_created_at",
                table: "email_outbox",
                columns: new[] { "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_status_created_at",
                table: "email_outbox",
                columns: new[] { "status", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_order_tracking_tokens_order_id",
                table: "order_tracking_tokens",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_tracking_tokens_token_hash",
                table: "order_tracking_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_logs");

            migrationBuilder.DropTable(
                name: "order_tracking_tokens");

            migrationBuilder.DropTable(
                name: "email_outbox");
        }
    }
}
