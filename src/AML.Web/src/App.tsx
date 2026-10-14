import type { ReactNode } from 'react';
import { ConfigProvider, App as AntApp } from 'antd';
import enUS from 'antd/locale/en_US';
import arEG from 'antd/locale/ar_EG';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from './auth/AuthContext';
import AppLayout from './AppLayout';
import LoginPage from './pages/LoginPage';
import { StateForbidden, StateLoading } from './components/StateViews';
import Dashboard from './pages/Dashboard';
import CustomersList from './pages/customers/CustomersList';
import CustomerCreate from './pages/customers/CustomerCreate';
import CustomerProfile from './pages/customers/CustomerProfile';
import { TransactionDetail, TransactionsList } from './pages/transactions/Transactions';
import RiskAssessments from './pages/risk/RiskAssessments';
import { MethodologiesList, MethodologyDetail } from './pages/risk/Methodologies';
import RulesList from './pages/rules/RulesList';
import RuleBuilder from './pages/rules/RuleBuilder';
import { AlertDetail, AlertsList } from './pages/alerts/Alerts';
import { CaseDetail, CasesList } from './pages/cases/Cases';
import ScreeningPage from './pages/screening/Screening';
import WatchlistsPage from './pages/screening/Watchlists';
import { RegulatoryReportDetail, RegulatoryReportsList } from './pages/reporting/RegulatoryReports';
import AuthorityRequests from './pages/reporting/AuthorityRequests';
import ActionsPage from './pages/ActionsPage';
import EffectivenessPage from './pages/EffectivenessPage';
import ReportsPage from './pages/ReportsPage';
import OperationsPage from './pages/OperationsPage';
import FieldConfigPage from './pages/admin/FieldConfig';
import FormLayoutsPage from './pages/admin/FormLayouts';
import WalletMappingsPage from './pages/admin/WalletMappings';
import LookupsPage from './pages/admin/Lookups';
import UsersPage from './pages/admin/Users';
import AuditPage from './pages/admin/Audit';
import JurisdictionPage from './pages/admin/Jurisdiction';
import FxRatesPage from './pages/admin/FxRates';
import SettingsPage from './pages/admin/Settings';

const theme = {
  token: {
    colorPrimary: '#1f5fbf',
    colorLink: '#1a55ad',
    borderRadius: 6,
    fontFamily: "'Segoe UI', 'Noto Sans Arabic', 'Noto Sans', Tahoma, Arial, sans-serif",
  },
};

function Guard({ perm, children }: { perm?: string | string[]; children: ReactNode }) {
  const { can } = useAuth();
  return can(perm) ? <>{children}</> : <StateForbidden />;
}

function RequireAuth({ children }: { children: ReactNode }) {
  const { me, loading } = useAuth();
  const loc = useLocation();
  if (loading) return <StateLoading />;
  if (!me) return <Navigate to="/login" replace state={{ from: loc.pathname + loc.search }} />;
  return <>{children}</>;
}

const routes: Array<{ path: string; perm?: string | string[]; el: ReactNode }> = [
  { path: '/dashboard', perm: 'dashboard.read', el: <Dashboard /> },
  { path: '/customers', perm: 'customers.read', el: <CustomersList /> },
  { path: '/customers/new', perm: 'customers.write', el: <CustomerCreate /> },
  { path: '/customers/:id', perm: 'customers.read', el: <CustomerProfile /> },
  { path: '/transactions', perm: 'transactions.read', el: <TransactionsList /> },
  { path: '/transactions/:id', perm: 'transactions.read', el: <TransactionDetail /> },
  { path: '/risk/assessments', perm: 'risk.read', el: <RiskAssessments /> },
  { path: '/admin/risk-methodologies', perm: 'risk.read', el: <MethodologiesList /> },
  { path: '/admin/risk-methodologies/:id', perm: 'risk.read', el: <MethodologyDetail /> },
  { path: '/rules', perm: 'rules.read', el: <RulesList /> },
  { path: '/rules/new', perm: 'rules.config', el: <RuleBuilder /> },
  { path: '/rules/:id', perm: 'rules.read', el: <RuleBuilder /> },
  { path: '/alerts', perm: 'alerts.read', el: <AlertsList /> },
  { path: '/alerts/:id', perm: 'alerts.read', el: <AlertDetail /> },
  { path: '/cases', perm: 'cases.read', el: <CasesList /> },
  { path: '/cases/:id', perm: 'cases.read', el: <CaseDetail /> },
  { path: '/screening', perm: 'screening.read', el: <ScreeningPage /> },
  { path: '/watchlists', perm: 'screening.read', el: <WatchlistsPage /> },
  { path: '/regulatory-reports', perm: 'str.prepare', el: <RegulatoryReportsList /> },
  { path: '/regulatory-reports/:id', perm: 'str.prepare', el: <RegulatoryReportDetail /> },
  { path: '/authority-requests', perm: 'authority.manage', el: <AuthorityRequests /> },
  { path: '/actions', perm: ['actions.read', 'holds.work'], el: <ActionsPage /> },
  { path: '/effectiveness', perm: ['effectiveness.read', 'qa.review'], el: <EffectivenessPage /> },
  { path: '/reports', perm: 'reports.read', el: <ReportsPage /> },
  { path: '/operations', perm: 'operations.read', el: <OperationsPage /> },
  { path: '/admin/fields', perm: 'config.fields', el: <FieldConfigPage /> },
  { path: '/admin/layouts', perm: 'config.layouts', el: <FormLayoutsPage /> },
  { path: '/admin/wallet-mappings', perm: 'admin.wallet-mappings', el: <WalletMappingsPage /> },
  { path: '/admin/lookups', perm: 'admin.lookups', el: <LookupsPage /> },
  { path: '/admin/users', perm: 'admin.users', el: <UsersPage /> },
  { path: '/admin/audit', perm: 'audit.read', el: <AuditPage /> },
  { path: '/admin/jurisdiction', perm: 'jurisdiction.manage', el: <JurisdictionPage /> },
  { path: '/admin/fx-rates', perm: 'admin.fx', el: <FxRatesPage /> },
  { path: '/admin/settings', perm: 'admin.settings', el: <SettingsPage /> },
];

export default function App() {
  const { i18n } = useTranslation();
  const ar = i18n.language === 'ar';
  return (
    <ConfigProvider direction={ar ? 'rtl' : 'ltr'} locale={ar ? arEG : enUS} theme={theme}>
      <AntApp>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route
            element={
              <RequireAuth>
                <AppLayout />
              </RequireAuth>
            }
          >
            <Route index element={<Navigate to="/dashboard" replace />} />
            {routes.map((r) => (
              <Route key={r.path} path={r.path} element={<Guard perm={r.perm}>{r.el}</Guard>} />
            ))}
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Route>
        </Routes>
      </AntApp>
    </ConfigProvider>
  );
}
