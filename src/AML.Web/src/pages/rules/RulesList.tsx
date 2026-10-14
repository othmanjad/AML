import { Button, Space } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { Rule } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable } from '../../components/DataTable';
import { Page } from '../../components/Page';
import { SampleTag, col, cols } from '../../components/display';

export default function RulesList() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const nav = useNavigate();
  return (
    <Page
      name="rules"
      title={t('nav.rules')}
      extra={
        can('rules.config') && (
          <Button type="primary" icon={<PlusOutlined />} onClick={() => nav('/rules/new')} data-testid="rule-create">
            {t('rule.newRule')}
          </Button>
        )
      }
    >
      <DataTable<Rule>
        testId="rules-table"
        url="/api/admin/aml-rules"
        columns={[
          col('code', { link: (r) => `/rules/${r.id}`, sort: 'code' }),
          col('name', {
            sort: 'name',
            render: (v, r) => (
              <Space wrap size={4}>
                {v}
                <SampleTag show={r.isSample} />
              </Space>
            ),
          }),
          col('status', { sort: 'status' }),
          ...cols<Rule>('category', 'ruleType', 'severity', 'activeVersion', 'isRealtimeCapable', 'typologyCodes', 'createdAt'),
        ]}
      />
    </Page>
  );
}
