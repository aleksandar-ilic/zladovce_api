using System.Security.Cryptography;
using System.Text;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Tracking;

namespace Zladovce.UnitTests.Domain;

public class TrackingCodeTests
{
    [Fact]
    public void Generated_codes_are_26_crockford_characters_and_distinct()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => TrackingCode.Generate()).ToList();

        Assert.All(codes, code =>
        {
            Assert.Equal(TrackingCode.Length, code.Length);
            Assert.All(code, c => Assert.Contains(c, TrackingCode.Alphabet));
            Assert.Equal(code, TrackingCode.Normalize(code));
        });
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Generated_codes_use_the_whole_alphabet_in_every_position_except_the_last_bits()
    {
        // 26 × 5 = 130 bits: every position carries 5 random bits, so over many codes each position sees (nearly) all 32 symbols.
        var codes = Enumerable.Range(0, 4000).Select(_ => TrackingCode.Generate()).ToList();
        for (var position = 0; position < TrackingCode.Length; position++)
        {
            var symbols = codes.Select(c => c[position]).Distinct().Count();
            Assert.True(symbols >= 30, $"position {position}: only {symbols} symbols");
        }

        // Roughly half of all 130 bits are set (no stuck bits).
        var ones = codes.Sum(c => TrackingCode.TryDecode(c, out var bytes) ? bytes.Sum(b => int.PopCount(b)) : 0);
        var ratio = ones / (double)(codes.Count * TrackingCode.Bits);
        Assert.InRange(ratio, 0.48, 0.52);
    }

    [Fact]
    public void Encode_and_decode_round_trip_130_bits()
    {
        var value = RandomNumberGenerator.GetBytes(TrackingCode.ByteLength);
        value[^1] &= 0b1100_0000;

        var code = TrackingCode.Encode(value);

        Assert.True(TrackingCode.TryDecode(code, out var decoded));
        Assert.Equal(value, decoded);
        Assert.Equal(new string('0', 26), TrackingCode.Encode(new byte[17]));
        Assert.Equal(new string('Z', 26), TrackingCode.Encode(Enumerable.Repeat((byte)0xFF, 17).ToArray()));
    }

    [Theory]
    [InlineData("ABCDE-FGHJK-MNPQR-STVWX-YZ0123", "ABCDEFGHJKMNPQRSTVWXYZ0123")]
    [InlineData("abcde fghjk mnpqr stvwx yz0123", "ABCDEFGHJKMNPQRSTVWXYZ0123")]
    [InlineData("  abcdefghjkmnpqrstvwxyz0123  ", "ABCDEFGHJKMNPQRSTVWXYZ0123")]
    [InlineData("OOOOO-IIIII-LLLLL-ooooo-iiiiil", "00000111111111100000111111")]
    [InlineData("A-B-C-D-E-F-G-H-J-K-M-N-P-Q-R-S-T-V-W-X-Y-Z-0-1-2-3", "ABCDEFGHJKMNPQRSTVWXYZ0123")]
    public void Normalize_accepts_separators_case_and_the_crockford_ambiguity_mapping(string input, string expected) =>
        Assert.Equal(expected, TrackingCode.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDE")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ01234")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012U")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012*")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012Š")]
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012_")]
    [InlineData("' OR 1=1 --")]
    public void Malformed_input_is_not_a_code(string? input)
    {
        Assert.Null(TrackingCode.Normalize(input));
        Assert.False(TrackingCode.TryDecode(input, out _));
    }

    [Fact]
    public void Overlong_input_is_rejected_before_it_is_examined()
    {
        var code = TrackingCode.Generate();
        Assert.Null(TrackingCode.Normalize(code + new string(' ', TrackingCode.MaxInputLength)));
    }

    [Fact]
    public void Format_groups_five_five_five_five_six()
    {
        Assert.Equal("ABCDE-FGHJK-MNPQR-STVWX-YZ0123", TrackingCode.Format("abcdefghjkmnpqrstvwxyz0123"));
        Assert.Throws<ArgumentException>(() => TrackingCode.Format("nope"));
    }

    [Fact]
    public void Hash_is_sha256_of_the_canonical_code_and_every_form_of_input_hashes_alike()
    {
        var code = TrackingCode.Generate();
        var expected = SHA256.HashData(Encoding.ASCII.GetBytes(code));

        Assert.Equal(expected, TrackingCode.Hash(code));
        Assert.Equal(expected, TrackingCode.LookupHash(TrackingCode.Format(code).ToLowerInvariant()));
        Assert.Equal(32, TrackingCode.LookupHash("garbage").Length);
        Assert.Equal(32, TrackingCode.LookupHash(null).Length);
        Assert.Equal(32, TrackingCode.LookupHash(new string('x', 10_000)).Length);
        Assert.NotEqual(TrackingCode.LookupHash("garbage"), TrackingCode.Hash("garbage"));
    }

    [Fact]
    public void Issued_token_stores_only_the_hash_of_its_code()
    {
        var orderId = Guid.CreateVersion7();
        var (token, code) = OrderTrackingToken.Issue(orderId, DateTimeOffset.UtcNow);

        Assert.Equal(orderId, token.OrderId);
        Assert.Equal(TrackingCode.Hash(code), token.TokenHash);
        Assert.Null(token.RevokedAt);
        Assert.Null(token.LastUsedAt);
        Assert.DoesNotContain(code, string.Join(",", typeof(OrderTrackingToken).GetProperties().Select(p => p.GetValue(token)?.ToString())));
    }
}

