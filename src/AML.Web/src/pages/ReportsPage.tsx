import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Col, DatePicker, Form, Input, List, Row as AntRow, Select, Space, Switch, Typography, message } from 'antd';
import { DownloadOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import i18n from '../i18n';
import { api, ApiError } from '../api/client';
import type { Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DataTable } from '../components/DataTable';
import { Page, Section } from '../components/Page';
import { Async, ErrorAlert } from '../components/StateViews';
import { useApi } from '../components/hooks';
import { StatusTag, cols } from '../components/display';
import { dateProps } from '../components/FormFields';
import { fmtAuto } from '../utils/format';

const reportTitle = (code: string, fallback: string) => (i18n.exists(`reportCat.${code}`) ? i18n.t(`reportCat.${code}`) : fallback);
const colTitle = (c: string) => {
  const k = `f.${c.charAt(0).toLowerCase()}${c.slice(1)}`;
  return i18n.exists(k) ? i18n.t(k) : c;
};

function ExportPanel({ code, filters }: { code: string; filters: Row }) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [format, setFormat] = useState('CSV');
  const [sensitive, setSensitive] = useState(false);
  const [restricted, setRestricted] = useState(false);
  const [job, setJob] = useState<Row | null>(null);
  const [err, setErr] = useState<ApiError | null>(null);
  const timer = useRef<number>();
  useEffect(() => () => window.clearTimeout(timer.current), []);

  const poll = (jobId: string) => {
    api
      .get<Row>(`/api/exports/${jobId}`)
      .then((j) => {
        setJob(j);
        if (j.status === 'Completed') {
          void api.download(`/api/exports/${jobId}/download`, j.fileName ?? `${code}.${format.toLowerCase()}`).catch((e: ApiError) => setErr(e));
        } else if (j.status !== 'Failed') timer.current = window.setTimeout(() => poll(jobId), 1500);
      })
      .catch((e: ApiError) => setErr(e));
  };

  const start = async () => {
    setErr(null);
    setJob(null);
    try {
      const r = await api.post<Row>(`/api/reports/${code}/export`, { format, from: filters.from, to: filters.to, groupBy: filters.groupBy, includeSensitive: sensitive, includeRestricted: restricted });
      setJob(r);
      poll(r.jobId);
    } catch (e) {
      setErr(e as ApiError);
    }
  };

  if (!can('reports.export')) return null;
  return (
    <Card size="small" title={t('reportsPage.export')} data-testid="export-panel" style={{ marginBottom: 12 }}>
      <Space wrap align="end">
        <div>
          <label className="sr-label" htmlFor="export-format">{t('f.format')}</label>
          <Select id="export-format" data-testid="export-format" value={format} onChange={setFormat} style={{ width: 120 }} options={['CSV', 'XLSX'].map((x) => ({ value: x, label: x }))} />
        </div>
        {can('reports.export.sensitive') && (
          <div>
            <label className="sr-label" htmlFor="export-sensitive">{t('reportsPage.includeSensitive')}</label>
            <Switch id="export-sensitive" checked={sensitive} onChange={setSensitive} />
          </div>
        )}
        {can('reports.export.restricted') && (
          <div>
            <label className="sr-label" htmlFor="export-restricted">{t('reportsPage.includeRestricted')}</label>
            <Switch id="export-restricted" checked={restricted} onChange={setRestricted} />
          </div>
        )}
        <Button type="primary" icon={<DownloadOutlined />} onClick={start} data-testid="export-start" loading={!!job && !['Completed', 'Failed'].includes(job.status)}>
          {t('reportsPage.exportButton')}
        </Button>
      </Space>
      <div style={{ marginTop: 8 }}>
        <ErrorAlert error={err} />
        {job && (
          <div data-testid="export-status">
            {t('f.status')}: <StatusTag value={job.status} /> {job.rowCount !== undefined && job.rowCount !== null && <span>· {t('f.rowCount')}: {job.rowCount}</span>}
            {job.error && <Alert type="error" message={job.error} style={{ marginTop: 8 }} />}
            {job.status === 'Completed' && (
              <Button type="link" data-testid="export-download" onClick={() => api.download(`/api/exports/${job.jobId}/download`, job.fileName ?? 'export').catch((e: ApiError) => message.error(e.detail ?? e.message))}>
                {t('actions.download')} {job.fileName}
              </Button>
            )}
          </div>
        )}
      </div>
    </Card>
  );
}

