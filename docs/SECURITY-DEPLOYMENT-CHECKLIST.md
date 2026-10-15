# Security & deployment checklist

## Before any non-development deployment
- [ ] `ASPNETCORE_ENVIRONMENT=Production` (or Staging) — enables HSTS + HTTPS redirection, disables Swagger, enforces the manual-only FIU channel.
- [ ] `DevAuth__Enabled=false`; configure `Auth__Authority`/`Auth__Audience` for the enterprise OIDC provider; map IdP groups to platform roles.
- [ ] Secrets only from a secret manager/KMS: `ConnectionStrings__Aml`, `Security__EncryptionKey`, `Security__SearchTokenKey`, `Security__IntegrityKey`, client secrets. Rotate integration client secrets (`POST /api/admin/source-systems/{code}/rotate-secret`).
- [ ] Key rotation procedure agreed (`Security__EncryptionKeyId`); HMAC search-token key changes require re-tokenisation.
- [ ] SQL login for the application is least-privilege (DML only; no `ALTER`/`DISABLE TRIGGER`/`DROP`); migrations run by a separate deployment principal.
- [ ] Verify DB triggers exist: `TR_AuditLogs_Protect`, `TR_RiskAssessments_Immutable`, `TR_RiskAssessments_NoDelete`, `TR_RiskAssessmentFactors_Immutable`, `TR_AlertRuleMatches_Immutable`, `TR_CaseNotes_Immutable`, `TR_RegulatoryReports_SubmittedImmutable`.
- [ ] TDE / storage encryption enabled for SQL Server and the evidence store (`Storage__Root` outside any web root, not publicly served).
- [ ] TLS 1.2+ at the edge; HSTS verified on the public host name (ASP.NET Core omits it for `localhost`).
- [ ] CORS origins restricted to the deployed UI (`Cors__Origins`).
- [ ] Malware scanning: replace the signature/EICAR stub (`SignatureMalwareScanner`) with an AV engine (e.g. ICAP/ClamAV) before accepting real documents.
- [ ] Core-banking/wallet adapter: only the mock adapter exists — implement and certify the real `ICoreSystemAdapter` before enabling automatic freezes in production (see KNOWN-GAPS).
- [ ] FIU: confirm goAML schema/version with the national FIU and replace the baseline XSD; production uses manual export + recorded submission evidence.
- [ ] Jurisdiction settings: every `ToBeConfirmed` value reviewed by Compliance with legal source + article reference and approved (four-eyes).
- [ ] Sample rules/thresholds/methodologies (flagged `IsSample`) reviewed, re-calibrated and approved, or left disabled.
- [ ] Demo data disabled (`Seed__DemoData=false`); demo/test records can never be submitted.
- [ ] Run `POST /api/admin/audit/verify` after go-live and on a schedule; alert on tampered rows.

## Operations
- [ ] Deploy `AML.Worker` (one or more instances; jobs are idempotent) and set `Jobs__RunInApi=false` on API nodes.
- [ ] Monitoring: `/api/operations/health` (job heartbeats, data freshness, provider status, retry queues, outbox), operational alerts, log shipping of masked JSON logs with correlation IDs.
- [ ] Backups: full + log backups meeting the agreed RPO; quarterly restore test (restore verified in TC-NFR-005). Document RTO/RPO (target to be agreed, e.g. RPO ≤ 15 min, RTO ≤ 4 h).
- [ ] DR: secondary SQL replica + standby API/worker deployment; failover runbook tested (TC-NFR-006 pending infrastructure).
- [ ] Performance: peak-load and 24 h soak tests on production-like infrastructure (real-time p95 budget 300 ms configurable via `Realtime:LatencyBudgetMs`).
- [ ] Retention: retention policies confirmed per record class; deletion runs require dry-run → submit → independent approval → execute.
- [ ] Access reviews: periodic review of user roles, data-scope regions and restricted-case access report (`/api/cases/access-report`).

## Security controls implemented (for reference)
Permission policies on every endpoint (403 + audit on denial), maker-checker on configuration/approvals, data-scope and
tenant isolation, tipping-off controls (restricted cases invisible without trace, neutral outbound codes), AES-GCM field
encryption + keyed HMAC search tokens, masked logging (no IDs/tokens/secrets), HMAC-chained append-only audit log,
problem-details errors without stack traces, upload type/size/magic-byte validation + malware scan, CSV/XLSX formula-injection
neutralisation, allow-listed sorting, page-size caps, optimistic concurrency, idempotent ingestion/actions/decisions.
