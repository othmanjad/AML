# Transaction Monitoring Scenarios — Test Execution Summary

Workbook: `AML_Transaction_Monitoring_Test_Cases.xlsx` (120 scenarios, 314 test cases). Executed 2026-10-16 by the automated suite `tests/AML.Tests.Integration/TransactionMonitoring` against SQL Server 2022 (each scenario configured as a rule through the public API, data ingested through the integration API, rules executed and alerts inspected).

| Status | Count |
|---|---|
| Pass | 268 |
| Fail | 0 |
| Blocked | 46 |
| Not Run | 0 |
| **Total** | **314** |

## By category

| Category | Pass | Fail | Blocked | Not Run |
|---|---|---|---|---|
| Velocity & Frequency | 30 | 0 | 4 | 0 |
| Amount & Thresholds | 31 | 0 | 0 | 0 |
| Structuring & Smurfing | 22 | 0 | 0 | 0 |
| Rapid Movement & Pass-through | 17 | 0 | 0 | 0 |
| Fan-in / Fan-out | 14 | 0 | 0 | 0 |
| Geographic & Impossible Travel | 15 | 0 | 11 | 0 |
| Device, IP & Channel | 10 | 0 | 6 | 0 |
| Behavioural Change & Profile Mismatch | 17 | 0 | 2 | 0 |
| Cash-in / Cash-out | 11 | 0 | 0 | 0 |
| Agent Network | 7 | 0 | 8 | 0 |
| Merchant | 4 | 0 | 6 | 0 |
| Reversals, Refunds & Failed Attempts | 5 | 0 | 6 | 0 |
| Circular & Related-party | 7 | 0 | 0 | 0 |
| Money Mule | 8 | 0 | 0 | 0 |
| Cross-border Remittance | 9 | 0 | 2 | 0 |
| Top-ups, Vouchers & Bill Payments | 8 | 0 | 0 | 0 |
| Terrorist Financing Indicators | 8 | 0 | 0 | 0 |
| Sanctions & High-risk Counterparties | 10 | 0 | 0 | 0 |
| Account Takeover Overlap (FRAML) | 6 | 0 | 0 | 0 |
| Virtual Assets & Gambling | 6 | 0 | 0 | 0 |
| Engine Edge Cases (applies to all scenarios) | 23 | 0 | 1 | 0 |

## Failed test cases (open defects / engine gaps)

| ID | Description | Test cases |
|---|---|---|

## Defects fixed after the first execution (re-tested, now Pass)

| ID | Defect | Fix | Test cases |
|---|---|---|---|
| DEF-TM-01 | Own-wallet / self-transfer exclusion not configurable | Rule field txn.isOwnAccount | TM-VEL-03-03 |
| DEF-TM-02 | Missing profile/attribute data silently evaluated as no-match | Missing inputs raise RuleInputMissing data-quality items (result incomplete) | TM-AMT-03-04, TM-BEH-04-03 |
| DEF-TM-03 | Entity-resolution links not usable by rules or shown in evidence | Subject 'Entity' (linked customers, minimum confidence) and linkedParties in alert evidence | TM-STR-03-04, TM-STR-04-03 |
| DEF-TM-04 | No secondary indicator flags / severity escalation | action.indicators (flag + optional severity) and counterparty.* fields | TM-RMV-03-03, TM-FAN-01-04, TM-FAN-02-04 |
| DEF-TM-05 | Runs used the active rule version for all transactions | Manual runs evaluate each period with the version in force at transaction time | TM-GEO-03-03 |
| DEF-TM-06 | No per-customer behavioural baseline | historyCount / historySum / historyRatio aggregates over the customer's own lookback | TM-BEH-03-02 |
| DEF-TM-07 | Real-time list hits created no AML alert | Real-time list matches raise Critical/High alerts | TM-SAN-01-01, TM-SAN-04-01 |
| DEF-TM-08 | Keyword matching without normalisation | contains normalises case, diacritics and letter spacing | TM-SAN-02-03 |
| DEF-TM-09 | Later reversal not noted on the alert | LinkedTransactionReversed entry in alert history | TM-ENG-02-03 |
| DEF-TM-10 | Late arrivals never re-evaluated / not logged | Scheduler re-evaluates late arrivals within Rules:LateArrivalLookbackDays; older ones raise LateArrivalNotEvaluated | TM-ENG-06-01, TM-ENG-06-02 |
| DEF-TM-11 | No wallet-level aggregation; unresolved parties not evaluated | Subject 'Wallet' and unresolvedParties = Wallet | TM-ENG-07-01, TM-ENG-07-02 |
| DEF-TM-12 | Clearing-wallet legs double counted | Second leg marked EconomicLegOfId and excluded from monitoring/aggregates | TM-ENG-10-02 |
| DEF-TM-13 | Real-time holds not linked to batch alerts | Real-time rule hits raise the alert with the batch deduplication key | TM-ENG-11-01 |
| DEF-TM-14 | Quadratic rule evaluation (50k transactions did not finish) | Indexed windows, prefix sums, deferred evidence, merge by dedup key (50k in ~10 s) | TM-ENG-12-02 |