function ReportRunner({ code, title }: { code: string; title: string }) {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Row>({});
  const [run, setRun] = useState(0);
  const st = useApi<Row>(run ? `/api/reports/${code}` : null, { ...filters, run });
  return (
    <div data-testid="report-runner">
      <Typography.Title level={4}>{title}</Typography.Title>
      <Card size="small" style={{ marginBottom: 12 }}>
        <Form
          layout="vertical"
          onFinish={(v) => {
            setFilters(Object.fromEntries(Object.entries(v).filter(([, x]) => x)));
            setRun((x) => x + 1);
          }}
        >
          <div className="filter-grid">
            <Form.Item name="from" label={t('f.dateFrom')} {...dateProps('datetime')}>
              <DatePicker showTime style={{ width: '100%' }} data-testid="report-from" />
            </Form.Item>
            <Form.Item name="to" label={t('f.dateTo')} {...dateProps('datetime')}>
              <DatePicker showTime style={{ width: '100%' }} data-testid="report-to" />
            </Form.Item>
            <Form.Item name="groupBy" label={t('f.groupBy')}>
              <Input data-testid="report-groupby" />
            </Form.Item>
          </div>
          <Button type="primary" htmlType="submit" data-testid="report-run">
            {t('reportsPage.run')}
          </Button>
        </Form>
      </Card>
      <ExportPanel code={code} filters={filters} />
      {run > 0 && (
        <Async state={st}>
          {(r) => (
            <Section title={`${reportTitle(r.code, r.title)} · ${t('table.total', { count: r.total })}`} testId="report-table-section">
              {r.notes && <Alert type="info" message={r.notes} style={{ marginBottom: 8 }} />}
              <DataTable
                testId="report-table"
                rowKey={(x) => JSON.stringify(x)}
                data={r.rows}
                columns={(r.columns as string[]).map((c) => ({ key: c, dataIndex: c, title: colTitle(c), sorter: true, render: (v: unknown) => fmtAuto(c, v) }))}
              />
            </Section>
          )}
        </Async>
      )}
    </div>
  );
}

export default function ReportsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const cat = useApi<Row[]>('/api/reports/catalogue');
  const [sel, setSel] = useState<Row | null>(null);
  return (
    <Page name="reports" title={t('nav.reports')}>
      <AntRow gutter={16}>
        <Col xs={24} lg={7}>
          <Card size="small" title={t('reportsPage.catalogue')} style={{ marginBottom: 12 }}>
            <Async state={cat}>
              {(list) => (
                <List
                  data-testid="report-catalogue"
                  size="small"
                  dataSource={list}
                  renderItem={(r) => (
                    <List.Item>
                      <Button type={sel?.code === r.code ? 'primary' : 'link'} onClick={() => setSel(r)} data-testid={`report-${r.code}`} style={{ whiteSpace: 'normal', textAlign: 'start', height: 'auto' }}>
                        {reportTitle(r.code, r.title)}
                      </Button>
                    </List.Item>
                  )}
                />
              )}
            </Async>
          </Card>
        </Col>
        <Col xs={24} lg={17}>{sel ? <ReportRunner key={sel.code} code={sel.code} title={reportTitle(sel.code, sel.title)} /> : <Alert type="info" showIcon message={t('reportsPage.choose')} />}</Col>
      </AntRow>
      {can('reports.export') && (
        <Section title={t('reportsPage.myExports')}>
          <DataTable testId="my-exports" url="/api/exports" rowKey="jobId" reloadKey={sel?.code} columns={cols('reportCode', 'format', 'status', 'rowCount', 'fileName', 'sha256', 'createdAt', 'completedAt', 'error')} />
        </Section>
      )}
    </Page>
  );
}
