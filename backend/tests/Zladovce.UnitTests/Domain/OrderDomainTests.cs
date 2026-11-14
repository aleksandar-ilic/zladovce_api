using Zladovce.Application.Cart;
using Zladovce.Application.Orders;
using Zladovce.Domain.Cart;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Common;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;

namespace Zladovce.UnitTests.Domain;

public class OrderStatusMachineTests
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Expected = new()
    {
        [OrderStatus.Received] = [OrderStatus.Accepted, OrderStatus.Rejected, OrderStatus.Cancelled],
        [OrderStatus.Accepted] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.DeliveryFailed],
        [OrderStatus.DeliveryFailed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Rejected] = [],
    };

    public static TheoryData<OrderStatus, OrderStatus> AllPairs()
    {
        var data = new TheoryData<OrderStatus, OrderStatus>();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_pair_is_allowed_exactly_as_in_P1_5(OrderStatus from, OrderStatus to)
    {
        var allowed = Expected[from].Contains(to);

        Assert.Equal(allowed, OrderStatusMachine.CanTransition(from, to));
        if (allowed)
        {
            Assert.Equal(to, OrderStatusMachine.Describe(from, to).To);
        }
        else
        {
            Assert.Throws<DomainException>(() => OrderStatusMachine.Describe(from, to));
        }
    }

    [Fact]
    public void The_table_covers_every_status_and_lists_next_statuses_in_order()
    {
        Assert.Equal(64, AllPairs().Count);
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.Equal(Expected[status], OrderStatusMachine.AllowedNext(status));
            Assert.Equal(Expected[status].Length == 0, OrderStatusMachine.IsFinal(status));
        }
    }

    [Theory]
    [InlineData(OrderStatus.Preparing)]
    [InlineData(OrderStatus.DeliveryFailed)]
    public void Only_moving_to_shipped_requires_a_shipment(OrderStatus from)
    {
        Assert.True(OrderStatusMachine.Describe(from, OrderStatus.Shipped).RequiresShipment);

        var others = Enum.GetValues<OrderStatus>()
            .SelectMany(f => OrderStatusMachine.TransitionsFrom(f))
            .Where(t => t.To != OrderStatus.Shipped);
        Assert.All(others, t => Assert.False(t.RequiresShipment));
    }

    [Theory]
    [InlineData(OrderStatus.Received, OrderStatus.Rejected, StockReturnPolicy.Automatic)]
    [InlineData(OrderStatus.Received, OrderStatus.Cancelled, StockReturnPolicy.Automatic)]
    [InlineData(OrderStatus.Accepted, OrderStatus.Cancelled, StockReturnPolicy.Automatic)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Cancelled, StockReturnPolicy.Automatic)]
    [InlineData(OrderStatus.DeliveryFailed, OrderStatus.Cancelled, StockReturnPolicy.AdminChoice)]
    [InlineData(OrderStatus.Received, OrderStatus.Accepted, StockReturnPolicy.None)]
    [InlineData(OrderStatus.Accepted, OrderStatus.Preparing, StockReturnPolicy.None)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Shipped, StockReturnPolicy.None)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Delivered, StockReturnPolicy.None)]
    [InlineData(OrderStatus.Shipped, OrderStatus.DeliveryFailed, StockReturnPolicy.None)]
    [InlineData(OrderStatus.DeliveryFailed, OrderStatus.Shipped, StockReturnPolicy.None)]
    public void Stock_returns_follow_P1_6(OrderStatus from, OrderStatus to, StockReturnPolicy expected) =>
        Assert.Equal(expected, OrderStatusMachine.Describe(from, to).StockReturn);

    [Fact]
    public void Every_transition_has_a_stock_rule_test_above()
    {
        // 11 transitions exist in total; each one is listed in Stock_returns_follow_P1_6.
        Assert.Equal(11, Enum.GetValues<OrderStatus>().Sum(s => OrderStatusMachine.AllowedNext(s).Count));
    }

    [Theory]
    [InlineData(OrderStatus.Received, "Primljena")]
    [InlineData(OrderStatus.Accepted, "Prihvaćena")]
    [InlineData(OrderStatus.Preparing, "U pripremi")]
    [InlineData(OrderStatus.Shipped, "Poslata")]
    [InlineData(OrderStatus.Delivered, "Isporučena")]
    [InlineData(OrderStatus.Cancelled, "Otkazana")]
    [InlineData(OrderStatus.Rejected, "Odbijena")]
    [InlineData(OrderStatus.DeliveryFailed, "Neuspešna isporuka")]
    public void Serbian_labels(OrderStatus status, string label) => Assert.Equal(label, OrderStatusLabels.Of(status));
}

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();

    internal static Order NewOrder(decimal shipping = 350m)
    {
        var (order, _) = Order.Place(
            "ZLD-2026-000001",
            Guid.NewGuid(),
            new byte[32],
            Guid.NewGuid(),
            Guid.NewGuid(),
            new OrderContact(" Ana ", "Jović", "ana@example.com", "064 123 4567"),
            "  ",
            [
                new OrderLine(Guid.NewGuid(), "Džem od šljiva", "370 g", "DZ-370", 590m, 2),
                new OrderLine(Guid.NewGuid(), "Sok od jabuke", "1 l", "SOK-1", 333.33m, 3),
            ],
            shipping,
            Now);
        return order;
    }

    [Fact]
    public void Place_snapshots_items_and_computes_totals()
    {
        var (order, history) = Order.Place(
            "ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(),
            new OrderContact("Ana", "Jović", "ana@example.com", "+381 64 123 4567"), " Pozvati pre dostave ",
            [new OrderLine(Guid.NewGuid(), "Džem", "370 g", "DZ", 590m, 2), new OrderLine(Guid.NewGuid(), "Sok", "1 l", "S", 333.33m, 3)],
            350m, Now);

        Assert.Equal(OrderStatus.Received, order.Status);
        Assert.Equal(PaymentMethod.CashOnDelivery, order.PaymentMethod);
        Assert.Equal([1180m, 999.99m], order.Items.Select(i => i.LineTotal));
        Assert.Equal(2179.99m, order.Subtotal);
        Assert.Equal(350m, order.ShippingPrice);
        Assert.Equal(order.Subtotal + order.ShippingPrice, order.Total);
        Assert.Equal("+381641234567", order.ContactPhone);
        Assert.Equal("Pozvati pre dostave", order.DeliveryNote);
        Assert.Null(order.CollectedAmount);
        Assert.Null(history.PreviousStatus);
        Assert.Equal(OrderStatus.Received, history.NewStatus);
        Assert.Null(history.ChangedByAdminId);
        Assert.Equal(order.Id, history.OrderId);
    }

    [Fact]
    public void Place_refuses_bad_input()
    {
        var contact = new OrderContact("Ana", "Jović", "ana@example.com", "0641234567");
        OrderLine Line(decimal price = 100m, int quantity = 1, Guid? id = null) => new(id ?? Guid.NewGuid(), "P", "V", "S", price, quantity);

        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[31], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line()], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line(0m)], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line(1.001m)], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line(quantity: 0)], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line()], -1m, Now));
        var id = Guid.NewGuid();
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, null, [Line(id: id), Line(id: id)], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact with { Phone = "+44 20 7946 0958" }, null, [Line()], 0m, Now));
        Assert.Throws<DomainException>(() => Order.Place("ZLD-2026-000001", Guid.NewGuid(), new byte[32], Guid.NewGuid(), Guid.NewGuid(), contact, new string('x', 501), [Line()], 0m, Now));
    }

    [Fact]
    public void Change_status_follows_the_machine_and_writes_history()
    {
        var order = NewOrder();

        var history = order.ChangeStatus(OrderStatus.Accepted, Admin, " interno ", "Hvala!", Now.AddMinutes(1));

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(OrderStatus.Received, history.PreviousStatus);
        Assert.Equal(OrderStatus.Accepted, history.NewStatus);
        Assert.Equal(Admin, history.ChangedByAdminId);
        Assert.Equal("interno", history.InternalNote);
        Assert.Equal("Hvala!", history.CustomerMessage);
        Assert.Equal(Now.AddMinutes(1), order.UpdatedAt);

        Assert.Throws<DomainException>(() => order.ChangeStatus(OrderStatus.Delivered, Admin, null, null, Now));
        Assert.Throws<DomainException>(() => order.ChangeStatus(OrderStatus.Preparing, Guid.Empty, null, null, Now));
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Payment_is_recorded_only_for_delivered_orders()
    {
        var order = NewOrder();
        Assert.Throws<DomainException>(() => order.RecordPayment(100m, Now, Now));

        order.ChangeStatus(OrderStatus.Accepted, Admin, null, null, Now);
        order.ChangeStatus(OrderStatus.Preparing, Admin, null, null, Now);
        order.ChangeStatus(OrderStatus.Shipped, Admin, null, null, Now);
        Assert.Throws<DomainException>(() => order.RecordPayment(100m, Now, Now));
        order.ChangeStatus(OrderStatus.Delivered, Admin, null, null, Now);

        Assert.Throws<DomainException>(() => order.RecordPayment(0m, Now, Now));
        Assert.Throws<DomainException>(() => order.RecordPayment(10.005m, Now, Now));

        order.RecordPayment(2500m, Now, Now);
        Assert.Equal(2500m, order.CollectedAmount);
        Assert.Equal(Now, order.CollectedAt);

        order.ClearPayment(Now);
        Assert.Null(order.CollectedAmount);
        Assert.Null(order.CollectedAt);
    }
}

