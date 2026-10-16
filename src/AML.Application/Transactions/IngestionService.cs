using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AML.Application.Alerts;
using AML.Application.Common;
using AML.Application.Risk;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Transactions;

/// <summary>
/// Idempotent transaction ingestion: (source system, source transaction ID) is the idempotency key. Duplicates are
/// recorded, conflicting payloads are quarantined as data-quality issues, out-of-order status events never regress
/// state, reversals are linked (also when they arrive first), FX conversion uses effective-dated rates, and every
/// record carries run, attempt, error and correlation metadata. Poison messages go to the dead-letter state.
/// </summary>
public sealed class IngestionService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly ICorrelationContext _corr;
    private readonly TenantSettingsService _settings;
    private readonly FxService _fx;
    private readonly AlertFactory _alerts;
    private readonly IFaultInjector _faults;
    private readonly ITransientErrorDetector _transient;
    private readonly IAuditService _audit;
    private readonly ILogger<IngestionService> _log;

    public IngestionService(IAmlDbContext db, ICurrentUser user, IClock clock, ICorrelationContext corr, TenantSettingsService settings, FxService fx,
        AlertFactory alerts, IFaultInjector faults, ITransientErrorDetector transient, IAuditService audit, ILogger<IngestionService> log)
    {
        _db = db; _user = user; _clock = clock; _corr = corr; _settings = settings; _fx = fx; _alerts = alerts; _faults = faults; _transient = transient; _audit = audit; _log = log;
    }

    public async Task<IngestionBatchResult> IngestAsync(string source, IngestionBatchRequest req, CancellationToken ct)
    {
        var tenantId = _user.TenantId;
        var src = await _db.Set<SourceSystem>().AsNoTracking().FirstOrDefaultAsync(s => s.Code == source && s.IsActive, ct)
                  ?? throw new ValidationException("source", $"Unknown or inactive source system '{source}'.");
        if (req.Transactions.Count == 0) throw new ValidationException("transactions", "At least one transaction is required.");
        if (req.Transactions.Count > 5000) throw new ValidationException("transactions", "Maximum 5000 transactions per batch.");

        var run = new IngestionRun
        {
            TenantId = tenantId, SourceSystem = src.Code, BatchId = string.IsNullOrWhiteSpace(req.BatchId) ? Guid.NewGuid().ToString("N") : req.BatchId!,
            ReceivedAt = _clock.UtcNow, RecordCount = req.Transactions.Count, CorrelationId = _corr.CorrelationId, SubmittedBy = _user.Username
        };
        _db.Set<IngestionRun>().Add(run);
        foreach (var ctl in req.ControlTotals ?? new())
        {
            var existing = await _db.Set<SourceControlTotal>().FirstOrDefaultAsync(x => x.SourceSystem == src.Code && x.BusinessDate == ctl.BusinessDate && x.Currency == ctl.Currency, ct);
            if (existing is null) _db.Set<SourceControlTotal>().Add(existing = new SourceControlTotal { TenantId = tenantId, SourceSystem = src.Code, BusinessDate = ctl.BusinessDate, Currency = ctl.Currency });
            existing.ReportedCount = ctl.Count;
            existing.ReportedAmount = ctl.Amount;
            existing.ReceivedAt = _clock.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        var results = new List<IngestionRecordResult>();
        foreach (var raw in req.Transactions)
        {
            var rawText = raw.GetRawText();
            string? stxId = null;
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("sourceTransactionId", out var sid) && sid.ValueKind == JsonValueKind.String) stxId = sid.GetString();
            var record = new IngestionRecord
            {
                TenantId = tenantId, RunId = run.Id, SourceSystem = src.Code, SourceTransactionId = stxId, PayloadHash = Sha256(rawText),
                CorrelationId = _corr.CorrelationId, ReceivedAt = _clock.UtcNow
            };
            _db.Set<IngestionRecord>().Add(record);
            await _db.SaveChangesAsync(ct);
            _db.Set<RawTransactionPayload>().Add(new RawTransactionPayload { TenantId = tenantId, IngestionRecordId = record.Id, Payload = rawText, StoredAt = _clock.UtcNow });
            await _db.SaveChangesAsync(ct);
            results.Add(await ProcessRecordAsync(record.Id, rawText, ct));
        }

        run = await _db.Set<IngestionRun>().FirstAsync(r => r.Id == run.Id, ct);
        run.ProcessedCount = results.Count(r => r.Status == "Processed");
        run.DuplicateCount = results.Count(r => r.Status == "Duplicate");
        run.ConflictCount = results.Count(r => r.Status == "Conflict");
        run.ErrorCount = results.Count(r => r.Status is "DeadLetter" or "Retrying");
        run.Status = run.ErrorCount + run.ConflictCount > 0 ? "CompletedWithErrors" : "Completed";
        run.CompletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new IngestionBatchResult(run.Id, run.BatchId, run.Status, run.RecordCount, run.ProcessedCount, run.DuplicateCount, run.ConflictCount, run.ErrorCount,
            run.CorrelationId, results);
    }

    /// <summary>Processes one stored record with bounded in-process retry for transient errors.</summary>
    public async Task<IngestionRecordResult> ProcessRecordAsync(long recordId, string rawText, CancellationToken ct, bool isReplay = false)
    {
        var maxAttempts = await _settings.IntSettingAsync("Ingestion:MaxAttempts", 3, ct: ct);
        var baseDelayMs = await _settings.IntSettingAsync("Ingestion:RetryBaseDelayMs", 50, ct: ct);
        var errors = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (status, txnId, error) = await ProcessOnceAsync(recordId, rawText, isReplay, ct);
                await FinishRecordAsync(recordId, status, txnId, error, attempt, errors, ct);
                return new IngestionRecordResult(await SourceIdAsync(recordId, ct), status, txnId, error, recordId);
            }
            catch (Exception ex) when (_transient.IsTransient(ex) && !ct.IsCancellationRequested)
            {
                _db.ChangeTracker.Clear();
                errors.Add($"attempt {attempt} at {_clock.UtcNow:O}: {ex.GetType().Name}: {ex.Message}");
                _log.LogWarning("Transient ingestion error on record {RecordId} attempt {Attempt}: {Error}", recordId, attempt, ex.Message);
                if (attempt >= maxAttempts)
                {
                    await FinishRecordAsync(recordId, "Retrying", null, "Transient failure after retries: " + ex.Message, attempt, errors, ct);
                    var rec = await _db.Set<IngestionRecord>().FirstAsync(r => r.Id == recordId, ct);
                    _db.Set<RetryQueueItem>().Add(new RetryQueueItem
                    {
                        TenantId = rec.TenantId, Kind = "Ingestion", ReferenceId = recordId.ToString(CultureInfo.InvariantCulture),
                        NextAttemptAt = _clock.UtcNow.AddSeconds(30), Attempts = attempt, LastError = ex.Message, CreatedAt = _clock.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);
                    return new IngestionRecordResult(rec.SourceTransactionId, "Retrying", null, ex.Message, recordId);
                }
                await Task.Delay(baseDelayMs * (int)Math.Pow(2, attempt - 1), ct);
            }
        }
    }

    private async Task<string?> SourceIdAsync(long recordId, CancellationToken ct) =>
        await _db.Set<IngestionRecord>().Where(r => r.Id == recordId).Select(r => r.SourceTransactionId).FirstOrDefaultAsync(ct);

    private async Task FinishRecordAsync(long recordId, string status, long? txnId, string? error, int attempts, List<string> errors, CancellationToken ct)
    {
        var rec = await _db.Set<IngestionRecord>().FirstAsync(r => r.Id == recordId, ct);
        rec.Status = status;
        rec.TransactionId = txnId ?? rec.TransactionId;
        rec.AttemptCount += attempts;
        rec.LastError = error;
        if (errors.Count > 0) rec.ErrorHistory = string.Join("\n", new[] { rec.ErrorHistory }.Where(x => x != null).Concat(errors));
        rec.ProcessedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(string Status, long? TxnId, string? Error)> ProcessOnceAsync(long recordId, string rawText, bool isReplay, CancellationToken ct)
    {
        var rec = await _db.Set<IngestionRecord>().AsNoTracking().FirstAsync(r => r.Id == recordId, ct);
        TransactionMessage? m;
        try
        {
            m = JsonSerializer.Deserialize<TransactionMessage>(rawText, TransactionMessage.Options);
        }
        catch (JsonException ex)
        {
            return await DeadLetterAsync(rec, $"Malformed payload: {ex.Message.Split('.')[0]}", ct);
        }
        if (m is null) return await DeadLetterAsync(rec, "Empty payload", ct);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(m.SourceTransactionId)) problems.Add("sourceTransactionId is required");
        if (string.IsNullOrWhiteSpace(m.Type)) problems.Add("type is required");
        if (string.IsNullOrWhiteSpace(m.Status)) problems.Add("status is required");
        if (m.EventTime is null) problems.Add("eventTime is required");
        if (m.Amount is null) problems.Add("amount is required");
        else if (m.Amount < 0) problems.Add("amount must not be negative (reversals are signalled with isReversal)");
        if (string.IsNullOrWhiteSpace(m.Currency)) problems.Add("currency is required");
        else
        {
            var cur = m.Currency.Trim().ToUpperInvariant();
            if (!await _db.Set<LookupValue>().AnyAsync(l => l.Category == "CURRENCY" && l.Code == cur && l.IsActive, ct))
                problems.Add($"unsupported currency '{m.Currency}' (never defaulted)");
        }
        if (m.InvoiceAmount is < 0) problems.Add("invoiceAmount must not be negative");
        if (m.Fee is < 0) problems.Add("fee must not be negative");
        if (m.Commission is < 0) problems.Add("commission must not be negative");
        if (m.Mcc is { } mcc && !System.Text.RegularExpressions.Regex.IsMatch(mcc.Trim(), "^[0-9]{4}$")) problems.Add("mcc must be a 4-digit merchant category code");
        if (m.SubType is { Length: > 50 }) problems.Add("subType exceeds 50 characters");
        if (m.InvoiceCurrency is { } ic && ic.Trim().Length != 3) problems.Add("invoiceCurrency must be an ISO 4217 code");
        if (m.InvoiceAmount is not null && string.IsNullOrWhiteSpace(m.InvoiceCurrency)) problems.Add("invoiceCurrency is required with invoiceAmount");
        if (problems.Count > 0) return await DeadLetterAsync(rec, "Validation failed: " + string.Join("; ", problems), ct);

        _faults.Check("ingestion.process");
        var source = rec.SourceSystem;
        var eventUtc = DateTime.SpecifyKind(m.EventTime!.Value.ToUniversalTime(), DateTimeKind.Utc);
        var normalized = await NormalizeStatusAsync(source, m.Status!, rec.TenantId, ct);
        var contentHash = ContentHash(m);

        await using var tx = await _db.Database.BeginIfNoneAsync(ct);
        var existing = await _db.Set<Transaction>().FirstOrDefaultAsync(t => t.SourceSystem == source && t.SourceTransactionId == m.SourceTransactionId, ct);
        if (existing is not null)
        {
            if (existing.PayloadHash == rec.PayloadHash)
            {
                await tx.CommitIfOwnedAsync(ct);
                return ("Duplicate", existing.Id, null);
            }
            if (existing.ContentHash != contentHash)
            {
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = rec.TenantId, IssueType = "PayloadConflict", EntityType = "Transaction", EntityId = existing.Id.ToString(CultureInfo.InvariantCulture),
                    Description = $"Message for {source}/{m.SourceTransactionId} (record {rec.Id}) has the same ID but different content (hash {rec.PayloadHash[..12]} vs {existing.PayloadHash[..12]}). Original kept; quarantined for review.",
                    Severity = "High", CreatedAt = _clock.UtcNow
                });
                await _db.SaveChangesAsync(ct);
                await tx.CommitIfOwnedAsync(ct);
                return ("Conflict", existing.Id, "Same source transaction ID with different payload; original not overwritten");
            }
            // Status update for an existing transaction (possibly out of order).
            var applied = existing.LastSourceEventAt is null || eventUtc > existing.LastSourceEventAt;
            _db.Set<TransactionStatusHistory>().Add(new TransactionStatusHistory
            {
                TenantId = rec.TenantId, TransactionId = existing.Id, SourceStatus = m.Status!, NormalizedStatus = normalized, SourceEventAt = eventUtc,
                ReceivedAt = _clock.UtcNow, Applied = applied, Note = applied ? null : "Older event ignored (out of order); state not regressed"
            });
            if (applied && existing.Status is not (TxnStatuses.Reversed or TxnStatuses.PartiallyReversed))
            {
                existing.SourceStatus = m.Status!;
                existing.Status = normalized;
                existing.LastSourceEventAt = eventUtc;
            }
            else if (applied) existing.LastSourceEventAt = eventUtc;
            await _db.SaveChangesAsync(ct);
            await RefreshAggregatesAsync(existing, ct);
            await tx.CommitIfOwnedAsync(ct);
            return ("Processed", existing.Id, null);
        }

        var tenant = await _settings.TenantAsync(rec.TenantId, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var currency = m.Currency!.Trim().ToUpperInvariant();
        var conv = await _fx.ConvertAsync(rec.TenantId, m.Amount!.Value, currency, tenant.BaseCurrency, eventUtc, ct);
        var t = new Transaction
        {
            TenantId = rec.TenantId, SourceSystem = source, SourceTransactionId = m.SourceTransactionId!, OriginalReference = m.OriginalReference,
            PayloadHash = rec.PayloadHash, ContentHash = contentHash, SenderWalletExternalId = m.SenderWallet, ReceiverWalletExternalId = m.ReceiverWallet,
            TransactionType = m.Type!, SourceStatus = m.Status!, Status = normalized, Channel = m.Channel, Amount = m.Amount.Value, Currency = currency, Fee = m.Fee,
            Commission = m.Commission, SubType = string.IsNullOrWhiteSpace(m.SubType) ? null : m.SubType.Trim(), Mcc = m.Mcc?.Trim(),
            SenderBalanceBefore = m.SenderBalanceBefore, SenderBalanceAfter = m.SenderBalanceAfter, ReceiverBalanceBefore = m.ReceiverBalanceBefore, ReceiverBalanceAfter = m.ReceiverBalanceAfter,
            BaseCurrency = tenant.BaseCurrency, BaseAmount = conv.BaseAmount, FxRate = conv.RateUsed?.Value ?? (conv.Status == "SameCurrency" ? 1m : null),
            FxRateDate = conv.RateUsed?.EffectiveFrom, FxRateId = conv.RateUsed?.Id, FxRateVersion = conv.RateUsed?.Version, FxStatus = conv.Status,
            BusinessDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(eventUtc, tz)), TimestampUtc = eventUtc, LastSourceEventAt = eventUtc,
            OriginalSourceTransactionId = m.OriginalSourceTransactionId, IsReversal = m.IsReversal, IsRefund = m.IsRefund,
            SourceCountry = m.SourceCountry?.ToUpperInvariant(), DestinationCountry = m.DestinationCountry?.ToUpperInvariant(),
            OriginatorName = m.Originator?.Name, OriginatorAccount = m.Originator?.Account, OriginatorAddress = m.Originator?.Address,
            OriginatorNationalId = m.Originator?.NationalId, OriginatorDateOfBirth = m.Originator?.DateOfBirth, OriginatorPlaceOfBirth = m.Originator?.PlaceOfBirth,
            BeneficiaryName = m.Beneficiary?.Name, BeneficiaryAccount = m.Beneficiary?.Account, IntermediaryInstitution = m.IntermediaryInstitution,
            OrderingInstitution = m.OrderingInstitution, Remarks = m.Remarks,
            InvoiceNumber = m.InvoiceNumber, InvoiceAmount = m.InvoiceAmount, InvoiceCurrency = m.InvoiceCurrency?.Trim().ToUpperInvariant(), GoodsCategory = m.GoodsCategory, CopResult = m.CopResult, DeviceId = m.DeviceId, IpAddress = m.IpAddress,
            IngestedAt = _clock.UtcNow, IngestionRunId = rec.RunId, IsTestData = m.IsTestData
        };
        var dq = new List<string>();
        if (conv.Status == "Missing")
        {
            dq.Add("MissingFxRate");
            _db.Set<DataQualityIssue>().Add(new DataQualityIssue
            {
                TenantId = rec.TenantId, IssueType = "MissingFxRate", EntityType = "Transaction", EntityId = m.SourceTransactionId,
                Description = $"No effective FX rate {currency}->{tenant.BaseCurrency} at {eventUtc:O}; base amount not computed (rate never defaulted to 1).",
                Severity = "High", CreatedAt = _clock.UtcNow
            });
        }
        if (t.Amount == 0)
        {
            var zeroTypes = RiskScoringEngine.Split(await _settings.SettingAsync("Ingestion:ZeroAmountAllowedTypes", "BalanceInquiry,FeeWaiver", rec.TenantId, ct));
            if (!zeroTypes.Contains(t.TransactionType, StringComparer.OrdinalIgnoreCase))
            {
                dq.Add("ZeroAmount");
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = rec.TenantId, IssueType = "ZeroAmount", EntityType = "Transaction", EntityId = m.SourceTransactionId,
                    Description = $"Zero-amount {t.TransactionType} accepted and flagged per transaction-type policy.", Severity = "Low", CreatedAt = _clock.UtcNow
                });
            }
        }
        // Physical-channel transactions must carry their location; geographic rules exclude them otherwise.
        var physical = RiskScoringEngine.Split(await _settings.SettingAsync("Ingestion:PhysicalChannels", "ATM,Agent,POS,Branch", rec.TenantId, ct));
        if (string.IsNullOrWhiteSpace(t.SourceCountry) && physical.Contains(t.Channel ?? "", StringComparer.OrdinalIgnoreCase))
        {
            dq.Add("MissingLocation");
            _db.Set<DataQualityIssue>().Add(new DataQualityIssue
            {
                TenantId = rec.TenantId, IssueType = "MissingLocation", EntityType = "Transaction", EntityId = m.SourceTransactionId,
                Description = $"{t.Channel} transaction without source country; excluded from country-based monitoring until corrected.", Severity = "Medium", CreatedAt = _clock.UtcNow
            });
        }
        // Reported balances must reconcile with the movement: sender after = before − amount − fee; receiver after = before + amount.
        var mismatches = new List<string>();
        if (t.SenderBalanceBefore is { } sb && t.SenderBalanceAfter is { } sa && Math.Abs(sb - t.Amount - (t.Fee ?? 0) - sa) > 0.01m)
            mismatches.Add($"sender {sb} − {t.Amount} − fee {t.Fee ?? 0} ≠ {sa}");
        if (t.ReceiverBalanceBefore is { } rb && t.ReceiverBalanceAfter is { } ra && Math.Abs(rb + t.Amount - ra) > 0.01m)
            mismatches.Add($"receiver {rb} + {t.Amount} ≠ {ra}");
        if (mismatches.Count > 0 && normalized == TxnStatuses.Completed)
        {
            dq.Add("BalanceMismatch");
            _db.Set<DataQualityIssue>().Add(new DataQualityIssue
            {
                TenantId = rec.TenantId, IssueType = "BalanceMismatch", EntityType = "Transaction", EntityId = m.SourceTransactionId,
                Description = "Reported balances do not reconcile with the transaction: " + string.Join("; ", mismatches) + ".", Severity = "Medium", CreatedAt = _clock.UtcNow
            });
        }
        if (normalized == "Unknown")
        {
            dq.Add("UnknownStatus");
            t.Status = TxnStatuses.Pending;
        }
        t.DataQualityStatus = dq.Count == 0 ? "OK" : string.Join(",", dq);

        if (!string.IsNullOrWhiteSpace(m.AgentCustomerExternalId))
            t.AgentCustomerId = await _db.Set<Customer>().Where(c => c.SourceSystem == source && c.ExternalId == m.AgentCustomerExternalId).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(m.MerchantCustomerExternalId))
            t.MerchantCustomerId = await _db.Set<Customer>().Where(c => c.SourceSystem == source && c.ExternalId == m.MerchantCustomerExternalId).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);

        var decision = await _db.Set<RealtimeDecision>().FirstOrDefaultAsync(d => d.SourceSystem == source && d.SourceTransactionId == t.SourceTransactionId, ct);
        if (decision is not null) t.RealtimeDecisionId = decision.DecisionId;

        _db.Set<Transaction>().Add(t);
        await _db.SaveChangesAsync(ct);
        if (decision is not null) decision.TransactionId = t.Id;

        _db.Set<TransactionStatusHistory>().Add(new TransactionStatusHistory
        {
            TenantId = t.TenantId, TransactionId = t.Id, SourceStatus = t.SourceStatus, NormalizedStatus = t.Status, SourceEventAt = eventUtc, ReceivedAt = _clock.UtcNow, Applied = true
        });
        await ResolvePartiesAsync(t, ct);
        await LinkReversalsAsync(t, ct);
        await CheckPaymentTransparencyAsync(t, ct);
        _db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(), TenantId = t.TenantId, EventType = "TransactionIngested", CorrelationId = _corr.CorrelationId, CreatedAt = _clock.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { t.Id, t.SourceSystem, t.SourceTransactionId, t.Status, t.SenderCustomerId, t.ReceiverCustomerId })
        });
        await _db.SaveChangesAsync(ct);
        await RefreshAggregatesAsync(t, ct);
        if (isReplay) _audit.Record("INGESTION_REPLAY_PROCESSED", "IngestionRecord", recordId.ToString(CultureInfo.InvariantCulture), null, new { t.Id, t.SourceTransactionId });
        await _db.SaveChangesAsync(ct);
        await tx.CommitIfOwnedAsync(ct);
        return ("Processed", t.Id, null);
    }

    private async Task<(string, long?, string?)> DeadLetterAsync(IngestionRecord rec, string reason, CancellationToken ct)
    {
        _db.Set<OperationalAlert>().Add(new OperationalAlert
        {
            TenantId = rec.TenantId, Category = "IngestionDeadLetter", Message = $"Record {rec.Id} from {rec.SourceSystem} dead-lettered: {reason}",
            Reference = rec.Id.ToString(CultureInfo.InvariantCulture), Severity = "Warning", CreatedAt = _clock.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return ("DeadLetter", null, reason);
    }

    public async Task<string> NormalizeStatusAsync(string source, string sourceStatus, long tenantId, CancellationToken ct)
    {
        var maps = await _db.Set<TransactionStatusMapping>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.SourceSystem == source || x.SourceSystem == "*") && x.SourceStatus == sourceStatus).ToListAsync(ct);
        return maps.OrderByDescending(x => x.SourceSystem != "*").Select(x => x.NormalizedStatus).FirstOrDefault()
               ?? (new[] { "Pending", "Completed", "Failed", "Reversed", "PartiallyReversed" }.FirstOrDefault(s => s.Equals(sourceStatus, StringComparison.OrdinalIgnoreCase)) ?? "Unknown");
    }

    /// <summary>Version of the party-resolution rule set (stored with every resolution for traceability).</summary>
    public const string PartyResolutionRuleVersion = "PR-1";

    private async Task ResolvePartiesAsync(Transaction t, CancellationToken ct)
    {
        var at = t.TimestampUtc;
        var mappings = await _db.Set<WalletMapping>().AsNoTracking()
            .Where(w => w.SourceSystem == t.SourceSystem && w.IsActive && w.EffectiveFrom <= at && (w.EffectiveTo == null || w.EffectiveTo > at) &&
                        (w.ExternalWalletId == t.SenderWalletExternalId || w.ExternalWalletId == t.ReceiverWalletExternalId))
            .ToListAsync(ct);
        foreach (var side in new[] { "Sender", "Receiver" })
        {
            var ext = side == "Sender" ? t.SenderWalletExternalId : t.ReceiverWalletExternalId;
            var res = new TransactionPartyResolution { TenantId = t.TenantId, TransactionId = t.Id, Side = side, OriginalWalletExternalId = ext, ResolvedAt = _clock.UtcNow, RuleVersion = PartyResolutionRuleVersion };
            var mapping = mappings.FirstOrDefault(w => w.ExternalWalletId == ext);
            if (ext is null)
            {
                res.Method = "None"; res.Status = "Unresolved"; res.Evidence = "No wallet provided";
            }
            else if (mapping is not null)
            {
                res.Method = "InternalWalletCounterLeg";
                res.RuleVersion = $"{PartyResolutionRuleVersion}/WM-{mapping.Id}v{mapping.RuleVersion}";
                // Find the counter leg through the internal wallet by shared reference.
                var counter = t.OriginalReference is null ? null : await _db.Set<Transaction>().Where(o => o.Id != t.Id && o.SourceSystem == t.SourceSystem &&
                    o.OriginalReference == t.OriginalReference && (side == "Receiver" ? o.SenderWalletExternalId == ext : o.ReceiverWalletExternalId == ext)).FirstOrDefaultAsync(ct);
                if (counter is not null)
                {
                    var economic = side == "Receiver" ? await OwnerAsync(counter.ReceiverWalletExternalId, t.SourceSystem, ct) : await OwnerAsync(counter.SenderWalletExternalId, t.SourceSystem, ct);
                    res.ResolvedCustomerId = economic;
                    res.Status = economic is null ? "Unresolved" : "Resolved";
                    res.Confidence = economic is null ? null : 0.95m;
                    res.Evidence = $"{mapping.MappingCategory} wallet {ext} is not an economic party; counter leg {counter.SourceTransactionId} via reference {t.OriginalReference}";
                    if (side == "Receiver") t.ReceiverCustomerId = economic; else t.SenderCustomerId = economic;
                    // Both legs now describe the same economic transfer; monitor it once (through the leg that arrived first).
                    t.EconomicLegOfId ??= counter.Id;
                    // Complete the counter leg's resolution as well.
                    var counterSide = side == "Receiver" ? "Sender" : "Receiver";
                    var mine = side == "Receiver" ? await OwnerAsync(t.SenderWalletExternalId, t.SourceSystem, ct) : await OwnerAsync(t.ReceiverWalletExternalId, t.SourceSystem, ct);
                    var counterRes = await _db.Set<TransactionPartyResolution>().FirstOrDefaultAsync(r => r.TransactionId == counter.Id && r.Side == counterSide, ct);
                    if (counterRes is not null && counterRes.Status != "Resolved" && mine is not null)
                    {
                        counterRes.ResolvedCustomerId = mine;
                        counterRes.Status = "Resolved";
                        counterRes.Confidence = 0.95m;
                        counterRes.Evidence = $"{mapping.MappingCategory} wallet {ext} counter leg {t.SourceTransactionId} via reference {t.OriginalReference}";
                        if (counterSide == "Sender") counter.SenderCustomerId = mine; else counter.ReceiverCustomerId = mine;
                        await CloseUnresolvedIssueAsync(counter, ct);
                    }
                }
                else
                {
                    res.Status = "Unresolved";
                    res.Evidence = $"{mapping.MappingCategory} wallet {ext} is an internal wallet; counter leg not yet received (reference {t.OriginalReference ?? "none"})";
                }
            }
            else
            {
                var owner = await OwnerAsync(ext, t.SourceSystem, ct);
                res.Method = "WalletOwner";
                res.ResolvedCustomerId = owner;
                res.Status = owner is null ? "Unresolved" : "Resolved";
                res.Confidence = owner is null ? null : 1.0m;
                res.Evidence = owner is null ? $"Wallet {ext} unknown or has no owner" : $"Wallet {ext} owned by customer {owner}";
                if (side == "Sender") t.SenderCustomerId = owner; else t.ReceiverCustomerId = owner;
            }
            _db.Set<TransactionPartyResolution>().Add(res);
            if (res.Status == "Unresolved")
            {
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = t.TenantId, IssueType = "UnresolvedParty", EntityType = "Transaction", EntityId = t.Id.ToString(CultureInfo.InvariantCulture),
                    Description = $"{side} party unresolved for {t.SourceSystem}/{t.SourceTransactionId}: {res.Evidence}", Severity = "Medium", CreatedAt = _clock.UtcNow
                });
            }
        }
        t.SenderWalletId = await _db.Set<Wallet>().Where(w => w.SourceSystem == t.SourceSystem && w.ExternalWalletId == t.SenderWalletExternalId).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct);
        t.ReceiverWalletId = await _db.Set<Wallet>().Where(w => w.SourceSystem == t.SourceSystem && w.ExternalWalletId == t.ReceiverWalletExternalId).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task CloseUnresolvedIssueAsync(Transaction t, CancellationToken ct)
    {
        var id = t.Id.ToString(CultureInfo.InvariantCulture);
        var issues = await _db.Set<DataQualityIssue>().Where(d => d.IssueType == "UnresolvedParty" && d.EntityId == id && d.Status == "Open").ToListAsync(ct);
        foreach (var i in issues) { i.Status = "Resolved"; i.ResolvedAt = _clock.UtcNow; i.ResolvedBy = "system:counter-leg"; }
    }

    private async Task<long?> OwnerAsync(string? walletExt, string source, CancellationToken ct) =>
        walletExt is null ? null : await _db.Set<Wallet>().Where(w => w.SourceSystem == source && w.ExternalWalletId == walletExt).Select(w => w.CustomerId).FirstOrDefaultAsync(ct);

    private async Task LinkReversalsAsync(Transaction t, CancellationToken ct)
    {
        if ((t.IsReversal || t.IsRefund) && !string.IsNullOrWhiteSpace(t.OriginalSourceTransactionId))
        {
            var original = await _db.Set<Transaction>().FirstOrDefaultAsync(o => o.SourceSystem == t.SourceSystem && o.SourceTransactionId == t.OriginalSourceTransactionId, ct);
            if (original is null)
            {
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = t.TenantId, IssueType = "UnlinkedReversal", EntityType = "Transaction", EntityId = t.Id.ToString(CultureInfo.InvariantCulture),
                    Description = $"Reversal/refund {t.SourceTransactionId} references {t.OriginalSourceTransactionId} which has not arrived yet; will link on arrival.",
                    Severity = "Medium", CreatedAt = _clock.UtcNow
                });
            }
            else
            {
                ApplyReversal(original, t);
                await NoteReversalOnAlertsAsync(original, t, ct);
            }
        }
        // A reversal may have arrived before this original transaction.
        var pending = await _db.Set<Transaction>().Where(r => r.SourceSystem == t.SourceSystem && r.OriginalSourceTransactionId == t.SourceTransactionId && r.OriginalTransactionId == null && r.Id != t.Id).ToListAsync(ct);
        foreach (var r in pending)
        {
            ApplyReversal(t, r);
            await NoteReversalOnAlertsAsync(t, r, ct);
            var rid = r.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var i in await _db.Set<DataQualityIssue>().Where(d => d.IssueType == "UnlinkedReversal" && d.EntityId == rid && d.Status == "Open").ToListAsync(ct))
            {
                i.Status = "Resolved"; i.ResolvedAt = _clock.UtcNow; i.ResolvedBy = "system:late-original";
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A reversal/refund of a transaction already linked to an alert is recorded on the alert's history. The alert keeps its
    /// original evidence and status (no automatic closure); the investigator decides.
    /// </summary>
    private async Task NoteReversalOnAlertsAsync(Transaction original, Transaction reversal, CancellationToken ct)
    {
        var alerts = await (from x in _db.Set<AmlAlertTransaction>() join a in _db.Set<AmlAlert>() on x.AlertId equals a.Id
                            where x.TransactionId == original.Id select new { a.Id, a.Status }).ToListAsync(ct);
        foreach (var a in alerts)
            _db.Set<AlertStatusHistory>().Add(new AlertStatusHistory
            {
                TenantId = original.TenantId, AlertId = a.Id, FromStatus = a.Status, ToStatus = a.Status, Actor = "system", At = _clock.UtcNow, Reason = "LinkedTransactionReversed",
                Notes = $"Linked transaction {original.SourceTransactionId} {(reversal.IsRefund ? "refunded" : "reversed")} by {reversal.SourceTransactionId} " +
                        $"({reversal.Amount.ToString(CultureInfo.InvariantCulture)} {reversal.Currency}) on {reversal.TimestampUtc:yyyy-MM-dd HH:mm}Z; evidence unchanged, review whether the alert still stands."
            });
    }

    private static void ApplyReversal(Transaction original, Transaction reversal)
    {
        reversal.OriginalTransactionId = original.Id;
        if (reversal.Status != TxnStatuses.Completed) return;
        original.ReversedAmount += reversal.Amount;
        original.Status = original.ReversedAmount >= original.Amount ? TxnStatuses.Reversed : TxnStatuses.PartiallyReversed;
    }

    private async Task CheckPaymentTransparencyAsync(Transaction t, CancellationToken ct)
    {
        var rules = await _db.Set<PaymentTransparencyRule>().AsNoTracking().ToListAsync(ct);
        if (rules.Count == 0) return;
        var rates = await _fx.RatesAsync(t.TenantId, ct);
        var outcome = PaymentTransparencyValidator.Check(t, rules,
            (amount, cur, target) => FxConverter.Convert(amount, cur, target, t.TimestampUtc, rates).BaseAmount, t.TimestampUtc);
        if (!outcome.Applicable) { t.PaymentTransparencyStatus = "NotApplicable"; return; }
        t.PaymentTransparencyStatus = outcome.IsComplete ? "Complete" : "Incomplete";
        if (outcome.IsComplete) return;
        var issues = outcome.Missing.Select(f => $"missing {f}").Concat(outcome.Meaningless.Select(f => $"meaningless {f}")).ToList();
        t.PaymentTransparencyIssues = string.Join("; ", issues);
        await _alerts.UpsertAsync(new AlertDraft
        {
            TenantId = t.TenantId, Source = "PaymentTransparency", AlertType = "R16Completeness", PrimaryCustomerId = t.SenderCustomerId,
            SubjectKey = $"txn:{t.Id}", Severity = "Medium", Title = $"Payment transparency: incomplete originator/beneficiary data ({outcome.Corridor})",
            Description = $"Rule {outcome.RuleCode}: {t.PaymentTransparencyIssues}", DeduplicationKey = $"R16:{t.SourceSystem}:{t.SourceTransactionId}",
            EvidenceJson = JsonSerializer.Serialize(new { outcome.RuleCode, outcome.Corridor, outcome.Missing, outcome.Meaningless, outcome.ComparedAmount, t.Amount, t.Currency }),
            MatchKey = $"txn:{t.Id}", TransactionIds = new[] { t.Id }, WindowStart = t.TimestampUtc, WindowEnd = t.TimestampUtc, IsTestData = t.IsTestData
        }, ct);
    }

    /// <summary>Incrementally recomputes daily aggregates only for the affected customer/business-date pairs (no full scans).</summary>
    public async Task RefreshAggregatesAsync(Transaction t, CancellationToken ct)
    {
        var keys = new List<(long Customer, string Direction)>();
        if (t.SenderCustomerId is { } s) keys.Add((s, "Out"));
        if (t.ReceiverCustomerId is { } r) keys.Add((r, "In"));
        var affected = new List<Transaction> { t };
        if (t.OriginalTransactionId is { } oid)
        {
            var original = await _db.Set<Transaction>().FirstOrDefaultAsync(x => x.Id == oid, ct);
            if (original is not null)
            {
                affected.Add(original);
                if (original.SenderCustomerId is { } os) keys.Add((os, "Out"));
                if (original.ReceiverCustomerId is { } or) keys.Add((or, "In"));
            }
        }
        foreach (var date in affected.Select(a => a.BusinessDate).Distinct())
        foreach (var (customer, direction) in keys.Distinct())
        {
            var q = _db.Set<Transaction>().Where(x => x.BusinessDate == date && !x.IsReversal && x.EconomicLegOfId == null && (x.Status == TxnStatuses.Completed || x.Status == TxnStatuses.PartiallyReversed || x.Status == TxnStatuses.Reversed));
            q = direction == "Out" ? q.Where(x => x.SenderCustomerId == customer) : q.Where(x => x.ReceiverCustomerId == customer);
            var stats = await q.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(x => x.Amount == 0 ? 0 : (x.BaseAmount ?? 0) * (x.Amount - x.ReversedAmount) / x.Amount) }).FirstOrDefaultAsync(ct);
            var agg = await _db.Set<CustomerDailyAggregate>().FirstOrDefaultAsync(a => a.CustomerId == customer && a.BusinessDate == date && a.Direction == direction, ct);
            if (agg is null) _db.Set<CustomerDailyAggregate>().Add(agg = new CustomerDailyAggregate { TenantId = t.TenantId, CustomerId = customer, BusinessDate = date, Direction = direction });
            agg.TxnCount = stats?.Count ?? 0;
            agg.BaseAmount = Math.Round(stats?.Sum ?? 0, 4);
            agg.UpdatedAt = _clock.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Manual replay of a dead-lettered/failed record (optionally with a corrected payload). Processed exactly once; audited.</summary>
    public async Task<IngestionRecordResult> ReplayAsync(long recordId, JsonElement? correctedPayload, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Common.Perm.OperationsReplay);
        var rec = await _db.Set<IngestionRecord>().FirstOrDefaultAsync(r => r.Id == recordId, ct) ?? throw new NotFoundException("Ingestion record", recordId);
        if (rec.Status is not ("DeadLetter" or "Retrying" or "Failed"))
            throw new ConflictException($"Record {recordId} is {rec.Status}; only dead-lettered or failed records can be replayed.");
        var raw = correctedPayload?.GetRawText() ?? (await _db.Set<RawTransactionPayload>().AsNoTracking().FirstAsync(p => p.IngestionRecordId == recordId, ct)).Payload;
        if (correctedPayload is not null)
        {
            rec.PayloadHash = Sha256(raw);
            if (correctedPayload.Value.TryGetProperty("sourceTransactionId", out var sid)) rec.SourceTransactionId = sid.GetString();
            _db.Set<RawTransactionPayload>().Add(new RawTransactionPayload { TenantId = rec.TenantId, IngestionRecordId = rec.Id, Payload = raw, StoredAt = _clock.UtcNow });
        }
        rec.ReplayedBy = _user.Username;
        rec.ReplayedAt = _clock.UtcNow;
        _audit.Record("INGESTION_REPLAY", "IngestionRecord", recordId.ToString(CultureInfo.InvariantCulture), new { rec.Status, rec.LastError }, new { corrected = correctedPayload is not null }, reason);
        await _db.SaveChangesAsync(ct);
        var result = await ProcessRecordAsync(recordId, raw, ct, isReplay: true);
        if (result.Status == "Processed")
        {
            var r2 = await _db.Set<IngestionRecord>().FirstAsync(r => r.Id == recordId, ct);
            r2.Status = "Replayed";
            await _db.SaveChangesAsync(ct);
            return result with { Status = "Replayed" };
        }
        return result;
    }

    /// <summary>Retries records queued after transient failures (bounded; permanent failures dead-lettered).</summary>
    public async Task<int> RetryQueuedAsync(long tenantId, CancellationToken ct)
    {
        var maxQueueAttempts = await _settings.IntSettingAsync("Ingestion:MaxQueueAttempts", 5, tenantId, ct);
        var due = await _db.Set<RetryQueueItem>().IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.Kind == "Ingestion" && r.Status == "Pending" && r.NextAttemptAt <= _clock.UtcNow).Take(100).ToListAsync(ct);
        foreach (var item in due)
        {
            var id = long.Parse(item.ReferenceId, CultureInfo.InvariantCulture);
            var raw = (await _db.Set<RawTransactionPayload>().IgnoreQueryFilters().AsNoTracking().Where(p => p.IngestionRecordId == id).OrderByDescending(p => p.Id).FirstAsync(ct)).Payload;
            var res = await ProcessRecordAsync(id, raw, ct);
            var it = await _db.Set<RetryQueueItem>().IgnoreQueryFilters().FirstAsync(x => x.Id == item.Id, ct);
            it.Attempts++;
            if (res.Status != "Retrying") { it.Status = "Completed"; it.CompletedAt = _clock.UtcNow; }
            else if (it.Attempts >= maxQueueAttempts)
            {
                it.Status = "Failed";
                var rec = await _db.Set<IngestionRecord>().IgnoreQueryFilters().FirstAsync(r => r.Id == id, ct);
                rec.Status = "DeadLetter";
                rec.LastError = $"Permanent failure after {it.Attempts} queued attempts: {res.Error}";
                _db.Set<OperationalAlert>().Add(new OperationalAlert { TenantId = tenantId, Category = "IngestionRetryExhausted", Message = rec.LastError, Reference = id.ToString(CultureInfo.InvariantCulture), Severity = "Error", CreatedAt = _clock.UtcNow });
            }
            else it.NextAttemptAt = _clock.UtcNow.AddSeconds(30 * Math.Pow(2, it.Attempts));
            await _db.SaveChangesAsync(ct);
        }
        return due.Count;
    }

    /// <summary>Reconciles source control totals against imported records per source and business day.</summary>
    public async Task<List<ReconciliationResult>> ReconcileAsync(string? source, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new ValidationException("to", "to must be on or after from.");
        if (to.DayNumber - from.DayNumber > 92) throw new ValidationException("to", "Maximum range is 92 days.");
        var sources = await _db.Set<SourceSystem>().AsNoTracking().Where(s => s.IsActive && (source == null || s.Code == source)).ToListAsync(ct);
        var results = new List<ReconciliationResult>();
        foreach (var s in sources)
        {
            var controls = await _db.Set<SourceControlTotal>().AsNoTracking().Where(c => c.SourceSystem == s.Code && c.BusinessDate >= from && c.BusinessDate <= to).ToListAsync(ct);
            var imported = await _db.Set<Transaction>().AsNoTracking().Where(t => t.SourceSystem == s.Code && t.BusinessDate >= from && t.BusinessDate <= to && !t.IsTestData)
                .GroupBy(t => new { t.BusinessDate, t.Currency }).Select(g => new { g.Key.BusinessDate, g.Key.Currency, Count = g.Count(), Amount = g.Sum(x => x.Amount) }).ToListAsync(ct);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var dayControls = controls.Where(c => c.BusinessDate == d).ToList();
                var dayImported = imported.Where(i => i.BusinessDate == d).ToList();
                if (dayControls.Count == 0)
                {
                    var count = dayImported.Sum(i => i.Count);
                    results.Add(new ReconciliationResult
                    {
                        TenantId = s.TenantId, SourceSystem = s.Code, BusinessDate = d, ImportedCount = count, ImportedAmount = dayImported.Sum(i => i.Amount),
                        Status = count == 0 && s.ExpectedFeed == "Daily" ? "Gap" : "NoControlTotals", RunAt = _clock.UtcNow
                    });
                    continue;
                }
                foreach (var c in dayControls)
                {
                    var imp = dayImported.Where(i => i.Currency == c.Currency).ToList();
                    var r = new ReconciliationResult
                    {
                        TenantId = s.TenantId, SourceSystem = s.Code, BusinessDate = d, SourceCount = c.ReportedCount, SourceAmount = c.ReportedAmount,
                        ImportedCount = imp.Sum(i => i.Count), ImportedAmount = imp.Sum(i => i.Amount), RunAt = _clock.UtcNow, TransactionType = c.Currency
                    };
                    r.CountDifference = r.SourceCount - r.ImportedCount;
                    r.AmountDifference = r.SourceAmount - r.ImportedAmount;
                    r.Status = r.CountDifference == 0 && r.AmountDifference == 0 ? "Matched" : "Mismatch";
                    results.Add(r);
                }
            }
        }
        _db.Set<ReconciliationResult>().AddRange(results);
        foreach (var gap in results.Where(r => r.Status == "Gap"))
        {
            var key = $"{gap.SourceSystem}:{gap.BusinessDate:yyyy-MM-dd}";
            if (!await _db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "FeedGap" && d.EntityId == key, ct))
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue { TenantId = gap.TenantId, IssueType = "FeedGap", EntityType = "SourceSystem", EntityId = key, Description = $"No transactions received from {gap.SourceSystem} for {gap.BusinessDate:yyyy-MM-dd}", Severity = "High", CreatedAt = _clock.UtcNow });
        }
        _audit.Record("RECONCILIATION_RUN", "SourceSystem", source ?? "*", null, new { from, to, matched = results.Count(r => r.Status == "Matched"), mismatched = results.Count(r => r.Status == "Mismatch"), gaps = results.Count(r => r.Status == "Gap") });
        await _db.SaveChangesAsync(ct);
        return results;
    }

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string ContentHash(TransactionMessage m)
    {
        var core = JsonSerializer.Serialize(new
        {
            m.SourceTransactionId, m.OriginalReference, m.Type, m.Channel, m.Amount, Currency = m.Currency?.ToUpperInvariant(), m.Fee, m.SenderWallet, m.ReceiverWallet,
            m.OriginalSourceTransactionId, m.IsReversal, m.IsRefund, m.SourceCountry, m.DestinationCountry, OriginatorName = m.Originator?.Name, BeneficiaryName = m.Beneficiary?.Name, BeneficiaryAccount = m.Beneficiary?.Account, m.Remarks
        });
        return Sha256(core);
    }
}
