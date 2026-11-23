import { useState } from "react";
import { App, Button, Checkbox, Form, Input, InputNumber, Modal, Radio, Segmented, Select, Space, Table, Tag, Typography, Upload } from "antd";
import { DeleteOutlined, PlusOutlined, UploadOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../../api/client";
import type { Paged } from "../../api/types";
import { dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { errorText, ExportsTable, ImapImportForm, ImportJobsTable, migrationApi, type DataExport } from "../settings/importExport";
import { AdminPermission, adminApi } from "./adminApi";

// Compliance (PROMPT 7.5): legal holds, retention, eDiscovery and exports; migration (7.7): imports for users.

interface Hold { id: string; name: string; scope: string; userEmail: string | null; orgUnitName: string | null; query: string | null; reason: string | null; matched: number; createdAt: string; releasedAt: string | null }
interface Rule { id: string; name: string; action: "DELETE_AFTER" | "KEEP_MIN"; days: number; query: string | null; orgUnitId: string | null; isEnabled: boolean; lastRunAt: string | null; lastRunDeleted: number }
interface Hit { messageId: string; mailbox: string; receivedAt: string; from: string | null; subject: string | null; hiddenFromUser: boolean; held: boolean }

const complianceApi = {
  holds: () => api.get<Hold[]>("/api/v1/admin/compliance/holds"),
  createHold: (h: { name: string; scope: string; userId?: string; orgUnitId?: string; query?: string; reason?: string }) => api.post<string>("/api/v1/admin/compliance/holds", h),
  release: (id: string) => api.post<boolean>(`/api/v1/admin/compliance/holds/${id}/release`, {}),
  rules: () => api.get<Rule[]>("/api/v1/admin/compliance/retention"),
  saveRule: (r: Omit<Rule, "id" | "lastRunAt" | "lastRunDeleted"> & { id?: string }) =>
    r.id ? api.put<string>(`/api/v1/admin/compliance/retention/${r.id}`, r) : api.post<string>("/api/v1/admin/compliance/retention", r),
  deleteRule: (id: string) => api.delete<boolean>(`/api/v1/admin/compliance/retention/${id}`),
  search: (query: string) => api.post<{ items: Hit[]; count: number; countCapped: boolean }>("/api/v1/admin/compliance/ediscovery/search", { query }),
  exportSearch: (query: string, format: string) => api.post<string>("/api/v1/admin/compliance/ediscovery/export", { query, format }),
  exports: () => api.get<DataExport[]>("/api/v1/admin/compliance/exports"),
  userExport: (userId: string) => api.post<string>(`/api/v1/admin/compliance/users/${userId}/export`, {}),
};

function useUsers() {
  return useQuery({ queryKey: ["admin-users-all"], queryFn: () => api.get<Paged<{ id: string; email: string }>>("/api/v1/admin/users?page=1&pageSize=200") });
}

export function ComplianceAdmin() {
  const { t } = useTranslation();
  const [view, setView] = useState("holds");
  return (
    <Space direction="vertical" className="bm-full">
      <Segmented value={view} onChange={(v) => setView(String(v))}
        options={["holds", "retention", "ediscovery", "exports"].map((v) => ({ value: v, label: t(`compliance.${v}`) }))} />
      {view === "holds" && <Holds />}
      {view === "retention" && <Retention />}
      {view === "ediscovery" && <EDiscovery />}
      {view === "exports" && <Exports />}
    </Space>
  );
}

function Holds() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.ComplianceEdit);
  const holds = useQuery({ queryKey: ["holds"], queryFn: complianceApi.holds });
  const users = useUsers();
  const units = useQuery({ queryKey: ["admin-org-units"], queryFn: adminApi.orgUnits });
  const [open, setOpen] = useState(false);
  const [form] = Form.useForm<{ name: string; scope: string; userId?: string; orgUnitId?: string; query?: string; reason?: string }>();
  const scope = Form.useWatch("scope", form);
  const refresh = () => qc.invalidateQueries({ queryKey: ["holds"] });
  return (
    <>
      <Typography.Paragraph type="secondary">{t("compliance.holdsIntro")}</Typography.Paragraph>
      {canEdit && <Button icon={<PlusOutlined />} onClick={() => { form.resetFields(); setOpen(true); }}>{t("compliance.newHold")}</Button>}
      <Table<Hold> rowKey="id" size="small" loading={holds.isPending} dataSource={holds.data ?? []} pagination={false} scroll={{ x: true }}
        columns={[
          { title: t("compliance.name"), render: (_, h) => <Space direction="vertical" size={0}><strong>{h.name}</strong>{h.reason && <Typography.Text type="secondary">{h.reason}</Typography.Text>}</Space> },
          { title: t("compliance.scope"), render: (_, h) => <Space direction="vertical" size={0}>{t(`compliance.scopes.${h.scope}`)}
            <Typography.Text type="secondary">{h.userEmail ?? h.orgUnitName ?? h.query}</Typography.Text></Space> },
          { title: t("compliance.matched"), render: (_, h) => (h.scope === "QUERY" ? h.matched : "—") },
          { title: t("admin.trace.at"), render: (_, h) => dateTime(h.createdAt, fmt) },
          { title: t("admin.trace.status"), render: (_, h) => (h.releasedAt ? <Tag>{t("compliance.released", { at: dateTime(h.releasedAt, fmt) })}</Tag> : <Tag color="red">{t("compliance.active")}</Tag>) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, h) => canEdit && !h.releasedAt &&
            <Button size="small" onClick={() => modal.confirm({ title: t("compliance.releaseConfirm", { name: h.name }), content: t("compliance.releaseHint"),
              onOk: () => complianceApi.release(h.id).then(refresh, (e: unknown) => void message.error(errorText(t, e))) })}>{t("compliance.release")}</Button> },
        ]} />
      <Modal open={open} title={t("compliance.newHold")} okText={t("compliance.place")} onCancel={() => setOpen(false)} forceRender
        onOk={() => void form.validateFields().then((v) => complianceApi.createHold(v)).then(() => { setOpen(false); return refresh(); }, (e: unknown) => {
          if (e instanceof Error) void message.error(errorText(t, e));
        })}>
        <Form form={form} layout="vertical" initialValues={{ scope: "USER" }}>
          <Form.Item name="name" label={t("compliance.name")} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="scope" label={t("compliance.scope")}>
            <Radio.Group options={["USER", "ORG_UNIT", "QUERY"].map((s) => ({ value: s, label: t(`compliance.scopes.${s}`) }))} />
          </Form.Item>
          {scope === "USER" && <Form.Item name="userId" label={t("admin.tenants.supportUser")} rules={[{ required: true }]}>
            <Select showSearch optionFilterProp="label" options={(users.data?.items ?? []).map((u) => ({ value: u.id, label: u.email }))} /></Form.Item>}
          {scope === "ORG_UNIT" && <Form.Item name="orgUnitId" label={t("admin.users.orgUnit")} rules={[{ required: true }]}>
            <Select options={(units.data ?? []).map((o) => ({ value: o.id, label: o.name }))} /></Form.Item>}
          {scope === "QUERY" && <Form.Item name="query" label={t("compliance.query")} extra={t("compliance.queryHoldHint")} rules={[{ required: true }]}>
            <Input placeholder="from:@doitac.test after:2025/01/01" /></Form.Item>}
          <Form.Item name="reason" label={t("compliance.reason")}><Input.TextArea rows={2} /></Form.Item>
        </Form>
      </Modal>
    </>
  );
}

