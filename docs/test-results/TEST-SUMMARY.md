# Test & build summary — AML Platform v1.1

Execution date: 2026-10-15 · Environment: .NET 8.0 SDK, SQL Server 2022 (Docker), Node 22, Chromium (Playwright 1.56.1).

## Build
| Component | Command | Result |
|---|---|---|
| Backend solution | `dotnet build AML.sln` | Succeeded (0 errors) |
| Frontend | `npm run build` (`tsc --noEmit` + `vite build`) | Succeeded (0 TypeScript errors; single 1.6 MB JS bundle) |
| Database | EF Core migrations `InitialCreate` (117 tables) + `ProtectionTriggers` | Applied on clean databases in every integration run |

## Automated test runs
| Suite | Tests | Passed | Failed |
|---|---|---|---|
| `AML.Tests.Unit` (domain engines) | 74 | 74 | 0 |
| `AML.Tests.Integration` excluding `TransactionMonitoring` (API + SQL Server, fresh DB per run, ~7.5 min) | 239 | 239 | 0 |
| `AML.Tests.Integration` — `TransactionMonitoring` scenario suite (separate workbook, own DB) | 101 methods / 314 cases | 101 | 0 — cases: 252 Pass, 0 Fail, 62 Blocked; see `TM-TEST-SUMMARY.md` |
| Playwright E2E (`src/AML.Web/e2e`, running API + UI) | 9 | 9 | 0 |
| **Total** | **322** | **322** | **0** |

## Test-case workbooks (results filled in)
- `AML_Platform_Test_Cases_v1.1_RESULTS.xlsx` — 318 cases: **316 Pass, 0 Fail, 2 Blocked** (TC-NFR-004 24 h soak, TC-NFR-006 DR fail-over: need infrastructure not available here).
- `AML_Platform_Test_Cases_v1.1_2_RESULTS.xlsx` — 295 cases: **294 Pass, 0 Fail, 1 N/A** (AML-TC-0270 model governance: no ML model in v1.1 scope).

Each row has Status, Actual Result (the outcome the test observed), Tester (`Claude (automated test suite)`), Execution Date and — in the v1.1 workbook — the automated test(s) that produced the result in *Notes*. The Summary sheet formulas update automatically.

### How to read "Pass"
- A case is marked Pass only when every automated test mapped to it passed; the mapping is explicit in source (`[Tc(...)]` attributes / `[TC-…]` Playwright titles).
- Many cases are written as UI (E2E) procedures. Most were verified through the same API the UI uses (integration level). UI behaviour is verified directly by the Playwright specs (RTL/Arabic, bilingual labels, loading/empty/error/forbidden states, confirmation modals, accessibility/contrast, responsive layout, metadata failure, locale formatting, XSS rendering, profile tabs) and by a scripted crawl of all ~31 pages in English and Arabic during frontend development. A human UAT pass over the UI flows is still recommended.
- Performance figures (TC-NFR-001/002, AML-TC-0247) come from short in-process runs on a single developer container (200 real-time decisions: p95 well below the 300 ms budget). They are not a substitute for a peak-load test on production-like infrastructure. TC-RPT-003 used a small export, not 200 000 rows.

## Results by module (v1.1 workbook)
| Module | Total | Pass | Fail | Blocked | N/A |
|---|---|---|---|---|---|
| Dynamic Customer Fields | 18 | 18 | 0 | 0 | 0 |
| Customer Profile & Search | 12 | 12 | 0 | 0 | 0 |
| Customer Acceptance, CDD & Beneficial Ownership | 16 | 16 | 0 | 0 | 0 |
| Risk Methodology Configuration | 12 | 12 | 0 | 0 | 0 |
| Risk Engine Calculation | 20 | 20 | 0 | 0 | 0 |
| Risk Override & Review | 10 | 10 | 0 | 0 | 0 |
| Wallets & Party Resolution | 10 | 10 | 0 | 0 | 0 |
| Transaction Ingestion & Idempotency | 14 | 14 | 0 | 0 | 0 |
| Currency, Time Zone & Calendar | 10 | 10 | 0 | 0 | 0 |
| Rule Builder & Execution | 20 | 20 | 0 | 0 | 0 |
| Alert Deduplication & Lifecycle | 12 | 12 | 0 | 0 | 0 |
| Case Management | 10 | 10 | 0 | 0 | 0 |
| Screening & List Management | 16 | 16 | 0 | 0 | 0 |
| Compliance Actions & Freezing | 10 | 10 | 0 | 0 | 0 |
| Real-time Decisioning | 10 | 10 | 0 | 0 | 0 |
| Payment Transparency (R.16) | 6 | 6 | 0 | 0 | 0 |
| Regulatory Reporting (STR/SAR) | 12 | 12 | 0 | 0 | 0 |
| Tipping-off Controls | 8 | 8 | 0 | 0 | 0 |
| Entity Resolution & Device Intelligence | 6 | 6 | 0 | 0 | 0 |
| Agent & Merchant Risk | 5 | 5 | 0 | 0 | 0 |
| Configuration Integrity | 4 | 4 | 0 | 0 | 0 |
| Monitoring Effectiveness & Tuning | 6 | 6 | 0 | 0 | 0 |
| Investigation QA | 5 | 5 | 0 | 0 | 0 |
| Retention & Legal Hold | 6 | 6 | 0 | 0 | 0 |
| Security & Audit | 16 | 16 | 0 | 0 | 0 |
| API Contracts | 8 | 8 | 0 | 0 | 0 |
| Reports & Exports | 8 | 8 | 0 | 0 | 0 |
| Localization & Arabic Name Matching | 10 | 10 | 0 | 0 | 0 |
| Non-functional (Performance, Availability, DR) | 8 | 6 | 0 | 2 | 0 |
| User Interface States | 5 | 5 | 0 | 0 | 0 |
| Jurisdiction Configuration & Seed Data | 5 | 5 | 0 | 0 | 0 |

## Defects found by the tests and fixed before the final run
1. Decimal field validation allowed too many integer digits for a configured precision/scale (`FieldValueValidator`).
2. Field-change history showed numbers with trailing zeros (`5000.00000000`).
3. KYC-gap report missed "no identification" and listed individual-only fields for corporates.
4. Acceptance policy evaluated KYC completeness before PEP/high-risk, so PEPs never reached *Pending Senior Approval*.
5. Methodology simulation returned 500 for future-dated drafts (now uses the methodology's effective date; engine errors map to 409).
6. Party-resolution rule version was not stored for owner-based resolutions.
7. **Real-time decisioning never evaluated real-time rules** (tenant filter missing in the isolated evaluation scope).
8. Screening-provider outage simulation never recovered after the configured number of failures.
9. `X-Correlation-ID` header missing on error responses.
10. WCAG 2 AA contrast failures in the UI (secondary text 3.4:1, status tags) — theme tokens/tag colours darkened.
11. Logging used the process-wide static Serilog logger, so a second host instance could redirect/close logging (now per host).

## Reproduce
```bash
dotnet test AML.sln --logger trx                    # needs SQL Server; set AML_TEST_SQL if not localhost sa
cd src/AML.Web && npx playwright test               # needs the API on :5080 (Development + demo data)
```
