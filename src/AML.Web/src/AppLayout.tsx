import { useEffect, useMemo, useState } from 'react';
import { Button, Grid, Layout, Menu, Space, Tag, Typography } from 'antd';
import { LogoutOutlined, GlobalOutlined, MenuFoldOutlined, MenuUnfoldOutlined, UserOutlined } from '@ant-design/icons';
import { Link, Outlet, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from './auth/AuthContext';
import { flatNav, visibleNav, type NavItem } from './nav';
import { setLanguage } from './i18n';

const { Header, Sider, Content } = Layout;

export function LangSwitch() {
  const { i18n, t } = useTranslation();
  const next = i18n.language === 'ar' ? 'en' : 'ar';
  return (
    <Button
      data-testid="lang-switch"
      icon={<GlobalOutlined />}
      onClick={() => void setLanguage(next)}
      aria-label={t('header.switchLanguage')}
      lang={next}
    >
      {next === 'ar' ? 'العربية' : 'English'}
    </Button>
  );
}

export default function AppLayout() {
  const { t } = useTranslation();
  const { me, can, logout } = useAuth();
  const nav = useMemo(() => visibleNav(can), [can]);
  const loc = useLocation();
  const screens = Grid.useBreakpoint();
  const [collapsed, setCollapsed] = useState(false);

  useEffect(() => {
    if (screens.lg === false) setCollapsed(true);
  }, [screens.lg]);

  const selected = useMemo(() => {
    const all = flatNav(nav).filter((n) => n.path);
    const match = all
      .filter((n) => loc.pathname === n.path || loc.pathname.startsWith(`${n.path}/`))
      .sort((a, b) => (b.path?.length ?? 0) - (a.path?.length ?? 0))[0];
    return match ? [match.key] : [];
  }, [nav, loc.pathname]);

  const toItems = (items: NavItem[]): any[] => // eslint-disable-line @typescript-eslint/no-explicit-any
    items.map((n) =>
      n.children
        ? { key: n.key, icon: n.icon, label: <span data-testid={`nav-${n.key}`}>{t(`nav.${n.key}`)}</span>, children: toItems(n.children) }
        : {
            key: n.key,
            icon: n.icon,
            label: (
              <Link to={n.path!} data-testid={`nav-${n.key}`}>
                {t(`nav.${n.key}`)}
              </Link>
            ),
          },
    );

  const isMobile = screens.md === false;

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        collapsible
        collapsed={collapsed}
        onCollapse={setCollapsed}
        trigger={null}
        width={260}
        collapsedWidth={isMobile ? 0 : 72}
        theme="light"
        className="app-sider"
        data-testid="sidebar"
      >
        <div className="brand">
          <Link to="/dashboard" aria-label={t('app.title')}>
            <img src="/favicon.svg" alt="" width={28} height={28} />
            {!collapsed && <span>{t('app.title')}</span>}
          </Link>
        </div>
        <nav aria-label={t('header.mainNavigation')}>
          <Menu mode="inline" items={toItems(nav)} selectedKeys={selected} defaultOpenKeys={selected.length && loc.pathname.startsWith('/admin') ? ['admin'] : []} onClick={() => isMobile && setCollapsed(true)} />
        </nav>
      </Sider>
      <Layout>
        <Header className="app-header">
          <Space>
            <Button
              type="text"
              icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
              onClick={() => setCollapsed(!collapsed)}
              aria-label={t(collapsed ? 'header.expandMenu' : 'header.collapseMenu')}
              data-testid="sidebar-toggle"
            />
            <Typography.Text strong className="header-title">
              {t('app.title')}
            </Typography.Text>
          </Space>
          <Space wrap size="small">
            <LangSwitch />
            <span className="header-user" data-testid="current-user">
              <UserOutlined aria-hidden /> {me?.username}
              {me?.roles?.slice(0, 1).map((r) => (
                <Tag key={r} style={{ marginInlineStart: 6 }}>
                  {r}
                </Tag>
              ))}
            </span>
            <Button
              data-testid="logout"
              icon={<LogoutOutlined />}
              onClick={async () => {
                await logout();
                window.location.assign('/login');
              }}
            >
              {t('header.logout')}
            </Button>
          </Space>
        </Header>
        <Content className="app-content">
          <main>
            <Outlet />
          </main>
        </Content>
      </Layout>
    </Layout>
  );
}