## Blocked scenarios (cannot be configured with the current rule DSL / data)

| Scenario | Reason |
|---|---|
| AGT-01 | Rules cannot test links between the agent and the customers it serves (shared ID/phone/device/family). |
| AGT-02 | peerRatio uses agent volume, not the cash-out/cash-in ratio compared with a regional peer median × R. |
| AGT-04 | No 'customers registered by agent who transact within 24 h' share aggregate. |
| AGT-06 | Agent location / customer residence distance is not modelled. |
| BEH-07 | No salary-credit linkage or 'M consecutive months' pattern. |
| DEV-03 | IP anonymiser (VPN/proxy/TOR) classification is not captured or exposed to rules. |
| DEV-04 | Rooted/jailbroken/emulator device flags are not captured or exposed to rules. |
| DEV-07 | Channel limit-hit events are not ingested; channel switch sequence cannot be expressed. |
| GEO-01 | No geolocation or distance/speed (impossible travel) computation; transactions carry country only. |
| GEO-07 | No 'first cross-border after M months domestic-only' aggregate (newCountries ignores amount and months of domestic history). |
| GEO-08 | Agent locations / distances are not modelled; cash-in and cash-out by different persons cannot be correlated. |
| GEO-09 | No border-zone agent classification available to rules (agent attributes are not readable from customer-subject rules). |
| MER-03 | Rules cannot read links between payers and the merchant owner. |
| MER-04 | No sequence pattern 'payment followed by refund or merchant cash-out ≥ P%' scoped to the same merchant (rapidMovement measures any outflow after any inflow, without refund pairing). |
| MER-05 | POS geolocation and merchant registered area are not modelled. |
| REV-02 | Refund destination instrument vs original payment instrument is not captured. |
| REV-03 | No sequence pattern of failed attempts with decreasing amounts followed by a success. |
| REV-05 | Invoice amounts are captured (txn.invoiceAmount), but a refund cannot be linked to the third party that receives it, so refunds to third parties cannot be compared with the invoice. |
| VEL-07 | No aggregate for regularity of time gaps (standard deviation of inter-transaction intervals). |
| XBR-03 | No corridor share per peer group (nationality/segment). |
| TM-ENG-12-01 | 2 million transactions/day with all active rules requires a production-sized SQL Server and data generator; not executable in this test container. The engine loads all transactions of the window into memory (no pre-aggregated batch path), so the target is not demonstrated. |

## Scenarios made executable by the rule DSL extension (Blocked in the first execution)

AMT-05, AMT-07, ATO-03, BEH-02, BEH-05, BEH-08, CIR-02, CIR-03, CSH-03, DEV-02, DEV-05, DEV-06, FAN-03, FAN-05, GEO-02, GEO-06, MUL-02, MUL-03, MUL-04, STR-06, VAG-01, VAG-02, VAG-03, VEL-06, VEL-08

## Scenarios made executable by end-of-day balances, multi-hop chains and peer percentiles (second extension)

AMT-06, ATO-01, ATO-02, RMV-02, RMV-04, TF-04

## Method notes

- A case is **Pass** when the stated expected outcome was observed with a rule configuration that captures the scenario's core logic; where a parameter of the scenario could not be expressed, the configuration used and the limitation are recorded in the Notes column.
- Thresholds are the workbook's example defaults (to be confirmed by compliance), not regulatory values.
- All data is synthetic; no STR, freeze or core-system command was sent to a real system.
