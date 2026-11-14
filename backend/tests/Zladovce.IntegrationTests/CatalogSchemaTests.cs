using Npgsql;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CatalogSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Money_is_numeric_12_2_and_amounts_numeric_10_3()
    {
        var columns = await postgres.ScalarAsync<string>(
            """
            SELECT string_agg(column_name || ':' || numeric_precision || ',' || numeric_scale, ';' ORDER BY column_name)
            FROM information_schema.columns
            WHERE table_name = 'product_variants' AND data_type = 'numeric'
            """);

        Assert.Equal("discount_price:12,2;regular_price:12,2;unit_amount:10,3", columns);
    }

    [Theory]
    [InlineData("ix_categories_slug", "WHERE (deleted_at IS NULL)")]
    [InlineData("ix_products_slug", "WHERE (deleted_at IS NULL)")]
    [InlineData("ix_products_sku", "WHERE (deleted_at IS NULL)")]
    [InlineData("ix_product_variants_sku", "WHERE (deleted_at IS NULL)")]
    [InlineData("ux_product_images_one_main", "WHERE is_main")]
    [InlineData("ix_products_name_trgm", "gin (lower(zladovce_unaccent(name)) gin_trgm_ops)")]
    [InlineData("ix_inventory_movements_variant_id_created_at", "created_at DESC")]
    public async Task Indexes_are_as_designed(string index, string expectedFragment)
    {
        var definition = await postgres.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = @i", ("i", index));
        Assert.NotNull(definition);
        Assert.Contains(expectedFragment, definition);
    }

    [Fact]
    public async Task Unaccent_wrapper_is_immutable_and_removes_serbian_diacritics()
    {
        Assert.Equal("i", await postgres.ScalarAsync<string>(
            "SELECT provolatile::text FROM pg_proc WHERE proname = 'zladovce_unaccent'"));
        Assert.Equal("Dzem od sljiva cokolada cevap", await postgres.ScalarAsync<string>(
            "SELECT zladovce_unaccent('Džem od šljiva čokolada ćevap')"));
    }

    [Fact]
    public async Task Price_rules_are_enforced_by_the_database()
    {
        var categoryId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await postgres.ExecuteAsync(
            "INSERT INTO categories (id, name, slug, sort_order, is_active, created_at, updated_at) VALUES (@id, 'x', @slug, 0, true, now(), now())",
            ("id", categoryId), ("slug", $"schema-{Unique.Token()}"));
        await postgres.ExecuteAsync(
            """
            INSERT INTO products (id, category_id, name, slug, short_description, description, sku, is_active, is_featured, created_at, updated_at)
            VALUES (@id, @c, 'x', @slug, '', '', @sku, false, false, now(), now())
            """,
            ("id", productId), ("c", categoryId), ("slug", $"schema-{Unique.Token()}"), ("sku", $"SCH-{Unique.Token()}"));

        const string insert =
            """
            INSERT INTO product_variants (id, product_id, name, sku, unit_amount, measurement_unit, regular_price, discount_price, is_active, sort_order, created_at, updated_at)
            VALUES (@id, @p, 'x', @sku, 1, @unit, @regular, @discount, true, 0, now(), now())
            """;

        async Task<string?> Violation(decimal regular, object discount, string unit = "g")
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(insert,
                ("id", Guid.CreateVersion7()), ("p", productId), ("sku", $"V-{Unique.Token()}"), ("unit", unit), ("regular", regular), ("discount", discount)));
            return exception.ConstraintName;
        }

        Assert.Equal("ck_product_variants_regular_price", await Violation(0m, DBNull.Value));
        Assert.Equal("ck_product_variants_discount_price", await Violation(100m, 100m));
        Assert.Equal("ck_product_variants_measurement_unit", await Violation(100m, DBNull.Value, "lb"));
    }
}
