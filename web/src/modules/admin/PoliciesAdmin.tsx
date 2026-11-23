import { useState } from "react";
import { App, Button, Checkbox, Form, Input, InputNumber, Modal, Select, Space, Switch, Table, Tag, Typography } from "antd";
import { DeleteOutlined, PlusOutlined, UndoOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { adminApi, type PolicyValue, type RoutingRule } from "./adminApi";

/** Policies by tenant and org unit, routing rules, inbox category rules (PROMPT 7.4). */
export function PoliciesAdmin() {
  const { t } = useTranslation();
  const [unit, setUnit] = useState<string | null>(null);
  const units = useQuery({ queryKey: ["admin-org-units"], queryFn: adminApi.orgUnits });
  return (
    <Space direction="vertical" className="bm-full">
      <Space wrap>
        <Typography.Text>{t("admin.policies.scope")}</Typography.Text>
        <Select className="bm-filter-select" value={unit ?? ""} onChange={(v) => setUnit(v || null)} aria-label={t("admin.policies.scope")}
          options={[{ value: "", label: t("admin.policies.wholeOrg") }, ...(units.data ?? []).map((u) => ({ value: u.id, label: u.name }))]} />
      </Space>
      <PolicyTable unit={unit} />
      <RoutingRules />
      <CategoryRules />
    </Space>
  );
}

function PolicyTable({ unit }: { unit: string | null }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const policies = useQuery({ queryKey: ["admin-policies", unit], queryFn: () => adminApi.policies(unit) });
  const save = (key: string, value: unknown) =>
    void adminApi.setPolicy(key, unit, value).then(
      () => { void message.success(t("settings.saved")); return qc.invalidateQueries({ queryKey: ["admin-policies"] }); },
      (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")));
  const effective = (p: PolicyValue) => (unit ? p.unitValue ?? p.tenantValue ?? p.default : p.tenantValue ?? p.default);
  const own = (p: PolicyValue) => (unit ? p.unitValue : p.tenantValue);

  return (
    <Table<PolicyValue> rowKey="key" size="small" pagination={false} loading={policies.isPending} dataSource={policies.data ?? []} scroll={{ x: true }}
      columns={[
        { title: t("admin.policies.policy"), render: (_, p) => <Space direction="vertical" size={0}><strong>{t(`admin.policies.k.${p.key}`)}</strong>
          <Typography.Text type="secondary">{t(`admin.policies.h.${p.key}`)}</Typography.Text></Space> },
        { title: t("admin.policies.value"), render: (_, p) => <PolicyEditor key={`${p.key}-${unit}-${JSON.stringify(effective(p))}`} p={p} value={effective(p)} onSave={(v) => save(p.key, v)} /> },
        { title: t("admin.policies.source"), render: (_, p) => <Space>
          <Tag>{own(p) !== null && own(p) !== undefined ? t(unit ? "admin.policies.fromUnit" : "admin.policies.fromOrg") : unit && p.tenantValue !== null ? t("admin.policies.fromOrg") : t("admin.policies.fromDefault")}</Tag>
          {own(p) !== null && own(p) !== undefined &&
            <Button size="small" type="text" icon={<UndoOutlined />} aria-label={t("admin.policies.reset")} title={t("admin.policies.reset")} onClick={() => save(p.key, null)} />}
        </Space> },
      ]} />
  );
}

function PolicyEditor({ p, value, onSave }: { p: PolicyValue; value: unknown; onSave: (v: unknown) => void }) {
  const { t } = useTranslation();
  const [v, setV] = useState<unknown>(value);
  const label = t(`admin.policies.k.${p.key}`);
  if (p.type === "bool") return <Switch checked={Boolean(value)} aria-label={label} onChange={onSave} />;
  const editor =
    p.type === "int" ? <InputNumber min={0} value={v as number} aria-label={label} onChange={(n) => setV(n)} />
    : p.type === "list" ? <Select mode="tags" className="bm-policy-list" value={(v as string[]) ?? []} aria-label={label} onChange={setV} tokenSeparators={[",", " "]} open={false} />
    : <Input.TextArea rows={2} value={(v as string) ?? ""} aria-label={label} onChange={(e) => setV(e.target.value)} />;
  return <Space align="start">{editor}<Button size="small" disabled={JSON.stringify(v) === JSON.stringify(value)} onClick={() => onSave(v)}>{t("admin.save")}</Button></Space>;
}

type RuleForm = Omit<RoutingRule, "sortOrder">;

function RoutingRules() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const rules = useQuery({ queryKey: ["admin-routing"], queryFn: adminApi.routingRules });
  const [editing, setEditing] = useState<RuleForm | null>(null);
  const [form] = Form.useForm<RuleForm>();
  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-routing"] });
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? (e.fieldErrors[0]?.message ?? e.title) : t("errors.generic"));
  const direction = Form.useWatch("direction", form);
  const actionType = Form.useWatch(["action", "type"], form);

  const save = async () => {
    const v = await form.validateFields();
    try {
      await adminApi.saveRoutingRule({ ...v, id: editing?.id ?? null });
      setEditing(null);
      await refresh();
    } catch (e) {
      fail(e);
    }
  };
  const describe = (r: RoutingRule) => [
    r.condition.senderContains && t("admin.routing.c.sender", { v: r.condition.senderContains }),
    r.condition.recipientContains && t("admin.routing.c.recipient", { v: r.condition.recipientContains }),
    r.condition.subjectContains && t("admin.routing.c.subject", { v: r.condition.subjectContains }),
    r.condition.hasAttachment != null && t(r.condition.hasAttachment ? "admin.routing.c.withAttachment" : "admin.routing.c.noAttachment"),
  ].filter(Boolean).join(" · ");

  return (
    <>
      <Space className="bm-row-between bm-full bm-settings-gap">
        <Typography.Title level={5}>{t("admin.routing.title")}</Typography.Title>
        <Button icon={<PlusOutlined />} onClick={() => {
          const empty: RuleForm = { id: "", name: "", direction: "INBOUND", condition: {}, action: { type: "BCC", address: "" }, isEnabled: true };
          form.setFieldsValue(empty);
          setEditing(empty);
        }}>{t("admin.routing.new")}</Button>
      </Space>
      <Table<RoutingRule> rowKey="id" size="small" pagination={false} loading={rules.isPending} dataSource={rules.data ?? []} scroll={{ x: true }}
        onRow={(r) => ({ onClick: () => { form.setFieldsValue(r); setEditing(r); }, style: { cursor: "pointer" } })}
        columns={[
          { title: t("admin.routing.name"), render: (_, r) => <span dir="auto">{r.name}</span> },
          { title: t("admin.routing.direction"), render: (_, r) => t(`admin.routing.dir.${r.direction}`) },
          { title: t("admin.routing.when"), render: (_, r) => describe(r) },
          { title: t("admin.routing.then"), render: (_, r) => t(`admin.routing.a.${r.action.type}`, { address: r.action.address ?? "" }) },
          { title: t("admin.routing.enabled"), render: (_, r) => (r.isEnabled ? <Tag color="green">{t("admin.domains.yes")}</Tag> : <Tag>{t("admin.domains.no")}</Tag>) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, r) =>
            <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.delete")}
              onClick={(e) => { e.stopPropagation(); void adminApi.deleteRoutingRule(r.id).then(refresh, fail); }} /> },
        ]} />
      <Modal open={editing !== null} title={t("admin.routing.title")} onCancel={() => setEditing(null)} onOk={() => void save()} okText={t("admin.save")} forceRender>
        <Form<RuleForm> form={form} layout="vertical">
          <Form.Item name="name" label={t("admin.routing.name")} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="direction" label={t("admin.routing.direction")}>
            <Select options={["INBOUND", "OUTBOUND"].map((d) => ({ value: d, label: t(`admin.routing.dir.${d}`) }))} />
          </Form.Item>
          <Typography.Text strong>{t("admin.routing.when")}</Typography.Text>
          <Form.Item name={["condition", "senderContains"]} label={t("admin.routing.sender")}><Input /></Form.Item>
          <Form.Item name={["condition", "recipientContains"]} label={t("admin.routing.recipient")}><Input /></Form.Item>
          <Form.Item name={["condition", "subjectContains"]} label={t("admin.routing.subject")}><Input /></Form.Item>
          <Form.Item name={["condition", "hasAttachment"]} label={t("admin.routing.attachment")}>
            <Select allowClear options={[{ value: true, label: t("admin.routing.c.withAttachment") }, { value: false, label: t("admin.routing.c.noAttachment") }]} />
          </Form.Item>
          <Form.Item name={["action", "type"]} label={t("admin.routing.then")}>
            <Select options={(direction === "OUTBOUND" ? ["BCC", "REJECT"] : ["BCC", "REDIRECT", "REJECT", "QUARANTINE"])
              .map((a) => ({ value: a, label: t(`admin.routing.a.${a}`, { address: "…" }) }))} />
          </Form.Item>
          {(actionType === "BCC" || actionType === "REDIRECT") &&
            <Form.Item name={["action", "address"]} label={t("admin.routing.address")} rules={[{ required: true, type: "email" }]}><Input /></Form.Item>}
          <Form.Item name="isEnabled" valuePropName="checked"><Checkbox>{t("admin.routing.enabled")}</Checkbox></Form.Item>
        </Form>
      </Modal>
    </>
  );
}

