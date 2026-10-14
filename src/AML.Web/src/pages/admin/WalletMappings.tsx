import { useState } from 'react';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { cols, tv } from '../../components/display';
import type { FieldSpec } from '../../components/FormFields';

const CATEGORIES = ['Clearing', 'Settlement', 'Fee', 'Suspense', 'Agent', 'Merchant', 'Treasury'];

export default function WalletMappingsPage() {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const fields = (edit: boolean): FieldSpec[] => [
    { name: 'sourceSystem', label: t('f.sourceSystem'), type: 'select', required: true, options: ['CORE', 'WALLET'].map((x) => ({ value: x, label: x })) },
    { name: 'externalWalletId', label: t('f.externalWalletId'), required: true },
    { name: 'mappingCategory', label: t('f.mappingCategory'), type: 'select', required: true, options: CATEGORIES.map((x) => ({ value: x, label: tv(x) })) },
    { name: 'channel', label: t('f.channel') },
    { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
    { name: 'effectiveTo', label: t('f.effectiveTo'), type: 'datetime' },
    { name: 'isActive', label: t('f.isActive'), type: 'switch' },
    { name: 'notes', label: t('f.notes'), type: 'textarea' },
    ...(edit ? [{ name: 'reason', label: t('f.reason'), type: 'textarea' as const, required: true }] : []),
  ];
  return (
    <Page
      name="wallet-mappings"
      title={t('nav.walletMappings')}
      extra={
        <ActionButton
          testId="wallet-mapping-create"
          type="primary"
          icon={<PlusOutlined />}
          label={t('actions.create')}
          fields={fields(false)}
          initialValues={{ sourceSystem: 'CORE', mappingCategory: 'Clearing', isActive: true }}
          onSubmit={(v) => api.post('/api/admin/wallet-mappings', v)}
          onDone={bump}
        />
      }
    >
      <DataTable
        testId="wallet-mappings-table"
        url="/api/admin/wallet-mappings"
        reloadKey={n}
        columns={[
          ...cols('sourceSystem', 'externalWalletId', 'mappingCategory', 'channel', 'effectiveFrom', 'effectiveTo', 'isActive', 'notes', 'ruleVersion', 'updatedBy', 'updatedAt'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, r: Row) => (
              <ActionButton
                size="small"
                testId={`wallet-mapping-edit-${r.id}`}
                label={t('actions.edit')}
                fields={fields(true)}
                initialValues={r}
                onSubmit={(v) => api.put(`/api/admin/wallet-mappings/${r.id}`, { ...v, rowVersion: r.rowVersion })}
                onDone={bump}
              />
            ),
          },
        ]}
      />
    </Page>
  );
}
