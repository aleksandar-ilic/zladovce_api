namespace Zladovce.Domain.Common;

/// <summary>
/// Thrown when a domain invariant is violated. The message is for developers and logs;
/// user-facing Serbian text is produced at the API boundary.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
