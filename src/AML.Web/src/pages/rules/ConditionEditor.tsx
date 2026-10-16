import { useEffect, useState, type ReactNode } from 'react';
import { Button, Input, InputNumber, Select, Space, Switch, Tag } from 'antd';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import type { RuleFieldsMeta } from '../../api/types';

/* eslint-disable @typescript-eslint/no-explicit-any */
export type Node = Record<string, any>;

const Labeled = ({ id, label, children }: { id: string; label: string; children: ReactNode }) => (
  <div>
    <label htmlFor={id} className="sr-label">
      {label}
    </label>
    {children}
  </div>
);

/** Free-text value editor: comma separated for between/in; typed (number/bool) according to the field type. */
function ValueInput({ id, value, type, op, onChange, label, readOnly }: { id: string; value: any; type: string; op?: string; onChange: (v: any) => void; label: string; readOnly?: boolean }) {
  const multi = op === 'between' || op === 'in' || op === 'notIn';
  const toText = (v: any) => (Array.isArray(v) ? v.join(', ') : v === undefined || v === null ? '' : String(v));
  const [text, setText] = useState(toText(value));
  useEffect(() => setText(toText(value)), [JSON.stringify(value)]); // eslint-disable-line react-hooks/exhaustive-deps
  const conv = (s: string): any => {
    const x = s.trim();
    if (type === 'Number' && x !== '' && !isNaN(Number(x))) return Number(x);
    if (type === 'Bool') return x === 'true';
    return x;
  };
  const commit = (s: string) => onChange(multi ? s.split(',').map((p) => conv(p)).filter((p) => p !== '') : conv(s));
  if (type === 'Bool' && !multi)
    return (
      <Labeled id={id} label={label}>
        <Switch id={id} disabled={readOnly} checked={value === true} onChange={(c) => onChange(c)} data-testid={`${id}-value`} />
      </Labeled>
    );
  return (
    <Labeled id={id} label={label}>
      <Input
        id={id}
        data-testid={`${id}-value`}
        disabled={readOnly}
        dir="ltr"
        value={text}
        style={{ width: 180 }}
        placeholder={multi ? 'a, b' : undefined}
        onChange={(e) => {
          setText(e.target.value);
          commit(e.target.value);
        }}
      />
    </Labeled>
  );
}

interface EdProps {
  node: Node;
  onChange: (n: Node) => void;
  onRemove?: () => void;
  meta: RuleFieldsMeta;
  path: string;
  readOnly?: boolean;
}

const fieldType = (meta: RuleFieldsMeta, code?: string) =>
  meta.fields.find((f) => f.code === code)?.type ?? meta.dynamicFields.find((f) => f.code === code)?.type ?? 'Text';

const opsFor = (meta: RuleFieldsMeta, code?: string) => meta.fields.find((f) => f.code === code)?.operators ?? meta.operators;

function FieldCondition({ node, onChange, onRemove, meta, path, readOnly }: EdProps) {
  const { t } = useTranslation();
  const ft = fieldType(meta, node.field);
  const fieldOpts = [...meta.fields.map((f) => f.code), ...meta.dynamicFields.map((f) => f.code)].map((c) => ({ value: c, label: c }));
  return (
    <div className="cond-row" data-testid="cond-field-row">
      <Labeled id={`${path}-field`} label={t('rule.field')}>
        <Select id={`${path}-field`} data-testid={`${path}-field`} disabled={readOnly} style={{ width: 230 }} showSearch value={node.field} options={fieldOpts} onChange={(v) => onChange({ ...node, field: v })} />
      </Labeled>
      <Labeled id={`${path}-op`} label={t('rule.operator')}>
        <Select
          id={`${path}-op`}
          data-testid={`${path}-op`}
          disabled={readOnly}
          style={{ width: 130 }}
          value={node.operator}
          options={opsFor(meta, node.field).map((o) => ({ value: o, label: t(`ops.${o}`) }))}
          onChange={(v) => onChange({ ...node, operator: v })}
        />
      </Labeled>
      <ValueInput id={`${path}-v`} label={t('rule.value')} value={node.value} type={ft} op={node.operator} onChange={(v) => onChange({ ...node, value: v })} readOnly={readOnly} />
      {ft === 'Number' && ['eq', 'neq', 'gt', 'gte', 'lt', 'lte'].includes(node.operator) && <ValueByEditor node={node} onChange={onChange} meta={meta} path={path} readOnly={readOnly} />}
      {onRemove && !readOnly && <Button icon={<DeleteOutlined />} aria-label={t('actions.remove')} onClick={onRemove} />}
    </div>
  );
}

