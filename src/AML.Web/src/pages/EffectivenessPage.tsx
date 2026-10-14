import { useState } from 'react';
import { Tabs, Tag } from 'antd';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import type { Row } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { DataTable } from '../components/DataTable';
import { Page } from '../components/Page';
import { ActionButton } from '../components/ActionButton';
import { col, cols, tv } from '../components/display';
import { fmtNumber } from '../utils/format';

const pct = (v: number | null) => (v === null || v === undefined ? '—' : `${fmtNumber(v * (v <= 1 ? 100 : 1), 1)}%`);

export default function EffectivenessPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => setN((x) => x + 1);
  const tabs = [];
  if (can('effectiveness.read')) {
    tabs.push({
      key: 'metrics',
      label: <span data-testid="tab-metrics">{t('eff.ruleMetrics')}</span>,
      children: (
        <DataTable
          testId="rule-metrics"
          url="/api/governance/rule-metrics"
          rowKey="ruleId"
          columns={[
            ...cols('ruleCode', 'ruleName', 'isActive', 'alertsGenerated', 'alertsConvertedOrLinkedToCase'),
            col('alertToCaseRate', { render: pct }),
            col('casesWithReport'),
            col('caseToReportRate', { render: pct }),
            ...cols('closedAlerts', 'falsePositives'),
            col('falsePositiveRate', { render: pct }),
            ...cols('avgHandlingHours', 'avgOpenAlertAgeDays'),
          ]}
        />
      ),
    });
    tabs.push({
      key: 'coverage',
      label: <span data-testid="tab-coverage">{t('eff.coverage')}</span>,
      children: (
        <DataTable
          testId="typology-coverage"
          url="/api/governance/coverage"
          rowKey="code"
          transform={(x: Row) => x.typologies ?? []}
          rowClassName={(r: Row) => (r.covered ? '' : 'row-uncovered')}
          columns={[
            ...cols('code', 'name', 'riskRating', 'rules', 'activeRules', 'otherControls'),
            col('covered', { render: (v: boolean) => (v ? <Tag color="green">{t('eff.covered')}</Tag> : <Tag color="red" data-testid="uncovered">{t('eff.uncovered')}</Tag>) }),
          ]}
        />
      ),
    });
  }
  if (can('qa.review')) {
    tabs.push({
      key: 'qa',
      label: <span data-testid="tab-qa">{t('eff.qaSamples')}</span>,
      children: (
        <>
          <div style={{ marginBottom: 12 }}>
            <ActionButton
              testId="qa-run-sampling"
              type="primary"
              label={t('eff.runSampling')}
              fields={[
                { name: 'from', label: t('f.dateFrom'), type: 'datetime', required: true },
                { name: 'to', label: t('f.dateTo'), type: 'datetime', required: true },
              ]}
              onSubmit={(v) => api.post('/api/governance/qa/sample', v)}
              onDone={bump}
            />
          </div>
          <DataTable
            testId="qa-samples"
            url="/api/governance/qa/samples"
            reloadKey={n}
            columns={[
              ...cols('id', 'alertId', 'configCode', 'stratum', 'analyst', 'sampledAt', 'status', 'reviewer', 'errorCategory', 'findings', 'reopened', 'reviewedAt'),
              {
                key: 'actions',
                title: t('f.actions'),
                render: (_: unknown, r: Row) =>
                  r.status === 'Pending' ? (
                    <ActionButton
                      size="small"
                      testId={`qa-review-${r.id}`}
                      label={t('eff.review')}
                      fields={[
                        { name: 'passed', label: t('f.passed'), type: 'switch' },
                        { name: 'errorCategory', label: t('f.errorCategory'), type: 'select', options: ['Documentation', 'Disposition', 'Escalation', 'Timeliness', 'Other'].map((x) => ({ value: x, label: tv(x) })) },
                        { name: 'findings', label: t('f.findings'), type: 'textarea' },
                        { name: 'reopen', label: t('eff.reopenAlert'), type: 'switch' },
                      ]}
                      initialValues={{ passed: true }}
                      onSubmit={(v) => api.post(`/api/governance/qa/samples/${r.id}/review`, { passed: !!v.passed, errorCategory: v.errorCategory, findings: v.findings, reopen: !!v.reopen })}
                      onDone={bump}
                    />
                  ) : null,
              },
            ]}
          />
        </>
      ),
    });
    tabs.push({
      key: 'analyst',
      label: <span data-testid="tab-analyst">{t('eff.analystQuality')}</span>,
      children: <DataTable testId="analyst-quality" url="/api/governance/qa/analyst-quality" rowKey="analyst" reloadKey={n} columns={cols('analyst', 'sampled', 'reviewed', 'passed', 'errors', 'passRate')} />,
    });
  }
  return (
    <Page name="effectiveness" title={t('nav.effectiveness')}>
      <Tabs destroyOnHidden items={tabs} />
    </Page>
  );
}
