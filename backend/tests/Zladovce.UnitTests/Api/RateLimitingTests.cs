using System.Net;
using Microsoft.AspNetCore.Http;
using Zladovce.Api.Endpoints;
using Zladovce.Api.RateLimiting;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Orders;

namespace Zladovce.UnitTests.Api;

public class RateLimitingTests
{
    [Fact]
    public void Windows_are_fixed_and_aligned_to_the_epoch()
    {
        var window = TimeSpan.FromMinutes(10);

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
            RateLimitKeys.WindowStart(new DateTimeOffset(2026, 11, 14, 10, 9, 59, TimeSpan.Zero), window));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 10, 10, 0, TimeSpan.Zero),
            RateLimitKeys.WindowStart(new DateTimeOffset(2026, 11, 14, 10, 10, 0, TimeSpan.Zero), window));
        Assert.Equal(TimeSpan.Zero, RateLimitKeys.WindowStart(new DateTimeOffset(2026, 11, 14, 12, 34, 56, TimeSpan.FromHours(2)), window).Offset);
    }

    [Fact]
    public void Bucket_key_contains_the_policy_and_only_a_hash_of_the_address()
    {
        var key = RateLimitKeys.BucketKey("CreateOrder", "203.0.113.7");

        Assert.StartsWith("CreateOrder:", key);
        Assert.DoesNotContain("203.0.113.7", key);
        Assert.Equal("CreateOrder:".Length + 64, key.Length);
        Assert.Equal(key, RateLimitKeys.BucketKey("CreateOrder", "203.0.113.7"));
        Assert.NotEqual(key, RateLimitKeys.BucketKey("CreateOrder", "203.0.113.8"));
    }

    [Fact]
    public void Policies_have_the_documented_defaults_and_can_be_overridden()
    {
        var defaults = new RateLimitingOptions().Resolve(RateLimitPolicies.CreateOrder);
        Assert.Equal(5, defaults.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.Window);

        var options = new RateLimitingOptions();
        options.Policies[RateLimitPolicies.CreateOrder] = new RateLimitPolicyOptions { PermitLimit = 2, WindowSeconds = 30 };
        var overridden = options.Resolve(RateLimitPolicies.CreateOrder);
        Assert.Equal(2, overridden.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(30), overridden.Window);

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Resolve("Unknown"));
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(1, 1)]
    [InlineData(59.01, 60)]
    [InlineData(-3, 1)]
    public void Retry_after_is_whole_seconds_and_at_least_one(double seconds, int expected) =>
        Assert.Equal(expected, new RateLimitedException(TimeSpan.FromSeconds(seconds)).RetryAfterSeconds);

    private static HttpContext Context(string? forwardedFor, string? remote = "10.0.0.1")
    {
        var context = new DefaultHttpContext();
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return context;
    }

    [Theory]
    [InlineData("198.51.100.1, 203.0.113.9", 1, "203.0.113.9")] // API Gateway appended the caller; the first entry is client-supplied
    [InlineData("198.51.100.1, 203.0.113.9, 192.0.2.5", 2, "203.0.113.9")] // one more trusted proxy in front
    [InlineData("203.0.113.9", 3, "203.0.113.9")] // fewer entries than hops: the leftmost one
    [InlineData("not-an-ip", 1, "10.0.0.1")] // unusable header: the connection address
    [InlineData(null, 1, "10.0.0.1")]
    [InlineData("198.51.100.1", 0, "10.0.0.1")] // header ignored
    public void Client_address_is_taken_from_the_trusted_end_of_X_Forwarded_For(string? header, int hops, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), ClientIpResolver.Resolve(Context(header), new ClientIpOptions { TrustedProxyHops = hops }));

    [Fact]
    public void Mapped_IPv4_addresses_are_normalised()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.9"), ClientIpResolver.Resolve(Context(null, "::ffff:203.0.113.9"), new ClientIpOptions()));
        Assert.Null(ClientIpResolver.Resolve(Context(null, null), new ClientIpOptions()));
    }

    [Theory]
    [InlineData("0193a1b2-0000-7000-8000-000000000001", true)]
    [InlineData(" 0193a1b2-0000-7000-8000-000000000001 ", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Idempotency_key_must_be_a_uuid(string? header, bool valid)
    {
        if (valid)
        {
            Assert.NotEqual(Guid.Empty, CheckoutEndpoints.ParseIdempotencyKey(header));
        }
        else
        {
            var exception = Assert.Throws<RequestValidationException>(() => CheckoutEndpoints.ParseIdempotencyKey(header));
            Assert.True(exception.Errors.ContainsKey("idempotencyKey"));
        }
    }

    [Theory]
    [InlineData("064 123", "64123")]
    [InlineData("+381 64 123", "64123")]
    [InlineData("0038164123", "64123")]
    [InlineData("64123", "64123")]
    [InlineData("06", null)]
    [InlineData("Ana", null)]
    [InlineData("ZLD-2026-000001", null)]
    public void Phone_search_ignores_country_code_and_trunk_zero(string search, string? expected) =>
        Assert.Equal(expected, OrderAdminService.PhoneFragment(search));
}