function CategoryRules() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const rules = useQuery({ queryKey: ["admin-category-rules"], queryFn: adminApi.categoryRules });
  const [draft, setDraft] = useState({ fromDomain: "", listId: "", category: "PROMOTIONS" });
  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-category-rules"] });
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? (e.fieldErrors[0]?.message ?? e.title) : t("errors.generic"));
  return (
    <>
      <Typography.Title level={5} className="bm-settings-gap">{t("admin.categories.title")}</Typography.Title>
      <Space.Compact className="bm-full">
        <Input placeholder={t("admin.categories.fromDomain")} aria-label={t("admin.categories.fromDomain")} value={draft.fromDomain} onChange={(e) => setDraft({ ...draft, fromDomain: e.target.value })} />
        <Input placeholder={t("admin.categories.listId")} aria-label={t("admin.categories.listId")} value={draft.listId} onChange={(e) => setDraft({ ...draft, listId: e.target.value })} />
        <Select value={draft.category} className="bm-filter-select" aria-label={t("admin.categories.category")} onChange={(category) => setDraft({ ...draft, category })}
          options={["PRIMARY", "SOCIAL", "PROMOTIONS", "UPDATES", "FORUMS"].map((c) => ({ value: c, label: t(`filters.cat.${c}`) }))} />
        <Button icon={<PlusOutlined />} disabled={!draft.fromDomain.trim() && !draft.listId.trim()}
          onClick={() => void adminApi.addCategoryRule({ fromDomain: draft.fromDomain.trim() || null, listId: draft.listId.trim() || null, category: draft.category })
            .then(() => { setDraft({ fromDomain: "", listId: "", category: draft.category }); return refresh(); }, fail)}>{t("admin.add")}</Button>
      </Space.Compact>
      <Table rowKey="id" size="small" loading={rules.isPending} dataSource={rules.data ?? []} pagination={{ pageSize: 20 }}
        columns={[
          { title: t("admin.categories.match"), render: (_, r) => r.fromDomain ?? r.listId },
          { title: t("admin.categories.category"), render: (_, r) => t(`filters.cat.${r.category}`) },
          { title: t("admin.categories.source"), render: (_, r) => <Tag>{t(r.isSystem ? "admin.categories.system" : "admin.categories.custom")}</Tag> },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, r) => !r.isSystem &&
            <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.delete")} onClick={() => void adminApi.deleteCategoryRule(r.id).then(refresh, fail)} /> },
        ]} />
    </>
  );
}
