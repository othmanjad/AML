import { useState } from 'react';
import { Button, Space, Tabs, Tag, Upload, message } from 'antd';
import { DownloadOutlined, PlusOutlined, UploadOutlined } from '@ant-design/icons';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page, Section } from '../../components/Page';
import { Async } from '../../components/StateViews';
import { useApi, useLookupOptions } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { KV, StatusTag, col, cols, customerName, tv } from '../../components/display';
import { caseColumns } from '../customers/ProfileTabs';
import { fmtAmount } from '../../utils/format';

const PRIORITIES = ['Low', 'Medium', 'High', 'Critical'];
const CONF = ['Standard', 'Restricted', 'StrictlyConfidential'];
const lv = (xs: string[]) => xs.map((x) => ({ value: x, label: tv(x) }));

export function CasesList() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  const [filters, setFilters] = useState<Query>({});
  const statuses = useLookupOptions('CASE_STATUS');
  return (
    <Page
      name="cases"
      title={t('nav.cases')}
      extra={
        can('cases.work') && (
          <ActionButton
            testId="case-create"
            type="primary"
            icon={<PlusOutlined />}
            label={t('cases.create')}
            fields={[
              { name: 'title', label: t('f.title'), required: true },
              { name: 'caseType', label: t('f.caseType'), type: 'select', options: lv(['Investigation', 'EDD', 'SanctionsReview', 'AuthorityRequest']) },
              { name: 'priority', label: t('f.priority'), type: 'select', options: lv(PRIORITIES) },
              { name: 'confidentiality', label: t('f.confidentiality'), type: 'select', options: lv(CONF) },
              { name: 'primaryCustomerId', label: t('f.primaryCustomerId'), type: 'number' },
              { name: 'assignedInvestigator', label: t('f.assignedInvestigator') },
              { name: 'dueDate', label: t('f.dueDate'), type: 'datetime' },
              { name: 'summary', label: t('f.summary'), type: 'textarea' },
            ]}
            initialValues={{ caseType: 'Investigation', priority: 'Medium', confidentiality: 'Standard' }}
            onSubmit={(v) => api.post<Row>('/api/cases', v)}
            onDone={(r: Row) => nav(`/cases/${r?.case?.id ?? r?.id}`)}
          />
        )
      }
    >
      <FilterBar
        testId="case-filters"
        onChange={setFilters}
        fields={[
          { name: 'search', label: t('actions.search') },
          { name: 'status', label: t('f.status'), type: 'select', options: statuses },
          { name: 'customerId', label: t('f.customerId'), type: 'number' },
        ]}
      />
      <DataTable testId="cases-table" url="/api/cases" params={filters} columns={caseColumns()} defaultSort={{ sort: 'createdAt', dir: 'desc' }} rowClassName={(r: Row) => (r.isOverdue ? 'row-overdue' : '')} />
    </Page>
  );
}

