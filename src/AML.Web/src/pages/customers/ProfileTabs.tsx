import { useState } from 'react';
import { Alert, Button, Card, Form, Input, List, Modal, Space, Tag, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import { api, ApiError } from '../../api/client';
import type { Assessment, AttributeValue, CustomerDetail, FormMetadata, Row } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Section } from '../../components/Page';
import { Async, ErrorAlert } from '../../components/StateViews';
import { useApi, useLookupOptions } from '../../components/hooks';
import { KV, StatusTag, col, cols, tv } from '../../components/display';
import { ActionButton } from '../../components/ActionButton';
import { AssessmentView } from '../../components/RiskViews';
import { DynamicForm, attributeInitialValues, cleanAttributes, fieldLabel } from '../../components/DynamicForm';
import { fmtAmount, fmtDateTime } from '../../utils/format';

type P = { c: CustomerDetail; reload?: () => void };

export const SCREENING_TRANSITIONS: Record<string, string[]> = {
  PotentialMatch: ['UnderReview', 'Escalated', 'ConfirmedMatch', 'FalsePositive', 'UnableToDetermine'],
  UnderReview: ['ConfirmedMatch', 'FalsePositive', 'UnableToDetermine', 'Escalated'],
  Escalated: ['ConfirmedMatch', 'FalsePositive', 'UnableToDetermine', 'UnderReview'],
  UnableToDetermine: ['UnderReview', 'Escalated', 'ConfirmedMatch', 'FalsePositive'],
  FalsePositive: ['UnderReview'],
  ConfirmedMatch: ['UnderReview'],
};

export function CoreTab({ c, reload }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const countries = useLookupOptions('COUNTRY');
  return (
    <Section
      title={t('tabs.core')}
      extra={
        can('customers.write') && (
          <ActionButton
            testId="core-edit"
            label={t('actions.edit')}
            initialValues={c}
            fields={[
              { name: 'nameLatin', label: t('f.nameLatin') },
              { name: 'nameArabic', label: t('f.nameArabic') },
              { name: 'dateOfBirth', label: t('f.dateOfBirth'), type: 'date' },
              { name: 'nationalityCountry', label: t('f.nationalityCountry'), type: 'select', options: countries },
              { name: 'residenceCountry', label: t('f.residenceCountry'), type: 'select', options: countries },
              { name: 'incorporationCountry', label: t('f.incorporationCountry'), type: 'select', options: countries },
              { name: 'assignedOfficer', label: t('f.assignedOfficer') },
              { name: 'region', label: t('f.region') },
              { name: 'isPep', label: t('f.isPep'), type: 'switch' },
              { name: 'reason', label: t('f.reason'), type: 'textarea', required: true },
            ]}
            onSubmit={(v) => api.put(`/api/customers/${c.id}`, { ...v, rowVersion: c.rowVersion })}
            onDone={reload}
          />
        )
      }
    >
      <KV
        testId="core-fields"
        data={c}
        keys={['sourceSystem', 'externalId', 'customerType', 'status', 'nameLatin', 'nameArabic', 'dateOfBirth', 'nationalityCountry', 'residenceCountry', 'incorporationCountry', 'registrationDate', 'assignedOfficer', 'region', 'isPep', 'isFrozen', 'exitedAt', 'isTestData', 'rowVersion']}
      />
    </Section>
  );
}

