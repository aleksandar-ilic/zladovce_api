using System.Net;
using System.Text.Json;
using Amazon.Lambda.S3Events;
using ImageMagick;
using Microsoft.Extensions.Configuration;
using Zladovce.IntegrationTests.Infrastructure;
using Zladovce.Lambda;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class MediaTests : IDisposable
{
    private readonly MinioFixture _minio;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _browser = new();
    private readonly AdminApi _api;

    public MediaTests(PostgresFixture postgres, MinioFixture minio)
    {
        _minio = minio;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _api = new AdminApi(_client);
    }

    public void Dispose()
    {
        _browser.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    internal static byte[] Image(uint width, uint height, MagickFormat format)
    {
        using var image = new MagickImage(MagickColors.Goldenrod, width, height) { Format = format };
        if (format == MagickFormat.Jpeg)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCamera");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            image.SetProfile(exif);
        }

        return image.ToByteArray();
    }

    /// <summary>Asks the API for an upload and posts the file exactly as a browser form would.</summary>
    private async Task<(string Key, HttpResponseMessage Upload)> UploadAsync(
        byte[] content, string contentType, string purpose = "product", string? sentContentType = null)
    {
        var grant = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/media/upload-url",
            new { fileName = "slika.jpg", contentType, size = content.Length, purpose });

        using var form = new MultipartFormDataContent();
        foreach (var field in grant.GetProperty("fields").EnumerateObject())
        {
            form.Add(new StringContent(field.Value.GetString()!), field.Name);
        }

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(sentContentType ?? contentType);
        form.Add(file, "file", "slika");

        var upload = await _browser.PostAsync(grant.GetProperty("uploadUrl").GetString(), form);
        return (grant.GetProperty("key").GetString()!, upload);
    }

    [Fact]
    public async Task Full_product_image_flow_from_upload_to_delete()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());

        var (firstKey, firstUpload) = await UploadAsync(Image(2000, 1000, MagickFormat.Jpeg), "image/jpeg");
        Assert.Equal(HttpStatusCode.NoContent, firstUpload.StatusCode);
        Assert.StartsWith("uploads/product/", firstKey);
        Assert.True(await _minio.ExistsAsync(firstKey));

        var attached = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = firstKey, altText = "Tegla džema" }, HttpStatusCode.Created);
        var first = attached.GetProperty("images")[0];
        Assert.True(first.GetProperty("isMain").GetBoolean());
        Assert.Equal([400, 800, 1600], first.GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()));
        Assert.Equal(2000, first.GetProperty("width").GetInt32());
        Assert.Equal(1000, first.GetProperty("height").GetInt32());
        Assert.Equal("Tegla džema", first.GetProperty("altText").GetString());

        var baseUrl = first.GetProperty("baseUrl").GetString()!;
        var storageKey = $"media/products/{first.Id()}";
        Assert.Equal($"{_minio.Options.BaseUrl}/{storageKey}", baseUrl);
        Assert.False(await _minio.ExistsAsync(firstKey));

        foreach (var width in new[] { 400, 800, 1600 })
        {
            using var rendition = new MagickImage(await _minio.ReadAsync($"{storageKey}/{width}.webp"));
            Assert.Equal(MagickFormat.WebP, rendition.Format);
            Assert.Equal((uint)width, rendition.Width);
            Assert.Equal((uint)(width / 2), rendition.Height);
            Assert.Null(rendition.GetExifProfile());
        }

        // Re-attaching the same upload is refused.
        var again = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images", new { uploadKey = firstKey });
        await ResponseAssertions.AssertProblemAsync(again, HttpStatusCode.Conflict, "IMAGE_ALREADY_ATTACHED");

        var (secondKey, _) = await UploadAsync(Image(600, 600, MagickFormat.Png), "image/png");
        var withTwo = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = secondKey }, HttpStatusCode.Created);
        var second = withTwo.GetProperty("images")[1];
        Assert.False(second.GetProperty("isMain").GetBoolean());
        Assert.Equal([400, 600], second.GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()));

        var reordered = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/images/order",
            new { imageIds = new[] { second.Id(), first.Id() }, mainImageId = second.Id() });
        Assert.Equal([second.Id(), first.Id()], reordered.GetProperty("images").EnumerateArray().Select(i => i.Id()));
        Assert.True(reordered.GetProperty("images")[0].GetProperty("isMain").GetBoolean());
        Assert.False(reordered.GetProperty("images")[1].GetProperty("isMain").GetBoolean());

        var incomplete = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/images/order",
            new { imageIds = new[] { second.Id() }, mainImageId = (Guid?)null });
        await ResponseAssertions.AssertProblemAsync(incomplete, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var alt = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/images/{first.Id()}", new { altText = "Nov opis" });
        Assert.Equal("Nov opis", alt.GetProperty("images").EnumerateArray().Single(i => i.Id() == first.Id()).GetProperty("altText").GetString());

        // The product list and the shop show the main image.
        var listed = (await _api.GetAsync($"/api/v1/admin/products?category={category.Id()}")).Items()[0];
        Assert.Equal(second.Id(), listed.GetProperty("mainImage").Id());
        var page = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/catalog/products/{product.GetProperty("slug").GetString()}")).RootElement;
        Assert.Equal(2, page.GetProperty("images").GetArrayLength());
        Assert.Contains(second.Id().ToString(), page.GetProperty("images")[0].GetProperty("baseUrl").GetString());

        // Deleting the main image removes its files and promotes the other image.
        var afterDelete = await _api.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/products/{product.Id()}/images/{second.Id()}");
        var remaining = Assert.Single(afterDelete.GetProperty("images").EnumerateArray());
        Assert.Equal(first.Id(), remaining.Id());
        Assert.True(remaining.GetProperty("isMain").GetBoolean());
        Assert.False(await _minio.ExistsAsync($"media/products/{second.Id()}/400.webp"));
        Assert.False(await _minio.ExistsAsync($"media/products/{second.Id()}/600.webp"));
        Assert.False(await _minio.ExistsAsync($"media/products/{second.Id()}/manifest.json"));
        Assert.True(await _minio.ExistsAsync($"{storageKey}/800.webp"));

        var missing = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/products/{product.Id()}/images/{second.Id()}");
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.True(await new PostgresFixtureAccessor(_factory).AuditCountAsync("ProductImage.Deleted", second.Id()) == 1);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_rejected_and_removed()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());

        var (key, upload) = await UploadAsync("<html>ovo nije slika</html>"u8.ToArray(), "image/jpeg");
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);

        var attach = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images", new { uploadKey = key });
        await ResponseAssertions.AssertProblemAsync(attach, HttpStatusCode.BadRequest, "INVALID_IMAGE");
        Assert.False(await _minio.ExistsAsync(key));

        // Asking again gives the same answer (the rejection is remembered).
        var again = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images", new { uploadKey = key });
        await ResponseAssertions.AssertProblemAsync(again, HttpStatusCode.BadRequest, "INVALID_IMAGE");
    }

    [Fact]
    public async Task Storage_enforces_the_content_type_of_the_grant_and_unknown_uploads_are_reported()
    {
        var grant = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/media/upload-url",
            new { fileName = "a.png", contentType = "image/png", size = 10, purpose = "product" });
        using var form = new MultipartFormDataContent();
        foreach (var field in grant.GetProperty("fields").EnumerateObject())
        {
            form.Add(new StringContent(field.Name == "Content-Type" ? "text/html" : field.Value.GetString()!), field.Name);
        }

        form.Add(new ByteArrayContent(Image(10, 10, MagickFormat.Png)), "file", "a.png");
        var refused = await _browser.PostAsync(grant.GetProperty("uploadUrl").GetString(), form);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());
        var notUploaded = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = grant.GetProperty("key").GetString() });
        await ResponseAssertions.AssertProblemAsync(notUploaded, HttpStatusCode.BadRequest, "UPLOAD_NOT_FOUND");

        var foreignKey = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = "media/products/x/400.webp" });
        await ResponseAssertions.AssertProblemAsync(foreignKey, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var categoryKey = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = $"uploads/category/{Guid.CreateVersion7()}.png" });
        await ResponseAssertions.AssertProblemAsync(categoryKey, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Upload_url_request_is_validated()
    {
        var response = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/media/upload-url",
            new { fileName = "a.gif", contentType = "image/gif", size = 20 * 1024 * 1024, purpose = "blog" });

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("contentType", out _));
        Assert.True(errors.TryGetProperty("size", out _));
        Assert.True(errors.TryGetProperty("purpose", out _));
    }

    [Fact]
    public async Task Category_image_uses_the_same_flow_and_is_replaced_and_removed()
    {
        var (key, _) = await UploadAsync(Image(1200, 800, MagickFormat.WebP), "image/webp", purpose: "category");
        Assert.StartsWith("uploads/category/", key);

        var category = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/categories",
            AdminApi.Category($"Sa slikom {Unique.Token()}", imageKey: key), HttpStatusCode.Created);
        var image = category.GetProperty("image");
        var storageKey = image.GetProperty("key").GetString()!;
        Assert.StartsWith("media/categories/", storageKey);
        Assert.Equal([400, 800, 1200], image.GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()));
        Assert.True(await _minio.ExistsAsync($"{storageKey}/1200.webp"));

        var publicCategory = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/catalog/categories/{category.GetProperty("slug").GetString()}")).RootElement;
        Assert.EndsWith(storageKey, publicCategory.GetProperty("image").GetProperty("baseUrl").GetString());

        // Keeping the same key keeps the image.
        var kept = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}",
            AdminApi.Category(category.GetProperty("name").GetString()!, imageKey: storageKey));
        Assert.Equal(storageKey, kept.GetProperty("image").GetProperty("key").GetString());

        // A product upload cannot be used for a category.
        var (productKey, _) = await UploadAsync(Image(100, 100, MagickFormat.Png), "image/png");
        var wrongPurpose = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}",
            AdminApi.Category("x", imageKey: productKey));
        var wrongPurposeProblem = await ResponseAssertions.AssertProblemAsync(wrongPurpose, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(wrongPurposeProblem.GetProperty("errors").TryGetProperty("imageKey", out _));

        // Null removes the image and its files.
        var removed = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}",
            AdminApi.Category(category.GetProperty("name").GetString()!, imageKey: null));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("image").ValueKind);
        Assert.False(await _minio.ExistsAsync($"{storageKey}/1200.webp"));
    }

    [Fact]
    public async Task Archiving_a_category_clears_its_image_and_deletes_the_files()
    {
        var (key, _) = await UploadAsync(Image(900, 600, MagickFormat.Png), "image/png", purpose: "category");
        var category = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/categories",
            AdminApi.Category($"Arhiva sa slikom {Unique.Token()}", imageKey: key), HttpStatusCode.Created);
        var storageKey = category.GetProperty("image").GetProperty("key").GetString()!;
        string[] files = [$"{storageKey}/400.webp", $"{storageKey}/800.webp", $"{storageKey}/900.webp", $"{storageKey}/manifest.json"];
        foreach (var file in files)
        {
            Assert.True(await _minio.ExistsAsync(file), file);
        }

        var archived = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{category.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        // Same save: the archived row no longer refers to the image.
        var row = await new PostgresFixtureAccessor(_factory).CategoryImageAsync(category.Id());
        Assert.NotNull(row.DeletedAt);
        Assert.Null(row.ImageKey);
        Assert.Null(row.ImageWidth);
        Assert.Null(row.ImageHeight);

        // After the save: every rendition and the manifest are gone.
        foreach (var file in files)
        {
            Assert.False(await _minio.ExistsAsync(file), file);
        }

        Assert.Equal(1L, await new PostgresFixtureAccessor(_factory).AuditCountAsync("Category.Archived", category.Id()));
    }

    [Fact]
    public async Task Archiving_a_category_succeeds_when_the_image_files_cannot_be_deleted()
    {
        // A store that refuses every request (wrong credentials): attaching works through the real store,
        // archiving runs against the broken one. The database change must stand; the failure is only logged.
        var (key, _) = await UploadAsync(Image(500, 500, MagickFormat.Png), "image/png", purpose: "category");
        var category = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/categories",
            AdminApi.Category($"Neobrisiva slika {Unique.Token()}", imageKey: key), HttpStatusCode.Created);
        var storageKey = category.GetProperty("image").GetProperty("key").GetString()!;

        var settings = _minio.Settings();
        settings["Media:SecretKey"] = "wrong-secret";
        using var brokenStorage = new DevApiFactory { ConnectionString = _factory.ConnectionString, ExtraSettings = settings };
        using var client = brokenStorage.CreateClient();

        var archived = await new AdminApi(client).SendAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{category.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        var row = await new PostgresFixtureAccessor(_factory).CategoryImageAsync(category.Id());
        Assert.NotNull(row.DeletedAt);
        Assert.Null(row.ImageKey);

        // The files stay behind (unused, costing storage only).
        Assert.True(await _minio.ExistsAsync($"{storageKey}/500.webp"));
    }

    [Fact]
    public async Task Image_processor_lambda_handles_s3_events_and_the_api_waits_for_its_result()
    {
        // API without inline processing: as on AWS, where the Lambda does the work.
        using var factory = new DevApiFactory
        {
            ConnectionString = _factory.ConnectionString,
            ExtraSettings = _minio.Settings(processInline: false),
        };
        using var client = factory.CreateClient();
        var api = new AdminApi(client);

        var category = await api.CreateCategoryAsync();
        var product = await api.CreateProductAsync(category.Id());
        var (key, _) = await UploadAsync(Image(900, 300, MagickFormat.Jpeg), "image/jpeg");

        var notReady = await api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images", new { uploadKey = key });
        await ResponseAssertions.AssertProblemAsync(notReady, HttpStatusCode.Conflict, "IMAGE_NOT_READY");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(_minio.Settings(processInline: false))
            .Build();
        var function = new ImageProcessorFunction(configuration);

        await function.HandleAsync(S3EventFor(key), new FakeLambdaContext());
        // A repeated delivery of the same event is harmless.
        await function.HandleAsync(S3EventFor(key), new FakeLambdaContext());

        var imageId = key.Split('/')[^1].Split('.')[0];
        Assert.True(await _minio.ExistsAsync($"media/products/{imageId}/800.webp"));
        Assert.True(await _minio.ExistsAsync($"media/products/{imageId}/900.webp"));
        Assert.False(await _minio.ExistsAsync(key));

        var attached = await api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/images",
            new { uploadKey = key }, HttpStatusCode.Created);
        Assert.Equal([400, 800, 900], attached.GetProperty("images")[0].GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()));
    }

    private static S3Event S3EventFor(string key) => new()
    {
        Records =
        [
            new S3Event.S3EventNotificationRecord
            {
                EventName = "ObjectCreated:Post",
                S3 = new S3Event.S3Entity
                {
                    Bucket = new S3Event.S3BucketEntity { Name = MinioFixture.Bucket },
                    Object = new S3Event.S3ObjectEntity { Key = key },
                },
            },
            new S3Event.S3EventNotificationRecord
            {
                S3 = new S3Event.S3Entity
                {
                    Bucket = new S3Event.S3BucketEntity { Name = "another-bucket" },
                    Object = new S3Event.S3ObjectEntity { Key = key },
                },
            },
        ],
    };
}

/// <summary>Small helper to read audit rows through the application's own data source.</summary>
internal sealed class PostgresFixtureAccessor(DevApiFactory factory)
{
    public async Task<long> AuditCountAsync(string action, Guid entityId)
    {
        var dataSource = (Npgsql.NpgsqlDataSource)factory.Services.GetService(typeof(Npgsql.NpgsqlDataSource))!;
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM audit_logs WHERE action = @a AND entity_id = @id");
        command.Parameters.AddWithValue("a", action);
        command.Parameters.AddWithValue("id", entityId.ToString());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<(DateTimeOffset? DeletedAt, string? ImageKey, int? ImageWidth, int? ImageHeight)> CategoryImageAsync(Guid categoryId)
    {
        var dataSource = (Npgsql.NpgsqlDataSource)factory.Services.GetService(typeof(Npgsql.NpgsqlDataSource))!;
        await using var command = dataSource.CreateCommand(
            "SELECT deleted_at, image_key, image_width, image_height FROM categories WHERE id = @id");
        command.Parameters.AddWithValue("id", categoryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }
}
