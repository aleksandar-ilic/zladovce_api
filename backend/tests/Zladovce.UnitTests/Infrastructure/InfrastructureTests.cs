using Amazon.CognitoIdentityProvider.Model;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Identity;

namespace Zladovce.UnitTests.Infrastructure;

public class DatabaseOptionsTests
{
    private static DatabaseOptions Iam() => new()
    {
        UseIamAuth = true,
        Host = "db.example.eu-central-1.rds.amazonaws.com",
        Port = 5432,
        Name = "zladovce",
        User = "zladovce_app",
        Region = "eu-central-1",
    };

    [Fact]
    public void Plain_mode_requires_a_connection_string()
    {
        Assert.Throws<InvalidOperationException>(() => new DatabaseOptions().Validate());
        new DatabaseOptions { ConnectionString = "Host=localhost;Database=x" }.Validate();
    }

    [Fact]
    public void Iam_mode_names_every_missing_setting()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new DatabaseOptions { UseIamAuth = true, Host = "h" }.Validate());

        Assert.Contains("Name", exception.Message);
        Assert.Contains("User", exception.Message);
        Assert.Contains("Region", exception.Message);
        Assert.DoesNotContain("Host,", exception.Message);
    }

    [Fact]
    public void Iam_mode_rejects_an_invalid_port_and_pool_size()
    {
        var badPort = Iam();
        badPort.Port = 0;
        Assert.Throws<InvalidOperationException>(badPort.Validate);

        var badPool = Iam();
        badPool.MaxPoolSize = 0;
        Assert.Throws<InvalidOperationException>(badPool.Validate);
    }

    [Fact]
    public void Iam_connection_string_has_no_password_and_requires_tls()
    {
        var options = Iam();
        options.ConnectionString = "Host=ignored;Password=must-not-be-used";
        options.MaxPoolSize = 5;

        var csb = new NpgsqlConnectionStringBuilder(NpgsqlDataSourceFactory.BuildConnectionString(options));

        Assert.Equal("db.example.eu-central-1.rds.amazonaws.com", csb.Host);
        Assert.Equal(5432, csb.Port);
        Assert.Equal("zladovce", csb.Database);
        Assert.Equal("zladovce_app", csb.Username);
        Assert.Null(csb.Password);
        Assert.Equal(SslMode.Require, csb.SslMode);
        Assert.Equal(5, csb.MaxPoolSize);
    }

    [Fact]
    public void Root_certificate_switches_to_full_verification()
    {
        var options = Iam();
        options.RootCertificatePath = "/var/task/global-bundle.pem";

        var csb = new NpgsqlConnectionStringBuilder(NpgsqlDataSourceFactory.BuildConnectionString(options));

        Assert.Equal(SslMode.VerifyFull, csb.SslMode);
        Assert.Equal("/var/task/global-bundle.pem", csb.RootCertificate);
    }

    [Fact]
    public void Plain_connection_string_is_used_as_configured()
    {
        var options = new DatabaseOptions { ConnectionString = "Host=localhost;Port=5433;Database=zladovce;Username=zladovce;Password=zladovce_dev" };

        var csb = new NpgsqlConnectionStringBuilder(NpgsqlDataSourceFactory.BuildConnectionString(options));

        Assert.Equal(5433, csb.Port);
        Assert.Equal("zladovce_dev", csb.Password);
    }

    [Fact]
    public void Data_source_can_be_built_in_iam_mode_without_contacting_aws()
    {
        var generator = new CountingTokenGenerator();

        using var dataSource = NpgsqlDataSourceFactory.Create(Iam(), generator, TimeProvider.System);

        Assert.DoesNotContain("Password", dataSource.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, generator.Calls);
    }
}

internal sealed class CountingTokenGenerator : IRdsAuthTokenGenerator
{
    public int Calls { get; private set; }
    public (string Region, string Host, int Port, string User) LastArguments { get; private set; }

    public string GenerateToken(string region, string host, int port, string user)
    {
        Calls++;
        LastArguments = (region, host, port, user);
        return $"token-{Calls}";
    }
}

public class RdsAuthTokenProviderTests
{
    private static readonly DatabaseOptions Options = new()
    {
        UseIamAuth = true,
        Host = "db.internal",
        Port = 6543,
        Name = "zladovce",
        User = "zladovce_app",
        Region = "eu-central-1",
    };

    [Fact]
    public void Token_is_generated_for_the_configured_endpoint_and_user()
    {
        var generator = new CountingTokenGenerator();
        var provider = new RdsAuthTokenProvider(generator, Options, new FakeTimeProvider());

        Assert.Equal("token-1", provider.GetToken());
        Assert.Equal(("eu-central-1", "db.internal", 6543, "zladovce_app"), generator.LastArguments);
    }

    [Fact]
    public void Token_is_reused_for_ten_minutes_and_then_replaced()
    {
        var time = new FakeTimeProvider();
        var generator = new CountingTokenGenerator();
        var provider = new RdsAuthTokenProvider(generator, Options, time);

        Assert.Equal("token-1", provider.GetToken());
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("token-1", provider.GetToken());

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("token-2", provider.GetToken());
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public void A_long_pause_never_returns_an_expired_token()
    {
        // A frozen Lambda instance resumes hours later: the wall clock moved, no timer fired.
        var time = new FakeTimeProvider();
        var generator = new CountingTokenGenerator();
        var provider = new RdsAuthTokenProvider(generator, Options, time);
        provider.GetToken();

        time.Advance(TimeSpan.FromHours(3));

        Assert.Equal("token-2", provider.GetToken());
    }
}

public class CognitoAdminProfileLookupTests
{
    private static AttributeType Attribute(string name, string value) => new() { Name = name, Value = value };

    [Fact]
    public void Email_and_name_are_read_from_user_attributes()
    {
        var profile = CognitoAdminProfileLookup.ToProfile(
            [Attribute("sub", "abc"), Attribute("email", " ana@example.com "), Attribute("name", "Ana Anić")]);

        Assert.NotNull(profile);
        Assert.Equal("ana@example.com", profile.Email);
        Assert.Equal("Ana Anić", profile.FullName);
    }

    [Fact]
    public void Name_falls_back_to_given_and_family_name()
    {
        var profile = CognitoAdminProfileLookup.ToProfile(
            [Attribute("email", "ana@example.com"), Attribute("given_name", "Ana"), Attribute("family_name", "Anić")]);

        Assert.Equal("Ana Anić", profile?.FullName);
    }

    [Fact]
    public void Missing_name_is_null_and_missing_email_means_no_profile()
    {
        Assert.Null(CognitoAdminProfileLookup.ToProfile([Attribute("email", "ana@example.com")])?.FullName);
        Assert.Null(CognitoAdminProfileLookup.ToProfile([Attribute("name", "Ana")]));
        Assert.Null(CognitoAdminProfileLookup.ToProfile(null));
    }
}
