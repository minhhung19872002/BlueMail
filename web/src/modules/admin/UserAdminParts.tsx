import { useState } from "react";
import { Alert, App, Button, Checkbox, Descriptions, Drawer, Form, Input, InputNumber, Modal, Select, Space, Switch, Table, Tag, Typography, Upload } from "antd";
import { DeleteOutlined, UploadOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import type { Paged } from "../../api/types";
import { bytes, dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { AdminPermission, adminApi, type ImportResult, type ImportRow } from "./adminApi";

const GB = 1024 * 1024 * 1024;

/** One user (PROMPT 7.1): profile, quota, aliases, address change, password reset, sign-ins, delete with transfer. */
export function UserDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.UsersEdit);
  const user = useQuery({ queryKey: ["admin-user", id], queryFn: () => adminApi.user(id) });
  const units = useQuery({ queryKey: ["admin-org-units"], queryFn: adminApi.orgUnits });
  const others = useQuery({ queryKey: ["admin-users-all"], queryFn: () => api.get<Paged<{ id: string; email: string }>>("/api/v1/admin/users?page=1&pageSize=200") });
  const [alias, setAlias] = useState("");
  const [address, setAddress] = useState("");
  const [transferTo, setTransferTo] = useState<string | undefined>();
  const [deleting, setDeleting] = useState(false);
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? (e.fieldErrors[0]?.message ?? e.title) : t("errors.generic"));
  const refresh = () => Promise.all([user.refetch(), qc.invalidateQueries({ queryKey: ["admin-users"] })]);
  const u = user.data;

  const resetPassword = () => modal.confirm({
    title: t("admin.users.resetConfirm", { email: u?.email }),
    onOk: async () => {
      try {
        const temp = await adminApi.resetPassword(id);
        modal.info({ title: t("admin.users.tempPassword"), content: <Space direction="vertical"><code className="bm-query">{temp}</code>
          <Typography.Text type="secondary">{t("admin.users.tempPasswordHint")}</Typography.Text></Space> });
      } catch (e) {
        fail(e);
      }
    },
  });

  return (
    <Drawer open width="min(760px, 100vw)" title={u?.displayName} onClose={onClose}>
      {u && (
        <Space direction="vertical" className="bm-full">
          <Descriptions size="small" column={1} bordered items={[
            { key: "email", label: t("adminUsers.email"), children: u.email },
            { key: "status", label: t("adminUsers.status"), children: <Tag>{t(`userStatus.${u.status}`)}</Tag> },
            { key: "2fa", label: t("adminUsers.twoFactor"), children: t(u.twoFactorEnabled ? "admin.domains.yes" : "admin.domains.no") },
            { key: "usage", label: t("admin.users.storage"), children: `${bytes(u.usedBytes, i18n.language)} / ${bytes(u.quotaBytes, i18n.language)}` },
            { key: "last", label: t("adminUsers.lastLogin"), children: u.lastLoginAt ? dateTime(u.lastLoginAt, fmt) : t("adminUsers.never") },
          ]} />
          <Form layout="vertical" disabled={!canEdit} key={u.id + u.quotaBytes}
            initialValues={{ displayName: u.displayName, isTenantAdmin: u.isTenantAdmin, quotaGb: u.quotaBytes / GB, orgUnitId: u.orgUnitId, imapEnabled: u.imapEnabled }}
            onFinish={(v: { displayName: string; isTenantAdmin: boolean; quotaGb: number; orgUnitId: string | null; imapEnabled: boolean }) =>
              void adminApi.updateUser(id, { ...v, quotaBytes: Math.round(v.quotaGb * GB) }).then(() => { void message.success(t("settings.saved")); return refresh(); }, fail)}>
            <Form.Item name="displayName" label={t("adminUsers.name")} rules={[{ required: true }]}><Input dir="auto" /></Form.Item>
            <Form.Item name="quotaGb" label={t("admin.users.quotaGb")}><InputNumber min={0.1} step={1} /></Form.Item>
            <Form.Item name="orgUnitId" label={t("admin.users.orgUnit")}>
              <Select allowClear options={(units.data ?? []).map((o) => ({ value: o.id, label: o.name }))} />
            </Form.Item>
            <Form.Item name="isTenantAdmin" valuePropName="checked"><Checkbox>{t("adminUsers.makeAdmin")}</Checkbox></Form.Item>
            <Form.Item name="imapEnabled" valuePropName="checked" label={t("admin.users.imap")}><Switch /></Form.Item>
            {canEdit && <Button type="primary" htmlType="submit">{t("admin.save")}</Button>}
          </Form>

          <Typography.Title level={5}>{t("admin.users.aliases")}</Typography.Title>
          {u.aliases.map((a) => (
            <Space key={a}>{a}{canEdit && <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.users.removeAlias", { email: a })}
              onClick={() => void adminApi.removeAlias(id, a).then(refresh, fail)} />}</Space>
          ))}
          {canEdit && (
            <Space.Compact className="bm-full">
              <Input placeholder={t("admin.users.newAlias")} aria-label={t("admin.users.newAlias")} value={alias} onChange={(e) => setAlias(e.target.value)} />
              <Button disabled={!alias.trim()} onClick={() => void adminApi.addAlias(id, alias.trim()).then(() => { setAlias(""); return refresh(); }, fail)}>{t("admin.add")}</Button>
            </Space.Compact>
          )}
          {canEdit && (
            <>
              <Typography.Title level={5}>{t("admin.users.changeAddress")}</Typography.Title>
              <Typography.Text type="secondary">{t("admin.users.changeAddressHint")}</Typography.Text>
              <Space.Compact className="bm-full">
                <Input placeholder={t("admin.users.newAddress")} aria-label={t("admin.users.newAddress")} value={address} onChange={(e) => setAddress(e.target.value)} />
                <Button disabled={!address.trim()} onClick={() => void adminApi.changeAddress(id, address.trim()).then(() => { setAddress(""); return refresh(); }, fail)}>
                  {t("admin.users.change")}
                </Button>
              </Space.Compact>
              <Space wrap>
                <Button onClick={resetPassword}>{t("admin.users.resetPassword")}</Button>
                <Button danger onClick={() => setDeleting(true)}>{t("admin.users.delete")}</Button>
              </Space>
            </>
          )}

          <Typography.Title level={5}>{t("admin.users.signIns")}</Typography.Title>
          <Table rowKey={(r) => r.at + r.protocol} size="small" pagination={false} dataSource={u.recentLogins} scroll={{ x: true }}
            columns={[
              { title: t("admin.trace.at"), render: (_, l) => dateTime(l.at, fmt) },
              { title: t("admin.users.protocol"), dataIndex: "protocol" },
              { title: t("admin.users.result"), render: (_, l) => <Tag color={l.result === "SUCCESS" ? "green" : "red"}>{l.result}</Tag> },
              { title: "IP", dataIndex: "ip" },
            ]} />
        </Space>
      )}
      <Modal open={deleting} title={t("admin.users.deleteTitle", { email: u?.email })} okButtonProps={{ danger: true }} okText={t("admin.delete")} onCancel={() => setDeleting(false)}
        onOk={() => void adminApi.deleteUser(id, transferTo ?? null).then((n) => {
          void message.success(t("admin.users.deleted", { count: n }));
          setDeleting(false);
          onClose();
          return qc.invalidateQueries({ queryKey: ["admin-users"] });
        }, fail)}>
        <Typography.Paragraph>{t("admin.users.deleteHint")}</Typography.Paragraph>
        <Select allowClear showSearch className="bm-full" placeholder={t("admin.users.transferTo")} aria-label={t("admin.users.transferTo")} value={transferTo} onChange={setTransferTo}
          optionFilterProp="label" options={(others.data?.items ?? []).filter((o) => o.id !== id).map((o) => ({ value: o.id, label: o.email }))} />
      </Modal>
    </Drawer>
  );
}

