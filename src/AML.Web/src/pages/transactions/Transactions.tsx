import { useState } from 'react';
import { Alert, Card, Col, Row as AntRow, Space, Tag, Typography } from 'antd';
import { Link, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { Query } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page, Section } from '../../components/Page';
import { Async } from '../../components/StateViews';
import { useApi, useLookupOptions } from '../../components/hooks';
import { KV, StatusTag, col, cols, fl } from '../../components/display';
import { txnColumns } from '../customers/ProfileTabs';
import { fmtAmount, fmtDateTime } from '../../utils/format';

export function TransactionsList() {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Query>({});
  const currencies = useLookupOptions('CURRENCY');
  const types = useLookupOptions('TXN_TYPE');
  const statuses = useLookupOptions('TXN_STATUS');
  return (
    <Page name="transactions" title={t('nav.transactions')}>
      <FilterBar
        testId="transaction-filters"
        onChange={setFilters}
        fields={[
          { name: 'from', label: t('f.dateFrom'), type: 'datetime' },
          { name: 'to', label: t('f.dateTo'), type: 'datetime' },
          { name: 'minAmount', label: t('f.minAmount'), type: 'number' },
          { name: 'maxAmount', label: t('f.maxAmount'), type: 'number' },
          { name: 'currency', label: t('f.currency'), type: 'select', options: currencies },
          { name: 'type', label: t('f.transactionType'), type: 'select', options: types },
          { name: 'status', label: t('f.status'), type: 'select', options: statuses },
          { name: 'channel', label: t('f.channel') },
          { name: 'customerId', label: t('f.customerId'), type: 'number' },
          { name: 'wallet', label: t('f.wallet') },
          { name: 'reference', label: t('f.reference') },
          { name: 'sourceSystem', label: t('f.sourceSystem'), type: 'select', options: ['CORE', 'WALLET'].map((x) => ({ value: x, label: x })) },
        ]}
      />
      <DataTable testId="transactions-table" url="/api/transactions" params={filters} columns={txnColumns()} defaultSort={{ sort: 'timestamp', dir: 'desc' }} />
    </Page>
  );
}

