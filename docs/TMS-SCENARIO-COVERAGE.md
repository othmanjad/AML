# Coverage of "AML TMS Rules and Scenarios Summary"

How each rule type, scenario, parameter and requirement of `AML_TMS_Rules_and_Scenarios_Summary.docx` maps to the platform, with an example rule definition and the automated tests that prove it. Thresholds below are the document's illustrative values (to be confirmed by compliance), not regulatory values. TM-* IDs are cases of `AML_Transaction_Monitoring_Test_Cases.xlsx`; COV-* checks are in `tests/AML.Tests.Integration/TransactionMonitoring/CoverageExtensionTests.cs`.

## Rule types

| Rule type | How it is configured | Tests |
|---|---|---|
| Threshold | Field condition on `txn.amount` / `txn.baseAmount` (optionally `valueBy` for risk-weighted thresholds) | TM-AMT-01, TM-AMT-08, COV-RW-01..03 |
| Aggregation | `sum` / `count` over rolling or calendar windows; subject Customer, Wallet, Entity or Household | TM-AMT-02, TM-STR-01..06, COV-HH-01..03 |
| Velocity | `count` over a short window, `consecutiveDays` | TM-VEL-01..06, TM-VEL-08 |
| Profile deviation | `profileRatio` (declared activity), `historyRatio` / `historySum` (own baseline) | TM-AMT-03..05, TM-BEH-01..05 |
| Peer-group deviation | `peerPercentile`, `peerRatio` with `peerBy` (type, region, residence, nationality, risk level, any `field:CODE`) and `minPeers` | TM-AMT-06-01..03, COV-PEER-01..05 |
| Geographic / list-based | `txn.destinationCountry inList "COUNTRY:HighRisk"`, `txn.destinationCountryRisk` with `valueBy`, `newCountries`; screening of customers and counterparties against sanctions, PEP and adverse-media lists | TM-GEO-02..06, TM-SAN-01..04, COV-LIST-01..02 |
| Pattern / typology | `rapidMovement`, `cycle` (round-tripping), `chain` (layering), `reciprocalCount` | TM-RMV-01..05, TM-CIR-01..03, COV-CHN-01..04 |

## Core scenarios TM-01 … TM-12

| ID | Coverage | Example definition (abridged) | Tests |
|---|---|---|---|
| TM-01 Cash structuring | Full | `window: Rolling 3 days`, `and(sum[txn.type in CashIn,CashOut] gte 9000, count gte 3)`; related parties via `subject: Entity` or `Household` | TM-STR-01..06, TM-CSH-01, COV-HH-* |
| TM-02 Pass-through | Full | `and(rapidMovement{maxMinutes: 7200} gte 80, avgBalance lte 100)` or `balanceRetentionPct lte 5` | COV-BAL-02..04, TM-RMV-02-01..03 |
| TM-03 Round / just-below threshold | Full | `count[txn.amount multipleOf 100 or txn.amount between 9000,9999] gte 3` | TM-AMT-07, TM-STR-01, TM-STR-05, TM-XBR-05 |
| TM-04 High-risk jurisdiction | Full | `txn.destinationCountry inList "COUNTRY:HighRisk"`; cumulative `sum[...] gte X`; `txn.amount gte 10000` with `valueBy {field: txn.destinationCountryRisk, values: {HighRisk: 500, Elevated: 2000}}` | TM-GEO-03, TM-GEO-04, COV-RW-03, COV-LIST-01 |
| TM-05 Volume spike vs profile | Full | `and(profileRatio{profileField: profile.expectedMonthlyVolume} gte 3, sum gte 1000)` or `peerPercentile{peerBy: [customer.type, customer.region]} gte 99` (calendar month) | TM-AMT-03, TM-AMT-04, TM-AMT-06, TM-VEL-05, COV-PEER-* |
| TM-06 Large single transaction | Full | `and(txn.amount gte 10000, historyRatio gte 3)` (window = the transaction, lookback = trailing period) | TM-AMT-01, TM-AMT-05 |
| TM-07 Dormant reactivation | Full | `and(dormancyDays gte 180, sum gte X)` | TM-BEH-01 |
| TM-08 Layering via linked accounts | Full | `count[txn.isLinkedParty eq true] gte N` or `chain{maxDepth: 6, maxMinutes: 240, minHopPct: 80} gte 3` | TM-RMV-04-01..04, TM-CIR-03, COV-CHN-01..04 |
| TM-09 Unrelated third parties | Full | `distinctCounterparties gte N`, `newCounterpartyShare gte P` | TM-FAN-01..03, TM-MUL-01 |
| TM-10 Cross-border anomaly | Full | `and(txn.isCrossBorder eq true, newCountries gte 1, txn.amount gte X)` | TM-XBR-01, TM-XBR-04, TM-GEO-05 |
| TM-11 Cash-intensive / trade-based | Full | `benchmarkDeviation{benchmark: CASH_SHARE_PCT, numerator: txn.type eq CashIn, direction: in} gte T`; trade: `and(txn.hasInvoice eq true, txn.invoiceVariancePct gte 20)` | TM-CSH-01, TM-CSH-03, TM-MER-01, COV-BEN-01..04, COV-INV-01..02 |
| TM-12 Sanctions / PEP / adverse media | Full (internal lists) | Batch and real-time name screening with fuzzy score thresholds; real-time block + Critical/High alert | TM-SAN-01..04, screening suites |

