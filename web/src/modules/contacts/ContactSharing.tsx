import { useState } from "react";
import { App, Button, Empty, Input, List, Modal, Space, Typography } from "antd";
import { DeleteOutlined, ShareAltOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { contactKeys, contactsApi } from "./contactsApi";

/** Outlook "Share Contacts": colleagues of the organisation who may read (not change) my contacts. */
export function ContactSharing() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [email, setEmail] = useState("");
  const shares = useQuery({ queryKey: contactKeys.shares, queryFn: contactsApi.shares, enabled: open });

  const set = async (address: string, shared: boolean) => {
    try {
      qc.setQueryData(contactKeys.shares, await contactsApi.setShare(address, shared));
      if (shared) setEmail("");
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    }
  };

  return (
    <>
      <Button icon={<ShareAltOutlined />} onClick={() => setOpen(true)}>{t("contacts.share.button")}</Button>
      {open && (
        <Modal open title={t("contacts.share.title")} onCancel={() => setOpen(false)} footer={null}>
          <Typography.Paragraph type="secondary">{t("contacts.share.help")}</Typography.Paragraph>
          <Space.Compact style={{ inlineSize: "100%" }}>
            <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} onPressEnter={() => email.trim() && void set(email.trim(), true)}
              placeholder={t("contacts.share.email")} aria-label={t("contacts.share.email")} maxLength={320} />
            <Button type="primary" disabled={!email.trim()} onClick={() => void set(email.trim(), true)}>{t("contacts.share.add")}</Button>
          </Space.Compact>
          {(shares.data?.mine.length ?? 0) === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t("contacts.share.none")} /> : (
            <List dataSource={shares.data?.mine ?? []} renderItem={(s) => (
              <List.Item actions={[<Button key="x" type="text" danger icon={<DeleteOutlined />} aria-label={t("contacts.share.stop", { name: s.name })}
                onClick={() => void set(s.email, false)} />]}>
                <List.Item.Meta title={s.name} description={s.email} />
              </List.Item>
            )} />
          )}
        </Modal>
      )}
    </>
  );
}
