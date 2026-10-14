import { useState } from 'react';
import { Tabs } from 'antd';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { cols, tv } from '../../components/display';
import { SCREENING_TRANSITIONS, ScreeningResultsTable } from '../customers/ProfileTabs';

export default function ScreeningPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<Query>({});
  const [n, setN] = useState(0);
  const statuses = Object.keys(SCREENING_TRANSITIONS);
  return (
    <Page name="screening" title={t('nav.screening')}>
      <Tabs
        destroyOnHidden
        items={[
          {
            key: 'results',
            label: <span data-testid="tab-results">{t('screening.results')}</span>,
            children: (
              <>
                <FilterBar
                  testId="screening-filters"
                  onChange={setFilters}
                  fields={[
                    { name: 'status', label: t('f.status'), type: 'select', options: statuses.map((s) => ({ value: s, label: tv(s) })) },
                    { name: 'category', label: t('f.category'), type: 'select', options: ['Sanctions', 'PEP', 'AdverseMedia', 'Terrorism', 'ProliferationFinancing', 'Internal'].map((s) => ({ value: s, label: tv(s) })) },
                  ]}
                />
                <ScreeningResultsTable testId="screening-results" url="/api/screening/results" params={filters} reloadKey={n} onChanged={() => setN((x) => x + 1)} />
              </>
            ),
          },
          ...(can('operations.read')
            ? [
                {
                  key: 'queue',
                  label: <span data-testid="tab-provider-queue">{t('screening.providerQueue')}</span>,
                  children: (
                    <DataTable
                      testId="screening-queue"
                      url="/api/operations/screening-queue"
                      reloadKey={n}
                      columns={[
                        ...cols('subjectName', 'subjectType', 'trigger', 'status', 'attempts', 'nextAttemptAt', 'lastError', 'createdAt'),
                        {
                          key: 'actions',
                          title: t('f.actions'),
                          render: (_: unknown, r: Row) => (can('screening.run') ? <ActionButton size="small" label={t('actions.retry')} onSubmit={() => api.post(`/api/screening/requests/${r.id}/retry`)} onDone={() => setN((x) => x + 1)} /> : null),
                        },
                      ]}
                    />
                  ),
                },
                {
                  key: 'providers',
                  label: <span data-testid="tab-providers">{t('screening.providers')}</span>,
                  children: <DataTable testId="screening-providers" url="/api/operations/health" rowKey="providerCode" transform={(h: Row) => h.screeningProviders ?? []} columns={cols('providerCode', 'isAvailable', 'simulationMode', 'lastSuccessAt', 'lastFailureAt', 'lastError')} />,
                },
              ]
            : []),
        ]}
      />
    </Page>
  );
}
