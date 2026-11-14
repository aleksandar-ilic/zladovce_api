using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Zladovce.Infrastructure.Database;
using Zladovce.IntegrationTests.Infrastructure;
using Zladovce.Lambda;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CatalogSecurityTests(PostgresFixture postgres)
{
    public static readonly TheoryData<string, string> AdminEndpoints = new()
    {
        { "GET", "/api/v1/admin/categories" },
        { "POST", "/api/v1/admin/categories" },
        { "PUT", "/api/v1/admin/categories/order" },
        { "DELETE", $"/api/v1/admin/categories/{Guid.Empty}" },
        { "GET", "/api/v1/admin/products" },
        { "POST", "/api/v1/admin/products" },
        { "PUT", $"/api/v1/admin/products/{Guid.Empty}" },
        { "POST", $"/api/v1/admin/products/{Guid.Empty}/archive" },
        { "POST", $"/api/v1/admin/products/{Guid.Empty}/variants" },
        { "DELETE", $"/api/v1/admin/products/{Guid.Empty}/variants/{Guid.Empty}" },
        { "POST", $"/api/v1/admin/products/{Guid.Empty}/images" },
        { "PUT", $"/api/v1/admin/products/{Guid.Empty}/images/order" },
        { "DELETE", $"/api/v1/admin/products/{Guid.Empty}/images/{Guid.Empty}" },
        { "POST", "/api/v1/admin/media/upload-url" },
        { "GET", "/api/v1/admin/inventory" },
        { "POST", $"/api/v1/admin/inventory/{Guid.Empty}/adjustments" },
        { "PUT", $"/api/v1/admin/inventory/{Guid.Empty}/threshold" },
        { "GET", $"/api/v1/admin/inventory/{Guid.Empty}/movements" },
        { "GET", "/api/v1/admin/dashboard/low-stock" },
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_endpoints_need_authentication(string method, string path)
    {
        using var factory = new DevApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonBody() });

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_endpoints_refuse_tokens_without_an_administrator_group(string method, string path)
    {
        using var factory = new CognitoApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        var token = factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Customers"]);

        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonBody() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task Staff_may_manage_the_catalog()
    {
        using var factory = new DevApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        var staff = new AdminApi(client, $"urednik.{Unique.Token()}@example.com", "Staff");

        var category = await staff.CreateCategoryAsync();
        await staff.CreateProductAsync(category.Id());
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Deployed_environments_serve_the_catalog_without_development_features(string environment)
    {
        using var factory = new CustomApiFactory(environment, new Dictionary<string, string?>
        {
            ["Auth:Cognito:Region"] = CognitoApiFactory.Region,
            ["Auth:Cognito:UserPoolId"] = CognitoApiFactory.UserPoolId,
            ["Auth:Cognito:ClientId"] = CognitoApiFactory.ClientId,
            ["Cors:AllowedOrigins:0"] = "https://admin.dev.example",
            ["Media:BucketName"] = "zladovce-media-dev",
            ["Media:BaseUrl"] = "https://media.dev.example",
        })
        { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/catalog/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);

        var devHeaders = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/categories")
            .AsDevAdmin("owner@example.com", "Owner"));
        Assert.Equal(HttpStatusCode.Unauthorized, devHeaders.StatusCode);

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/admin/products");
        preflight.Headers.Add("Origin", "https://admin.dev.example");
        preflight.Headers.Add("Access-Control-Request-Method", "PATCH");
        var cors = await client.SendAsync(preflight);
        Assert.Contains("PATCH", string.Join(",", cors.Headers.GetValues("Access-Control-Allow-Methods")));
    }

    private static StringContent JsonBody() => new("{}", System.Text.Encoding.UTF8, "application/json");
}

/// <summary>The migration Lambda's bootstrap action against a real PostgreSQL 17 (RDS's <c>rds_iam</c> role is simulated).</summary>
[Collection(PostgresCollection.Name)]
public class RoleBootstrapTests(PostgresFixture postgres)
{
    private const string Migrator = "zladovce_migrator_test";
    private const string App = "zladovce_app_test";

    private sealed class FixedCredentials(string username, string password) : IMasterCredentialsProvider
    {
        public Task<DatabaseCredentials> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseCredentials(username, password));
    }

    [Fact]
    public async Task Bootstrap_then_migrate_gives_an_owner_role_and_a_data_only_app_role()
    {
        var master = new NpgsqlConnectionStringBuilder(postgres.ConnectionString);
        var databaseUrl = await postgres.CreateEmptyDatabaseAsync("bootstrap_db");
        await postgres.ExecuteAsync("DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rds_iam') THEN CREATE ROLE rds_iam; END IF; END $$;");

        // Connection settings without the master password: the bootstrap gets it from the credentials provider.
        var endpointOnly = new NpgsqlConnectionStringBuilder(databaseUrl) { Username = "nobody", Password = null }.ConnectionString;
        var bootstrapConfig = Configuration(new()
        {
            ["Database:ConnectionString"] = endpointOnly,
            ["Database:User"] = Migrator,
            ["Database:AppUser"] = App,
            ["Database:MasterSecretArn"] = "arn:aws:secretsmanager:eu-central-1:000000000000:secret:test",
        });

        var runner = new MigrationRunner(bootstrapConfig, new FixedCredentials(master.Username!, master.Password!));
        var first = await runner.HandleAsync(new MigrationRequest { Action = "bootstrap" }, new FakeLambdaContext());
        var second = await runner.HandleAsync(new MigrationRequest { Action = "bootstrap" }, new FakeLambdaContext());
        Assert.Equal("bootstrap", first.Action);
        Assert.Equal([Migrator, App], second.Roles!);

        Assert.True(await postgres.ScalarAsync<bool>($"SELECT pg_has_role('{Migrator}', 'rds_iam', 'member')"));
        Assert.True(await postgres.ScalarAsync<bool>($"SELECT pg_has_role('{App}', 'rds_iam', 'member')"));

        // Test only: passwords instead of IAM tokens, so the roles can sign in to the container.
        await postgres.ExecuteAsync($"ALTER ROLE {Migrator} PASSWORD 'migrator_pw'");
        await postgres.ExecuteAsync($"ALTER ROLE {App} PASSWORD 'app_pw'");
        var asMigrator = new NpgsqlConnectionStringBuilder(databaseUrl) { Username = Migrator, Password = "migrator_pw" }.ConnectionString;
        var asApp = new NpgsqlConnectionStringBuilder(databaseUrl) { Username = App, Password = "app_pw" }.ConnectionString;

        var migrate = await new MigrationRunner(Configuration(new() { ["Database:ConnectionString"] = asMigrator }))
            .HandleAsync(new MigrationRequest(), new FakeLambdaContext());
        Assert.Equal(2, migrate.AppliedMigrations.Count);

        await using (var connection = new NpgsqlConnection(databaseUrl))
        {
            await connection.OpenAsync();
            await using var owner = new NpgsqlCommand("SELECT tableowner FROM pg_tables WHERE tablename = 'products'", connection);
            Assert.Equal(Migrator, await owner.ExecuteScalarAsync());
        }

        // The application role reads and writes data ...
        await using (var connection = new NpgsqlConnection(asApp))
        {
            await connection.OpenAsync();
            var id = Guid.CreateVersion7();
            await Execute(connection,
                $"INSERT INTO categories (id, name, slug, sort_order, is_active, created_at, updated_at) VALUES ('{id}', 'Džem', 'dzem', 0, true, now(), now())");
            await using (var read = new NpgsqlCommand("SELECT zladovce_unaccent(name) || '|' || lower(zladovce_unaccent(name)) FROM categories WHERE slug = 'dzem'", connection))
            {
                Assert.Equal("Dzem|dzem", await read.ExecuteScalarAsync());
            }

            await Execute(connection, $"UPDATE categories SET name = 'Med' WHERE id = '{id}'");
            await using (var read = new NpgsqlCommand("SELECT zladovce_unaccent(name) FROM categories WHERE slug = 'dzem'", connection))
            {
                Assert.Equal("Med", await read.ExecuteScalarAsync());
            }

            await Execute(connection, $"DELETE FROM categories WHERE id = '{id}'");

            // ... but cannot change the schema.
            var create = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, "CREATE TABLE intruder (id int)"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, create.SqlState);
            var drop = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, "DROP TABLE categories"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, drop.SqlState);
            var alter = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, "ALTER TABLE products ADD COLUMN x int"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, alter.SqlState);
        }

        // The API works end to end when connected as the application role.
        using var factory = new DevApiFactory { ConnectionString = asApp };
        using var client = factory.CreateClient();
        var api = new AdminApi(client);
        var category = await api.CreateCategoryAsync();
        var product = await api.CreateProductAsync(category.Id(), "Džem od kajsija");
        var found = await api.GetAsync("/api/v1/admin/products?q=dzem");
        Assert.True(found.Items().Length == 1, found.ToString());
        Assert.Equal([product.Id()], found.Items().Select(p => p.Id()));
    }

    [Fact]
    public async Task Unknown_action_fails()
    {
        var runner = new MigrationRunner(Configuration(new() { ["Database:ConnectionString"] = postgres.ConnectionString }));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.HandleAsync(new MigrationRequest { Action = "drop" }, new FakeLambdaContext()));
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration(Dictionary<string, string?> values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
