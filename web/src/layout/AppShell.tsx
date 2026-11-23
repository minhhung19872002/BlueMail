import { useEffect, useMemo, useState } from "react";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import { Avatar, Button, Dropdown, Grid, Layout, Menu, Progress, Tooltip, type MenuProps } from "antd";
import {
  BulbOutlined, ClusterOutlined, LogoutOutlined, MenuOutlined, SafetyOutlined, TeamOutlined, UserOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../api/client";
import { Permission, type Labels } from "../api/types";
import { signOut } from "../auth/auth";
import { bytes } from "../format";
import { countFor, systemViews, userLabels } from "../modules/mail/labels";
import { useCan, useMe } from "../session";
import { useUi, type Density, type ThemePreference } from "../store/ui";

const { Header, Sider, Content } = Layout;

export function AppShell() {
  const { t } = useTranslation();
  const me = useMe();
  const navigate = useNavigate();
  const location = useLocation();
  const screens = Grid.useBreakpoint();
  // Phones (< md): the label column is an overlay, closed by default; desktop keeps the stored preference.
  const narrow = screens.md === false;
  const [mobileOpen, setMobileOpen] = useState(false);
  const storedCollapsed = useUi((s) => s.sidebarCollapsed);
  const toggleStored = useUi((s) => s.toggleSidebar);
  const collapsed = narrow ? !mobileOpen : storedCollapsed;
  const toggle = narrow ? () => setMobileOpen((o) => !o) : toggleStored;
  const setTheme = useUi((s) => s.setTheme);
  const setDensity = useUi((s) => s.setDensity);
  const canUsers = useCan(Permission.AdminUsersView);
  const canTenants = useCan(Permission.PlatformTenantsView);
  const labels = useQuery({ queryKey: ["labels"], queryFn: () => api.get<Labels>("/api/v1/mail/labels") });

  const inboxUnread = labels.data ? countFor(systemViews[0]!, labels.data.labels) : 0;
  useEffect(() => {
    document.title = inboxUnread > 0 ? t("app.titleUnread", { count: inboxUnread }) : t("app.title");
  }, [inboxUnread, t]);

  const navItems: MenuProps["items"] = useMemo(() => {
    const items: NonNullable<MenuProps["items"]> = systemViews.map((v) => {
      const n = labels.data ? countFor(v, labels.data.labels) : 0;
      return {
        key: `/mail/${v.route}`,
        label: (
          <span className="bm-nav-item">
            <span>{t(v.i18nKey)}</span>
            {n > 0 && <span className="bm-nav-count" aria-label={t("labels.unreadCount", { count: n })}>{n}</span>}
          </span>
        ),
      };
    });
    const custom = labels.data ? userLabels(labels.data.labels) : [];
    if (custom.length > 0) {
      items.push({ type: "divider" });
      items.push(...custom.map((l) => ({ key: `/mail/label/${l.id}`, label: l.name })));
    }
    if (canUsers || canTenants) {
      items.push({ type: "divider" });
      if (canUsers) items.push({ key: "/admin/users", icon: <TeamOutlined />, label: t("nav.adminUsers") });
      if (canTenants) items.push({ key: "/admin/tenants", icon: <ClusterOutlined />, label: t("nav.adminTenants") });
    }
    return items;
  }, [labels.data, canUsers, canTenants, t]);

  const accountMenu: MenuProps = {
    items: [
      { key: "who", label: me.email, disabled: true },
      { type: "divider" },
      { key: "/settings/sessions", icon: <SafetyOutlined />, label: t("nav.sessions") },
      {
        key: "theme", icon: <BulbOutlined />, label: t("theme.title"),
        children: (["system", "light", "dark"] as ThemePreference[]).map((m) => ({ key: `theme:${m}`, label: t(`theme.${m}`) })),
      },
      {
        key: "density", label: t("density.title"),
        children: (["default", "comfortable", "compact"] as Density[]).map((d) => ({ key: `density:${d}`, label: t(`density.${d}`) })),
      },
      { type: "divider" },
      { key: "signout", icon: <LogoutOutlined />, label: t("nav.signOut") },
    ],
    onClick: ({ key }) => {
      if (key === "signout") void signOut();
      else if (key.startsWith("theme:")) setTheme(key.slice(6) as ThemePreference);
      else if (key.startsWith("density:")) setDensity(key.slice(8) as Density);
      else if (key.startsWith("/")) navigate(key);
    },
  };

  const quota = labels.data?.quotaBytes ?? 0;
  const used = labels.data?.usedBytes ?? 0;

  return (
    <Layout className="bm-shell">
      <Header className="bm-header">
        <Button type="text" icon={<MenuOutlined />} onClick={toggle} aria-label={t("nav.toggleMenu")} aria-expanded={!collapsed} />
        <span className="bm-brand">{me.tenantName}</span>
        <span className="bm-header-spacer" />
        <Dropdown menu={accountMenu} trigger={["click"]}>
          <Button type="text" aria-label={t("nav.account")}>
            <Avatar size="small" icon={<UserOutlined />} />
            {!narrow && <span>{me.displayName}</span>}
          </Button>
        </Dropdown>
      </Header>
      <Layout>
        <Sider
          className={narrow ? "bm-sider bm-sider-overlay" : "bm-sider"}
          width={256}
          collapsedWidth={narrow ? 0 : 72}
          collapsed={collapsed}
          trigger={null}
          theme="light"
        >
          <nav aria-label={t("nav.labels")}>
            <Menu
              mode="inline"
              selectedKeys={[location.pathname]}
              items={navItems}
              onClick={({ key }) => {
                navigate(key);
                if (narrow && !collapsed) toggle();
              }}
            />
          </nav>
          {!collapsed && quota > 0 && (
            <div className="bm-storage">
              <Tooltip title={t("storage.tooltip")}>
                <Progress percent={Math.min(100, Math.round((used / quota) * 100))} showInfo={false} size="small" />
              </Tooltip>
              {t("storage.used", { used: bytes(used, me.locale), quota: bytes(quota, me.locale) })}
            </div>
          )}
        </Sider>
        <Content className="bm-content">
          <div className="bm-panel">
            <Outlet />
          </div>
        </Content>
      </Layout>
    </Layout>
  );
}