function AttributeEditor({ c, meta, attrs, onDone }: { c: CustomerDetail; meta: FormMetadata; attrs: AttributeValue[]; onDone: () => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [form] = Form.useForm();
  const [error, setError] = useState<ApiError | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);
  const save = async () => {
    let v: Record<string, unknown>;
    try {
      v = await form.validateFields();
    } catch {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.put(`/api/customers/${c.id}/attributes`, { values: cleanAttributes(v.attributes as Record<string, unknown>), reason: v.reason, source: 'UI' });
      setOpen(false);
      onDone();
    } catch (e) {
      const err = e as ApiError;
      setError(err);
      if (err.errors) setFieldErrors(Object.fromEntries(Object.entries(err.errors).map(([k, m]) => [k.replace(/^(values|attributes)\./i, '').toUpperCase(), m])));
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <Button data-testid="attributes-edit" onClick={() => setOpen(true)}>
        {t('actions.edit')}
      </Button>
      <Modal
        open={open}
        title={t('profile.editAttributes')}
        onCancel={() => setOpen(false)}
        onOk={save}
        confirmLoading={busy}
        width={820}
        destroyOnHidden
        okText={t('actions.save')}
        cancelText={t('actions.cancel')}
        okButtonProps={{ 'data-testid': 'attributes-save' } as never}
        modalRender={(n) => <div data-testid="attributes-modal">{n}</div>}
      >
        <ErrorAlert error={error} />
        <Form form={form} layout="vertical" initialValues={{ attributes: attributeInitialValues(attrs) }} preserve={false}>
          <DynamicForm meta={meta} serverErrors={fieldErrors} />
          <Form.Item name="reason" label={t('f.reason')}>
            <Input.TextArea rows={2} data-testid="attributes-reason" />
          </Form.Item>
        </Form>
      </Modal>
    </>
  );
}

export function AttributesTab({ c }: P) {
  const { t, i18n } = useTranslation();
  const { can } = useAuth();
  const attrs = useApi<AttributeValue[]>(`/api/customers/${c.id}/attributes`);
  const meta = useApi<FormMetadata>('/api/config/customer-fields', { customerType: c.customerType });
  const [n, setN] = useState(0);
  return (
    <>
      <Section
        title={t('tabs.attributes')}
        extra={can('customers.write') && meta.data && attrs.data && <AttributeEditor c={c} meta={meta.data} attrs={attrs.data} onDone={() => (attrs.reload(), setN((x) => x + 1))} />}
      >
        {meta.error && <ErrorAlert error={meta.error} />}
        <Async state={attrs}>
          {(list) => {
            const groups = meta.data?.groups ?? [];
            const codes = [...new Set([...groups.map((g) => g.code), ...list.map((a) => a.groupCode)])];
            return (
              <div data-testid="attribute-groups">
                {codes
                  .map((code) => ({ code, g: groups.find((x) => x.code === code), items: list.filter((a) => a.groupCode === code) }))
                  .filter((x) => x.items.length)
                  .map(({ code, g, items }) => (
                    <Card key={code} size="small" title={g ? fieldLabel(g, i18n.language) : code} style={{ marginBottom: 12 }}>
                      <KV
                        data={Object.fromEntries(items.map((a) => [a.code, a]))}
                        keys={items.map((a) => a.code)}
                        labels={Object.fromEntries(items.map((a) => [a.code, fieldLabel(a, i18n.language)]))}
                        render={Object.fromEntries(
                          items.map((a) => [
                            a.code,
                            (v: AttributeValue) => (
                              <span data-testid={`attr-${a.code}`}>
                                {v.display} {v.masked && <Tag>{t('profile.masked')}</Tag>} <StatusTag value={v.verificationStatus} />
                              </span>
                            ),
                          ]),
                        )}
                      />
                    </Card>
                  ))}
              </div>
            );
          }}
        </Async>
      </Section>
      <Section title={t('profile.attributeHistory')}>
        <DataTable
          testId="attribute-history"
          url={`/api/customers/${c.id}/attributes/history`}
          reloadKey={n}
          columns={cols('fieldCode', 'fieldVersion', 'previousValue', 'newValue', 'actor', 'timestampUtc', 'reason', 'correlationId')}
        />
      </Section>
    </>
  );
}

export function KycTab({ c, reload }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => (setN((x) => x + 1), reload?.());
  return (
    <>
      {c.kycGaps.length > 0 && (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          data-testid="kyc-gaps"
          message={t('profile.kycGapsTitle')}
          description={
            <ul style={{ margin: 0 }}>
              {c.kycGaps.map((g) => (
                <li key={g}>{g}</li>
              ))}
            </ul>
          }
        />
      )}
      <Section
        title={t('profile.identifications')}
        extra={
          can('customers.write') && (
            <ActionButton
              testId="add-identification"
              label={t('actions.add')}
              fields={[
                { name: 'idType', label: t('f.idType'), required: true },
                { name: 'idNumber', label: t('f.idNumber'), required: true },
                { name: 'issuingCountry', label: t('f.issuingCountry') },
                { name: 'issueDate', label: t('f.issueDate'), type: 'date' },
                { name: 'expiryDate', label: t('f.expiryDate'), type: 'date' },
              ]}
              onSubmit={(v) => api.post(`/api/customers/${c.id}/identifications`, v)}
              onDone={reload}
            />
          )
        }
      >
        <DataTable testId="identifications" data={c.identifications as Row[]} columns={cols('idType', 'masked', 'issuingCountry', 'issueDate', 'expiryDate', 'verificationStatus', 'isExpired')} />
      </Section>
      <Section
        title={t('profile.contacts')}
        extra={
          can('customers.write') && (
            <ActionButton
              testId="add-contact"
              label={t('actions.add')}
              fields={[
                { name: 'contactType', label: t('f.contactType'), type: 'select', required: true, options: ['Phone', 'Email', 'Address'].map((x) => ({ value: x, label: tv(x) })) },
                { name: 'value', label: t('f.value'), required: true },
                { name: 'isPrimary', label: t('f.isPrimary'), type: 'switch' },
              ]}
              onSubmit={(v) => api.post(`/api/customers/${c.id}/contacts`, v)}
              onDone={reload}
            />
          )
        }
      >
        <DataTable testId="contacts" data={c.contacts as Row[]} columns={cols('contactType', 'masked', 'isPrimary')} />
      </Section>
      <Section
        title={t('profile.acceptance')}
        extra={
          <Space wrap>
            {can('cdd.decide') && <ActionButton testId="onboard" type="primary" label={t('profile.onboard')} confirm onSubmit={() => api.post(`/api/customers/${c.id}/onboard`)} onDone={bump} />}
            {can('cdd.decide') && <ActionButton testId="acceptance-decide" label={t('profile.decide')} onSubmit={() => api.post(`/api/customers/${c.id}/acceptance/decide`)} onDone={bump} />}
            {can('cdd.senior-approve') && (
              <ActionButton
                testId="senior-approval"
                label={t('profile.seniorApproval')}
                confirm
                fields={[
                  { name: 'approve', label: t('f.approve'), type: 'switch' },
                  { name: 'reason', label: t('f.reason'), type: 'textarea' },
                ]}
                initialValues={{ approve: true }}
                onSubmit={(v) => api.post(`/api/customers/${c.id}/acceptance/senior-approval`, { approve: !!v.approve, reason: v.reason })}
                onDone={bump}
              />
            )}
          </Space>
        }
      >
        <DataTable testId="acceptance-decisions" url={`/api/customers/${c.id}/acceptance`} reloadKey={n} columns={cols('decision', 'policyCode', 'policyVersion', 'decidedBy', 'approvedBy', 'decidedAt', 'reason', 'reasonCodes', 'conditions', 'isAutomatic', 'isCurrent')} />
      </Section>
      <Section title={t('profile.eddTasks')}>
        <DataTable
          testId="edd-tasks"
          url={`/api/customers/${c.id}/edd-tasks`}
          reloadKey={n}
          columns={[
            ...cols('taskType', 'description', 'levelCode', 'status', 'evidenceReference', 'signedOffBy', 'signedOffAt'),
            {
              key: 'actions',
              title: t('f.actions'),
              render: (_: unknown, r: Row) =>
                can('cdd.decide') && r.status !== 'Completed' ? (
                  <ActionButton size="small" label={t('actions.complete')} fields={[{ name: 'evidence', label: t('f.evidence'), type: 'textarea' }]} onSubmit={(v) => api.post(`/api/customers/edd-tasks/${r.id}/complete`, v)} onDone={bump} />
                ) : null,
            },
          ]}
        />
      </Section>
      <Section title={t('tabs.documents')}>
        <DataTable testId="kyc-documents" url={`/api/customers/${c.id}/documents`} reloadKey={n} columns={cols('documentType', 'reference', 'expiryDate', 'status', 'sha256', 'createdAt')} />
      </Section>
    </>
  );
}

export function FinancialTab({ c }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const currencies = useLookupOptions('CURRENCY');
  return (
    <Section
      title={t('tabs.financial')}
      extra={
        can('customers.write') && (
          <ActionButton
            testId="add-financial-profile"
            label={t('actions.add')}
            fields={[
              { name: 'declaredMonthlyIncome', label: t('f.declaredMonthlyIncome'), type: 'number' },
              { name: 'currency', label: t('f.currency'), type: 'select', options: currencies },
              { name: 'sourceOfFunds', label: t('f.sourceOfFunds') },
              { name: 'sourceOfWealth', label: t('f.sourceOfWealth') },
              { name: 'occupation', label: t('f.occupation') },
              { name: 'expectedMonthlyVolume', label: t('f.expectedMonthlyVolume'), type: 'number' },
              { name: 'expectedMonthlyCount', label: t('f.expectedMonthlyCount'), type: 'number' },
              { name: 'effectiveFrom', label: t('f.effectiveFrom'), type: 'datetime' },
            ]}
            onSubmit={(v) => api.post(`/api/customers/${c.id}/financial-profiles`, v)}
            onDone={() => setN((x) => x + 1)}
          />
        )
      }
    >
      <DataTable
        testId="financial-profiles"
        url={`/api/customers/${c.id}/financial-profiles`}
        reloadKey={n}
        columns={cols('declaredMonthlyIncome', 'currency', 'sourceOfFunds', 'sourceOfWealth', 'occupation', 'expectedMonthlyVolume', 'expectedMonthlyCount', 'effectiveFrom', 'effectiveTo', 'declaredAt')}
      />
    </Section>
  );
}

const stripEmptyChildren = (rows: Row[]): Row[] => rows.map((r) => ({ ...r, children: r.children?.length ? stripEmptyChildren(r.children) : undefined }));

export function WalletsTab({ c }: P) {
  const { t } = useTranslation();
  return (
    <Section title={t('tabs.wallets')}>
      <DataTable
        testId="wallets-tree"
        url={`/api/customers/${c.id}/wallets`}
        transform={(x: Row[]) => stripEmptyChildren(x)}
        expandable={{ defaultExpandAllRows: true }}
        columns={[
          col('externalWalletId'),
          col('walletType'),
          col('status'),
          col('currency'),
          col('isInternal', { render: (v: boolean) => (v ? <Tag color="purple" data-testid="wallet-internal">{t('profile.internal')}</Tag> : t('common.no')) }),
          col('mappingCategory'),
          col('isFrozen'),
          col('sourceSystem'),
        ]}
      />
    </Section>
  );
}

export const txnColumns = () => [
  col<Row>('sourceTransactionId', { link: (r) => `/transactions/${r.id}`, sort: 'reference' }),
  col<Row>('timestampUtc', { sort: 'timestamp' }),
  col<Row>('transactionType', { sort: 'type' }),
  col<Row>('status', { sort: 'status' }),
  col<Row>('amount', { sort: 'amount', render: (v, r) => fmtAmount(v, r.currency) }),
  col<Row>('baseAmount', { sort: 'baseAmount', render: (v, r) => fmtAmount(v, r.baseCurrency) }),
  col<Row>('channel'),
  col<Row>('senderWalletExternalId'),
  col<Row>('receiverWalletExternalId'),
  col<Row>('dataQualityStatus'),
];

export function TransactionsTab({ c }: P) {
  return <DataTable testId="customer-transactions" url={`/api/customers/${c.id}/transactions`} columns={txnColumns()} />;
}

export function RiskTab({ c, reload }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [sel, setSel] = useState<Assessment | null>(null);
  const [n, setN] = useState(0);
  return (
    <>
      <Section title={t('profile.assessmentHistory')}>
        <DataTable<Assessment>
          testId="assessment-history"
          url={`/api/customers/${c.id}/risk-assessments`}
          columns={[
            col('calculatedAt', {
              render: (v, r) => (
                <Button type="link" onClick={() => setSel(r)} data-testid={`assessment-${r.id}`} style={{ padding: 0 }}>
                  {fmtDateTime(v)}
                </Button>
              ),
            }),
            ...cols<Assessment>('trigger', 'methodologyCode', 'methodologyVersion', 'calculatedScore', 'calculatedLevel', 'finalLevel', 'status', 'hasMissingData'),
          ]}
        />
      </Section>
      <Section title={t('risk.overrideRequests')}>
        <DataTable
          testId="override-requests"
          url="/api/risk/overrides"
          params={{ customerId: c.id }}
          reloadKey={n}
          columns={[
            ...cols('requestedLevel', 'calculatedLevel', 'reason', 'requestedBy', 'requestedAt', 'status', 'decidedBy', 'decisionReason'),
            {
              key: 'actions',
              title: t('f.actions'),
              render: (_: unknown, r: Row) =>
                can('risk.override.approve') && r.status === 'Pending' ? (
                  <ActionButton
                    size="small"
                    label={t('actions.decide')}
                    confirm
                    fields={[
                      { name: 'approve', label: t('f.approve'), type: 'switch' },
                      { name: 'reason', label: t('f.reason'), type: 'textarea' },
                    ]}
                    initialValues={{ approve: true }}
                    onSubmit={(v) => api.post(`/api/risk/overrides/${r.id}/decision`, { approve: !!v.approve, reason: v.reason })}
                    onDone={() => (setN((x) => x + 1), reload?.())}
                  />
                ) : null,
            },
          ]}
        />
      </Section>
      <Modal open={!!sel} onCancel={() => setSel(null)} footer={null} width={1000} title={t('risk.assessmentDetail')} destroyOnHidden>
        {sel && <AssessmentView a={sel} />}
      </Modal>
    </>
  );
}


export function ScreeningResultsTable({ url, params, transform, reloadKey, onChanged, testId }: { url: string; params?: Row; transform?: (x: Row) => Row[]; reloadKey?: unknown; onChanged?: () => void; testId: string }) {
  const { t } = useTranslation();
  const { can } = useAuth();
  return (
    <DataTable
      testId={testId}
      url={url}
      params={params}
      transform={transform}
      reloadKey={reloadKey}
      columns={[
        ...cols('subjectName', 'sourceCode', 'listEntryId', 'listVersion', 'matchScore', 'matchMethod', 'category', 'status', 'screenedAt', 'reviewer', 'explanation'),
        {
          key: 'actions',
          title: t('f.actions'),
          render: (_: unknown, r: Row) =>
            can('screening.review') && SCREENING_TRANSITIONS[r.status] ? (
              <ActionButton
                size="small"
                testId={`screening-transition-${r.id}`}
                label={t('screening.transition')}
                confirm={t('screening.confirmTransition')}
                fields={[
                  { name: 'toStatus', label: t('f.toStatus'), type: 'select', required: true, options: SCREENING_TRANSITIONS[r.status].map((s) => ({ value: s, label: tv(s) })) },
                  { name: 'notes', label: t('f.notes'), type: 'textarea' },
                ]}
                onSubmit={(v) => api.post(`/api/screening/results/${r.id}/transition`, { ...v, rowVersion: r.rowVersion })}
                onDone={onChanged}
              />
            ) : null,
        },
      ]}
    />
  );
}

export function ScreeningTab({ c, reload }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const bump = () => (setN((x) => x + 1), reload?.());
  return (
    <>
      <Section title={t('screening.results')}>
        <ScreeningResultsTable testId="customer-screening-results" url={`/api/customers/${c.id}/screening`} transform={(x) => x.results} reloadKey={n} onChanged={bump} />
      </Section>
      <Section title={t('screening.requests')}>
        <DataTable
          testId="customer-screening-requests"
          url={`/api/customers/${c.id}/screening`}
          transform={(x: Row) => x.requests}
          reloadKey={n}
          columns={[
            ...cols('subjectName', 'trigger', 'status', 'attempts', 'resultCount', 'suppressedCount', 'lastError', 'createdAt', 'completedAt'),
            {
              key: 'actions',
              title: t('f.actions'),
              render: (_: unknown, r: Row) =>
                can('screening.run') && r.status !== 'Completed' ? <ActionButton size="small" label={t('actions.retry')} onSubmit={() => api.post(`/api/screening/requests/${r.id}/retry`)} onDone={bump} /> : null,
            },
          ]}
        />
      </Section>
    </>
  );
}

export const alertColumns = () => [
  col<Row>('alertNumber', { link: (r) => `/alerts/${r.id}`, sort: 'id' }),
  col<Row>('title'),
  col<Row>('severity', { sort: 'severity' }),
  col<Row>('status', { sort: 'status' }),
  col<Row>('ruleCode', { sort: 'rule' }),
  col<Row>('assignedTo'),
  col<Row>('createdAt', { sort: 'createdAt' }),
  col<Row>('dueAt', { sort: 'dueAt' }),
  col<Row>('isOverdue', { render: (v: boolean) => (v ? <Tag color="red">{tv('Overdue')}</Tag> : '—') }),
];

export const caseColumns = () => [
  col<Row>('caseNumber', { link: (r) => `/cases/${r.id}`, sort: 'caseNumber' }),
  col<Row>('title'),
  col<Row>('caseType'),
  col<Row>('priority', { sort: 'priority' }),
  col<Row>('status', { sort: 'status' }),
  col<Row>('assignedInvestigator'),
  col<Row>('dueDate', { sort: 'dueDate' }),
  col<Row>('createdAt', { sort: 'createdAt' }),
  col<Row>('isOverdue', { render: (v: boolean) => (v ? <Tag color="red">{tv('Overdue')}</Tag> : '—') }),
];

export function AlertsTab({ c }: P) {
  return <DataTable testId="customer-alerts" url={`/api/customers/${c.id}/alerts`} columns={alertColumns()} />;
}

export function CasesTab({ c }: P) {
  return <DataTable testId="customer-cases" url={`/api/customers/${c.id}/cases`} columns={caseColumns()} />;
}

export function RelationshipsTab({ c }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  const own = useApi<Row>(`/api/customers/${c.id}/ownership`, { n });
  return (
    <>
      <Section title={t('profile.entityLinks')}>
        <DataTable testId="entity-links" url={`/api/customers/${c.id}/relationships`} columns={cols('linkType', 'customerAId', 'customerBId', 'confidence', 'evidence', 'status', 'ruleVersion', 'createdAt')} />
      </Section>
      <Section
        title={t('profile.ownership')}
        extra={
          can('customers.write') && (
            <ActionButton
              testId="add-ownership"
              label={t('actions.add')}
              fields={[
                { name: 'ownerCustomerId', label: t('f.ownerCustomerId'), type: 'number' },
                { name: 'ownerName', label: t('f.ownerName') },
                { name: 'ownerType', label: t('f.ownerType'), type: 'select', options: ['Person', 'Entity'].map((x) => ({ value: x, label: tv(x) })) },
                { name: 'percentage', label: t('f.percentage'), type: 'number' },
                { name: 'controlType', label: t('f.controlType'), type: 'select', options: ['Ownership', 'Control', 'Signatory', 'Director'].map((x) => ({ value: x, label: tv(x) })) },
                { name: 'ownerIsPep', label: t('f.ownerIsPep'), type: 'switch' },
                { name: 'evidence', label: t('f.evidence'), type: 'textarea' },
              ]}
              initialValues={{ ownerType: 'Person', controlType: 'Ownership' }}
              onSubmit={(v) => api.post(`/api/customers/${c.id}/ownership`, v)}
              onDone={() => setN((x) => x + 1)}
            />
          )
        }
      >
        <Async state={own}>
          {(o) => (
            <>
              <Typography.Paragraph>
                {t('f.thresholdPercent')}: {o.thresholdPercent}% — <Typography.Text type="secondary">{o.thresholdSource}</Typography.Text>
              </Typography.Paragraph>
              {o.missingOwnershipInformation && <Alert type="warning" showIcon message={t('profile.missingOwnership')} style={{ marginBottom: 8 }} />}
              {o.cycles?.length > 0 && <Alert type="error" showIcon message={`${t('profile.cycles')}: ${o.cycles.join('; ')}`} style={{ marginBottom: 8 }} />}
              <Typography.Title level={5}>{t('profile.uboAnalysis')}</Typography.Title>
              <DataTable
                testId="ubo-table"
                data={o.owners}
                rowKey="ownerKey"
                columns={[
                  ...cols('ownerName'),
                  col('effectivePercentage', { render: (v: number) => `${v}%` }),
                  col('isBeneficialOwner', { render: (v: boolean) => (v ? <Tag color="red">{t('profile.ubo')}</Tag> : t('common.no')) }),
                  ...cols('controlTypes', 'basis', 'paths', 'isPep'),
                ]}
              />
              <Typography.Title level={5} style={{ marginTop: 12 }}>
                {t('profile.ownershipLinks')}
              </Typography.Title>
              <DataTable testId="ownership-links" data={o.links} columns={cols('ownerName', 'ownerType', 'percentage', 'controlType', 'ownerIsPep', 'effectiveFrom', 'effectiveTo', 'evidence')} />
              {o.unresolved?.length > 0 && (
                <List size="small" header={t('profile.unresolved')} dataSource={o.unresolved as string[]} renderItem={(x) => <List.Item>{x}</List.Item>} />
              )}
            </>
          )}
        </Async>
      </Section>
    </>
  );
}

export function DocumentsTab({ c }: P) {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [n, setN] = useState(0);
  return (
    <Section
      title={t('tabs.documents')}
      extra={
        can('customers.write') && (
          <ActionButton
            testId="add-document"
            label={t('actions.add')}
            fields={[
              { name: 'documentType', label: t('f.documentType'), required: true },
              { name: 'reference', label: t('f.reference') },
              { name: 'expiryDate', label: t('f.expiryDate'), type: 'date' },
            ]}
            onSubmit={(v) => api.post(`/api/customers/${c.id}/documents`, v)}
            onDone={() => setN((x) => x + 1)}
          />
        )
      }
    >
      <DataTable testId="customer-documents" url={`/api/customers/${c.id}/documents`} reloadKey={n} columns={cols('documentType', 'reference', 'expiryDate', 'status', 'sha256', 'createdAt')} />
    </Section>
  );
}

export const auditColumns = () => cols('timestampUtc', 'actor', 'action', 'entityType', 'entityId', 'reason', 'outcome', 'correlationId', 'newValues');

export function HistoryTab({ c }: P) {
  return <DataTable testId="customer-history" url={`/api/customers/${c.id}/history`} columns={auditColumns()} />;
}
