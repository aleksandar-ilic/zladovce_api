using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Tracking;
using Zladovce.Infrastructure.Notifications;

namespace Zladovce.UnitTests.Application;

public class TrackingCodeProtectionTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trip_returns_the_code()
    {
        var code = TrackingCode.Generate();
        var outboxId = Guid.CreateVersion7();

        var data = AesGcmTrackingCodeProtector.Protect(Key, code, outboxId);

        Assert.Equal(code, AesGcmTrackingCodeProtector.Unprotect(Key, data, outboxId));
        Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(code)), Convert.ToHexString(data), StringComparison.Ordinal);
        Assert.Equal(1 + 12 + 16 + 26, data.Length);
    }

    [Fact]
    public void Same_code_encrypts_differently_every_time()
    {
        var code = TrackingCode.Generate();
        var id = Guid.CreateVersion7();
        Assert.NotEqual(AesGcmTrackingCodeProtector.Protect(Key, code, id), AesGcmTrackingCodeProtector.Protect(Key, code, id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(40)]
    public void Any_changed_byte_is_detected(int index)
    {
        var id = Guid.CreateVersion7();
        var data = AesGcmTrackingCodeProtector.Protect(Key, TrackingCode.Generate(), id);
        data[index] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => AesGcmTrackingCodeProtector.Unprotect(Key, data, id));
    }

    [Fact]
    public void Ciphertext_is_bound_to_its_outbox_row_and_key()
    {
        var id = Guid.CreateVersion7();
        var data = AesGcmTrackingCodeProtector.Protect(Key, TrackingCode.Generate(), id);

        Assert.ThrowsAny<CryptographicException>(() => AesGcmTrackingCodeProtector.Unprotect(Key, data, Guid.CreateVersion7()));
        Assert.ThrowsAny<CryptographicException>(() => AesGcmTrackingCodeProtector.Unprotect(RandomNumberGenerator.GetBytes(32), data, id));
        Assert.ThrowsAny<CryptographicException>(() => AesGcmTrackingCodeProtector.Unprotect(Key, data[..10], id));
    }

    [Fact]
    public async Task Configured_key_provider_requires_32_bytes()
    {
        var good = new ConfiguredTrackingKeyProvider(Options.Create(new TrackingOptions { TokenEncryptionKey = Convert.ToBase64String(Key) }));
        Assert.Equal(Key, await good.GetKeyAsync(CancellationToken.None));

        var shortKey = new ConfiguredTrackingKeyProvider(Options.Create(new TrackingOptions { TokenEncryptionKey = Convert.ToBase64String(new byte[16]) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shortKey.GetKeyAsync(CancellationToken.None));

        var notBase64 = new ConfiguredTrackingKeyProvider(Options.Create(new TrackingOptions { TokenEncryptionKey = "not base64!" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notBase64.GetKeyAsync(CancellationToken.None));
    }

    [Fact]
    public void Secret_may_be_the_plain_key_or_json()
    {
        var base64 = Convert.ToBase64String(Key);
        Assert.Equal(Key, SecretsManagerTrackingKeyProvider.ParseSecret(base64));
        Assert.Equal(Key, SecretsManagerTrackingKeyProvider.ParseSecret($"{{\"key\":\"{base64}\"}}"));
        Assert.Throws<InvalidOperationException>(() => SecretsManagerTrackingKeyProvider.ParseSecret("{\"other\":1}"));
        Assert.Throws<InvalidOperationException>(() => SecretsManagerTrackingKeyProvider.ParseSecret(null));
    }

    [Fact]
    public void The_development_key_is_32_bytes() => Assert.NotNull(TrackingOptions.DecodeKey(TrackingOptions.DevelopmentKey));
}

public class EmailConfigurationGuardTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> Complete() => new()
    {
        ["Storefront:BaseUrl"] = "https://zladovce.example",
        ["Tracking:TokenEncryptionKeySecretArn"] = "arn:aws:secretsmanager:eu-central-1:000000000000:secret:tracking",
        ["Email:FromAddress"] = "prodavnica@zladovce.example",
    };

    [Fact]
    public void Complete_deployed_configuration_passes() =>
        NotificationServices.EnsureConfiguration(Config(Complete()), isDevelopment: false, sendsEmail: true);

    [Theory]
    [InlineData("Storefront:BaseUrl", null, "Storefront:BaseUrl")]
    [InlineData("Storefront:BaseUrl", "http://zladovce.example", "Storefront:BaseUrl")]
    [InlineData("Storefront:BaseUrl", "zladovce.example", "Storefront:BaseUrl")]
    [InlineData("Tracking:TokenEncryptionKeySecretArn", null, "Tracking:TokenEncryptionKey")]
    [InlineData("Email:FromAddress", null, "Email:FromAddress")]
    public void Missing_values_stop_a_deployed_host(string key, string? value, string expected)
    {
        var values = Complete();
        values[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NotificationServices.EnsureConfiguration(Config(values), isDevelopment: false, sendsEmail: true));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void The_public_development_key_is_refused_outside_development()
    {
        var values = Complete();
        values["Tracking:TokenEncryptionKeySecretArn"] = null;
        values["Tracking:TokenEncryptionKey"] = TrackingOptions.DevelopmentKey;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NotificationServices.EnsureConfiguration(Config(values), isDevelopment: false, sendsEmail: false));
        Assert.Contains("development key", exception.Message);

        NotificationServices.EnsureConfiguration(Config(values), isDevelopment: true, sendsEmail: true);
    }

    [Fact]
    public void Sender_is_optional_when_sending_is_disabled_or_done_elsewhere()
    {
        var values = Complete();
        values["Email:FromAddress"] = null;

        NotificationServices.EnsureConfiguration(Config(values), isDevelopment: false, sendsEmail: false);

        values["Email:Enabled"] = "false";
        NotificationServices.EnsureConfiguration(Config(values), isDevelopment: false, sendsEmail: true);
    }

    [Fact]
    public void Recipient_address_is_removed_from_errors()
    {
        var text = EmailDispatcher.Describe(new InvalidOperationException("550 Mailbox Ana.Jovic@Example.com unavailable"), "ana.jovic@example.com");

        Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[primalac]", text);
        Assert.StartsWith("InvalidOperationException: 550", text);
    }
}

public class OrderEmailRendererTests
{
    private static readonly EmailBranding Branding = new("Zladovce", "kontakt@zladovce.example", "+381 64 000 0000", "Zlatibor", null, "https://zladovce.example");

    private static OrderEmailPayload Payload(
        OrderStatus status = OrderStatus.Received,
        string firstName = "Ana",
        string? message = null,
        OrderEmailShipment? shipment = null,
        bool resent = false,
        IReadOnlyList<OrderEmailLine>? lines = null) =>
        new(
            OrderEmailPayload.CurrentVersion,
            "ZLD-2026-000042",
            firstName,
            new DateTimeOffset(2026, 7, 1, 13, 5, 0, TimeSpan.Zero),
            status,
            new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero),
            lines ?? [new("Džem od šljiva", "370 g", 2, 617m, 1234m), new("Ajvar ljuti", "720 g", 1, 1100.5m, 1100.5m)],
            2334.5m,
            350m,
            2684.5m,
            PaymentMethod.CashOnDelivery,
            new OrderEmailAddress("Čačak", "Gradsko šetalište 3", "32000"),
            shipment,
            message,
            "https://zladovce.example/track-order",
            resent);

    [Theory]
    [InlineData(0, "0,00 RSD")]
    [InlineData(5, "5,00 RSD")]
    [InlineData(1234, "1.234,00 RSD")]
    [InlineData(1234567.8, "1.234.567,80 RSD")]
    [InlineData(999.99, "999,99 RSD")]
    public void Money_is_formatted_the_serbian_way(decimal amount, string expected) =>
        Assert.Equal(expected, OrderEmailRenderer.FormatMoney(amount));

    [Fact]
    public void Dates_are_shown_in_belgrade_time()
    {
        // 13:05 UTC in July = 15:05 CEST; 23:30 UTC on 31 December = 00:30 on 1 January (CET).
        Assert.Equal("01.07.2026. u 15:05", OrderEmailRenderer.FormatDateTime(new DateTimeOffset(2026, 7, 1, 13, 5, 0, TimeSpan.Zero)));
        Assert.Equal("01.01.2027. u 00:30", OrderEmailRenderer.FormatDateTime(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal("01.01.2027.", OrderEmailRenderer.FormatDate(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Confirmation_contains_everything_the_specification_asks_for()
    {
        var code = TrackingCode.Generate();
        var grouped = TrackingCode.Format(code);

        var email = OrderEmailRenderer.Render(EmailTemplate.OrderReceived, Payload(), code, Branding);

        Assert.Equal("Porudžbina ZLD-2026-000042 je primljena", email.Subject);
        foreach (var part in new[]
                 {
                     "ZLD-2026-000042", "Zdravo, Ana!", "Džem od šljiva", "370 g", "Ajvar ljuti", "720 g", "617,00 RSD", "1.234,00 RSD",
                     "1.100,50 RSD", "350,00 RSD", "2.684,50 RSD", "Plaćanje pouzećem", grouped, "Gradsko šetalište 3", "32000 Čačak",
                     "01.07.2026. u 15:05", "kontakt@zladovce.example", "+381 64 000 0000",
                 })
        {
            Assert.Contains(part, email.Html);
            Assert.Contains(part, email.Text);
        }

        Assert.Contains($"href=\"https://zladovce.example/track-order#kod={grouped}\"", email.Html);
        Assert.Contains($"https://zladovce.example/track-order#kod={grouped}", email.Text);
        Assert.Contains("<html lang=\"sr-Latn\"", email.Html);
        Assert.Contains("max-width:600px", email.Html);
        Assert.DoesNotContain("<img", email.Html);
        Assert.DoesNotContain("<script", email.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ovu poruku ste dobili", email.Html);
    }

    [Fact]
    public void Hostile_values_are_encoded_everywhere()
    {
        const string attack = "<script>alert('x')</script>\"><img src=x onerror=alert(1)>&";
        var payload = Payload(
            firstName: attack,
            message: attack,
            lines: [new(attack, attack, 1, 100m, 100m)],
            shipment: new OrderEmailShipment(attack, attack, "javascript:alert(1)", DateTimeOffset.UtcNow)) with
        {
            ShippingAddress = new OrderEmailAddress(attack, attack, "11000"),
        };
        var branding = Branding with { StoreName = attack, ContactEmail = attack, Phone = attack, BusinessAddress = attack };

        foreach (var template in Enum.GetValues<EmailTemplate>())
        {
            var html = OrderEmailRenderer.Render(template, payload, TrackingCode.Generate(), branding).Html;

            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'x')", html);
            Assert.Contains("&lt;script&gt;", html);
        }
    }

    [Fact]
    public void Serbian_letters_are_kept_as_text()
    {
        var html = OrderEmailRenderer.Render(EmailTemplate.OrderReceived, Payload(firstName: "Đorđe Čučković Šćepanović Žaklina"), null, Branding).Html;
        Assert.Contains("Đorđe Čučković Šćepanović Žaklina", html);
        Assert.Contains("Porudžbina", html);
    }

    [Fact]
    public void Shipped_email_shows_courier_number_and_link()
    {
        var shipment = new OrderEmailShipment("Brza pošta", "PK123456", "https://kurir.example/pracenje?broj=PK123456&x=1", new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
        var email = OrderEmailRenderer.Render(EmailTemplate.OrderShipped, Payload(OrderStatus.Shipped, shipment: shipment) with { Lines = null }, TrackingCode.Generate(), Branding);

        Assert.Equal("Porudžbina ZLD-2026-000042 je poslata", email.Subject);
        Assert.Contains("Brza pošta", email.Html);
        Assert.Contains("PK123456", email.Html);
        Assert.Contains("href=\"https://kurir.example/pracenje?broj=PK123456&amp;x=1\"", email.Html);
        Assert.Contains("03.07.2026.", email.Html);
        Assert.Contains("https://kurir.example/pracenje?broj=PK123456&x=1", email.Text);
    }

    [Fact]
    public void Shipped_email_without_courier_number_or_link_omits_them()
    {
        var email = OrderEmailRenderer.Render(
            EmailTemplate.OrderShipped, Payload(OrderStatus.Shipped, shipment: new("Brza pošta", null, null, DateTimeOffset.UtcNow)), null, Branding);

        Assert.Contains("Brza pošta", email.Html);
        Assert.DoesNotContain("Broj pošiljke", email.Html);
        Assert.DoesNotContain("stranici kurirske službe", email.Html);
    }

    [Fact]
    public void Rejected_uses_the_cancellation_template_with_the_message()
    {
        var email = OrderEmailRenderer.Render(EmailTemplate.OrderCancelled, Payload(OrderStatus.Rejected, message: "Nemamo dostavu u vaše mesto."), null, Branding);

        Assert.Equal("Porudžbina ZLD-2026-000042 nije prihvaćena", email.Subject);
        Assert.Contains("Nemamo dostavu u vaše mesto.", email.Html);
        Assert.Contains("Nemamo dostavu u vaše mesto.", email.Text);
        Assert.Equal("Porudžbina ZLD-2026-000042 je otkazana", OrderEmailRenderer.Render(EmailTemplate.OrderCancelled, Payload(OrderStatus.Cancelled), null, Branding).Subject);
    }

    [Theory]
    [InlineData(EmailTemplate.OrderAccepted, "je prihvaćena")]
    [InlineData(EmailTemplate.OrderPreparing, "se priprema")]
    [InlineData(EmailTemplate.OrderDelivered, "je isporučena")]
    public void Status_emails_have_their_subject_message_and_no_order_summary(EmailTemplate template, string subjectEnd)
    {
        var code = TrackingCode.Generate();
        var email = OrderEmailRenderer.Render(template, Payload(message: "Hvala na strpljenju!"), code, Branding);

        Assert.EndsWith(subjectEnd, email.Subject);
        Assert.Contains("Hvala na strpljenju!", email.Html);
        Assert.Contains(TrackingCode.Format(code), email.Html);
        Assert.DoesNotContain("Pregled porudžbine", email.Html);
    }

    [Fact]
    public void Contact_section_is_left_out_when_the_store_has_no_contact_data()
    {
        var email = OrderEmailRenderer.Render(
            EmailTemplate.OrderAccepted, Payload(), null, Branding with { ContactEmail = null, Phone = null, BusinessAddress = null });

        Assert.DoesNotContain("Imate pitanje?", email.Html);
        Assert.DoesNotContain("Kod za praćenje", email.Html);
    }

    [Fact]
    public void Logo_is_the_only_image_and_only_over_https()
    {
        Assert.Contains("<img src=\"https://cdn.zladovce.example/logo.png\"",
            OrderEmailRenderer.Render(EmailTemplate.OrderAccepted, Payload(), null, Branding with { LogoUrl = "https://cdn.zladovce.example/logo.png" }).Html);
        Assert.DoesNotContain("<img",
            OrderEmailRenderer.Render(EmailTemplate.OrderAccepted, Payload(), null, Branding with { LogoUrl = "http://cdn.zladovce.example/logo.png" }).Html);
    }

    [Fact]
    public void Resent_confirmation_says_so()
    {
        var email = OrderEmailRenderer.Render(EmailTemplate.OrderReceived, Payload(resent: true), TrackingCode.Generate(), Branding);
        Assert.Equal("Potvrda porudžbine ZLD-2026-000042", email.Subject);
        Assert.Contains("Kod za praćenje u ovoj poruci je nov", email.Html);
    }

    [Fact]
    public void Payload_survives_json_and_holds_no_code()
    {
        var payload = Payload(shipment: new("Brza pošta", "1", null, DateTimeOffset.UtcNow));
        var json = payload.ToJson();

        var back = OrderEmailPayload.FromJson(json);
        Assert.Equal(payload.OrderNumber, back.OrderNumber);
        Assert.Equal(payload.Lines!.Count, back.Lines!.Count);
        Assert.Equal(payload.Total, back.Total);
        Assert.Equal(OrderStatus.Received, back.Status);
        Assert.Contains("\"status\":\"Received\"", json);
        Assert.DoesNotContain("trackingCode", json, StringComparison.OrdinalIgnoreCase);
    }
}
