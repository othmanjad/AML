import { Alert, Space, Table, Tag, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import type { Assessment, AssessmentFactor } from '../api/types';
import { KV, StatusTag, fl, tv } from './display';
import { fmtNumber } from '../utils/format';

/** Factor breakdown: factor, category, observed value, data status, score, weight, contribution, explanation. */
export function FactorTable({ factors, testId = 'factor-table' }: { factors: AssessmentFactor[]; testId?: string }) {
  return (
    <Table<AssessmentFactor>
      data-testid={testId}
      size="small"
      rowKey="factorCode"
      dataSource={factors}
      pagination={false}
      scroll={{ x: 'max-content' }}
      rowClassName={(r) => (r.dataStatus === 'Missing' || r.dataStatus === 'Stale' ? 'row-overdue' : '')}
      columns={[
        { key: 'factor', title: fl('factor'), render: (_, r) => <span>{r.factorName} <Typography.Text type="secondary">({r.factorCode} v{r.factorVersion})</Typography.Text></span> },
        { key: 'category', title: fl('category'), dataIndex: 'categoryCode' },
        { key: 'observedValue', title: fl('observedValue'), dataIndex: 'observedValue', render: (v) => v ?? '—' },
        { key: 'dataStatus', title: fl('dataStatus'), dataIndex: 'dataStatus', render: (v) => <StatusTag value={v} /> },
        { key: 'score', title: fl('score'), dataIndex: 'normalizedScore', render: (v) => fmtNumber(v) },
        { key: 'weight', title: fl('weight'), dataIndex: 'weight', render: (v) => fmtNumber(v) },
        { key: 'contribution', title: fl('contribution'), dataIndex: 'contribution', render: (v, r) => (r.included ? fmtNumber(v) : <Tag>{fl('excluded')}</Tag>) },
        { key: 'explanation', title: fl('explanation'), dataIndex: 'explanation', width: 380, render: (v) => <span style={{ whiteSpace: 'normal' }}>{v}</span> },
      ]}
    />
  );
}

export function MissingDataWarnings({ factors }: { factors: AssessmentFactor[] }) {
  const { t } = useTranslation();
  const missing = factors.filter((f) => ['Missing', 'Stale'].includes(f.dataStatus));
  if (!missing.length) return null;
  return (
    <Alert
      data-testid="missing-data-warnings"
      type="warning"
      showIcon
      style={{ marginBottom: 12 }}
      message={t('risk.missingData')}
      description={
        <ul style={{ margin: 0, paddingInlineStart: 18 }}>
          {missing.map((f) => (
            <li key={f.factorCode}>
              {f.factorName}: {tv(f.dataStatus)} {f.missingHandling ? `— ${f.missingHandling}` : ''}
            </li>
          ))}
        </ul>
      }
    />
  );
}

/** Full assessment explanation used by profile overview, risk history and assessments list. */
export function AssessmentView({ a }: { a: Assessment }) {
  const { t } = useTranslation();
  return (
    <div data-testid="assessment-view">
      <KV
        data={a}
        keys={['methodologyCode', 'methodologyVersion', 'aggregationMethod', 'trigger', 'calculatedScore', 'calculatedLevel', 'finalLevel', 'status', 'calculatedAt', 'createdBy']}
      />
      {(a.appliedOverrides.length > 0 || a.appliedFloors.length > 0) && (
        <Alert
          style={{ marginTop: 12 }}
          type="info"
          showIcon
          message={t('risk.overridesApplied')}
          description={
            <Space direction="vertical">
              {[...a.appliedOverrides, ...a.appliedFloors].map((x) => (
                <span key={x}>{x}</span>
              ))}
            </Space>
          }
        />
      )}
      <Typography.Paragraph style={{ marginTop: 12 }} data-testid="assessment-explanation">
        {a.explanation}
      </Typography.Paragraph>
      <MissingDataWarnings factors={a.factors} />
      <FactorTable factors={a.factors} />
    </div>
  );
}