export function CaseDetail() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  const st = useApi<Row>(`/api/cases/${id}`);
  const [uploading, setUploading] = useState(false);
  return (
    <Async state={st}>
      {(d) => {
        const c = d.case as Row;
        const reload = st.reload;
        const work = can('cases.work');
        const notes = (d.notes ?? []) as Row[];
        const superseded = new Set(notes.map((n) => n.supersedesNoteId).filter(Boolean));
        return (
          <Page
            name="case-detail"
            title={`${c.caseNumber} — ${c.title}`}
            subTitle={
              <Space wrap>
                <StatusTag value={c.status} testId="case-status" />
                <StatusTag value={c.priority} />
                <Tag>{tv(c.confidentiality)}</Tag>
                {c.isOverdue && <Tag color="red">{tv('Overdue')}</Tag>}
              </Space>
            }
            extra={
              <>
                {work &&
                  ((d.allowedTransitions ?? []) as string[]).map((s) => (
                    <ActionButton
                      key={s}
                      testId={`case-transition-${s}`}
                      label={t('cases.moveTo', { status: tv(s) })}
                      confirm={s === 'Closed' ? t('cases.confirmClose') : undefined}
                      fields={[
                        { name: 'comment', label: t('f.comment'), type: 'textarea' },
                        ...(s === 'Closed' || s === 'PendingApproval'
                          ? [
                              { name: 'finalDecision', label: t('f.finalDecision'), type: 'select' as const, options: lv(['NoFurtherAction', 'EnhancedMonitoring', 'ExitRelationship', 'FileReport']) },
                              { name: 'closureReason', label: t('f.closureReason'), type: 'textarea' as const },
                            ]
                          : []),
                      ]}
                      onSubmit={(v) => api.post(`/api/cases/${c.id}/transition`, { ...v, toStatus: s, rowVersion: c.rowVersion })}
                      onDone={reload}
                    />
                  ))}
                {can('cases.approve') && c.status === 'PendingApproval' && (
                  <ConfirmButton
                    testId="case-approve"
                    type="primary"
                    label={t('actions.approve')}
                    fields={[
                      { name: 'approve', label: t('f.approve'), type: 'switch' },
                      { name: 'comment', label: t('f.comment'), type: 'textarea' },
                    ]}
                    initialValues={{ approve: true }}
                    onSubmit={(v) => api.post(`/api/cases/${c.id}/approve`, { approve: !!v.approve, comment: v.comment })}
                    onDone={reload}
                  />
                )}
                {can('str.prepare') && (
                  <ActionButton
                    testId="case-create-str"
                    label={t('cases.createStr')}
                    fields={[{ name: 'reportType', label: t('f.reportType'), type: 'select', options: lv(['STR', 'SAR', 'TFS']) }]}
                    initialValues={{ reportType: 'STR' }}
                    onSubmit={(v) => api.post<Row>(`/api/cases/${c.id}/reports`, v)}
                    onDone={(r: Row) => (r?.id ? nav(`/regulatory-reports/${r.id}`) : reload())}
                  />
                )}
              </>
            }
          >
            <Tabs
              className="wrap-tabs"
              items={[
                {
                  key: 'overview',
                  label: <span data-testid="tab-overview">{t('tabs.overview')}</span>,
                  children: (
                    <Section
                      title={t('tabs.overview')}
                      extra={
                        work && (
                          <ActionButton
                            testId="case-edit"
                            label={t('actions.edit')}
                            initialValues={c}
                            fields={[
                              { name: 'title', label: t('f.title') },
                              { name: 'priority', label: t('f.priority'), type: 'select', options: lv(PRIORITIES) },
                              { name: 'summary', label: t('f.summary'), type: 'textarea' },
                              { name: 'findings', label: t('f.findings'), type: 'textarea' },
                              { name: 'recommendedAction', label: t('f.recommendedAction'), type: 'select', options: lv(['NoFurtherAction', 'EnhancedMonitoring', 'ExitRelationship', 'FileReport']) },
                              { name: 'assignedInvestigator', label: t('f.assignedInvestigator') },
                              { name: 'reviewer', label: t('f.reviewer') },
                              { name: 'dueDate', label: t('f.dueDate'), type: 'datetime' },
                              { name: 'confidentiality', label: t('f.confidentiality'), type: 'select', options: lv(CONF) },
                            ]}
                            onSubmit={(v) => api.put(`/api/cases/${c.id}`, { ...v, rowVersion: c.rowVersion })}
                            onDone={reload}
                          />
                        )
                      }
                    >
                      <KV
                        data={c}
                        keys={['caseType', 'status', 'priority', 'confidentiality', 'primaryCustomerId', 'assignedInvestigator', 'reviewer', 'team', 'dueDate', 'summary', 'findings', 'recommendedAction', 'finalDecision', 'closureReason', 'suspicionFormedAt', 'approvalStatus', 'approvedBy', 'createdAt', 'createdBy', 'closedAt']}
                        render={{ primaryCustomerId: (v) => (v ? <Link to={`/customers/${v}`}>#{v}</Link> : '—') }}
                      />
                    </Section>
                  ),
                },
                {
                  key: 'alerts',
                  label: <span data-testid="tab-alerts">{t('tabs.alerts')}</span>,
                  children: (
                    <Section
                      title={t('tabs.alerts')}
                      extra={work && <ActionButton testId="link-alert" label={t('cases.linkAlert')} fields={[{ name: 'alertId', label: t('f.alertId'), type: 'number', required: true }]} onSubmit={(v) => api.post(`/api/cases/${c.id}/link-alert`, v)} onDone={reload} />}
                    >
                      <DataTable testId="case-alerts" data={d.alerts ?? []} columns={[col('alertNumber', { link: (r) => `/alerts/${r.id}` }), ...cols('title', 'severity', 'status', 'ruleCode', 'linkedAt')]} />
                    </Section>
                  ),
                },
                {
                  key: 'transactions',
                  label: <span data-testid="tab-transactions">{t('tabs.transactions')}</span>,
                  children: (
                    <Section
                      title={t('tabs.transactions')}
                      extra={work && <ActionButton testId="link-transaction" label={t('cases.linkTransaction')} fields={[{ name: 'transactionId', label: t('f.transactionId'), type: 'number', required: true }]} onSubmit={(v) => api.post(`/api/cases/${c.id}/link-transaction`, v)} onDone={reload} />}
                    >
                      <DataTable testId="case-transactions" data={d.transactions ?? []} columns={[col('sourceTransactionId', { link: (r) => `/transactions/${r.id}` }), ...cols('transactionType', 'timestampUtc', 'status'), col('amount', { render: (v, r) => fmtAmount(v, r.currency) })]} />
                    </Section>
                  ),
                },
                {
                  key: 'customers',
                  label: <span data-testid="tab-customers">{t('tabs.customers')}</span>,
                  children: (
                    <Section
                      title={t('tabs.customers')}
                      extra={
                        work && (
                          <ActionButton
                            testId="link-customer"
                            label={t('cases.linkCustomer')}
                            fields={[
                              { name: 'customerId', label: t('f.customerId'), type: 'number', required: true },
                              { name: 'role', label: t('f.role') },
                            ]}
                            onSubmit={(v) => api.post(`/api/cases/${c.id}/link-customer`, v)}
                            onDone={reload}
                          />
                        )
                      }
                    >
                      <DataTable testId="case-customers" data={d.customers ?? []} columns={[col('id', { link: (r) => `/customers/${r.id}` }), col('name', { render: (_, r) => customerName(r) }), ...cols('customerType', 'role')]} />
                    </Section>
                  ),
                },
                {
                  key: 'notes',
                  label: <span data-testid="tab-notes">{t('tabs.notes')}</span>,
                  children: (
                    <Section
                      title={t('tabs.notes')}
                      extra={work && <ActionButton testId="note-add" label={t('cases.addNote')} fields={[{ name: 'text', label: t('f.text'), type: 'textarea', required: true, rows: 5 }]} onSubmit={(v) => api.post(`/api/cases/${c.id}/notes`, v)} onDone={reload} />}
                    >
                      <p className="ant-typography-secondary">{t('cases.notesImmutable')}</p>
                      <DataTable
                        testId="case-notes"
                        data={notes}
                        rowClassName={(n: Row) => (superseded.has(n.id) ? 'row-overdue' : '')}
                        columns={[
                          ...cols('id', 'author', 'createdAt'),
                          col('text', { render: (v, n) => <span style={{ whiteSpace: 'pre-wrap' }}>{v}{superseded.has(n.id) && <Tag style={{ marginInlineStart: 6 }}>{t('cases.superseded')}</Tag>}</span> }),
                          col('supersedesNoteId'),
                          {
                            key: 'actions',
                            title: t('f.actions'),
                            render: (_: unknown, n: Row) =>
                              work ? (
                                <ActionButton
                                  size="small"
                                  testId={`note-correct-${n.id}`}
                                  label={t('cases.correct')}
                                  initialValues={{ text: n.text }}
                                  fields={[{ name: 'text', label: t('f.text'), type: 'textarea', required: true, rows: 5 }]}
                                  onSubmit={(v) => api.post(`/api/cases/${c.id}/notes`, { text: v.text, supersedesNoteId: n.id })}
                                  onDone={reload}
                                />
                              ) : null,
                          },
                        ]}
                      />
                    </Section>
                  ),
                },
                {
                  key: 'tasks',
                  label: <span data-testid="tab-tasks">{t('tabs.tasks')}</span>,
                  children: (
                    <Section
                      title={t('tabs.tasks')}
                      extra={
                        work && (
                          <ActionButton
                            testId="task-add"
                            label={t('cases.addTask')}
                            fields={[
                              { name: 'title', label: t('f.title'), required: true },
                              { name: 'assignee', label: t('f.assignee') },
                              { name: 'dueDate', label: t('f.dueDate'), type: 'datetime' },
                            ]}
                            onSubmit={(v) => api.post(`/api/cases/${c.id}/tasks`, v)}
                            onDone={reload}
                          />
                        )
                      }
                    >
                      <DataTable
                        testId="case-tasks"
                        data={d.tasks ?? []}
                        rowClassName={(r: Row) => (r.isOverdue ? 'row-overdue' : '')}
                        columns={[
                          ...cols('title', 'assignee', 'dueDate', 'status', 'completedAt'),
                          col('isOverdue', { render: (v: boolean) => (v ? <Tag color="red" data-testid="task-overdue">{tv('Overdue')}</Tag> : '—') }),
                          {
                            key: 'actions',
                            title: t('f.actions'),
                            render: (_: unknown, r: Row) => (work && r.status !== 'Completed' ? <ActionButton size="small" label={t('actions.complete')} onSubmit={() => api.post(`/api/cases/${c.id}/tasks/${r.id}/complete`)} onDone={reload} /> : null),
                          },
                        ]}
                      />
                    </Section>
                  ),
                },
                {
                  key: 'documents',
                  label: <span data-testid="tab-documents">{t('tabs.documents')}</span>,
                  children: (
                    <Section
                      title={t('tabs.documents')}
                      extra={
                        work && (
                          <Upload
                            showUploadList={false}
                            customRequest={async ({ file, onSuccess, onError }) => {
                              setUploading(true);
                              try {
                                await api.upload(`/api/cases/${c.id}/documents`, file as File);
                                message.success(t('msg.done'));
                                onSuccess?.({});
                                reload();
                              } catch (e) {
                                const err = e as ApiError;
                                message.error(`${err.detail ?? err.message}${err.correlationId ? ` — ${t('state.reference')}: ${err.correlationId}` : ''}`);
                                onError?.(err);
                              } finally {
                                setUploading(false);
                              }
                            }}
                          >
                            <Button icon={<UploadOutlined />} loading={uploading} data-testid="document-upload">
                              {t('cases.upload')}
                            </Button>
                          </Upload>
                        )
                      }
                    >
                      <DataTable
                        testId="case-documents"
                        data={d.documents ?? []}
                        columns={[
                          ...cols('fileName', 'contentType', 'sizeBytes', 'sha256', 'scanStatus', 'uploadedBy', 'uploadedAt'),
                          {
                            key: 'actions',
                            title: t('f.actions'),
                            render: (_: unknown, r: Row) => (
                              <Button size="small" icon={<DownloadOutlined />} onClick={() => api.download(`/api/cases/${c.id}/documents/${r.id}`, r.fileName).catch((e: ApiError) => message.error(e.detail ?? e.message))}>
                                {t('actions.download')}
                              </Button>
                            ),
                          },
                        ]}
                      />
                    </Section>
                  ),
                },
                {
                  key: 'reports',
                  label: <span data-testid="tab-reports">{t('tabs.reports')}</span>,
                  children: <DataTable testId="case-reports" data={d.reports ?? []} columns={[col('reportNumber', { link: (r) => `/regulatory-reports/${r.id}` }), ...cols('reportType', 'status')]} />,
                },
                {
                  key: 'history',
                  label: <span data-testid="tab-history">{t('tabs.history')}</span>,
                  children: <DataTable testId="case-history" data={d.history ?? []} columns={cols('fromStatus', 'toStatus', 'actor', 'at', 'comment')} />,
                },
              ]}
            />
          </Page>
        );
      }}
    </Async>
  );
}