/** CSV import (PROMPT 7.1): upload, see the rows as a table, fix errors in place, then create. */
export function UserImport({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [rows, setRows] = useState<ImportRow[]>([]);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const errorsFor = (i: number) => result?.errors.filter((e) => e.row === i + 1) ?? [];

  const run = async (commit: boolean) => {
    setBusy(true);
    try {
      const r = await adminApi.importUsers(rows, commit);
      setResult(r);
      if (r.created > 0) {
        void message.success(t("admin.import.created", { count: r.created }));
        await qc.invalidateQueries({ queryKey: ["admin-users"] });
      }
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  };
  const edit = (i: number, patch: Partial<ImportRow>) => {
    setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
    setResult(null);
  };
  const close = () => { setRows([]); setResult(null); onClose(); };

  return (
    <Modal open={open} width="min(1100px, 100vw)" title={t("admin.import.title")} onCancel={close} footer={result?.created ? <Button onClick={close}>{t("common.close")}</Button> : [
      <Button key="check" disabled={!rows.length} loading={busy} onClick={() => void run(false)}>{t("admin.import.check")}</Button>,
      <Button key="go" type="primary" disabled={!rows.length} loading={busy} onClick={() => void run(true)}>{t("admin.import.create", { count: rows.length })}</Button>,
    ]}>
      {result?.created ? (
        <Space direction="vertical" className="bm-full">
          <Alert type="success" showIcon message={t("admin.import.created", { count: result.created })} description={t("admin.import.passwordsHint")} />
          <Table rowKey="email" size="small" pagination={false} dataSource={result.passwords}
            columns={[{ title: t("adminUsers.email"), dataIndex: "email" }, { title: t("admin.users.tempPassword"), render: (_, p) => <code>{p.password}</code> }]} />
        </Space>
      ) : (
        <Space direction="vertical" className="bm-full">
          <Typography.Text type="secondary">{t("admin.import.hint")}</Typography.Text>
          <Upload accept=".csv,text/csv" maxCount={1} showUploadList={false}
            beforeUpload={(file) => { void adminApi.parseUsers(file).then((r) => { setRows(r); setResult(null); }, fail); return false; }}>
            <Button icon={<UploadOutlined />}>{t("admin.import.choose")}</Button>
          </Upload>
          {result && result.errors.length > 0 && <Alert type="error" showIcon message={t("admin.import.hasErrors", { count: result.errors.length })} />}
          {result && result.errors.length === 0 && <Alert type="info" showIcon message={t("admin.import.ok")} />}
          <Table rowKey={(_, i) => String(i)} size="small" dataSource={rows} pagination={{ pageSize: 20, hideOnSinglePage: true }} scroll={{ x: true }}
            rowClassName={(_, i) => (errorsFor(i).length ? "bm-row-error" : "")}
            columns={[
              { title: "#", render: (_, __, i) => i + 1 },
              { title: t("adminUsers.email"), render: (_, r, i) => <Input size="small" value={r.email} aria-label={`${t("adminUsers.email")} ${i + 1}`} onChange={(e) => edit(i, { email: e.target.value })} /> },
              { title: t("adminUsers.name"), render: (_, r, i) => <Input size="small" value={r.displayName} aria-label={`${t("adminUsers.name")} ${i + 1}`} onChange={(e) => edit(i, { displayName: e.target.value })} /> },
              { title: t("admin.users.quotaGb"), render: (_, r, i) => <InputNumber size="small" min={0} value={r.quotaGb} aria-label={`${t("admin.users.quotaGb")} ${i + 1}`} onChange={(v) => edit(i, { quotaGb: v })} /> },
              { title: t("admin.users.orgUnit"), render: (_, r, i) => <Input size="small" value={r.orgUnit ?? ""} aria-label={`${t("admin.users.orgUnit")} ${i + 1}`} onChange={(e) => edit(i, { orgUnit: e.target.value || null })} /> },
              { title: t("adminUsers.admin"), render: (_, r, i) => <Checkbox checked={r.isTenantAdmin} aria-label={`${t("adminUsers.admin")} ${i + 1}`} onChange={(e) => edit(i, { isTenantAdmin: e.target.checked })} /> },
              { title: t("admin.import.problems"), render: (_, __, i) => errorsFor(i).map((e) => <Tag color="red" key={e.field + e.code}>{t(`admin.import.err.${e.code}`, { defaultValue: e.code })}</Tag>) },
              { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, __, i) =>
                <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.import.removeRow", { row: i + 1 })} onClick={() => { setRows(rows.filter((_, j) => j !== i)); setResult(null); }} /> },
            ]} />
        </Space>
      )}
    </Modal>
  );
}
