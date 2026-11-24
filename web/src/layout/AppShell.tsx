import { useEffect, useMemo, useState, type DragEvent } from "react";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import { Alert, App, Avatar, Button, Dropdown, Grid, Input, Layout, Menu, Modal, Progress, Tooltip, type MenuProps } from "antd";
import {
  BulbOutlined, CoffeeOutlined, ContactsOutlined, SettingOutlined, DeleteOutlined, EditOutlined, FilterOutlined, ForwardOutlined, LayoutOutlined, LogoutOutlined,
  MenuOutlined, PlusOutlined, SafetyOutlined, SignatureFilled, TeamOutlined, UndoOutlined, UserOutlined, StarOutlined, StarFilled, MailOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../api/client";
import type { Labels } from "../api/types";
import { signOut } from "../auth/auth";
import { bytes } from "../format";
import { countFor, labelPalette, systemViews, userLabels } from "../modules/mail/labels";
import { act, createLabel, deleteLabel, mailKeys } from "../modules/mail/mailApi";
import { dragType } from "../modules/mail/ConversationList";
import { useMailboxLive } from "../modules/mail/realtime";
import { SearchBox } from "../modules/mail/SearchBox";
import { ShortcutHost } from "../modules/mail/ShortcutHost";
import { rulesApi, rulesKeys, vacationActive } from "../modules/settings/rulesApi";
import { useSaveSettings, useSettings, type MailSettings } from "../modules/settings/settingsApi";
import { ComposeDock } from "../modules/compose/ComposeDock";
import { useCompose } from "../modules/compose/composeStore";
import { useMe } from "../session";
import { useAdminSections } from "../modules/admin/adminSections";
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
  const setUndoSeconds = useUi((s) => s.setUndoSeconds);
  const openCompose = useCompose((s) => s.open);
  const adminSections = useAdminSections();
  const canAdmin = adminSections.length > 0;
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const vacation = useQuery({ queryKey: rulesKeys.vacation, queryFn: rulesApi.vacation });
  const settings = useSettings();
  const saveSettings = useSaveSettings();
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

    // Favorites (Outlook on the web): pinned lists at the top of the folder pane; a star on every list toggles it.
    const favorites = settings?.favorites ?? [];
    const toggleFavorite = (key: string) => {
      const next = favorites.includes(key) ? favorites.filter((f) => f !== key) : [...favorites, key];
      void saveSettings({ favorites: next });
    };
    const favButton = (key: string, name: string) => (
      <Button size="small" type="text" className="bm-nav-fav" aria-pressed={favorites.includes(key)}
        icon={favorites.includes(key) ? <StarFilled /> : <StarOutlined />}
        aria-label={t(favorites.includes(key) ? "mail.fav.remove" : "mail.fav.add") + ": " + name}
        onClick={(e) => { e.stopPropagation(); toggleFavorite(key); }} />
    );
    const favItems: NonNullable<MenuProps["items"]> = [];
    for (const key of favorites) {
      const v = systemViews.find((x) => `/mail/${x.route}` === key);
      const l = key.startsWith("/mail/label/") ? labels.data?.labels.find((x) => `/mail/label/${x.id}` === key) : undefined;
      if (!v && !l) continue;
      const name = v ? t(v.i18nKey) : l!.name;
      const n = v ? (labels.data ? countFor(v, labels.data.labels) : 0) : l!.unread;
      favItems.push({
        key: "fav:" + key,
        label: (
          <span className="bm-nav-item">
            <span className="bm-nav-label">{name}</span>
            {n > 0 && <span className="bm-nav-count">{n}</span>}
            {favButton(key, name)}
          </span>
        ),
      });
    }

    const items: NonNullable<MenuProps["items"]> = systemViews.map((v) => {
      const n = labels.data ? countFor(v, labels.data.labels) : 0;
      const target = v.systemKey ? labels.data?.labels.find((l) => l.systemKey === v.systemKey) : undefined;
      return {
        key: `/mail/${v.route}`,
        label: (
          <span className="bm-nav-item" {...(target && dropTargets.has(v.systemKey ?? "") ? drop(target.id, true) : {})}>
            <span>{t(v.i18nKey)}</span>
            {n > 0 && <span className="bm-nav-count" aria-label={t("labels.unreadCount", { count: n })}>{n}</span>}
            {favButton(`/mail/${v.route}`, t(v.i18nKey))}
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
          {favButton(`/mail/label/${l.id}`, l.name)}
          <Button size="small" type="text" className="bm-nav-del" icon={<DeleteOutlined />} aria-label={t("mail.deleteLabelTitle", { name: l.name })}
            onClick={(e) => { e.stopPropagation(); confirmDelete(l.id, l.name); }} />
        </span>
      ),
    })));
    if (favItems.length > 0) {
      items.unshift(
        { key: "fav-head", className: "bm-nav-heading", label: <span className="bm-nav-item"><span>{t("mail.fav.heading")}</span></span> },
        ...favItems,
        { type: "divider" },
      );
    }
    if (canAdmin) {
      items.push({ type: "divider" });
      items.push({ key: `/admin/${adminSections[0]?.key ?? ""}`, icon: <TeamOutlined />, label: t("nav.admin") });
    }
    return items;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [labels.data, canAdmin, adminSections, t, qc, message, modal, location.pathname, navigate, settings?.favorites]);

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
      { key: "/settings", icon: <SettingOutlined />, label: t("nav.settings") },
      { key: "/contacts", icon: <ContactsOutlined />, label: t("nav.contacts") },
      { key: "/settings/sessions", icon: <SafetyOutlined />, label: t("nav.sessions") },
      { key: "/settings/signatures", icon: <SignatureFilled />, label: t("nav.signatures") },
      { key: "/settings/filters", icon: <FilterOutlined />, label: t("nav.filters") },
      { key: "/settings/vacation", icon: <CoffeeOutlined />, label: t("nav.vacation") },
      { key: "/settings/forwarding", icon: <ForwardOutlined />, label: t("nav.forwarding") },
      {
        key: "undo", icon: <UndoOutlined />, label: t("compose.undoSetting"),
        children: [5, 10, 20, 30].map((n) => ({ key: `undo:${n}`, label: t("compose.undoSeconds", { n }) })),
      },
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
      // Quick switches change the account settings (server), which every device follows.
      else if (key.startsWith("theme:")) { setTheme(key.slice(6) as ThemePreference); void saveSettings({ theme: key.slice(6).toUpperCase() as MailSettings["theme"] }); }
      else if (key.startsWith("density:")) { setDensity(key.slice(8) as Density); void saveSettings({ density: key.slice(8).toUpperCase() as MailSettings["density"] }); }
      else if (key.startsWith("pane:")) { setReadingPane(key.slice(5) as ReadingPane); void saveSettings({ readingPane: key.slice(5).toUpperCase() as MailSettings["readingPane"] }); }
      else if (key.startsWith("undo:")) {
        const n = Number(key.slice(5)) as 5 | 10 | 20 | 30;
        setUndoSeconds(n);
        void saveSettings({ undoSendSeconds: n });
      }
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
        <SearchBox />
        <span className="bm-header-spacer" />
        <Dropdown menu={accountMenu} trigger={["click"]}>
          <Button type="text" aria-label={narrow ? t("nav.account") : `${me.displayName} · ${t("nav.account")}`}>
            <Avatar size="small" icon={<UserOutlined />} />
            {!narrow && <span>{me.displayName}</span>}
          </Button>
        </Dropdown>
      </Header>
      <Layout>
        {!narrow && (
          // App switcher of Outlook on the web: one column of apps at the window edge.
          <nav className="bm-app-rail" aria-label={t("nav.apps")}>
            <Tooltip title={t("nav.mail")} placement="right">
              <Button type="text" icon={<MailOutlined />} aria-label={t("nav.mail")} aria-current={location.pathname.startsWith("/mail") ? "page" : undefined}
                onClick={() => navigate("/mail/inbox")} />
            </Tooltip>
            <Tooltip title={t("nav.contacts")} placement="right">
              <Button type="text" icon={<ContactsOutlined />} aria-label={t("nav.contacts")} aria-current={location.pathname.startsWith("/contacts") ? "page" : undefined}
                onClick={() => navigate("/contacts")} />
            </Tooltip>
          </nav>
        )}
        <Sider
          className={narrow ? "bm-sider bm-sider-overlay" : "bm-sider"}
          width={256}
          collapsedWidth={narrow ? 0 : 72}
          collapsed={collapsed}
          trigger={null}
          theme="light"
        >
          <div className="bm-compose-btn-wrap">
            <Button type="primary" size="large" icon={<EditOutlined />} className="bm-compose-btn" onClick={() => openCompose({})}
              aria-label={t("compose.compose")}>
              {!collapsed && t("compose.compose")}
            </Button>
          </div>
          <nav aria-label={t("nav.labels")}>
            <Menu
              mode="inline"
              selectedKeys={location.pathname.startsWith("/admin/") && adminSections[0] ? [`/admin/${adminSections[0].key}`] : [location.pathname, "fav:" + location.pathname]}
              items={navItems}
              onClick={({ key }) => {
                if (key === "labels-head" || key === "fav-head") return;
                navigate(key.startsWith("fav:") ? key.slice(4) : key);
                if (narrow && !collapsed) toggle();
              }}
            />
          </nav>
          {!collapsed && quota > 0 && (
            <div className="bm-storage">
              <Tooltip title={t("storage.tooltip")}>
                <Progress percent={Math.min(100, Math.round((used / quota) * 100))} showInfo={false} size="small" aria-label={t("storage.tooltip")} />
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
          {me.supportActor && (
            // A support sign-in is never silent (PROMPT 7.6): the whole session shows who is acting.
            <Alert className="bm-banner" type="warning" showIcon message={t("support.banner", { actor: me.supportActor })} />
          )}
          {vacationActive(vacation.data) && (
            // "While it is on there is a reminder at the top of the page" (PROMPT 6.8)
            <Alert className="bm-banner bm-vacation-banner" type="info" showIcon message={t("vacation.banner")} action={
              <Button size="small" onClick={() => void rulesApi.saveVacation({ ...vacation.data!, enabled: false })
                .then(() => qc.invalidateQueries({ queryKey: rulesKeys.vacation }))}>{t("vacation.turnOff")}</Button>
            } />
          )}
          <div className="bm-panel">
            <Outlet />
          </div>
        </Content>
        <ComposeDock />
        <ShortcutHost enabled={settings?.keyboardShortcuts ?? false} />
      </Layout>
    </Layout>
  );
}
