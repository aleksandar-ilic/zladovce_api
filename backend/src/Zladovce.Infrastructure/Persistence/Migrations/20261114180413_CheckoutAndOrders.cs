using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zladovce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CheckoutAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "courier_companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "citext", nullable: false),
                    tracking_url_template = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_courier_companies", x => x.id);
                    table.CheckConstraint("ck_courier_companies_sort_order", "sort_order >= 0");
                    table.CheckConstraint("ck_courier_companies_tracking_url_template", "tracking_url_template IS NULL OR (tracking_url_template LIKE 'https://%' AND position('{trackingNumber}' in tracking_url_template) > 0)");
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_number_counters",
                columns: table => new
                {
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_number_counters", x => x.year);
                    table.CheckConstraint("ck_order_number_counters_last_value", "last_value > 0");
                    table.CheckConstraint("ck_order_number_counters_year", "year BETWEEN 2000 AND 9999");
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_buckets",
                columns: table => new
                {
                    bucket_key = table.Column<string>(type: "text", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_buckets", x => new { x.bucket_key, x.window_start });
                    table.CheckConstraint("ck_rate_limit_buckets_count", "count > 0");
                });

            migrationBuilder.CreateTable(
                name: "addresses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    street = table.Column<string>(type: "text", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_addresses", x => x.id);
                    table.CheckConstraint("ck_addresses_postal_code", "postal_code ~ '^[0-9]{5}$'");
                    table.ForeignKey(
                        name: "fk_addresses_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipping_address_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    contact_first_name = table.Column<string>(type: "text", nullable: false),
                    contact_last_name = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "citext", nullable: false),
                    contact_phone = table.Column<string>(type: "text", nullable: false),
                    delivery_note = table.Column<string>(type: "text", nullable: true),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    shipping_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    collected_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_collected", "(collected_amount IS NULL AND collected_at IS NULL) OR (collected_amount > 0 AND collected_at IS NOT NULL)");
                    table.CheckConstraint("ck_orders_order_number", "order_number ~ '^ZLD-[0-9]{4}-[0-9]{6,}$'");
                    table.CheckConstraint("ck_orders_payment_method", "payment_method IN ('CashOnDelivery')");
                    table.CheckConstraint("ck_orders_request_hash", "octet_length(request_hash) = 32");
                    table.CheckConstraint("ck_orders_shipping_price", "shipping_price >= 0");
                    table.CheckConstraint("ck_orders_status", "status IN ('Received', 'Accepted', 'Preparing', 'Shipped', 'Delivered', 'Cancelled', 'Rejected', 'DeliveryFailed')");
                    table.CheckConstraint("ck_orders_subtotal", "subtotal > 0");
                    table.CheckConstraint("ck_orders_total", "total = subtotal + shipping_price");
                    table.ForeignKey(
                        name: "fk_orders_addresses_shipping_address_id",
                        column: x => x.shipping_address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    variant_name = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(12,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_line_total", "line_total = unit_price * quantity");
                    table.CheckConstraint("ck_order_items_quantity", "quantity > 0");
                    table.CheckConstraint("ck_order_items_unit_price", "unit_price > 0");
                    table.ForeignKey(
                        name: "fk_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_items_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_notes", x => x.id);
                    table.CheckConstraint("ck_order_notes_note", "btrim(note) <> ''");
                    table.ForeignKey(
                        name: "fk_order_notes_admin_users_admin_user_id",
                        column: x => x.admin_user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_notes_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<string>(type: "text", nullable: true),
                    new_status = table.Column<string>(type: "text", nullable: false),
                    changed_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    internal_note = table.Column<string>(type: "text", nullable: true),
                    customer_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_new_status", "new_status IN ('Received', 'Accepted', 'Preparing', 'Shipped', 'Delivered', 'Cancelled', 'Rejected', 'DeliveryFailed')");
                    table.CheckConstraint("ck_order_status_history_previous_status", "previous_status IS NULL OR previous_status IN ('Received', 'Accepted', 'Preparing', 'Shipped', 'Delivered', 'Cancelled', 'Rejected', 'DeliveryFailed')");
                    table.ForeignKey(
                        name: "fk_order_status_history_admin_users_changed_by_admin_id",
                        column: x => x.changed_by_admin_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    courier_company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracking_number = table.Column<string>(type: "text", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.ForeignKey(
                        name: "fk_shipments_courier_companies_courier_company_id",
                        column: x => x.courier_company_id,
                        principalTable: "courier_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipments_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_order_id",
                table: "inventory_movements",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_addresses_customer_id",
                table: "addresses",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_courier_companies_name",
                table: "courier_companies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_email",
                table: "customers",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_items_order_id",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_variant_id",
                table: "order_items",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_notes_admin_user_id",
                table: "order_notes",
                column: "admin_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_notes_order_id_created_at",
                table: "order_notes",
                columns: new[] { "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_changed_by_admin_id",
                table: "order_status_history",
                column: "changed_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_new_status_created_at",
                table: "order_status_history",
                columns: new[] { "new_status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_id_created_at",
                table: "order_status_history",
                columns: new[] { "order_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_contact_email",
                table: "orders",
                column: "contact_email");

            migrationBuilder.CreateIndex(
                name: "ix_orders_contact_phone",
                table: "orders",
                column: "contact_phone");

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_id",
                table: "orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_order_number",
                table: "orders",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_placed_at",
                table: "orders",
                column: "placed_at");

            migrationBuilder.CreateIndex(
                name: "ix_orders_shipping_address_id",
                table: "orders",
                column: "shipping_address_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_placed_at",
                table: "orders",
                columns: new[] { "status", "placed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_rate_limit_buckets_window_start",
                table: "rate_limit_buckets",
                column: "window_start");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_courier_company_id",
                table: "shipments",
                column: "courier_company_id");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_order_id",
                table: "shipments",
                column: "order_id");

            migrationBuilder.AddForeignKey(
                name: "fk_inventory_movements_orders_order_id",
                table: "inventory_movements",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_inventory_movements_orders_order_id",
                table: "inventory_movements");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "order_notes");

            migrationBuilder.DropTable(
                name: "order_number_counters");

            migrationBuilder.DropTable(
                name: "order_status_history");

            migrationBuilder.DropTable(
                name: "rate_limit_buckets");

            migrationBuilder.DropTable(
                name: "shipments");

            migrationBuilder.DropTable(
                name: "courier_companies");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "addresses");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropIndex(
                name: "ix_inventory_movements_order_id",
                table: "inventory_movements");
        }
    }
}
