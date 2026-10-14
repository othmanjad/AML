import type { ReactNode } from 'react';
import { Alert, Button, Empty, Result, Spin, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import type { ApiError } from '../api/client';
import type { AsyncState } from './hooks';

export function StateLoading() {
  const { t } = useTranslation();
  return (
    <div data-testid="state-loading" role="status" aria-live="polite" style={{ padding: 32, textAlign: 'center' }}>
      <Spin />
      <div style={{ marginTop: 8 }}>{t('state.loading')}</div>
    </div>
  );
}

export function StateEmpty({ text }: { text?: string }) {
  const { t } = useTranslation();
  return (
    <div data-testid="state-empty" style={{ padding: 16 }}>
      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={text ?? t('state.empty')} />
    </div>
  );
}

export function StateForbidden() {
  const { t } = useTranslation();
  return (
    <div data-testid="state-forbidden">
      <Result status="403" title={t('state.forbiddenTitle')} subTitle={t('state.forbidden')} />
    </div>
  );
}

/** Correlation ID line shown on operational errors. */
export function Reference({ id }: { id?: string }) {
  const { t } = useTranslation();
  if (!id) return null;
  return (
    <Typography.Text type="secondary" copyable={{ text: id }}>
      {t('state.reference')}: {id}
    </Typography.Text>
  );
}

export function StateError({ error, onRetry }: { error: ApiError; onRetry?: () => void }) {
  const { t } = useTranslation();
  if (error.status === 403) return <StateForbidden />;
  return (
    <div data-testid="state-error" style={{ padding: 8 }}>
      <Alert
        type="error"
        showIcon
        message={error.title || t('state.error')}
        description={
          <div>
            <div>{error.detail || error.message}</div>
            {error.correlationId && (
              <div>
                <Reference id={error.correlationId} />
              </div>
            )}
          </div>
        }
        action={
          onRetry && (
            <Button size="small" onClick={onRetry}>
              {t('actions.retry')}
            </Button>
          )
        }
      />
    </div>
  );
}

/** Inline error used inside forms/modals (no retry). */
export function ErrorAlert({ error }: { error?: ApiError | null }) {
  if (!error) return null;
  return (
    <div data-testid="state-error" style={{ marginBottom: 12 }}>
      <Alert
        type="error"
        showIcon
        message={error.detail || error.title || error.message}
        description={error.correlationId ? <Reference id={error.correlationId} /> : undefined}
      />
    </div>
  );
}

/** Renders loading / error / forbidden / empty states for a useApi() result, then the content. */
export function Async<T>({
  state,
  children,
  isEmpty,
  emptyText,
}: {
  state: AsyncState<T>;
  children: (data: T) => ReactNode;
  isEmpty?: (d: T) => boolean;
  emptyText?: string;
}) {
  if (state.error) return <StateError error={state.error} onRetry={state.reload} />;
  if (state.data === undefined) return state.loading ? <StateLoading /> : <StateEmpty text={emptyText} />;
  const d = state.data;
  const empty = d === null || (Array.isArray(d) && d.length === 0) || (isEmpty ? isEmpty(d) : false);
  if (empty) return <StateEmpty text={emptyText} />;
  return <>{children(d)}</>;
}
