import { useState } from 'react';
import { Space, Switch } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { cols, tv } from '../../components/display';
import { useLookupOptions } from '../../components/hooks';

export default function FxRatesPage() {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const [history, setHistory] = useState(false);
  const currencies = useLookupOptions('CURRENCY');
  const bump = () => setN((x) => x + 1);
  return (
    <Page
      name="fx-rates"
      title={t('nav.fx')}
      extra={
        <>
          <Space>
            <label htmlFor="fx-history">{t('fx.includeHistory')}</label>
            <Switch id="fx-history" checked={history} onChange={setHistory} data-testid="fx-history" />
          </Space>
          <ActionButton
            testId="fx-create"
            type="primary"
            icon={<PlusOutlined />}
            label={t('actions.add')}
            fields={[
              { name: 'fromCurrency', label: t('f.fromCurrency'), type: 'select', options: currencies, required: true },
              { name: 'toCurrency', label: t('f.toCurrency'), type: 'select', options: currencies, required: true },
              { name: 'rate', label: t('f.rate'), type: 'number', required: true },
              { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime', required: true },
              { name: 'source', label: t('f.source'), required: true },
              { name: 'rateType', label: t('f.rateType'), type: 'select', options: ['Mid', 'Buy', 'Sell'].map((x) => ({ value: x, label: tv(x) })) },
            ]}
            initialValues={{ toCurrency: 'JOD', rateType: 'Mid' }}
            onSubmit={(v) => api.post('/api/admin/fx-rates', v)}
            onDone={bump}
          />
        </>
      }
    >
      <DataTable
        testId="fx-table"
        url="/api/admin/fx-rates"
        params={{ includeHistory: history || undefined }}
        reloadKey={n}
        columns={[
          ...cols('fromCurrency', 'toCurrency', 'rate', 'rateType', 'source', 'effectiveFrom', 'version', 'isCurrent', 'supersedesId', 'isTestData', 'createdBy', 'createdAt'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) =>
              r.isCurrent ? (
                <ActionButton
                  size="small"
                  testId={`fx-edit-${r.id}`}
                  label={t('actions.edit')}
                  description={<p>{t('fx.editCreatesVersion')}</p>}
                  fields={[
                    { name: 'rate', label: t('f.rate'), type: 'number', required: true },
                    { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
                  ]}
                  initialValues={{ rate: r.rate }}
                  onSubmit={(v) => api.put(`/api/admin/fx-rates/${r.id}`, v)}
                  onDone={bump}
                />
              ) : null,
          },
        ]}
      />
    </Page>
  );
}
