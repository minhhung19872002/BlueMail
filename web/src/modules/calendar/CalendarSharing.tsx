import { useState } from "react";
import { App, Button, Input, List, Modal, Select, Space, Typography } from "antd";
import { ShareAltOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { calendarApi, calendarKeys } from "./calendarApi";

/** Outlook "Share Calendar": who may see (or also change) my calendar. Colleagues only. */
export function CalendarSharing() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [email, setEmail] = useState("");
  const [permission, setPermission] = useState<"VIEW" | "EDIT">("VIEW");
  const shares = useQuery({ queryKey: [...calendarKeys.all, "shares"], queryFn: calendarApi.shares, enabled: open });
  const set = async (address: string, p: "VIEW" | "EDIT" | "NONE") => {
    try {
      qc.setQueryData([...calendarKeys.all, "shares"], await calendarApi.setShare(address, p));
      if (p !== "NONE") setEmail("");
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    }
  };
  const permissions = [{ value: "VIEW", label: t("calendar.share.view") }, { value: "EDIT", label: t("calendar.share.edit") }];
  return (
    <>
      <Button icon={<ShareAltOutlined />} onClick={() => setOpen(true)}>{t("calendar.share.button")}</Button>
      {open && (
        <Modal open title={t("calendar.share.title")} onCancel={() => setOpen(false)} footer={null} destroyOnHidden>
          <Typography.Paragraph type="secondary">{t("calendar.share.intro")}</Typography.Paragraph>
          <Space.Compact style={{ inlineSize: "100%" }}>
            <Input value={email} onChange={(e) => setEmail(e.target.value)} placeholder={t("calendar.share.email")} aria-label={t("calendar.share.email")}
              onPressEnter={() => email.trim() && void set(email.trim(), permission)} />
            <Select value={permission} onChange={setPermission} options={permissions} aria-label={t("calendar.share.permission")} style={{ minInlineSize: 120 }} />
            <Button type="primary" disabled={!email.trim()} onClick={() => void set(email.trim(), permission)}>{t("calendar.share.add")}</Button>
          </Space.Compact>
          <List style={{ marginBlockStart: 12 }} dataSource={shares.data?.mine ?? []} locale={{ emptyText: t("calendar.share.none") }}
            renderItem={(s) => (
              <List.Item actions={[
                <Select key="p" size="small" value={s.permission} options={permissions} aria-label={t("calendar.share.permission")}
                  onChange={(v) => void set(s.email, v)} />,
                <Button key="x" size="small" type="link" danger onClick={() => void set(s.email, "NONE")}>{t("calendar.share.stop")}</Button>,
              ]}>
                <List.Item.Meta title={s.name} description={s.email} />
              </List.Item>
            )} />
        </Modal>
      )}
    </>
  );
}
