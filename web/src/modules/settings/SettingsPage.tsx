import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { Alert, App, Button, Checkbox, Descriptions, Form, Input, List, Progress, Radio, Select, Space, Switch, Tabs, Typography } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import type { Labels, LabelSummary } from "../../api/types";
import { config } from "../../config";
import { bytes } from "../../format";
import { useMe } from "../../session";
import { mailKeys } from "../mail/mailApi";
import { userLabels } from "../mail/labels";
import { composeApi } from "../compose/composeApi";
import { FiltersPage } from "./FiltersPage";
import { ForwardingPage } from "./ForwardingPage";
import { SignaturesPage } from "./SignaturesPage";
import { ImportExportSettings } from "./importExport";
import { MailAppsSettings } from "./AppPasswords";
import { settingsApi, useSaveSettings, useSettings, type Category, type MailSettings, type Profile } from "./settingsApi";

/** Settings in tabs, as Gmail (PROMPT 6.11). */
export function SettingsPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "general";
  return (
    <section aria-labelledby="settings-title">
      <Typography.Title level={4} id="settings-title">{t("settings.title")}</Typography.Title>
      <Tabs activeKey={tab} onChange={(k) => setParams({ tab: k })} items={[
        { key: "general", label: t("settings.tabs.general"), children: <General /> },
        { key: "labels", label: t("settings.tabs.labels"), children: <LabelSettings /> },
        { key: "inbox", label: t("settings.tabs.inbox"), children: <InboxSettings /> },
        { key: "accounts", label: t("settings.tabs.accounts"), children: <Accounts /> },
        { key: "filters", label: t("settings.tabs.filters"), children: <FiltersPage /> },
        { key: "forwarding", label: t("settings.tabs.forwarding"), children: <ForwardingPage /> },
        { key: "templates", label: t("settings.tabs.templates"), children: <SignaturesPage /> },
        { key: "appearance", label: t("settings.tabs.appearance"), children: <Appearance /> },
        { key: "import", label: t("settings.tabs.import"), children: <ImportExportSettings /> },
      ]} />
    </section>
  );
}

function useSaver() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const save = useSaveSettings();
  return (patch: Partial<MailSettings>) => void save(patch).then(() => message.success(t("settings.saved")), () => message.error(t("errors.generic")));
}

const timeZones = (): string[] => {
  const intl = Intl as unknown as { supportedValuesOf?: (k: string) => string[] };
  return intl.supportedValuesOf ? intl.supportedValuesOf("timeZone") : ["UTC", "Asia/Ho_Chi_Minh", "Asia/Tokyo", "Asia/Shanghai", "Asia/Bangkok", "Europe/Warsaw"];
};

