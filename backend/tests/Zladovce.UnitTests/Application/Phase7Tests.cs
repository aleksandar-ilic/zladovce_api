using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zladovce.Application.Analytics;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Content;
using Zladovce.Application.Identity;
using Zladovce.Application.Media;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Content;
using Zladovce.Domain.Finance;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Marketing;
using Zladovce.Infrastructure.Identity;
using Zladovce.Infrastructure.Notifications;
using Zladovce.Infrastructure.Site;

namespace Zladovce.UnitTests.Application;

public class AnalyticsPeriodTests
{
    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Today_starts_at_Belgrade_midnight_not_UTC_midnight()
    {
        // 2026-11-15 00:30 in Belgrade (UTC+1) is still 14 November in UTC.
        var period = AnalyticsPeriod.Resolve("today", null, null, At("2026-11-14T23:30:00Z"));
        Assert.Equal(new DateOnly(2026, 11, 15), period.Current.From);
        Assert.Equal(At("2026-11-14T23:00:00Z"), period.Current.StartUtc);
        Assert.Equal(At("2026-11-15T23:00:00Z"), period.Current.EndUtc);
        Assert.Equal(new DateRange(new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 14)), period.Previous);
    }

    [Fact]
    public void Dst_days_have_23_and_25_hours()
    {
        var spring = new DateRange(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29)); // last Sunday of March
        Assert.Equal(At("2026-03-28T23:00:00Z"), spring.StartUtc);
        Assert.Equal(At("2026-03-29T22:00:00Z"), spring.EndUtc);
        Assert.Equal(TimeSpan.FromHours(23), spring.EndUtc - spring.StartUtc);

        var autumn = new DateRange(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)); // last Sunday of October
        Assert.Equal(At("2026-10-24T22:00:00Z"), autumn.StartUtc);
        Assert.Equal(At("2026-10-25T23:00:00Z"), autumn.EndUtc);
        Assert.Equal(TimeSpan.FromHours(25), autumn.EndUtc - autumn.StartUtc);

        Assert.True(spring.Contains(At("2026-03-28T23:00:00Z")));
        Assert.False(spring.Contains(At("2026-03-29T22:00:00Z")));
        Assert.True(spring.Contains(At("2026-03-29T21:59:59Z")));
    }

    [Theory]
    [InlineData("yesterday", "2026-11-14", "2026-11-14", "2026-11-13", "2026-11-13")]
    [InlineData("last7", "2026-11-09", "2026-11-15", "2026-11-02", "2026-11-08")]
    [InlineData("last30", "2026-10-17", "2026-11-15", "2026-09-17", "2026-10-16")]
    [InlineData("thisMonth", "2026-11-01", "2026-11-15", "2026-10-01", "2026-10-15")]
    [InlineData("lastMonth", "2026-10-01", "2026-10-31", "2026-09-01", "2026-09-30")]
    [InlineData("thisYear", "2026-01-01", "2026-11-15", "2025-01-01", "2025-11-15")]
    public void Periods_and_their_comparable_previous_periods(string key, string from, string to, string previousFrom, string previousTo)
    {
        var period = AnalyticsPeriod.Resolve(key, null, null, At("2026-11-15T10:00:00Z"));
        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), period.Current.From);
        Assert.Equal(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), period.Current.To);
        Assert.Equal(DateOnly.Parse(previousFrom, System.Globalization.CultureInfo.InvariantCulture), period.Previous.From);
        Assert.Equal(DateOnly.Parse(previousTo, System.Globalization.CultureInfo.InvariantCulture), period.Previous.To);
    }

    [Fact]
    public void This_month_on_the_31st_compares_with_the_whole_shorter_previous_month()
    {
        var period = AnalyticsPeriod.Resolve("thisMonth", null, null, At("2026-03-31T12:00:00Z"));
        Assert.Equal(new DateRange(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), period.Previous);
    }

    [Fact]
    public void Custom_periods_shift_by_their_length_and_bucket_by_month_when_long()
    {
        var custom = AnalyticsPeriod.Resolve("custom", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19), At("2026-11-15T10:00:00Z"));
        Assert.Equal(new DateRange(new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 9)), custom.Previous);
        Assert.Equal(SeriesGranularity.Day, custom.Granularity);
        Assert.Equal(10, custom.Buckets().Count);

        var year = AnalyticsPeriod.Resolve("thisYear", null, null, At("2026-11-15T10:00:00Z"));
        Assert.Equal(SeriesGranularity.Month, year.Granularity);
        var buckets = year.Buckets();
        Assert.Equal(11, buckets.Count);
        Assert.Equal(new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15)), buckets[^1]);

        var ninetyTwo = AnalyticsPeriod.Resolve("custom", new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 2), At("2026-11-15T10:00:00Z"));
        Assert.Equal(SeriesGranularity.Day, ninetyTwo.Granularity);
        var ninetyThree = AnalyticsPeriod.Resolve("custom", new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 3), At("2026-11-15T10:00:00Z"));
        Assert.Equal(SeriesGranularity.Month, ninetyThree.Granularity);
    }
}

