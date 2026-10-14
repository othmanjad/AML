import { useState } from 'react';
import { Alert, Button, Form, Input, InputNumber, List, Modal, Select, Space, Switch, Table, Tabs } from 'antd';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError } from '../../api/client';
import type { Factor, Methodology, Row, Threshold } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page, Section } from '../../components/Page';
import { Async, ErrorAlert } from '../../components/StateViews';
import { useApi } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { KV, SampleTag, StatusTag, col, cols, fl, tv } from '../../components/display';
import { FactorTable, MissingDataWarnings } from '../../components/RiskViews';
import { applyServerErrors } from '../../components/FormFields';

const LEVELS = ['Low', 'Medium', 'High', 'Critical'];
const AGG = ['WeightedAverage', 'WeightedSumCapped', 'MaxFactor', 'CategoryMatrix'];
const CTYPES = ['Individual', 'Corporate', 'Agent', 'Merchant'];
const lv = (xs: string[]) => xs.map((x) => ({ value: x, label: tv(x) }));

export function MethodologiesList() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  return (
    <Page
      name="risk-methodologies"
      title={t('nav.riskMethodologies')}
      extra={
        can('risk.config') && (
          <ActionButton
            testId="methodology-create"
            type="primary"
            icon={<PlusOutlined />}
            label={t('actions.create')}
            fields={[
              { name: 'code', label: t('f.code'), required: true },
              { name: 'name', label: t('f.name'), required: true },
              { name: 'aggregationMethod', label: t('f.aggregationMethod'), type: 'select', options: lv(AGG), required: true },
              { name: 'appliesToCustomerTypes', label: t('f.appliesToCustomerTypes'), type: 'multiselect', options: lv(CTYPES) },
              { name: 'owner', label: t('f.owner') },
              { name: 'rationale', label: t('f.rationale'), type: 'textarea' },
            ]}
            initialValues={{ aggregationMethod: 'WeightedAverage' }}
            onSubmit={(v) => api.post<Methodology>('/api/admin/risk-methodologies', { ...v, appliesToCustomerTypes: (v.appliesToCustomerTypes ?? []).join(',') })}
            onDone={(m: Methodology) => nav(`/admin/risk-methodologies/${m.id}`)}
          />
        )
      }
    >
      <DataTable<Methodology>
        testId="methodologies-table"
        url="/api/admin/risk-methodologies"
        columns={[
          col('code', { link: (r) => `/admin/risk-methodologies/${r.id}`, sort: 'code' }),
          col('name', { render: (v, r) => <span>{v} <SampleTag show={r.isSample} /></span> }),
          ...cols<Methodology>('version', 'status', 'aggregationMethod', 'appliesToCustomerTypes', 'effectiveFrom', 'effectiveTo', 'approvedBy'),
        ]}
      />
    </Page>
  );
}

