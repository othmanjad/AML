import { Alert, Badge, Card, Col, Row as AntRow, Space, Tabs, Tag, Typography } from 'antd';
import { useParams, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import type { Assessment, CustomerDetail } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Page, Section } from '../../components/Page';
import { Async } from '../../components/StateViews';
import { useApi, useLookupOptions } from '../../components/hooks';
import { KV, StatusTag, customerName, tv } from '../../components/display';
import { ActionButton } from '../../components/ActionButton';
import { AssessmentView } from '../../components/RiskViews';
import { fmtDate, fmtDateTime, fmtNumber } from '../../utils/format';
import {
  AlertsTab,
  AttributesTab,
  CasesTab,
  CoreTab,
  DocumentsTab,
  FinancialTab,
  HistoryTab,
  KycTab,
  RelationshipsTab,
  RiskTab,
  ScreeningTab,
  TransactionsTab,
  WalletsTab,
} from './ProfileTabs';

const RISK_LEVELS = ['Low', 'Medium', 'High', 'Critical'];

function WhyRisk({ c }: { c: CustomerDetail }) {
  const { t } = useTranslation();
  const st = useApi<Assessment[]>(c.risk ? `/api/customers/${c.id}/risk-assessments` : null);
  const r = c.risk;
  if (!r) return <Alert type="info" showIcon message={t('risk.notAssessed')} data-testid="risk-not-assessed" />;
  return (
    <div data-testid="why-risk">
      <KV
        data={{ ...r, methodology: `${r.methodologyCode ?? '—'} v${r.methodologyVersion ?? '—'}` }}
        keys={['methodology', 'lastAssessedAt', 'nextReviewDate', 'calculatedScore', 'calculatedLevel', 'level', 'monitoringLevel', 'reviewOverdue']}
      />
      {r.overrideApplied && (
        <Alert
          style={{ marginTop: 12 }}
          type="warning"
          showIcon
          data-testid="override-indicator"
          message={`${t('risk.overrideApplied')}: ${tv(r.overrideType)}`}
          description={r.overrideReason ?? undefined}
        />
      )}
      {r.sanctionsWorkflow && <Alert style={{ marginTop: 12 }} type="error" showIcon message={t('risk.sanctionsWorkflow')} />}
      <div style={{ marginTop: 12 }}>
        <Async state={st}>
          {(list) => {
            const a = list.find((x) => x.id === r.lastAssessmentId) ?? list[0];
            return a ? <AssessmentView a={a} /> : null;
          }}
        </Async>
      </div>
    </div>
  );
}

function Overview({ c }: { c: CustomerDetail }) {
  const { t } = useTranslation();
  return (
    <>
      <AntRow gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={24} md={8}>
          <Card size="small" title={t('profile.riskLevel')}>
            <Space direction="vertical">
              <StatusTag value={c.risk?.level} testId="risk-level" />
              <span>
                {t('f.score')}: {fmtNumber(c.risk?.score)}
              </span>
              {c.risk?.reviewOverdue && <Tag color="red">{t('f.reviewOverdue')}</Tag>}
            </Space>
          </Card>
        </Col>
        <Col xs={24} md={8}>
          <Card size="small" title={t('profile.screeningStatus')}>
            <StatusTag value={c.screeningStatus} testId="screening-status" />
          </Card>
        </Col>
        <Col xs={24} md={8}>
          <Card size="small" title={t('f.kycStatus')}>
            <Space wrap>
              <StatusTag value={c.kycStatus} testId="kyc-status" />
              {c.kycGaps.length > 0 && <Tag color="orange">{t('profile.kycGaps', { count: c.kycGaps.length })}</Tag>}
            </Space>
          </Card>
        </Col>
      </AntRow>
      <Section title={t('profile.summary')}>
        <KV data={c} keys={['customerType', 'status', 'externalId', 'sourceSystem', 'dueDiligenceLevel', 'acceptanceDecision', 'assignedOfficer', 'region', 'householdId', 'isPep', 'isFrozen', 'createdAt', 'updatedAt']} />
      </Section>
      <Section title={t('risk.whyTitle')} testId="why-risk-panel">
        <WhyRisk c={c} />
      </Section>
    </>
  );
}

