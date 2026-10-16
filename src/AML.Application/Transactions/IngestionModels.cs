using System.Text.Json;

namespace AML.Application.Transactions;

public sealed class IngestionBatchRequest
{
    public string? BatchId { get; set; }
    /// <summary>Raw transaction messages; each is validated and processed independently (one bad record never fails the batch).</summary>
    public List<JsonElement> Transactions { get; set; } = new();
    public List<ControlTotalInput>? ControlTotals { get; set; }
}

public sealed class ControlTotalInput
{
    public DateOnly BusinessDate { get; set; }
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
}

public sealed class PartyInfo
{
    public string? Name { get; set; }
    public string? Account { get; set; }
    public string? Address { get; set; }
    public string? NationalId { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? PlaceOfBirth { get; set; }
}

/// <summary>Canonical inbound transaction contract (source-system adapters map their formats to this).</summary>
public sealed class TransactionMessage
{
    public string? SourceTransactionId { get; set; }
    public string? OriginalReference { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public DateTime? EventTime { get; set; }
    public string? Channel { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public decimal? Fee { get; set; }
    public string? SenderWallet { get; set; }
    public string? ReceiverWallet { get; set; }
    public string? OriginalSourceTransactionId { get; set; }
    public bool IsReversal { get; set; }
    public bool IsRefund { get; set; }
    public string? SourceCountry { get; set; }
    public string? DestinationCountry { get; set; }
    public string? AgentCustomerExternalId { get; set; }
    public string? MerchantCustomerExternalId { get; set; }
    public PartyInfo? Originator { get; set; }
    public PartyInfo? Beneficiary { get; set; }
    public string? IntermediaryInstitution { get; set; }
    public string? OrderingInstitution { get; set; }
    public string? Remarks { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal? InvoiceAmount { get; set; }
    public string? InvoiceCurrency { get; set; }
    public string? GoodsCategory { get; set; }
    public string? CopResult { get; set; }
    public string? DeviceId { get; set; }
    public string? IpAddress { get; set; }
    public bool IsTestData { get; set; }

    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

public sealed record IngestionRecordResult(string? SourceTransactionId, string Status, long? TransactionId, string? Error, long RecordId);

public sealed record IngestionBatchResult(long RunId, string BatchId, string Status, int Received, int Processed, int Duplicates, int Conflicts, int Errors,
    string CorrelationId, IReadOnlyList<IngestionRecordResult> Records);

/// <summary>Transient infrastructure failure (retried with backoff).</summary>
public sealed class TransientFaultException : Exception
{
    public TransientFaultException(string message) : base(message) { }
}

public interface ITransientErrorDetector
{
    bool IsTransient(Exception ex);
}

public sealed class DefaultTransientErrorDetector : ITransientErrorDetector
{
    public bool IsTransient(Exception ex) => ex is TransientFaultException or TimeoutException || ex.InnerException is TransientFaultException or TimeoutException;
}