function Retention() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.ComplianceEdit);
  const rules = useQuery({ queryKey: ["retention"], queryFn: complianceApi.rules });
  const units = useQuery({ queryKey: ["admin-org-units"], queryFn: adminApi.orgUnits });
  const [editing, setEditing] = useState<Partial<Rule> | null>(null);
  const [form] = Form.useForm<Rule>();
  const action = Form.useWatch("action", form);
  const refresh = () => qc.invalidateQueries({ queryKey: ["retention"] });
  const fail = (e: unknown) => void message.error(errorText(t, e));
  return (
    <>
      <Typography.Paragraph type="secondary">{t("compliance.retentionIntro")}</Typography.Paragraph>
      {canEdit && <Button icon={<PlusOutlined />} onClick={() => { const r = { action: "DELETE_AFTER" as const, days: 365, isEnabled: true }; form.resetFields(); form.setFieldsValue(r); setEditing(r); }}>
        {t("compliance.newRule")}</Button>}
      <Table<Rule> rowKey="id" size="small" loading={rules.isPending} dataSource={rules.data ?? []} pagination={false} scroll={{ x: true }}
        onRow={(r) => ({ onClick: () => { if (canEdit) { form.setFieldsValue(r); setEditing(r); } }, style: { cursor: canEdit ? "pointer" : undefined } })}
        columns={[
          { title: t("compliance.name"), dataIndex: "name" },
          { title: t("compliance.action"), render: (_, r) => t(`compliance.actions.${r.action}`, { days: r.days }) },
          { title: t("compliance.query"), render: (_, r) => (r.query ? <code className="bm-query">{r.query}</code> : t("compliance.allMail")) },
          { title: t("admin.users.orgUnit"), render: (_, r) => units.data?.find((o) => o.id === r.orgUnitId)?.name ?? t("admin.policies.wholeOrg") },
          { title: t("compliance.lastRun"), render: (_, r) => (r.lastRunAt ? t("compliance.lastRunValue", { at: dateTime(r.lastRunAt, fmt), count: r.lastRunDeleted }) : "—") },
          { title: t("admin.routing.enabled"), render: (_, r) => (r.isEnabled ? <Tag color="green">{t("admin.domains.yes")}</Tag> : <Tag>{t("admin.domains.no")}</Tag>) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, r) => canEdit &&
            <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.delete")}
              onClick={(e) => { e.stopPropagation(); void complianceApi.deleteRule(r.id).then(refresh, fail); }} /> },
        ]} />
      <Modal open={editing !== null} title={t("compliance.newRule")} okText={t("admin.save")} onCancel={() => setEditing(null)} forceRender
        onOk={() => void form.validateFields().then((v) => complianceApi.saveRule({ ...v, id: editing?.id, query: v.query || null, orgUnitId: v.orgUnitId ?? null }))
          .then(() => { setEditing(null); return refresh(); }, (e: unknown) => { if (e instanceof Error) fail(e); })}>
        <Form form={form} layout="vertical">
          <Form.Item name="name" label={t("compliance.name")} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="action" label={t("compliance.action")}>
            <Radio.Group options={["DELETE_AFTER", "KEEP_MIN"].map((a) => ({ value: a, label: t(`compliance.actionNames.${a}`) }))} />
          </Form.Item>
          <Form.Item name="days" label={t("compliance.days")} rules={[{ required: true }]}><InputNumber min={1} max={36500} /></Form.Item>
          {action === "DELETE_AFTER" && <Form.Item name="query" label={t("compliance.query")} extra={t("compliance.queryRuleHint")}><Input /></Form.Item>}
          <Form.Item name="orgUnitId" label={t("admin.users.orgUnit")}>
            <Select allowClear placeholder={t("admin.policies.wholeOrg")} options={(units.data ?? []).map((o) => ({ value: o.id, label: o.name }))} />
          </Form.Item>
          <Form.Item name="isEnabled" valuePropName="checked"><Checkbox>{t("admin.routing.enabled")}</Checkbox></Form.Item>
        </Form>
      </Modal>
    </>
  );
}

