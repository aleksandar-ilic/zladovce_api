using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zladovce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cognito_sub = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_users", x => x.id);
                    table.CheckConstraint("ck_admin_users_role", "role IN ('Owner', 'Staff')");
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: true),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_logs_admin_users_admin_user_id",
                        column: x => x.admin_user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "store_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    store_name = table.Column<string>(type: "text", nullable: false),
                    logo_key = table.Column<string>(type: "text", nullable: true),
                    contact_email = table.Column<string>(type: "citext", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    business_address = table.Column<string>(type: "text", nullable: true),
                    shipping_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    shipping_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    free_shipping_threshold = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    min_order_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    estimated_delivery_text = table.Column<string>(type: "text", nullable: true),
                    delivery_info = table.Column<string>(type: "text", nullable: true),
                    facebook_url = table.Column<string>(type: "text", nullable: true),
                    instagram_url = table.Column<string>(type: "text", nullable: true),
                    other_links = table.Column<string>(type: "jsonb", nullable: false),
                    default_seo_title = table.Column<string>(type: "text", nullable: true),
                    default_seo_description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_store_settings", x => x.id);
                    table.CheckConstraint("ck_store_settings_free_shipping_threshold", "free_shipping_threshold IS NULL OR free_shipping_threshold > 0");
                    table.CheckConstraint("ck_store_settings_min_order_amount", "min_order_amount IS NULL OR min_order_amount > 0");
                    table.CheckConstraint("ck_store_settings_shipping_price", "shipping_price >= 0");
                    table.CheckConstraint("ck_store_settings_singleton", "id = 1");
                    table.ForeignKey(
                        name: "fk_store_settings_admin_users_updated_by_admin_id",
                        column: x => x.updated_by_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_users_cognito_sub",
                table: "admin_users",
                column: "cognito_sub",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_admin_users_email",
                table: "admin_users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_admin_user_id",
                table: "audit_logs",
                column: "admin_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_store_settings_updated_by_admin_id",
                table: "store_settings",
                column: "updated_by_admin_id");

            // Seed: the single store settings row with neutral values. Only the store name is known;
            // contact data, links and delivery texts are entered by the owner in the admin application.
            migrationBuilder.Sql(
                """
                INSERT INTO store_settings (id, store_name, shipping_enabled, shipping_price, other_links, updated_at)
                VALUES (1, 'Zladovce', TRUE, 0.00, '[]'::jsonb, now())
                ON CONFLICT (id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "store_settings");

            migrationBuilder.DropTable(
                name: "admin_users");
        }
    }
}
