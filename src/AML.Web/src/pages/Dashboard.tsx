import { Card, Col, Row as AntRow, Statistic, Table } from 'antd';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Page } from '../components/Page';
import { Async } from '../components/StateViews';
import { useApi } from '../components/hooks';
import { StatusTag, fl } from '../components/display';
import { fmtNumber } from '../utils/format';
import type { Row } from '../api/types';

const Breakdown = ({ rows, k, testId }: { rows: Row[]; k: string; testId: string }) => (
  <Table
    data-testid={testId}
    size="small"
    pagination={false}
    rowKey={k}
    dataSource={rows}
    columns={[
      { key: k, dataIndex: k, title: fl(k), render: (v: string) => <StatusTag value={v} /> },
      { key: 'count', dataIndex: 'count', title: fl('count'), render: (v: number) => fmtNumber(v) },
    ]}
  />
);

export default function Dashboard() {
  const { t } = useTranslation();
  const st = useApi<Row>('/api/dashboard');
  return (
    <Page name="dashboard" title={t('nav.dashboard')}>
      <Async state={st}>
        {(d) => (
          <>
            <div className="kpi-grid">
              <Card>
                <Statistic title={t('dash.customers')} value={fmtNumber(d.customers?.total)} />
                <Link to="/customers">{t('actions.view')}</Link>
              </Card>
              <Card>
                <Statistic title={t('dash.openAlerts')} value={fmtNumber(d.alerts?.open)} />
                <Link to="/alerts">{t('actions.view')}</Link>
              </Card>
              <Card>
                <Statistic title={t('dash.overdueAlerts')} value={fmtNumber(d.alerts?.overdue)} valueStyle={{ color: d.alerts?.overdue ? '#c41d1d' : undefined }} />
              </Card>
              <Card>
                <Statistic title={t('dash.openCases')} value={fmtNumber(d.cases?.open)} />
                <Link to="/cases">{t('actions.view')}</Link>
              </Card>
              <Card>
                <Statistic title={t('dash.overdueReviews')} value={fmtNumber(d.reviews?.overdue)} />
              </Card>
              <Card>
                <Statistic title={t('dash.txns24h')} value={fmtNumber(d.transactions?.last24h)} />
              </Card>
              <Card>
                <Statistic title={t('dash.dataQualityOpen')} value={fmtNumber(d.transactions?.dataQualityOpen)} />
              </Card>
              <Card>
                <Statistic title={t('dash.heldTxns')} value={fmtNumber(d.holds?.held)} />
              </Card>
            </div>
            <AntRow gutter={[16, 16]}>
              <Col xs={24} md={12} xl={6}>
                <Card size="small" title={t('dash.riskDistribution')}>
                  <Breakdown rows={d.riskDistribution ?? []} k="level" testId="dash-risk" />
                </Card>
              </Col>
              <Col xs={24} md={12} xl={6}>
                <Card size="small" title={t('dash.alertsBySeverity')}>
                  <Breakdown rows={d.alerts?.bySeverity ?? []} k="severity" testId="dash-severity" />
                </Card>
              </Col>
              <Col xs={24} md={12} xl={6}>
                <Card size="small" title={t('dash.screening')}>
                  <Breakdown rows={d.screening ?? []} k="status" testId="dash-screening" />
                </Card>
              </Col>
              <Col xs={24} md={12} xl={6}>
                <Card size="small" title={t('dash.customersByType')}>
                  <Breakdown rows={d.customers?.byType ?? []} k="type" testId="dash-types" />
                </Card>
              </Col>
            </AntRow>
          </>
        )}
      </Async>
    </Page>
  );
}