## Parameters

| Parameter | Where |
|---|---|
| Monetary threshold, count N, multiplier K/M, inactivity D | Condition `value`; all rules are data, versioned, maker-checker approved |
| Time window | `window` (Rolling minutes/hours/days or Calendar Day/Week/Month, Asia/Amman) and `lookbackDays` |
| Aggregation key | `subject`: Customer, Wallet, Agent, Device, Entity (beneficial owners / linked parties via entity resolution), Household; counterparty grouping via `distinctCounterparties` / `reciprocalCount` |
| Risk weighting | `valueBy` on `customer.riskLevel`, `txn.destinationCountryRisk`, `txn.sourceCountryRisk` or any text field |
| Match score | Screening thresholds per list (configuration) |

Tuning: simulation, backtest, ATL/BTL tuning runs with sampling, rule effectiveness metrics (alert volume, false-positive rate, case and STR conversion) — `/api/admin/aml-rules/{id}/simulate|backtest|tune`, `/effectiveness`.

## Alert, case and reporting flow
Alert scoring and evidence → triage → disposition (false positive with reason, or escalation) → case (consolidated alerts, narrative, evidence, approval) → STR/SAR (from a case, MLRO approval, goAML XML, manual submission) and CTR (daily cash aggregation above `CTR_THRESHOLD`, MLRO approval, XML/CSV export, manual submission). Nothing is sent to an FIU automatically. Tests: COV-CTR-01..05 and the alert/case/STR suites.

## Data requirements

| Requirement | Status |
|---|---|
| Transaction records | Ingested (amount, currency, time, type, channel, direction, countries, device, remarks, invoice data) |
| Account and customer master data incl. balance history | Customers, wallets; end-of-day balances via `POST /api/integrations/{source}/balances` |
| KYC/CDD profile | Expected activity, occupation, risk rating, beneficial owners |
| Counterparty data | Resolved parties, originator/beneficiary information, `counterparty.*` fields |
| Reference / watch lists | Internal lists (sanctions, PEP, adverse media) and the COUNTRY lookup with HighRisk / Elevated attributes |
| Relationship links | Entity resolution, ownership links, household identifier |

## Remaining limits
- Peer groups use current customer attributes; peer aggregates need calendar windows.
- Benchmark values are maintained manually with their source; no external benchmark feed.
- Balances depend on a daily end-of-day feed from the core; intraday balances are not modelled.
- Screening uses internal lists only (no commercial provider); FIU STR/CTR schemas are to be confirmed.
- Machine-learning / anomaly models are not implemented (rules only).
