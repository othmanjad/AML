import { useState } from 'react';
import { Alert, Button, Card, DatePicker, Form, Input, Select, Space, Switch, Tooltip } from 'antd';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError } from '../../api/client';
import type { CustomerDetail, FormMetadata } from '../../api/types';
import { Page } from '../../components/Page';
import { useApi, useLookupOptions } from '../../components/hooks';
import { DynamicForm, cleanAttributes } from '../../components/DynamicForm';
import { ErrorAlert, StateLoading, Reference } from '../../components/StateViews';
import { dateProps } from '../../components/FormFields';
import { tv } from '../../components/display';
import { CUSTOMER_TYPES, SOURCE_SYSTEMS } from './CustomersList';

const CORE_FIELDS = ['sourceSystem', 'externalId', 'customerType', 'nameLatin', 'nameArabic', 'dateOfBirth', 'nationalityCountry', 'residenceCountry', 'incorporationCountry', 'registrationDate', 'assignedOfficer', 'region', 'householdId', 'isPep'];

export default function CustomerCreate() {
  const { t } = useTranslation();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const [type, setType] = useState<string>('Individual');
  const meta = useApi<FormMetadata>(type ? '/api/config/customer-fields' : null, { customerType: type });
  const countries = useLookupOptions('COUNTRY');
  const typeOptions = useLookupOptions('CUSTOMER_TYPE');
  const [error, setError] = useState<ApiError | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [saving, setSaving] = useState(false);
  const metaReady = !!meta.data && !meta.error && !meta.loading;
  const isPerson = type === 'Individual';

  const submit = async () => {
    if (!metaReady) return; // never submit an incomplete form
    let v: Record<string, unknown>;
    try {
      v = await form.validateFields();
    } catch {
      return;
    }
    setSaving(true);
    setError(null);
    setFieldErrors({});
    try {
      const body = { ...v, customerType: type, attributes: cleanAttributes(v.attributes as Record<string, unknown>) };
      const c = await api.post<CustomerDetail>('/api/customers', body);
      nav(`/customers/${c.id}`);
    } catch (e) {
      const err = e instanceof ApiError ? e : new ApiError(0, { detail: String(e) }, 'Error');
      setError(err);
      if (err.errors) {
        const codes = new Set(meta.data?.fields.map((f) => f.code));
        const dyn: Record<string, string[]> = {};
        const core: { name: (string | number)[]; errors: string[] }[] = [];
        Object.entries(err.errors).forEach(([k, msgs]) => {
          const key = k.replace(/^attributes\./i, '');
          const code = [...codes].find((c) => c.toLowerCase() === key.toLowerCase());
          if (code) dyn[code] = msgs;
          else {
            const cf = CORE_FIELDS.find((c) => c.toLowerCase() === key.toLowerCase());
            if (cf) core.push({ name: [cf], errors: msgs });
            else if (key.toLowerCase() === 'name') core.push({ name: ['nameLatin'], errors: msgs });
          }
        });
        setFieldErrors(dyn);
        form.setFields(core);
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Page name="customer-create" title={t('customers.create')}>
      <Form form={form} layout="vertical" initialValues={{ sourceSystem: 'CORE', customerType: 'Individual', isPep: false }}>
        <Card size="small" title={t('customers.coreFields')} style={{ marginBottom: 12 }}>
          <div className="form-grid">
            <Form.Item name="customerType" label={t('f.customerType')} rules={[{ required: true }]}>
              <Select
                data-testid="customer-type"
                options={typeOptions.length ? typeOptions : CUSTOMER_TYPES.map((x) => ({ value: x, label: tv(x) }))}
                onChange={(v: string) => {
                  setType(v);
                  setFieldErrors({});
                  form.setFieldValue('attributes', {});
                }}
              />
            </Form.Item>
            <Form.Item name="sourceSystem" label={t('f.sourceSystem')} rules={[{ required: true, message: t('validation.required', { field: t('f.sourceSystem') }) }]}>
              <Select data-testid="core-sourceSystem" options={SOURCE_SYSTEMS.map((s) => ({ value: s, label: s }))} />
            </Form.Item>
            <Form.Item name="externalId" label={t('f.externalId')} rules={[{ required: true, message: t('validation.required', { field: t('f.externalId') }) }]}>
              <Input data-testid="core-externalId" />
            </Form.Item>
            <Form.Item name="nameLatin" label={t('f.nameLatin')}>
              <Input data-testid="core-nameLatin" lang="en" dir="ltr" />
            </Form.Item>
            <Form.Item name="nameArabic" label={t('f.nameArabic')}>
              <Input data-testid="core-nameArabic" lang="ar" dir="rtl" />
            </Form.Item>
            <Form.Item name={isPerson ? 'dateOfBirth' : 'registrationDate'} label={t(isPerson ? 'f.dateOfBirth' : 'f.registrationDate')} {...dateProps('date')}>
              <DatePicker data-testid={isPerson ? 'core-dateOfBirth' : 'core-registrationDate'} style={{ width: '100%' }} />
            </Form.Item>
            {isPerson ? (
              <>
                <Form.Item name="nationalityCountry" label={t('f.nationalityCountry')}>
                  <Select data-testid="core-nationalityCountry" options={countries} showSearch optionFilterProp="label" allowClear />
                </Form.Item>
                <Form.Item name="residenceCountry" label={t('f.residenceCountry')}>
                  <Select data-testid="core-residenceCountry" options={countries} showSearch optionFilterProp="label" allowClear />
                </Form.Item>
              </>
            ) : (
              <Form.Item name="incorporationCountry" label={t('f.incorporationCountry')}>
                <Select data-testid="core-incorporationCountry" options={countries} showSearch optionFilterProp="label" allowClear />
              </Form.Item>
            )}
            <Form.Item name="assignedOfficer" label={t('f.assignedOfficer')}>
              <Input data-testid="core-assignedOfficer" />
            </Form.Item>
            <Form.Item name="region" label={t('f.region')}>
              <Input data-testid="core-region" />
            </Form.Item>
            <Form.Item name="householdId" label={t('f.householdId')}>
              <Input data-testid="core-householdId" dir="ltr" />
            </Form.Item>
            <Form.Item name="isPep" label={t('f.isPep')} valuePropName="checked">
              <Switch data-testid="core-isPep" />
            </Form.Item>
          </div>
        </Card>

        <Card size="small" title={t('customers.dynamicFields')} style={{ marginBottom: 12 }} data-testid="dynamic-section">
          {meta.error ? (
            <div data-testid="metadata-error">
              <Alert
                type="error"
                showIcon
                message={t('customers.metadataFailed')}
                description={
                  <div>
                    <div>{meta.error.detail || meta.error.message}</div>
                    <Reference id={meta.error.correlationId} />
                  </div>
                }
                action={
                  <Button onClick={meta.reload} data-testid="metadata-retry">
                    {t('actions.retry')}
                  </Button>
                }
              />
            </div>
          ) : !meta.data || meta.loading ? (
            <StateLoading />
          ) : (
            <DynamicForm meta={meta.data} serverErrors={fieldErrors} />
          )}
        </Card>

        <ErrorAlert error={error} />
        <Space>
          <Tooltip title={metaReady ? undefined : t('customers.saveDisabled')}>
            <Button type="primary" onClick={submit} loading={saving} disabled={!metaReady} data-testid="customer-save">
              {t('actions.save')}
            </Button>
          </Tooltip>
          <Button onClick={() => nav('/customers')} data-testid="customer-cancel">
            {t('actions.cancel')}
          </Button>
        </Space>
      </Form>
    </Page>
  );
}