const custLink = (id?: number | null) => (id ? <Link to={`/customers/${id}`}>#{id}</Link> : '—');

function PartyBlock({ title, data, testId }: { title: string; data: Row | null | undefined; testId: string }) {
  const keys = data ? Object.keys(data) : [];
  return (
    <Card size="small" title={title} data-testid={testId} style={{ height: '100%' }}>
      {data && keys.length ? <KV data={data} keys={keys} column={1} /> : <Typography.Text type="secondary">—</Typography.Text>}
    </Card>
  );
}

export function TransactionDetail() {
  const { id } = useParams();
  const { t } = useTranslation();
  const st = useApi<Row>(`/api/transactions/${id}`);
  const rel = useApi<Row>(`/api/transactions/${id}/related`);
  return (
    <Async state={st}>
      {(d) => {
        const x = d.transaction as Row;
        const pt = (d.paymentTransparency ?? {}) as Row;
        return (
          <Page
            name="transaction-detail"
            title={`${t('txn.title')} ${x.sourceTransactionId}`}
            subTitle={
              <Space wrap>
                <StatusTag value={x.status} />
                <span>{fmtAmount(x.amount, x.currency)}</span>
                <span>{fmtDateTime(x.timestampUtc)}</span>
                {x.isTestData && <Tag>{t('common.testData')}</Tag>}
              </Space>
            }
          >
            <Section title={t('txn.sourceFields')} testId="txn-source">
              <KV
                data={x}
                keys={['sourceSystem', 'sourceTransactionId', 'originalReference', 'transactionType', 'sourceStatus', 'status', 'subType', 'mcc', 'channel', 'amount', 'currency', 'fee', 'commission', 'senderBalanceBefore', 'senderBalanceAfter', 'receiverBalanceBefore', 'receiverBalanceAfter', 'invoiceNumber', 'invoiceAmount', 'invoiceCurrency', 'goodsCategory', 'netAmount', 'reversedAmount', 'businessDate', 'timestampUtc', 'ingestedAt', 'ingestionRunId', 'dataQualityStatus', 'isReversal', 'isRefund', 'originalSourceTransactionId', 'sourceCountry', 'destinationCountry', 'remarks', 'copResult', 'payloadHash']}
              />
            </Section>
            <Section title={t('txn.fxEvidence')} testId="txn-fx">
              <KV data={x} keys={['amount', 'currency', 'fxRate', 'fxRateDate', 'fxRateVersion', 'fxStatus', 'baseAmount', 'baseCurrency']} render={{ amount: (v) => fmtAmount(v, x.currency), baseAmount: (v) => fmtAmount(v, x.baseCurrency) }} />
            </Section>
            <Section title={t('txn.parties')} testId="txn-parties">
              <Typography.Title level={5}>{t('txn.originalParties')}</Typography.Title>
              <KV data={{ ...(d.originalParties ?? {}), senderCustomerId: x.senderCustomerId, receiverCustomerId: x.receiverCustomerId }} keys={['sender', 'receiver', 'senderCustomerId', 'receiverCustomerId']} render={{ senderCustomerId: custLink, receiverCustomerId: custLink }} />
              <Typography.Title level={5} style={{ marginTop: 12 }}>
                {t('txn.resolvedParties')}
              </Typography.Title>
              <DataTable
                testId="resolved-parties"
                data={d.resolvedParties ?? []}
                rowKey="side"
                columns={[...cols('side', 'originalWalletExternalId'), col('resolvedCustomerId', { render: custLink }), ...cols('method', 'status', 'confidence', 'evidence', 'ruleVersion')]}
              />
            </Section>
            <Section title={t('txn.paymentTransparency')} testId="txn-pt" extra={<StatusTag value={pt.paymentTransparencyStatus} />}>
              {pt.paymentTransparencyIssues && <Alert type="warning" showIcon message={String(pt.paymentTransparencyIssues)} style={{ marginBottom: 12 }} />}
              <AntRow gutter={[16, 16]}>
                <Col xs={24} md={12}>
                  <PartyBlock title={t('txn.originator')} data={pt.originator} testId="pt-originator" />
                </Col>
                <Col xs={24} md={12}>
                  <PartyBlock title={t('txn.beneficiary')} data={pt.beneficiary} testId="pt-beneficiary" />
                </Col>
              </AntRow>
              <div style={{ marginTop: 12 }}>
                <KV data={pt} keys={['intermediaryInstitution', 'orderingInstitution', 'confirmationOfPayee']} />
              </div>
            </Section>
            <Section title={t('txn.statusHistory')}>
              <DataTable testId="txn-status-history" data={d.statusHistory ?? []} rowKey="receivedAt" columns={cols('sourceStatus', 'normalizedStatus', 'sourceEventAt', 'receivedAt', 'applied', 'note')} />
            </Section>
            <Section title={t('txn.riskAtTime')}>
              <DataTable
                testId="txn-risk"
                data={d.riskAtTime ?? []}
                rowKey="customerId"
                columns={[
                  col('customerId', { render: custLink }),
                  col('atTransactionTime', { render: (v: Row | null) => (v ? <span><StatusTag value={v.level} /> {v.score}</span> : '—') }),
                  col('current', { render: (v: Row | null) => (v ? <span><StatusTag value={v.level} /> {v.score} · {fmtDateTime(v.lastAssessedAt)}</span> : '—') }),
                ]}
              />
            </Section>
            <AntRow gutter={16}>
              <Col xs={24} lg={12}>
                <Section title={t('nav.alerts')}>
                  <DataTable testId="txn-alerts" data={d.alerts ?? []} columns={[col('alertNumber', { link: (r) => `/alerts/${r.id}` }), ...cols('title', 'severity', 'status', 'ruleCode')]} />
                </Section>
              </Col>
              <Col xs={24} lg={12}>
                <Section title={t('nav.cases')}>
                  <DataTable testId="txn-cases" data={d.cases ?? []} columns={[col('caseNumber', { link: (r) => `/cases/${r.id}` }), ...cols('title', 'status')]} />
                </Section>
              </Col>
            </AntRow>
            <Section title={t('txn.ruleMatches')}>
              <DataTable testId="txn-rule-matches" data={d.ruleMatches ?? []} rowKey={(r) => `${r.alertId}-${r.matchKey}`} columns={cols('alertId', 'ruleVersion', 'matchKey', 'windowStart', 'windowEnd', 'matchedAt')} />
            </Section>
            <Section title={t('txn.related')} testId="txn-related">
              <Async state={rel}>
                {(r) => (
                  <ol className="chain" data-testid="related-chain" aria-label={t('txn.related')}>
                    {((r.chain ?? []) as Row[]).map((s) => (
                      <li key={s.id} className={`chain-step ${String(s.id) === id ? 'current' : ''}`} data-testid="chain-step" data-step={s.step}>
                        <Space wrap>
                          <strong>{s.step}.</strong>
                          <Link to={`/transactions/${s.id}`}>{s.sourceTransactionId}</Link>
                          <Tag>{s.relation}</Tag>
                          <span>{fmtDateTime(s.timestampUtc)}</span>
                          <span>{fmtAmount(s.amount, s.currency)}</span>
                          <StatusTag value={s.status} />
                          <span>
                            {s.senderWalletExternalId} → {s.receiverWalletExternalId}
                          </span>
                          {s.isReversal && <Tag color="red">{fl('isReversal')}</Tag>}
                          {s.isRefund && <Tag color="orange">{fl('isRefund')}</Tag>}
                        </Space>
                      </li>
                    ))}
                  </ol>
                )}
              </Async>
            </Section>
          </Page>
        );
      }}
    </Async>
  );
}