public class CsvWriterTests
{
    [Fact]
    public void Serbian_excel_format_with_bom_semicolons_and_decimal_comma()
    {
        var csv = new CsvWriter().Row("Proizvod", "Količina").Text("Džem; šljiva").Number(1234.5m).EndRow();
        var bytes = csv.ToBytes();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("Proizvod;Količina\r\n\"Džem; šljiva\";1234,50\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("dva\nreda", "\"dva\nreda\"")]
    [InlineData(" razmak", "\" razmak\"")]
    [InlineData("obično", "obično")]
    [InlineData("", "")]
    public void Rfc4180_quoting(string value, string expected) =>
        Assert.Equal(expected + "\r\n", new CsvWriter().Text(value).EndRow().ToString());

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"\"http://x\"\")")]
    [InlineData("+381641234567", "'+381641234567")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    public void Formula_injection_is_neutralised(string value, string expectedCell)
    {
        var line = new CsvWriter().Text(value).EndRow().ToString();
        Assert.StartsWith(expectedCell.Contains('"') ? "\"'" : "'", line);
        Assert.Contains(expectedCell.TrimStart('\''), line);
    }

    [Fact]
    public void Numbers_are_never_prefixed() =>
        Assert.Equal("-5,00;-5\r\n", new CsvWriter().Number(-5m).Number(-5).EndRow().ToString());
}

public class RichHtmlSanitizerTests
{
    private static readonly RichHtmlSanitizer Sanitizer = new(Options.Create(new MediaOptions { BaseUrl = "https://cdn.zladovce.test" }));

    [Theory]
    [InlineData("<p>a<script>alert(1)</script></p>", "<p>a</p>")]
    [InlineData("<p onclick=\"x()\" onmouseover=\"y()\">a</p>", "<p>a</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">a</a>", "<a>a</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">a</a>", "<a>a</a>")]
    [InlineData("<img src=\"https://evil.test/x.png\" alt=\"x\">", "")]
    [InlineData("<img src=\"https://cdn.zladovce.test.evil.test/media/blog/x/800.webp\">", "")]
    [InlineData("<img src=\"https://cdn.zladovce.test/media/products/x/800.webp\">", "")]
    [InlineData("<img src=\"https://cdn.zladovce.test/media/blog/../uploads/x.png\">", "")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", "")]
    [InlineData("<iframe src=\"https://x\"></iframe><object></object>", "")]
    [InlineData("<div style=\"color:red\" class=\"c\">t</div>", "t")]
    public void Dangerous_markup_is_removed(string input, string expected) => Assert.Equal(expected, Sanitizer.Sanitize(input));

    [Fact]
    public void Own_images_and_figures_are_kept()
    {
        var html = "<figure><img src=\"https://cdn.zladovce.test/media/blog/0193/800.webp\" alt=\"Tegla\" width=\"800\" height=\"9999999\" onerror=\"x\"><figcaption>Opis</figcaption></figure>";
        Assert.Equal("<figure><img src=\"https://cdn.zladovce.test/media/blog/0193/800.webp\" alt=\"Tegla\" width=\"800\"><figcaption>Opis</figcaption></figure>", Sanitizer.Sanitize(html));
        Assert.Equal("<img src=\"https://cdn.zladovce.test/media/pages/1/400.webp\">", Sanitizer.Sanitize("<img src=\"https://cdn.zladovce.test/media/pages/1/400.webp\">"));
    }

    [Fact]
    public void Without_a_media_base_url_no_image_is_allowed() =>
        Assert.Equal(string.Empty, new RichHtmlSanitizer(Options.Create(new MediaOptions())).Sanitize("<img src=\"https://cdn.zladovce.test/media/blog/1/400.webp\">"));
}

public class Phase7DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newsletter_states_follow_double_opt_in()
    {
        var subscriber = NewsletterSubscriber.SignUp("a@example.com", Now);
        Assert.Equal(NewsletterSubscriberStatus.Pending, subscriber.Status);
        Assert.False(subscriber.SignUpAgain(Now.AddMinutes(5)));          // too soon: no second email
        Assert.True(subscriber.SignUpAgain(Now.AddMinutes(31)));          // pending again later: send again
        Assert.Equal(Now.AddMinutes(31), subscriber.ConfirmationSentAt);
        Assert.True(subscriber.Confirm(Now.AddMinutes(40)));
        Assert.False(subscriber.Confirm(Now.AddMinutes(41)));
        Assert.Equal(NewsletterSubscriberStatus.Confirmed, subscriber.Status);
        Assert.False(subscriber.SignUpAgain(Now.AddDays(1)));             // confirmed: nothing to send
        Assert.True(subscriber.Unsubscribe(Now.AddDays(2)));
        Assert.False(subscriber.Unsubscribe(Now.AddDays(3)));
        Assert.Equal(NewsletterSubscriberStatus.Unsubscribed, subscriber.Status);
        Assert.True(subscriber.SignUpAgain(Now.AddDays(4)));              // new double opt-in
        Assert.Equal(NewsletterSubscriberStatus.Pending, subscriber.Status);
    }