public class EmailOutboxRuleTests
{
    [Theory]
    [InlineData(OrderStatus.Received, EmailTemplate.OrderReceived)]
    [InlineData(OrderStatus.Accepted, EmailTemplate.OrderAccepted)]
    [InlineData(OrderStatus.Preparing, EmailTemplate.OrderPreparing)]
    [InlineData(OrderStatus.Shipped, EmailTemplate.OrderShipped)]
    [InlineData(OrderStatus.Delivered, EmailTemplate.OrderDelivered)]
    [InlineData(OrderStatus.Cancelled, EmailTemplate.OrderCancelled)]
    [InlineData(OrderStatus.Rejected, EmailTemplate.OrderCancelled)]
    public void Each_status_sends_its_email(OrderStatus status, EmailTemplate template) =>
        Assert.Equal(template, OrderEmailRules.TemplateFor(status));

    [Fact]
    public void Delivery_failed_sends_nothing() => Assert.Null(OrderEmailRules.TemplateFor(OrderStatus.DeliveryFailed));

    [Fact]
    public void Every_status_has_a_rule() =>
        Assert.All(Enum.GetValues<OrderStatus>(), s => _ = OrderEmailRules.TemplateFor(s));

    [Fact]
    public void Dedup_keys_identify_one_event_each()
    {
        var order = Guid.Parse("01933b5e-0000-7000-8000-000000000001");
        var history = Guid.Parse("01933b5e-0000-7000-8000-000000000002");

        Assert.Equal("order:01933b5e-0000-7000-8000-000000000001:received", EmailDedupKeys.OrderReceived(order));
        Assert.Equal(
            "order:01933b5e-0000-7000-8000-000000000001:history:01933b5e-0000-7000-8000-000000000002",
            EmailDedupKeys.StatusChange(order, history));
        Assert.Equal("order:01933b5e-0000-7000-8000-000000000001:resend:2", EmailDedupKeys.ResentConfirmation(order, 2));
        Assert.NotEqual(EmailDedupKeys.ResentConfirmation(order, 1), EmailDedupKeys.ResentConfirmation(order, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailDedupKeys.ResentConfirmation(order, 0));
    }

    [Fact]
    public void Back_off_is_one_five_thirty_minutes_two_and_twelve_hours_then_dead()
    {
        Assert.Equal(6, EmailRetryPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(1), EmailRetryPolicy.DelayAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(5), EmailRetryPolicy.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(30), EmailRetryPolicy.DelayAfter(3));
        Assert.Equal(TimeSpan.FromHours(2), EmailRetryPolicy.DelayAfter(4));
        Assert.Equal(TimeSpan.FromHours(12), EmailRetryPolicy.DelayAfter(5));
        Assert.Null(EmailRetryPolicy.DelayAfter(6));
        Assert.Null(EmailRetryPolicy.DelayAfter(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailRetryPolicy.DelayAfter(0));
    }

    [Fact]
    public void A_new_row_is_pending_and_due_now_and_only_a_dead_row_can_be_retried()
    {
        var now = DateTimeOffset.UtcNow;
        var row = EmailOutboxMessage.Create(Guid.CreateVersion7(), "k", EmailTemplate.OrderAccepted, Guid.CreateVersion7(), " a@b.rs ", "{}", [1, 2], now);

        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Equal(now, row.NextAttemptAt);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal("a@b.rs", row.Recipient);
        Assert.Throws<Zladovce.Domain.Common.DomainException>(() => row.Retry(now, null));
    }

    [Fact]
    public void Log_errors_are_truncated()
    {
        var log = EmailLog.Create(Guid.CreateVersion7(), 1, false, null, new string('x', 5000), DateTimeOffset.UtcNow);
        Assert.Equal(EmailOutboxMessage.MaxErrorLength, log.Error!.Length);
    }
}