function FactorModal({ m, factor, open, onClose, onSaved }: { m: Methodology; factor?: Factor; open: boolean; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [form] = Form.useForm();
  const [err, setErr] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  const ds = useApi<Row>('/api/config/risk-data-sources');
  const cats = useApi<Row[]>('/api/admin/risk-categories');
  const sources = [...((ds.data?.registry as Row[]) ?? []).map((x) => x.code as string), ...((ds.data?.dynamicFields as Row[]) ?? []).map((x) => x.code as string)];
  const save = async () => {
    let v: Row;
    try {
      v = await form.validateFields();
    } catch {
      return;
    }
    setBusy(true);
    setErr(null);
    try {
      const body = { ...v, applicableCustomerTypes: (v.applicableCustomerTypes ?? []).join(','), rowVersion: factor?.rowVersion };
      if (factor) await api.put(`/api/admin/risk-factors/${factor.id}`, body);
      else await api.post(`/api/admin/risk-methodologies/${m.id}/factors`, body);
      onSaved();
      onClose();
    } catch (e) {
      setErr(e as ApiError);
      applyServerErrors(form, e as ApiError);
    } finally {
      setBusy(false);
    }
  };
  const initial = factor
    ? { ...factor, applicableCustomerTypes: factor.applicableCustomerTypes ? factor.applicableCustomerTypes.split(',').filter(Boolean) : [] }
    : { factorType: 'Categorical', weight: 1, missingDataHandling: 'AssignScore', missingDataScore: 50, isActive: true, mappings: [{ matchType: 'Default', score: 50, priority: 0 }] };
  return (
    <Modal
      open={open}
      title={factor ? `${t('actions.edit')} ${factor.code}` : t('risk.addFactor')}
      onCancel={onClose}
      onOk={save}
      confirmLoading={busy}
      width={900}
      destroyOnHidden
      okText={t('actions.save')}
      cancelText={t('actions.cancel')}
      okButtonProps={{ 'data-testid': 'factor-save' } as never}
      modalRender={(n) => <div data-testid="factor-modal">{n}</div>}
    >
      <ErrorAlert error={err} />
      <Form form={form} layout="vertical" initialValues={initial} preserve={false}>
        <div className="form-grid">
          <Form.Item name="code" label={fl('code')} rules={[{ required: true }]}>
            <Input data-testid="factor-code" disabled={!!factor} />
          </Form.Item>
          <Form.Item name="name" label={fl('name')} rules={[{ required: true }]}>
            <Input data-testid="factor-name" />
          </Form.Item>
          <Form.Item name="categoryCode" label={fl('categoryCode')} rules={[{ required: true }]}>
            <Select data-testid="factor-category" options={(cats.data ?? []).map((c) => ({ value: c.code, label: `${c.code} — ${c.name}` }))} showSearch />
          </Form.Item>
          <Form.Item name="dataSource" label={fl('dataSource')} rules={[{ required: true }]}>
            <Select data-testid="factor-datasource" options={sources.map((s) => ({ value: s, label: s }))} showSearch />
          </Form.Item>
          <Form.Item name="factorType" label={fl('factorType')}>
            <Select options={lv(['Categorical', 'Numeric', 'Boolean'])} />
          </Form.Item>
          <Form.Item name="weight" label={fl('weight')} rules={[{ required: true }]}>
            <InputNumber data-testid="factor-weight" style={{ width: '100%' }} min={0} />
          </Form.Item>
          <Form.Item name="applicableCustomerTypes" label={fl('applicableCustomerTypes')}>
            <Select mode="multiple" options={lv(CTYPES)} />
          </Form.Item>
          <Form.Item name="missingDataHandling" label={fl('missingDataHandling')}>
            <Select options={lv(['AssignScore', 'ExcludeWithWarning'])} />
          </Form.Item>
          <Form.Item name="missingDataScore" label={fl('missingDataScore')}>
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="freshnessDays" label={fl('freshnessDays')}>
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="isActive" label={fl('isActive')} valuePropName="checked">
            <Switch />
          </Form.Item>
        </div>
        <Form.Item name="description" label={fl('description')}>
          <Input.TextArea rows={2} />
        </Form.Item>
        <h4>{t('risk.scoreMappings')}</h4>
        <Form.List name="mappings">
          {(items, { add, remove }) => (
            <div data-testid="mapping-editor">
              {items.map((it) => (
                <Space key={it.key} wrap align="end" style={{ marginBottom: 8 }}>
                  <Form.Item name={[it.name, 'matchType']} label={fl('matchType')}>
                    <Select style={{ width: 120 }} options={lv(['Equals', 'Range', 'Default'])} />
                  </Form.Item>
                  <Form.Item name={[it.name, 'value']} label={fl('value')}>
                    <Input style={{ width: 140 }} />
                  </Form.Item>
                  <Form.Item name={[it.name, 'rangeMin']} label={fl('rangeMin')}>
                    <InputNumber style={{ width: 100 }} />
                  </Form.Item>
                  <Form.Item name={[it.name, 'rangeMax']} label={fl('rangeMax')}>
                    <InputNumber style={{ width: 100 }} />
                  </Form.Item>
                  <Form.Item name={[it.name, 'score']} label={fl('score')} rules={[{ required: true }]}>
                    <InputNumber style={{ width: 90 }} />
                  </Form.Item>
                  <Form.Item name={[it.name, 'priority']} label={fl('priority')}>
                    <InputNumber style={{ width: 80 }} />
                  </Form.Item>
                  <Button icon={<DeleteOutlined />} aria-label={t('actions.remove')} onClick={() => remove(it.name)} />
                </Space>
              ))}
              <Button icon={<PlusOutlined />} onClick={() => add({ matchType: 'Equals', score: 0, priority: 1 })} data-testid="mapping-add">
                {t('risk.addMapping')}
              </Button>
            </div>
          )}
        </Form.List>
      </Form>
    </Modal>
  );
}

function ThresholdEditor({ m, editable, onSaved }: { m: Methodology; editable: boolean; onSaved: () => void }) {
  const { t } = useTranslation();
  const [form] = Form.useForm();
  const [err, setErr] = useState<ApiError | null>(null);
  const anySample = m.thresholds.some((x) => x.isSample);
  return (
    <div data-testid="thresholds">
      {anySample && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={<span>{t('risk.sampleThresholds')} <SampleTag /></span>} />
      )}
      <Table<Threshold>
        data-testid="thresholds-table"
        size="small"
        rowKey="level"
        pagination={false}
        dataSource={m.thresholds}
        columns={[
          col<Threshold>('level'),
          ...cols<Threshold>('minScore', 'maxScore', 'reviewMonths'),
          col<Threshold>('isSample', { render: (v: boolean) => (v ? <SampleTag /> : t('common.no')) }),
        ]}
      />
      {editable && (
        <div style={{ marginTop: 16 }}>
          <ErrorAlert error={err} />
          <Form
            form={form}
            layout="vertical"
            initialValues={{ thresholds: m.thresholds.length ? m.thresholds : LEVELS.map((l, i) => ({ level: l, minScore: i * 25, maxScore: i * 25 + 24.99, reviewMonths: 12 })) }}
            onFinish={async (v) => {
              setErr(null);
              try {
                await api.put(`/api/admin/risk-methodologies/${m.id}/thresholds`, v.thresholds);
                onSaved();
              } catch (e) {
                setErr(e as ApiError);
              }
            }}
          >
            <Form.List name="thresholds">
              {(items, { add, remove }) => (
                <>
                  {items.map((it) => (
                    <Space key={it.key} wrap align="end">
                      <Form.Item name={[it.name, 'level']} label={fl('level')} rules={[{ required: true }]}>
                        <Select style={{ width: 120 }} options={lv(LEVELS)} />
                      </Form.Item>
                      <Form.Item name={[it.name, 'minScore']} label={fl('minScore')} rules={[{ required: true }]}>
                        <InputNumber />
                      </Form.Item>
                      <Form.Item name={[it.name, 'maxScore']} label={fl('maxScore')} rules={[{ required: true }]}>
                        <InputNumber />
                      </Form.Item>
                      <Form.Item name={[it.name, 'reviewMonths']} label={fl('reviewMonths')}>
                        <InputNumber />
                      </Form.Item>
                      <Button icon={<DeleteOutlined />} aria-label={t('actions.remove')} onClick={() => remove(it.name)} style={{ marginBottom: 24 }} />
                    </Space>
                  ))}
                  <Space>
                    <Button onClick={() => add({ level: 'Low', minScore: 0, maxScore: 0, reviewMonths: 12 })} icon={<PlusOutlined />}>
                      {t('actions.add')}
                    </Button>
                    <Button type="primary" htmlType="submit" data-testid="thresholds-save">
                      {t('actions.save')}
                    </Button>
                  </Space>
                </>
              )}
            </Form.List>
          </Form>
        </div>
      )}
    </div>
  );
}

