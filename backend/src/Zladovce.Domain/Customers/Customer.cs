using Zladovce.Domain.Common;

namespace Zladovce.Domain.Customers;

/// <summary>
/// A guest buyer (table <c>customers</c>), one row per email address, with the latest name and phone.
/// Created or updated by checkout through an upsert on the email, so concurrent orders cannot create duplicates.
/// </summary>
public sealed class Customer
{
    private Customer()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, case-insensitive (<c>citext</c>).</summary>
    public string Email { get; private set; } = null!;
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;

    /// <summary>E.164.</summary>
    public string Phone { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>
/// A delivery address (table <c>addresses</c>). Immutable: a changed address is a new row, so an order's address can never
/// change afterwards. Checkout inserts one row per order.
/// </summary>
public sealed class Address
{
    private Address()
    {
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public string City { get; private set; } = null!;

    /// <summary>Street and house number.</summary>
    public string Street { get; private set; } = null!;

    /// <summary>Five digits.</summary>
    public string PostalCode { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static Address Create(Guid customerId, string city, string street, string postalCode, DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
        {
            throw new DomainException("An address belongs to a customer.");
        }

        var trimmedCity = Text.NullIfBlank(city) ?? throw new DomainException("City is required.");
        var trimmedStreet = Text.NullIfBlank(street) ?? throw new DomainException("Street is required.");
        var code = postalCode?.Trim();
        if (!SerbianPostalCode.IsValid(code))
        {
            throw new DomainException("A postal code has five digits.");
        }

        return new Address
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            City = trimmedCity,
            Street = trimmedStreet,
            PostalCode = code!,
            CreatedAt = now,
        };
    }
}
