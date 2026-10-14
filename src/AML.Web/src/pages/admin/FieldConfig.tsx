import { useState } from 'react';
import { Button, Drawer, Input, Space, Tabs } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page, Section } from '../../components/Page';
import { useApi } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { SampleTag, col, cols, tv } from '../../components/display';
import type { FieldSpec } from '../../components/FormFields';

const DATA_TYPES = ['ShortText', 'LongText', 'Integer', 'Decimal', 'Date', 'DateTime', 'Boolean', 'SingleSelect', 'MultiSelect', 'Country', 'Currency', 'Phone', 'Email', 'Identifier', 'DocumentReference'];
const CTYPES = ['Individual', 'Corporate', 'Agent', 'Merchant'];
const STATUSES = ['Draft', 'PendingApproval', 'Published', 'Superseded', 'Inactive'];
const lv = (xs: string[]) => xs.map((x) => ({ value: x, label: tv(x) }));
const split = (s?: string | null) => (s ? s.split(',').filter(Boolean) : []);

function OptionsPanel({ code }: { code: string }) {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  return (
    <Section
      title={t('fields.options')}
      extra={
        <ActionButton
          testId="option-add"
          label={t('actions.add')}
          fields={[
            { name: 'optionCode', label: t('f.optionCode'), required: true },
            { name: 'labelEn', label: t('f.labelEn'), required: true },
            { name: 'labelAr', label: t('f.labelAr') },
            { name: 'sortOrder', label: t('f.sortOrder'), type: 'number' },
          ]}
          initialValues={{ sortOrder: 0 }}
          onSubmit={(v) => api.post(`/api/admin/customer-fields/${encodeURIComponent(code)}/options`, v)}
          onDone={bump}
        />
      }
    >
      <DataTable
        testId="field-options"
        url={`/api/admin/customer-fields/${encodeURIComponent(code)}/options`}
        reloadKey={n}
        columns={[
          ...cols('optionCode', 'labelEn', 'labelAr', 'sortOrder', 'isActive'),
          {
            key: 'actions',
            title: t('f.actions'),
            render: (_: unknown, o: Row) => (
              <Space>
                <ConfirmButton
                  size="small"
                  danger={o.isActive}
                  label={o.isActive ? t('actions.deactivate') : t('actions.activate')}
                  onSubmit={() => api.post(`/api/admin/customer-fields/options/${o.id}/active`, { active: !o.isActive })}
                  onDone={bump}
                />
              </Space>
            ),
          },
        ]}
      />
      <Section title={t('fields.optionWhereUsed')}>
        <OptionWhereUsed code={code} />
      </Section>
    </Section>
  );
}

function OptionWhereUsed({ code }: { code: string }) {
  const { t } = useTranslation();
  const [opt, setOpt] = useState<string>('');
  const [q, setQ] = useState<string | null>(null);
  return (
    <>
      <Space style={{ marginBottom: 8 }}>
        <label className="sr-label" htmlFor="opt-wu">{t('f.optionCode')}</label>
        <Input id="opt-wu" value={opt} onChange={(e) => setOpt(e.target.value)} style={{ width: 200 }} data-testid="option-where-used-input" />
        <Button onClick={() => setQ(opt)} disabled={!opt}>
          {t('actions.search')}
        </Button>
      </Space>
      {q && <DataTable testId="option-where-used" url={`/api/admin/customer-fields/${encodeURIComponent(code)}/options/${encodeURIComponent(q)}/where-used`} rowKey={(r) => `${r.consumerType}-${r.reference}-${r.version}`} columns={cols('consumerType', 'reference', 'version', 'status', 'isActiveConsumer', 'detail')} />}
    </>
  );
}

