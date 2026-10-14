using System.Globalization;
using System.Text.Json;
using AML.Application.Actions;
using AML.Application.Common;
using AML.Application.Risk;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Screening;

public sealed record ScreeningSubject(string SubjectType, string SubjectId, long? CustomerId, string Name, string? NameArabic, DateOnly? DateOfBirth, string? Nationality);

public sealed record ProviderMatch(string SourceCode, string ListEntryId, string? ListVersion, string EntryHash, string EntryName, decimal Score, string Method,
    string Explanation, string Category, string MatchedAttributesJson);

public sealed record ProviderResponse(IReadOnlyList<ProviderMatch> Matches, string RawResponse);

public sealed class ScreeningProviderException : Exception
{
    /// <summary>Timeout, Unavailable, Malformed.</summary>
    public string Kind { get; }
    public ScreeningProviderException(string kind, string message) : base(message) => Kind = kind;
}

/// <summary>Adapter to an existing screening service (sanctions, PEP, adverse media, internal lists).</summary>
public interface IScreeningProvider
{
    string Code { get; }
    Task<ProviderResponse> ScreenAsync(long tenantId, ScreeningSubject subject, IReadOnlyCollection<long>? onlyEntryIds, CancellationToken ct);
}

public sealed class WatchlistEntryInput
{
    public string EntryId { get; set; } = "";
    public string PrimaryName { get; set; } = "";
    public List<string>? Aliases { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    public string? Category { get; set; }
}

public sealed class ListUpdateRequest
{
    public string VersionLabel { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public DateTime? EffectiveAt { get; set; }
    public List<WatchlistEntryInput> Additions { get; set; } = new();
    public List<WatchlistEntryInput> Amendments { get; set; } = new();
    public List<string> Removals { get; set; } = new();
}

public sealed class ScreeningService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly IScreeningProvider _provider;
    private readonly ICryptoService _crypto;
    private readonly TenantSettingsService _settings;
    private readonly ComplianceActionService _actions;
    private readonly RiskAssessmentService _risk;
    private readonly ILogger<ScreeningService> _log;

