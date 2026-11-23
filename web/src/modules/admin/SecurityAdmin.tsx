import { useState } from "react";
import { App, Button, Checkbox, Form, Input, InputNumber, Select, Space, Switch, Table, Tag, Typography } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { AdminPermission, adminApi, type QuarantineItem, type SpamSettings } from "./adminApi";

/** Spam settings, DNS block lists and the quarantine (PROMPT 6, 7.5). */
export function SecurityAdmin() {
  const canSpam = useCan(AdminPermission.SpamEdit);
  const canQuarantine = useCan(AdminPermission.QuarantineView);
  return (
    <Space direction="vertical" className="bm-full">
      {canSpam && <SpamSettingsForm />}
      {canQuarantine && <Quarantine />}
    </Space>
  );
}

function SpamSettingsForm() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: ["admin-spam"], queryFn: adminApi.spamSettings });
  const [form] = Form.useForm<Omit<SpamSettings, "lists">>();
  const fail = (e: unknown) => {
    if (e instanceof ApiError && e.fieldErrors.length > 0) form.setFields(e.fieldErrors.map((f) => ({ name: f.field as "spamThreshold", errors: [f.message] })));
    else void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  };
  if (!settings.data) return null;
  return (
    <>
      <Typography.Title level={5}>{t("admin.spam.title")}</Typography.Title>
      <Form form={form} layout="vertical" initialValues={settings.data} className="bm-settings-form"
        onFinish={(v) => void adminApi.saveSpamSettings(v).then(() => { void message.success(t("settings.saved")); return qc.invalidateQueries({ queryKey: ["admin-spam"] }); }, fail)}>
        <Form.Item name="spamThreshold" label={t("admin.spam.spamThreshold")} extra={t("admin.spam.spamThresholdHint")}><InputNumber min={1} max={50} step={0.5} /></Form.Item>
        <Form.Item name="rejectThreshold" label={t("admin.spam.rejectThreshold")} extra={t("admin.spam.rejectThresholdHint")}><InputNumber min={1} max={100} step={0.5} /></Form.Item>
        <Form.Item name="greylisting" valuePropName="checked"><Checkbox>{t("admin.spam.greylisting")}</Checkbox></Form.Item>
        <Form.Item name="suspiciousAttachments" label={t("admin.spam.suspicious")}>
          <Select options={["QUARANTINE", "SPAM", "REJECT"].map((v) => ({ value: v, label: t(`admin.spam.sus.${v}`) }))} />
        </Form.Item>
        <Form.Item name="phishingReportAddress" label={t("admin.spam.phishingAddress")}><Input type="email" /></Form.Item>
        <Button type="primary" htmlType="submit">{t("admin.save")}</Button>
      </Form>
      <Typography.Title level={5} className="bm-settings-gap">{t("admin.spam.lists")}</Typography.Title>
      <Table rowKey="id" size="small" pagination={false} dataSource={settings.data.lists} scroll={{ x: true }}
        columns={[
          { title: t("admin.spam.list"), render: (_, l) => <Space direction="vertical" size={0}><strong>{l.name}</strong><code>{l.zone}</code></Space> },
          { title: t("admin.spam.score"), dataIndex: "score" },
          { title: t("admin.spam.license"), render: (_, l) => <Typography.Text type="secondary">{l.licenseNote}</Typography.Text> },
          { title: t("admin.routing.enabled"), render: (_, l) => <Switch checked={l.enabled} aria-label={l.name}
            onChange={(on) => void adminApi.toggleList(l.id, on).then(() => qc.invalidateQueries({ queryKey: ["admin-spam"] }), fail)} /> },
        ]} />
    </>
  );
}

function Quarantine() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.QuarantineEdit);
  const [status, setStatus] = useState("HELD");
  const [page, setPage] = useState(1);
  const items = useQuery({ queryKey: ["admin-quarantine", status, page], queryFn: () => adminApi.quarantine(status, page) });
  const act = (p: Promise<unknown>) => void p.then(() => qc.invalidateQueries({ queryKey: ["admin-quarantine"] }),
    (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")));
  return (
    <>
      <Space className="bm-row-between bm-full bm-settings-gap">
        <Typography.Title level={5}>{t("admin.quarantine.title")}</Typography.Title>
        <Select value={status} onChange={(s) => { setStatus(s); setPage(1); }} className="bm-filter-select" aria-label={t("admin.quarantine.status")}
          options={["HELD", "RELEASED", "DELETED"].map((s) => ({ value: s, label: t(`admin.quarantine.st.${s}`) }))} />
      </Space>
      <Table<QuarantineItem> rowKey="id" size="small" loading={items.isPending} dataSource={items.data?.items ?? []} scroll={{ x: true }}
        pagination={{ current: page, pageSize: 50, total: items.data?.total ?? 0, onChange: setPage,
          showTotal: (total, range) => t("common.rangeOfTotal", { from: range[0], to: range[1], total }) }}
        columns={[
          { title: t("admin.quarantine.at"), render: (_, q) => dateTime(q.createdAt, fmt) },
          { title: t("admin.quarantine.from"), render: (_, q) => q.fromAddress ?? q.envelopeFrom },
          { title: t("admin.quarantine.subject"), render: (_, q) => <span dir="auto">{q.subject}</span> },
          { title: t("admin.quarantine.reason"), render: (_, q) => <Tag>{q.reason}</Tag> },
          { title: t("admin.spam.score"), render: (_, q) => q.spamScore?.toFixed(1) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, q) => canEdit && q.status === "HELD" && (
            <Space>
              <Button size="small" onClick={() => act(adminApi.release(q.id))}>{t("admin.quarantine.release")}</Button>
              <Button size="small" danger onClick={() => act(adminApi.discard(q.id))}>{t("admin.delete")}</Button>
            </Space>
          ) },
        ]} />
    </>
  );
}
