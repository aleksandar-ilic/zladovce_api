using ImageMagick;
using Zladovce.Application.Media;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Media;

namespace Zladovce.UnitTests.Infrastructure;

public class MagickImageTransformerTests
{
    private readonly MagickImageTransformer _transformer = new();

    internal static byte[] CreateImage(uint width, uint height, MagickFormat format, Orientation orientation = Orientation.TopLeft)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Orientation = orientation;
        if (format == MagickFormat.Jpeg)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCamera");
            exif.SetValue(ExifTag.Orientation, (ushort)orientation);
            image.SetProfile(exif);
        }

        image.Format = format;
        return image.ToByteArray();
    }

    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    public void Accepted_formats_produce_webp_renditions_of_the_planned_widths(MagickFormat format)
    {
        var result = _transformer.Transform(CreateImage(1000, 500, format));

        Assert.Equal(1000, result.Width);
        Assert.Equal(500, result.Height);
        Assert.Equal([400, 800, 1000], result.Renditions.Select(r => r.Width));

        foreach (var rendition in result.Renditions)
        {
            using var decoded = new MagickImage(rendition.Content);
            Assert.Equal(MagickFormat.WebP, decoded.Format);
            Assert.Equal((uint)rendition.Width, decoded.Width);
            Assert.Equal((uint)(rendition.Width / 2), decoded.Height);
        }
    }

    [Fact]
    public void Small_images_are_never_enlarged()
    {
        var result = _transformer.Transform(CreateImage(300, 200, MagickFormat.Png));
        Assert.Equal([300], result.Renditions.Select(r => r.Width));
    }

    [Fact]
    public void Orientation_is_applied_and_metadata_removed()
    {
        // Stored 600x300, but the EXIF orientation says "rotate 90Â°": the picture is 300 wide and 600 high.
        var result = _transformer.Transform(CreateImage(600, 300, MagickFormat.Jpeg, Orientation.RightTop));

        Assert.Equal(300, result.Width);
        Assert.Equal(600, result.Height);

        using var decoded = new MagickImage(result.Renditions[0].Content);
        Assert.Null(decoded.GetExifProfile());
        Assert.Null(decoded.GetColorProfile());
    }

    [Fact]
    public void Non_image_content_is_rejected() =>
        Assert.Throws<InvalidImageException>(() => _transformer.Transform("<html>not an image</html>"u8.ToArray()));

    [Fact]
    public void Truncated_image_is_rejected()
    {
        var png = CreateImage(100, 100, MagickFormat.Png);
        Assert.Throws<InvalidImageException>(() => _transformer.Transform(png[..40]));
    }

    [Fact]
    public void Other_image_formats_are_rejected() =>
        Assert.Throws<InvalidImageException>(() => _transformer.Transform(CreateImage(50, 50, MagickFormat.Gif)));
}

public class DatabaseRoleSqlTests
{
    [Fact]
    public void Statements_reference_both_roles_and_the_database()
    {
        var sql = string.Join('\n', DatabaseRoleSql.Build("zladovce_migrator", "zladovce_app", "zladovce"));

        Assert.Contains("GRANT rds_iam TO \"zladovce_migrator\"", sql);
        Assert.Contains("GRANT rds_iam TO \"zladovce_app\"", sql);
        Assert.Contains("ALTER SCHEMA public OWNER TO \"zladovce_migrator\"", sql);
        Assert.Contains("ALTER DEFAULT PRIVILEGES FOR ROLE \"zladovce_migrator\" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO \"zladovce_app\"", sql);
        Assert.DoesNotContain("GRANT CREATE ON SCHEMA public TO \"zladovce_app\"", sql);
    }

    [Theory]
    [InlineData("Migrator", "app")]
    [InlineData("migrator; DROP TABLE x", "app")]
    [InlineData("migrator", "")]
    [InlineData("same", "same")]
    public void Unsafe_or_missing_role_names_are_refused(string migrator, string app) =>
        Assert.Throws<InvalidOperationException>(() => DatabaseRoleSql.Build(migrator, app, "zladovce"));

    [Fact]
    public void Master_secret_is_parsed()
    {
        var credentials = SecretsManagerMasterCredentialsProvider.ParseSecret("""{"username":"master","password":"p@ss","engine":"postgres"}""");
        Assert.Equal("master", credentials.Username);
        Assert.Equal("p@ss", credentials.Password);
        Assert.DoesNotContain("p@ss", credentials.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"username":"dbadmin_user"}""")]
    public void Malformed_secret_fails_without_echoing_it(string? json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SecretsManagerMasterCredentialsProvider.ParseSecret(json));
        Assert.DoesNotContain("dbadmin_user", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Master_connection_uses_the_iam_endpoint_with_master_credentials()
    {
        var connection = DatabaseRoleBootstrapper.BuildMasterConnectionString(
            new DatabaseOptions { UseIamAuth = true, Host = "db.example", Port = 5432, Name = "zladovce", User = "zladovce_migrator", Region = "eu-central-1" },
            new DatabaseCredentials("master", "secret"));

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("db.example", builder.Host);
        Assert.Equal("zladovce", builder.Database);
        Assert.Equal("master", builder.Username);
        Assert.Equal(Npgsql.SslMode.Require, builder.SslMode);
    }
}