function Simulator({ m }: { m: Methodology }) {
  const { t } = useTranslation();
  const [res, setRes] = useState<Row | null>(null);
  const [err, setErr] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  return (
    <div data-testid="simulator">
      <Form
        layout="vertical"
        onFinish={async (v) => {
          setBusy(true);
          setErr(null);
          setRes(null);
          try {
            const inputs = v.inputs ? JSON.parse(v.inputs) : undefined;
            setRes(await api.post(`/api/admin/risk-methodologies/${m.id}/simulate`, { customerId: v.customerId || undefined, customerType: v.customerType || undefined, inputs }));
          } catch (e) {
            setErr(e instanceof ApiError ? e : new ApiError(400, { detail: t('validation.json') }, 'JSON'));
          } finally {
            setBusy(false);
          }
        }}
      >
        <div className="form-grid">
          <Form.Item name="customerId" label={fl('customerId')}>
            <InputNumber data-testid="sim-customerId" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="customerType" label={fl('customerType')}>
            <Select data-testid="sim-customerType" allowClear options={lv(CTYPES)} />
          </Form.Item>
        </div>
        <Form.Item name="inputs" label={t('risk.simInputs')} extra={t('risk.simInputsHelp')}>
          <Input.TextArea data-testid="sim-inputs" rows={5} className="mono" placeholder='{"customer.nationality": "JO", "profile.declaredMonthlyIncome": 400}' />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={busy} data-testid="sim-run">
          {t('risk.simulate')}
        </Button>
      </Form>
      <div style={{ marginTop: 16 }}>
        <ErrorAlert error={err} />
        {res && (
          <div data-testid="sim-result">
            <KV data={res} keys={['customerType', 'calculatedScore', 'calculatedLevel', 'finalLevel', 'mandatoryOverrideLevel', 'requiresApproval', 'sanctionsWorkflow', 'hasMissingData']} />
            {(res.warnings as string[])?.length > 0 && <Alert style={{ marginTop: 12 }} type="warning" showIcon message={(res.warnings as string[]).join(' · ')} />}
            <p style={{ marginTop: 12 }}>{res.explanation}</p>
            <MissingDataWarnings factors={res.factors} />
            <FactorTable factors={res.factors} testId="sim-factors" />
          </div>
        )}
      </div>
    </div>
  );
}

