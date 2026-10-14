import { useState } from 'react';
import { Space, Tag } from 'antd';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page, Section } from '../../components/Page';
import { Async } from '../../components/StateViews';
import { useApi, useLookupOptions } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { JsonBlock, KV, StatusTag, col, cols, tv } from '../../components/display';
import { alertColumns } from '../customers/ProfileTabs';
import { fmtAmount, fmtDateTime } from '../../utils/format';

export const RESOLUTION_CODES = ['Closed-NoSuspicionIdentified', 'Closed-FalsePositive', 'Closed-Duplicate', 'Closed-Other'];
const OPEN = ['New', 'Assigned', 'UnderReview', 'PendingInformation', 'Escalated'];

export function AlertsList() {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Query>({});
  const statuses = useLookupOptions('ALERT_STATUS');
  const severities = useLookupOptions('ALERT_SEVERITY');
  return (
    <Page name="alerts" title={t('nav.alerts')}>
      <FilterBar
        testId="alert-filters"
        onChange={setFilters}
        fields={[
          { name: 'status', label: t('f.status'), type: 'select', options: statuses },
          { name: 'severity', label: t('f.severity'), type: 'select', options: severities },
          { name: 'ruleCode', label: t('f.ruleCode') },
          { name: 'assignedTo', label: t('f.assignedTo') },
          { name: 'customerId', label: t('f.customerId'), type: 'number' },
          { name: 'overdue', label: t('f.isOverdue'), type: 'switch' },
          { name: 'open', label: t('alerts.openOnly'), type: 'switch' },
        ]}
      />
      <DataTable testId="alerts-table" url="/api/alerts" params={filters} columns={alertColumns()} defaultSort={{ sort: 'createdAt', dir: 'desc' }} rowClassName={(r: Row) => (r.isOverdue ? 'row-overdue' : '')} />
    </Page>
  );
}

const conditionCols = () => cols('Expression', 'Actual', 'Expected', 'Result');

function Evidence({ ev }: { ev: Row }) {
  const { t } = useTranslation();
  const inner = (ev?.evidence ?? {}) as Row;
  return (
    <div data-testid="alert-evidence">
      <h4>{t('alerts.matchedConditions')}</h4>
      <DataTable testId="matched-conditions" data={(ev?.matchedConditions ?? []) as Row[]} rowKey="Expression" columns={conditionCols()} />
      <h4 style={{ marginTop: 12 }}>{t('alerts.window')}</h4>
      <KV data={inner} keys={['windowType', 'windowStartUtc', 'windowEndUtc', 'windowStartLocal', 'windowEndLocal', 'timeZone', 'count', 'triggerTransaction']} testId="window-boundaries" />
      <h4 style={{ marginTop: 12 }}>{t('alerts.contributingTransactions')}</h4>
      <DataTable
        testId="contributing-transactions"
        data={(inner.transactions ?? []) as Row[]}
        columns={[
          col('sourceTransactionId', { link: (r) => `/transactions/${r.id}` }),
          ...cols('timestampUtc', 'direction', 'status'),
          col('originalAmount', { render: (v, r) => fmtAmount(v, r.currency) }),
          ...cols('currency', 'fxRate', 'fxRateDate', 'fxStatus'),
          col('baseAmount', { render: (v, r) => fmtAmount(v, r.baseCurrency) }),
          ...cols('reversedAmount'),
        ]}
      />
      {ev?.rule && (
        <>
          <h4 style={{ marginTop: 12 }}>{t('alerts.ruleDefinition')}</h4>
          <JsonBlock value={ev.rule} />
        </>
      )}
    </div>
  );
}

