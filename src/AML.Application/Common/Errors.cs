namespace AML.Application.Common;

public abstract class AppException : Exception
{
    protected AppException(string message) : base(message) { }
}

public sealed class ValidationException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IDictionary<string, string[]> errors, string message = "One or more validation errors occurred.") : base(message)
        => Errors = new Dictionary<string, string[]>(errors);

    public ValidationException(string field, string error) : this(new Dictionary<string, string[]> { [field] = new[] { error } }, error) { }

    public static void ThrowIf(Dictionary<string, List<string>> errors)
    {
        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(k => k.Key, v => v.Value.ToArray()),
                string.Join(" ", errors.SelectMany(e => e.Value)));
    }
}

public sealed class NotFoundException : AppException
{
    public NotFoundException(string entity, object id) : base($"{entity} '{id}' was not found.") { }
}

/// <summary>State conflict (concurrency, duplicates, invalid workflow transition, dependency blocking).</summary>
public sealed class ConflictException : AppException
{
    public object? Details { get; }
    public ConflictException(string message, object? details = null) : base(message) => Details = details;
}

/// <summary>Authorization failure, including segregation-of-duties (maker-checker) violations.</summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message) : base(message) { }
}

public static class Guard
{
    public static void FourEyes(string maker, string checker, string what)
    {
        if (string.Equals(maker, checker, StringComparison.OrdinalIgnoreCase))
            throw new ForbiddenException($"Segregation of duties: {what} must be approved by a different user than the requester ({maker}).");
    }

    public static void Require(ICurrentUser user, string permission)
    {
        if (!user.Has(permission)) throw new ForbiddenException($"Permission '{permission}' is required.");
    }

    public static byte[] RowVersion(string? base64, string entity)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new ValidationException("rowVersion", $"rowVersion is required to update {entity} (optimistic concurrency).");
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ValidationException("rowVersion", "rowVersion is not valid base64."); }
    }
}
