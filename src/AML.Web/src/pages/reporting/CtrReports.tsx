import { useState } from 'react';
import { Alert, Button, Space, message } from 'antd';
import { DownloadOutlined, PlusOutlined } from '@ant-design/icons';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { col, cols, tv } from '../../components/display';
import { fmtAmount } from '../../utils/format';

const STATUSES = ['Draft', 'Approved', 'Exported', 'Submitted', 'Rejected'];

/** Cash transaction reports: generated drafts, MLRO approval, export and manual submission (never sent automatically). */
export default function CtrReportsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<Query>({});
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const download = (r: Row, format: 'xml' | 'csv') =>
    api
      .download(`/api/ctr-reports/${r.id}/export?format=${format}`, `${r.reportNumber}.${format}`)
      .then(bump)
      .catch((e: ApiError) => message.error(`${e.detail ?? e.message}${e.correlationId ? ` — ${t('state.reference')}: ${e.correlationId}` : ''}`));
  return (
    <Page
      name="ctr-reports"
      title={t('nav.ctrReports')}
      extra={
        <ActionButton
          testId="ctr-generate"
          type="primary"
          icon={<PlusOutlined />}
          label={t('ctr.generate')}
          fields={[{ name: 'businessDate', label: t('f.businessDate'), type: 'date', required: true }]}
          onSubmit={async (v) => {
            const r = await api.post<Row>('/api/ctr-reports/generate', v);
            message.success(t('ctr.generated', { created: r.created, updated: r.updated, threshold: fmtAmount(r.threshold, r.baseCurrency) }));
            return r;
          }}
          onDone={bump}
        />
      }
    >
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('ctr.notice')} data-testid="ctr-notice" />
      <FilterBar
        onChange={setFilters}
        fields={[
          { name: 'status', label: t('f.status'), type: 'select', options: STATUSES.map((x) => ({ value: x, label: tv(x) })) },
          { name: 'from', label: t('f.from'), type: 'date' },
          { name: 'to', label: t('f.to'), type: 'date' },
        ]}
      />
      <DataTable
        testId="ctr-table"
        url="/api/ctr-reports"
        params={filters}
        reloadKey={n}
        columns={[
          ...cols('reportNumber', 'businessDate'),
          col('customerName', { render: (v, r) => <Link to={`/customers/${r.customerId}`}>{v ?? `#${r.customerId}`}</Link> }),
          col('totalBaseAmount', { render: (v, r) => fmtAmount(v, r.baseCurrency) }),
          ...cols('transactionCount', 'status', 'dueDate', 'submissionReference'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) => (
              <Space wrap size="small">
                {can('str.approve') && r.status === 'Draft' && (
                  <>
                    <ActionButton size="small" testId={`ctr-approve-${r.id}`} label={t('actions.approve')} fields={[{ name: 'comment', label: t('f.comment'), type: 'textarea' }]} onSubmit={(v) => api.post(`/api/ctr-reports/${r.id}/approve`, v)} onDone={bump} />
                    <ConfirmButton size="small" danger testId={`ctr-reject-${r.id}`} label={t('actions.reject')} fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/ctr-reports/${r.id}/reject`, v)} onDone={bump} />
                  </>
                )}
                {can('str.submit') && ['Approved', 'Exported', 'Submitted'].includes(r.status) && (
                  <>
                    <Button size="small" icon={<DownloadOutlined />} data-testid={`ctr-xml-${r.id}`} onClick={() => download(r, 'xml')}>
                      XML
                    </Button>
                    <Button size="small" icon={<DownloadOutlined />} data-testid={`ctr-csv-${r.id}`} onClick={() => download(r, 'csv')}>
                      CSV
                    </Button>
                  </>
                )}
                {can('str.submit') && r.status === 'Exported' && !r.isTestData && (
                  <ConfirmButton size="small" testId={`ctr-submit-${r.id}`} label={t('ctr.recordSubmission')} fields={[{ name: 'reference', label: t('f.reference'), required: true }]} onSubmit={(v) => api.post(`/api/ctr-reports/${r.id}/manual-submission`, v)} onDone={bump} />
                )}
              </Space>
            ),
          },
        ]}
      />
    </Page>
  );
}
