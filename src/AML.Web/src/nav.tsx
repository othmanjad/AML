import type { ReactNode } from 'react';
import {
  AlertOutlined,
  AuditOutlined,
  BankOutlined,
  BarChartOutlined,
  ClusterOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  ExperimentOutlined,
  FileProtectOutlined,
  FileSearchOutlined,
  FolderOpenOutlined,
  LockOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
  SwapOutlined,
  TeamOutlined,
  UnorderedListOutlined,
  FundOutlined,
  MailOutlined,
} from '@ant-design/icons';

export interface NavItem {
  key: string;
  path?: string;
  perm?: string | string[];
  icon?: ReactNode;
  children?: NavItem[];
}

/** Primary navigation. Items are hidden when the user lacks every listed permission. Labels: nav.<key>. */
export const NAV: NavItem[] = [
  { key: 'dashboard', path: '/dashboard', perm: 'dashboard.read', icon: <DashboardOutlined /> },
  { key: 'customers', path: '/customers', perm: 'customers.read', icon: <TeamOutlined /> },
  { key: 'transactions', path: '/transactions', perm: 'transactions.read', icon: <SwapOutlined /> },
  { key: 'riskAssessments', path: '/risk/assessments', perm: 'risk.read', icon: <FundOutlined /> },
  { key: 'rules', path: '/rules', perm: 'rules.read', icon: <ClusterOutlined /> },
  { key: 'alerts', path: '/alerts', perm: 'alerts.read', icon: <AlertOutlined /> },
  { key: 'cases', path: '/cases', perm: 'cases.read', icon: <FolderOpenOutlined /> },
  { key: 'screening', path: '/screening', perm: 'screening.read', icon: <FileSearchOutlined /> },
  { key: 'watchlists', path: '/watchlists', perm: 'screening.read', icon: <UnorderedListOutlined /> },
  { key: 'regulatoryReports', path: '/regulatory-reports', perm: 'str.prepare', icon: <FileProtectOutlined /> },
  { key: 'ctrReports', path: '/ctr-reports', perm: 'str.prepare', icon: <FileProtectOutlined /> },
  { key: 'authorityRequests', path: '/authority-requests', perm: 'authority.manage', icon: <MailOutlined /> },
  { key: 'actions', path: '/actions', perm: ['actions.read', 'holds.work'], icon: <LockOutlined /> },
  { key: 'effectiveness', path: '/effectiveness', perm: ['effectiveness.read', 'qa.review'], icon: <ExperimentOutlined /> },
  { key: 'reports', path: '/reports', perm: 'reports.read', icon: <BarChartOutlined /> },
  { key: 'operations', path: '/operations', perm: 'operations.read', icon: <DatabaseOutlined /> },
  {
    key: 'admin',
    icon: <SettingOutlined />,
    children: [
      { key: 'fieldConfig', path: '/admin/fields', perm: 'config.fields', icon: <SafetyCertificateOutlined /> },
      { key: 'formLayouts', path: '/admin/layouts', perm: 'config.layouts' },
      { key: 'riskMethodologies', path: '/admin/risk-methodologies', perm: 'risk.read' },
      { key: 'ruleBuilder', path: '/rules/new', perm: 'rules.config' },
      { key: 'benchmarks', path: '/admin/benchmarks', perm: 'rules.read' },
      { key: 'walletMappings', path: '/admin/wallet-mappings', perm: 'admin.wallet-mappings', icon: <BankOutlined /> },
      { key: 'lookups', path: '/admin/lookups', perm: 'admin.lookups' },
      { key: 'users', path: '/admin/users', perm: 'admin.users' },
      { key: 'audit', path: '/admin/audit', perm: 'audit.read', icon: <AuditOutlined /> },
      { key: 'jurisdiction', path: '/admin/jurisdiction', perm: 'jurisdiction.manage' },
      { key: 'fx', path: '/admin/fx-rates', perm: 'admin.fx' },
      { key: 'settings', path: '/admin/settings', perm: 'admin.settings' },
    ],
  },
];

export const visibleNav = (can: (p?: string | string[]) => boolean): NavItem[] =>
  NAV.map((n) => (n.children ? { ...n, children: n.children.filter((c) => can(c.perm)) } : n)).filter((n) => (n.children ? n.children.length > 0 : can(n.perm)));

export const flatNav = (items: NavItem[] = NAV): NavItem[] => items.flatMap((n) => (n.children ? flatNav(n.children) : [n]));
