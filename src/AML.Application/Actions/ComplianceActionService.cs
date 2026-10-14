using System.Globalization;
using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Actions;

/// <summary>Outbound command to the core/wallet system. Only neutral codes are sent (tipping-off protection).</summary>
public sealed record CoreCommand(long TenantId, string IdempotencyKey, string Command, string Target, string NeutralCode);

/// <summary>Outcome: Ack (applied), Failed (definitely not applied), Unknown (timeout / uncertain).</summary>
public sealed record CoreResult(string Outcome, string? CoreReference, decimal? Amount, string? Currency, string? Error);

public interface ICoreSystemAdapter
{
    string Mode { get; }
    Task<CoreResult> SendAsync(CoreCommand command, CancellationToken ct);
    Task<CoreResult?> LookupAsync(long tenantId, string idempotencyKey, CancellationToken ct);
    Task<string?> TargetStateAsync(long tenantId, string target, CancellationToken ct);
}

public sealed record ActionDto(long Id, string ActionType, long? CustomerId, long? WalletId, long? TransactionId, string? TargetReference, string Status, string IdempotencyKey,
    string NeutralCode, string? InternalReason, bool IsAutomatic, string? TriggerType, long? ScreeningResultId, long? CaseId, string RequestedBy, string? ApprovedBy,
    DateTime? ApprovedAt, string? CoreReference, DateTime? AcknowledgedAt, int Attempts, string? LastError, decimal? FrozenAmount, string? FrozenCurrency, DateTime? FrozenAt,
    string? ReconciliationStatus, DateTime CreatedAt);

