import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Form, Input, InputNumber, List, Select, Space, Switch, Tabs, Typography } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, ApiError } from '../../api/client';
import type { Row, Rule, RuleFieldsMeta, RuleVersion } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page, Section } from '../../components/Page';
import { Async, ErrorAlert, StateLoading } from '../../components/StateViews';
import { useApi } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { KV, SampleTag, StatusTag, col, cols, tv } from '../../components/display';
import { NodeEditor, type Node } from './ConditionEditor';

const DEFAULT_DEF: Node = {
  subject: 'Customer',
  reversalHandling: 'Net',
  window: { type: 'Rolling', hours: 24 },
  condition: { op: 'and', conditions: [{ aggregate: 'count', operator: 'gte', value: 3, filter: { field: 'txn.baseAmount', operator: 'between', value: [900, 999.99] } }] },
  action: { severity: 'Medium' },
};
const SEVERITIES = ['Low', 'Medium', 'High', 'Critical'];
const lv = (xs: string[]) => xs.map((x) => ({ value: x, label: tv(x) }));
const windowFields = (from: string, to: string) => ({ from, to });

function DefinitionEditor({ def, setDef, meta, readOnly }: { def: Node; setDef: (d: Node) => void; meta: RuleFieldsMeta; readOnly: boolean }) {
  const { t } = useTranslation();
  const [mode, setMode] = useState('builder');
  const [json, setJson] = useState(JSON.stringify(def, null, 2));
  const [jsonErr, setJsonErr] = useState<string | null>(null);
  useEffect(() => {
    if (mode === 'builder') setJson(JSON.stringify(def, null, 2));
  }, [def, mode]);
  const w = (def.window ?? {}) as Node;
  const setW = (patch: Node) => setDef({ ...def, window: { ...w, ...patch } });
  return (
    <Tabs
      activeKey={mode}
      onChange={(k) => {
        if (k === 'builder' && jsonErr) return;
        setMode(k);
      }}
      items={[
        {
          key: 'builder',
          label: <span data-testid="tab-builder">{t('rule.builder')}</span>,
          children: (
            <div data-testid="condition-builder">
              <div className="form-grid">
                <div>
                  <label className="sr-label" htmlFor="def-subject">{t('rule.subject')}</label>
                  <Select id="def-subject" data-testid="def-subject" disabled={readOnly} style={{ width: '100%' }} value={def.subject} options={lv(['Customer', 'Wallet', 'Agent', 'Device', 'Entity'])} onChange={(v) => setDef({ ...def, subject: v })} />
                </div>
                <div>
                  <label className="sr-label" htmlFor="def-reversal">{t('rule.reversalHandling')}</label>
                  <Select id="def-reversal" disabled={readOnly} style={{ width: '100%' }} value={def.reversalHandling} options={lv(['Net', 'Exclude', 'Include'])} onChange={(v) => setDef({ ...def, reversalHandling: v })} />
                </div>
                <div>
                  <label className="sr-label" htmlFor="def-wtype">{t('rule.windowType')}</label>
                  <Select
                    id="def-wtype"
                    data-testid="def-window-type"
                    disabled={readOnly}
                    style={{ width: '100%' }}
                    allowClear
                    value={def.window ? w.type : undefined}
                    options={lv(['Rolling', 'Calendar'])}
                    onChange={(v) => setDef({ ...def, window: v ? (v === 'Rolling' ? { type: 'Rolling', hours: 24 } : { type: 'Calendar', unit: 'Day' }) : undefined })}
                  />
                </div>
                {w.type === 'Rolling' && (
                  <>
                    <div>
                      <label className="sr-label" htmlFor="def-hours">{t('rule.hours')}</label>
                      <InputNumber id="def-hours" data-testid="def-window-hours" disabled={readOnly} style={{ width: '100%' }} value={w.hours} onChange={(v) => setW({ hours: v ?? undefined })} />
                    </div>
                    <div>
                      <label className="sr-label" htmlFor="def-days">{t('rule.days')}</label>
                      <InputNumber id="def-days" disabled={readOnly} style={{ width: '100%' }} value={w.days} onChange={(v) => setW({ days: v ?? undefined })} />
                    </div>
                  </>
                )}
                {w.type === 'Calendar' && (
                  <div>
                    <label className="sr-label" htmlFor="def-unit">{t('rule.unit')}</label>
                    <Select id="def-unit" disabled={readOnly} style={{ width: '100%' }} value={w.unit} options={lv(['Day', 'Week', 'Month'])} onChange={(v) => setW({ unit: v })} />
                  </div>
                )}
                <div>
                  <label className="sr-label" htmlFor="def-sev">{t('f.severity')}</label>
                  <Select id="def-sev" disabled={readOnly} style={{ width: '100%' }} value={def.action?.severity} options={lv(SEVERITIES)} onChange={(v) => setDef({ ...def, action: { ...(def.action ?? {}), severity: v } })} />
                </div>
                <div>
                  <label className="sr-label" htmlFor="def-lookback">{t('rule.lookbackDays')}</label>
                  <InputNumber id="def-lookback" disabled={readOnly} style={{ width: '100%' }} value={def.lookbackDays} onChange={(v) => setDef({ ...def, lookbackDays: v ?? undefined })} />
                </div>
                <div>
                  <label className="sr-label" htmlFor="def-rt">{t('rule.realtimeCapable')}</label>
                  <Switch id="def-rt" disabled={readOnly} checked={!!def.realtimeCapable} onChange={(c) => setDef({ ...def, realtimeCapable: c || undefined })} />
                </div>
              </div>
              <Typography.Title level={5} style={{ marginTop: 12 }}>
                {t('rule.condition')}
              </Typography.Title>
              <NodeEditor node={def.condition ?? { op: 'and', conditions: [] }} meta={meta} path="c" readOnly={readOnly} onChange={(n) => setDef({ ...def, condition: n })} />
            </div>
          ),
        },
        {
          key: 'json',
          label: <span data-testid="tab-json">{t('rule.json')}</span>,
          children: (
            <div>
              <label className="sr-label" htmlFor="rule-json">{t('rule.jsonDefinition')}</label>
              <Input.TextArea
                id="rule-json"
                data-testid="rule-json"
                className="mono"
                rows={20}
                value={json}
                readOnly={readOnly}
                onChange={(e) => {
                  setJson(e.target.value);
                  try {
                    const d = JSON.parse(e.target.value);
                    setJsonErr(null);
                    setDef(d);
                  } catch (x) {
                    setJsonErr((x as Error).message);
                  }
                }}
              />
              {jsonErr && <Alert type="error" showIcon message={`${t('validation.json')}: ${jsonErr}`} style={{ marginTop: 8 }} data-testid="json-error" />}
            </div>
          ),
        },
      ]}
    />
  );
}

