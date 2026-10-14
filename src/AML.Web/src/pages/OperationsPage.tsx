import { useState } from 'react';
import { Card, Space, Statistic, Tabs } from 'antd';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../api/client';
import type { Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DataTable } from '../components/DataTable';
import { FilterBar, Page, Section } from '../components/Page';
import { Async } from '../components/StateViews';
import { useApi } from '../components/hooks';
import { ActionButton, ConfirmButton } from '../components/ActionButton';
import { KV, cols, tv } from '../components/display';
import { fmtNumber } from '../utils/format';

function Health() {
  const { t } = useTranslation();
  const st = useApi<Row>('/api/operations/health');
  return (
    <Async state={st}>
      {(h) => (
        <div data-testid="ops-health">
          <div className="kpi-grid">
            <Card size="small">
              <Statistic title={t('ops.outboxPending')} value={fmtNumber(h.outbox?.pending)} />
            </Card>
            <Card size="small">
              <Statistic title={t('ops.outboxPublished')} value={fmtNumber(h.outbox?.published)} />
            </Card>
            <Card size="small">
              <Statistic title={t('ops.retryQueue')} value={fmtNumber((h.retryQueue ?? []).length)} />
            </Card>
            <Card size="small">
              <Statistic title={t('ops.operationalAlerts')} value={fmtNumber((h.operationalAlerts ?? []).length)} />
            </Card>
          </div>
          <Section title={t('ops.jobs')} testId="ops-jobs">
            <DataTable data={h.jobs ?? []} rowKey="jobName" rowClassName={(r: Row) => (r.stale || r.consecutiveFailures ? 'row-overdue' : '')} columns={cols('jobName', 'lastRunAt', 'lastStatus', 'lastDurationMs', 'consecutiveFailures', 'stale', 'lastError')} />
          </Section>
          <Section title={t('ops.dataFreshness')} testId="ops-freshness">
            <DataTable data={h.dataFreshness ?? []} rowKey="source" columns={cols('source', 'lastIngestedAt', 'lastTransactionAt')} />
          </Section>
          <Section title={t('ops.screeningProviders')} testId="ops-providers">
            <DataTable data={h.screeningProviders ?? []} rowKey="providerCode" columns={cols('providerCode', 'isAvailable', 'simulationMode', 'lastSuccessAt', 'lastFailureAt', 'lastError')} />
          </Section>
          <Section title={t('ops.retryQueue')} testId="ops-retry">
            <DataTable data={h.retryQueue ?? []} rowKey={(r) => JSON.stringify(r)} columns={Object.keys((h.retryQueue ?? [])[0] ?? { type: 1, count: 1 }).map((k) => cols(k)[0])} />
          </Section>
          <Section title={t('ops.ingestionStats')} testId="ops-ingestion">
            <DataTable data={h.ingestion ?? []} rowKey="status" columns={cols('status', 'count')} />
          </Section>
          <Section title={t('ops.ruleExecutions')}>
            <DataTable data={h.ruleExecutions ?? []} columns={cols('ruleCode', 'ruleVersion', 'mode', 'status', 'matches', 'errorCount', 'startedAt', 'correlationId')} />
          </Section>
          <Section title={t('ops.integrations')}>
            <KV data={h.integrations ?? {}} keys={Object.keys(h.integrations ?? {})} />
          </Section>
        </div>
      )}
    </Async>
  );
}

