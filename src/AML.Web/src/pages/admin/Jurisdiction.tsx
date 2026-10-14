import { useState } from 'react';
import { Tag } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { col, cols, tv } from '../../components/display';

export default function JurisdictionPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  return (
    <Page
      name="jurisdiction"
      title={t('nav.jurisdiction')}
      extra={
        <ActionButton
          testId="jurisdiction-propose"
          type="primary"
          icon={<PlusOutlined />}
          label={t('jurisdiction.propose')}
          fields={[
            { name: 'key', label: t('f.key'), required: true },
            { name: 'value', label: t('f.value'), required: true },
            { name: 'unit', label: t('f.unit') },
            { name: 'legalSource', label: t('f.legalSource'), required: true },
            { name: 'articleReference', label: t('f.articleReference'), required: true },
            { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
            { name: 'complianceOwner', label: t('f.complianceOwner') },
          ]}
          onSubmit={(v) => api.post('/api/governance/jurisdiction', v)}
          onDone={bump}
        />
      }
    >
      <DataTable
        testId="jurisdiction-table"
        url="/api/governance/jurisdiction"
        reloadKey={n}
        columns={[
          ...cols('jurisdiction', 'key', 'value', 'unit'),
          col('status', { render: (v: string) => <Tag color={v === 'Approved' || v === 'Active' ? 'green' : v === 'ToBeConfirmed' ? 'orange' : 'blue'} data-testid="jurisdiction-status" data-value={v}>{tv(v)}</Tag> }),
          ...cols('legalSource', 'articleReference', 'effectiveFrom', 'effectiveTo', 'complianceOwner', 'approvedBy', 'approvedAt', 'createdBy'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) =>
              can('jurisdiction.approve') && r.status !== 'Approved' && r.status !== 'Superseded' ? (
                <ConfirmButton size="small" type="primary" testId={`jurisdiction-approve-${r.id}`} label={t('actions.approve')} onSubmit={() => api.post(`/api/governance/jurisdiction/${r.id}/approve`)} onDone={bump} />
              ) : null,
          },
        ]}
      />
    </Page>
  );
}