public sealed class ActionRequest
{
    public string ActionType { get; set; } = "";
    public long? CustomerId { get; set; }
    public long? WalletId { get; set; }
    public long? TransactionId { get; set; }
    public long? CaseId { get; set; }
    public string? Reason { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class ComplianceActionService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly ICoreSystemAdapter _core;
    private readonly TenantSettingsService _settings;
    private readonly ILogger<ComplianceActionService> _log;

    public ComplianceActionService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, ICoreSystemAdapter core, TenantSettingsService settings, ILogger<ComplianceActionService> log)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _core = core; _settings = settings; _log = log;
    }

    public static ActionDto ToDto(ComplianceAction a, bool showInternal) => new(a.Id, a.ActionType, a.CustomerId, a.WalletId, a.TransactionId, a.TargetReference, a.Status,
        a.IdempotencyKey, a.NeutralCode, showInternal ? a.InternalReason : null, a.IsAutomatic, a.TriggerType, showInternal ? a.ScreeningResultId : null,
        showInternal ? a.CaseId : null, a.RequestedBy, a.ApprovedBy, a.ApprovedAt, a.CoreReference, a.AcknowledgedAt, a.Attempts, a.LastError, a.FrozenAmount,
        a.FrozenCurrency, a.FrozenAt, a.ReconciliationStatus, a.CreatedAt);

    private bool ShowInternal => _user.Has(Perm.ActionsRequest) || _user.Has(Perm.ActionsApprove);

    public async Task<List<ActionDto>> ListAsync(long? customerId, string? status, CancellationToken ct)
    {
        var q = _db.Set<ComplianceAction>().AsNoTracking();
        if (!string.IsNullOrEmpty(_user.DataScopeRegion))
        {
            var visible = new AccessPolicy(_user, _db).Customers().Select(c => c.Id);
            q = q.Where(a => a.CustomerId == null || visible.Contains(a.CustomerId.Value));
        }
        if (customerId is not null) q = q.Where(a => a.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
        return (await q.OrderByDescending(a => a.Id).Take(500).ToListAsync(ct)).Select(a => ToDto(a, ShowInternal)).ToList();
    }

    public async Task<ActionDto> GetAsync(long id, CancellationToken ct) =>
        ToDto(await _db.Set<ComplianceAction>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Action", id), ShowInternal);

    /// <summary>Manual action request. Release and non-mandatory restrictions require four-eyes approval per policy.</summary>
    public async Task<ActionDto> RequestAsync(ActionRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ActionsRequest);
        if (!ActionTypes.All.Contains(r.ActionType)) throw new ValidationException("actionType", $"Unknown action type. Allowed: {string.Join(", ", ActionTypes.All)}.");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw new ValidationException("reason", "A reason is required.");
        var (target, customerId, walletId) = await ResolveTargetAsync(r, ct);
        var policy = await _db.Set<ComplianceActionPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.TriggerType == "Manual" && p.ActionType == r.ActionType && p.IsActive, ct);
        var requiresApproval = r.ActionType is ActionTypes.Release or ActionTypes.ReleaseTransaction || (policy?.RequiresApproval ?? true);
        var key = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? $"MANUAL:{r.ActionType}:{target}:{Guid.NewGuid():N}" : r.IdempotencyKey!;
        var existing = await _db.Set<ComplianceAction>().AsNoTracking().FirstOrDefaultAsync(a => a.IdempotencyKey == key, ct);
        if (existing is not null) return ToDto(existing, true);
        var a = new ComplianceAction
        {
            TenantId = _user.TenantId, ActionType = r.ActionType, CustomerId = customerId, WalletId = walletId, TransactionId = r.TransactionId, TargetReference = target,
            Status = requiresApproval ? "PendingApproval" : "Queued", IdempotencyKey = key, InternalReason = r.Reason, IsAutomatic = false, TriggerType = "Manual",
            CaseId = r.CaseId, RequestedBy = _user.Username, CreatedAt = _clock.UtcNow, NeutralCode = NeutralCodeFor(r.ActionType)
        };
        _db.Set<ComplianceAction>().Add(a);
        _audit.Record("COMPLIANCE_ACTION_REQUEST", "ComplianceAction", key, null, new { r.ActionType, target, requiresApproval }, r.Reason, restricted: r.CaseId is not null);
        await _db.SaveChangesAsync(ct);
        if (!requiresApproval) await DispatchAsync(a.Id, ct);
        return await GetAsync(a.Id, ct);
    }

    private async Task<(string Target, long? CustomerId, long? WalletId)> ResolveTargetAsync(ActionRequest r, CancellationToken ct)
    {
        if (r.WalletId is { } wid)
        {
            var w = await _db.Set<Wallet>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == wid, ct) ?? throw new NotFoundException("Wallet", wid);
            return ($"WALLET:{w.ExternalWalletId}", w.CustomerId ?? r.CustomerId, w.Id);
        }
        if (r.TransactionId is { } tid)
        {
            var t = await _db.Set<Transaction>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Transaction", tid);
            return ($"TXN:{t.SourceSystem}:{t.SourceTransactionId}", t.SenderCustomerId, null);
        }
        if (r.CustomerId is { } cid)
        {
            var c = await new AccessPolicy(_user, _db).Customers().AsNoTracking().FirstOrDefaultAsync(x => x.Id == cid, ct) ?? throw new NotFoundException("Customer", cid);
            return ($"CUSTOMER:{c.SourceSystem}:{c.ExternalId}", c.Id, null);
        }
        throw new ValidationException("target", "A wallet, transaction or customer target is required.");
    }

    public static string NeutralCodeFor(string actionType) => actionType switch
    {
        ActionTypes.Release or ActionTypes.ReleaseTransaction => "RESTRICTION_LIFTED",
        ActionTypes.RequireKycRefresh => "KYC_REFRESH_REQUIRED",
        ActionTypes.HoldTransaction => "TRANSACTION_UNDER_REVIEW",
        ActionTypes.RejectTransaction => "TRANSACTION_DECLINED",
        _ => "RESTRICTED_CONTACT_COMPLIANCE"
    };

    public async Task<ActionDto> ApproveAsync(long id, bool approve, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ActionsApprove);
        var a = await _db.Set<ComplianceAction>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Action", id);
        if (a.Status != "PendingApproval") throw new ConflictException("Action is not pending approval.");
        Guard.FourEyes(a.RequestedBy, _user.Username, $"{a.ActionType} approval");
        a.Status = approve ? "Queued" : "Rejected";
        a.ApprovedBy = _user.Username;
        a.ApprovedAt = _clock.UtcNow;
        _audit.Record(approve ? "COMPLIANCE_ACTION_APPROVE" : "COMPLIANCE_ACTION_REJECT", "ComplianceAction", a.IdempotencyKey, null, new { a.ActionType, a.TargetReference }, reason);
        await _db.SaveChangesAsync(ct);
        if (approve) await DispatchAsync(a.Id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Automatic actions configured for a confirmed screening match (e.g. immediate freeze for UN/national lists).</summary>
    public async Task<List<ActionDto>> OnConfirmedMatchAsync(ScreeningResult result, WatchlistSource source, CancellationToken ct)
    {
        var created = new List<ActionDto>();
        if (result.CustomerId is not { } cid) return created;
        var trigger = source.IsMandatory ? "ConfirmedMandatoryListMatch" : "ConfirmedListMatch";
        var policies = await _db.Set<ComplianceActionPolicy>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == result.TenantId && p.IsActive && p.TriggerType == trigger).ToListAsync(ct);
        policies = policies.Where(p => string.IsNullOrWhiteSpace(p.Categories) || p.Categories.Split(',').Contains(result.Category)).ToList();
        var wallets = await _db.Set<Wallet>().IgnoreQueryFilters().AsNoTracking().Where(w => w.CustomerId == cid && w.TenantId == result.TenantId).ToListAsync(ct);
        foreach (var p in policies)
        {
            var targets = p.ActionType == ActionTypes.FreezeWallet
                ? wallets.Select(w => ($"WALLET:{w.ExternalWalletId}", (long?)w.Id)).ToList()
                : new List<(string, long?)> { ($"CUSTOMER:{cid}", null) };
            foreach (var (target, walletId) in targets)
            {
                var key = $"SCREENING:{result.Id}:{p.ActionType}:{target}";
                var existing = await _db.Set<ComplianceAction>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.IdempotencyKey == key, ct);
                if (existing is not null) { created.Add(ToDto(existing, true)); continue; }
                var a = new ComplianceAction
                {
                    TenantId = result.TenantId, ActionType = p.ActionType, CustomerId = cid, WalletId = walletId, TargetReference = target,
                    Status = p.RequiresApproval ? "PendingApproval" : "Queued", IdempotencyKey = key, InternalReason = $"Confirmed {result.Category} match {source.Code}/{result.ListEntryId}",
                    IsAutomatic = p.IsAutomatic, TriggerType = trigger, ScreeningResultId = result.Id, RequestedBy = "system", CreatedAt = _clock.UtcNow,
                    NeutralCode = NeutralCodeFor(p.ActionType)
                };
                _db.Set<ComplianceAction>().Add(a);
                _audit.Record("COMPLIANCE_ACTION_AUTO", "ComplianceAction", key, null, new { p.ActionType, target, trigger }, a.InternalReason, restricted: true);
                await _db.SaveChangesAsync(ct);
                if (a.Status == "Queued") await DispatchAsync(a.Id, ct);
                created.Add(await GetAsync(a.Id, ct));
            }
        }
        return created;
    }

    /// <summary>Compliance action configured on a rule (e.g. HoldTransaction) subject to the RuleAction policy.</summary>
    public async Task RequestFromRuleAsync(string actionType, long transactionId, long? customerId, long alertId, string reason, long tenantId, CancellationToken ct)
    {
        var key = $"RULE:{alertId}:{actionType}:{transactionId}";
        if (await _db.Set<ComplianceAction>().IgnoreQueryFilters().AnyAsync(a => a.IdempotencyKey == key, ct)) return;
        var policy = await _db.Set<ComplianceActionPolicy>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.TriggerType == "RuleAction" && p.ActionType == actionType && p.IsActive, ct);
        var t = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == transactionId, ct);
        var a = new ComplianceAction
        {
            TenantId = tenantId, ActionType = actionType, CustomerId = customerId, TransactionId = transactionId, TargetReference = $"TXN:{t.SourceSystem}:{t.SourceTransactionId}",
            Status = policy is null || policy.RequiresApproval ? "PendingApproval" : "Queued", IdempotencyKey = key, InternalReason = reason, IsAutomatic = policy?.IsAutomatic ?? false,
            TriggerType = "RuleAction", RequestedBy = "system", CreatedAt = _clock.UtcNow, NeutralCode = NeutralCodeFor(actionType)
        };
        _db.Set<ComplianceAction>().Add(a);
        _audit.Record("COMPLIANCE_ACTION_RULE", "ComplianceAction", key, null, new { actionType, transactionId, alertId }, reason);
        await _db.SaveChangesAsync(ct);
        if (a.Status == "Queued") await DispatchAsync(a.Id, ct);
    }

    /// <summary>Sends an action with its idempotency key. Uncertain outcomes are reconciled before any retry.</summary>
    public async Task DispatchAsync(long id, CancellationToken ct)
    {
        var a = await _db.Set<ComplianceAction>().IgnoreQueryFilters().FirstAsync(x => x.Id == id, ct);
        if (a.Status is "Applied" or "Rejected" or "PendingApproval" or "Cancelled") return;
        CoreResult result;
        if (a.Status == "Unknown")
        {
            var lookup = await _core.LookupAsync(a.TenantId, a.IdempotencyKey, ct);
            a.ReconciliationStatus = lookup is null ? "NotFoundOnCore" : "FoundOnCore";
            result = lookup ?? await SendAsync(a, ct);
        }
        else result = await SendAsync(a, ct);
        a.Attempts++;
        switch (result.Outcome)
        {
            case "Ack":
                a.Status = "Applied";
                a.CoreReference = result.CoreReference;
                a.AcknowledgedAt = _clock.UtcNow;
                a.LastError = null;
                a.NextAttemptAt = null;
                await ApplyLocalEffectAsync(a, result, ct);
                break;
            case "Unknown":
                a.Status = "Unknown";
                a.LastError = result.Error ?? "No acknowledgement received";
                a.NextAttemptAt = _clock.UtcNow.AddSeconds(Backoff(a.Attempts));
                await OpsAlertAsync(a, ct);
                break;
            default:
                var max = await _settings.IntSettingAsync("Actions:MaxAttempts", 5, a.TenantId, ct);
                a.Status = a.Attempts >= max ? "Failed" : "Retrying";
                a.LastError = result.Error;
                a.NextAttemptAt = a.Status == "Retrying" ? _clock.UtcNow.AddSeconds(Backoff(a.Attempts)) : null;
                await OpsAlertAsync(a, ct);
                break;
        }
        _audit.Record("COMPLIANCE_ACTION_DISPATCH", "ComplianceAction", a.IdempotencyKey, null, new { a.Status, result.Outcome, a.Attempts, a.CoreReference, core = _core.Mode }, restricted: a.CaseId is not null);
        await _db.SaveChangesAsync(ct);
    }

    private static double Backoff(int attempts) => Math.Min(3600, 15 * Math.Pow(2, attempts - 1));

    private async Task<CoreResult> SendAsync(ComplianceAction a, CancellationToken ct)
    {
        try
        {
            return await _core.SendAsync(new CoreCommand(a.TenantId, a.IdempotencyKey, a.ActionType, a.TargetReference ?? "", a.NeutralCode), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or HttpRequestException)
        {
            return new CoreResult("Unknown", null, null, null, ex.Message);
        }
    }

    private async Task OpsAlertAsync(ComplianceAction a, CancellationToken ct)
    {
        _db.Set<OperationalAlert>().Add(new OperationalAlert
        {
            TenantId = a.TenantId, Category = "CoreActionFailure", Message = $"{a.ActionType} {a.TargetReference} not applied ({a.Status}): {a.LastError}",
            Reference = a.Id.ToString(CultureInfo.InvariantCulture), Severity = a.Status == "Failed" ? "Error" : "Warning", CreatedAt = _clock.UtcNow
        });
        await Task.CompletedTask;
    }

    private async Task ApplyLocalEffectAsync(ComplianceAction a, CoreResult r, CancellationToken ct)
    {
        var customer = a.CustomerId is { } cid ? await _db.Set<Customer>().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var wallet = a.WalletId is { } wid ? await _db.Set<Wallet>().IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == wid, ct) : null;
        switch (a.ActionType)
        {
            case ActionTypes.FreezeWallet:
            case ActionTypes.BlockOutgoing:
            case ActionTypes.BlockIncoming:
                if (wallet is not null) wallet.IsFrozen = true;
                if (customer is not null) customer.IsFrozen = true;
                a.FrozenAmount = r.Amount;
                a.FrozenCurrency = r.Currency;
                a.FrozenAt = _clock.UtcNow;
                var hours = await _settings.JurisdictionDecimalAsync("FREEZE_NOTIFICATION_HOURS", 24, null, a.TenantId, ct);
                _db.Set<AuthorityNotification>().Add(new AuthorityNotification
                {
                    TenantId = a.TenantId, ActionId = a.Id, CustomerId = a.CustomerId, Authority = await _settings.SettingAsync("Sanctions:CompetentAuthority", "Competent authority (to be confirmed)", a.TenantId, ct),
                    Status = "Draft", DueAt = _clock.UtcNow.AddHours((double)hours), CreatedAt = _clock.UtcNow,
                    ContentJson = JsonSerializer.Serialize(new
                    {
                        reportType = "AssetFreezeNotification", customer = customer is null ? null : new { customer.Id, customer.NameLatin, customer.NameArabic, customer.ExternalId, customer.DateOfBirth },
                        wallet = wallet?.ExternalWalletId, frozenAmount = r.Amount, currency = r.Currency, frozenAt = a.FrozenAt, coreReference = r.CoreReference,
                        basis = a.InternalReason, screeningResultId = a.ScreeningResultId
                    })
                });
                break;
            case ActionTypes.Release:
                if (wallet is not null) wallet.IsFrozen = false;
                if (customer is not null && !await _db.Set<Wallet>().IgnoreQueryFilters().AnyAsync(w => w.CustomerId == customer.Id && w.IsFrozen && w.Id != a.WalletId, ct)) customer.IsFrozen = false;
                break;
            case ActionTypes.SuspendAgent:
                if (customer is not null) customer.Status = "Suspended";
                break;
            case ActionTypes.HoldTransaction:
                if (a.TransactionId is { } tid)
                {
                    var t = await _db.Set<Transaction>().IgnoreQueryFilters().FirstAsync(x => x.Id == tid, ct);
                    if (!await _db.Set<HoldQueueItem>().IgnoreQueryFilters().AnyAsync(h => h.TransactionId == tid && h.Status == "Held", ct))
                    {
                        var sla = await _settings.IntSettingAsync("Realtime:HoldSlaMinutes", 30, a.TenantId, ct);
                        _db.Set<HoldQueueItem>().Add(new HoldQueueItem
                        {
                            TenantId = a.TenantId, TransactionId = tid, SourceSystem = t.SourceSystem, SourceTransactionId = t.SourceTransactionId, Reason = a.InternalReason ?? "Rule action",
                            SlaDueAt = _clock.UtcNow.AddMinutes(sla), CoreActionId = a.Id, CreatedAt = _clock.UtcNow
                        });
                    }
                }
                break;
        }
    }

    public async Task<int> RetryDueAsync(long tenantId, CancellationToken ct)
    {
        var due = await _db.Set<ComplianceAction>().IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && (a.Status == "Retrying" || a.Status == "Unknown") && a.NextAttemptAt <= _clock.UtcNow).Select(a => a.Id).ToListAsync(ct);
        foreach (var id in due) await DispatchAsync(id, ct);
        return due.Count;
    }

    /// <summary>Compares local applied freeze state with the actual core state; mismatches are reported.</summary>
    public async Task<List<ActionReconciliation>> ReconcileAsync(long tenantId, CancellationToken ct)
    {
        var applied = await _db.Set<ComplianceAction>().IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.Status == "Applied" && (a.ActionType == ActionTypes.FreezeWallet || a.ActionType == ActionTypes.SuspendAgent)).ToListAsync(ct);
        var released = applied.Where(a => _db.Set<ComplianceAction>().IgnoreQueryFilters().Any(r => r.ActionType == ActionTypes.Release && r.Status == "Applied" && r.TargetReference == a.TargetReference && r.Id > a.Id)).Select(a => a.Id).ToHashSet();
        var results = new List<ActionReconciliation>();
        foreach (var a in applied.Where(a => !released.Contains(a.Id)))
        {
            var core = await _core.TargetStateAsync(tenantId, a.TargetReference ?? "", ct) ?? "Unknown";
            var expected = a.ActionType == ActionTypes.SuspendAgent ? "Suspended" : "Frozen";
            var r = new ActionReconciliation { TenantId = tenantId, ActionId = a.Id, LocalState = expected, CoreState = core, IsMismatch = core != expected, CheckedAt = _clock.UtcNow, Note = core != expected ? "Core state differs from AML action record" : null };
            a.ReconciliationStatus = r.IsMismatch ? "Mismatch" : "Matched";
            if (r.IsMismatch)
                _db.Set<OperationalAlert>().Add(new OperationalAlert { TenantId = tenantId, Category = "ActionReconciliationMismatch", Message = $"{a.ActionType} {a.TargetReference}: AML={expected}, core={core}", Reference = a.Id.ToString(CultureInfo.InvariantCulture), Severity = "Error", CreatedAt = _clock.UtcNow });
            _db.Set<ActionReconciliation>().Add(r);
            results.Add(r);
        }
        await _db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<List<AuthorityNotification>> NotificationsAsync(long? customerId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ActionsRead);
        var q = _db.Set<AuthorityNotification>().AsNoTracking();
        if (customerId is not null) q = q.Where(n => n.CustomerId == customerId);
        return await q.OrderByDescending(n => n.Id).Take(200).ToListAsync(ct);
    }
}