function General() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const me = useMe();
  const qc = useQueryClient();
  const s = useSettings();
  const save = useSaver();
  const [profile, setProfile] = useState<Profile>({ displayName: me.displayName, locale: me.locale, timeZone: me.timeZone, hourCycle: me.hourCycle, weekStart: me.weekStart });
  if (!s) return null;
  const saveProfile = async () => {
    try {
      await settingsApi.saveProfile(profile);
      await qc.invalidateQueries({ queryKey: ["me"] });
      message.success(t("settings.saved"));
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };

  return (
    <Form layout="vertical" className="bm-settings-form">
      <Form.Item label={t("settings.name")}>
        <Input aria-label={t("settings.name")} value={profile.displayName} maxLength={200} onChange={(e) => setProfile({ ...profile, displayName: e.target.value })} />
      </Form.Item>
      <Form.Item label={t("settings.language")}>
        <Select aria-label={t("settings.language")} value={profile.locale ?? "en"} onChange={(locale: string) => setProfile({ ...profile, locale })}
          options={[{ value: "vi", label: "Tiếng Việt" }, { value: "en", label: "English" }]} />
      </Form.Item>
      <Form.Item label={t("settings.timeZone")}>
        <Select aria-label={t("settings.timeZone")} showSearch value={profile.timeZone ?? "UTC"} onChange={(timeZone: string) => setProfile({ ...profile, timeZone })}
          options={timeZones().map((z) => ({ value: z, label: z }))} />
      </Form.Item>
      <Form.Item label={t("settings.clock")}>
        <Radio.Group aria-label={t("settings.clock")} value={profile.hourCycle ?? ""} onChange={(e) => setProfile({ ...profile, hourCycle: (e.target.value as "h12" | "h23" | "") || null })}>
          <Radio value="">{t("settings.clockAuto")}</Radio>
          <Radio value="h23">24h</Radio>
          <Radio value="h12">12h</Radio>
        </Radio.Group>
      </Form.Item>
      <Form.Item label={t("settings.weekStart")}>
        <Select aria-label={t("settings.weekStart")} value={profile.weekStart ?? 1} onChange={(weekStart: number) => setProfile({ ...profile, weekStart })}
          options={[1, 6, 0].map((d) => ({ value: d, label: t(`settings.weekday.${d}`) }))} />
      </Form.Item>
      <Button type="primary" onClick={() => void saveProfile()}>{t("settings.saveProfile")}</Button>

      <Typography.Title level={5} className="bm-settings-gap">{t("settings.mail")}</Typography.Title>
      <Form.Item label={t("settings.pageSize")}>
        <Select aria-label={t("settings.pageSize")} value={s.pageSize} onChange={(pageSize: 25 | 50 | 100) => save({ pageSize })} options={[25, 50, 100].map((n) => ({ value: n, label: String(n) }))} />
      </Form.Item>
      <Form.Item label={t("settings.undoSend")}>
        <Select aria-label={t("settings.undoSend")} value={s.undoSendSeconds} onChange={(undoSendSeconds: 5 | 10 | 20 | 30) => save({ undoSendSeconds })}
          options={[5, 10, 20, 30].map((n) => ({ value: n, label: t("compose.undoSeconds", { n }) }))} />
      </Form.Item>
      <Form.Item label={t("settings.defaultReply")}>
        <Radio.Group aria-label={t("settings.defaultReply")} value={s.defaultReply} onChange={(e) => save({ defaultReply: e.target.value as MailSettings["defaultReply"] })}>
          <Radio value="REPLY">{t("settings.reply")}</Radio>
          <Radio value="REPLY_ALL">{t("settings.replyAll")}</Radio>
        </Radio.Group>
      </Form.Item>
      <Form.Item label={t("settings.externalImages")}>
        <Radio.Group aria-label={t("settings.externalImages")} value={s.externalImages} onChange={(e) => save({ externalImages: e.target.value as MailSettings["externalImages"] })}>
          <Radio value="ASK">{t("settings.imagesAsk")}</Radio>
          <Radio value="ALWAYS">{t("settings.imagesAlways")}</Radio>
        </Radio.Group>
      </Form.Item>
      <Space direction="vertical">
        <Switch aria-label={t("settings.conversationView")} checked={s.conversationView} onChange={(conversationView) => save({ conversationView })} /> <span>{t("settings.conversationView")}</span>
        <Checkbox checked={s.keyboardShortcuts} onChange={(e) => save({ keyboardShortcuts: e.target.checked })}>{t("settings.shortcuts")}</Checkbox>
        <Checkbox checked={s.sendAndArchive} onChange={(e) => save({ sendAndArchive: e.target.checked })}>{t("settings.sendAndArchive")}</Checkbox>
        <Checkbox checked={s.desktopNotifications} onChange={(e) => {
          const on = e.target.checked;
          if (on && "Notification" in window && Notification.permission === "default") void Notification.requestPermission();
          save({ desktopNotifications: on });
        }}>{t("settings.notifications")}</Checkbox>
      </Space>
      <Form.Item label={t("settings.autoAdvance")} className="bm-settings-gap">
        <Select aria-label={t("settings.autoAdvance")} value={s.autoAdvance} onChange={(autoAdvance: MailSettings["autoAdvance"]) => save({ autoAdvance })}
          options={["LIST", "NEWER", "OLDER"].map((v) => ({ value: v, label: t(`settings.advance.${v}`) }))} />
      </Form.Item>
      <Space wrap>
        <Link to="/settings?tab=templates">{t("nav.signatures")}</Link>
        <Link to="/settings/vacation">{t("nav.vacation")}</Link>
      </Space>
    </Form>
  );
}

function LabelSettings() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const update = (l: LabelSummary, patch: { showInList?: string; showInMessageList?: boolean }) =>
    void api.put(`/api/v1/mail/labels/${l.id}`, { name: l.name, colorBg: l.colorBg, colorText: l.colorText, ...patch })
      .then(() => qc.invalidateQueries({ queryKey: mailKeys.labels }));
  return (
    <List className="bm-settings-list" dataSource={labels.data ? userLabels(labels.data.labels).concat(labels.data.labels.filter((l) => l.systemKey === null && l.showInList === "HIDE")) : []}
      renderItem={(l) => (
        <List.Item>
          <span className="bm-label-name">{l.name}</span>
          <Space wrap>
            <Select value={l.showInList} onChange={(v: string) => update(l, { showInList: v })} aria-label={t("settings.showInList", { name: l.name })}
              options={["SHOW", "IF_UNREAD", "HIDE"].map((v) => ({ value: v, label: t(`settings.visibility.${v}`) }))} />
            <Checkbox checked={l.showInMessageList} onChange={(e) => update(l, { showInMessageList: e.target.checked })}>{t("settings.showInMessages")}</Checkbox>
          </Space>
        </List.Item>
      )} />
  );
}

