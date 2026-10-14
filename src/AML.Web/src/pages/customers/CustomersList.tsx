import { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Card, Checkbox, Collapse, Form, Input, Popover, Select, Space, Tag, Typography, message } from 'antd';
import { PlusOutlined, SettingOutlined } from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../../api/client';
import type { CustomerListItem, FormField, FormMetadata } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { col, customerName, tv } from '../../components/display';
import { useLookupOptions } from '../../components/hooks';
import { fieldLabel } from '../../components/DynamicForm';
import { ErrorAlert } from '../../components/StateViews';

export const CUSTOMER_TYPES = ['Individual', 'Corporate', 'Agent', 'Merchant'];
export const SOURCE_SYSTEMS = ['CORE', 'WALLET'];
const KYC = ['Incomplete', 'Complete', 'Expired'];
const SCREENING = ['NotScreened', 'Clear', 'PotentialMatch', 'UnderReview', 'ConfirmedMatch', 'FalsePositive', 'Escalated', 'UnableToDetermine'];
const PREF_KEY = 'customers.columns';
const ALL_COLUMNS = ['externalId', 'name', 'customerType', 'status', 'riskLevel', 'riskScore', 'kycStatus', 'screeningStatus', 'nationalityCountry', 'nextReviewDate', 'assignedOfficer', 'sourceSystem', 'region', 'createdAt', 'updatedAt'];
const DEFAULT_COLUMNS = ['externalId', 'name', 'customerType', 'status', 'riskLevel', 'kycStatus', 'screeningStatus', 'nextReviewDate', 'assignedOfficer'];
const SORT_KEYS: Record<string, string> = { externalId: 'externalId', name: 'name', customerType: 'type', status: 'status', riskLevel: 'riskLevel', riskScore: 'riskScore', kycStatus: 'kycStatus', nationalityCountry: 'country', nextReviewDate: 'nextReviewDate', createdAt: 'createdAt', updatedAt: 'updatedAt' };
const OPS = ['eq', 'contains', 'gt', 'gte', 'lt', 'lte'];

const opts = (xs: string[], tv: (s: string) => string) => xs.map((x) => ({ value: x, label: tv(x) }));