function RunResult({ r }: { r: Row }) {
  return (
    <div data-testid="run-result" style={{ marginTop: 12 }}>
      <KV data={r} keys={['executionId', 'status', 'recordsEvaluated', 'matches', 'alertsCreated', 'alertsDeduplicated', 'errors', 'correlationId']} />
    </div>
  );
}

export default function RuleBuilder() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  const meta = useApi<RuleFieldsMeta>('/api/config/rule-fields');
  const ruleSt = useApi<Rule>(id ? `/api/admin/aml-rules/${id}` : null);
  const rule = ruleSt.data;
  const [def, setDef] = useState<Node>(DEFAULT_DEF);
  const [validation, setValidation] = useState<Row | null>(null);
  const [runResult, setRunResult] = useState<Row | null>(null);
  const [err, setErr] = useState<ApiError | null>(null);
  const [execKey, setExecKey] = useState(0);
  const [metaForm] = Form.useForm();

  const version: RuleVersion | undefined = useMemo(() => {
    if (!rule) return undefined;
    const by = (s: string) => rule.versions.find((v) => v.status === s);
    return by('Draft') ?? by('PendingApproval') ?? by('Approved') ?? by('Active') ?? [...rule.versions].sort((a, b) => b.version - a.version)[0];
  }, [rule]);

  useEffect(() => {
    if (version) setDef(version.definition as Node);
  }, [version]);

  const isNew = !id;
  const editable = can('rules.config') && (isNew || version?.status === 'Draft');
  const reload = () => {
    ruleSt.reload();
    setExecKey((x) => x + 1);
  };

  const save = async () => {
    setErr(null);
    try {
      if (isNew) {
        const m = await metaForm.validateFields();
        const r = await api.post<Rule>('/api/admin/aml-rules', { ...m, definition: def });
        nav(`/rules/${r.id}`);
      } else if (version) {
        await api.put(`/api/admin/aml-rules/${rule!.id}/versions/${version.id}`, { definition: def, severity: def.action?.severity, rowVersion: version.rowVersion });
        reload();
      }
    } catch (e) {
      if (e instanceof ApiError) setErr(e);
    }
  };

  if (meta.loading && !meta.data) return <StateLoading />;

  const body = (
    <Page
      name="rule-builder"
      title={isNew ? t('rule.newRule') : `${rule?.code} — ${rule?.name}`}
      subTitle={
        rule && (
          <Space wrap>
            <StatusTag value={rule.status} />
            {version && (
              <span>
                v{version.version} <StatusTag value={version.status} />
              </span>
            )}
            <SampleTag show={rule.isSample} />
          </Space>
        )
      }
      extra={
        <>
          {editable && (
            <Button type="primary" onClick={save} data-testid="rule-save">
              {isNew ? t('actions.create') : t('actions.saveDraft')}
            </Button>
          )}
          {!isNew && can('rules.config') && (
            <>
              <ActionButton
                testId="rule-validate"
                label={t('actions.validate')}
                onSubmit={() => api.post<Row>(`/api/admin/aml-rules/${rule!.id}/validate`, { versionId: version?.id, definition: def })}
                onDone={(r: Row) => setValidation(r)}
              />
              <ActionButton
                testId="rule-simulate"
                label={t('rule.simulate')}
                fields={[
                  { name: 'from', label: t('f.dateFrom'), type: 'datetime', required: true },
                  { name: 'to', label: t('f.dateTo'), type: 'datetime', required: true },
                ]}
                onSubmit={(v) => api.post<Row>(`/api/admin/aml-rules/${rule!.id}/simulate`, { ...windowFields(v.from, v.to), versionId: version?.id })}
                onDone={(r: Row) => (setRunResult(r), setExecKey((x) => x + 1))}
              />
              <ActionButton
                testId="rule-backtest"
                label={t('rule.backtest')}
                fields={[
                  { name: 'from', label: t('f.dateFrom'), type: 'datetime', required: true },
                  { name: 'to', label: t('f.dateTo'), type: 'datetime', required: true },
                  { name: 'createAlerts', label: t('rule.createBacktestAlerts'), type: 'switch' },
                ]}
                onSubmit={(v) => api.post<Row>(`/api/admin/aml-rules/${rule!.id}/run-backtest`, { ...windowFields(v.from, v.to), versionId: version?.id, createAlerts: !!v.createAlerts })}
                onDone={(r: Row) => (setRunResult(r), setExecKey((x) => x + 1))}
              />
            </>
          )}
          {!isNew && version?.status === 'Draft' && can('rules.config') && (
            <ActionButton
              testId="rule-submit"
              label={t('actions.submitForApproval')}
              fields={[
                { name: 'rationale', label: t('f.rationale'), type: 'textarea', required: true },
                { name: 'testEvidence', label: t('f.testEvidence'), type: 'textarea', required: true },
                { name: 'expectedImpact', label: t('f.expectedImpact'), type: 'textarea', required: true },
              ]}
              onSubmit={(v) => api.post(`/api/admin/aml-rules/${rule!.id}/submit`, v)}
              onDone={reload}
            />
          )}
          {!isNew && version?.status === 'PendingApproval' && can('rules.approve') && (
            <>
              <ConfirmButton
                testId="rule-approve"
                type="primary"
                label={t('actions.approve')}
                confirm={t('rule.confirmApprove')}
                fields={[{ name: 'comment', label: t('f.comment'), type: 'textarea' }]}
                onSubmit={(v) => api.post(`/api/admin/aml-rules/${rule!.id}/approve`, v)}
                onDone={reload}
              />
              <ActionButton
                testId="rule-reject"
                danger
                label={t('actions.reject')}
                fields={[{ name: 'comment', label: t('f.comment'), type: 'textarea', required: true }]}
                onSubmit={(v) => api.post(`/api/admin/aml-rules/${rule!.id}/reject`, v)}
                onDone={reload}
              />
            </>
          )}
          {!isNew && version?.status === 'Approved' && can('rules.config') && (
            <ConfirmButton testId="rule-publish" type="primary" label={t('actions.publish')} confirm={t('rule.confirmPublish')} onSubmit={() => api.post(`/api/admin/aml-rules/${rule!.id}/publish`)} onDone={reload} />
          )}
          {!isNew && can('rules.config') && rule && (rule.status === 'Active' || rule.status === 'Disabled') && (
            <ConfirmButton
              testId="rule-toggle"
              danger={rule.status === 'Active'}
              label={rule.status === 'Active' ? t('actions.disable') : t('actions.enable')}
              fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea', required: true }]}
              onSubmit={(v) => api.post(`/api/admin/aml-rules/${rule.id}/enabled`, { enabled: rule.status !== 'Active', reason: v.reason })}
              onDone={reload}
            />
          )}
          {!isNew && can('rules.run') && rule?.status === 'Active' && (
            <ActionButton
              testId="rule-run"
              label={t('rule.run')}
              confirm
              fields={[
                { name: 'from', label: t('f.dateFrom'), type: 'datetime', required: true },
                { name: 'to', label: t('f.dateTo'), type: 'datetime', required: true },
              ]}
              onSubmit={(v) => api.post<Row>(`/api/admin/aml-rules/${rule.id}/run`, windowFields(v.from, v.to))}
              onDone={(r: Row) => (setRunResult(r), setExecKey((x) => x + 1))}
            />
          )}
          {!isNew && can('rules.config') && version && version.status !== 'Draft' && !rule?.versions.some((v) => ['Draft', 'PendingApproval', 'Approved'].includes(v.status)) && (
            <ActionButton testId="rule-new-version" label={t('actions.newVersion')} onSubmit={() => api.post(`/api/admin/aml-rules/${rule!.id}/versions`)} onDone={reload} />
          )}
        </>
      }
    >
      <ErrorAlert error={err} />
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
            (validation.errors as Row[])?.length ? (
              <List
                size="small"
                data-testid="validation-errors"
                dataSource={validation.errors as Row[]}
                renderItem={(e) => (
                  <List.Item>
                    <code>{e.path}</code> — {e.message}
                  </List.Item>
                )}
              />
            ) : undefined
          }
        />
      )}
      {runResult && <RunResult r={runResult} />}
      {isNew && (
        <Card size="small" title={t('rule.details')} style={{ marginBottom: 12 }}>
          <Form form={metaForm} layout="vertical" initialValues={{ ruleType: 'Batch', severity: 'Medium' }}>
            <div className="form-grid">
              <Form.Item name="code" label={t('f.code')} rules={[{ required: true, message: t('validation.required', { field: t('f.code') }) }]}>
                <Input data-testid="rule-code" />
              </Form.Item>
              <Form.Item name="name" label={t('f.name')} rules={[{ required: true, message: t('validation.required', { field: t('f.name') }) }]}>
                <Input data-testid="rule-name" />
              </Form.Item>
              <Form.Item name="category" label={t('f.category')} rules={[{ required: true, message: t('validation.required', { field: t('f.category') }) }]}>
                <Input data-testid="rule-category" />
              </Form.Item>
              <Form.Item name="ruleType" label={t('f.ruleType')}>
                <Select data-testid="rule-type" options={lv(['Batch', 'Realtime'])} />
              </Form.Item>
              <Form.Item name="severity" label={t('f.severity')}>
                <Select data-testid="rule-severity" options={lv(SEVERITIES)} />
              </Form.Item>
              <Form.Item name="typologyCodes" label={t('f.typologyCodes')}>
                <Input data-testid="rule-typologies" />
              </Form.Item>
            </div>
            <Form.Item name="description" label={t('f.description')}>
              <Input.TextArea rows={2} data-testid="rule-description" />
            </Form.Item>
          </Form>
        </Card>
      )}
      {!editable && !isNew && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('rule.readOnly')} />}
      <Section title={t('rule.definition')}>{meta.data ? <DefinitionEditor def={def} setDef={setDef} meta={meta.data} readOnly={!editable} /> : <ErrorAlert error={meta.error} />}</Section>
      {rule && (
        <>
          <Section title={t('tabs.versionHistory')}>
            <DataTable<RuleVersion>
              testId="rule-versions"
              data={rule.versions}
              columns={[col('version', { render: (v) => `v${v}` }), ...cols<RuleVersion>('status', 'severity', 'effectiveFrom', 'createdBy', 'submittedBy', 'approvedBy', 'publishedBy', 'rationale', 'validationResult')]}
            />
          </Section>
          <Section title={t('rule.executions')}>
            <DataTable testId="rule-executions" url="/api/admin/aml-rules/executions" params={{ ruleId: rule.id }} reloadKey={execKey} columns={cols('id', 'ruleVersion', 'mode', 'status', 'windowStart', 'windowEnd', 'recordsEvaluated', 'matches', 'alertsCreated', 'errorCount', 'startedAt', 'completedAt', 'correlationId')} />
          </Section>
        </>
      )}
    </Page>
  );

  return id ? <Async state={ruleSt}>{() => body}</Async> : body;
}