export default function OperationsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [dq, setDq] = useState<Query>({});
  const [recStatus, setRecStatus] = useState<Query>({ status: 'DeadLetter' });
  const [n, setN] = useState(0);
  const [recon, setRecon] = useState<Row[] | null>(null);
  const bump = () => setN((x) => x + 1);
  return (
    <Page name="operations" title={t('nav.operations')}>
      <Tabs
        destroyOnHidden
        items={[
          { key: 'health', label: <span data-testid="tab-health">{t('ops.health')}</span>, children: <Health /> },
          {
            key: 'dq',
            label: <span data-testid="tab-data-quality">{t('ops.dataQuality')}</span>,
            children: (
              <>
                <FilterBar
                  onChange={setDq}
                  fields={[
                    { name: 'type', label: t('f.issueType'), type: 'select', options: ['UnresolvedParty', 'PayloadConflict', 'MissingFxRate', 'CircularOwnership', 'MissingBeneficialOwner', 'UnlinkedReversal', 'FeedGap', 'ScreeningUnavailable', 'PaymentTransparency'].map((x) => ({ value: x, label: tv(x) })) },
                    { name: 'status', label: t('f.status'), type: 'select', options: ['Open', 'Resolved'].map((x) => ({ value: x, label: tv(x) })) },
                  ]}
                />
                <DataTable
                  testId="dq-issues"
                  url="/api/operations/data-quality"
                  params={dq}
                  reloadKey={n}
                  columns={[
                    ...cols('id', 'issueType', 'entityType', 'entityId', 'description', 'severity', 'status', 'createdAt', 'resolvedBy', 'resolvedAt'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Row) =>
                        can('operations.replay') && r.status === 'Open' ? (
                          <ActionButton size="small" label={t('ops.resolve')} fields={[{ name: 'note', label: t('f.notes'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/operations/data-quality/${r.id}/resolve`, v)} onDone={bump} />
                        ) : null,
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'deadletter',
            label: <span data-testid="tab-dead-letter">{t('ops.deadLetter')}</span>,
            children: (
              <>
                <FilterBar
                  initial={recStatus}
                  onChange={(q) => setRecStatus(q)}
                  fields={[
                    { name: 'status', label: t('f.status'), type: 'select', options: ['DeadLetter', 'Failed', 'Quarantined', 'Processed', 'Duplicate', 'Received'].map((x) => ({ value: x, label: tv(x) })) },
                    { name: 'runId', label: t('f.runId'), type: 'number' },
                  ]}
                />
                <DataTable
                  testId="dead-letter-records"
                  url="/api/operations/ingestion-records"
                  params={recStatus}
                  reloadKey={n}
                  columns={[
                    ...cols('id', 'runId', 'sourceSystem', 'sourceTransactionId', 'status', 'attemptCount', 'lastError', 'correlationId', 'receivedAt', 'processedAt', 'replayedBy'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Row) =>
                        can('operations.replay') && r.status !== 'Processed' ? (
                          <ConfirmButton
                            size="small"
                            testId={`replay-${r.id}`}
                            label={t('ops.replay')}
                            fields={[
                              { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
                              { name: 'correctedPayload', label: t('ops.correctedPayload'), type: 'json' },
                            ]}
                            onSubmit={(v) => api.post(`/api/operations/ingestion-records/${r.id}/replay`, v)}
                            onDone={bump}
                          />
                        ) : null,
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'runs',
            label: <span data-testid="tab-ingestion-runs">{t('ops.ingestionRuns')}</span>,
            children: <DataTable testId="ingestion-runs" url="/api/operations/ingestion-runs" columns={cols('id', 'sourceSystem', 'batchId', 'status', 'received', 'processed', 'duplicates', 'conflicts', 'errors', 'startedAt', 'completedAt')} />,
          },
          {
            key: 'duplicates',
            label: <span data-testid="tab-duplicates">{t('ops.duplicates')}</span>,
            children: <DataTable testId="duplicates" url="/api/operations/duplicates" rowKey={(r) => JSON.stringify(r)} columns={cols('sourceSystem', 'sourceTransactionId', 'count', 'firstReceivedAt', 'lastReceivedAt')} />,
          },
          {
            key: 'recon',
            label: <span data-testid="tab-reconciliation">{t('ops.reconciliation')}</span>,
            children: (
              <>
                <Space style={{ marginBottom: 12 }}>
                  <ActionButton
                    testId="reconcile-run"
                    type="primary"
                    label={t('ops.runReconciliation')}
                    fields={[
                      { name: 'source', label: t('f.sourceSystem'), type: 'select', options: ['CORE', 'WALLET'].map((x) => ({ value: x, label: x })) },
                      { name: 'from', label: t('f.dateFrom'), type: 'date', required: true },
                      { name: 'to', label: t('f.dateTo'), type: 'date', required: true },
                    ]}
                    onSubmit={(v) => api.post<Row[]>('/api/operations/reconciliation', v)}
                    onDone={(r: Row[]) => setRecon(r)}
                  />
                </Space>
                {recon && (
                  <DataTable
                    testId="reconciliation-results"
                    data={recon}
                    rowKey={(r) => `${r.sourceSystem}-${r.businessDate}-${r.currency}`}
                    rowClassName={(r: Row) => (r.status !== 'Matched' ? 'row-overdue' : '')}
                    columns={cols('sourceSystem', 'businessDate', 'currency', 'sourceCount', 'sourceAmount', 'importedCount', 'importedAmount', 'countDifference', 'amountDifference', 'status')}
                  />
                )}
              </>
            ),
          },
        ]}
      />
    </Page>
  );
}
