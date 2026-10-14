using System.Linq.Expressions;
using AML.Application.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Transactions;

public sealed class TransactionListItem
{
    public long Id { get; init; }
    public string SourceSystem { get; init; }
    public string SourceTransactionId { get; init; }
    public string? OriginalReference { get; init; }
    public string TransactionType { get; init; }
    public string Status { get; init; }
    public string SourceStatus { get; init; }
    public string? Channel { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; }
    public decimal? BaseAmount { get; init; }
    public string? BaseCurrency { get; init; }
    public string FxStatus { get; init; }
    public DateTime TimestampUtc { get; init; }
    public DateOnly BusinessDate { get; init; }
    public string? SenderWalletExternalId { get; init; }
    public string? ReceiverWalletExternalId { get; init; }
    public long? SenderCustomerId { get; init; }
    public long? ReceiverCustomerId { get; init; }
    public bool IsReversal { get; init; }
    public decimal ReversedAmount { get; init; }
    public string DataQualityStatus { get; init; }
    public string? SourceCountry { get; init; }
    public string? DestinationCountry { get; init; }
}

public sealed class TransactionQuery
{
    /// <summary>Inclusive lower bound (UTC).</summary>
    public DateTime? From { get; set; }
    /// <summary>Exclusive upper bound (UTC).</summary>
    public DateTime? To { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Currency { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Channel { get; set; }
    public long? SenderCustomerId { get; set; }
    public long? ReceiverCustomerId { get; set; }
    public long? CustomerId { get; set; }
    public string? Wallet { get; set; }
    public string? Reference { get; set; }
    public string? SourceSystem { get; set; }
    public long? AlertId { get; set; }
    public long? CaseId { get; set; }
    public string? DataQuality { get; set; }
}

public sealed class TransactionQueryService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly AccessPolicy _access;
    private readonly IAuditService _audit;

    public TransactionQueryService(IAmlDbContext db, ICurrentUser user, AccessPolicy access, IAuditService audit)
    {
        _db = db; _user = user; _access = access; _audit = audit;
    }

    private static readonly Dictionary<string, Expression<Func<TransactionListItem, object?>>> Sorts = new()
    {
        ["timestamp"] = t => t.TimestampUtc, ["amount"] = t => t.Amount, ["baseAmount"] = t => t.BaseAmount, ["type"] = t => t.TransactionType,
        ["status"] = t => t.Status, ["id"] = t => t.Id, ["reference"] = t => t.SourceTransactionId
    };

    private IQueryable<Transaction> Visible()
    {
        var q = _db.Set<Transaction>().AsQueryable();
        if (!string.IsNullOrEmpty(_user.DataScopeRegion))
        {
            var ids = _access.Customers().Select(c => c.Id);
            q = q.Where(t => (t.SenderCustomerId != null && ids.Contains(t.SenderCustomerId.Value)) || (t.ReceiverCustomerId != null && ids.Contains(t.ReceiverCustomerId.Value)));
        }
        return q;
    }

    public async Task<PagedResult<TransactionListItem>> ListAsync(TransactionQuery f, PageRequest page, CancellationToken ct)
    {
        if (f.From is { } a && f.To is { } b && b < a) throw new ValidationException("to", "to must be after from.");
        var q = Visible().AsNoTracking();
        if (f.From is { } from) q = q.Where(t => t.TimestampUtc >= from);
        if (f.To is { } to) q = q.Where(t => t.TimestampUtc < to);
        if (f.MinAmount is { } min) q = q.Where(t => t.Amount >= min);
        if (f.MaxAmount is { } max) q = q.Where(t => t.Amount <= max);
        if (!string.IsNullOrWhiteSpace(f.Currency)) q = q.Where(t => t.Currency == f.Currency);
        if (!string.IsNullOrWhiteSpace(f.Type)) q = q.Where(t => t.TransactionType == f.Type);
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(t => t.Status == f.Status);
        if (!string.IsNullOrWhiteSpace(f.Channel)) q = q.Where(t => t.Channel == f.Channel);
        if (f.SenderCustomerId is { } s) q = q.Where(t => t.SenderCustomerId == s);
        if (f.ReceiverCustomerId is { } r) q = q.Where(t => t.ReceiverCustomerId == r);
        if (f.CustomerId is { } c) q = q.Where(t => t.SenderCustomerId == c || t.ReceiverCustomerId == c || t.AgentCustomerId == c);
        if (!string.IsNullOrWhiteSpace(f.Wallet)) q = q.Where(t => t.SenderWalletExternalId == f.Wallet || t.ReceiverWalletExternalId == f.Wallet);
        if (!string.IsNullOrWhiteSpace(f.Reference)) q = q.Where(t => t.SourceTransactionId == f.Reference || t.OriginalReference == f.Reference);
        if (!string.IsNullOrWhiteSpace(f.SourceSystem)) q = q.Where(t => t.SourceSystem == f.SourceSystem);
        if (!string.IsNullOrWhiteSpace(f.DataQuality)) q = q.Where(t => t.DataQualityStatus.Contains(f.DataQuality));
        if (f.AlertId is { } alertId)
        {
            var visible = _access.Alerts().Where(x => x.Id == alertId).Select(x => x.Id);
            var ids = _db.Set<AmlAlertTransaction>().Where(x => visible.Contains(x.AlertId)).Select(x => x.TransactionId);
            q = q.Where(t => ids.Contains(t.Id));
        }
        if (f.CaseId is { } caseId)
        {
            var visible = _access.Cases().Where(x => x.Id == caseId).Select(x => x.Id);
            var ids = _db.Set<AmlCaseTransaction>().Where(x => visible.Contains(x.CaseId)).Select(x => x.TransactionId);
            q = q.Where(t => ids.Contains(t.Id));
        }
        var rows = q.Select(t => new TransactionListItem { Id = t.Id, SourceSystem = t.SourceSystem, SourceTransactionId = t.SourceTransactionId, OriginalReference = t.OriginalReference, TransactionType = t.TransactionType, Status = t.Status, SourceStatus = t.SourceStatus, Channel = t.Channel, Amount = t.Amount, Currency = t.Currency, BaseAmount = t.BaseAmount, BaseCurrency = t.BaseCurrency, FxStatus = t.FxStatus, TimestampUtc = t.TimestampUtc, BusinessDate = t.BusinessDate, SenderWalletExternalId = t.SenderWalletExternalId, ReceiverWalletExternalId = t.ReceiverWalletExternalId, SenderCustomerId = t.SenderCustomerId, ReceiverCustomerId = t.ReceiverCustomerId, IsReversal = t.IsReversal, ReversedAmount = t.ReversedAmount, DataQualityStatus = t.DataQualityStatus, SourceCountry = t.SourceCountry, DestinationCountry = t.DestinationCountry });
        return await rows.SortBy(page, Sorts, "timestamp", defaultDesc: true).ToPagedAsync(page, ct);
    }

    public async Task<object> GetAsync(long id, CancellationToken ct)
    {
        var t = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Transaction", id);
        var resolutions = await _db.Set<TransactionPartyResolution>().AsNoTracking().Where(r => r.TransactionId == id).ToListAsync(ct);
        var history = await _db.Set<TransactionStatusHistory>().AsNoTracking().Where(h => h.TransactionId == id).OrderBy(h => h.Id).ToListAsync(ct);
        var alertIds = _db.Set<AmlAlertTransaction>().Where(x => x.TransactionId == id).Select(x => x.AlertId);
        var alerts = await _access.Alerts().AsNoTracking().Where(a => alertIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AlertNumber, a.Title, a.Severity, a.Status, a.RuleCode, a.RuleVersion }).ToListAsync(ct);
        var visibleAlertIds = alerts.Select(a => a.Id).ToList();
        var matches = await _db.Set<AmlAlertRuleMatch>().AsNoTracking().Where(m => visibleAlertIds.Contains(m.AlertId))
            .Select(m => new { m.AlertId, m.RuleVersion, m.MatchKey, m.MatchedConditionsJson, m.WindowStart, m.WindowEnd, m.MatchedAt }).ToListAsync(ct);
        var caseIds = _db.Set<AmlCaseTransaction>().Where(x => x.TransactionId == id).Select(x => x.CaseId);
        var cases = await _access.Cases().AsNoTracking().Where(c => caseIds.Contains(c.Id)).Select(c => new { c.Id, c.CaseNumber, c.Title, c.Status }).ToListAsync(ct);
        var customerIds = new[] { t.SenderCustomerId, t.ReceiverCustomerId, t.AgentCustomerId }.Where(x => x != null).Select(x => x!.Value).ToList();
        var customers = await _access.Customers().AsNoTracking().Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.NameLatin, c.NameArabic, c.CustomerType, c.ExternalId }).ToListAsync(ct);
        var riskAtTime = new List<object>();
        foreach (var cid in customerIds.Distinct())
        {
            var hist = await _db.Set<CustomerRiskAssessment>().AsNoTracking().Where(a => a.CustomerId == cid && a.CalculatedAt <= t.TimestampUtc && a.Status != "Rejected" && a.Status != "PendingApproval")
                .OrderByDescending(a => a.CalculatedAt).Select(a => new { a.Id, a.FinalLevel, a.CalculatedScore, a.CalculatedAt, a.MethodologyCode, a.MethodologyVersion }).FirstOrDefaultAsync(ct);
            var current = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().Where(p => p.CustomerId == cid).Select(p => new { p.Level, p.Score, p.LastAssessedAt }).FirstOrDefaultAsync(ct);
            riskAtTime.Add(new { customerId = cid, atTransactionTime = hist, current });
        }
        var decision = t.RealtimeDecisionId is null ? null : await _db.Set<RealtimeDecision>().AsNoTracking().Where(d => d.DecisionId == t.RealtimeDecisionId)
            .Select(d => new { d.DecisionId, d.Decision, d.ReasonCodes, d.CreatedAt, d.IsFallback }).FirstOrDefaultAsync(ct);
        return new
        {
            transaction = new
            {
                t.Id, t.SourceSystem, t.SourceTransactionId, t.OriginalReference, t.PayloadHash, t.TransactionType, t.SourceStatus, t.Status, t.Channel, t.Amount, t.Currency,
                t.Fee, t.BaseAmount, t.BaseCurrency, t.FxRate, t.FxRateDate, t.FxRateVersion, t.FxStatus, t.ReversedAmount, NetAmount = t.Amount - t.ReversedAmount, t.BusinessDate,
                t.TimestampUtc, t.IngestedAt, t.IngestionRunId, t.DataQualityStatus, t.IsReversal, t.IsRefund, t.OriginalSourceTransactionId, t.OriginalTransactionId,
                t.SourceCountry, t.DestinationCountry, t.SenderWalletExternalId, t.ReceiverWalletExternalId, t.SenderCustomerId, t.ReceiverCustomerId, t.AgentCustomerId,
                t.MerchantCustomerId, t.Remarks, t.CopResult, DeviceId = _access.MaskDevice(t.DeviceId), IpAddress = _user.Has(Perm.DevicesUnmask) ? t.IpAddress : (t.IpAddress is null ? null : FieldValueValidator.Mask(t.IpAddress)),
                t.IsTestData, t.RealtimeDecisionId
            },
            paymentTransparency = new
            {
                t.PaymentTransparencyStatus, t.PaymentTransparencyIssues,
                originator = new { name = t.OriginatorName, account = t.OriginatorAccount, address = t.OriginatorAddress, nationalId = t.OriginatorNationalId is null ? null : FieldValueValidator.Mask(t.OriginatorNationalId), dateOfBirth = t.OriginatorDateOfBirth, placeOfBirth = t.OriginatorPlaceOfBirth },
                beneficiary = new { name = t.BeneficiaryName, account = t.BeneficiaryAccount },
                intermediaryInstitution = t.IntermediaryInstitution, orderingInstitution = t.OrderingInstitution, confirmationOfPayee = t.CopResult
            },
            originalParties = new { sender = t.SenderWalletExternalId, receiver = t.ReceiverWalletExternalId },
            resolvedParties = resolutions.Select(r => new { r.Side, r.OriginalWalletExternalId, r.ResolvedCustomerId, r.Method, r.Status, r.Evidence, r.Confidence, r.RuleVersion }),
            customers, statusHistory = history.Select(h => new { h.SourceStatus, h.NormalizedStatus, h.SourceEventAt, h.ReceivedAt, h.Applied, h.Note }),
            riskAtTime, alerts, ruleMatches = matches, cases, realtimeDecision = decision
        };
    }

    /// <summary>Related transactions and chain (original, reversals/refunds, same reference), ordered as a timeline.</summary>
    public async Task<object> RelatedAsync(long id, CancellationToken ct)
    {
        var t = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Transaction", id);
        var ids = new HashSet<long> { t.Id };
        var frontier = new Queue<Transaction>();
        frontier.Enqueue(t);
        var all = new Dictionary<long, Transaction> { [t.Id] = t };
        while (frontier.Count > 0 && all.Count < 50)
        {
            var cur = frontier.Dequeue();
            var related = await Visible().AsNoTracking().Where(x => x.Id != cur.Id && (
                (cur.OriginalReference != null && x.OriginalReference == cur.OriginalReference) ||
                x.OriginalTransactionId == cur.Id || (cur.OriginalTransactionId != null && x.Id == cur.OriginalTransactionId) ||
                (x.SourceSystem == cur.SourceSystem && x.OriginalSourceTransactionId == cur.SourceTransactionId) ||
                (cur.OriginalSourceTransactionId != null && x.SourceSystem == cur.SourceSystem && x.SourceTransactionId == cur.OriginalSourceTransactionId))).Take(50).ToListAsync(ct);
            foreach (var r in related.Where(r => all.TryAdd(r.Id, r))) frontier.Enqueue(r);
        }
        var chain = all.Values.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).Select((x, i) => new
        {
            step = i + 1, x.Id, x.SourceTransactionId, x.OriginalReference, x.TransactionType, x.Status, x.Amount, x.Currency, x.TimestampUtc, x.IsReversal, x.IsRefund,
            x.OriginalTransactionId, x.SenderWalletExternalId, x.ReceiverWalletExternalId, x.SenderCustomerId, x.ReceiverCustomerId,
            relation = x.Id == t.Id ? "Self" : x.Id == t.OriginalTransactionId ? "Original" : x.OriginalTransactionId == t.Id ? (x.IsRefund ? "Refund" : "Reversal") : "SameReference"
        }).ToList();
        return new { transactionId = id, count = chain.Count, chain };
    }

    public async Task<object> RawPayloadAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.TransactionsRaw);
        var t = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Transaction", id);
        var recordIds = await _db.Set<IngestionRecord>().AsNoTracking().Where(r => r.TransactionId == id).Select(r => r.Id).ToListAsync(ct);
        var payloads = await _db.Set<RawTransactionPayload>().AsNoTracking().Where(p => recordIds.Contains(p.IngestionRecordId)).Select(p => new { p.IngestionRecordId, p.Payload, p.StoredAt }).ToListAsync(ct);
        _audit.Record("RAW_PAYLOAD_READ", "Transaction", id.ToString(), null, new { records = recordIds.Count });
        await _db.SaveChangesAsync(ct);
        return new { t.Id, t.SourceTransactionId, payloads };
    }
}
