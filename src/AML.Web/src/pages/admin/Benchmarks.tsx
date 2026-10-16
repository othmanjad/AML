import { useState } from 'react';
import { Alert } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { cols } from '../../components/display';

const SEGMENT_FIELDS = ['customer.type', 'customer.region', 'customer.residence', 'customer.nationality', 'customer.riskLevel', 'field:BUSINESS_ACTIVITY', 'field:AGENT_MCC', 'field:OCCUPATION'];

/** Industry / segment benchmarks used by the benchmarkDeviation rule aggregate (values with their source, to be confirmed). */
export default function BenchmarksPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  return (
    <Page
      name="benchmarks"
      title={t('nav.benchmarks')}
      extra={
        can('rules.config') && (
          <ActionButton
            testId="benchmark-create"
            type="primary"
            icon={<PlusOutlined />}
            label={t('actions.create')}
            fields={[
              { name: 'metric', label: t('f.metric'), required: true },
              { name: 'segmentField', label: t('f.segmentField'), type: 'select', required: true, options: SEGMENT_FIELDS.map((x) => ({ value: x, label: x })) },
              { name: 'segmentValue', label: t('f.segmentValue'), required: true },
              { name: 'value', label: t('f.value'), type: 'number', required: true },
              { name: 'source', label: t('f.source'), type: 'textarea', required: true },
              { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
            ]}
            initialValues={{ segmentField: 'customer.type' }}
            onSubmit={(v) => api.post('/api/admin/benchmarks', v)}
            onDone={bump}
          />
        )
      }
    >
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('benchmarks.notice')} />
      <DataTable
        testId="benchmarks-table"
        url="/api/admin/benchmarks"
        params={{ includeHistory: true }}
        reloadKey={n}
        columns={[
          ...cols('metric', 'segmentField', 'segmentValue', 'value', 'source', 'effectiveFrom', 'effectiveTo', 'isActive', 'createdBy'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) =>
              can('rules.config') && r.isActive && !r.effectiveTo ? (
                <ConfirmButton size="small" danger testId={`benchmark-deactivate-${r.id}`} label={t('actions.deactivate')} fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/admin/benchmarks/${r.id}/deactivate`, v)} onDone={bump} />
              ) : null,
          },
        ]}
      />
    </Page>
  );
}
