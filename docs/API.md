# API reference

Base URL: `https://<host>/api`. All endpoints except `POST /api/auth/login` (development only) and `POST /api/auth/token` require a Bearer JWT.
OpenAPI/Swagger UI is served at `/swagger` in the Development environment only.

## Conventions
- **Errors** — RFC 7807 `application/problem+json` with `type`, `title`, `status`, `detail`, `errors` (field → messages), `traceId` and `correlationId`. No stack traces.
- **Correlation** — send `X-Correlation-ID` (≤64 safe chars) or one is generated; it is echoed in the response header, error bodies, logs and audit records.
- **Paging** — list endpoints accept `page`, `pageSize` (capped at 200), `sort` (allow-listed column) and `dir` (`asc|desc`) and return `{ items, page, pageSize, total }`.
- **Concurrency** — mutable aggregates expose `rowVersion` (base64); send it back on updates; stale values return `409`.
- **Authorization** — every endpoint is protected by a permission policy (`perm:<code>`); denials return `403` and are audited (`ACCESS_DENIED`). Data scope, tenant and confidentiality filters return `404` for invisible records (no existence leakage).
- **Integration clients** — `POST /api/auth/token {clientId, clientSecret}` issues a client token limited to its source system (`integrations.ingest`, `realtime.decide`).
- **Idempotency** — ingestion is idempotent on `(sourceSystem, sourceTransactionId)` + payload hash; real-time decisions on the source transaction ID; screening and compliance actions on idempotency keys.

## Key payloads
```jsonc
// POST /api/integrations/{source}/transactions
{ "batchId": "B-1", "controlTotals": [{ "businessDate": "2026-10-14", "count": 2, "amount": 300, "currency": "JOD" }],
  "transactions": [{ "sourceTransactionId": "T-1", "type": "P2P", "status": "SUCC", "eventTime": "2026-10-14T10:00:00Z", "amount": 150.25,
    "currency": "JOD", "senderWallet": "W1", "receiverWallet": "W2", "channel": "Mobile", "sourceCountry": "JO", "destinationCountry": "AE",
    "originalReference": "REF", "originalSourceTransactionId": null, "isReversal": false, "originator": { "name": "…", "address": "…", "dateOfBirth": "1990-01-01" },
    "beneficiary": { "name": "…", "account": "…" }, "copResult": "MATCH", "deviceId": "…", "remarks": "…" }] }
// → { runId, batchId, status, received, processed, duplicates, conflicts, errors, correlationId, records: [{ sourceTransactionId, status, transactionId, error, recordId }] }

// POST /api/realtime/transactions/decide  → { decisionId, decision: Allow|HoldForReview|Reject, reasonCodes, correlationId, latencyMs, isFallback, idempotent }
{ "sourceTransactionId": "RT-1", "transactionType": "P2P", "channel": "Mobile", "amount": 50, "currency": "JOD", "senderWallet": "W1", "beneficiaryName": "…", "remarks": "…" }

// Rule definition (POST /api/admin/aml-rules → definition)
{ "subject": "Customer", "scope": { "channels": ["Agent"] }, "eligibleStatuses": ["Completed"], "reversalHandling": "Net",
  "window": { "type": "Rolling", "hours": 24 }, "dedupKey": ["rule", "version", "subject", "day"],
  "condition": { "op": "or", "conditions": [ { "op": "and", "conditions": [ { "field": "txn.amount", "operator": "gt", "value": 5000 }, { "field": "txn.channel", "operator": "eq", "value": "Agent" } ] },
                                          { "aggregate": "count", "operator": "gt", "value": 10 } ] },
  "action": { "severity": "High", "complianceAction": "HoldTransaction" } }
```
Allowed operators: `eq neq gt gte lt lte between in notIn contains multipleOf`; fields: `GET /api/config/rule-fields`.