    [Fact]
    public void Blog_post_is_public_only_when_published_and_due()
    {
        var post = BlogPost.CreateDraft(Guid.NewGuid(), null, "Naslov", "naslov", "Uvod", "<p>x</p>", null, null, Now);
        Assert.False(post.IsPublicAt(Now));
        post.Publish(Now.AddHours(1), Now);
        Assert.False(post.IsPublicAt(Now));
        Assert.True(post.IsPublicAt(Now.AddHours(1)));
        post.Unpublish(Now);
        Assert.False(post.IsPublicAt(Now.AddDays(1)));
        post.Publish(Now, Now);
        post.Archive(Now);
        Assert.False(post.IsPublicAt(Now.AddDays(1)));
        Assert.Throws<Zladovce.Domain.Common.DomainException>(() => post.Publish(Now, Now));

        var empty = BlogPost.CreateDraft(Guid.NewGuid(), null, "Prazno", "prazno", "", "", null, null, Now);
        Assert.Throws<Zladovce.Domain.Common.DomainException>(() => empty.Publish(Now, Now));
    }

    [Fact]
    public void Expense_categories_are_a_fixed_list_with_labels()
    {
        Assert.Equal(["Ambalaza", "Sirovine", "Dostava", "Marketing", "Ostalo"], ExpenseCategories.All.Select(c => c.Value));
        Assert.Equal("Ambalaža", ExpenseCategories.LabelOf("Ambalaza"));
        Assert.Equal("Staro", ExpenseCategories.LabelOf("Staro"));
        Assert.Throws<Zladovce.Domain.Common.DomainException>(() => Expense.Create(new DateOnly(2026, 1, 1), "Hrana", "x", 10m, Guid.NewGuid(), Now));
        Assert.Throws<Zladovce.Domain.Common.DomainException>(() => Expense.Create(new DateOnly(2026, 1, 1), "Ostalo", "x", 0.001m, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Cms_keys_round_trip()
    {
        Assert.True(CmsPageKeys.TryParse("delivery", out var key));
        Assert.Equal(CmsPageKey.Delivery, key);
        Assert.False(CmsPageKeys.TryParse("Delivery", out _));
        Assert.Equal(["home", "about", "privacy", "terms", "delivery"], Enum.GetValues<CmsPageKey>().Select(CmsPageKeys.ToKey));
    }
}

public class NewsletterTokenTests
{
    private sealed class FixedKey(byte[] key) : ITrackingKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken) => Task.FromResult(key);
    }

    private static readonly HmacNewsletterTokens Tokens = new(new FixedKey(new byte[32]));

    [Fact]
    public async Task Tokens_round_trip_and_carry_their_purpose()
    {
        var id = Guid.NewGuid();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_123);
        var confirm = await Tokens.CreateAsync(new NewsletterToken(NewsletterTokenPurpose.Confirm, id, at), CancellationToken.None);
        var unsubscribe = await Tokens.CreateAsync(new NewsletterToken(NewsletterTokenPurpose.Unsubscribe, id, null), CancellationToken.None);

        Assert.Equal(new NewsletterToken(NewsletterTokenPurpose.Confirm, id, at), await Tokens.ReadAsync(confirm, CancellationToken.None));
        Assert.Equal(new NewsletterToken(NewsletterTokenPurpose.Unsubscribe, id, null), await Tokens.ReadAsync(unsubscribe, CancellationToken.None));
        Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", confirm);
    }

