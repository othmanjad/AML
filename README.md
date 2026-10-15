# AML & Transaction-Monitoring Platform (v1.1 implementation)

Configurable anti-money-laundering platform implemented from *AML Platform AI Implementation Specification v1.1*:
dynamic customer data model, configurable customer risk assessment (CRA), JSON rule engine with batch and real-time
monitoring, alerts and case management, sanctions/PEP screening with list management, compliance actions (freeze/hold)
through adapters, regulatory reporting (goAML-style STR), entity resolution, effectiveness/QA, retention and an
Arabic/English RTL web UI.

> All seeded data is fictitious and labelled as test/sample. Jurisdiction-specific values are seeded as
> **"To be confirmed by compliance"**. Development/test environments can never freeze funds or submit reports to real systems.

## Solution layout

| Path | Purpose |
|---|---|
| `src/AML.Domain` | Entities and pure domain engines: risk scoring, rule DSL validator/evaluator, Arabic name matching, ownership, working calendar, FX, R.16 validator, field validation |
| `src/AML.Application` | Use-case services (customers, CDD, risk, ingestion, rules, alerts, cases, screening, actions, real-time, reporting, governance, reports, admin) and abstractions |
| `src/AML.Infrastructure` | EF Core `AmlDbContext`, migrations, reference + demo seeders, crypto (AES-GCM/HMAC/PBKDF2), adapters (screening, mock core, goAML XML, sandbox FIU channel, CSV/XLSX export), background jobs |
| `src/AML.Api` | ASP.NET Core 8 REST API (JWT, permission policies, problem-details, correlation IDs, masking logs) |
| `src/AML.Worker` | Hosted background worker running the job catalogue (outbox, retries, schedulers, SLA, retention-related jobs) |
| `src/AML.Web` | React 18 + TypeScript + Vite + Ant Design UI (English/Arabic RTL), Playwright E2E tests in `e2e/` |
| `tests/AML.Tests.Unit` | Domain unit tests (risk engine, rules, name matching, ownership, calendar/FX, R.16, field validation) |
| `tests/AML.Tests.Integration` | API-level integration tests against a real SQL Server database (`WebApplicationFactory`) |
| `docs/` | API reference, ERD, security & deployment checklist, known gaps, test results |

## Prerequisites
- .NET SDK 8.0, Node.js 20+ (22 tested), SQL Server 2019+ (Docker image `mcr.microsoft.com/mssql/server:2022-latest` works).
- Optional: `dotnet tool install -g dotnet-ef --version 8.0.11` for migration management.

## Quick start (development)

```bash
# 1. SQL Server
docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong-password>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

# 2. Configuration — copy and fill in (never commit the filled file)
cp .env.example .env && $EDITOR .env
set -a && . ./.env && set +a

# 3. API (applies migrations + reference seed + demo data when enabled)
dotnet run --project src/AML.Api            # http://localhost:5080, Swagger at /swagger in Development

# 4. Worker (production-style background jobs; skip if Jobs__RunInApi=true)
dotnet run --project src/AML.Worker

# 5. Frontend
cd src/AML.Web && npm install && npm run dev  # http://localhost:5173 (proxies /api to AML_API_URL)
```

Development users (password = `DevAuth__SeedPassword`): `admin`, `manager` (AML manager), `officer`, `officer2`,
`senior` (senior reviewer), `mlro`, `investigator`, `auditor` (read-only), `ops` (operations), `regional`
(data scope NORTH), `tenantb` (second tenant). Integration clients: `jo-demo-core`, `jo-demo-wallet`, `jo-demo-cards`
(secret = `Integrations__SeedClientSecret`) via `POST /api/auth/token`.

### Migrations
Migrations live in `src/AML.Infrastructure/Persistence/Migrations` (`InitialCreate`, `ProtectionTriggers`).
They are applied automatically when `Database__MigrateOnStartup=true`, or manually:
```bash
dotnet ef database update --project src/AML.Infrastructure --startup-project src/AML.Api
dotnet ef migrations add <Name> --project src/AML.Infrastructure --startup-project src/AML.Api -o Persistence/Migrations
```

## Tests

```bash
# Unit tests (no database)
dotnet test tests/AML.Tests.Unit

# Integration tests: each run creates and drops a throw-away database AML_Test_<id>.
# AML_TEST_SQL overrides the server connection (default: localhost,1433 sa with the local container password used in CI).
AML_TEST_SQL="Server=localhost,1433;User Id=sa;Password=<pwd>;TrustServerCertificate=true" dotnet test tests/AML.Tests.Integration

# End-to-end UI tests (API must be running on :5080 with Development settings and demo data)
cd src/AML.Web && npx playwright test        # set CHROMIUM_PATH if Chromium is not at /opt/pw-browsers/...
```

Every automated test is tagged with the test-case IDs it verifies (`[Tc("TC-RSK-005","AML-TC-0049")]` in C#,
`[TC-…]` prefixes in Playwright titles) and records its observed outcome as `ACTUAL:` output.
Executed results for all 613 test cases are in `docs/test-results/` — see
[`docs/test-results/TEST-SUMMARY.md`](docs/test-results/TEST-SUMMARY.md).

## Documentation
- [API reference](docs/API.md) (Swagger/OpenAPI available in Development at `/swagger`)
- [Data model / ERD](docs/ERD.md)
- [Security & deployment checklist](docs/SECURITY-DEPLOYMENT-CHECKLIST.md)
- [Implemented features & known gaps](docs/KNOWN-GAPS.md)
- [Test & build summary](docs/test-results/TEST-SUMMARY.md)
