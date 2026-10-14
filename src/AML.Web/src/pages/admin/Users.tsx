import { useState } from 'react';
import { Space, Table, Tabs } from 'antd';
import { CheckOutlined, PlusOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { Async } from '../../components/StateViews';
import { useApi } from '../../components/hooks';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { col, cols, fl } from '../../components/display';
import type { FieldSpec } from '../../components/FormFields';

export default function UsersPage() {
  const { t } = useTranslation();
  const [n, setN] = useState(0);
  const roles = useApi<Row>('/api/admin/roles');
  const bump = () => setN((x) => x + 1);
  const roleOpts = ((roles.data?.roles ?? []) as Row[]).map((r) => ({ value: r.code, label: r.name ?? r.code }));
  const fields = (create: boolean): FieldSpec[] => [
    { name: 'username', label: t('f.username'), required: true },
    { name: 'displayName', label: t('f.displayName'), required: true },
    { name: 'email', label: t('f.email') },
    { name: 'password', label: t('f.password'), type: 'password', required: create, help: create ? undefined : t('users.passwordHelp') },
    { name: 'team', label: t('f.team') },
    { name: 'dataScopeRegion', label: t('f.dataScopeRegion') },
    { name: 'roles', label: t('f.roles'), type: 'multiselect', options: roleOpts, required: true },
    { name: 'isActive', label: t('f.isActive'), type: 'switch' },
  ];
  return (
    <Page
      name="users"
      title={t('nav.users')}
      extra={<ActionButton testId="user-create" type="primary" icon={<PlusOutlined />} label={t('users.create')} fields={fields(true)} initialValues={{ isActive: true, roles: [] }} onSubmit={(v) => api.post('/api/admin/users', v)} onDone={bump} />}
    >
      <Tabs
        items={[
          {
            key: 'users',
            label: <span data-testid="tab-users">{t('users.users')}</span>,
            children: (
              <DataTable
                testId="users-table"
                url="/api/admin/users"
                reloadKey={n}
                columns={[
                  ...cols('username', 'displayName', 'email', 'isActive', 'team', 'dataScopeRegion', 'roles', 'createdAt', 'lastLoginAt'),
                  {
                    key: 'actions',
                    title: t('f.actions'),
                    render: (_: unknown, r: Row) => (
                      <Space>
                        <ActionButton
                          size="small"
                          testId={`user-edit-${r.username}`}
                          label={t('actions.edit')}
                          fields={fields(false)}
                          initialValues={{ ...r, password: undefined }}
                          onSubmit={(v) => api.put(`/api/admin/users/${r.id}`, { ...v, password: v.password || undefined })}
                          onDone={bump}
                        />
                        <ConfirmButton size="small" danger testId={`user-revoke-${r.username}`} label={t('users.revoke')} onSubmit={() => api.post(`/api/admin/users/${r.id}/revoke-sessions`)} />
                      </Space>
                    ),
                  },
                ]}
              />
            ),
          },
          {
            key: 'matrix',
            label: <span data-testid="tab-permission-matrix">{t('users.matrix')}</span>,
            children: (
              <Async state={roles}>
                {(d) => {
                  const rs = d.roles as Row[];
                  const perms = (d.permissions as Row[]).map((p) => p.code as string);
                  return (
                    <Table
                      data-testid="permission-matrix"
                      className="matrix"
                      size="small"
                      bordered
                      pagination={false}
                      scroll={{ x: 'max-content', y: 600 }}
                      rowKey="perm"
                      dataSource={perms.map((p) => ({ perm: p }))}
                      columns={[
                        col<Row>('perm', { title: fl('permission'), fixed: 'left', render: (v) => <code>{v}</code> }),
                        ...rs.map((r) => ({
                          key: r.code,
                          title: r.code,
                          render: (_: unknown, row: Row) =>
                            (r.permissions as string[]).includes(row.perm) ? <CheckOutlined aria-label={t('common.yes')} style={{ color: '#1a7f37' }} /> : <span aria-label={t('common.no')}>·</span>,
                        })),
                      ]}
                    />
                  );
                }}
              </Async>
            ),
          },
        ]}
      />
    </Page>
  );
}
