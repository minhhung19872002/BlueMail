import { useState } from "react";
import { Alert, App, Button, Descriptions, Input, Modal, Space, Table, Typography } from "antd";
import { DeleteOutlined, PlusOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { dateTime } from "../../format";
import { useMe, useUserFormat } from "../../session";

interface AppPassword { id: string; name: string; createdAt: string; lastUsedAt: string | null }
interface NewAppPassword { id: string; password: string; imapHost: string; imapPort: number; smtpHost: string; smtpPort: number }

/** Mail programs (PROMPT 8.1): app passwords for IMAP/SMTP and the server settings to type in. */
export function MailAppsSettings() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const me = useMe();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["app-passwords"], queryFn: () => api.get<AppPassword[]>("/api/v1/me/app-passwords") });
  const [name, setName] = useState("");
  const [created, setCreated] = useState<NewAppPassword | null>(null);
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const create = () => void api.post<NewAppPassword>("/api/v1/me/app-passwords", { name: name.trim() })
    .then((r) => { setCreated(r); setName(""); return qc.invalidateQueries({ queryKey: ["app-passwords"] }); }, fail);

  return (
    <Space direction="vertical" className="bm-full">
      <Typography.Title level={5}>{t("mailApps.title")}</Typography.Title>
      <Typography.Paragraph type="secondary">{me.twoFactorEnabled ? t("mailApps.introTwoFactor") : t("mailApps.intro")}</Typography.Paragraph>
      <Space.Compact className="bm-full">
        <Input value={name} onChange={(e) => setName(e.target.value)} placeholder={t("mailApps.namePlaceholder")} aria-label={t("mailApps.name")} onPressEnter={create} />
        <Button icon={<PlusOutlined />} disabled={!name.trim()} onClick={create}>{t("mailApps.create")}</Button>
      </Space.Compact>
      <Table<AppPassword> rowKey="id" size="small" pagination={false} loading={list.isPending} dataSource={list.data ?? []} locale={{ emptyText: t("mailApps.none") }}
        columns={[
          { title: t("mailApps.name"), dataIndex: "name" },
          { title: t("mailApps.created"), render: (_, a) => dateTime(a.createdAt, fmt) },
          { title: t("mailApps.lastUsed"), render: (_, a) => (a.lastUsedAt ? dateTime(a.lastUsedAt, fmt) : t("adminUsers.never")) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, a) =>
            <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t("mailApps.revoke", { name: a.name })}
              onClick={() => modal.confirm({ title: t("mailApps.revokeConfirm", { name: a.name }), okButtonProps: { danger: true },
                onOk: () => api.delete<boolean>(`/api/v1/me/app-passwords/${a.id}`).then(() => qc.invalidateQueries({ queryKey: ["app-passwords"] }), fail) })} /> },
        ]} />
      <Modal open={created !== null} title={t("mailApps.createdTitle")} onCancel={() => setCreated(null)} footer={<Button type="primary" onClick={() => setCreated(null)}>{t("common.close")}</Button>}>
        {created && (
          <Space direction="vertical" className="bm-full">
            <Alert type="warning" showIcon message={t("mailApps.once")} />
            <Typography.Text code copyable className="bm-app-password">{created.password}</Typography.Text>
            <Descriptions size="small" column={1} bordered items={[
              { key: "u", label: t("mailApps.user"), children: me.email },
              { key: "i", label: "IMAP", children: `${created.imapHost} : ${created.imapPort} (SSL/TLS)` },
              { key: "s", label: "SMTP", children: `${created.smtpHost} : ${created.smtpPort} (SSL/TLS)` },
            ]} />
          </Space>
        )}
      </Modal>
    </Space>
  );
}