    [Fact]
    public async Task Forged_or_malformed_tokens_are_refused()
    {
        var token = await Tokens.CreateAsync(new NewsletterToken(NewsletterTokenPurpose.Unsubscribe, Guid.NewGuid(), null), CancellationToken.None);
        var other = new HmacNewsletterTokens(new FixedKey([.. Enumerable.Repeat((byte)7, 32)]));

        Assert.Null(await other.ReadAsync(token, CancellationToken.None));
        Assert.Null(await Tokens.ReadAsync(token.Replace('.', ','), CancellationToken.None));
        Assert.Null(await Tokens.ReadAsync("abc", CancellationToken.None));
        Assert.Null(await Tokens.ReadAsync(null, CancellationToken.None));
        Assert.Null(await Tokens.ReadAsync(new string('a', 300), CancellationToken.None));
        var parts = token.Split('.');
        Assert.Null(await Tokens.ReadAsync($"{parts[0]}A.{parts[1]}", CancellationToken.None));
    }
}

public class AuditSummaryTests
{
    [Fact]
    public void Summaries_are_serbian_and_list_changed_fields()
    {
        var changes = JsonDocument.Parse("{\"name\":{\"old\":\"a\",\"new\":\"b\"},\"isActive\":{\"old\":true,\"new\":false}}").RootElement;
        Assert.Equal("Proizvod – izmena (name, isActive)", AuditLogService.Summarize("Product.Updated", "Product", changes));
        Assert.Equal("Kategorija – arhiviranje", AuditLogService.Summarize("Category.Archived", "Category", null));
        var status = JsonDocument.Parse("{\"status\":{\"old\":\"Received\",\"new\":\"Accepted\"}}").RootElement;
        Assert.Equal("Porudžbina – promena statusa: Received → Accepted", AuditLogService.Summarize("Order.StatusChanged", "Order", status));
        Assert.Equal("Nepoznato – Radnja", AuditLogService.Summarize("Nepoznato.Radnja", null, null));
    }
}

public class SiteRebuildOptionsTests
{
    [Theory]
    [InlineData("https://api.netlify.example/build_hooks/abc", true)]
    [InlineData("http://127.0.0.1:5000/hook", true)]
    [InlineData("http://localhost:5000/hook", true)]
    [InlineData("http://example.com/hook", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("", false)]
    public void Only_https_or_loopback_http(string url, bool ok) => Assert.Equal(ok, SiteOptions.IsAcceptableUrl(url));

    [Fact]
    public void Secret_may_be_plain_or_json() =>
        Assert.Equal("https://x.test/h", SiteRebuildDispatcher.ParseSecret("{ \"url\": \"https://x.test/h\" }"));
}

public class MarketingEmailTests
{
    private static readonly EmailBranding Branding = new("Zladovce", "info@zladovce.test", null, null, null, "https://zladovce.test");

    [Fact]
    public void Newsletter_confirmation_has_both_links()
    {
        var email = MarketingEmailRenderer.RenderNewsletterConfirm("https://zladovce.test/newsletter/potvrda#token=a.b", "https://zladovce.test/newsletter/odjava#token=c.d", Branding);
        Assert.Contains("href=\"https://zladovce.test/newsletter/potvrda#token=a.b\"", email.Html);
        Assert.Contains("href=\"https://zladovce.test/newsletter/odjava#token=c.d\"", email.Html);
        Assert.Contains("odjava#token=c.d", email.Text);
        Assert.Contains("Potvrdite prijavu", email.Subject);
    }

    [Fact]
    public void Contact_message_values_are_encoded()
    {
        var payload = new ContactMessagePayload(1, Guid.NewGuid(), "<script>x</script>", "a@example.com", "<img src=x onerror=alert(1)>", DateTimeOffset.UtcNow);
        var email = MarketingEmailRenderer.RenderContactMessage(payload, Branding);
        Assert.DoesNotContain("<script>x", email.Html);
        Assert.DoesNotContain("<img src=x", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html);
    }
}

public class CognitoProvisioningTests
{
    /// <summary>A recording stand-in for the Cognito client (only the calls the provisioning makes are answered).</summary>
    public class FakeCognito : DispatchProxy
    {
        public List<AmazonWebServiceRequestLike> Calls { get; } = [];

