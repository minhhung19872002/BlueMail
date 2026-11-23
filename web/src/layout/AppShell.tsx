import { useEffect, useMemo, useState, type DragEvent } from "react";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import { App, Avatar, Button, Dropdown, Grid, Input, Layout, Menu, Modal, Progress, Tooltip, type MenuProps } from "antd";
import {
  BulbOutlined, ClusterOutlined, DeleteOutlined, LayoutOutlined, LogoutOutlined, MenuOutlined, PlusOutlined, SafetyOutlined,
  TeamOutlined, UserOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../api/client";
import { Permission, type Labels } from "../api/types";
import { signOut } from "../auth/auth";
import { bytes } from "../format";
import { countFor, labelPalette, systemViews, userLabels } from "../modules/mail/labels";
import { act, createLabel, deleteLabel, mailKeys } from "../modules/mail/mailApi";
import { dragType } from "../modules/mail/ConversationList";
import { useMailboxLive } from "../modules/mail/realtime";
import { useCan, useMe } from "../session";
import { useUi, type Density, type ReadingPane, type ThemePreference } from "../store/ui";

const { Header, Sider, Content } = Layout;

// System lists that accept dropped conversations ("move to").
const dropTargets = new Set(["INBOX", "SPAM", "TRASH", "IMPORTANT", "STARRED"]);

export function AppShell() {
  const { t } = useTranslation();
  const me = useMe();
  const navigate = useNavigate();
  const location = useLocation();
  const screens = Grid.useBreakpoint();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  // Phones (< md): the label column is an overlay, closed by default; desktop keeps the stored preference.
  const narrow = screens.md === false;
  const [mobileOpen, setMobileOpen] = useState(false);
  const storedCollapsed = useUi((s) => s.sidebarCollapsed);
  const toggleStored = useUi((s) => s.toggleSidebar);
  const collapsed = narrow ? !mobileOpen : storedCollapsed;
  const toggle = narrow ? () => setMobileOpen((o) => !o) : toggleStored;
  const setTheme = useUi((s) => s.setTheme);
  const setDensity = useUi((s) => s.setDensity);
  const setReadingPane = useUi((s) => s.setReadingPane);
  const canUsers = useCan(Permission.AdminUsersView);
  const canTenants = useCan(Permission.PlatformTenantsView);
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const [newLabel, setNewLabel] = useState<string | null>(null);
  const [newColor, setNewColor] = useState<string | null>(null);
  useMailboxLive();

  const inboxUnread = labels.data ? countFor(systemViews[0]!, labels.data.labels) : 0;
  useEffect(() => {
    document.title = inboxUnread > 0 ? t("app.titleUnread", { count: inboxUnread }) : t("app.title");
  }, [inboxUnread, t]);

  const navItems: MenuProps["items"] = useMemo(() => {
    const refresh = async () => {
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      await qc.invalidateQueries({ queryKey: ["thread"] });
    };

    // Drop conversations dragged from the list: "move" onto system lists, "add label" onto user labels.
    const drop = (labelId: string, move: boolean) => ({
      onDragOver: (e: DragEvent) => {
        if (e.dataTransfer.types.includes(dragType)) e.preventDefault();
      },
      onDrop: (e: DragEvent) => {
        const raw = e.dataTransfer.getData(dragType);
        if (!raw) return;
        e.preventDefault();
        const threadIds = JSON.parse(raw) as string[];
        void act({ action: move ? "moveTo" : "addLabel", threadIds, labelId })
          .then(refresh)
          .then(() => message.success(t("mail.done.labelled", { count: threadIds.length })));
      },
    });

    const confirmDelete = (id: string, name: string) =>
      modal.confirm({
        title: t("mail.deleteLabelTitle", { name }),
        content: t("mail.deleteLabelBody"),
        okButtonProps: { danger: true },
        onOk: async () => {
          await deleteLabel(id);
          await refresh();
          if (location.pathname === `/mail/label/${id}`) navigate("/mail/inbox");
        },
      });

    const items: NonNullable<MenuProps["items"]> = systemViews.map((v) => {
      const n = labels.data ? countFor(v, labels.data.labels) : 0;
      const target = v.systemKey ? labels.data?.labels.find((l) => l.systemKey === v.systemKey) : undefined;
      return {
        key: `/mail/${v.route}`,
        label: (
          <span className="bm-nav-item" {...(target && dropTargets.has(v.systemKey ?? "") ? drop(target.id, true) : {})}>
            <span>{t(v.i18nKey)}</span>
            {n > 0 && <span className="bm-nav-count" aria-label={t("labels.unreadCount", { count: n })}>{n}</span>}
          </span>
        ),
      };
    });
    const custom = labels.data ? userLabels(labels.data.labels) : [];
    items.push({ type: "divider" });
    // Not "disabled": that would also disable the + button inside; clicks on the heading itself are ignored below.
    items.push({
      key: "labels-head",
      className: "bm-nav-heading",
      label: (
        <span className="bm-nav-item">
          <span>{t("nav.labelsHeading")}</span>
          <Button size="small" type="text" className="bm-nav-add" icon={<PlusOutlined />} aria-label={t("mail.newLabel")}
            onClick={(e) => { e.stopPropagation(); setNewLabel(""); }} />
        </span>
      ),
    });
    items.push(...custom.map((l) => ({
      key: `/mail/label/${l.id}`,
      label: (
        <span className="bm-nav-item" {...drop(l.id, false)}>
          <span className="bm-label-dot" style={l.colorBg ? { background: l.colorBg } : undefined} aria-hidden />
          <span className="bm-nav-label">{l.name}</span>
          {l.unread > 0 && <span className="bm-nav-count">{l.unread}</span>}
          <Button size="small" type="text" className="bm-nav-del" icon={<DeleteOutlined />} aria-label={t("mail.deleteLabelTitle", { name: l.name })}
            onClick={(e) => { e.stopPropagation(); confirmDelete(l.id, l.name); }} />
        </span>
      ),
    })));
    if (canUsers || canTenants) {
      items.push({ type: "divider" });
      if (canUsers) items.push({ key: "/admin/users", icon: <TeamOutlined />, label: t("nav.adminUsers") });
      if (canTenants) items.push({ key: "/admin/tenants", icon: <ClusterOutlined />, label: t("nav.adminTenants") });
    }
    return items;
  }, [labels.data, canUsers, canTenants, t, qc, message, modal, location.pathname, navigate]);

  const saveLabel = async () => {
    const name = newLabel?.trim();
    if (!name) return;
    try {
      await createLabel(name, newColor, null);
      setNewLabel(null);
      setNewColor(null);
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
    } catch {
      message.error(t("mail.labelExists"));
    }
  };

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
        key: "pane", icon: <LayoutOutlined />, label: t("pane.title"),
        children: (["none", "right", "below"] as ReadingPane[]).map((p) => ({ key: `pane:${p}`, label: t(`pane.${p}`) })),
      },
      {
        key: "density", label: t("density.title"),
        children: (["default", "comfortable", "compact"] as Density[]).map((d) => ({ key: `density:${d}`, label: t(`density.${d}`) })),
      },
      { key: "notify", label: t("mail.enableNotifications"), disabled: !("Notification" in window) || Notification.permission !== "default" },
      { type: "divider" },
      { key: "signout", icon: <LogoutOutlined />, label: t("nav.signOut") },
    ],
    onClick: ({ key }) => {
      if (key === "signout") void signOut();
      else if (key === "notify") void Notification.requestPermission();
      else if (key.startsWith("theme:")) setTheme(key.slice(6) as ThemePreference);
      else if (key.startsWith("density:")) setDensity(key.slice(8) as Density);
      else if (key.startsWith("pane:")) setReadingPane(key.slice(5) as ReadingPane);
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
                if (key === "labels-head") return;
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
        <Modal open={newLabel !== null} title={t("mail.newLabel")} okText={t("mail.create")} onOk={() => void saveLabel()}
          onCancel={() => setNewLabel(null)} okButtonProps={{ disabled: !newLabel?.trim() }}>
          <Input autoFocus maxLength={225} value={newLabel ?? ""} onChange={(e) => setNewLabel(e.target.value)} onPressEnter={() => void saveLabel()}
            aria-label={t("mail.labelName")} placeholder={t("mail.labelName")} />
          <div className="bm-palette" role="radiogroup" aria-label={t("mail.labelColor")}>
            {labelPalette.map((c, i) => (
              <button key={c} type="button" role="radio" aria-checked={newColor === c} aria-label={t("mail.colorN", { n: i + 1 })}
                className="bm-swatch" style={{ background: c }} onClick={() => setNewColor(newColor === c ? null : c)} />
            ))}
          </div>
        </Modal>
        <Content className="bm-content">
          <div className="bm-panel">
            <Outlet />
          </div>
        </Content>
      </Layout>
    </Layout>
  );
}