public class OrderNumberAndTimeTests
{
    [Theory]
    [InlineData(2026, 1, "ZLD-2026-000001")]
    [InlineData(2026, 42, "ZLD-2026-000042")]
    [InlineData(2027, 999_999, "ZLD-2027-999999")]
    [InlineData(2027, 1_000_000, "ZLD-2027-1000000")]
    public void Order_numbers_are_formatted(int year, int sequence, string expected) =>
        Assert.Equal(expected, OrderNumber.Format(year, sequence));

    [Fact]
    public void Order_number_rejects_out_of_range_values()
    {
        Assert.Throws<DomainException>(() => OrderNumber.Format(2026, 0));
        Assert.Throws<DomainException>(() => OrderNumber.Format(1999, 1));
    }

    [Theory]
    [InlineData("2026-12-31T22:59:59Z", 2026)] // 23:59:59 in Belgrade (CET, UTC+1)
    [InlineData("2026-12-31T23:00:00Z", 2027)] // midnight in Belgrade: already the new year while it is still 2026 in UTC
    [InlineData("2027-01-01T00:30:00Z", 2027)]
    [InlineData("2026-06-30T21:59:59Z", 2026)] // summer time (CEST, UTC+2)
    public void Year_is_taken_in_Belgrade(string utc, int year) =>
        Assert.Equal(year, SerbianTime.YearOf(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2026-11-14", "2026-11-13T23:00:00Z")] // winter: UTC+1
    [InlineData("2026-07-01", "2026-06-30T22:00:00Z")] // summer: UTC+2
    [InlineData("2026-03-29", "2026-03-28T23:00:00Z")] // day of the switch to summer time starts in winter time
    public void Start_of_a_Serbian_day_in_UTC(string date, string utc) =>
        Assert.Equal(
            DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture),
            SerbianTime.StartOfDay(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Fallback_zone_matches_the_system_zone()
    {
        var fallback = SerbianTime.CentralEuropean();
        for (var instant = new DateTimeOffset(2025, 1, 1, 0, 30, 0, TimeSpan.Zero); instant.Year < 2028; instant = instant.AddHours(7))
        {
            Assert.Equal(SerbianTime.Zone.GetUtcOffset(instant), fallback.GetUtcOffset(instant));
        }
    }
}

public class SerbianContactTests
{
    [Theory]
    [InlineData("064 123 4567", "+381641234567")]
    [InlineData("064/123-4567", "+381641234567")]
    [InlineData("0641234567", "+381641234567")]
    [InlineData("+381 64 123 4567", "+381641234567")]
    [InlineData("+381641234567", "+381641234567")]
    [InlineData("00381641234567", "+381641234567")]
    [InlineData("+381 (0)64 123 4567", "+381641234567")]
    [InlineData("063 123456", "+38163123456")]
    [InlineData("011 123 4567", "+381111234567")]
    [InlineData(" 065.555.1234 ", "+381655551234")]
    public void Serbian_numbers_are_normalised_to_E164(string input, string expected) =>
        Assert.Equal(expected, SerbianPhone.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("641234567")] // no leading 0 or country code
    [InlineData("+385 91 123 4567")] // Croatia
    [InlineData("0044 20 7946 0958")] // another country
    [InlineData("064 123")] // too short
    [InlineData("064 1234 56789")] // too long
    [InlineData("064-123-ABCD")]
    [InlineData("064+1234567")]
    [InlineData("+381 00 123 4567")]
    public void Other_values_are_not_Serbian_numbers(string? input) => Assert.Null(SerbianPhone.Normalize(input));

    [Theory]
    [InlineData("11000", true)]
    [InlineData("21000", true)]
    [InlineData("1100", false)]
    [InlineData("110000", false)]
    [InlineData("11 000", false)]
    [InlineData("1100a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Postal_codes_have_five_digits(string? code, bool valid) => Assert.Equal(valid, SerbianPostalCode.IsValid(code));
}

public class CourierRulesTests
{
    [Theory]
    [InlineData("https://kurir.example/pracenje?broj={trackingNumber}", true)]
    [InlineData("https://kurir.example/{trackingNumber}", true)]
    [InlineData("http://kurir.example/{trackingNumber}", false)]
    [InlineData("https://kurir.example/pracenje", false)]
    [InlineData("kurir.example/{trackingNumber}", false)]
    [InlineData("https:///{trackingNumber}", false)]
    [InlineData("https://user:pw@kurir.example/{trackingNumber}", false)]
    [InlineData("https://kurir.example/ {trackingNumber}", false)]
    [InlineData("javascript:alert(1)//{trackingNumber}", false)]
    public void Tracking_url_template_must_be_https_with_the_placeholder(string template, bool valid) =>
        Assert.Equal(valid, CourierCompany.IsValidTemplate(template));

    [Fact]
    public void Tracking_url_is_built_from_the_template_and_escapes_the_number()
    {
        Assert.Equal("https://k.example/t?n=AB%2012%2F3", CourierCompany.TrackingUrl("https://k.example/t?n={trackingNumber}", " AB 12/3 "));
        Assert.Null(CourierCompany.TrackingUrl(null, "AB"));
        Assert.Null(CourierCompany.TrackingUrl("https://k.example/{trackingNumber}", null));
        Assert.Null(CourierCompany.TrackingUrl("https://k.example/{trackingNumber}", " "));
    }

    [Fact]
    public void Courier_refuses_invalid_values()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<DomainException>(() => CourierCompany.Create(" ", null, true, 0, now));
        Assert.Throws<DomainException>(() => CourierCompany.Create("X", "http://x.example/{trackingNumber}", true, 0, now));
        Assert.Throws<DomainException>(() => CourierCompany.Create("X", null, true, -1, now));
        var courier = CourierCompany.Create(" Kurir ", "  ", true, 2, now);
        Assert.Equal("Kurir", courier.Name);
        Assert.Null(courier.TrackingUrlTemplate);
    }
}

public class CheckoutRequestHashTests
{
    private static readonly Guid A = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("01900000-0000-7000-8000-000000000002");

    private static CreateOrderRequest Request(
        IReadOnlyList<CreateOrderItemRequest>? items = null,
        string email = "ana@example.com",
        string phone = "064 123 4567",
        string? note = null,
        string firstName = "Ana",
        decimal? expectedTotal = null) => new()
        {
            Items = items ?? [Item(A, 2), Item(B, 1)],
            Customer = new CheckoutCustomerRequest { FirstName = firstName, LastName = "Jović", Email = email, Phone = phone },
            Address = new CheckoutAddressRequest { City = "Novi Sad", Street = "Bulevar 1", PostalCode = "21000" },
            DeliveryNote = note,
            AcceptedTerms = true,
            ExpectedTotal = expectedTotal,
        };

    private static CreateOrderItemRequest Item(Guid id, int quantity, decimal? known = null) =>
        new() { VariantId = id, Quantity = quantity, KnownUnitPrice = known };

    private static byte[] Hash(CreateOrderRequest request) => CheckoutRequestHash.Compute(NormalizedCheckout.From(request));

    [Fact]
    public void Equivalent_requests_have_the_same_hash()
    {
        var baseline = Hash(Request());

        Assert.Equal(32, baseline.Length);
        Assert.Equal(baseline, Hash(Request()));
        Assert.Equal(baseline, Hash(Request(items: [Item(B, 1), Item(A, 2)])));
        Assert.Equal(baseline, Hash(Request(items: [Item(A, 1), Item(B, 1), Item(A, 1)])));
        Assert.Equal(baseline, Hash(Request(items: [Item(A, 2, 590m), Item(B, 1, 100m)], expectedTotal: 1500m)));
        Assert.Equal(baseline, Hash(Request(email: "  ANA@Example.com ")));
        Assert.Equal(baseline, Hash(Request(phone: "+381 64 123 4567")));
        Assert.Equal(baseline, Hash(Request(note: "   ")));
        Assert.Equal(baseline, Hash(Request(firstName: " Ana ")));
    }

    [Fact]
    public void Different_content_changes_the_hash()
    {
        var baseline = Hash(Request());

        Assert.NotEqual(baseline, Hash(Request(items: [Item(A, 3), Item(B, 1)])));
        Assert.NotEqual(baseline, Hash(Request(items: [Item(A, 2)])));
        Assert.NotEqual(baseline, Hash(Request(email: "ana2@example.com")));
        Assert.NotEqual(baseline, Hash(Request(phone: "064 123 4568")));
        Assert.NotEqual(baseline, Hash(Request(note: "Zvoniti dva puta")));
        Assert.NotEqual(baseline, Hash(Request(firstName: "Anа"))); // Cyrillic а
    }
}

public class CheckoutValidationTests
{
    private static readonly CreateOrderRequestValidator Validator = new();

    private static CreateOrderRequest Valid() => new()
    {
        Items = [new CreateOrderItemRequest { VariantId = Guid.NewGuid(), Quantity = 1 }],
        Customer = new CheckoutCustomerRequest { FirstName = "Đorđe", LastName = "O'Brien-Šarić", Email = "djordje@example.rs", Phone = "064 123 4567" },
        Address = new CheckoutAddressRequest { City = "Čačak", Street = "Gradsko šetalište 12/3", PostalCode = "32000" },
        DeliveryNote = null,
        AcceptedTerms = true,
    };

    private static Dictionary<string, string[]> Errors(CreateOrderRequest request) =>
        Validator.Validate(request).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    [Fact]
    public void A_complete_request_with_Serbian_letters_is_valid() => Assert.Empty(Errors(Valid()));

    [Fact]
    public void Each_field_has_a_Serbian_message()
    {
        var request = Valid() with
        {
            Customer = new CheckoutCustomerRequest { FirstName = " ", LastName = "J0vić", Email = "ana@", Phone = "12345" },
            Address = new CheckoutAddressRequest { City = "", Street = new string('x', 201), PostalCode = "1100" },
            DeliveryNote = new string('n', 501),
            AcceptedTerms = false,
        };

        var errors = Errors(request);

        Assert.Equal(["Ime je obavezno."], errors["Customer.FirstName"]);
        Assert.Equal(["Prezime sme da sadrži samo slova, razmak, crticu, tačku i apostrof."], errors["Customer.LastName"]);
        Assert.Equal(["Unesite ispravnu adresu e-pošte (npr. ime@primer.rs)."], errors["Customer.Email"]);
        Assert.Equal(["Unesite ispravan broj telefona u Srbiji, npr. 064 123 4567 ili +381 64 123 4567."], errors["Customer.Phone"]);
        Assert.Equal(["Mesto je obavezno."], errors["Address.City"]);
        Assert.Equal(["Ulica i broj mogu imati najviše 200 znakova."], errors["Address.Street"]);
        Assert.Equal(["Poštanski broj ima tačno 5 cifara."], errors["Address.PostalCode"]);
        Assert.Equal(["Napomena može imati najviše 500 znakova."], errors["DeliveryNote"]);
        Assert.Equal(["Morate prihvatiti uslove kupovine."], errors["AcceptedTerms"]);
    }

    [Fact]
    public void Items_follow_the_cart_rules()
    {
        var errors = Errors(Valid() with
        {
            Items = [new CreateOrderItemRequest { VariantId = Guid.Empty, Quantity = 100, KnownUnitPrice = 1.001m }],
        });

        Assert.True(errors.ContainsKey("Items[0].VariantId"));
        Assert.True(errors.ContainsKey("Items[0].Quantity"));
        Assert.True(errors.ContainsKey("Items[0].KnownUnitPrice"));
        Assert.Equal(["Korpa je prazna."], Errors(Valid() with { Items = [] })["Items"]);
        Assert.True(Errors(Valid() with { Customer = null })["Customer"].Length == 1);
        Assert.True(Errors(Valid() with { ExpectedTotal = 1.234m }).ContainsKey("ExpectedTotal"));
    }
}

public class QuantityLimitReasonTests
{
    private static CartVariantSnapshot Variant(int stock) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ajvar", "ajvar", "370 g", "AJ-370", 370m, MeasurementUnit.Gram, 650m, null, null, true, stock);

    [Theory]
    [InlineData(3, 5, QuantityLimitReason.Stock)]      // 3 on stock, cap 20
    [InlineData(19, 25, QuantityLimitReason.Stock)]
    [InlineData(20, 25, QuantityLimitReason.OrderLimit)] // stock equals the cap: the cap is what applies
    [InlineData(500, 30, QuantityLimitReason.OrderLimit)]
    public void Reduced_lines_say_whether_stock_or_the_order_cap_applies(int stock, int requested, QuantityLimitReason reason)
    {
        var variant = Variant(stock);
        var quote = CartQuoteCalculator.Calculate([new CartItem(variant.VariantId, requested)],
            new Dictionary<Guid, CartVariantSnapshot> { [variant.VariantId] = variant }, new ShippingTerms(true, 0m, null, null), 20);

        var adjustment = Assert.Single(quote.Adjustments);
        Assert.Equal(CartAdjustmentReason.QuantityReduced, adjustment.Reason);
        Assert.Equal(reason, adjustment.LimitReason);
    }

    [Fact]
    public void Other_adjustments_have_no_limit_reason()
    {
        var variant = Variant(5);
        var quote = CartQuoteCalculator.Calculate(
            [new CartItem(variant.VariantId, 1, 600m), new CartItem(Guid.NewGuid(), 1)],
            new Dictionary<Guid, CartVariantSnapshot> { [variant.VariantId] = variant }, new ShippingTerms(true, 0m, null, null), 20);

        Assert.Equal(2, quote.Adjustments.Count);
        Assert.All(quote.Adjustments, a => Assert.Null(a.LimitReason));
    }
}
