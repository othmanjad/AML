using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

/// <summary>
/// Scenarios that cannot be configured with the rule DSL (or lack the required source data). For each, the closest
/// definition an analyst would write is submitted to the rule validator and the rejection is recorded as evidence; all
/// test cases of the scenario are reported Blocked ("SC:" lines are expanded to the scenario's TC IDs by the report tool).
/// </summary>
[Collection("tm")]
public class CapabilityGapTests : TmBase
{
    public CapabilityGapTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static object W(object condition, object? window = null) => new { window = window ?? new { type = "Rolling", days = 30 }, condition };
    private static object Agg(string aggregate, string op = "gte", object? value = null) => new { aggregate, @operator = op, value = value ?? 1 };

    public static IEnumerable<object[]> Gaps() => new (string Sc, string Reason, object Attempt)[]
    {
        ("VEL-06", "Customer registration date/time is not available to rules (no 'customer.registrationDate' field or 'hours since onboarding' aggregate).", W(And(F_("customer.registrationDate", "gte", "2025-01-01"), Cnt(null, "gte", 10)))),
        ("VEL-07", "No aggregate for regularity of time gaps (standard deviation of inter-transaction intervals).", W(Agg("intervalStdDev", "lte", 5))),
        ("VEL-08", "No distinct-channel count aggregate (distinctCounterparties counts counterparties only).", W(Agg("distinctChannels", "gte", 4))),
        ("AMT-05", "No historical-baseline aggregate (customer's own 90-day average with minimum history).", W(Agg("historicalAverageRatio", "gt", 5))),
        ("AMT-06", "peerRatio compares with the peer median of agent-processed volume only; no percentile, segment or minimum peer-group size.", W(new { aggregate = "peerPercentile", peerGroup = "typeSegmentRegion", @operator = "gte", value = 99 })),
        ("AMT-07", "No arithmetic operator for exact multiples (modulo) on amounts.", W(Cnt(F_("txn.amount", "multipleOf", 100), "gte", 5))),
        ("STR-06", "No consecutive-period pattern (N consecutive days each within a band).", W(Agg("consecutiveDays", "gte", 4))),
        ("RMV-02", "Wallet balances / end-of-day balances are not ingested or available to rules.", W(And(F_("wallet.endOfDayBalance", "lte", 10), Sum(null, "gte", 5000)))),
        ("RMV-04", "cycle detects funds returning to the subject only; no open multi-hop chain (A→B→C→D) with per-hop retention.", W(new { aggregate = "chain", maxDepth = 3, @operator = "gte", value = 1 })),
        ("FAN-03", "No first-time-counterparty share aggregate (counterparty history not compared).", W(Agg("newCounterpartyShare", "gte", 80))),
        ("FAN-05", "Rules cannot read counterparties' device/IP/onboarding-agent attributes.", W(Agg("sendersSharingAttribute", "gte", 5))),
        ("GEO-01", "No geolocation or distance/speed (impossible travel) computation; transactions carry country only.", W(Agg("travelSpeedKmh", "gt", 900))),
        ("GEO-02", "newCountries counts countries not seen in history; there is no distinct-country count within T hours for physical transactions.", W(Agg("distinctCountries", "gte", 3), new { type = "Rolling", hours = 24 })),
        ("GEO-06", "Login/session IP country is not available to rules (device signals are not exposed as rule fields).", W(F_("session.ipCountry", "neq", "JO"))),
        ("GEO-07", "No 'first cross-border after M months domestic-only' aggregate (newCountries ignores amount and months of domestic history).", W(Agg("firstCrossBorderAfterMonths", "gte", 6))),
        ("GEO-08", "Agent locations / distances are not modelled; cash-in and cash-out by different persons cannot be correlated.", W(Agg("agentDistanceKm", "gte", 100))),
        ("GEO-09", "No border-zone agent classification available to rules (agent attributes are not readable from customer-subject rules).", W(F_("agent.borderZone", "eq", true))),
        ("DEV-02", "Device first-seen/login events are not available to rules.", W(F_("device.isNew", "eq", true))),
        ("DEV-03", "IP anonymiser (VPN/proxy/TOR) classification is not captured or exposed to rules.", W(F_("session.ipAnonymiser", "eq", true))),
        ("DEV-04", "Rooted/jailbroken/emulator device flags are not captured or exposed to rules.", W(F_("device.rooted", "eq", true))),
        ("DEV-05", "SIM-change events are not ingested.", W(F_("event.simSwapHoursAgo", "lte", 48))),
        ("DEV-06", "No distinct-devices-per-customer aggregate (customersPerDevice is the inverse).", W(Agg("devicesPerCustomer", "gte", 4))),
        ("DEV-07", "Channel limit-hit events are not ingested; channel switch sequence cannot be expressed.", W(Agg("channelSwitchAfterLimit", "gte", 1))),
        ("BEH-02", "No transaction-type mix baseline comparison (share change vs 90-day baseline).", W(Agg("typeShareChange", "gte", 60))),
        ("BEH-05", "Customer date of birth / age is not available to rules.", W(F_("customer.age", "lt", 18))),
        ("BEH-07", "No salary-credit linkage or 'M consecutive months' pattern.", W(Agg("salaryRemittedShareConsecutiveMonths", "gte", 3))),
        ("BEH-08", "Contact-detail change history is not available to rules.", W(F_("customer.contactChangedDaysAgo", "lte", 3))),
        ("CSH-03", "ratio cannot divide outgoing cash by incoming electronic funds across directions (numerator and denominator share one direction filter) and has no minimum-incoming guard.", W(new { aggregate = "ratio", numerator = F_("txn.type", "eq", "CashOut"), denominator = F_("txn.direction", "eq", "in"), crossDirection = true, @operator = "gte", value = 90 })),
        ("AGT-01", "Rules cannot test links between the agent and the customers it serves (shared ID/phone/device/family).", W(Agg("agentLinkedCustomerTxns", "gte", 5))),
        ("AGT-02", "peerRatio uses agent volume, not the cash-out/cash-in ratio compared with a regional peer median × R.", W(new { aggregate = "peerRatioOfRatio", peerGroup = "region", @operator = "gt", value = 3 })),
        ("AGT-04", "No 'customers registered by agent who transact within 24 h' share aggregate.", W(Agg("agentNewCustomerActiveShare", "gte", 50))),
        ("AGT-06", "Agent location / customer residence distance is not modelled.", W(Agg("agentDistanceKm", "gte", 50))),
        ("MER-03", "Rules cannot read links between payers and the merchant owner.", W(Agg("ownerLinkedPayers", "gte", 3))),
        ("MER-04", "No sequence pattern 'payment followed by refund or merchant cash-out ≥ P%' scoped to the same merchant (rapidMovement measures any outflow after any inflow, without refund pairing).", W(Agg("paymentThenRefundShare", "gte", 90))),
        ("MER-05", "POS geolocation and merchant registered area are not modelled.", W(Agg("posDistanceKm", "gt", 20))),
        ("REV-02", "Refund destination instrument vs original payment instrument is not captured.", W(F_("txn.refundInstrumentMismatch", "eq", true))),
        ("REV-03", "No sequence pattern of failed attempts with decreasing amounts followed by a success.", W(Agg("failedDecreasingThenSuccess", "gte", 3))),
        ("REV-05", "Invoice amounts and refund-to-third-party linkage are not captured.", W(F_("txn.invoiceAmount", "lt", 0))),
        ("CIR-02", "No reciprocal back-and-forth count between the same two wallets.", W(Agg("reciprocalTransfers", "gte", 6))),
        ("CIR-03", "Entity-resolution links between customers are not usable in rules.", W(Agg("linkedPartyTransfers", "gte", 3000))),
        ("MUL-02", "Account age (registration date) is not available to rules.", W(And(F_("customer.accountAgeDays", "lte", 14), Sum(null, "gte", 2000, "in")))),
        ("MUL-03", "Counterparty fraud/AML flags are not readable by rules.", W(Agg("inflowsFromFlaggedWallets", "gte", 1))),
        ("MUL-04", "Customer age is not available to rules.", W(F_("customer.age", "between", new[] { 18, 24 }))),
        ("XBR-03", "No corridor share per peer group (nationality/segment).", W(Agg("corridorPeerShare", "lt", 1))),
        ("TF-04", "Balances are not available (withdrawal of ≥ P% of balance) and dormancy cannot be combined with a cross-border step sequence.", W(F_("wallet.balanceWithdrawnPct", "gte", 90))),
        ("ATO-01", "Authentication events (password reset) and balances are not ingested.", W(F_("event.passwordResetHoursAgo", "lte", 6))),
        ("ATO-02", "Beneficiary-added events and balances are not ingested.", W(F_("event.beneficiaryAddedMinutesAgo", "lte", 30))),
        ("ATO-03", "Login country history is not available to rules.", W(F_("session.isNewLoginCountry", "eq", true))),
        ("VAG-01", "No merchant/beneficiary classification (VASP) readable for the counterparty of a transaction.", W(F_("counterparty.category", "eq", "VASP"))),
        ("VAG-02", "No merchant MCC/gambling classification readable for the counterparty of a transaction.", W(F_("counterparty.mcc", "in", new[] { "7995" }))),
        ("VAG-03", "No counterparty VASP classification for inflows; cannot restrict rapidMovement inflows to VASP sources.", W(F_("counterparty.category", "eq", "VASP"))),
    }.Select(g => new object[] { g.Sc, g.Reason, g.Attempt });

    [Theory]
    [MemberData(nameof(Gaps))]
    public void Scenario_not_configurable(string scenario, string reason, object attempt)
    {
        var errors = Validate(attempt);
        Assert.NotEmpty(errors);
        Blocked("SC:" + scenario, $"Capability gap — {reason} Closest rule definition rejected by the rule validator: {string.Join("; ", errors.Take(3))}.");
    }
}
