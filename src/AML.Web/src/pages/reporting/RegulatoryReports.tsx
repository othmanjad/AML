import { useState } from 'react';
import { Alert, Button, Form, Input, List, Space, Tag, message } from 'antd';
import { DownloadOutlined } from '@ant-design/icons';
import { Link, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page, Section } from '../../components/Page';
import { Async, ErrorAlert } from '../../components/StateViews';
import { useApi } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { KV, StatusTag, col, cols, tv } from '../../components/display';
import { fmtAmount } from '../../utils/format';

const STATUSES = ['Draft', 'UnderReview', 'Approved', 'Submitted', 'Acknowledged', 'RejectedByFiu', 'Amended', 'Withdrawn'];

export function RegulatoryReportsList() {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Query>({});
  return (
    <Page name="regulatory-reports" title={t('nav.regulatoryReports')}>
      <FilterBar onChange={setFilters} fields={[{ name: 'status', label: t('f.status'), type: 'select', options: STATUSES.map((x) => ({ value: x, label: tv(x) })) }]} />
      <DataTable
        testId="reports-table"
        url="/api/regulatory-reports"
        params={filters}
        columns={[
          col('reportNumber', { link: (r) => `/regulatory-reports/${r.id}`, sort: 'reportNumber' }),
          ...cols('reportType', 'status', 'confidentiality'),
          col('caseId', { render: (v) => <Link to={`/cases/${v}`}>#{v}</Link> }),
          ...cols('preparedBy', 'approvedBy', 'dueAt', 'isOverdue', 'submittedAt', 'fiuReference', 'isTestData'),
        ]}
      />
    </Page>
  );
}

function NarrativeEditor({ r, onSaved }: { r: Row; onSaved: () => void }) {
  const { t } = useTranslation();
  const [err, setErr] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  const editable = r.status === 'Draft';
  return (
    <Form
      layout="vertical"
      initialValues={{ narrative: r.narrative, indicatorCodes: (r.indicators ?? []).join(', ') }}
      onFinish={async (v) => {
        setBusy(true);
        setErr(null);
        try {
          await api.put(`/api/regulatory-reports/${r.id}`, {
            narrative: v.narrative,
            indicatorCodes: String(v.indicatorCodes ?? '')
              .split(',')
              .map((s: string) => s.trim())
              .filter(Boolean),
            rowVersion: r.rowVersion,
          });
          message.success(t('msg.saved'));
          onSaved();
        } catch (e) {
          setErr(e as ApiError);
        } finally {
          setBusy(false);
        }
      }}
    >
      <ErrorAlert error={err} />
      <Form.Item name="narrative" label={t('f.narrative')}>
        <Input.TextArea rows={10} data-testid="report-narrative" readOnly={!editable} />
      </Form.Item>
      <Form.Item name="indicatorCodes" label={t('f.indicators')}>
        <Input data-testid="report-indicators" readOnly={!editable} />
      </Form.Item>
      {editable && (
        <Button type="primary" htmlType="submit" loading={busy} data-testid="report-save">
          {t('actions.save')}
        </Button>
      )}
    </Form>
  );
}

export function RegulatoryReportDetail() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const st = useApi<Row>(`/api/regulatory-reports/${id}`);
  const [validation, setValidation] = useState<Row | null>(null);
  return (
    <Async state={st}>
      {(r) => {
        const reload = st.reload;
        const submitAllowed = !!r.submitAllowed;
        return (
          <Page
            name="regulatory-report-detail"
            title={`${r.reportNumber} (${tv(r.reportType)})`}
            subTitle={
              <Space wrap>
                <StatusTag value={r.status} testId="report-status" />
                <Tag>{tv(r.confidentiality)}</Tag>
                {r.isTestData && <Tag color="gold">{t('common.testData')}</Tag>}
                {r.isOverdue && <Tag color="red">{tv('Overdue')}</Tag>}
              </Space>
            }
            extra={
              <>
                <ActionButton testId="report-validate" label={t('actions.validate')} onSubmit={() => api.post<Row>(`/api/regulatory-reports/${r.id}/validate`)} onDone={(x: Row) => setValidation(x)} />
                {r.status === 'Draft' && <ActionButton testId="report-submit-review" label={t('reports.submitForReview')} onSubmit={() => api.post(`/api/regulatory-reports/${r.id}/submit-for-review`)} onDone={reload} />}
                {can('str.approve') && r.status === 'UnderReview' && (
                  <ConfirmButton testId="report-approve" type="primary" label={t('actions.approve')} fields={[{ name: 'comment', label: t('f.comment'), type: 'textarea' }]} onSubmit={(v) => api.post(`/api/regulatory-reports/${r.id}/approve`, v)} onDone={reload} />
                )}
                {can('str.submit') && (
                  <ConfirmButton
                    testId="report-submit"
                    type="primary"
                    danger
                    label={t('reports.submitToFiu')}
                    disabled={!submitAllowed}
                    tooltip={submitAllowed ? undefined : t('reports.submitNotAllowed')}
                    onSubmit={() => api.post(`/api/regulatory-reports/${r.id}/submit`)}
                    onDone={reload}
                  />
                )}
                {can('str.submit') && (
                  <Button
                    icon={<DownloadOutlined />}
                    data-testid="report-export"
                    onClick={() =>
                      api.download(`/api/regulatory-reports/${r.id}/export`, `report-${r.id}.xml`).catch((e: ApiError) => message.error(`${e.detail ?? e.message}${e.correlationId ? ` — ${t('state.reference')}: ${e.correlationId}` : ''}`))
                    }
                  >
                    {t('reports.exportXml')}
                  </Button>
                )}
                {can('str.submit') && (
                  <ActionButton
                    testId="report-ack"
                    label={t('reports.recordAck')}
                    fields={[
                      { name: 'fiuReference', label: t('f.fiuReference'), required: true },
                      { name: 'accepted', label: t('f.accepted'), type: 'switch' },
                      { name: 'details', label: t('f.details'), type: 'textarea' },
                    ]}
                    initialValues={{ accepted: true }}
                    onSubmit={(v) => api.post(`/api/regulatory-reports/${r.id}/acknowledgement`, { ...v, accepted: !!v.accepted })}
                    onDone={reload}
                  />
                )}
                {can('str.submit') && (
                  <ConfirmButton
                    testId="report-manual"
                    label={t('reports.manualSubmission')}
                    fields={[
                      { name: 'evidence', label: t('f.evidence'), type: 'textarea', required: true },
                      { name: 'fiuReference', label: t('f.fiuReference') },
                    ]}
                    onSubmit={(v) => api.post(`/api/regulatory-reports/${r.id}/manual-submission`, v)}
                    onDone={reload}
                  />
                )}
                {r.status !== 'Withdrawn' && (
                  <ConfirmButton testId="report-withdraw" danger label={t('reports.withdraw')} fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/regulatory-reports/${r.id}/withdraw`, v)} onDone={reload} />
                )}
              </>
            }
          >
            {!submitAllowed && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('reports.submitNotAllowed')} data-testid="submit-not-allowed" />}
            {validation && (
              <Alert
                data-testid="validation-result"
                closable
                onClose={() => setValidation(null)}
                style={{ marginBottom: 12 }}
                type={validation.valid ? 'success' : 'error'}
                showIcon
                message={validation.valid ? t('msg.valid') : t('msg.invalid')}
                description={(validation.errors as unknown[])?.length ? <List size="small" data-testid="validation-errors" dataSource={validation.errors as unknown[]} renderItem={(e) => <List.Item>{typeof e === 'string' ? e : JSON.stringify(e)}</List.Item>} /> : undefined}
              />
            )}
            <Section title={t('reports.details')}>
              <KV
                data={r}
                keys={['reportType', 'status', 'caseId', 'preparedBy', 'reviewedBy', 'approvedBy', 'approvedAt', 'suspicionFormedAt', 'dueAt', 'submittedBy', 'submittedAt', 'submissionChannel', 'payloadHash', 'fiuReference', 'acknowledgedAt', 'acknowledgementDetails', 'manualSubmissionEvidence', 'previousReportId', 'validationErrors']}
                render={{ caseId: (v) => <Link to={`/cases/${v}`}>#{v}</Link> }}
              />
            </Section>
            <Section title={t('reports.narrative')}>
              <NarrativeEditor r={r} onSaved={reload} />
            </Section>
            <Section title={t('reports.subjects')}>
              <DataTable testId="report-subjects" rowKey="customerId" data={r.subjects ?? []} columns={[col('customerId', { link: (x) => `/customers/${x.customerId}` }), ...cols('name', 'nameArabic', 'nationality', 'accounts')]} />
            </Section>
            <Section title={t('nav.transactions')}>
              <DataTable
                testId="report-transactions"
                rowKey="reference"
                data={r.transactions ?? []}
                columns={[...cols('reference', 'TimestampUtc', 'type'), col('Amount', { render: (v, x) => fmtAmount(v, x.Currency) }), ...cols('from', 'to')]}
              />
            </Section>
          </Page>
        );
      }}
    </Async>
  );
}
