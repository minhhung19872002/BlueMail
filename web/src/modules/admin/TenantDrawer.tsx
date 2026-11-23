import { useState } from "react";
import { App, Button, Checkbox, Descriptions, Drawer, Form, Input, InputNumber, Modal, Select, Space, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { bytes } from "../../format";
import { adminApi, type TenantDetail } from "./adminApi";

type TenantForm = Omit<TenantDetail, "id" | "code" | "status" | "usage">;

/** A customer organisation for the platform administrator (PROMPT 7.6): limits, usage, suspension mode, support sign-in. */
export function TenantDrawer({ id, canEdit, onClose }: { id: string; canEdit: boolean; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const tenant = useQuery({ queryKey: ["admin-tenant", id], queryFn: () => adminApi.tenant(id) });
  const [support, setSupport] = useState(false);
  const [supportUser, setSupportUser] = useState<string | undefined>();
  const [reason, setReason] = useState("");
  const [link, setLink] = useState<string | null>(null);
  const users = useQuery({
    queryKey: ["admin-tenant-users", id], enabled: support,
    queryFn: () => api.get<{ id: string; email: string; displayName: string }[]>(`/api/v1/admin/tenants/${id}/users`),
  });
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? (e.fieldErrors[0]?.message ?? e.title) : t("errors.generic"));
  const d = tenant.data;

  return (
    <Drawer open width="min(720px, 100vw)" title={d?.name} onClose={onClose}>
      {d && (
        <Space direction="vertical" className="bm-full">
          <Descriptions size="small" column={1} bordered items={[
            { key: "code", label: t("adminTenants.code"), children: d.code },
            { key: "users", label: t("adminTenants.users"), children: `${d.usage.users}${d.userLimit ? ` / ${d.userLimit}` : ""}` },
            { key: "storage", label: t("admin.users.storage"), children: bytes(d.usage.storageBytes, i18n.language) },
            { key: "mail", label: t("admin.tenants.mail30d"), children: t("admin.tenants.mail30dValue", { received: d.usage.received30d, sent: d.usage.sent30d }) },
            { key: "domains", label: t("adminTenants.domains"), children: d.usage.domains.join(", ") },
          ]} />
          <Form<TenantForm> layout="vertical" disabled={!canEdit} initialValues={d} key={JSON.stringify(d)}
            onFinish={(v) => void adminApi.updateTenant(id, { ...v, branding: d.branding }).then(() => { void message.success(t("settings.saved")); return tenant.refetch(); }, fail)}>
            <Form.Item name="name" label={t("adminTenants.name")} rules={[{ required: true }]}><Input dir="auto" /></Form.Item>
            <Form.Item name="plan" label={t("admin.tenants.plan")} rules={[{ required: true }]}><Input /></Form.Item>
            <Form.Item name="userLimit" label={t("admin.tenants.userLimit")} extra={t("admin.tenants.zeroUnlimited")}><InputNumber min={0} /></Form.Item>
            <Form.Item name="storageQuotaBytes" label={t("admin.tenants.storageGb")} extra={t("admin.tenants.zeroUnlimited")}
              getValueProps={(v: number) => ({ value: v / 1024 ** 3 })} normalize={(v: number | null) => Math.round((v ?? 0) * 1024 ** 3)}><InputNumber min={0} /></Form.Item>
            <Form.Item name="outboundDailyLimit" label={t("admin.tenants.dailyLimit")} extra={t("admin.tenants.zeroUnlimited")}><InputNumber min={0} /></Form.Item>
            <Form.Item name="defaultLocale" label={t("admin.tenants.locale")}><Select options={[{ value: "vi", label: "Tiếng Việt" }, { value: "en", label: "English" }]} /></Form.Item>
            <Form.Item name="defaultTimeZone" label={t("admin.tenants.timeZone")}>
              <Select showSearch options={Intl.supportedValuesOf("timeZone").map((z) => ({ value: z, label: z }))} />
            </Form.Item>
            <Form.Item name="suspendTemporary" valuePropName="checked" extra={t("admin.tenants.suspendModeHint")}><Checkbox>{t("admin.tenants.suspendMode")}</Checkbox></Form.Item>
            {canEdit && <Button type="primary" htmlType="submit">{t("admin.save")}</Button>}
          </Form>
          {canEdit && (
            <>
              <Typography.Title level={5}>{t("admin.tenants.support")}</Typography.Title>
              <Typography.Text type="secondary">{t("admin.tenants.supportHint")}</Typography.Text>
              <Button onClick={() => { setSupport(true); setLink(null); }}>{t("admin.tenants.supportStart")}</Button>
            </>
          )}
        </Space>
      )}
      <Modal open={support} title={t("admin.tenants.support")} onCancel={() => setSupport(false)} okText={t("admin.tenants.supportStart")}
        okButtonProps={{ disabled: !supportUser || !reason.trim() || link !== null }}
        onOk={() => void adminApi.supportSignIn(id, supportUser!, reason.trim()).then(setLink, fail)}>
        {link ? (
          <Space direction="vertical" className="bm-full">
            <Typography.Paragraph>{t("admin.tenants.supportLink")}</Typography.Paragraph>
            <Typography.Link href={link} target="_blank" rel="noopener noreferrer">{t("admin.tenants.supportOpen")}</Typography.Link>
          </Space>
        ) : (
          <Space direction="vertical" className="bm-full">
            <Select showSearch className="bm-full" placeholder={t("admin.tenants.supportUser")} aria-label={t("admin.tenants.supportUser")} value={supportUser} onChange={setSupportUser}
              optionFilterProp="label" options={(users.data ?? []).map((u) => ({ value: u.id, label: `${u.displayName} <${u.email}>` }))} />
            <Input.TextArea rows={3} placeholder={t("admin.tenants.supportReason")} aria-label={t("admin.tenants.supportReason")} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Space>
        )}
      </Modal>
    </Drawer>
  );
}