export default function CustomerProfile() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { can } = useAuth();
  const [sp, setSp] = useSearchParams();
  const st = useApi<CustomerDetail>(`/api/customers/${id}`);
  const statuses = useLookupOptions('CUSTOMER_STATUS');
  const tab = sp.get('tab') ?? 'overview';

  return (
    <Async state={st}>
      {(c) => {
        const badge = (n: number) => (n > 0 ? <Badge count={n} size="small" style={{ marginInlineStart: 6 }} data-testid="tab-count" /> : null);
        const tabs = [
          { key: 'overview', el: <Overview c={c} /> },
          { key: 'core', el: <CoreTab c={c} reload={st.reload} /> },
          { key: 'attributes', el: <AttributesTab c={c} /> },
          { key: 'kyc', el: <KycTab c={c} reload={st.reload} /> },
          { key: 'financial', el: <FinancialTab c={c} /> },
          { key: 'wallets', el: <WalletsTab c={c} /> },
          { key: 'transactions', el: <TransactionsTab c={c} /> },
          { key: 'risk', el: <RiskTab c={c} reload={st.reload} /> },
          { key: 'screening', el: <ScreeningTab c={c} reload={st.reload} /> },
          { key: 'alerts', el: <AlertsTab c={c} />, badge: badge(c.alertCount) },
          { key: 'cases', el: <CasesTab c={c} />, badge: badge(c.caseCount) },
          { key: 'relationships', el: <RelationshipsTab c={c} /> },
          { key: 'documents', el: <DocumentsTab c={c} /> },
          { key: 'history', el: <HistoryTab c={c} /> },
        ];
        return (
          <Page
            name="customer-profile"
            title={
              <span>
                {customerName(c)} <Typography.Text type="secondary" style={{ fontSize: 14 }}>#{c.id} · {c.externalId}</Typography.Text>
              </span>
            }
            subTitle={
              <Space wrap size={4} style={{ marginTop: 4 }}>
                <StatusTag value={c.customerType} />
                <StatusTag value={c.status} />
                {c.flags.map((f) => (
                  <Tag key={f} color="purple">
                    {tv(f)}
                  </Tag>
                ))}
                {c.isFrozen && <Tag color="red">{t('f.isFrozen')}</Tag>}
              </Space>
            }
            extra={
              <>
                {can('risk.assess') && (
                  <ActionButton
                    testId="run-assessment"
                    label={t('profile.runAssessment')}
                    fields={[{ name: 'reason', label: t('f.reason'), type: 'textarea' }]}
                    onSubmit={(v) => api.post(`/api/customers/${c.id}/risk-assessments`, { trigger: 'Manual', reason: v.reason })}
                    onDone={st.reload}
                  />
                )}
                {can('screening.run') && (
                  <ActionButton testId="screen-now" label={t('profile.screenNow')} onSubmit={() => api.post(`/api/customers/${c.id}/screening`)} onDone={st.reload} />
                )}
                {can('risk.override.request') && (
                  <ActionButton
                    testId="request-override"
                    label={t('profile.requestOverride')}
                    fields={[
                      { name: 'requestedLevel', label: t('f.requestedLevel'), type: 'select', required: true, options: RISK_LEVELS.map((x) => ({ value: x, label: tv(x) })) },
                      { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
                      { name: 'evidence', label: t('f.evidence'), type: 'textarea' },
                    ]}
                    onSubmit={(v) => api.post('/api/risk/overrides', { customerId: c.id, ...v })}
                  />
                )}
                {can('customers.write') && (
                  <ActionButton
                    testId="change-status"
                    label={t('profile.changeStatus')}
                    confirm
                    fields={[
                      { name: 'status', label: t('f.status'), type: 'select', required: true, options: statuses },
                      { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
                    ]}
                    onSubmit={(v) => api.post(`/api/customers/${c.id}/status`, { ...v, rowVersion: c.rowVersion })}
                    onDone={st.reload}
                  />
                )}
                {can('customers.exit') && (
                  <ActionButton
                    testId="request-exit"
                    danger
                    label={t('profile.requestExit')}
                    confirm
                    fields={[
                      { name: 'reasonCode', label: t('f.reasonCode'), required: true },
                      { name: 'internalReason', label: t('f.internalReason'), type: 'textarea' },
                      { name: 'linkedCaseId', label: t('f.caseId'), type: 'number' },
                      { name: 'evidence', label: t('f.evidence'), type: 'textarea' },
                    ]}
                    onSubmit={(v) => api.post(`/api/customers/${c.id}/exit`, v)}
                    onDone={st.reload}
                  />
                )}
              </>
            }
          >
            <Card size="small" style={{ marginBottom: 12 }}>
              <Space wrap size="large">
                <span>
                  {t('profile.riskLevel')}: <StatusTag value={c.risk?.level} />
                </span>
                <span>
                  {t('profile.screeningStatus')}: <StatusTag value={c.screeningStatus} />
                </span>
                <span>
                  {t('f.nextReviewDate')}: {fmtDate(c.risk?.nextReviewDate)}
                </span>
                <span>
                  {t('f.lastAssessedAt')}: {fmtDateTime(c.risk?.lastAssessedAt)}
                </span>
              </Space>
            </Card>
            <Tabs
              className="wrap-tabs"
              activeKey={tab}
              onChange={(k) => setSp({ tab: k }, { replace: true })}
              destroyOnHidden
              items={tabs.map((x) => ({
                key: x.key,
                label: (
                  <span data-testid={`tab-${x.key}`}>
                    {t(`tabs.${x.key}`)}
                    {x.badge}
                  </span>
                ),
                children: <div data-testid={`tabpanel-${x.key}`}>{x.el}</div>,
              }))}
            />
          </Page>
        );
      }}
    </Async>
  );
}