        public Exception? FailWith { get; set; }

        public sealed record AmazonWebServiceRequestLike(string Operation, object Request);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name == "Dispose")
            {
                return null;
            }

            Calls.Add(new AmazonWebServiceRequestLike(name, args![0]!));
            if (FailWith is { } failure && name == "AdminCreateUserAsync")
            {
                return Task.FromException<AdminCreateUserResponse>(failure);
            }

            return name switch
            {
                "AdminCreateUserAsync" => Task.FromResult(new AdminCreateUserResponse
                {
                    User = new UserType { Attributes = [new AttributeType { Name = "sub", Value = "sub-123" }] },
                }),
                "AdminAddUserToGroupAsync" => Task.FromResult(new AdminAddUserToGroupResponse()),
                "AdminRemoveUserFromGroupAsync" => Task.FromResult(new AdminRemoveUserFromGroupResponse()),
                "AdminDisableUserAsync" => Task.FromResult(new AdminDisableUserResponse()),
                "AdminEnableUserAsync" => Task.FromResult(new AdminEnableUserResponse()),
                _ => throw new NotSupportedException(name),
            };
        }
    }

    private static (CognitoIdentityProvisioning Provisioning, FakeCognito Fake) Create()
    {
        var client = DispatchProxy.Create<IAmazonCognitoIdentityProvider, FakeCognito>();
        var fake = (FakeCognito)(object)client;
        var options = Options.Create(new CognitoOptions { Region = "eu-central-1", UserPoolId = "pool-1", ClientId = "c" });
        return (new CognitoIdentityProvisioning(options, NullLogger<CognitoIdentityProvisioning>.Instance, () => client), fake);
    }

    [Fact]
    public async Task Invite_creates_the_user_with_an_emailed_temporary_password_and_adds_the_group()
    {
        var (provisioning, fake) = Create();
        var subject = await provisioning.CreateUserAsync("ana@example.com", "Ana", AdminRole.Staff, CancellationToken.None);

        Assert.Equal("sub-123", subject);
        var create = Assert.IsType<AdminCreateUserRequest>(fake.Calls[0].Request);
        Assert.Equal("pool-1", create.UserPoolId);
        Assert.Equal("ana@example.com", create.Username);
        Assert.Equal(["EMAIL"], create.DesiredDeliveryMediums);
        Assert.Null(create.TemporaryPassword);
        var group = Assert.IsType<AdminAddUserToGroupRequest>(fake.Calls[1].Request);
        Assert.Equal("Staff", group.GroupName);
    }

    [Fact]
    public async Task Role_change_and_enable_disable_sync_cognito()
    {
        var (provisioning, fake) = Create();
        await provisioning.ChangeRoleAsync("ana@example.com", AdminRole.Staff, AdminRole.Owner, CancellationToken.None);
        await provisioning.SetEnabledAsync("ana@example.com", false, CancellationToken.None);
        await provisioning.SetEnabledAsync("ana@example.com", true, CancellationToken.None);

        Assert.Equal(["AdminAddUserToGroupAsync", "AdminRemoveUserFromGroupAsync", "AdminDisableUserAsync", "AdminEnableUserAsync"], fake.Calls.Select(c => c.Operation));
        Assert.Equal("Owner", ((AdminAddUserToGroupRequest)fake.Calls[0].Request).GroupName);
        Assert.Equal("Staff", ((AdminRemoveUserFromGroupRequest)fake.Calls[1].Request).GroupName);
    }

    [Fact]
    public async Task Existing_user_and_failures_are_translated()
    {
        var (provisioning, fake) = Create();
        fake.FailWith = new UsernameExistsException("exists");
        await Assert.ThrowsAsync<IdentityUserExistsException>(() => provisioning.CreateUserAsync("ana@example.com", "Ana", AdminRole.Staff, CancellationToken.None));

        fake.FailWith = new NotAuthorizedException("no");
        await Assert.ThrowsAsync<IdentityProvisioningException>(() => provisioning.CreateUserAsync("ana@example.com", "Ana", AdminRole.Staff, CancellationToken.None));
    }

    [Fact]
    public async Task Development_provisioning_uses_the_development_subject() =>
        Assert.Equal("dev:ana@example.com", await new DevelopmentIdentityProvisioning().CreateUserAsync(" Ana@Example.com ", "Ana", AdminRole.Owner, CancellationToken.None));
}
