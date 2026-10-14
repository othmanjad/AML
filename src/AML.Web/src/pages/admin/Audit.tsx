import { useState } from 'react';
import { Alert } from 'antd';
import { SafetyOutlined } from '@ant-design/icons';
import { useTranslation } from 'react-i18next';
import { api, type Query } from '../../api/client';
import type { Row } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { FilterBar, Page } from '../../components/Page';
import { ActionButton } from '../../components/ActionButton';
import { JsonBlock } from '../../components/display';
import { auditColumns } from '../customers/ProfileTabs';

export default function AuditPage() {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<Query>({});
  const [verify, setVerify] = useState<Row | null>(null);
  return (
    <Page
      name="audit"
      title={t('nav.audit')}
      extra={<ActionButton testId="audit-verify" icon={<SafetyOutlined />} label={t('audit.verify')} onSubmit={() => api.post<Row>('/api/admin/audit/verify')} onDone={(r: Row) => setVerify(r)} />}
    >
      {verify && (
        <Alert
          data-testid="audit-verify-result"
          closable
          onClose={() => setVerify(null)}
          style={{ marginBottom: 12 }}
          type={verify.valid === false || verify.intact === false ? 'error' : 'success'}
          showIcon
          message={t('audit.verifyResult')}
          description={<JsonBlock value={verify} maxHeight={200} />}
        />
      )}
      <FilterBar
        testId="audit-filters"
        onChange={setFilters}
        fields={[
          { name: 'entityType', label: t('f.entityType') },
          { name: 'entityId', label: t('f.entityId') },
          { name: 'actor', label: t('f.actor') },
          { name: 'action', label: t('f.action') },
          { name: 'from', label: t('f.dateFrom'), type: 'datetime' },
          { name: 'to', label: t('f.dateTo'), type: 'datetime' },
        ]}
      />
      <DataTable testId="audit-table" url="/api/admin/audit" params={filters} pageSize={50} columns={auditColumns()} />
    </Page>
  );
}
