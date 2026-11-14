using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zladovce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    image_key = table.Column<string>(type: "text", nullable: true),
                    image_width = table.Column<int>(type: "integer", nullable: true),
                    image_height = table.Column<int>(type: "integer", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_image", "(image_key IS NULL AND image_width IS NULL AND image_height IS NULL) OR (image_key IS NOT NULL AND image_width > 0 AND image_height > 0)");
                    table.CheckConstraint("ck_categories_sort_order", "sort_order >= 0");
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    short_description = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_featured = table.Column<bool>(type: "boolean", nullable: false),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_products_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    alt_text = table.Column<string>(type: "text", nullable: true),
                    is_main = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_images", x => x.id);
                    table.CheckConstraint("ck_product_images_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_product_images_sort_order", "sort_order >= 0");
                    table.ForeignKey(
                        name: "fk_product_images_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    unit_amount = table.Column<decimal>(type: "numeric(10,3)", nullable: false),
                    measurement_unit = table.Column<string>(type: "text", nullable: false),
                    regular_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    discount_price = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.CheckConstraint("ck_product_variants_discount_price", "discount_price IS NULL OR (discount_price > 0 AND discount_price < regular_price)");
                    table.CheckConstraint("ck_product_variants_measurement_unit", "measurement_unit IN ('g', 'kg', 'ml', 'l', 'kom')");
                    table.CheckConstraint("ck_product_variants_regular_price", "regular_price > 0");
                    table.CheckConstraint("ck_product_variants_sort_order", "sort_order >= 0");
                    table.CheckConstraint("ck_product_variants_unit_amount", "unit_amount > 0");
                    table.ForeignKey(
                        name: "fk_product_variants_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory",
                columns: table => new
                {
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_on_hand = table.Column<int>(type: "integer", nullable: false),
                    low_stock_threshold = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory", x => x.variant_id);
                    table.CheckConstraint("ck_inventory_low_stock_threshold", "low_stock_threshold >= 0");
                    table.CheckConstraint("ck_inventory_quantity_on_hand", "quantity_on_hand >= 0");
                    table.ForeignKey(
                        name: "fk_inventory_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_type = table.Column<string>(type: "text", nullable: false),
                    quantity_delta = table.Column<int>(type: "integer", nullable: false),
                    quantity_after = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_movements", x => x.id);
                    table.CheckConstraint("ck_inventory_movements_movement_type", "movement_type IN ('InitialStock', 'ManualAdjustment', 'OrderPlaced', 'OrderCancelled', 'ReturnRestock')");
                    table.CheckConstraint("ck_inventory_movements_quantity_after", "quantity_after >= 0");
                    table.CheckConstraint("ck_inventory_movements_quantity_delta", "quantity_delta <> 0");
                    table.CheckConstraint("ck_inventory_movements_reason", "movement_type <> 'ManualAdjustment' OR (reason IS NOT NULL AND btrim(reason) <> '')");
                    table.ForeignKey(
                        name: "fk_inventory_movements_admin_users_admin_user_id",
                        column: x => x.admin_user_id,
                        principalTable: "admin_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inventory_movements_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_slug",
                table: "categories",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_admin_user_id",
                table: "inventory_movements",
                column: "admin_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_variant_id_created_at",
                table: "inventory_movements",
                columns: new[] { "variant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_product_images_product_id",
                table: "product_images",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_images_one_main",
                table: "product_images",
                column: "product_id",
                unique: true,
                filter: "is_main");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id",
                table: "product_variants",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_sku",
                table: "product_variants",
                column: "sku",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_is_active_is_featured",
                table: "products",
                columns: new[] { "is_active", "is_featured" });

            migrationBuilder.CreateIndex(
                name: "ix_products_sku",
                table: "products",
                column: "sku",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_slug",
                table: "products",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            // unaccent() is only STABLE (it depends on the dictionary search path), so it cannot be used in an index.
            // This wrapper names the dictionary explicitly and is therefore safe to declare IMMUTABLE.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION zladovce_unaccent(text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $$;
                """);

            // Diacritic-insensitive search ("dzem" finds "Džem"): LIKE '%term%' on lower(zladovce_unaccent(name)).
            migrationBuilder.Sql(
                "CREATE INDEX ix_products_name_trgm ON products USING gin (lower(zladovce_unaccent(name)) gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_products_name_trgm;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS zladovce_unaccent(text);");

            migrationBuilder.DropTable(
                name: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_movements");

            migrationBuilder.DropTable(
                name: "product_images");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "categories");
        }
    }
}