function InboxSettings() {
  const { t } = useTranslation();
  const s = useSettings();
  const save = useSaver();
  if (!s) return null;
  const all: Category[] = ["PRIMARY", "PROMOTIONS", "SOCIAL", "UPDATES", "FORUMS"];
  return (
    <Space direction="vertical" className="bm-settings-form">
      <Typography.Text>{t("settings.categoriesHint")}</Typography.Text>
      {all.map((c) => (
        <Checkbox key={c} checked={s.inboxCategories.includes(c)} disabled={c === "PRIMARY"}
          onChange={(e) => save({ inboxCategories: e.target.checked ? [...s.inboxCategories, c] : s.inboxCategories.filter((x) => x !== c) })}>
          {t(`filters.cat.${c}`)}
        </Checkbox>
      ))}
      <Typography.Text type="secondary">{t("settings.categoriesRules")}</Typography.Text>
    </Space>
  );
}

function Accounts() {
  const { t } = useTranslation();
  const me = useMe();
  const from = useQuery({ queryKey: ["from-options"], queryFn: composeApi.fromOptions });
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const quota = labels.data?.quotaBytes ?? 0;
  const used = labels.data?.usedBytes ?? 0;
  const changePassword = `${config().identityUrl}/account/change-password?returnUrl=${encodeURIComponent(window.location.href)}`;
  return (
    <Space direction="vertical" className="bm-settings-form">
      <Descriptions column={1} size="small" items={[{ key: "e", label: t("settings.address"), children: me.email }]} />
      <Typography.Title level={5}>{t("settings.sendAs")}</Typography.Title>
      <List size="small" dataSource={from.data ?? []} renderItem={(f) => <List.Item>{f.name ? `${f.name} <${f.address}>` : f.address}</List.Item>} />
      <Typography.Title level={5}>{t("settings.password")}</Typography.Title>
      <a href={changePassword}>{t("settings.changePassword")}</a>
      <Typography.Title level={5}>{t("settings.storage")}</Typography.Title>
      {quota > 0 && <Progress percent={Math.min(100, Math.round((used / quota) * 100))} aria-label={t("storage.tooltip")} />}
      <Typography.Text>{t("storage.used", { used: bytes(used, me.locale), quota: bytes(quota, me.locale) })}</Typography.Text>
      <MailAppsSettings />
    </Space>
  );
}

function Appearance() {
  const { t } = useTranslation();
  const s = useSettings();
  const save = useSaver();
  if (!s) return null;
  return (
    <Form layout="vertical" className="bm-settings-form">
      <Form.Item label={t("theme.title")}>
        <Radio.Group aria-label={t("theme.title")} value={s.theme} onChange={(e) => save({ theme: e.target.value as MailSettings["theme"] })}>
          {(["SYSTEM", "LIGHT", "DARK"] as const).map((v) => <Radio key={v} value={v}>{t(`theme.${v.toLowerCase()}`)}</Radio>)}
        </Radio.Group>
      </Form.Item>
      <Form.Item label={t("density.title")}>
        <Radio.Group aria-label={t("density.title")} value={s.density} onChange={(e) => save({ density: e.target.value as MailSettings["density"] })}>
          {(["DEFAULT", "COMFORTABLE", "COMPACT"] as const).map((v) => <Radio key={v} value={v}>{t(`density.${v.toLowerCase()}`)}</Radio>)}
        </Radio.Group>
      </Form.Item>
      <Form.Item label={t("pane.title")}>
        <Radio.Group aria-label={t("pane.title")} value={s.readingPane} onChange={(e) => save({ readingPane: e.target.value as MailSettings["readingPane"] })}>
          {(["NONE", "RIGHT", "BELOW"] as const).map((v) => <Radio key={v} value={v}>{t(`pane.${v.toLowerCase()}`)}</Radio>)}
        </Radio.Group>
      </Form.Item>
      <Alert type="info" showIcon message={t("settings.appearanceHint")} />
    </Form>
  );
}
