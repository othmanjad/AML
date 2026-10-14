import { useState } from 'react';
import { Space, Tabs } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../api/client';
import type { Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DataTable } from '../components/DataTable';
import { FilterBar, Page } from '../components/Page';
import { ActionButton, ConfirmButton } from '../components/ActionButton';
import { col, cols, tv } from '../components/display';

const ACTION_TYPES = ['FreezeWallet', 'BlockOutgoing', 'BlockIncoming', 'HoldTransaction', 'ReduceLimits', 'RequireKycRefresh', 'SuspendAgent', 'Release', 'ReleaseTransaction', 'RejectTransaction'];
const custLink = (v: number | null) => (v ? <Link to={`/customers/${v}`}>#{v}</Link> : '—');

export default function ActionsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<Query>({});
  const [holdFilters, setHoldFilters] = useState<Query>({});
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const tabs = [];
  if (can('actions.read'))
    tabs.push({
      key: 'actions',
      label: <span data-testid="tab-actions">{t('actions_page.actions')}</span>,
      children: (
        <>
          <FilterBar
            onChange={setFilters}
            fields={[
              { name: 'status', label: t('f.status'), type: 'select', options: ['PendingApproval', 'Approved', 'Dispatched', 'Completed', 'Failed', 'Rejected'].map((x) => ({ value: x, label: tv(x) })) },
              { name: 'customerId', label: t('f.customerId'), type: 'number' },
            ]}
          />
          <DataTable
            testId="actions-table"
            url="/api/compliance-actions"
            params={filters}
            reloadKey={n}
            columns={[
              ...cols('id', 'actionType'),
              col('customerId', { render: custLink }),
              ...cols('walletId', 'transactionId', 'targetReference', 'status', 'requestedBy', 'approvedBy', 'internalReason', 'isAutomatic', 'coreReference', 'attempts', 'lastError', 'reconciliationStatus', 'createdAt'),
              {
                key: 'actions',
                title: t('f.actions'),
                render: (_: unknown, r: Row) => (
                  <Space>
                    {can('actions.approve') && r.status === 'PendingApproval' && (
                      <>
                        <ConfirmButton size="small" type="primary" testId={`action-approve-${r.id}`} label={t('actions.approve')} fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea' }]} onSubmit={(v) => api.post(`/api/compliance-actions/${r.id}/approve`, { approve: true, reason: v.reason })} onDone={bump} />
                        <ConfirmButton size="small" danger testId={`action-reject-${r.id}`} label={t('actions.reject')} fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/compliance-actions/${r.id}/approve`, { approve: false, reason: v.reason })} onDone={bump} />
                      </>
                    )}
                    {can('actions.request') && r.status === 'Failed' && <ActionButton size="small" label={t('actions.retry')} onSubmit={() => api.post(`/api/compliance-actions/${r.id}/retry`)} onDone={bump} />}
                  </Space>
                ),
              },
            ]}
          />
        </>
      ),
    });
  if (can('holds.work'))
    tabs.push({
      key: 'holds',
      label: <span data-testid="tab-holds">{t('actions_page.holds')}</span>,
      children: (
        <>
          <FilterBar onChange={setHoldFilters} fields={[{ name: 'status', label: t('f.status'), type: 'select', options: ['Held', 'Released', 'Rejected', 'Expired'].map((x) => ({ value: x, label: tv(x) })) }]} />
          <DataTable
            testId="holds-table"
            url="/api/holds"
            params={holdFilters}
            reloadKey={n}
            columns={[
              ...cols('id', 'sourceTransactionId', 'decisionId'),
              col('customerId', { render: custLink }),
              ...cols('amount', 'currency', 'reasonCodes', 'status', 'slaDueAt', 'decidedBy', 'decidedAt', 'createdAt'),
              {
                key: 'actions',
                title: t('f.actions'),
                render: (_: unknown, r: Row) =>
                  r.status === 'Held' ? (
                    <Space>
                      <ConfirmButton size="small" type="primary" testId={`hold-release-${r.id}`} label={t('actions_page.release')} fields={[{ name: 'note', label: t('f.notes'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/holds/${r.id}/decision`, { release: true, note: v.note })} onDone={bump} />
                      <ConfirmButton size="small" danger testId={`hold-reject-${r.id}`} label={t('actions.reject')} fields={[{ name: 'note', label: t('f.notes'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/holds/${r.id}/decision`, { release: false, note: v.note })} onDone={bump} />
                    </Space>
                  ) : null,
              },
            ]}
          />
        </>
      ),
    });
  if (can('actions.read'))
    tabs.push({
      key: 'notifications',
      label: <span data-testid="tab-notifications">{t('actions_page.notifications')}</span>,
      children: <DataTable testId="notifications-table" url="/api/compliance-actions/notifications" columns={cols('id', 'customerId', 'authority', 'notificationType', 'status', 'dueAt', 'sentAt', 'createdAt')} />,
    });
  return (
    <Page
      name="actions"
      title={t('nav.actions')}
      extra={
        <>
          {can('actions.request') && (
            <ActionButton
              testId="action-request"
              type="primary"
              icon={<PlusOutlined />}
              label={t('actions_page.request')}
              confirm={t('actions_page.confirmRequest')}
              fields={[
                { name: 'actionType', label: t('f.actionType'), type: 'select', required: true, options: ACTION_TYPES.map((x) => ({ value: x, label: tv(x) })) },
                { name: 'customerId', label: t('f.customerId'), type: 'number' },
                { name: 'walletId', label: t('f.walletId'), type: 'number' },
                { name: 'transactionId', label: t('f.transactionId'), type: 'number' },
                { name: 'caseId', label: t('f.caseId'), type: 'number' },
                { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
              ]}
              onSubmit={(v) => api.post('/api/compliance-actions', { ...v, idempotencyKey: crypto.randomUUID() })}
              onDone={bump}
            />
          )}
          {can('actions.request') && <ActionButton testId="action-reconcile" label={t('actions_page.reconcile')} onSubmit={() => api.post('/api/compliance-actions/reconcile')} onDone={bump} />}
        </>
      }
    >
      <Tabs destroyOnHidden items={tabs} />
    </Page>
  );
}
