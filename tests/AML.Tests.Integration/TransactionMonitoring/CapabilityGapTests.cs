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
        ("VEL-07", "No aggregate for regularity of time gaps (standard deviation of inter-transaction intervals).", W(Agg("intervalStdDev", "lte", 5))),
        ("GEO-01", "No geolocation or distance/speed (impossible travel) computation; transactions carry country only.", W(Agg("travelSpeedKmh", "gt", 900))),
        ("GEO-07", "No 'first cross-border after M months domestic-only' aggregate (newCountries ignores amount and months of domestic history).", W(Agg("firstCrossBorderAfterMonths", "gte", 6))),
        ("GEO-08", "Agent locations / distances are not modelled; cash-in and cash-out by different persons cannot be correlated.", W(Agg("agentDistanceKm", "gte", 100))),
        ("GEO-09", "No border-zone agent classification available to rules (agent attributes are not readable from customer-subject rules).", W(F_("agent.borderZone", "eq", true))),
        ("DEV-03", "IP anonymiser (VPN/proxy/TOR) classification is not captured or exposed to rules.", W(F_("session.ipAnonymiser", "eq", true))),
        ("DEV-04", "Rooted/jailbroken/emulator device flags are not captured or exposed to rules.", W(F_("device.rooted", "eq", true))),
        ("DEV-07", "Channel limit-hit events are not ingested; channel switch sequence cannot be expressed.", W(Agg("channelSwitchAfterLimit", "gte", 1))),
        ("BEH-07", "No salary-credit linkage or 'M consecutive months' pattern.", W(Agg("salaryRemittedShareConsecutiveMonths", "gte", 3))),
        ("AGT-01", "Rules cannot test links between the agent and the customers it serves (shared ID/phone/device/family).", W(Agg("agentLinkedCustomerTxns", "gte", 5))),
        ("AGT-02", "peerRatio uses agent volume, not the cash-out/cash-in ratio compared with a regional peer median × R.", W(new { aggregate = "peerRatioOfRatio", peerGroup = "region", @operator = "gt", value = 3 })),
        ("AGT-04", "No 'customers registered by agent who transact within 24 h' share aggregate.", W(Agg("agentNewCustomerActiveShare", "gte", 50))),
        ("AGT-06", "Agent location / customer residence distance is not modelled.", W(Agg("agentDistanceKm", "gte", 50))),
        ("MER-03", "Rules cannot read links between payers and the merchant owner.", W(Agg("ownerLinkedPayers", "gte", 3))),
        ("MER-04", "No sequence pattern 'payment followed by refund or merchant cash-out ≥ P%' scoped to the same merchant (rapidMovement measures any outflow after any inflow, without refund pairing).", W(Agg("paymentThenRefundShare", "gte", 90))),
        ("MER-05", "POS geolocation and merchant registered area are not modelled.", W(Agg("posDistanceKm", "gt", 20))),
        ("REV-02", "Refund destination instrument vs original payment instrument is not captured.", W(F_("txn.refundInstrumentMismatch", "eq", true))),
        ("REV-03", "No sequence pattern of failed attempts with decreasing amounts followed by a success.", W(Agg("failedDecreasingThenSuccess", "gte", 3))),
        ("REV-05", "Invoice amounts are captured (txn.invoiceAmount), but a refund cannot be linked to the third party that receives it, so refunds to third parties cannot be compared with the invoice.", W(F_("txn.refundToThirdParty", "eq", true))),
        ("XBR-03", "No corridor share per peer group (nationality/segment).", W(Agg("corridorPeerShare", "lt", 1))),
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