const AGG_EXTRA: Record<string, string[]> = {
  ratio: ['measure', 'numerator', 'denominator'],
  rapidMovement: ['maxMinutes'],
  profileRatio: ['profileField', 'measure'],
  peerRatio: ['peerGroup', 'peerBy', 'minPeers', 'measure'],
  peerPercentile: ['peerBy', 'minPeers', 'measure'],
  benchmarkDeviation: ['benchmark', 'measure', 'numerator', 'denominator'],
  chain: ['maxDepth', 'maxMinutes', 'minHopPct'],
  cycle: ['maxDepth'],
  distinctCount: ['of'],
  consecutiveDays: ['dailyRange'],
  minutesSinceEvent: ['event'],
};

const PEER_ATTRS = ['customer.type', 'customer.region', 'customer.residence', 'customer.nationality', 'customer.riskLevel'];

/** Optional threshold chosen by a text attribute (valueBy): "High=3000, Critical=1000"; the condition's value is the default. */
function ValueByEditor({ node, onChange, meta, path, readOnly }: EdProps) {
  const { t } = useTranslation();
  const toText = (vb?: Node) => (vb?.values ? Object.entries(vb.values).map(([k, v]) => `${k}=${v}`).join(', ') : '');
  const [text, setText] = useState(toText(node.valueBy));
  useEffect(() => setText(toText(node.valueBy)), [JSON.stringify(node.valueBy)]); // eslint-disable-line react-hooks/exhaustive-deps
  const textFields = [...meta.fields, ...meta.dynamicFields].filter((f) => f.type === 'Text').map((f) => ({ value: f.code, label: f.code }));
  const parse = (s: string) =>
    Object.fromEntries(
      s
        .split(',')
        .map((p) => p.split('='))
        .filter((kv) => kv.length === 2 && kv[0].trim() !== '' && !isNaN(Number(kv[1])))
        .map(([k, v]) => [k.trim(), Number(v)]),
    );
  return (
    <>
      <Labeled id={`${path}-vbf`} label={t('rule.valueBy')}>
        <Select
          id={`${path}-vbf`}
          data-testid={`${path}-valueBy`}
          disabled={readOnly}
          allowClear
          showSearch
          style={{ width: 210 }}
          value={node.valueBy?.field}
          options={textFields}
          onChange={(f) => onChange({ ...node, valueBy: f ? { field: f, values: node.valueBy?.values ?? {} } : undefined })}
        />
      </Labeled>
      {node.valueBy && (
        <Labeled id={`${path}-vbv`} label={t('rule.valueByValues')}>
          <Input
            id={`${path}-vbv`}
            data-testid={`${path}-valueBy-values`}
            disabled={readOnly}
            dir="ltr"
            style={{ width: 220 }}
            placeholder="High=3000, Critical=1000"
            value={text}
            onChange={(e) => {
              setText(e.target.value);
              onChange({ ...node, valueBy: { ...node.valueBy, values: parse(e.target.value) } });
            }}
          />
        </Labeled>
      )}
    </>
  );
}

const DISTINCT_OF = ['channel', 'device', 'sourceCountry', 'destinationCountry', 'country', 'type', 'counterparty'];
const SIGNAL_EVENTS = ['NewDevice', 'NewLoginCountry', 'Registration', 'Login', 'SimChange', 'PhoneChange', 'EmailChange', 'AddressChange', 'PasswordReset', 'BeneficiaryAdded', 'DeviceChangeApproved'];

