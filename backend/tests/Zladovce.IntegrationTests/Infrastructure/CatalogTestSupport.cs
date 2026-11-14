using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Zladovce.Application.Media;
using Zladovce.Infrastructure.Media;

namespace Zladovce.IntegrationTests.Infrastructure;

/// <summary>
/// A real S3-compatible store (MinIO, the same open-source server as in docker-compose.yml) for the whole test run.
/// Media tests upload through real pre-signed POSTs and check the real objects.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Bucket = "zladovce-media-test";
    public const string AccessKey = "zladovce_test";
    public const string SecretKey = "zladovce_test_secret";
    private const ushort ApiPort = 9000;

    private readonly IContainer _container = new ContainerBuilder("cgr.dev/chainguard/minio:latest")
        .WithCommand("server", "/tmp/data")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/minio/health/ready")))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}";

    public MediaOptions Options => new()
    {
        BucketName = Bucket,
        BaseUrl = $"{ServiceUrl}/{Bucket}",
        ServiceUrl = ServiceUrl,
        ForcePathStyle = true,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        ProcessInline = true,
    };

    /// <summary>Settings for an API host that uses this store.</summary>
    public Dictionary<string, string?> Settings(bool processInline = true) => new()
    {
        ["Media:BucketName"] = Bucket,
        ["Media:BaseUrl"] = $"{ServiceUrl}/{Bucket}",
        ["Media:ServiceUrl"] = ServiceUrl,
        ["Media:ForcePathStyle"] = "true",
        ["Media:AccessKey"] = AccessKey,
        ["Media:SecretKey"] = SecretKey,
        ["Media:ProcessInline"] = processInline ? "true" : "false",
        ["Media:ReadyWaitSeconds"] = "1",
    };

    public IAmazonS3 Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Client = S3ClientFactory.Create(Options);
        await Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        await _container.DisposeAsync();
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            await Client.GetObjectMetadataAsync(Bucket, key);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<byte[]> ReadAsync(string key)
    {
        using var response = await Client.GetObjectAsync(Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

/// <summary>An HTTP client that talks to the admin API with the development headers.</summary>
public sealed class AdminApi(HttpClient client, string email = "katalog.owner@example.com", string role = "Owner")
{
    public HttpClient Client => client;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).AsDevAdmin(email, role);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    public async Task<JsonElement> JsonAsync(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await SendAsync(method, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public Task<JsonElement> GetAsync(string url) => JsonAsync(HttpMethod.Get, url);

    public async Task<JsonElement> CreateCategoryAsync(string? name = null, bool isActive = true, string? slug = null) =>
        await JsonAsync(HttpMethod.Post, "/api/v1/admin/categories",
            Category(name ?? $"Kategorija {Unique.Token()}", slug: slug, isActive: isActive), HttpStatusCode.Created);

    /// <summary>A complete category body (create and update require every member).</summary>
    public static object Category(
        string name,
        string? slug = null,
        string? imageKey = null,
        bool isActive = true,
        int sortOrder = 0,
        string? description = null,
        string? seoTitle = null,
        string? seoDescription = null) =>
        new { name, slug, description, imageKey, sortOrder, isActive, seoTitle, seoDescription };

    public static object Variant(string sku, decimal price = 500m, decimal? discount = null, int stock = 10, int threshold = 2, bool isActive = true, string name = "370 g") =>
        new { name, sku, unitAmount = 370m, measurementUnit = "g", regularPrice = price, discountPrice = discount, isActive, initialStock = stock, lowStockThreshold = threshold };

    public async Task<JsonElement> CreateProductAsync(Guid categoryId, string? name = null, bool isActive = true, bool isFeatured = false, params object[] variants)
    {
        var token = Unique.Token();
        return await JsonAsync(HttpMethod.Post, "/api/v1/admin/products", new
        {
            categoryId,
            name = name ?? $"Proizvod {token}",
            sku = $"P-{token}",
            shortDescription = "Kratak opis",
            description = "<p>Opis</p>",
            isActive,
            isFeatured,
            variants = variants.Length > 0 ? variants : [Variant($"V-{token}")],
        }, HttpStatusCode.Created);
    }
}

public static class Unique
{
    /// <summary>Short random token for names, slugs and SKUs, so tests never collide in the shared database.</summary>
    public static string Token() => Guid.NewGuid().ToString("N")[..10];
}

public static class JsonExtensions
{
    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static JsonElement[] Items(this JsonElement element) => [.. element.GetProperty("items").EnumerateArray()];
}
