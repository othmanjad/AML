import { useState } from 'react';
import { Button, Modal, Tabs } from 'antd';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Assessment, Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { AssessmentView } from '../../components/RiskViews';
import { col, cols, tv } from '../../components/display';
import { useLookupOptions } from '../../components/hooks';

const custLink = (v: number) => <Link to={`/customers/${v}`}>#{v}</Link>;

export default function RiskAssessments() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<Query>({});
  const [ovFilters, setOvFilters] = useState<Query>({});
  const [taskFilters, setTaskFilters] = useState<Query>({});
  const [sel, setSel] = useState<Assessment | null>(null);
  const [n, setN] = useState(0);
  const levels = useLookupOptions('RISK_LEVEL');
  const bump = () => setN((x) => x + 1);
  const decision = (path: string, approveLabel: string) => (r: Row) => (
    <ActionButton
      size="small"
      label={approveLabel}
      confirm
      fields={[
        { name: 'approve', label: t('f.approve'), type: 'switch' },
        { name: 'note', label: t('f.reason'), type: 'textarea' },
      ]}
      initialValues={{ approve: true }}
      onSubmit={(v) => api.post(path.replace('{id}', String(r.id)), { approve: !!v.approve, note: v.note, reason: v.note })}
      onDone={bump}
    />
  );

  return (
    <Page name="risk-assessments" title={t('nav.riskAssessments')}>
      <Tabs
        destroyOnHidden
        items={[
          {
            key: 'assessments',
            label: <span data-testid="tab-assessments">{t('risk.assessments')}</span>,
            children: (
              <>
                <FilterBar
                  onChange={setFilters}
                  fields={[
                    { name: 'status', label: t('f.status'), type: 'select', options: ['Completed', 'PendingApproval', 'Rejected', 'Superseded'].map((x) => ({ value: x, label: tv(x) })) },
                    { name: 'level', label: t('f.level'), type: 'select', options: levels },
                  ]}
                />
                <DataTable<Assessment>
                  testId="assessments-table"
                  url="/api/risk/assessments"
                  params={filters}
                  reloadKey={n}
                  columns={[
                    col('id', {
                      render: (v, r) => (
                        <Button type="link" style={{ padding: 0 }} onClick={() => setSel(r)} data-testid={`assessment-${r.id}`}>
                          #{v}
                        </Button>
                      ),
                    }),
                    col('customerId', { render: custLink }),
                    ...cols<Assessment>('methodologyCode', 'methodologyVersion', 'trigger', 'calculatedScore', 'calculatedLevel', 'finalLevel', 'status', 'calculatedAt'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Assessment) => (can('risk.approve') && r.status === 'PendingApproval' ? decision('/api/risk/assessments/{id}/decision', t('actions.decide'))(r) : null),
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'overrides',
            label: <span data-testid="tab-overrides">{t('risk.overrideRequests')}</span>,
            children: (
              <>
                <FilterBar onChange={setOvFilters} fields={[{ name: 'status', label: t('f.status'), type: 'select', options: ['Pending', 'Approved', 'Rejected'].map((x) => ({ value: x, label: tv(x) })) }]} />
                <DataTable
                  testId="overrides-table"
                  url="/api/risk/overrides"
                  params={ovFilters}
                  reloadKey={n}
                  columns={[
                    col('customerId', { render: custLink }),
                    ...cols('calculatedLevel', 'requestedLevel', 'reason', 'evidence', 'requestedBy', 'requestedAt', 'status', 'decidedBy', 'finalLevel'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Row) => (can('risk.override.approve') && r.status === 'Pending' ? decision('/api/risk/overrides/{id}/decision', t('actions.decide'))(r) : null),
                    },
                  ]}
                />
              </>
            ),
          },
          {
            key: 'tasks',
            label: <span data-testid="tab-review-tasks">{t('risk.reviewTasks')}</span>,
            children: (
              <>
                <FilterBar
                  onChange={setTaskFilters}
                  fields={[
                    { name: 'status', label: t('f.status'), type: 'select', options: ['Open', 'Escalated', 'Completed'].map((x) => ({ value: x, label: tv(x) })) },
                    { name: 'overdue', label: t('f.isOverdue'), type: 'switch' },
                  ]}
                />
                <DataTable
                  testId="review-tasks-table"
                  url="/api/risk/review-tasks"
                  params={taskFilters}
                  reloadKey={n}
                  columns={[
                    col('customerId', { render: custLink }),
                    ...cols('dueDate', 'owner', 'status', 'reason', 'escalatedTo', 'completedBy', 'completedAt'),
                    {
                      key: 'actions',
                      title: t('f.actions'),
                      render: (_: unknown, r: Row) =>
                        can('risk.review.work') && r.status !== 'Completed' ? (
                          <ActionButton
                            size="small"
                            label={t('actions.complete')}
                            fields={[{ name: 'evidence', label: t('f.evidence'), type: 'textarea', required: true }]}
                            onSubmit={(v) => api.post(`/api/risk/review-tasks/${r.id}/complete`, v)}
                            onDone={bump}
                          />
                        ) : null,
                    },
                  ]}
                />
              </>
            ),
          },
        ]}
      />
      <Modal open={!!sel} onCancel={() => setSel(null)} footer={null} width={1000} title={t('risk.assessmentDetail')} destroyOnHidden>
        {sel && <AssessmentView a={sel} />}
      </Modal>
    </Page>
  );
}
