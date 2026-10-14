import { useMemo, useState } from 'react';
import { Alert, Button, Card, Col, Form, Input, Row as AntRow, Select, message } from 'antd';
import { useTranslation } from 'react-i18next';
import { api, ApiError } from '../../api/client';
import type { FormField, FormMetadata, Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Page, Section } from '../../components/Page';
import { ErrorAlert } from '../../components/StateViews';
import { useApi } from '../../components/hooks';
import { cols, tv } from '../../components/display';
import { DynamicForm } from '../../components/DynamicForm';

const CTYPES = ['Individual', 'Corporate', 'Agent', 'Merchant'];

/** Builds preview metadata from a layout definition and published field definitions. */
const previewMeta = (layout: Row[], defs: Row[], groups: Row[], customerType: string): FormMetadata => {
  const published = defs.filter((d) => d.status === 'Published');
  const fields: FormField[] = [];
  const g = layout.map((sec, i) => {
    const grp = groups.find((x) => x.code === sec.group);
    const codes = (sec.fields ?? []) as string[];
    codes.forEach((c) => {
      const d = published.find((x) => x.code === c);
      if (d && !fields.some((f) => f.code === c))
        fields.push({ ...(d as unknown as FormField), required: String(d.requiredForTypes ?? '').split(',').includes(customerType), options: [] });
    });
    return { code: sec.group, labelEn: grp?.labelEn ?? sec.group, labelAr: grp?.labelAr ?? sec.group, sortOrder: i, fieldCodes: codes };
  });
  return { customerType, layoutRole: '*', layoutVersion: 0, groups: g, fields };
};

export default function FormLayoutsPage() {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const defs = useApi<Row[]>('/api/admin/customer-fields');
  const groups = useApi<Row[]>('/api/admin/field-groups');
  const [form] = Form.useForm();
  const [json, setJson] = useState('[]');
  const [ctype, setCtype] = useState('Individual');
  const [err, setErr] = useState<ApiError | null>(null);
  const parsed = useMemo(() => {
    try {
      const v = JSON.parse(json);
      return Array.isArray(v) ? { ok: true as const, v: v as Row[] } : { ok: false as const, e: t('layouts.mustBeArray') };
    } catch (e) {
      return { ok: false as const, e: (e as Error).message };
    }
  }, [json, t]);

  const load = (r: Row) => {
    form.setFieldsValue({ customerType: r.customerType, roleCode: r.roleCode });
    setCtype(r.customerType);
    setJson(JSON.stringify(JSON.parse(r.layoutJson), null, 2));
  };

  return (
    <Page name="form-layouts" title={t('nav.formLayouts')}>
      <Section title={t('layouts.list')}>
        <DataTable
          testId="layouts-table"
          url="/api/admin/form-layouts"
          reloadKey={n}
          columns={[
            ...cols('customerType', 'roleCode', 'version', 'status', 'createdBy', 'createdAt'),
            {
              key: 'actions',
              title: t('f.actions'),
              render: (_: unknown, r: Row) => (
                <Button size="small" onClick={() => load(r)} data-testid={`layout-load-${r.id}`}>
                  {t('actions.edit')}
                </Button>
              ),
            },
          ]}
        />
      </Section>
      <AntRow gutter={16}>
        <Col xs={24} xl={12}>
          <Card size="small" title={t('layouts.editor')} style={{ marginBottom: 16 }}>
            <ErrorAlert error={err} />
            <Form
              form={form}
              layout="vertical"
              initialValues={{ customerType: 'Individual', roleCode: '*' }}
              onFinish={async (v) => {
                if (!parsed.ok) return;
                setErr(null);
                try {
                  await api.post('/api/admin/form-layouts', { customerType: v.customerType, roleCode: v.roleCode, layout: parsed.v });
                  message.success(t('msg.saved'));
                  setN((x) => x + 1);
                } catch (e) {
                  setErr(e as ApiError);
                }
              }}
            >
              <div className="form-grid">
                <Form.Item name="customerType" label={t('f.customerType')} rules={[{ required: true }]}>
                  <Select data-testid="layout-customer-type" options={CTYPES.map((x) => ({ value: x, label: tv(x) }))} onChange={setCtype} />
                </Form.Item>
                <Form.Item name="roleCode" label={t('f.roleCode')} rules={[{ required: true }]} extra={t('layouts.roleHelp')}>
                  <Input data-testid="layout-role" />
                </Form.Item>
              </div>
              <label className="sr-label" htmlFor="layout-json">{t('layouts.json')}</label>
              <Input.TextArea id="layout-json" data-testid="layout-json" className="mono" rows={16} value={json} onChange={(e) => setJson(e.target.value)} />
              {!parsed.ok && <Alert type="error" showIcon style={{ marginTop: 8 }} message={`${t('validation.json')}: ${parsed.e}`} data-testid="layout-json-error" />}
              <Button type="primary" htmlType="submit" style={{ marginTop: 12 }} disabled={!parsed.ok} data-testid="layout-save">
                {t('actions.save')}
              </Button>
            </Form>
          </Card>
        </Col>
        <Col xs={24} xl={12}>
          <Card size="small" title={t('layouts.preview')} data-testid="layout-preview">
            {parsed.ok && defs.data && groups.data ? (
              <Form layout="vertical" disabled>
                <DynamicForm meta={previewMeta(parsed.v, defs.data, groups.data, ctype)} />
              </Form>
            ) : (
              <Alert type="info" message={t('layouts.previewUnavailable')} />
            )}
          </Card>
        </Col>
      </AntRow>
    </Page>
  );
}
