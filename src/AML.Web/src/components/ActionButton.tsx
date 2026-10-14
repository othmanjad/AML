import { useState, type ReactNode } from 'react';
import { Alert, Button, Form, Modal, Tooltip, message, type ButtonProps } from 'antd';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { ErrorAlert } from './StateViews';
import { SpecField, applyServerErrors, parseSpecValues, type FieldSpec } from './FormFields';

export interface ActionButtonProps {
  label: string;
  testId?: string;
  icon?: ReactNode;
  type?: ButtonProps['type'];
  danger?: boolean;
  disabled?: boolean;
  tooltip?: string;
  size?: ButtonProps['size'];
  /** Form fields shown in the modal before running the action. */
  fields?: FieldSpec[];
  initialValues?: object;
  /** High-impact actions: show an explicit confirmation modal (data-testid="confirm-modal"). */
  confirm?: boolean | string;
  title?: string;
  description?: ReactNode;
  width?: number;
  onSubmit: (values: Record<string, any>) => Promise<unknown>; // eslint-disable-line @typescript-eslint/no-explicit-any
  onDone?: (result: any) => void; // eslint-disable-line @typescript-eslint/no-explicit-any
  successMessage?: string;
}

/**
 * Button that optionally opens a modal form and/or confirmation dialog, runs an API call,
 * shows server errors (with correlation reference) inline and maps validation errors to fields.
 */
export function ActionButton(p: ActionButtonProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [extra, setExtra] = useState<string[]>([]);
  const [form] = Form.useForm();
  const needsModal = !!p.confirm || (p.fields?.length ?? 0) > 0;

  const run = async (values: Record<string, unknown>) => {
    setBusy(true);
    setError(null);
    setExtra([]);
    try {
      const r = await p.onSubmit(values);
      message.success(p.successMessage ?? t('msg.done'));
      setOpen(false);
      form.resetFields();
      p.onDone?.(r);
    } catch (e) {
      const err = e instanceof ApiError ? e : new ApiError(0, { detail: String(e) }, 'Error');
      if (needsModal) {
        setError(err);
        setExtra(applyServerErrors(form, err));
      } else {
        message.error(`${err.detail || err.message}${err.correlationId ? ` — ${t('state.reference')}: ${err.correlationId}` : ''}`);
      }
    } finally {
      setBusy(false);
    }
  };

  const onOk = async () => {
    let values: Record<string, unknown> = {};
    if (p.fields?.length) {
      try {
        values = await form.validateFields();
      } catch {
        return; // field-level messages are shown by the form
      }
    }
    let parsed: Record<string, unknown>;
    try {
      parsed = parseSpecValues(p.fields ?? [], values);
    } catch {
      setError(new ApiError(400, { detail: t('validation.json') }, 'Invalid JSON'));
      return;
    }
    await run(parsed);
  };

  const testIdModal = p.confirm ? 'confirm-modal' : 'action-modal';
  const btn = (
    <Button
      data-testid={p.testId}
      icon={p.icon}
      type={p.type}
      danger={p.danger}
      disabled={p.disabled}
      size={p.size}
      loading={!needsModal && busy}
      onClick={() => (needsModal ? (setOpen(true), setError(null), setExtra([])) : run({}))}
    >
      {p.label}
    </Button>
  );

  return (
    <>
      {p.tooltip ? <Tooltip title={p.tooltip}>{p.disabled ? <span tabIndex={0}>{btn}</span> : btn}</Tooltip> : btn}
      {needsModal && (
        <Modal
          open={open}
          title={p.title ?? p.label}
          onCancel={() => setOpen(false)}
          onOk={onOk}
          confirmLoading={busy}
          width={p.width}
          destroyOnHidden
          okText={p.confirm ? t('actions.confirm') : t('actions.submit')}
          cancelText={t('actions.cancel')}
          okButtonProps={{ 'data-testid': p.confirm ? 'confirm-ok' : 'action-ok', danger: p.danger } as ButtonProps}
          cancelButtonProps={{ 'data-testid': p.confirm ? 'confirm-cancel' : 'action-cancel' } as ButtonProps}
          modalRender={(node) => <div data-testid={testIdModal}>{node}</div>}
        >
          {p.confirm && (
            <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={typeof p.confirm === 'string' ? p.confirm : t('msg.confirmAction', { action: p.label })} />
          )}
          {p.description}
          <ErrorAlert error={error} />
          {extra.length > 0 && <Alert type="error" message={extra.join(' · ')} style={{ marginBottom: 12 }} />}
          {p.fields && p.fields.length > 0 && (
            <Form form={form} layout="vertical" initialValues={p.initialValues} preserve={false}>
              {p.fields.map((f) => (
                <SpecField key={f.name} f={f} />
              ))}
            </Form>
          )}
        </Modal>
      )}
    </>
  );
}

/** Shorthand for a confirmed high-impact action. */
export const ConfirmButton = (p: Omit<ActionButtonProps, 'confirm'> & { confirm?: string }) => <ActionButton {...p} confirm={p.confirm ?? true} />;
