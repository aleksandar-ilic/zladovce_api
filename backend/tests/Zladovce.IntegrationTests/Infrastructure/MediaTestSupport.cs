using System.Net;
using ImageMagick;

namespace Zladovce.IntegrationTests.Infrastructure;

/// <summary>Uploads a generated image through the real pre-signed POST, as the admin application does.</summary>
public static class MediaTestSupport
{
    private static readonly HttpClient Browser = new();

    /// <summary>Returns the upload key for the given purpose (<c>blog</c>, <c>page</c>, <c>logo</c>, …).</summary>
    public static async Task<string> UploadAsync(AdminApi admin, HttpClient api, string purpose, uint width = 900, uint height = 600)
    {
        _ = api;
        using var image = new MagickImage(MagickColors.OliveDrab, width, height) { Format = MagickFormat.Png };
        var content = image.ToByteArray();
        var grant = await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/media/upload-url",
            new { fileName = "slika.png", contentType = "image/png", size = content.Length, purpose });

        using var form = new MultipartFormDataContent();
        foreach (var field in grant.GetProperty("fields").EnumerateObject())
        {
            form.Add(new StringContent(field.Value.GetString()!), field.Name);
        }

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "slika");
        using var upload = await Browser.PostAsync(grant.GetProperty("uploadUrl").GetString(), form);
        Assert.True(upload.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Created, $"upload: {(int)upload.StatusCode}");
        return grant.GetProperty("key").GetString()!;
    }
}
