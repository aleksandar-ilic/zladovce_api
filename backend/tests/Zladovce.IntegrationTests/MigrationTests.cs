using Npgsql;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_cleanly_on_an_empty_database_and_are_idempotent()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync("migration_empty_db");

        var first = await PostgresFixture.RunMigrationsAsync(connectionString);
        var second = await PostgresFixture.RunMigrationsAsync(connectionString);

        Assert.Equal(2, first.AppliedMigrations.Count);
        Assert.EndsWith("_InitialFoundation", first.AppliedMigrations[0]);
        Assert.EndsWith("_CatalogAndInventory", first.AppliedMigrations[1]);
        Assert.Equal(first.AppliedMigrations[1], first.CurrentMigration);

        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(first.CurrentMigration, second.CurrentMigration);
    }

    [Fact]
    public async Task Schema_has_exactly_the_foundation_and_catalog_tables()
    {
        var tables = await postgres.ScalarAsync<string[]>(
            """
            SELECT array_agg(table_name::text ORDER BY table_name)
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """);

        Assert.Equal(
            ["admin_users", "audit_logs", "categories", "inventory", "inventory_movements", "product_images", "product_variants", "products", "store_settings"],
            tables!);
    }

    [Fact]
    public async Task Required_extensions_are_installed()
    {
        var extensions = await postgres.ScalarAsync<string[]>(
            "SELECT array_agg(extname::text ORDER BY extname) FROM pg_extension WHERE extname IN ('citext', 'unaccent', 'pg_trgm')");

        Assert.Equal(["citext", "pg_trgm", "unaccent"], extensions!);
    }

    [Theory]
    [InlineData("admin_users", "id", "uuid")]
    [InlineData("admin_users", "email", "citext")]
    [InlineData("admin_users", "created_at", "timestamptz")]
    [InlineData("admin_users", "last_login_at", "timestamptz")]
    [InlineData("store_settings", "shipping_price", "numeric")]
    [InlineData("store_settings", "other_links", "jsonb")]
    [InlineData("store_settings", "updated_at", "timestamptz")]
    [InlineData("audit_logs", "changes", "jsonb")]
    [InlineData("audit_logs", "ip_address", "inet")]
    [InlineData("audit_logs", "created_at", "timestamptz")]
    public async Task Columns_use_the_designed_types(string table, string column, string expectedType)
    {
        var type = await postgres.ScalarAsync<string>(
            "SELECT udt_name::text FROM information_schema.columns WHERE table_name = @t AND column_name = @c",
            ("t", table), ("c", column));

        Assert.Equal(expectedType, type);
    }

    [Fact]
    public async Task Money_columns_are_numeric_12_2()
    {
        var precision = await postgres.ScalarAsync<string>(
            """
            SELECT string_agg(column_name || ':' || numeric_precision || ',' || numeric_scale, ';' ORDER BY column_name)
            FROM information_schema.columns
            WHERE table_name = 'store_settings' AND data_type = 'numeric'
            """);

        Assert.Equal("free_shipping_threshold:12,2;min_order_amount:12,2;shipping_price:12,2", precision);
    }

    [Fact]
    public async Task Store_settings_is_seeded_with_one_neutral_row()
    {
        Assert.Equal(1L, await postgres.ScalarAsync<long>("SELECT count(*) FROM store_settings"));
        Assert.Equal("Zladovce", await postgres.ScalarAsync<string>("SELECT store_name FROM store_settings WHERE id = 1"));
        Assert.Equal(
            0L,
            await postgres.ScalarAsync<long>(
                """
                SELECT count(*) FROM store_settings
                WHERE contact_email IS NOT NULL OR phone IS NOT NULL OR business_address IS NOT NULL
                   OR facebook_url IS NOT NULL OR instagram_url IS NOT NULL OR other_links <> '[]'::jsonb
                """));
    }

    [Fact]
    public async Task Store_settings_allows_only_the_row_with_id_1()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            "INSERT INTO store_settings (id, store_name, shipping_enabled, shipping_price, other_links, updated_at) VALUES (2, 'x', true, 0, '[]', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_store_settings_singleton", exception.ConstraintName);
    }

    [Fact]
    public async Task Admin_role_is_restricted_and_email_is_unique_ignoring_case()
    {
        const string insert =
            "INSERT INTO admin_users (id, cognito_sub, email, full_name, role, is_active, created_at, updated_at) VALUES (@id, @sub, @email, 'x', @role, true, now(), now())";

        var badRole = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            insert, ("id", Guid.CreateVersion7()), ("sub", "constraint-sub-1"), ("email", "constraint1@example.com"), ("role", "Admin")));
        Assert.Equal("ck_admin_users_role", badRole.ConstraintName);

        await postgres.ExecuteAsync(
            insert, ("id", Guid.CreateVersion7()), ("sub", "constraint-sub-2"), ("email", "Case@Example.com"), ("role", "Staff"));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            insert, ("id", Guid.CreateVersion7()), ("sub", "constraint-sub-3"), ("email", "case@example.COM"), ("role", "Staff")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }
}
