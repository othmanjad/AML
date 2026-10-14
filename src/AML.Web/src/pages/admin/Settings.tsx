import { useState } from 'react';
import { Tabs } from 'antd';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { cols } from '../../components/display';

export default function SettingsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  return (
    <Page name="settings" title={t('nav.settings')}>
      <Tabs
        destroyOnHidden
        items={[
          {
            key: 'settings',
            label: <span data-testid="tab-settings">{t('settings.system')}</span>,
            children: (
              <DataTable
                testId="settings-table"
                url="/api/admin/settings"
                rowKey="key"
                reloadKey={n}
                columns={[
                  ...cols('key', 'value', 'description', 'updatedBy', 'updatedAt'),
                  {
                    key: 'actions',
                    title: t('f.actions'),
                    render: (_: unknown, r: Row) => (
                      <ActionButton
                        size="small"
                        testId={`setting-edit-${r.key}`}
                        label={t('actions.edit')}
                        fields={[
                          { name: 'value', label: t('f.value'), required: true },
                          { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
                        ]}
                        initialValues={{ value: r.value }}
                        onSubmit={(v) => api.put(`/api/admin/settings/${encodeURIComponent(r.key)}`, v)}
                        onDone={bump}
                      />
                    ),
                  },
                ]}
              />
            ),
          },
          {
            key: 'holidays',
            label: <span data-testid="tab-holidays">{t('settings.holidays')}</span>,
            children: (
              <>
                <div style={{ marginBottom: 12 }}>
                  <ActionButton
                    testId="holiday-add"
                    label={t('actions.add')}
                    fields={[
                      { name: 'date', label: t('f.date'), type: 'date', required: true },
                      { name: 'name', label: t('f.name'), required: true },
                    ]}
                    onSubmit={(v) => api.post('/api/admin/holidays', v)}
                    onDone={bump}
                  />
                </div>
                <DataTable testId="holidays-table" url="/api/admin/holidays" reloadKey={n} columns={cols('date', 'name')} />
              </>
            ),
          },
          {
            key: 'sources',
            label: <span data-testid="tab-source-systems">{t('settings.sourceSystems')}</span>,
            children: (
              <DataTable
                testId="source-systems"
                url="/api/admin/source-systems"
                rowKey="code"
                reloadKey={n}
                columns={[
                  ...cols('code', 'name', 'clientId', 'isActive', 'secretRotatedAt'),
                  {
                    key: 'actions',
                    title: t('f.actions'),
                    render: (_: unknown, r: Row) => <ConfirmButton size="small" danger label={t('settings.rotateSecret')} onSubmit={() => api.post(`/api/admin/source-systems/${encodeURIComponent(r.code)}/rotate-secret`)} onDone={bump} />,
                  },
                ]}
              />
            ),
          },
          ...(can('admin.jobs')
            ? [
                {
                  key: 'jobs',
                  label: <span data-testid="tab-jobs">{t('settings.jobs')}</span>,
                  children: (
                    <DataTable
                      testId="jobs-table"
                      url="/api/admin/jobs"
                      rowKey="name"
                      columns={[
                        ...cols('name', 'interval', 'perTenant'),
                        { key: 'actions', title: t('f.actions'), render: (_: unknown, r: Row) => <ConfirmButton size="small" label={t('settings.runNow')} onSubmit={() => api.post(`/api/admin/jobs/${encodeURIComponent(r.name)}/run`)} /> },
                      ]}
                    />
                  ),
                },
              ]
            : []),
          ...(can('retention.manage')
            ? [
                {
                  key: 'retention',
                  label: <span data-testid="tab-retention">{t('settings.retention')}</span>,
                  children: (
                    <>
                      <div style={{ marginBottom: 12 }}>
                        <ActionButton testId="retention-dry-run" label={t('settings.dryRun')} fields={[{ name: 'asOf', label: t('f.asOf'), type: 'datetime' }]} onSubmit={(v) => api.post('/api/governance/retention/dry-run', v)} onDone={bump} />
                      </div>
                      <DataTable testId="retention-policies" url="/api/governance/retention/policies" columns={cols('recordClass', 'retentionMonths', 'trigger', 'isActive')} />
                      <div style={{ height: 12 }} />
                      <DataTable
                        testId="retention-runs"
                        url="/api/governance/retention/runs"
                        reloadKey={n}
                        columns={[
                          ...cols('id', 'status', 'asOf', 'candidateCount', 'requestedBy', 'approvedBy', 'executedAt'),
                          {
                            key: 'actions',
                            title: t('f.actions'),
                            render: (_: unknown, r: Row) => (
                              <>
                                {r.status === 'DryRun' && <ActionButton size="small" label={t('actions.submit')} onSubmit={() => api.post(`/api/governance/retention/runs/${r.id}/submit`)} onDone={bump} />}
                                {r.status === 'PendingApproval' && can('retention.approve') && <ConfirmButton size="small" label={t('actions.approve')} onSubmit={() => api.post(`/api/governance/retention/runs/${r.id}/approve`)} onDone={bump} />}
                                {r.status === 'Approved' && <ConfirmButton size="small" danger label={t('settings.execute')} onSubmit={() => api.post(`/api/governance/retention/runs/${r.id}/execute`)} onDone={bump} />}
                              </>
                            ),
                          },
                        ]}
                      />
                    </>
                  ),
                },
              ]
            : []),
        ]}
      />
    </Page>
  );
}
