import { useState } from 'react';
import { Button, Tabs } from 'antd';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page, Section } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { col, cols } from '../../components/display';
import { fmtNumber } from '../../utils/format';

const SAMPLE_UPDATE = JSON.stringify(
  { versionLabel: 'TEST-1', publishedAt: new Date().toISOString(), additions: [{ entryId: 'X-1', primaryName: 'Example Listed Person', aliases: [], category: 'Sanctions' }], amendments: [], removals: [] },
  null,
  2,
);

export default function WatchlistsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [sel, setSel] = useState<string | null>(null);
  const [n, setN] = useState(0);
  return (
    <Page name="watchlists" title={t('nav.watchlists')}>
      <Section title={t('watchlists.sources')}>
        <DataTable
          testId="watchlist-sources"
          url="/api/watchlists"
          reloadKey={n}
          columns={[
            col('code', {
              render: (v) => (
                <Button type="link" style={{ padding: 0 }} onClick={() => setSel(v)} data-testid={`watchlist-${v}`}>
                  {v}
                </Button>
              ),
            }),
            ...cols('name', 'listType', 'isMandatory', 'currentVersion', 'isActive', 'isTestData'),
            {
              key: 'actions',
              title: t('f.actions'),
              render: (_: unknown, r: Row) =>
                can('watchlists.manage') ? (
                  <ActionButton
                    size="small"
                    testId={`watchlist-import-${r.code}`}
                    label={t('watchlists.import')}
                    confirm={t('watchlists.confirmImport')}
                    width={760}
                    initialValues={{ payload: SAMPLE_UPDATE }}
                    fields={[{ name: 'payload', label: t('watchlists.updateJson'), type: 'json', required: true, rows: 14 }]}
                    onSubmit={(v) => api.post(`/api/watchlists/${encodeURIComponent(r.code)}/versions`, v.payload)}
                    onDone={() => (setN((x) => x + 1), setSel(r.code))}
                  />
                ) : null,
            },
          ]}
        />
      </Section>
      {sel && (
        <Section title={`${t('watchlists.source')}: ${sel}`} testId="watchlist-detail">
          <Tabs
            destroyOnHidden
            items={[
              {
                key: 'versions',
                label: <span data-testid="tab-versions">{t('watchlists.versions')}</span>,
                children: (
                  <DataTable
                    testId="watchlist-versions"
                    url={`/api/watchlists/${encodeURIComponent(sel)}/versions`}
                    reloadKey={n}
                    columns={[
                      ...cols('versionLabel', 'publishedAt', 'effectiveAt', 'receivedAt', 'additions', 'amendments', 'removals', 'rescreenStartedAt', 'rescreenCompletedAt'),
                      col('timeToScreenMs', { render: (v: number | null) => (v === null || v === undefined ? '—' : `${fmtNumber(v / 1000, 2)} s`) }),
                      ...cols('subjectsScreened', 'matchesRaised'),
                    ]}
                  />
                ),
              },
              {
                key: 'entries',
                label: <span data-testid="tab-entries">{t('watchlists.entries')}</span>,
                children: <DataTable testId="watchlist-entries" url={`/api/watchlists/${encodeURIComponent(sel)}/entries`} reloadKey={n} columns={cols('entryId', 'primaryName', 'aliasesJson', 'dateOfBirth', 'nationality', 'category', 'status', 'versionAdded', 'lastAmendedVersion')} />,
              },
            ]}
          />
        </Section>
      )}
    </Page>
  );
}
