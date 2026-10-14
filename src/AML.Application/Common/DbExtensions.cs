using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AML.Application.Common;

public static class DbExtensions
{
    /// <summary>Begins a transaction unless one is already active (allows composition of atomic service operations).</summary>
    public static async Task<IDbContextTransaction?> BeginIfNoneAsync(this DatabaseFacade db, CancellationToken ct) =>
        db.CurrentTransaction is null ? await db.BeginTransactionAsync(ct) : null;

    public static Task CommitIfOwnedAsync(this IDbContextTransaction? tx, CancellationToken ct) => tx is null ? Task.CompletedTask : tx.CommitAsync(ct);

    public static Task RollbackIfOwnedAsync(this IDbContextTransaction? tx, CancellationToken ct) => tx is null ? Task.CompletedTask : tx.RollbackAsync(ct);
}