/** Dynamic field filter panel: only searchable/filterable fields from published metadata. */
function FieldFilters({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  const { t, i18n } = useTranslation();
  const [fields, setFields] = useState<FormField[]>([]);
  const [error, setError] = useState<ApiError | null>(null);
  const [code, setCode] = useState<string>();
  const [op, setOp] = useState('eq');
  const [val, setVal] = useState<string>('');

  useEffect(() => {
    Promise.all(CUSTOMER_TYPES.map((ct) => api.get<FormMetadata>('/api/config/customer-fields', { customerType: ct }).catch((e) => e as ApiError)))
      .then((res) => {
        const map = new Map<string, FormField>();
        res.forEach((r) => {
          if (r instanceof ApiError) {
            setError(r);
            return;
          }
          r.fields.filter((f) => f.isSearchable || f.isFilterable).forEach((f) => map.set(f.code, f));
        });
        setFields([...map.values()].sort((a, b) => a.code.localeCompare(b.code)));
      });
  }, []);

  const field = fields.find((f) => f.code === code);
  const add = () => {
    if (!code || val === '') return;
    onChange([...value, `${code}:${op}:${val}`]);
    setVal('');
  };

  return (
    <div data-testid="field-filters">
      <ErrorAlert error={error} />
      <Space wrap align="end">
        <div>
          <label htmlFor="ff-field" className="sr-label">
            {t('customers.dynamicField')}
          </label>
          <Select
            id="ff-field"
            data-testid="field-filter-select"
            style={{ minWidth: 240 }}
            value={code}
            onChange={(v) => {
              setCode(v);
              setVal('');
            }}
            showSearch
            optionFilterProp="label"
            placeholder={t('customers.selectField')}
            options={fields.map((f) => ({ value: f.code, label: `${fieldLabel(f, i18n.language)} (${f.code})` }))}
          />
        </div>
        <div>
          <label htmlFor="ff-op" className="sr-label">
            {t('customers.operator')}
          </label>
          <Select id="ff-op" data-testid="field-filter-op" style={{ minWidth: 120 }} value={op} onChange={setOp} options={OPS.map((o) => ({ value: o, label: t(`ops.${o}`) }))} />
        </div>
        <div>
          <label htmlFor="ff-value" className="sr-label">
            {t('customers.value')}
          </label>
          {field && field.options.length > 0 ? (
            <Select
              id="ff-value"
              data-testid="field-filter-value"
              style={{ minWidth: 200 }}
              value={val || undefined}
              onChange={setVal}
              options={field.options.map((o) => ({ value: o.optionCode, label: fieldLabel(o, i18n.language) }))}
            />
          ) : (
            <Input id="ff-value" data-testid="field-filter-value" value={val} onChange={(e) => setVal(e.target.value)} onPressEnter={add} style={{ minWidth: 200 }} />
          )}
        </div>
        <Button onClick={add} data-testid="field-filter-add" disabled={!code || val === ''}>
          {t('actions.add')}
        </Button>
      </Space>
      <div style={{ marginTop: 8 }}>
        {value.map((f) => (
          <Tag key={f} closable onClose={() => onChange(value.filter((x) => x !== f))} data-testid="field-filter-chip">
            {f}
          </Tag>
        ))}
      </div>
    </div>
  );
}

function SensitiveSearch() {
  const { t } = useTranslation();
  const [body, setBody] = useState<Record<string, string> | null>(null);
  const [n, setN] = useState(0);
  const fetcher = useCallback(() => api.post<CustomerListItem[]>('/api/customers/search', body), [body]);
  return (
    <div data-testid="sensitive-search">
      <Typography.Paragraph type="secondary">{t('customers.sensitiveHint')}</Typography.Paragraph>
      <Form
        layout="vertical"
        onFinish={(v) => {
          const clean = Object.fromEntries(Object.entries(v).filter(([, x]) => x)) as Record<string, string>;
          setBody(Object.keys(clean).length ? clean : null);
          setN((x) => x + 1);
        }}
      >
        <div className="filter-grid">
          <Form.Item name="identityNumber" label={t('f.identityNumber')}>
            <Input data-testid="sensitive-identityNumber" autoComplete="off" />
          </Form.Item>
          <Form.Item name="phone" label={t('f.phone')}>
            <Input data-testid="sensitive-phone" autoComplete="off" />
          </Form.Item>
          <Form.Item name="email" label={t('f.email')}>
            <Input data-testid="sensitive-email" autoComplete="off" />
          </Form.Item>
        </div>
        <Button type="primary" htmlType="submit" data-testid="sensitive-submit">
          {t('actions.search')}
        </Button>
      </Form>
      {body && (
        <div style={{ marginTop: 12 }}>
          <DataTable<CustomerListItem>
            testId="sensitive-results"
            fetcher={fetcher}
            reloadKey={n}
            columns={[col('externalId', { link: (r) => `/customers/${r.id}` }), col('name', { render: (_, r) => customerName(r) }), col('customerType'), col('status'), col('riskLevel')]}
          />
        </div>
      )}
    </div>
  );
}

export default function CustomersList() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [filters, setFilters] = useState<Query>({});
  const [fieldFilter, setFieldFilter] = useState<string[]>([]);
  const [visible, setVisible] = useState<string[]>(DEFAULT_COLUMNS);
  const [selected, setSelected] = useState<number[]>([]);
  const [reload, setReload] = useState(0);
  const types = useLookupOptions('CUSTOMER_TYPE');
  const statuses = useLookupOptions('CUSTOMER_STATUS');
  const levels = useLookupOptions('RISK_LEVEL');
  const countries = useLookupOptions('COUNTRY');

  useEffect(() => {
    api
      .get<{ value: string | null }>(`/api/me/preferences/${PREF_KEY}`)
      .then((p) => {
        if (!p?.value) return;
        const cols = JSON.parse(p.value) as string[];
        if (Array.isArray(cols) && cols.length) setVisible(cols.filter((c) => ALL_COLUMNS.includes(c)));
      })
      .catch(() => undefined);
  }, []);

  const toggleColumn = async (key: string, on: boolean) => {
    const next = on ? ALL_COLUMNS.filter((c) => c === key || visible.includes(c)) : visible.filter((c) => c !== key);
    setVisible(next);
    try {
      await api.put(`/api/me/preferences/${PREF_KEY}`, { value: JSON.stringify(next) });
    } catch (e) {
      message.error((e as ApiError).detail ?? t('state.error'));
    }
  };

  const columns = useMemo(
    () =>
      visible.map((k) =>
        col<CustomerListItem>(k, {
          sort: SORT_KEYS[k],
          link: k === 'externalId' || k === 'name' ? (r) => `/customers/${r.id}` : undefined,
          render: k === 'name' ? (_, r) => customerName(r) : undefined,
        }),
      ),
    [visible],
  );

  const params: Query = { ...filters, search: search || undefined, fieldFilter };

  const chooser = (
    <div data-testid="column-chooser-panel" style={{ display: 'grid', gap: 4 }}>
      {ALL_COLUMNS.map((k) => (
        <Checkbox key={k} checked={visible.includes(k)} onChange={(e) => void toggleColumn(k, e.target.checked)} data-testid={`col-toggle-${k}`}>
          {t(`f.${k}`)}
        </Checkbox>
      ))}
    </div>
  );

  return (
    <Page
      name="customers"
      title={t('nav.customers')}
      extra={
        can('customers.write') && (
          <Button type="primary" icon={<PlusOutlined />} data-testid="customer-create" onClick={() => navigate('/customers/new')}>
            {t('customers.create')}
          </Button>
        )
      }
    >
      <Card size="small" style={{ marginBottom: 12 }}>
        <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
          <Input.Search
            data-testid="customer-search"
            aria-label={t('customers.searchLabel')}
            placeholder={t('customers.searchPlaceholder')}
            allowClear
            enterButton={t('actions.search')}
            onSearch={(v) => setSearch(v.trim())}
            style={{ maxWidth: 420 }}
          />
          <Space wrap>
            {can('customers.bulk') && selected.length > 0 && (
              <ActionButton
                testId="bulk-assign"
                label={t('customers.bulkAssign', { count: selected.length })}
                fields={[
                  { name: 'officer', label: t('f.assignedOfficer'), required: true },
                  { name: 'reason', label: t('f.reason'), type: 'textarea' },
                ]}
                onSubmit={(v) => api.post('/api/customers/bulk-assign', { customerIds: selected, officer: v.officer, reason: v.reason })}
                onDone={() => {
                  setSelected([]);
                  setReload((x) => x + 1);
                }}
              />
            )}
            <Popover content={chooser} trigger="click" title={t('customers.columns')} placement="bottomRight">
              <Button icon={<SettingOutlined />} data-testid="column-chooser">
                {t('customers.columns')}
              </Button>
            </Popover>
          </Space>
        </Space>
      </Card>
      <FilterBar
        testId="customer-filters"
        onChange={setFilters}
        fields={[
          { name: 'customerType', label: t('f.customerType'), type: 'select', options: types.length ? types : opts(CUSTOMER_TYPES, tv) },
          { name: 'status', label: t('f.status'), type: 'select', options: statuses },
          { name: 'riskLevel', label: t('f.riskLevel'), type: 'select', options: levels },
          { name: 'kycStatus', label: t('f.kycStatus'), type: 'select', options: opts(KYC, tv) },
          { name: 'screeningStatus', label: t('f.screeningStatus'), type: 'select', options: opts(SCREENING, tv) },
          { name: 'country', label: t('f.country'), type: 'select', options: countries },
          { name: 'sourceSystem', label: t('f.sourceSystem'), type: 'select', options: SOURCE_SYSTEMS.map((s) => ({ value: s, label: s })) },
          { name: 'assignedOfficer', label: t('f.assignedOfficer') },
          { name: 'reviewOverdue', label: t('f.reviewOverdue'), type: 'switch' },
        ]}
      />
      <Collapse
        style={{ marginBottom: 12, background: '#fff' }}
        items={[
          { key: 'fields', forceRender: true, label: <span data-testid="field-filters-toggle">{t('customers.fieldFilters')}</span>, children: <FieldFilters value={fieldFilter} onChange={setFieldFilter} /> },
          { key: 'sensitive', label: <span data-testid="sensitive-search-toggle">{t('customers.sensitiveSearch')}</span>, children: <SensitiveSearch /> },
        ]}
      />
      <DataTable<CustomerListItem>
        testId="customers-table"
        url="/api/customers"
        params={params}
        columns={columns}
        reloadKey={reload}
        defaultSort={{ sort: 'createdAt', dir: 'desc' }}
        rowSelection={can('customers.bulk') ? { selectedRowKeys: selected, onChange: (k) => setSelected(k as number[]) } : undefined}
      />
    </Page>
  );
}