function EDiscovery() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const canEdit = useCan(AdminPermission.ComplianceEdit);
  const [query, setQuery] = useState("");
  const [format, setFormat] = useState("MBOX");
  const [result, setResult] = useState<{ items: Hit[]; count: number; countCapped: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const search = async () => {
    setBusy(true);
    try {
      setResult(await complianceApi.search(query));
    } catch (e) {
      void message.error(errorText(t, e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <Typography.Paragraph type="secondary">{t("compliance.ediscoveryIntro")}</Typography.Paragraph>
      <Input.Search value={query} onChange={(e) => setQuery(e.target.value)} onSearch={() => void search()} loading={busy} enterButton={t("admin.trace.search")}
        placeholder="from:@doitac.test subject:hợp đồng" aria-label={t("compliance.query")} />
      {result && (
        <>
          <Space wrap className="bm-settings-gap">
            <Typography.Text strong>{t(result.countCapped ? "compliance.foundCapped" : "compliance.found", { count: result.count })}</Typography.Text>
            {canEdit && result.count > 0 && (
              <>
                <Select value={format} onChange={setFormat} aria-label={t("compliance.format")} options={[{ value: "MBOX", label: "mbox" }, { value: "EML", label: ".eml" }]} />
                <Button type="primary" onClick={() => void complianceApi.exportSearch(query, format).then(() => message.success(t("compliance.exportQueued")), (e: unknown) => void message.error(errorText(t, e)))}>
                  {t("compliance.export")}</Button>
              </>
            )}
          </Space>
          <Table<Hit> rowKey="messageId" size="small" dataSource={result.items} pagination={false} scroll={{ x: true }}
            columns={[
              { title: t("compliance.mailbox"), dataIndex: "mailbox" },
              { title: t("admin.trace.at"), render: (_, h) => dateTime(h.receivedAt, fmt) },
              { title: t("admin.quarantine.from"), dataIndex: "from" },
              { title: t("admin.quarantine.subject"), render: (_, h) => <span dir="auto">{h.subject}</span> },
              { title: t("compliance.state"), render: (_, h) => <Space>{h.hiddenFromUser && <Tag>{t("compliance.hidden")}</Tag>}{h.held && <Tag color="red">{t("compliance.held")}</Tag>}</Space> },
            ]} />
        </>
      )}
    </>
  );
}

function Exports() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const users = useUsers();
  const [userId, setUserId] = useState<string | undefined>();
  return (
    <>
      <Typography.Paragraph type="secondary">{t("compliance.exportsIntro")}</Typography.Paragraph>
      {useCan(AdminPermission.ComplianceEdit) && (
        <Space.Compact className="bm-full">
          <Select showSearch className="bm-full" placeholder={t("compliance.userExport")} aria-label={t("compliance.userExport")} value={userId} onChange={setUserId}
            optionFilterProp="label" options={(users.data?.items ?? []).map((u) => ({ value: u.id, label: u.email }))} />
          <Button disabled={!userId} onClick={() => userId && void complianceApi.userExport(userId).then(() => qc.invalidateQueries({ queryKey: ["admin-exports"] }),
            (e: unknown) => void message.error(errorText(t, e)))}>{t("compliance.exportUser")}</Button>
        </Space.Compact>
      )}
      <ExportsTable queryKey={["admin-exports"]} load={complianceApi.exports} />
    </>
  );
}

/** Imports for users of the organisation: one by one or a CSV batch (PROMPT 7.7). */
export function MigrationAdmin() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const users = useUsers();
  const [target, setTarget] = useState<string | undefined>();
  const [batchErrors, setBatchErrors] = useState<{ row: number; field: string; code: string }[]>([]);
  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-imports"] });
  return (
    <Space direction="vertical" className="bm-full">
      <Typography.Paragraph type="secondary">{t("migration.intro")}</Typography.Paragraph>
      <ImapImportForm onStart={(s) => (target ? migrationApi.startFor(target, s).then(refresh) : Promise.reject(new Error(t("migration.pickUser"))))}
        extra={<Form.Item label={t("migration.target")} required>
          <Select showSearch value={target} onChange={setTarget} optionFilterProp="label" aria-label={t("migration.target")}
            options={(users.data?.items ?? []).map((u) => ({ value: u.id, label: u.email }))} /></Form.Item>} />
      <Typography.Title level={5}>{t("migration.batch")}</Typography.Title>
      <Typography.Paragraph type="secondary">{t("migration.batchHint")}</Typography.Paragraph>
      <Upload accept=".csv,text/csv" showUploadList={false} beforeUpload={(file) => {
        void migrationApi.batch(file).then((r) => {
          setBatchErrors(r.errors);
          if (r.created > 0) {
            void message.success(t("migration.batchCreated", { count: r.created }));
            void refresh();
          }
        }, (e: unknown) => void message.error(errorText(t, e)));
        return false;
      }}>
        <Button icon={<UploadOutlined />}>{t("admin.import.choose")}</Button>
      </Upload>
      {batchErrors.length > 0 && (
        <Table rowKey={(e) => `${e.row}-${e.field}`} size="small" pagination={false} dataSource={batchErrors}
          columns={[{ title: "#", dataIndex: "row" }, { title: t("migration.field"), dataIndex: "field" },
            { title: t("admin.import.problems"), render: (_, e) => <Tag color="red">{t(`importErrors.${e.code}`, { defaultValue: t(`admin.import.err.${e.code}`, { defaultValue: e.code }) })}</Tag> }]} />
      )}
      <ImportJobsTable queryKey={["admin-imports"]} load={migrationApi.all} showUser />
    </Space>
  );
}
