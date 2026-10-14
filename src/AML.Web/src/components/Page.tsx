import { useState, type ReactNode } from 'react';
import { Button, Card, Form, Space, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import { SpecField, type FieldSpec } from './FormFields';
import type { Query } from '../api/client';

/** Page root with a translated title, optional actions and a data-testid="page-<name>". */
export function Page({ name, title, extra, children, subTitle }: { name: string; title: ReactNode; extra?: ReactNode; children: ReactNode; subTitle?: ReactNode }) {
  return (
    <section data-testid={`page-${name}`} className="page" aria-labelledby={`h-${name}`}>
      <div className="page-header">
        <div>
          <Typography.Title level={3} id={`h-${name}`} style={{ margin: 0 }}>
            {title}
          </Typography.Title>
          {subTitle && <Typography.Text type="secondary">{subTitle}</Typography.Text>}
        </div>
        {extra && (
          <Space wrap className="page-actions">
            {extra}
          </Space>
        )}
      </div>
      {children}
    </section>
  );
}

/** Filter panel built from field specs; emits clean query params on Apply. */
export function FilterBar({ fields, onChange, testId = 'filter-bar', initial }: { fields: FieldSpec[]; onChange: (q: Query) => void; testId?: string; initial?: Record<string, unknown> }) {
  const { t } = useTranslation();
  const [form] = Form.useForm();
  const apply = () => {
    const v = form.getFieldsValue() as Record<string, unknown>;
    const q: Query = {};
    Object.entries(v).forEach(([k, x]) => {
      if (x === undefined || x === null || x === '' || x === false) return;
      q[k] = x as string;
    });
    onChange(q);
  };
  return (
    <Card size="small" className="filter-bar" data-testid={testId} style={{ marginBottom: 12 }}>
      <Form form={form} layout="vertical" onFinish={apply} initialValues={initial}>
        <div className="filter-grid">
          {fields.map((f) => (
            <SpecField key={f.name} f={{ ...f, testId: f.testId ?? `filter-${f.name}` }} />
          ))}
        </div>
        <Space>
          <Button type="primary" htmlType="submit" data-testid="filter-apply">
            {t('actions.apply')}
          </Button>
          <Button
            data-testid="filter-reset"
            onClick={() => {
              form.resetFields();
              onChange({});
            }}
          >
            {t('actions.reset')}
          </Button>
        </Space>
      </Form>
    </Card>
  );
}

/** Small helper for a collapsible-free section card. */
export const Section = ({ title, children, extra, testId }: { title: ReactNode; children: ReactNode; extra?: ReactNode; testId?: string }) => (
  <Card size="small" title={title} extra={extra} style={{ marginBottom: 16 }} data-testid={testId}>
    {children}
  </Card>
);

export function useFilters(initial: Query = {}) {
  return useState<Query>(initial);
}
