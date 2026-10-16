import { useMemo, useState } from 'react';
import { Alert, Button, Select, Space, Tabs, Tag, Upload, message } from 'antd';
import { PlusOutlined, UploadOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api, ApiError, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton, ConfirmButton } from '../../components/ActionButton';
import { col, cols } from '../../components/display';
import { useApi } from '../../components/hooks';
import type { FieldSpec } from '../../components/FormFields';

const SOURCES = ['WALLET', 'CORE', 'CARDS'];

interface Category {
  category: string;
  label: string;
  appliesTo: string;
  codes: { code: string; label?: string; labelAr?: string; attribute?: string | null }[];
}

/**
 * Lookup management: the wallet/core send their own codes; mappings translate them to AML codes, codes without a mapping wait
 * in the unmapped queue, and every AML code carries an approved risk score used by LookupScore risk factors.
 */
export default function LookupManagementPage() {
  const { t, i18n } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const cats = useApi<Category[]>('/api/admin/lookup-management/categories');
  const catOptions = (cats.data ?? []).map((c) => ({ value: c.category, label: `${c.category} — ${c.appliesTo}` }));
  const [filters, setFilters] = useState<Query>({});
  const [scoreCat, setScoreCat] = useState<string>('OCCUPATION');
  const scores = useApi<Row[]>('/api/admin/lookup-management/scores', { category: scoreCat, n });
  const canMap = can('admin.lookups');

  const remapMessage = (r: Row) => {
    const rm = r?.remap;
    if (rm) message.success(t('lookups.remapped', { applied: rm.valuesApplied, reassessed: rm.customersReassessed }));
    return r;
  };

  const mappingFields = (edit: boolean): FieldSpec[] => [
    ...(edit
      ? []
      : [
          { name: 'sourceSystem', label: t('f.sourceSystem'), type: 'select' as const, required: true, options: SOURCES.map((x) => ({ value: x, label: x })) },
          { name: 'category', label: t('f.category'), type: 'select' as const, required: true, options: catOptions },
          { name: 'externalCode', label: t('f.externalCode'), required: true },
        ]),
    { name: 'externalLabel', label: t('f.externalLabel') },
    { name: 'code', label: t('f.amlCode'), required: true },
    ...(edit ? [{ name: 'isActive', label: t('f.isActive'), type: 'switch' as const }] : []),
    { name: 'notes', label: t('f.notes'), type: 'textarea' as const },
  ];

  const codeRows = useMemo(() => {
    const c = (cats.data ?? []).find((x) => x.category === scoreCat);
    const list = scores.data ?? [];
    return (c?.codes ?? []).map((x) => {
      const approved = list.find((s) => s.code === x.code && s.status === 'Approved');
      const pending = list.find((s) => s.code === x.code && s.status === 'PendingApproval');
      return { id: x.code, code: x.code, label: i18n.language === 'ar' ? (x.labelAr ?? x.label) : x.label, attribute: x.attribute, score: approved?.score, approvedBy: approved?.approvedBy, effectiveFrom: approved?.effectiveFrom, pending };
    });
  }, [cats.data, scores.data, scoreCat, i18n.language]);

  return (
    <Page name="lookup-management" title={t('nav.lookupManagement')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('lookups.notice')} />
      <Tabs
        items={[
          {
            key: 'scores',
            label: t('lookups.scores'),
            children: (
              <>
                <Space wrap style={{ marginBottom: 12 }}>
                  <label htmlFor="score-category">{t('f.category')}</label>
                  <Select id="score-category" data-testid="score-category" style={{ minWidth: 320 }} showSearch value={scoreCat} options={catOptions} onChange={setScoreCat} />
                  {can('risk.config') && (
                    <ActionButton
                      testId="score-propose"
                      type="primary"
                      icon={<PlusOutlined />}
                      label={t('lookups.proposeScore')}
                      fields={[
                        { name: 'code', label: t('f.amlCode'), required: true },
                        { name: 'score', label: t('f.score'), type: 'number', required: true },
                        { name: 'rationale', label: t('f.rationale'), type: 'textarea', required: true },
                        { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
                      ]}
                      onSubmit={(v) => api.post('/api/admin/lookup-management/scores', { ...v, category: scoreCat })}
                      onDone={bump}
                    />
                  )}
                </Space>
                <DataTable
                  testId="lookup-scores-table"
                  data={codeRows as Row[]}
                  columns={[
                    ...cols('code', 'label', 'attribute'),
                    col('score', { render: (v) => (v === undefined || v === null ? <Tag>{t('lookups.noScore')}</Tag> : <b>{v}</b>) }),
                    ...cols('approvedBy', 'effectiveFrom'),
                    {
                      key: 'pending',
                      title: t('lookups.pending'),
                      render: (_: unknown, r: Row) =>
                        r.pending ? (
                          <Space wrap size="small">
                            <Tag color="orange">
                              {r.pending.score} — {r.pending.createdBy}
                            </Tag>
                            {can('risk.approve') && (
                              <>
                                <ActionButton size="small" testId={`score-approve-${r.code}`} label={t('actions.approve')} fields={[{ name: 'comment', label: t('f.comment'), type: 'textarea' }]} onSubmit={(v) => api.post(`/api/admin/lookup-management/scores/${r.pending.id}/approve`, v).then((x) => { const y = x as Row; message.success(t('lookups.reassessed', { n: y.reassessed })); return x; })} onDone={bump} />
                                <ConfirmButton size="small" danger testId={`score-reject-${r.code}`} label={t('actions.reject')} fields={[{ name: 'comment', label: t('f.reason'), type: 'textarea', required: true }]} onSubmit={(v) => api.post(`/api/admin/lookup-management/scores/${r.pending.id}/reject`, v)} onDone={bump} />
                              </>
                            )}
                          </Space>
                        ) : (
                          can('risk.config') && (
                            <ActionButton size="small" testId={`score-set-${r.code}`} label={t('lookups.setScore')} fields={[{ name: 'score', label: t('f.score'), type: 'number', required: true }, { name: 'rationale', label: t('f.rationale'), type: 'textarea', required: true }]} onSubmit={(v) => api.post('/api/admin/lookup-management/scores', { ...v, category: scoreCat, code: r.code })} onDone={bump} />
                          )
                        ),
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'mappings',
            label: t('lookups.mappings'),
            children: (
              <>
                <Space wrap style={{ marginBottom: 12 }}>
                  {canMap && (
                    <ActionButton testId="mapping-create" type="primary" icon={<PlusOutlined />} label={t('actions.create')} fields={mappingFields(false)} initialValues={{ sourceSystem: 'WALLET' }}
                      onSubmit={(v) => api.post<Row>('/api/admin/lookup-management/mappings', v).then(remapMessage)} onDone={bump} />
                  )}
                  {canMap && (
                    <Upload
                      accept=".csv,text/csv"
                      showUploadList={false}
                      customRequest={async ({ file, onSuccess, onError }) => {
                        try {
                          const r = await api.upload<Row>('/api/admin/lookup-management/mappings/import', file as File);
                          message.success(t('lookups.imported', { created: r.created, updated: r.updated, rejected: r.rejected }));
                          if (r.errors?.length) message.warning((r.errors as string[]).slice(0, 3).join(' | '));
                          bump();
                          onSuccess?.(r);
                        } catch (e) {
                          message.error((e as ApiError).detail ?? String(e));
                          onError?.(e as Error);
                        }
                      }}
                    >
                      <Button icon={<UploadOutlined />} data-testid="mapping-import">
                        {t('lookups.importCsv')}
                      </Button>
                    </Upload>
                  )}
                </Space>
                <FilterBar
                  onChange={setFilters}
                  fields={[
                    { name: 'sourceSystem', label: t('f.sourceSystem'), type: 'select', options: SOURCES.map((x) => ({ value: x, label: x })) },
                    { name: 'category', label: t('f.category'), type: 'select', options: catOptions },
                    { name: 'search', label: t('f.search') },
                  ]}
                />
                <DataTable
                  testId="lookup-mappings-table"
                  url="/api/admin/lookup-management/mappings"
                  params={filters}
                  reloadKey={n}
                  columns={[
                    ...cols('sourceSystem', 'category', 'externalCode', 'externalLabel'),
                    col('code', { title: t('f.amlCode') }),
                    ...cols('isActive', 'updatedBy', 'updatedAt'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Row) =>
                        canMap && (
                          <ActionButton size="small" testId={`mapping-edit-${r.id}`} label={t('actions.edit')} fields={mappingFields(true)} initialValues={r}
                            onSubmit={(v) => api.put<Row>(`/api/admin/lookup-management/mappings/${r.id}`, { ...v, rowVersion: r.rowVersion }).then(remapMessage)} onDone={bump} />
                        ),
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'unmapped',
            label: t('lookups.unmapped'),
            children: (
              <DataTable
                testId="lookup-unmapped-table"
                url="/api/admin/lookup-management/unmapped"
                reloadKey={n}
                rowKey={(r: Row) => `${r.sourceSystem}|${r.category}|${r.externalCode}`}
                columns={[
                  ...cols('sourceSystem', 'category', 'externalCode', 'customers', 'firstSeen', 'lastSeen'),
                  {
                    key: 'actions',
                    title: t('f.actions'),
                    render: (_: unknown, r: Row) =>
                      canMap && (
                        <ActionButton size="small" type="primary" testId={`unmapped-map-${r.externalCode}`} label={t('lookups.mapNow')}
                          fields={[{ name: 'code', label: t('f.amlCode'), required: true }, { name: 'externalLabel', label: t('f.externalLabel') }]}
                          onSubmit={(v) => api.post<Row>('/api/admin/lookup-management/mappings', { ...v, sourceSystem: r.sourceSystem, category: r.category, externalCode: r.externalCode }).then(remapMessage)}
                          onDone={bump} />
                      ),
                  },
                ]}
              />
            ),
          },
        ]}
      />
    </Page>
  );
}