export function AlertDetail() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  const st = useApi<Row>(`/api/alerts/${id}`);
  return (
    <Async state={st}>
      {(d) => {
        const a = d.alert as Row;
        const rv = a.rowVersion;
        const open = OPEN.includes(a.status);
        const reload = st.reload;
        const work = can('alerts.work');
        return (
          <Page
            name="alert-detail"
            title={`${a.alertNumber} — ${a.title}`}
            subTitle={
              <Space wrap>
                <StatusTag value={a.severity} testId="alert-severity" />
                <StatusTag value={a.status} testId="alert-status" />
                {a.isOverdue && <Tag color="red">{tv('Overdue')}</Tag>}
                {a.caseId && <Link to={`/cases/${a.caseId}`}>{t('alerts.linkedCase', { id: a.caseId })}</Link>}
              </Space>
            }
            extra={
              <>
                {work && open && (
                  <ActionButton
                    testId="alert-assign"
                    label={t('alerts.assign')}
                    fields={[
                      { name: 'assignee', label: t('f.assignedTo'), required: true },
                      { name: 'team', label: t('f.team') },
                    ]}
                    initialValues={{ assignee: a.assignedTo }}
                    onSubmit={(v) => api.post(`/api/alerts/${a.id}/assign`, { ...v, rowVersion: rv })}
                    onDone={reload}
                  />
                )}
                {work && ['New', 'Assigned', 'PendingInformation', 'Escalated'].includes(a.status) && (
                  <ActionButton testId="alert-start-review" label={t('alerts.startReview')} onSubmit={() => api.post(`/api/alerts/${a.id}/start-review`, { rowVersion: rv })} onDone={reload} />
                )}
                {work && open && (
                  <ActionButton
                    testId="alert-disposition"
                    type="primary"
                    label={t('alerts.disposition')}
                    fields={[
                      { name: 'resolutionCode', label: t('f.resolutionCode'), type: 'select', required: true, options: RESOLUTION_CODES.map((x) => ({ value: x, label: tv(x) })) },
                      { name: 'notes', label: t('f.notes'), type: 'textarea', required: true },
                      { name: 'reviewedTransactions', label: t('alerts.reviewedTransactions'), type: 'checkbox', testId: 'check-reviewedTransactions' },
                      { name: 'reviewedCustomerProfile', label: t('alerts.reviewedCustomerProfile'), type: 'checkbox', testId: 'check-reviewedCustomerProfile' },
                    ]}
                    onSubmit={(v) =>
                      api.post(`/api/alerts/${a.id}/disposition`, {
                        resolutionCode: v.resolutionCode,
                        notes: v.notes,
                        checklist: { reviewedTransactions: !!v.reviewedTransactions, reviewedCustomerProfile: !!v.reviewedCustomerProfile },
                        rowVersion: rv,
                      })
                    }
                    onDone={reload}
                  />
                )}
                {can('alerts.approve') && a.status === 'PendingClosureApproval' && (
                  <ConfirmButton
                    testId="alert-approve-closure"
                    label={t('alerts.approveClosure')}
                    fields={[
                      { name: 'approve', label: t('f.approve'), type: 'switch' },
                      { name: 'comment', label: t('f.comment'), type: 'textarea' },
                    ]}
                    initialValues={{ approve: true }}
                    onSubmit={(v) => api.post(`/api/alerts/${a.id}/approve-closure`, { approve: !!v.approve, comment: v.comment, rowVersion: rv })}
                    onDone={reload}
                  />
                )}
                {work && open && (
                  <ActionButton
                    testId="alert-escalate"
                    label={t('alerts.escalate')}
                    fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]}
                    onSubmit={(v) => api.post(`/api/alerts/${a.id}/escalate`, { ...v, rowVersion: rv })}
                    onDone={reload}
                  />
                )}
                {can('cases.work') && !a.caseId && open && (
                  <ActionButton
                    testId="alert-convert"
                    label={t('alerts.convertToCase')}
                    fields={[
                      { name: 'title', label: t('f.title') },
                      { name: 'confidentiality', label: t('f.confidentiality'), type: 'select', options: ['Standard', 'Restricted', 'StrictlyConfidential'].map((x) => ({ value: x, label: tv(x) })) },
                    ]}
                    onSubmit={(v) => api.post<Row>(`/api/alerts/${a.id}/convert-to-case`, v)}
                    onDone={(r: Row) => (r?.id ? nav(`/cases/${r.id}`) : reload())}
                  />
                )}
                {can('qa.review') && !open && a.status !== 'ConvertedToCase' && (
                  <ConfirmButton
                    testId="alert-reopen"
                    label={t('alerts.reopen')}
                    fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]}
                    onSubmit={(v) => api.post(`/api/alerts/${a.id}/reopen`, v)}
                    onDone={reload}
                  />
                )}
              </>
            }
          >
            <Section title={t('alerts.summary')}>
              <KV
                data={a}
                keys={['source', 'alertType', 'ruleCode', 'ruleVersion', 'primaryCustomerId', 'assignedTo', 'assignedTeam', 'createdAt', 'dueAt', 'closedAt', 'resolutionCode', 'resolutionNotes', 'pendingClosureStatus', 'escalationReason', 'confidentiality', 'reopenCount', 'description']}
                render={{ primaryCustomerId: (v) => (v ? <Link to={`/customers/${v}`}>#{v}</Link> : '—') }}
              />
            </Section>
            <Section title={t('alerts.evidence')}>
              <Evidence ev={a.evidence} />
            </Section>
            <Section title={t('alerts.matches')}>
              <DataTable
                testId="alert-matches"
                data={(d.matches ?? []) as Row[]}
                expandable={{ expandedRowRender: (m: Row) => <DataTable data={m.matchedConditions ?? []} rowKey="Expression" columns={conditionCols()} /> }}
                columns={cols('matchKey', 'ruleVersion', 'windowStart', 'windowEnd', 'matchedAt')}
              />
            </Section>
            <Section title={t('nav.transactions')}>
              <DataTable
                testId="alert-transactions"
                data={(d.transactions ?? []) as Row[]}
                columns={[
                  col('sourceTransactionId', { link: (r) => `/transactions/${r.id}` }),
                  ...cols('transactionType', 'status', 'timestampUtc'),
                  col('amount', { render: (v, r) => fmtAmount(v, r.currency) }),
                  ...cols('fxRate'),
                  col('baseAmount', { render: (v, r) => fmtAmount(v, r.baseCurrency) }),
                ]}
              />
            </Section>
            <Section title={t('nav.customers')}>
              <DataTable testId="alert-customers" rowKey="customerId" data={(d.customers ?? []) as Row[]} columns={[col('customerId', { link: (r) => `/customers/${r.customerId}` }), col('role')]} />
            </Section>
            <Section title={t('alerts.statusHistory')}>
              <DataTable testId="alert-history" data={(d.history ?? []) as Row[]} columns={[...cols('fromStatus', 'toStatus', 'actor'), col('at', { render: (v) => fmtDateTime(v) }), ...cols('reason', 'resolutionCode', 'notes')]} />
            </Section>
          </Page>
        );
      }}
    </Async>
  );
}