    public ScreeningService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, IScreeningProvider provider, ICryptoService crypto,
        TenantSettingsService settings, ComplianceActionService actions, RiskAssessmentService risk, ILogger<ScreeningService> log)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _provider = provider; _crypto = crypto; _settings = settings; _actions = actions; _risk = risk; _log = log;
    }

    public string SubjectHash(ScreeningSubject s) =>
        _crypto.IntegrityHash($"{ArabicNameNormalizer.SearchKey(s.Name, s.NameArabic)}|{s.DateOfBirth:yyyy-MM-dd}|{s.Nationality}");

    public async Task<ScreeningSubject> CustomerSubjectAsync(long customerId, CancellationToken ct)
    {
        var c = await new AccessPolicy(_user, _db).Customers().AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId, ct)
                ?? await _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId && _user.IsSystem, ct)
                ?? throw new NotFoundException("Customer", customerId);
        return new ScreeningSubject("Customer", c.Id.ToString(CultureInfo.InvariantCulture), c.Id, c.NameLatin ?? c.NameArabic ?? "", c.NameArabic, c.DateOfBirth, c.NationalityCountry);
    }

    /// <summary>Screens a customer and its beneficial owners/signatories.</summary>
    public async Task<List<ScreeningRequest>> ScreenCustomerAsync(long customerId, string trigger, string? idempotencyKey, CancellationToken ct)
    {
        var subject = await CustomerSubjectAsync(customerId, ct);
        var key = idempotencyKey ?? $"{trigger}:customer:{customerId}:{_clock.UtcNow:yyyyMMddHHmmssfff}";
        var requests = new List<ScreeningRequest> { await ScreenAsync(subject, trigger, key, null, null, ct) };
        var links = await _db.Set<OwnershipLink>().AsNoTracking().Where(l => l.EntityCustomerId == customerId && l.EffectiveTo == null).ToListAsync(ct);
        foreach (var l in links)
        {
            var type = l.ControlType is "Signatory" or "Director" or "ActingOnBehalf" ? "Signatory" : "BeneficialOwner";
            var s = new ScreeningSubject(type, $"link:{l.Id}", customerId, l.OwnerName ?? "", l.OwnerNameArabic, l.OwnerDateOfBirth, l.OwnerNationality);
            requests.Add(await ScreenAsync(s, trigger, $"{key}:link:{l.Id}", null, null, ct));
        }
        return requests;
    }

    /// <summary>Idempotent screening request. Failures leave an explicit Unavailable state with a retry item (never a false clear).</summary>
    public async Task<ScreeningRequest> ScreenAsync(ScreeningSubject subject, string trigger, string idempotencyKey, IReadOnlyCollection<long>? onlyEntryIds, long? listVersionId, CancellationToken ct)
    {
        var tenantId = _user.TenantId;
        var existing = await _db.Set<ScreeningRequest>().IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return existing;
        var req = new ScreeningRequest
        {
            TenantId = tenantId, IdempotencyKey = idempotencyKey, SubjectType = subject.SubjectType, SubjectId = subject.SubjectId, CustomerId = subject.CustomerId,
            SubjectName = subject.Name, SubjectHash = SubjectHash(subject), ProviderCode = _provider.Code, Trigger = trigger, Status = "Pending", CreatedAt = _clock.UtcNow,
            ListVersionId = listVersionId
        };
        _db.Set<ScreeningRequest>().Add(req);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex))
        {
            _db.Entry(req).State = EntityState.Detached;
            return await _db.Set<ScreeningRequest>().IgnoreQueryFilters().FirstAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey, ct);
        }
        await AttemptAsync(req, subject, onlyEntryIds, ct);
        return req;
    }

    private async Task AttemptAsync(ScreeningRequest req, ScreeningSubject subject, IReadOnlyCollection<long>? onlyEntryIds, CancellationToken ct)
    {
        var timeoutMs = await _settings.IntSettingAsync("Screening:TimeoutMs", 3000, req.TenantId, ct);
        req.Attempts++;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            ProviderResponse resp;
            try
            {
                resp = await _provider.ScreenAsync(req.TenantId, subject, onlyEntryIds, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ScreeningProviderException("Timeout", $"Provider did not respond within {timeoutMs} ms");
            }
            await StoreResultsAsync(req, subject, resp, ct);
            req.Status = "Completed";
            req.CompletedAt = _clock.UtcNow;
            req.LastError = null;
            req.NextAttemptAt = null;
            await ResolveRetryItemAsync(req, ct);
        }
        catch (ScreeningProviderException ex)
        {
            var max = await _settings.IntSettingAsync("Screening:MaxAttempts", 5, req.TenantId, ct);
            req.LastError = $"{ex.Kind}: {ex.Message}";
            req.FailureHistory = string.Join("\n", new[] { req.FailureHistory }.Where(x => x != null).Append($"attempt {req.Attempts} at {_clock.UtcNow:O}: {req.LastError}"));
            req.Status = req.Attempts >= max ? "Failed" : "Unavailable";
            var delay = Math.Min(3600, 30 * Math.Pow(2, req.Attempts - 1));
            req.NextAttemptAt = req.Status == "Unavailable" ? _clock.UtcNow.AddSeconds(delay) : null;
            var item = await _db.Set<RetryQueueItem>().IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == req.TenantId && r.Kind == "Screening" && r.ReferenceId == req.Id.ToString() && r.Status == "Pending", ct);
            if (item is null && req.Status == "Unavailable")
                _db.Set<RetryQueueItem>().Add(item = new RetryQueueItem { TenantId = req.TenantId, Kind = "Screening", ReferenceId = req.Id.ToString(CultureInfo.InvariantCulture), CreatedAt = _clock.UtcNow });
            if (item is not null)
            {
                item.Attempts = req.Attempts; item.LastError = req.LastError; item.NextAttemptAt = req.NextAttemptAt ?? _clock.UtcNow;
                if (req.Status == "Failed") item.Status = "Failed";
            }
            _db.Set<OperationalAlert>().Add(new OperationalAlert
            {
                TenantId = req.TenantId, Category = "ScreeningUnavailable", Message = $"Screening {req.SubjectType} {req.SubjectId} via {req.ProviderCode} failed ({req.LastError}); status {req.Status}.",
                Reference = req.Id.ToString(CultureInfo.InvariantCulture), Severity = req.Status == "Failed" ? "Error" : "Warning", CreatedAt = _clock.UtcNow
            });
            var ps = await _db.Set<ScreeningProviderStatus>().IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == req.TenantId && p.ProviderCode == req.ProviderCode, ct);
            if (ps is not null) { ps.LastFailureAt = _clock.UtcNow; ps.LastError = req.LastError; }
            _log.LogWarning("Screening request {RequestId} failed: {Error}", req.Id, req.LastError);
        }
        await _db.SaveChangesAsync(ct);
        if (req.CustomerId is { } cid) await RefreshCustomerStatusAsync(cid, ct);
    }

    private async Task ResolveRetryItemAsync(ScreeningRequest req, CancellationToken ct)
    {
        var items = await _db.Set<RetryQueueItem>().IgnoreQueryFilters().Where(r => r.TenantId == req.TenantId && r.Kind == "Screening" && r.ReferenceId == req.Id.ToString() && r.Status == "Pending").ToListAsync(ct);
        foreach (var i in items) { i.Status = "Completed"; i.CompletedAt = _clock.UtcNow; }
        var ps = await _db.Set<ScreeningProviderStatus>().IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == req.TenantId && p.ProviderCode == req.ProviderCode, ct);
        if (ps is not null) ps.LastSuccessAt = _clock.UtcNow;
    }

    private async Task StoreResultsAsync(ScreeningRequest req, ScreeningSubject subject, ProviderResponse resp, CancellationToken ct)
    {
        var threshold = await _settings.DecimalSettingAsync("Screening:MatchThreshold", 0.85m, req.TenantId, ct);
        var allow = await _db.Set<AllowListEntry>().IgnoreQueryFilters().Where(a => a.TenantId == req.TenantId && a.SubjectType == subject.SubjectType && a.SubjectId == subject.SubjectId && a.IsActive).ToListAsync(ct);
        var encrypted = _crypto.Encrypt(resp.RawResponse);
        foreach (var m in resp.Matches.Where(m => m.Score >= threshold))
        {
            var al = allow.FirstOrDefault(a => a.SourceCode == m.SourceCode && a.ListEntryId == m.ListEntryId);
            if (al is not null)
            {
                if (al.SubjectHash == req.SubjectHash && al.EntryHash == m.EntryHash)
                {
                    _db.Set<ScreeningSuppressionLog>().Add(new ScreeningSuppressionLog { TenantId = req.TenantId, AllowListEntryId = al.Id, RequestId = req.Id, SubjectId = subject.SubjectId, ListEntryId = m.ListEntryId, At = _clock.UtcNow });
                    req.SuppressedCount++;
                    continue;
                }
                al.IsActive = false;
                al.InvalidatedAt = _clock.UtcNow;
                al.InvalidatedReason = al.SubjectHash != req.SubjectHash ? "Subject identifying attributes changed" : "List entry amended";
                _audit.Record("ALLOWLIST_INVALIDATED", "AllowListEntry", al.Id.ToString(CultureInfo.InvariantCulture), null, new { al.InvalidatedReason, m.ListEntryId });
            }
            var open = await _db.Set<ScreeningResult>().IgnoreQueryFilters().AnyAsync(r => r.TenantId == req.TenantId && r.SubjectType == subject.SubjectType && r.SubjectId == subject.SubjectId &&
                r.SourceCode == m.SourceCode && r.ListEntryId == m.ListEntryId && r.EntryHash == m.EntryHash && r.SubjectHash == req.SubjectHash &&
                r.Status != ScreeningStatuses.FalsePositive, ct);
            if (open) continue; // same logical result already exists (manual vs delta rescreen)
            _db.Set<ScreeningResult>().Add(new ScreeningResult
            {
                TenantId = req.TenantId, RequestId = req.Id, SubjectType = subject.SubjectType, SubjectId = subject.SubjectId, CustomerId = subject.CustomerId,
                SubjectName = subject.Name, ProviderCode = _provider.Code, SourceCode = m.SourceCode, ListEntryId = m.ListEntryId, ListVersion = m.ListVersion,
                EntryHash = m.EntryHash, SubjectHash = req.SubjectHash, MatchScore = m.Score, MatchMethod = m.Method, MatchedAttributesJson = m.MatchedAttributesJson,
                Explanation = m.Explanation, Category = m.Category, Status = ScreeningStatuses.PotentialMatch, ScreenedAt = _clock.UtcNow, ProviderResponseEncrypted = encrypted
            });
            req.ResultCount++;
        }
        if (subject.SubjectType is "BeneficialOwner" or "Signatory" && subject.SubjectId.StartsWith("link:"))
        {
            var lid = long.Parse(subject.SubjectId[5..], CultureInfo.InvariantCulture);
            var link = await _db.Set<OwnershipLink>().IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Id == lid, ct);
            if (link is not null) link.ScreeningStatus = req.ResultCount > 0 ? "PotentialMatch" : "Clear";
        }
    }

    /// <summary>Aggregated customer screening status (kept separate from risk level and alerts).</summary>
    public async Task RefreshCustomerStatusAsync(long customerId, CancellationToken ct)
    {
        var c = await _db.Set<Customer>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == customerId, ct);
        if (c is null) return;
        var results = await _db.Set<ScreeningResult>().IgnoreQueryFilters().AsNoTracking().Where(r => r.CustomerId == customerId).Select(r => new { r.Status, r.SubjectType }).ToListAsync(ct);
        var latest = await _db.Set<ScreeningRequest>().IgnoreQueryFilters().AsNoTracking().Where(r => r.CustomerId == customerId && r.SubjectType == "Customer").OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        string status;
        if (results.Any(r => r.Status == ScreeningStatuses.ConfirmedMatch && r.SubjectType == "Customer")) status = "ConfirmedMatch";
        else if (results.Any(r => r.Status is ScreeningStatuses.PotentialMatch or ScreeningStatuses.UnderReview or ScreeningStatuses.Escalated or ScreeningStatuses.UnableToDetermine)) status = "PotentialMatch";
        else if (latest is null) status = "NotScreened";
        else if (latest.Status is "Unavailable" or "Failed") status = "Unavailable";
        else if (latest.Status == "Pending") status = "Pending";
        else status = "Clear";
        c.ScreeningStatus = status;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ScreeningRequest> RetryAsync(long requestId, bool manual, CancellationToken ct)
    {
        var req = await _db.Set<ScreeningRequest>().IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == _user.TenantId, ct) ?? throw new NotFoundException("Screening request", requestId);
        if (req.Status == "Completed") throw new ConflictException("Screening request already completed.");
        if (manual) _audit.Record("SCREENING_MANUAL_RETRY", "ScreeningRequest", requestId.ToString(CultureInfo.InvariantCulture), new { req.Status, req.LastError }, null);
        ScreeningSubject subject;
        if (req.SubjectType == "Customer" && req.CustomerId is { } cid) subject = await CustomerSubjectAsync(cid, ct);
        else if (req.SubjectId.StartsWith("link:"))
        {
            var lid = long.Parse(req.SubjectId[5..]);
            var l = await _db.Set<OwnershipLink>().IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == lid, ct);
            subject = new ScreeningSubject(req.SubjectType, req.SubjectId, req.CustomerId, l.OwnerName ?? "", l.OwnerNameArabic, l.OwnerDateOfBirth, l.OwnerNationality);
        }
        else subject = new ScreeningSubject(req.SubjectType, req.SubjectId, req.CustomerId, req.SubjectName, null, null, null);
        await AttemptAsync(req, subject, null, ct);
        return req;
    }

    public async Task<int> RetryDueAsync(long tenantId, CancellationToken ct)
    {
        var due = await _db.Set<ScreeningRequest>().IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.Status == "Unavailable" && r.NextAttemptAt <= _clock.UtcNow).Select(r => r.Id).Take(200).ToListAsync(ct);
        foreach (var id in due) await RetryAsync(id, false, ct);
        return due.Count;
    }

    // ---------------- review workflow ----------------
    public async Task<ScreeningResult> TransitionAsync(long resultId, string toStatus, string? notes, string? rowVersion, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ScreeningReview);
        var r = await _db.Set<ScreeningResult>().FirstOrDefaultAsync(x => x.Id == resultId, ct) ?? throw new NotFoundException("Screening result", resultId);
        if (rowVersion is not null) _db.Entry(r).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(rowVersion, "screening result");
        if (!ScreeningStatuses.Transitions.TryGetValue(r.Status, out var allowed) || !allowed.Contains(toStatus))
            throw new ConflictException($"Invalid screening transition {r.Status} -> {toStatus}. Allowed: {string.Join(", ", allowed ?? Array.Empty<string>())}.");
        if (toStatus is ScreeningStatuses.ConfirmedMatch or ScreeningStatuses.FalsePositive && string.IsNullOrWhiteSpace(notes))
            throw new ValidationException("notes", "A rationale is required to confirm or clear a match.");
        var from = r.Status;
        r.Status = toStatus;
        r.Reviewer = _user.Username;
        r.ReviewedAt = _clock.UtcNow;
        r.Notes = notes;
        r.Decision = toStatus;
        r.NeedsReview = false;
        _db.Set<ScreeningReviewHistory>().Add(new ScreeningReviewHistory { TenantId = r.TenantId, ResultId = r.Id, FromStatus = from, ToStatus = toStatus, Actor = _user.Username, At = _clock.UtcNow, Notes = notes });
        _audit.Record("SCREENING_TRANSITION", "ScreeningResult", r.Id.ToString(CultureInfo.InvariantCulture), new { Status = from }, new { Status = toStatus, r.SourceCode, r.ListEntryId, r.ListVersion }, notes);
        if (toStatus == ScreeningStatuses.FalsePositive)
        {
            _db.Set<AllowListEntry>().Add(new AllowListEntry
            {
                TenantId = r.TenantId, SubjectType = r.SubjectType, SubjectId = r.SubjectId, SubjectHash = r.SubjectHash ?? "", SourceCode = r.SourceCode, ListEntryId = r.ListEntryId,
                EntryHash = r.EntryHash ?? "", ListVersion = r.ListVersion, ClearedResultId = r.Id, ClearedBy = _user.Username, ClearedAt = _clock.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
        if (r.CustomerId is { } cid)
        {
            await RefreshCustomerStatusAsync(cid, ct);
            if (toStatus == ScreeningStatuses.ConfirmedMatch)
            {
                var source = await _db.Set<WatchlistSource>().AsNoTracking().FirstOrDefaultAsync(s => s.Code == r.SourceCode, ct);
                if (r.Category == "PEP" && r.SubjectType == "Customer")
                {
                    var c = await _db.Set<Customer>().FirstAsync(x => x.Id == cid, ct);
                    c.IsPep = true;
                    await _db.SaveChangesAsync(ct);
                }
                if (r.SubjectType is "BeneficialOwner" or "Signatory" && r.Category == "PEP" && r.SubjectId.StartsWith("link:"))
                {
                    var linkId = long.Parse(r.SubjectId[5..]);
                    var link = await _db.Set<OwnershipLink>().FirstAsync(l => l.Id == linkId, ct);
                    link.OwnerIsPep = true;
                    link.ScreeningStatus = "ConfirmedPEP";
                    await _db.SaveChangesAsync(ct);
                }
                if (source is not null && r.SubjectType == "Customer" && r.Category is "Sanctions" or "Terrorism" or "ProliferationFinancing")
                    await _actions.OnConfirmedMatchAsync(r, source, ct);
            }
            if (toStatus is ScreeningStatuses.ConfirmedMatch or ScreeningStatuses.FalsePositive)
            {
                try { await _risk.AssessAsync(cid, "ScreeningResult", $"screening:{r.Id}:{toStatus}", $"Screening result {r.Id} {toStatus}", null, ct); }
                catch (ConflictException ex) { _log.LogWarning("Screening-triggered reassessment skipped: {Reason}", ex.Message); }
            }
        }
        return r;
    }

    // ---------------- list management ----------------
    public async Task<WatchlistVersion> ImportListUpdateAsync(string sourceCode, ListUpdateRequest u, CancellationToken ct)
    {
        Guard.Require(_user, Perm.WatchlistsManage);
        var source = await _db.Set<WatchlistSource>().FirstOrDefaultAsync(s => s.Code == sourceCode, ct) ?? throw new NotFoundException("Watchlist source", sourceCode);
        if (string.IsNullOrWhiteSpace(u.VersionLabel)) throw new ValidationException("versionLabel", "Version label is required.");
        if (await _db.Set<WatchlistVersion>().AnyAsync(v => v.SourceId == source.Id && v.VersionLabel == u.VersionLabel, ct))
            throw new ConflictException($"Version {u.VersionLabel} of {sourceCode} was already ingested.");
        var received = _clock.UtcNow;
        var version = new WatchlistVersion
        {
            TenantId = source.TenantId, SourceId = source.Id, SourceCode = source.Code, VersionLabel = u.VersionLabel, PublishedAt = u.PublishedAt, EffectiveAt = u.EffectiveAt ?? u.PublishedAt,
            ReceivedAt = received, Additions = u.Additions.Count, Amendments = u.Amendments.Count, Removals = u.Removals.Count,
            ChangeSetJson = JsonSerializer.Serialize(new { additions = u.Additions.Select(a => a.EntryId), amendments = u.Amendments.Select(a => a.EntryId), removals = u.Removals })
        };
        _db.Set<WatchlistVersion>().Add(version);
        var defaultCategory = source.ListType switch { "PEP" => "PEP", "ProliferationFinancing" => "ProliferationFinancing", "AdverseMedia" => "AdverseMedia", "Internal" => "Internal", _ => "Sanctions" };
        var changed = new List<WatchlistEntry>();
        foreach (var a in u.Additions)
        {
            if (await _db.Set<WatchlistEntry>().AnyAsync(e => e.SourceId == source.Id && e.EntryId == a.EntryId && e.Status == "Active", ct))
                throw new ConflictException($"Entry {a.EntryId} already exists; send it as an amendment.");
            var e = new WatchlistEntry { TenantId = source.TenantId, SourceId = source.Id, SourceCode = source.Code, EntryId = a.EntryId, VersionAdded = u.VersionLabel, IsTestData = source.IsTestData };
            ApplyEntry(e, a, defaultCategory);
            _db.Set<WatchlistEntry>().Add(e);
            changed.Add(e);
        }
        foreach (var a in u.Amendments)
        {
            var e = await _db.Set<WatchlistEntry>().FirstOrDefaultAsync(x => x.SourceId == source.Id && x.EntryId == a.EntryId && x.Status == "Active", ct)
                    ?? throw new ValidationException("amendments", $"Entry {a.EntryId} not found for amendment.");
            ApplyEntry(e, a, defaultCategory);
            e.LastAmendedVersion = u.VersionLabel;
            changed.Add(e);
        }
        var removedIds = new List<string>();
        foreach (var id in u.Removals)
        {
            var e = await _db.Set<WatchlistEntry>().FirstOrDefaultAsync(x => x.SourceId == source.Id && x.EntryId == id && x.Status == "Active", ct);
            if (e is null) continue;
            e.Status = "Removed";
            e.LastAmendedVersion = u.VersionLabel;
            removedIds.Add(id);
        }
        source.CurrentVersion = u.VersionLabel;
        await _db.SaveChangesAsync(ct);

        // Removed entries: open matches flagged for review / release.
        var flagged = await _db.Set<ScreeningResult>().Where(r => r.SourceCode == source.Code && removedIds.Contains(r.ListEntryId) && r.Status != ScreeningStatuses.FalsePositive).ToListAsync(ct);
        foreach (var r in flagged)
        {
            r.NeedsReview = true;
            r.NeedsReviewReason = $"List entry {r.ListEntryId} removed in version {u.VersionLabel}; review and release workflow available.";
        }
        _audit.Record("WATCHLIST_VERSION_INGEST", "WatchlistSource", source.Code, new { previous = version.VersionLabel }, new { u.VersionLabel, version.Additions, version.Amendments, version.Removals });
        await _db.SaveChangesAsync(ct);

        await DeltaRescreenAsync(version, changed.Select(c => c.Id).ToList(), ct);
        return version;
    }

    private void ApplyEntry(WatchlistEntry e, WatchlistEntryInput a, string defaultCategory)
    {
        e.PrimaryName = a.PrimaryName;
        e.AliasesJson = a.Aliases is null ? null : JsonSerializer.Serialize(a.Aliases);
        e.DateOfBirth = a.DateOfBirth;
        e.Nationality = a.Nationality;
        e.Category = a.Category ?? defaultCategory;
        e.Status = "Active";
        e.NameSearchKey = ArabicNameNormalizer.SearchKey(new[] { a.PrimaryName }.Concat(a.Aliases ?? new()).ToArray());
        e.EntryHash = _crypto.IntegrityHash($"{a.PrimaryName}|{string.Join(";", a.Aliases ?? new())}|{a.DateOfBirth:yyyy-MM-dd}|{a.Nationality}|{e.Category}");
    }

    /// <summary>Rescreens customers, beneficial owners, signatories and recent counterparties against the delta only; records time-to-screen.</summary>
    public async Task DeltaRescreenAsync(WatchlistVersion version, List<long> changedEntryIds, CancellationToken ct)
    {
        var v = await _db.Set<WatchlistVersion>().FirstAsync(x => x.Id == version.Id, ct);
        v.RescreenStartedAt = _clock.UtcNow;
        var subjects = new List<ScreeningSubject>();
        if (changedEntryIds.Count > 0)
        {
            subjects.AddRange(await _db.Set<Customer>().AsNoTracking().Where(c => !c.IsAnonymised && c.Status != "Rejected")
                .Select(c => new ScreeningSubject("Customer", c.Id.ToString(), c.Id, c.NameLatin ?? c.NameArabic ?? "", c.NameArabic, c.DateOfBirth, c.NationalityCountry)).ToListAsync(ct));
            subjects.AddRange(await _db.Set<OwnershipLink>().AsNoTracking().Where(l => l.EffectiveTo == null && l.OwnerCustomerId == null)
                .Select(l => new ScreeningSubject(l.ControlType == "Signatory" || l.ControlType == "Director" || l.ControlType == "ActingOnBehalf" ? "Signatory" : "BeneficialOwner",
                    "link:" + l.Id, l.EntityCustomerId, l.OwnerName ?? "", l.OwnerNameArabic, l.OwnerDateOfBirth, l.OwnerNationality)).ToListAsync(ct));
            var since = _clock.UtcNow.AddDays(-await _settings.IntSettingAsync("Screening:CounterpartyLookbackDays", 90, ct: ct));
            var cps = await _db.Set<Transaction>().AsNoTracking().Where(t => t.TimestampUtc >= since && t.BeneficiaryName != null && t.ReceiverCustomerId == null)
                .Select(t => t.BeneficiaryName!).Distinct().Take(5000).ToListAsync(ct);
            subjects.AddRange(cps.Select(n => new ScreeningSubject("Counterparty", "cp:" + ArabicNameNormalizer.SearchKey(n), null, n, null, null, null)));
        }
        foreach (var s in subjects)
        {
            var req = await ScreenAsync(s, "ListUpdate", $"delta:{v.Id}:{s.SubjectType}:{s.SubjectId}", changedEntryIds, v.Id, ct);
            v.MatchesRaised += req.ResultCount;
        }
        v = await _db.Set<WatchlistVersion>().FirstAsync(x => x.Id == version.Id, ct);
        v.SubjectsScreened = subjects.Count;
        v.RescreenCompletedAt = _clock.UtcNow;
        v.TimeToScreenMs = (long)(v.RescreenCompletedAt.Value - v.ReceivedAt).TotalMilliseconds;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<object> CustomerScreeningAsync(long customerId, CancellationToken ct)
    {
        var raw = _user.Has(Perm.ScreeningRaw);
        var results = await _db.Set<ScreeningResult>().AsNoTracking().Where(r => r.CustomerId == customerId).OrderByDescending(r => r.Id).ToListAsync(ct);
        var requests = await _db.Set<ScreeningRequest>().AsNoTracking().Where(r => r.CustomerId == customerId).OrderByDescending(r => r.Id).Take(50).ToListAsync(ct);
        return new
        {
            results = results.Select(r => ToDto(r, raw)),
            requests = requests.Select(r => new { r.Id, r.SubjectType, r.SubjectId, r.SubjectName, r.ProviderCode, r.Trigger, r.Status, r.Attempts, r.NextAttemptAt, r.LastError, r.FailureHistory, r.ResultCount, r.SuppressedCount, r.CreatedAt, r.CompletedAt })
        };
    }

    public object ToDto(ScreeningResult r, bool includeRaw) => new
    {
        r.Id, r.RequestId, r.SubjectType, r.SubjectId, r.CustomerId, r.SubjectName, r.ProviderCode, r.SourceCode, r.ListEntryId, r.ListVersion, r.MatchScore, r.MatchMethod,
        matchedAttributes = r.MatchedAttributesJson is null ? (object?)null : JsonDocument.Parse(r.MatchedAttributesJson).RootElement, r.Explanation, r.Category, r.Status,
        r.ScreenedAt, r.Reviewer, r.Decision, r.Notes, r.ReviewedAt, r.NeedsReview, r.NeedsReviewReason, rowVersion = Convert.ToBase64String(r.RowVersion),
        providerResponse = includeRaw && r.ProviderResponseEncrypted is not null ? _crypto.Decrypt(r.ProviderResponseEncrypted) : null
    };

    public async Task<object> ResultsAsync(string? status, string? category, int take, CancellationToken ct)
    {
        var visible = new AccessPolicy(_user, _db).Customers().Select(c => c.Id);
        var q = _db.Set<ScreeningResult>().AsNoTracking().Where(r => r.CustomerId == null || visible.Contains(r.CustomerId.Value));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(r => r.Category == category);
        var list = await q.OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var raw = _user.Has(Perm.ScreeningRaw);
        if (raw && list.Count > 0) { _audit.Record("SCREENING_RAW_READ", "ScreeningResult", null, null, new { count = list.Count }); await _db.SaveChangesAsync(ct); }
        return list.Select(r => ToDto(r, raw)).ToList();
    }

    public async Task<object> GetResultAsync(long id, CancellationToken ct)
    {
        var r = await _db.Set<ScreeningResult>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Screening result", id);
        var raw = _user.Has(Perm.ScreeningRaw);
        if (raw) { _audit.Record("SCREENING_RAW_READ", "ScreeningResult", id.ToString(CultureInfo.InvariantCulture), null, null); await _db.SaveChangesAsync(ct); }
        var history = await _db.Set<ScreeningReviewHistory>().AsNoTracking().Where(h => h.ResultId == id).OrderBy(h => h.Id).ToListAsync(ct);
        return new { result = ToDto(r, raw), history };
    }
}
