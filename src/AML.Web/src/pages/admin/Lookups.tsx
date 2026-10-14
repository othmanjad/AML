import { useState } from 'react';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { cols } from '../../components/display';
import type { FieldSpec } from '../../components/FormFields';

const CATEGORIES = ['ALERT_SEVERITY', 'ALERT_STATUS', 'CASE_STATUS', 'COUNTRY', 'CURRENCY', 'CUSTOMER_STATUS', 'CUSTOMER_TYPE', 'RISK_LEVEL', 'TXN_STATUS', 'TXN_TYPE', 'WALLET_TYPE'];

export default function LookupsPage() {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Query>({});
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const fields: FieldSpec[] = [
    { name: 'category', label: t('f.category'), type: 'tags', required: true, options: CATEGORIES.map((x) => ({ value: x, label: x })) },
    { name: 'code', label: t('f.code'), required: true },
    { name: 'labelEn', label: t('f.labelEn'), required: true },
    { name: 'labelAr', label: t('f.labelAr'), required: true },
    { name: 'sortOrder', label: t('f.sortOrder'), type: 'number' },
    { name: 'attribute', label: t('f.attribute') },
    { name: 'isActive', label: t('f.isActive'), type: 'switch' },
  ];
  const norm = (v: Row) => ({ ...v, category: Array.isArray(v.category) ? v.category[v.category.length - 1] : v.category });
  return (
    <Page
      name="lookups"
      title={t('nav.lookups')}
      extra={
        <ActionButton testId="lookup-create" type="primary" icon={<PlusOutlined />} label={t('actions.create')} fields={fields} initialValues={{ isActive: true, sortOrder: 0 }} onSubmit={(v) => api.post('/api/admin/lookups', norm(v))} onDone={bump} />
      }
    >
      <FilterBar onChange={setFilters} fields={[{ name: 'category', label: t('f.category'), type: 'select', options: CATEGORIES.map((x) => ({ value: x, label: x })) }]} />
      <DataTable
        testId="lookups-table"
        url="/api/admin/lookups"
        params={filters}
        reloadKey={n}
        columns={[
          ...cols('category', 'code', 'labelEn', 'labelAr', 'sortOrder', 'attribute', 'isActive'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) => (
              <ActionButton size="small" label={t('actions.edit')} fields={fields} initialValues={{ ...r, category: [r.category] }} onSubmit={(v) => api.post('/api/admin/lookups', { ...norm(v), id: r.id })} onDone={bump} />
            ),
          },
        ]}
      />
    </Page>
  );
}
