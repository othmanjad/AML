import { Card, DatePicker, Form, Input, InputNumber, Select, Switch } from 'antd';
import { useTranslation } from 'react-i18next';
import type { FormField, FormMetadata } from '../api/types';
import { useLookupOptions } from './hooks';
import { dateProps } from './FormFields';

export const fieldLabel = (f: { labelEn: string; labelAr?: string | null }, lang: string) => (lang === 'ar' ? f.labelAr || f.labelEn : f.labelEn);

const parseValidation = (json?: string | null): { regex?: string; min?: number; max?: number; maxLength?: number } => {
  if (!json) return {};
  try {
    return JSON.parse(json);
  } catch {
    return {};
  }
};

/**
 * One metadata-driven input. The control is chosen from dataType; the input carries data-testid="field-<CODE>".
 * Labels are taken from labelEn/labelAr; nothing about a specific customer field is hardcoded.
 */
export function DynamicField({ field, namePrefix = ['attributes'], serverError }: { field: FormField; namePrefix?: (string | number)[]; serverError?: string[] }) {
  const { t, i18n } = useTranslation();
  const countries = useLookupOptions('COUNTRY');
  const currencies = useLookupOptions('CURRENCY');
  const label = fieldLabel(field, i18n.language);
  const tid = `field-${field.code}`;
  const v = parseValidation(field.validationJson);
  const rules = [
    ...(field.required ? [{ required: true, message: t('validation.required', { field: label }) }] : []),
    ...(v.regex ? [{ pattern: new RegExp(v.regex), message: t('validation.pattern', { field: label }) }] : []),
    ...(field.dataType === 'Email' ? [{ type: 'email' as const, message: t('validation.email') }] : []),
    ...(v.maxLength ? [{ max: v.maxLength, message: t('validation.maxLength', { max: v.maxLength }) }] : []),
  ];
  const item = {
    name: [...namePrefix, field.code],
    label,
    rules,
    tooltip: field.description || undefined,
    required: field.required,
    validateStatus: serverError ? ('error' as const) : undefined,
    help: serverError?.join(' '),
  };
  const options = field.options
    .filter((o) => o.isActive)
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((o) => ({ value: o.optionCode, label: fieldLabel(o, i18n.language) }));

  switch (field.dataType) {
    case 'LongText':
      return (
        <Form.Item {...item}>
          <Input.TextArea data-testid={tid} rows={3} />
        </Form.Item>
      );
    case 'Integer':
    case 'Decimal':
      return (
        <Form.Item {...item}>
          <InputNumber data-testid={tid} style={{ width: '100%' }} precision={field.dataType === 'Integer' ? 0 : undefined} min={v.min} max={v.max} />
        </Form.Item>
      );
    case 'Date':
    case 'DateTime':
      return (
        <Form.Item {...item} {...dateProps(field.dataType === 'Date' ? 'date' : 'datetime')}>
          <DatePicker data-testid={tid} showTime={field.dataType === 'DateTime'} style={{ width: '100%' }} />
        </Form.Item>
      );
    case 'Boolean':
      return (
        <Form.Item {...item} valuePropName="checked">
          <Switch data-testid={tid} />
        </Form.Item>
      );
    case 'SingleSelect':
    case 'MultiSelect':
      return (
        <Form.Item {...item}>
          <Select data-testid={tid} options={options} mode={field.dataType === 'MultiSelect' ? 'multiple' : undefined} allowClear showSearch optionFilterProp="label" />
        </Form.Item>
      );
    case 'Country':
    case 'Currency':
      return (
        <Form.Item {...item}>
          <Select data-testid={tid} options={field.dataType === 'Country' ? countries : currencies} allowClear showSearch optionFilterProp="label" />
        </Form.Item>
      );
    default: // ShortText, Identifier, Phone, Email, DocumentReference
      return (
        <Form.Item {...item}>
          <Input data-testid={tid} type={field.dataType === 'Email' ? 'email' : field.dataType === 'Phone' ? 'tel' : 'text'} autoComplete="off" />
        </Form.Item>
      );
  }
}

/** Renders all groups (in returned order) and their fields from form metadata. */
export function DynamicForm({ meta, namePrefix = ['attributes'], serverErrors = {} }: { meta: FormMetadata; namePrefix?: (string | number)[]; serverErrors?: Record<string, string[]> }) {
  const { i18n } = useTranslation();
  const byCode = new Map(meta.fields.map((f) => [f.code, f]));
  const placed = new Set<string>();
  const groups = meta.groups.map((g) => {
    const fields = g.fieldCodes.map((c) => byCode.get(c)).filter((f): f is FormField => !!f);
    fields.forEach((f) => placed.add(f.code));
    return { g, fields };
  });
  const leftovers = meta.fields.filter((f) => !placed.has(f.code));
  return (
    <div data-testid="dynamic-form">
      {groups
        .filter((x) => x.fields.length > 0)
        .map(({ g, fields }) => (
          <Card key={g.code} size="small" title={fieldLabel(g, i18n.language)} style={{ marginBottom: 12 }} data-testid={`group-${g.code}`}>
            <div className="form-grid">
              {fields.map((f) => (
                <DynamicField key={f.code} field={f} namePrefix={namePrefix} serverError={serverErrors[f.code]} />
              ))}
            </div>
          </Card>
        ))}
      {leftovers.length > 0 && (
        <Card size="small" style={{ marginBottom: 12 }}>
          <div className="form-grid">
            {leftovers.map((f) => (
              <DynamicField key={f.code} field={f} namePrefix={namePrefix} serverError={serverErrors[f.code]} />
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}

/** Initial form values from existing attribute values (masked values are never pre-filled). */
export const attributeInitialValues = (attrs: Array<{ code: string; value: unknown; masked: boolean }>) =>
  Object.fromEntries(attrs.filter((a) => !a.masked && a.value !== null && a.value !== undefined).map((a) => [a.code, a.value]));

/** Drops empty values so only provided attributes are submitted. */
export const cleanAttributes = (vals: Record<string, unknown> | undefined) =>
  Object.fromEntries(Object.entries(vals ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== '' && !(Array.isArray(v) && v.length === 0)));