function AggregateCondition({ node, onChange, onRemove, meta, path, readOnly }: EdProps) {
  const { t } = useTranslation();
  const extra = AGG_EXTRA[node.aggregate] ?? [];
  const sub = (key: 'filter' | 'numerator' | 'denominator', label: string) => (
    <div style={{ marginInlineStart: 16 }}>
      <Tag>{label}</Tag>
      {node[key] ? (
        <NodeEditor node={node[key]} meta={meta} path={`${path}-${key}`} readOnly={readOnly} onChange={(n) => onChange({ ...node, [key]: n })} onRemove={() => onChange({ ...node, [key]: undefined })} />
      ) : (
        !readOnly && (
          <Button size="small" icon={<PlusOutlined />} onClick={() => onChange({ ...node, [key]: { field: 'txn.amount', operator: 'gte', value: 0 } })} data-testid={`${path}-add-${key}`}>
            {label}
          </Button>
        )
      )}
    </div>
  );
  return (
    <div className="cond-group" data-testid="cond-aggregate">
      <div className="cond-row">
        <Labeled id={`${path}-agg`} label={t('rule.aggregate')}>
          <Select id={`${path}-agg`} data-testid={`${path}-agg`} disabled={readOnly} style={{ width: 210 }} value={node.aggregate} options={meta.aggregates.map((a) => ({ value: a, label: a }))} onChange={(v) => onChange({ ...node, aggregate: v })} />
        </Labeled>
        <Labeled id={`${path}-op`} label={t('rule.operator')}>
          <Select id={`${path}-op`} data-testid={`${path}-op`} disabled={readOnly} style={{ width: 120 }} value={node.operator} options={['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'between'].map((o) => ({ value: o, label: t(`ops.${o}`) }))} onChange={(v) => onChange({ ...node, operator: v })} />
        </Labeled>
        <ValueInput id={`${path}-v`} label={t('rule.value')} value={node.value} type="Number" op={node.operator} onChange={(v) => onChange({ ...node, value: v })} readOnly={readOnly} />
        <Labeled id={`${path}-dir`} label={t('rule.direction')}>
          <Select id={`${path}-dir`} disabled={readOnly} allowClear style={{ width: 100 }} value={node.direction} options={['in', 'out', 'any'].map((o) => ({ value: o, label: t(`rule.dir.${o}`) }))} onChange={(v) => onChange({ ...node, direction: v })} />
        </Labeled>
        {extra.includes('measure') && (
          <Labeled id={`${path}-measure`} label={t('rule.measure')}>
            <Select id={`${path}-measure`} disabled={readOnly} style={{ width: 110 }} value={node.measure} options={['count', 'amount'].map((o) => ({ value: o, label: t(`rule.measures.${o}`) }))} onChange={(v) => onChange({ ...node, measure: v })} />
          </Labeled>
        )}
        {extra.includes('maxMinutes') && (
          <Labeled id={`${path}-mm`} label={t('rule.maxMinutes')}>
            <InputNumber id={`${path}-mm`} disabled={readOnly} value={node.maxMinutes} onChange={(v) => onChange({ ...node, maxMinutes: v ?? undefined })} />
          </Labeled>
        )}
        {extra.includes('minHopPct') && (
          <Labeled id={`${path}-mhp`} label={t('rule.minHopPct')}>
            <InputNumber id={`${path}-mhp`} disabled={readOnly} min={1} max={100} value={node.minHopPct} onChange={(v) => onChange({ ...node, minHopPct: v ?? undefined })} />
          </Labeled>
        )}
        {extra.includes('peerBy') && (
          <Labeled id={`${path}-pb`} label={t('rule.peerBy')}>
            <Select
              id={`${path}-pb`}
              data-testid={`${path}-peerBy`}
              mode="multiple"
              disabled={readOnly}
              style={{ minWidth: 240 }}
              value={node.peerBy ?? []}
              options={[...PEER_ATTRS, ...meta.dynamicFields.map((f) => f.code)].map((v) => ({ value: v, label: v }))}
              onChange={(v: string[]) => onChange({ ...node, peerBy: v.length ? v : undefined })}
            />
          </Labeled>
        )}
        {extra.includes('minPeers') && (
          <Labeled id={`${path}-mp`} label={t('rule.minPeers')}>
            <InputNumber id={`${path}-mp`} disabled={readOnly} min={1} value={node.minPeers} onChange={(v) => onChange({ ...node, minPeers: v ?? undefined })} />
          </Labeled>
        )}
        {extra.includes('benchmark') && (
          <Labeled id={`${path}-bm`} label={t('rule.benchmark')}>
            <Input id={`${path}-bm`} data-testid={`${path}-benchmark`} disabled={readOnly} dir="ltr" style={{ width: 170 }} value={node.benchmark} onChange={(e) => onChange({ ...node, benchmark: e.target.value.toUpperCase() })} />
          </Labeled>
        )}
        {['eq', 'neq', 'gt', 'gte', 'lt', 'lte'].includes(node.operator) && <ValueByEditor node={node} onChange={onChange} meta={meta} path={path} readOnly={readOnly} />}
        {extra.includes('maxDepth') && (
          <Labeled id={`${path}-md`} label={t('rule.maxDepth')}>
            <InputNumber id={`${path}-md`} disabled={readOnly} value={node.maxDepth} onChange={(v) => onChange({ ...node, maxDepth: v ?? undefined })} />
          </Labeled>
        )}
        {extra.includes('profileField') && (
          <Labeled id={`${path}-pf`} label={t('rule.profileField')}>
            <Input id={`${path}-pf`} disabled={readOnly} value={node.profileField} onChange={(e) => onChange({ ...node, profileField: e.target.value })} />
          </Labeled>
        )}
        {extra.includes('peerGroup') && (
          <Labeled id={`${path}-pg`} label={t('rule.peerGroup')}>
            <Input id={`${path}-pg`} disabled={readOnly} value={node.peerGroup} onChange={(e) => onChange({ ...node, peerGroup: e.target.value })} />
          </Labeled>
        )}
        {extra.includes('of') && (
          <Labeled id={`${path}-of`} label={t('rule.of')}>
            <Select id={`${path}-of`} disabled={readOnly} style={{ minWidth: 160 }} value={node.of} options={DISTINCT_OF.map((v) => ({ value: v, label: v }))} onChange={(v) => onChange({ ...node, of: v })} />
          </Labeled>
        )}
        {extra.includes('event') && (
          <Labeled id={`${path}-ev`} label={t('rule.event')}>
            <Select id={`${path}-ev`} disabled={readOnly} style={{ minWidth: 180 }} value={node.event} options={SIGNAL_EVENTS.map((v) => ({ value: v, label: v }))} onChange={(v) => onChange({ ...node, event: v })} />
          </Labeled>
        )}
        {extra.includes('dailyRange') && (
          <>
            <Labeled id={`${path}-drmin`} label={t('rule.dailyMin')}>
              <InputNumber id={`${path}-drmin`} disabled={readOnly} value={node.dailyRange?.[0]} onChange={(v) => onChange({ ...node, dailyRange: [v ?? 0, node.dailyRange?.[1] ?? 0] })} />
            </Labeled>
            <Labeled id={`${path}-drmax`} label={t('rule.dailyMax')}>
              <InputNumber id={`${path}-drmax`} disabled={readOnly} value={node.dailyRange?.[1]} onChange={(v) => onChange({ ...node, dailyRange: [node.dailyRange?.[0] ?? 0, v ?? 0] })} />
            </Labeled>
          </>
        )}
        {onRemove && !readOnly && <Button icon={<DeleteOutlined />} aria-label={t('actions.remove')} onClick={onRemove} />}
      </div>
      {extra.includes('numerator') ? (
        <>
          {sub('numerator', t('rule.numerator'))}
          {sub('denominator', t('rule.denominator'))}
        </>
      ) : (
        sub('filter', t('rule.filter'))
      )}
    </div>
  );
}

function Group({ node, onChange, onRemove, meta, path, readOnly }: EdProps) {
  const { t } = useTranslation();
  const children: Node[] = node.conditions ?? [];
  const set = (i: number, n: Node) => onChange({ ...node, conditions: children.map((c, j) => (j === i ? n : c)) });
  const add = (n: Node) => onChange({ ...node, conditions: [...children, n] });
  return (
    <div className="cond-group" data-testid="cond-group">
      <Space wrap>
        <Labeled id={`${path}-groupop`} label={t('rule.groupOperator')}>
          <Select
            id={`${path}-groupop`}
            data-testid={`${path}-groupop`}
            disabled={readOnly}
            style={{ width: 100 }}
            value={node.op}
            options={[
              { value: 'and', label: t('rule.and') },
              { value: 'or', label: t('rule.or') },
            ]}
            onChange={(v) => onChange({ ...node, op: v })}
          />
        </Labeled>
        {onRemove && !readOnly && <Button icon={<DeleteOutlined />} aria-label={t('actions.remove')} onClick={onRemove} style={{ marginTop: 20 }} />}
      </Space>
      {children.map((c, i) => (
        <NodeEditor key={i} node={c} meta={meta} path={`${path}-${i}`} readOnly={readOnly} onChange={(n) => set(i, n)} onRemove={() => onChange({ ...node, conditions: children.filter((_, j) => j !== i) })} />
      ))}
      {!readOnly && (
        <Space wrap style={{ marginTop: 6 }}>
          <Button size="small" icon={<PlusOutlined />} data-testid={`${path}-add-condition`} onClick={() => add({ field: 'txn.amount', operator: 'gte', value: 0 })}>
            {t('rule.addCondition')}
          </Button>
          <Button size="small" icon={<PlusOutlined />} data-testid={`${path}-add-aggregate`} onClick={() => add({ aggregate: 'count', operator: 'gte', value: 1 })}>
            {t('rule.addAggregate')}
          </Button>
          <Button size="small" icon={<PlusOutlined />} data-testid={`${path}-add-group`} onClick={() => add({ op: 'or', conditions: [] })}>
            {t('rule.addGroup')}
          </Button>
        </Space>
      )}
    </div>
  );
}

/** Recursive structured editor for a rule condition tree (nested AND/OR groups, field rows, aggregates). */
export function NodeEditor(p: EdProps) {
  if (p.node.op !== undefined || p.node.conditions) return <Group {...p} />;
  if (p.node.aggregate) return <AggregateCondition {...p} />;
  return <FieldCondition {...p} />;
}