export default function FieldConfigPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<Query>({});
  const [n, setN] = useState(0);
  const [drawer, setDrawer] = useState<Row | null>(null);
  const groups = useApi<Row[]>('/api/admin/field-groups', { n });
  const bump = () => setN((x) => x + 1);
  const groupOpts = (groups.data ?? []).map((g) => ({ value: g.code, label: `${g.code} — ${g.labelEn}` }));

  const fieldSpecs = (_existing?: boolean): FieldSpec[] => [
    { name: 'code', label: t('f.code'), required: true, help: t('fields.codeHelp') },
    { name: 'labelEn', label: t('f.labelEn'), required: true },
    { name: 'labelAr', label: t('f.labelAr'), required: true },
    { name: 'description', label: t('f.description'), type: 'textarea' },
    { name: 'groupCode', label: t('f.groupCode'), type: 'select', options: groupOpts, required: true },
    { name: 'dataType', label: t('f.dataType'), type: 'select', options: lv(DATA_TYPES), required: true },
    { name: 'customerTypes', label: t('f.customerTypes'), type: 'multiselect', options: lv(CTYPES) },
    { name: 'requiredForTypes', label: t('f.requiredForTypes'), type: 'multiselect', options: lv(CTYPES) },
    { name: 'isSearchable', label: t('f.isSearchable'), type: 'switch' },
    { name: 'isFilterable', label: t('f.isFilterable'), type: 'switch' },
    { name: 'isSensitive', label: t('f.isSensitive'), type: 'switch' },
    { name: 'isEncrypted', label: t('f.isEncrypted'), type: 'switch' },
    { name: 'validationJson', label: t('f.validationJson'), type: 'textarea', help: '{"regex":"^[0-9]{10}$","min":0,"max":100,"maxLength":50}' },
    { name: 'defaultValue', label: t('f.defaultValue') },
    { name: 'sortOrder', label: t('f.sortOrder'), type: 'number' },
    { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
  ];
  const toBody = (v: Row, rowVersion?: string) => ({ ...v, customerTypes: (v.customerTypes ?? []).join(','), requiredForTypes: (v.requiredForTypes ?? []).join(','), rowVersion });

  return (
    <Page
      name="field-config"
      title={t('nav.fieldConfig')}
      extra={
        <>
          <ActionButton
            testId="field-create"
            type="primary"
            icon={<PlusOutlined />}
            label={t('fields.createDraft')}
            width={720}
            fields={fieldSpecs()}
            initialValues={{ dataType: 'ShortText', sortOrder: 0, customerTypes: ['Individual'] }}
            onSubmit={(v) => api.post('/api/admin/customer-fields', toBody(v))}
            onDone={bump}
          />
          <ActionButton
            testId="group-upsert"
            label={t('fields.upsertGroup')}
            fields={[
              { name: 'code', label: t('f.code'), required: true },
              { name: 'labelEn', label: t('f.labelEn'), required: true },
              { name: 'labelAr', label: t('f.labelAr') },
              { name: 'sortOrder', label: t('f.sortOrder'), type: 'number' },
            ]}
            initialValues={{ sortOrder: 0 }}
            onSubmit={(v) => api.post('/api/admin/field-groups', v)}
            onDone={bump}
          />
        </>
      }
    >
      <FilterBar
        testId="field-filters"
        onChange={setFilters}
        fields={[
          { name: 'code', label: t('f.code') },
          { name: 'status', label: t('f.status'), type: 'select', options: lv(STATUSES) },
        ]}
      />
      <DataTable
        testId="fields-table"
        url="/api/admin/customer-fields"
        params={filters}
        reloadKey={n}
        columns={[
          col('code', {
            sort: 'code',
            render: (v, r) => (
              <Button type="link" style={{ padding: 0 }} onClick={() => setDrawer(r)} data-testid={`field-open-${v}`}>
                {v}
              </Button>
            ),
          }),
          col('version', { sort: 'version' }),
          col('status', { sort: 'status' }),
          ...cols('labelEn', 'labelAr', 'groupCode', 'dataType', 'customerTypes', 'requiredForTypes', 'isSearchable', 'isFilterable', 'isSensitive'),
          col('isSample', { render: (v: boolean) => (v ? <SampleTag /> : '—') }),
          ...cols('publishedBy', 'publishedAt', 'approvedBy'),
          {
            key: 'actions',
            title: t('f.actions'),
            fixed: 'right' as const,
            render: (_: unknown, r: Row) => (
              <Space wrap size={4}>
                {r.status === 'Draft' && (
                  <>
                    <ActionButton
                      size="small"
                      testId={`field-edit-${r.code}`}
                      label={t('actions.edit')}
                      width={720}
                      fields={fieldSpecs(true)}
                      initialValues={{ ...r, customerTypes: split(r.customerTypes), requiredForTypes: split(r.requiredForTypes) }}
                      onSubmit={(v) => api.put(`/api/admin/customer-fields/${r.id}`, toBody(v, r.rowVersion))}
                      onDone={bump}
                    />
                    <ConfirmButton size="small" type="primary" testId={`field-publish-${r.code}`} label={t('actions.publish')} confirm={t('fields.confirmPublish')} onSubmit={() => api.post(`/api/admin/customer-fields/${r.id}/publish`, { rowVersion: r.rowVersion })} onDone={bump} />
                  </>
                )}
                {r.status === 'PendingApproval' && can('config.fields.approve') && (
                  <ConfirmButton size="small" type="primary" testId={`field-approve-${r.code}`} label={t('actions.approve')} onSubmit={() => api.post(`/api/admin/customer-fields/${r.id}/approve`)} onDone={bump} />
                )}
                {r.status === 'Published' && (
                  <>
                    <ActionButton size="small" testId={`field-new-version-${r.code}`} label={t('actions.newVersion')} onSubmit={() => api.post(`/api/admin/customer-fields/${r.id}/new-version`)} onDone={bump} />
                    <ConfirmButton
                      size="small"
                      danger
                      testId={`field-deactivate-${r.code}`}
                      label={t('actions.deactivate')}
                      fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]}
                      onSubmit={(v) => api.post(`/api/admin/customer-fields/${r.id}/deactivate`, v)}
                      onDone={bump}
                    />
                  </>
                )}
              </Space>
            ),
          },
        ]}
      />
      <Section title={t('fields.groups')}>
        <DataTable testId="field-groups" data={groups.data ?? []} columns={cols('code', 'labelEn', 'labelAr', 'sortOrder', 'isActive')} />
      </Section>
      <Drawer open={!!drawer} onClose={() => setDrawer(null)} width={Math.min(900, window.innerWidth)} title={drawer ? `${drawer.code} v${drawer.version}` : ''} destroyOnHidden data-testid="field-drawer">
        {drawer && (
          <Tabs
            items={[
              {
                key: 'whereUsed',
                label: <span data-testid="tab-where-used">{t('fields.whereUsed')}</span>,
                children: <DataTable testId="where-used" url={`/api/admin/customer-fields/${encodeURIComponent(drawer.code)}/where-used`} rowKey={(r) => `${r.consumerType}-${r.reference}-${r.version}`} columns={cols('consumerType', 'reference', 'version', 'status', 'isActiveConsumer', 'detail')} />,
              },
              { key: 'options', label: <span data-testid="tab-options">{t('fields.options')}</span>, children: <OptionsPanel code={drawer.code} /> },
              {
                key: 'versions',
                label: <span data-testid="tab-versions">{t('tabs.versionHistory')}</span>,
                children: <DataTable testId="field-versions" url="/api/admin/customer-fields" params={{ code: drawer.code }} columns={cols('version', 'status', 'labelEn', 'labelAr', 'dataType', 'effectiveFrom', 'effectiveTo', 'createdBy', 'publishedBy', 'publishedAt', 'approvedBy')} />,
              },
            ]}
          />
        )}
      </Drawer>
    </Page>
  );
}