| Element | Values / meaning |
|---|---|
| `subject` | `Customer` (default), `Wallet`, `Agent`, `Device`, `Entity` (customers joined by entity-resolution links with confidence ≥ `entityMinConfidence`, default 0.9) |
| `unresolvedParties` | `Ignore` (default) or `Wallet`: customer rules evaluate unresolved wallets at wallet level |
| `applicability` | Pre-condition; subjects that do not meet it are recorded as *not applicable* in the execution result (not as non-matches) |
| Aggregates | `count sum distinctCounterparties ratio rapidMovement dormancyDays profileRatio peerRatio newCustomers customersPerDevice cycle newCountries` and `historyCount historySum historyRatio` (customer's own `lookbackDays` before the window), `distinctCount` (`of`: channel, device, sourceCountry, destinationCountry, country, type, counterparty), `reciprocalCount`, `consecutiveDays` (`dailyRange` [min,max]), `newCounterpartyShare`, `sharedDeviceSenders`, `typeShareChange`, `minutesSinceEvent` (`event`: NewDevice, NewLoginCountry or a device-signal event type) |
| `ratio` | Without `denominator` the numerator is a subset of the window; with one, numerator and denominator filters are applied independently |
| Fields | `txn.*` (incl. `txn.isOwnAccount`, `txn.isLinkedParty`, `txn.loginCountryMismatch`), `customer.*` (incl. `customer.accountAgeDays`, `customer.age`), `profile.*`, `field:CODE`, and `counterparty.*` (the other resolved party, e.g. `counterparty.isPep`, `counterparty.field:CODE`) |
| `action.indicators` | `[{ "label", "condition", "severity"?, "severityMode": "Raise" \| "Set" }]` — met indicators are listed in alert evidence; `Raise` lifts severity to at least `severity`, `Set` sets it (e.g. lowers it for approved journeys) |
| Missing inputs | A customer/profile input that is missing makes the condition not met and raises a `RuleInputMissing` data-quality item (manual/scheduled runs) |
| Versions | Manual runs evaluate each period with the rule version in force at that time; scheduled runs re-evaluate late-arriving transactions within `Rules:LateArrivalLookbackDays` (default 7) and record older ones as `LateArrivalNotEvaluated` |
| Real time | List matches and real-time rule hits raise alerts; a real-time rule hit uses the batch deduplication key so the batch run merges into the same alert |

Device signals (`POST /api/customers/{id}/device-signals`): `{ deviceId, ipAddress?, geoCountry?, eventType, occurredAt? }`, `eventType` ∈ Registration, Login, SimChange, PhoneChange, EmailChange, AddressChange, PasswordReset, BeneficiaryAdded, DeviceChangeApproved. Logins from IP prefixes in setting `Monitoring:RoamingIpPrefixes` are treated as having no reliable location.

## Endpoint catalogue

| Area | Method | Path | Permission |
|---|---|---|---|
| Dashboard | GET | `/api/dashboard` | dashboard.read |
| Reports | GET | `/api/reports/catalogue` | reports.read |
| Reports | GET | `/api/reports/{code}` | reports.read |
| Reports | POST | `/api/reports/{code}/export` | reports.export |
| Reports | GET | `/api/exports` | reports.export |
| Reports | GET | `/api/exports/{jobId:guid}` | reports.export |
| Reports | GET | `/api/exports/{jobId:guid}/download` | reports.export |
| Governance | GET | `/api/governance/rule-metrics` | effectiveness.read |
| Governance | GET | `/api/governance/coverage` | effectiveness.read |
| Governance | POST | `/api/governance/coverage` | tuning.run |
| Governance | GET | `/api/governance/feed-completeness` | effectiveness.read |
| Governance | GET | `/api/governance/validation-pack` | effectiveness.read |
| Governance | POST | `/api/governance/qa/sample` | qa.review |
| Governance | GET | `/api/governance/qa/samples` | qa.review |
| Governance | POST | `/api/governance/qa/samples/{id:long}/review` | qa.review |
| Governance | GET | `/api/governance/qa/analyst-quality` | qa.review |
| Governance | GET | `/api/governance/retention/policies` | retention.manage |
| Governance | POST | `/api/governance/retention/legal-holds` | retention.manage |
| Governance | POST | `/api/governance/retention/dry-run` | retention.manage |
| Governance | GET | `/api/governance/retention/runs` | retention.manage |
| Governance | POST | `/api/governance/retention/runs/{id:long}/submit` | retention.manage |
| Governance | POST | `/api/governance/retention/runs/{id:long}/approve` | retention.approve |
| Governance | POST | `/api/governance/retention/runs/{id:long}/execute` | retention.manage |
| Governance | GET | `/api/governance/jurisdiction` | jurisdiction.manage |
| Governance | POST | `/api/governance/jurisdiction` | jurisdiction.manage |
| Governance | POST | `/api/governance/jurisdiction/{id:long}/approve` | jurisdiction.approve |
| Governance | POST | `/api/governance/entity-resolution/run` | entity.read |
| Admin | GET | `/api/admin/users` | admin.users |
| Admin | POST | `/api/admin/users` | admin.users |
| Admin | PUT | `/api/admin/users/{id:long}` | admin.users |
| Admin | POST | `/api/admin/users/{id:long}/revoke-sessions` | admin.users |
| Admin | GET | `/api/admin/roles` | admin.users |
| Admin | PUT | `/api/admin/roles/{code}/permissions` | admin.users |
| Admin | GET | `/api/admin/lookups` | admin.lookups |
| Admin | POST | `/api/admin/lookups` | admin.lookups |
| Admin | GET | `/api/admin/settings` | admin.settings |
| Admin | PUT | `/api/admin/settings/{key}` | admin.settings |
| Admin | GET | `/api/admin/fx-rates` | admin.fx |
| Admin | POST | `/api/admin/fx-rates` | admin.fx |
| Admin | PUT | `/api/admin/fx-rates/{id:long}` | admin.fx |
| Admin | GET | `/api/admin/holidays` | admin.settings |
| Admin | POST | `/api/admin/holidays` | admin.settings |
| Admin | GET | `/api/admin/source-systems` | admin.settings |
| Admin | POST | `/api/admin/source-systems/{code}/rotate-secret` | admin.settings |
| Admin | GET | `/api/admin/jobs` | admin.jobs |
| Admin | POST | `/api/admin/jobs/{name}/run` | admin.jobs |
| Admin | GET | `/api/admin/audit` | audit.read |
| Admin | POST | `/api/admin/audit/verify` | audit.read |
| Operations | GET | `/api/operations/health` | operations.read |
| Operations | GET | `/api/operations/ingestion-runs` | operations.read |
| Operations | GET | `/api/operations/ingestion-records` | operations.read |
| Operations | POST | `/api/operations/ingestion-records/{id:long}/replay` | operations.replay |
| Operations | GET | `/api/operations/duplicates` | operations.read |
| Operations | GET | `/api/operations/data-quality` | operations.read |
| Operations | POST | `/api/operations/data-quality/{id:long}/resolve` | operations.replay |
| Operations | GET | `/api/operations/screening-queue` | operations.read |
| Operations | POST | `/api/operations/reconciliation` | operations.read |
| Auth | POST | `/api/auth/login` | anonymous (development only, DevAuth\_\_Enabled) |
| Auth | POST | `/api/auth/token` | anonymous (integration client credentials) |
| Auth | POST | `/api/auth/logout` | (authenticated) |
| Auth | GET | `/api/auth/me` | (authenticated) |
| Customers | GET | `/api/customers` | customers.read |
| Customers | POST | `/api/customers/search` | customers.read |
| Customers | POST | `/api/customers` | customers.write |
| Customers | GET | `/api/customers/{id:long}` | customers.read |
| Customers | PUT | `/api/customers/{id:long}` | customers.write |
| Customers | POST | `/api/customers/{id:long}/status` | customers.write |
| Customers | GET | `/api/customers/{id:long}/attributes` | customers.read |
| Customers | PUT | `/api/customers/{id:long}/attributes` | customers.write |
| Customers | GET | `/api/customers/{id:long}/attributes/history` | customers.read |
| Customers | POST | `/api/customers/{id:long}/identifications` | customers.write |
| Customers | POST | `/api/customers/{id:long}/contacts` | customers.write |
| Customers | GET | `/api/customers/{id:long}/financial-profiles` | customers.read |
| Customers | POST | `/api/customers/{id:long}/financial-profiles` | customers.write |
| Customers | GET | `/api/customers/{id:long}/documents` | customers.read |
| Customers | POST | `/api/customers/{id:long}/documents` | customers.write |
| Customers | GET | `/api/customers/{id:long}/ownership` | customers.read |
| Customers | POST | `/api/customers/{id:long}/ownership` | customers.write |
| Customers | GET | `/api/customers/{id:long}/relationships` | customers.read |
| Customers | GET | `/api/customers/{id:long}/history` | customers.read |
| Customers | GET | `/api/customers/{id:long}/wallets` | customers.read |
| Customers | GET | `/api/customers/{id:long}/transactions` | transactions.read |
| Customers | GET | `/api/customers/{id:long}/risk-assessments` | risk.read |
| Customers | POST | `/api/customers/{id:long}/risk-assessments` | risk.assess |
| Customers | GET | `/api/customers/{id:long}/alerts` | alerts.read |
| Customers | GET | `/api/customers/{id:long}/cases` | cases.read |
| Customers | GET | `/api/customers/{id:long}/screening` | screening.read |
| Customers | POST | `/api/customers/{id:long}/screening` | screening.run |
| Customers | GET | `/api/customers/{id:long}/device-signals` | entity.read |
| Customers | POST | `/api/customers/{id:long}/device-signals` | customers.write |
| Customers | GET | `/api/customers/{id:long}/graph` | entity.read |
| Customers | POST | `/api/customers/bulk-assign` | customers.bulk |
| Customers | POST | `/api/customers/{id:long}/onboard` | cdd.decide |
| Customers | GET | `/api/customers/{id:long}/acceptance` | customers.read |
| Customers | POST | `/api/customers/{id:long}/acceptance/decide` | cdd.decide |
| Customers | POST | `/api/customers/{id:long}/acceptance/manual` | cdd.decide |
| Customers | POST | `/api/customers/{id:long}/acceptance/senior-approval` | cdd.senior-approve |
| Customers | POST | `/api/customers/{id:long}/due-diligence/assign` | cdd.decide |
| Customers | GET | `/api/customers/{id:long}/edd-tasks` | customers.read |
| Customers | POST | `/api/customers/edd-tasks/{taskId:long}/complete` | cdd.decide |
| Customers | GET | `/api/customers/{id:long}/exit` | customers.read |
| Customers | POST | `/api/customers/{id:long}/exit` | customers.exit |
| Customers | POST | `/api/customers/exit/{exitId:long}/decide` | customers.exit.approve |
| Config | GET | `/api/config/customer-fields` | customers.read |
| Config | GET | `/api/config/lookups` | dashboard.read |
| Config | GET | `/api/config/risk-data-sources` | risk.read |
| Config | GET | `/api/config/rule-fields` | rules.read |
| Me | GET | `/api/me/preferences/{key}` | (authenticated) |
| Me | PUT | `/api/me/preferences/{key}` | (authenticated) |
| AdminFields | GET | `/api/admin/customer-fields` | config.fields |
| AdminFields | GET | `/api/admin/customer-fields/{id:long}` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields` | config.fields |
| AdminFields | PUT | `/api/admin/customer-fields/{id:long}` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields/{id:long}/new-version` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields/{id:long}/publish` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields/{id:long}/approve` | config.fields.approve |
| AdminFields | POST | `/api/admin/customer-fields/{id:long}/deactivate` | config.fields |
| AdminFields | GET | `/api/admin/customer-fields/{code}/where-used` | config.fields |
| AdminFields | GET | `/api/admin/customer-fields/{code}/options` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields/{code}/options` | config.fields |
| AdminFields | POST | `/api/admin/customer-fields/options/{optionId:long}/active` | config.fields |
| AdminFields | GET | `/api/admin/customer-fields/{code}/options/{option}/where-used` | config.fields |
| AdminFields | GET | `/api/admin/field-groups` | config.fields |
| AdminFields | POST | `/api/admin/field-groups` | config.fields |
| AdminFields | GET | `/api/admin/form-layouts` | config.layouts |
| AdminFields | POST | `/api/admin/form-layouts` | config.layouts |
| RiskAdmin | GET | `/api/admin/risk-methodologies` | risk.read |
| RiskAdmin | GET | `/api/admin/risk-methodologies/{id:long}` | risk.read |
| RiskAdmin | GET | `/api/admin/risk-methodologies/history/{code}` | risk.read |
| RiskAdmin | POST | `/api/admin/risk-methodologies` | risk.config |
| RiskAdmin | PUT | `/api/admin/risk-methodologies/{id:long}` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/new-version` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/factors` | risk.config |
| RiskAdmin | PUT | `/api/admin/risk-factors/{factorId:long}` | risk.config |
| RiskAdmin | DELETE | `/api/admin/risk-factors/{factorId:long}` | risk.config |
| RiskAdmin | PUT | `/api/admin/risk-methodologies/{id:long}/thresholds` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/overrides` | risk.config |
| RiskAdmin | DELETE | `/api/admin/risk-methodologies/{id:long}/overrides/{overrideId:long}` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/floors` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/validate` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/simulate` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/submit` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/approve` | risk.approve |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/publish` | risk.config |
| RiskAdmin | POST | `/api/admin/risk-methodologies/{id:long}/reject` | risk.approve |
| RiskAdmin | GET | `/api/admin/risk-categories` | risk.read |
| RiskAdmin | POST | `/api/admin/risk-categories` | risk.config |
| Risk | GET | `/api/risk/assessments` | risk.read |
| Risk | GET | `/api/risk/assessments/{id:long}` | risk.read |
| Risk | POST | `/api/risk/assessments/{id:long}/decision` | risk.approve |
| Risk | GET | `/api/risk/overrides` | risk.read |
| Risk | POST | `/api/risk/overrides` | risk.override.request |
| Risk | POST | `/api/risk/overrides/{id:long}/decision` | risk.override.approve |
| Risk | GET | `/api/risk/review-tasks` | risk.read |
| Risk | POST | `/api/risk/review-tasks/{id:long}/complete` | risk.review.work |
| Rules | GET | `/api/admin/aml-rules` | rules.read |
| Rules | GET | `/api/admin/aml-rules/{id:long}` | rules.read |
| Rules | POST | `/api/admin/aml-rules` | rules.config |
| Rules | PUT | `/api/admin/aml-rules/{id:long}/versions/{versionId:long}` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/versions` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/validate` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/simulate` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/submit` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/approve` | rules.approve |
| Rules | POST | `/api/admin/aml-rules/{id:long}/reject` | rules.approve |
| Rules | POST | `/api/admin/aml-rules/{id:long}/publish` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/enabled` | rules.config |
| Rules | POST | `/api/admin/aml-rules/{id:long}/run` | rules.run |
| Rules | POST | `/api/admin/aml-rules/{id:long}/run-backtest` | rules.config |
| Rules | GET | `/api/admin/aml-rules/executions` | rules.read |
| Rules | POST | `/api/admin/aml-rules/{id:long}/tuning` | tuning.run |
| Rules | GET | `/api/admin/aml-rules/{id:long}/tuning` | rules.read |
| Transactions | GET | `/api/transactions` | transactions.read |
| Transactions | GET | `/api/transactions/{id:long}` | transactions.read |
| Transactions | GET | `/api/transactions/{id:long}/related` | transactions.read |
| Transactions | GET | `/api/transactions/{id:long}/raw` | transactions.raw |
| Transactions | POST | `/api/integrations/{source}/transactions` | integrations.ingest |
| Transactions | POST | `/api/realtime/transactions/decide` | realtime.decide |
| Transactions | GET | `/api/admin/wallet-mappings` | admin.wallet-mappings |
| Transactions | POST | `/api/admin/wallet-mappings` | admin.wallet-mappings |
| Transactions | PUT | `/api/admin/wallet-mappings/{id:long}` | admin.wallet-mappings |
| Transactions | POST | `/api/wallets` | customers.write |
| Alerts | GET | `/api/alerts` | alerts.read |
| Alerts | GET | `/api/alerts/{id:long}` | alerts.read |
| Alerts | POST | `/api/alerts/{id:long}/assign` | alerts.work |
| Alerts | POST | `/api/alerts/{id:long}/start-review` | alerts.work |
| Alerts | POST | `/api/alerts/{id:long}/disposition` | alerts.work |
| Alerts | POST | `/api/alerts/{id:long}/approve-closure` | alerts.approve |
| Alerts | POST | `/api/alerts/{id:long}/escalate` | alerts.work |
| Alerts | POST | `/api/alerts/{id:long}/convert-to-case` | cases.work |
| Alerts | POST | `/api/alerts/{id:long}/reopen` | qa.review |
| Cases | GET | `/api/cases` | cases.read |
| Cases | GET | `/api/cases/{id:long}` | cases.read |
| Cases | POST | `/api/cases` | cases.work |
| Cases | PUT | `/api/cases/{id:long}` | cases.work |
| Cases | POST | `/api/cases/{id:long}/link-alert` | cases.work |
| Cases | POST | `/api/cases/{id:long}/link-transaction` | cases.work |
| Cases | POST | `/api/cases/{id:long}/link-customer` | cases.work |
| Cases | POST | `/api/cases/{id:long}/notes` | cases.work |
| Cases | PUT | `/api/cases/{id:long}/notes/{noteId:long}` | cases.work |
| Cases | POST | `/api/cases/{id:long}/transition` | cases.work |
| Cases | POST | `/api/cases/{id:long}/approve` | cases.approve |
| Cases | POST | `/api/cases/{id:long}/tasks` | cases.work |
| Cases | POST | `/api/cases/{id:long}/tasks/{taskId:long}/complete` | cases.work |
| Cases | GET | `/api/cases/my-tasks` | cases.read |
| Cases | POST | `/api/cases/{id:long}/documents` | cases.work |
| Cases | GET | `/api/cases/{id:long}/documents/{docId:long}` | cases.read |
| Cases | GET | `/api/cases/access-report` | audit.read |
| Cases | POST | `/api/cases/{id:long}/reports` | str.prepare |
| RegulatoryReports | GET | `/api/regulatory-reports` | str.prepare |
| RegulatoryReports | GET | `/api/regulatory-reports/{id:long}` | str.prepare |
| RegulatoryReports | PUT | `/api/regulatory-reports/{id:long}` | str.prepare |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/submit-for-review` | str.prepare |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/validate` | str.prepare |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/approve` | str.approve |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/submit` | str.submit |
| RegulatoryReports | GET | `/api/regulatory-reports/{id:long}/export` | str.submit |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/manual-submission` | str.submit |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/acknowledgement` | str.submit |
| RegulatoryReports | POST | `/api/regulatory-reports/{id:long}/withdraw` | str.prepare |
| AuthorityRequests | GET | `/api/authority-requests` | authority.manage |
| AuthorityRequests | GET | `/api/authority-requests/{id:long}` | authority.manage |
| AuthorityRequests | POST | `/api/authority-requests` | authority.manage |
| AuthorityRequests | POST | `/api/authority-requests/{id:long}/respond` | authority.manage |
| Screening | GET | `/api/screening/results` | screening.read |
| Screening | GET | `/api/screening/results/{id:long}` | screening.read |
| Screening | POST | `/api/screening/results/{id:long}/transition` | screening.review |
| Screening | POST | `/api/screening/requests/{id:long}/retry` | screening.run |
| Screening | GET | `/api/watchlists` | screening.read |
| Screening | GET | `/api/watchlists/{code}/versions` | screening.read |
| Screening | GET | `/api/watchlists/{code}/entries` | screening.read |
| Screening | POST | `/api/watchlists/{code}/versions` | watchlists.manage |
| Screening | POST | `/api/screening/provider-simulation` | admin.settings |
| Actions | GET | `/api/compliance-actions` | actions.read |
| Actions | GET | `/api/compliance-actions/{id:long}` | actions.read |
| Actions | POST | `/api/compliance-actions` | actions.request |
| Actions | POST | `/api/compliance-actions/{id:long}/approve` | actions.approve |
| Actions | POST | `/api/compliance-actions/{id:long}/retry` | actions.request |
| Actions | POST | `/api/compliance-actions/reconcile` | actions.request |
| Actions | GET | `/api/compliance-actions/notifications` | actions.read |
| Actions | GET | `/api/holds` | holds.work |
| Actions | POST | `/api/holds/{id:long}/decision` | holds.work |
| Actions | GET | `/api/realtime/decisions` | holds.work |
