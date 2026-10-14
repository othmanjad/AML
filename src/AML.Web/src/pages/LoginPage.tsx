import { useState } from 'react';
import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { ErrorAlert } from '../components/StateViews';
import { LangSwitch } from '../AppLayout';

export default function LoginPage() {
  const { t } = useTranslation();
  const { login, me } = useAuth();
  const nav = useNavigate();
  const loc = useLocation();
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  const from = (loc.state as { from?: string } | null)?.from ?? '/dashboard';
  if (me) return <Navigate to={from} replace />;

  const submit = async (v: { username: string; password: string }) => {
    setBusy(true);
    setError(null);
    try {
      await login(v.username, v.password);
      nav(from, { replace: true });
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError(0, { detail: String(e) }, 'Error'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-wrap" data-testid="page-login">
      <div style={{ position: 'absolute', top: 16, insetInlineEnd: 16 }}>
        <LangSwitch />
      </div>
      <Card className="login-card">
        <div style={{ textAlign: 'center', marginBottom: 16 }}>
          <img src="/favicon.svg" alt="" width={48} height={48} />
          <Typography.Title level={3} style={{ marginTop: 8 }}>
            {t('app.title')}
          </Typography.Title>
          <Typography.Text type="secondary">{t('login.subtitle')}</Typography.Text>
        </div>
        <Alert type="warning" showIcon message={t('login.devOnly')} description={t('login.devOnlyDetail')} style={{ marginBottom: 16 }} data-testid="dev-login-note" />
        {error && (error.status === 401 ? <Alert type="error" showIcon message={t('login.invalid')} style={{ marginBottom: 12 }} data-testid="state-error" /> : <ErrorAlert error={error} />)}
        <Form layout="vertical" onFinish={submit} requiredMark={false}>
          <Form.Item name="username" label={t('login.username')} rules={[{ required: true, message: t('validation.required', { field: t('login.username') }) }]}>
            <Input data-testid="login-username" autoComplete="username" autoFocus />
          </Form.Item>
          <Form.Item name="password" label={t('login.password')} rules={[{ required: true, message: t('validation.required', { field: t('login.password') }) }]}>
            <Input.Password data-testid="login-password" autoComplete="current-password" />
          </Form.Item>
          <Button type="primary" htmlType="submit" block loading={busy} data-testid="login-submit">
            {t('login.submit')}
          </Button>
        </Form>
      </Card>
    </div>
  );
}
