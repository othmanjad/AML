import { useState } from 'react';
import { PlusOutlined } from '@ant-design/icons';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { col, cols, tv } from '../../components/display';

export default function AuthorityRequests() {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  return (
    <Page
      name="authority-requests"
      title={t('nav.authorityRequests')}
      extra={
        <ActionButton
          testId="authority-create"
          type="primary"
          icon={<PlusOutlined />}
          label={t('actions.create')}
          fields={[
            { name: 'reference', label: t('f.reference'), required: true },
            { name: 'authority', label: t('f.authority'), required: true },
            { name: 'requestType', label: t('f.requestType'), type: 'select', options: ['InformationRequest', 'FreezeOrder', 'ProductionOrder', 'MonitoringOrder'].map((x) => ({ value: x, label: tv(x) })) },
            { name: 'customerId', label: t('f.customerId'), type: 'number' },
            { name: 'scope', label: t('f.scope'), type: 'textarea', required: true },
            { name: 'dueDate', label: t('f.dueDate'), type: 'datetime', required: true },
            { name: 'assignedOfficer', label: t('f.assignedOfficer') },
            { name: 'confidentiality', label: t('f.confidentiality'), type: 'select', options: ['Restricted', 'StrictlyConfidential'].map((x) => ({ value: x, label: tv(x) })) },
          ]}
          initialValues={{ requestType: 'InformationRequest', confidentiality: 'StrictlyConfidential' }}
          onSubmit={(v) => api.post('/api/authority-requests', v)}
          onDone={bump}
        />
      }
    >
      <DataTable
        testId="authority-table"
        url="/api/authority-requests"
        reloadKey={n}
        rowClassName={(r: Row) => (r.isOverdue ? 'row-overdue' : '')}
        columns={[
          ...cols('reference', 'authority', 'requestType'),
          col('customerId', { render: (v) => (v ? <Link to={`/customers/${v}`}>#{v}</Link> : '—') }),
          ...cols('scope', 'receivedAt', 'dueDate', 'assignedOfficer', 'status', 'isOverdue', 'respondedAt', 'response'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) =>
              r.status !== 'Responded' && r.status !== 'Closed' ? (
                <ActionButton
                  size="small"
                  testId={`authority-respond-${r.id}`}
                  label={t('authority.respond')}
                  confirm
                  fields={[
                    { name: 'response', label: t('f.response'), type: 'textarea', required: true },
                    { name: 'evidence', label: t('f.evidence'), type: 'textarea' },
                  ]}
                  onSubmit={(v) => api.post(`/api/authority-requests/${r.id}/respond`, v)}
                  onDone={bump}
                />
              ) : null,
          },
        ]}
      />
    </Page>
  );
}