export function MethodologyDetail() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  const st = useApi<Methodology>(`/api/admin/risk-methodologies/${id}`);
  const [factorModal, setFactorModal] = useState<{ open: boolean; f?: Factor }>({ open: false });
  const [validation, setValidation] = useState<Row | null>(null);
  return (
    <Async state={st}>
      {(m) => {
        const draft = m.status === 'Draft' && can('risk.config');
        const reload = st.reload;
        return (
          <Page
            name="methodology-detail"
            title={`${m.code} v${m.version}`}
            subTitle={
              <Space wrap>
                <span>{m.name}</span>
                <StatusTag value={m.status} />
                <SampleTag show={m.isSample} />
              </Space>
            }
            extra={
              <>
                {can('risk.config') && (
                  <ActionButton
                    testId="methodology-validate"
                    label={t('actions.validate')}
                    onSubmit={() => api.post<Row>(`/api/admin/risk-methodologies/${m.id}/validate`)}
                    onDone={(r: Row) => setValidation(r)}
                  />
                )}
                {draft && (
                  <ActionButton
                    testId="methodology-submit"
                    label={t('actions.submitForApproval')}
                    fields={[{ name: 'rationale', label: t('f.rationale'), type: 'textarea', required: true }]}
                    onSubmit={(v) => api.post(`/api/admin/risk-methodologies/${m.id}/submit`, v)}
                    onDone={reload}
                  />
                )}
                {can('risk.approve') && m.status === 'PendingApproval' && (
                  <>
                    <ConfirmButton testId="methodology-approve" type="primary" label={t('actions.approve')} onSubmit={() => api.post(`/api/admin/risk-methodologies/${m.id}/approve`)} onDone={reload} />
                    <ActionButton
                      testId="methodology-reject"
                      danger
                      label={t('actions.reject')}
                      fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]}
                      onSubmit={(v) => api.post(`/api/admin/risk-methodologies/${m.id}/reject`, v)}
                      onDone={reload}
                    />
                  </>
                )}
                {can('risk.config') && m.status !== 'Draft' && (
                  <ActionButton
                    testId="methodology-new-version"
                    label={t('actions.newVersion')}
                    onSubmit={() => api.post<Methodology>(`/api/admin/risk-methodologies/${m.id}/new-version`)}
                    onDone={(x: Methodology) => nav(`/admin/risk-methodologies/${x.id}`)}
                  />
                )}
              </>
            }
          >
            {validation && (
              <Alert
                data-testid="validation-result"
                closable
                onClose={() => setValidation(null)}
                style={{ marginBottom: 12 }}
                type={validation.valid ? 'success' : 'error'}
                showIcon
                message={validation.valid ? t('msg.valid') : t('msg.invalid')}
                description={
                  (validation.errors as string[])?.length ? (
                    <List size="small" data-testid="validation-errors" dataSource={validation.errors as unknown[]} renderItem={(e) => <List.Item>{typeof e === 'string' ? e : JSON.stringify(e)}</List.Item>} />
                  ) : undefined
                }
              />
            )}
            {!draft && m.status !== 'Draft' && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('risk.readOnlyVersion')} />}
            <Tabs
              destroyOnHidden
              items={[
                {
                  key: 'general',
                  label: <span data-testid="tab-general">{t('tabs.general')}</span>,
                  children: (
                    <Section
                      title={t('tabs.general')}
                      extra={
                        draft && (
                          <ActionButton
                            testId="methodology-edit"
                            label={t('actions.edit')}
                            initialValues={{ ...m, appliesToCustomerTypes: m.appliesToCustomerTypes.split(',').filter(Boolean) }}
                            fields={[
                              { name: 'name', label: t('f.name'), required: true },
                              { name: 'aggregationMethod', label: t('f.aggregationMethod'), type: 'select', options: lv(AGG) },
                              { name: 'cap', label: t('f.cap'), type: 'number' },
                              { name: 'scorePrecision', label: t('f.scorePrecision'), type: 'number' },
                              { name: 'appliesToCustomerTypes', label: t('f.appliesToCustomerTypes'), type: 'multiselect', options: lv(CTYPES) },
                              { name: 'approvalRequiredLevels', label: t('f.approvalRequiredLevels') },
                              { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
                              { name: 'effectiveTo', label: t('f.effectiveTo'), type: 'datetime' },
                              { name: 'owner', label: t('f.owner') },
                              { name: 'rationale', label: t('f.rationale'), type: 'textarea' },
                            ]}
                            onSubmit={(v) => api.put(`/api/admin/risk-methodologies/${m.id}`, { ...m, ...v, appliesToCustomerTypes: (v.appliesToCustomerTypes ?? []).join(','), code: m.code, rowVersion: m.rowVersion })}
                            onDone={reload}
                          />
                        )
                      }
                    >
                      <KV data={m} keys={['code', 'name', 'version', 'status', 'aggregationMethod', 'cap', 'scorePrecision', 'appliesToCustomerTypes', 'approvalRequiredLevels', 'effectiveFrom', 'effectiveTo', 'owner', 'rationale', 'createdBy', 'submittedBy', 'approvedBy', 'approvedAt']} />
                    </Section>
                  ),
                },
                {
                  key: 'factors',
                  label: <span data-testid="tab-factors">{t('risk.factorsMappings')}</span>,
                  children: (
                    <Section
                      title={t('risk.factorsMappings')}
                      extra={
                        draft && (
                          <Button icon={<PlusOutlined />} onClick={() => setFactorModal({ open: true })} data-testid="factor-add">
                            {t('risk.addFactor')}
                          </Button>
                        )
                      }
                    >
                      <DataTable<Factor>
                        testId="factors-table"
                        data={m.factors}
                        expandable={{
                          expandedRowRender: (f) => (
                            <Table size="small" rowKey={(x) => `${x.id}-${x.value}`} pagination={false} dataSource={f.mappings} columns={cols('matchType', 'value', 'rangeMin', 'rangeMax', 'score', 'priority', 'label')} />
                          ),
                        }}
                        columns={[
                          ...cols<Factor>('code', 'name', 'categoryCode', 'dataSource', 'factorType', 'weight', 'applicableCustomerTypes', 'missingDataHandling', 'missingDataScore', 'isActive'),
                          {
                            key: 'actions',
                            title: t('f.actions'),
                            render: (_: unknown, f: Factor) =>
                              draft ? (
                                <Space>
                                  <Button size="small" onClick={() => setFactorModal({ open: true, f })} data-testid={`factor-edit-${f.code}`}>
                                    {t('actions.edit')}
                                  </Button>
                                  <ConfirmButton size="small" danger label={t('actions.delete')} onSubmit={() => api.del(`/api/admin/risk-factors/${f.id}`)} onDone={reload} />
                                </Space>
                              ) : null,
                          },
                        ]}
                      />
                    </Section>
                  ),
                },
                {
                  key: 'thresholds',
                  label: <span data-testid="tab-thresholds">{t('risk.thresholds')}</span>,
                  children: <ThresholdEditor m={m} editable={draft} onSaved={reload} />,
                },
                {
                  key: 'overrides',
                  label: <span data-testid="tab-overrides">{t('risk.overridesFloors')}</span>,
                  children: (
                    <>
                      <Section
                        title={t('risk.overrides')}
                        extra={
                          draft && (
                            <ActionButton
                              testId="override-add"
                              label={t('actions.add')}
                              fields={[
                                { name: 'code', label: t('f.code'), required: true },
                                { name: 'conditionDataSource', label: t('f.conditionDataSource'), required: true },
                                { name: 'conditionOperator', label: t('f.conditionOperator'), type: 'select', options: ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'in'].map((x) => ({ value: x, label: t(`ops.${x}`) })) },
                                { name: 'conditionValue', label: t('f.conditionValue'), required: true },
                                { name: 'minimumLevel', label: t('f.minimumLevel'), type: 'select', options: lv(LEVELS), required: true },
                                { name: 'reason', label: t('f.reason'), required: true },
                                { name: 'priority', label: t('f.priority'), type: 'number' },
                                { name: 'requiresApproval', label: t('f.requiresApproval'), type: 'switch' },
                                { name: 'isSanctionsWorkflow', label: t('f.isSanctionsWorkflow'), type: 'switch' },
                              ]}
                              initialValues={{ conditionOperator: 'eq', conditionValue: 'true', minimumLevel: 'High', priority: 1 }}
                              onSubmit={(v) => api.post(`/api/admin/risk-methodologies/${m.id}/overrides`, v)}
                              onDone={reload}
                            />
                          )
                        }
                      >
                        <DataTable
                          testId="overrides-table"
                          data={m.overrides}
                          columns={[
                            ...cols('code', 'conditionDataSource', 'conditionOperator', 'conditionValue', 'minimumLevel', 'reason', 'priority', 'requiresApproval', 'isSanctionsWorkflow', 'isActive'),
                            {
                              key: 'actions',
                              title: t('f.actions'),
                              render: (_: unknown, o: Row) => (draft ? <ConfirmButton size="small" danger label={t('actions.remove')} onSubmit={() => api.del(`/api/admin/risk-methodologies/${m.id}/overrides/${o.id}`)} onDone={reload} /> : null),
                            },
                          ]}
                        />
                      </Section>
                      <Section
                        title={t('risk.floors')}
                        extra={
                          draft && (
                            <ActionButton
                              testId="floor-add"
                              label={t('actions.add')}
                              fields={[
                                { name: 'categoryCode', label: t('f.categoryCode'), required: true },
                                { name: 'triggerLevel', label: t('f.triggerLevel'), type: 'select', options: lv(LEVELS), required: true },
                                { name: 'minimumLevel', label: t('f.minimumLevel'), type: 'select', options: lv(LEVELS), required: true },
                              ]}
                              onSubmit={(v) => api.post(`/api/admin/risk-methodologies/${m.id}/floors`, v)}
                              onDone={reload}
                            />
                          )
                        }
                      >
                        <DataTable testId="floors-table" data={m.floors} columns={cols('categoryCode', 'triggerLevel', 'minimumLevel')} />
                      </Section>
                    </>
                  ),
                },
                { key: 'simulate', label: <span data-testid="tab-simulate">{t('risk.simulate')}</span>, children: <Simulator m={m} /> },
                {
                  key: 'history',
                  label: <span data-testid="tab-history">{t('tabs.versionHistory')}</span>,
                  children: (
                    <DataTable<Methodology>
                      testId="methodology-history"
                      url={`/api/admin/risk-methodologies/history/${encodeURIComponent(m.code)}`}
                      columns={[
                        col('version', { render: (v, r) => <Link to={`/admin/risk-methodologies/${r.id}`}>v{v}</Link> }),
                        ...cols<Methodology>('status', 'createdBy', 'submittedBy', 'approvedBy', 'approvedAt', 'effectiveFrom', 'effectiveTo', 'rationale'),
                      ]}
                    />
                  ),
                },
              ]}
            />
            {factorModal.open && <FactorModal m={m} factor={factorModal.f} open onClose={() => setFactorModal({ open: false })} onSaved={reload} />}
          </Page>
        );
      }}
    </Async>
  );
}
