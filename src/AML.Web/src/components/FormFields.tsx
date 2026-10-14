import { Checkbox, DatePicker, Form, Input, InputNumber, Select, Switch } from 'antd';
import type { FormInstance } from 'antd';
import dayjs from 'dayjs';
import i18n from '../i18n';
import type { ApiError } from '../api/client';

export interface FieldSpec {
  name: string;
  label: string;
  type?: 'text' | 'textarea' | 'number' | 'select' | 'multiselect' | 'tags' | 'date' | 'datetime' | 'switch' | 'checkbox' | 'json' | 'password' | 'file';
  options?: Array<{ value: string | number; label: string }>;
  required?: boolean;
  help?: string;
  rows?: number;
  testId?: string;
  placeholder?: string;
}

const dateProps = (type: 'date' | 'datetime') => ({
  getValueProps: (v: unknown) => ({ value: v ? dayjs(v as string) : null }),
  normalize: (d: dayjs.Dayjs | null) => (d ? (type === 'date' ? d.format('YYYY-MM-DD') : d.toDate().toISOString()) : null),
});

/** Renders a single generic form control; values are kept JSON-serialisable (dates as ISO strings). */
export function SpecField({ f }: { f: FieldSpec }) {
  const tid = f.testId ?? `input-${f.name}`;
  const rules = [
    ...(f.required ? [{ required: true, message: i18n.t('validation.required', { field: f.label }) }] : []),
    ...(f.type === 'json'
      ? [
          {
            validator: async (_: unknown, v: string) => {
              if (!v) return;
              JSON.parse(v);
            },
            message: i18n.t('validation.json'),
          },
        ]
      : []),
  ];
  const common = { name: f.name, label: f.label, rules, extra: f.help };
  switch (f.type) {
    case 'textarea':
    case 'json':
      return (
        <Form.Item {...common}>
          <Input.TextArea data-testid={tid} rows={f.rows ?? (f.type === 'json' ? 8 : 3)} placeholder={f.placeholder} className={f.type === 'json' ? 'mono' : undefined} />
        </Form.Item>
      );
    case 'number':
      return (
        <Form.Item {...common}>
          <InputNumber data-testid={tid} style={{ width: '100%' }} />
        </Form.Item>
      );
    case 'select':
    case 'multiselect':
    case 'tags':
      return (
        <Form.Item {...common}>
          <Select
            data-testid={tid}
            options={f.options}
            mode={f.type === 'multiselect' ? 'multiple' : f.type === 'tags' ? 'tags' : undefined}
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={f.placeholder}
          />
        </Form.Item>
      );
    case 'date':
    case 'datetime':
      return (
        <Form.Item {...common} {...dateProps(f.type)}>
          <DatePicker data-testid={tid} showTime={f.type === 'datetime'} style={{ width: '100%' }} />
        </Form.Item>
      );
    case 'switch':
      return (
        <Form.Item {...common} valuePropName="checked">
          <Switch data-testid={tid} />
        </Form.Item>
      );
    case 'checkbox':
      return (
        <Form.Item name={f.name} valuePropName="checked" rules={rules} extra={f.help}>
          <Checkbox data-testid={tid}>{f.label}</Checkbox>
        </Form.Item>
      );
    case 'password':
      return (
        <Form.Item {...common}>
          <Input.Password data-testid={tid} autoComplete="new-password" />
        </Form.Item>
      );
    default:
      return (
        <Form.Item {...common}>
          <Input data-testid={tid} placeholder={f.placeholder} />
        </Form.Item>
      );
  }
}

export { dateProps };

/** Converts json-typed fields from text to objects before submission. */
export const parseSpecValues = (fields: FieldSpec[], values: Record<string, unknown>) => {
  const out: Record<string, unknown> = { ...values };
  fields.forEach((f) => {
    if (f.type === 'json' && typeof out[f.name] === 'string') out[f.name] = out[f.name] ? JSON.parse(out[f.name] as string) : undefined;
  });
  return out;
};

/** Maps server validation errors (keys are field names/codes, any case, possibly dotted) to form fields. */
export const applyServerErrors = (form: FormInstance, err: ApiError, resolve?: (key: string) => (string | number)[] | null) => {
  if (!err.errors) return [] as string[];
  const unmatched: string[] = [];
  const names = Object.keys(form.getFieldsValue(true) ?? {});
  const fields: { name: (string | number)[]; errors: string[] }[] = [];
  Object.entries(err.errors).forEach(([key, msgs]) => {
    const custom = resolve?.(key);
    if (custom) {
      fields.push({ name: custom, errors: msgs });
      return;
    }
    const last = key.split('.').pop() ?? key;
    const match = names.find((n) => n.toLowerCase() === last.toLowerCase());
    if (match) fields.push({ name: [match], errors: msgs });
    else unmatched.push(...msgs.map((m) => `${key}: ${m}`));
  });
  form.setFields(fields);
  return unmatched;
};
