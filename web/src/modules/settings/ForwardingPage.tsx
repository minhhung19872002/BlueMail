import { useState } from "react";
import { Alert, App, Button, Checkbox, Empty, Input, List, Radio, Space, Tag, Typography } from "antd";
import { DeleteOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { rulesApi, rulesKeys } from "./rulesApi";

/**
 * Automatic forwarding (PROMPT 6.8): an address is usable only after the code BlueMail mails to it is entered;
 * one address is the active forward. Forwarded mail keeps SPF valid (SRS) and carries an ARC seal.
 */
export function ForwardingPage() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: rulesKeys.forwarding, queryFn: rulesApi.forwarding });
  const [target, setTarget] = useState("");
  const [codes, setCodes] = useState<Record<string, string>>({});
  const refresh = () => qc.invalidateQueries({ queryKey: rulesKeys.forwarding });
  const fail = (e: unknown) => message.error(e instanceof ApiError ? e.title : t("errors.generic"));

  const add = async () => {
    try {
      await rulesApi.addForwarding(target.trim());
      setTarget("");
      message.success(t("forwarding.codeSent"));
      await refresh();
    } catch (e) {
      fail(e);
    }
  };
  const verify = async (id: string) => {
    try {
      await rulesApi.verifyForwarding(id, codes[id] ?? "");
      message.success(t("forwarding.verified"));
      await refresh();
    } catch (e) {
      fail(e);
    }
  };

  const list = q.data?.addresses ?? [];
  const active = list.find((x) => x.isActive);
  const setActive = (id: string | null, keepCopy: boolean) => void rulesApi.setActive(id, keepCopy).then(refresh, fail);

  return (
    <section aria-labelledby="fw-title" className="bm-settings-form">
      <Typography.Title level={4} id="fw-title">{t("forwarding.title")}</Typography.Title>
      {q.data && !q.data.externalAllowed && <Alert className="bm-banner" type="info" showIcon message={t("forwarding.externalForbidden")} />}
      <Space.Compact className="bm-full">
        <Input type="email" value={target} maxLength={320} placeholder={t("forwarding.add")} aria-label={t("forwarding.add")}
          onChange={(e) => setTarget(e.target.value)} onPressEnter={() => void add()} />
        <Button type="primary" disabled={!target.trim()} onClick={() => void add()}>{t("forwarding.sendCode")}</Button>
      </Space.Compact>
      {q.data && list.length === 0 && <Empty description={t("forwarding.none")} />}
      <List className="bm-settings-list" dataSource={list} renderItem={(f) => (
        <List.Item actions={[
          <Button key="d" type="text" danger icon={<DeleteOutlined />} aria-label={t("forwarding.remove", { address: f.targetEmail })}
            onClick={() => modal.confirm({ title: t("forwarding.remove", { address: f.targetEmail }), okButtonProps: { danger: true },
              onOk: async () => { await rulesApi.deleteForwarding(f.id); await refresh(); } })} />,
        ]}>
          <Space wrap>
            <span>{f.targetEmail}</span>
            {f.verified ? <Tag color="green">{t("forwarding.verifiedTag")}</Tag> : (
              <Space.Compact>
                <Input size="small" inputMode="numeric" maxLength={6} className="bm-code-input" placeholder={t("forwarding.code")} aria-label={t("forwarding.code")}
                  value={codes[f.id] ?? ""} onChange={(e) => setCodes({ ...codes, [f.id]: e.target.value })} onPressEnter={() => void verify(f.id)} />
                <Button size="small" onClick={() => void verify(f.id)}>{t("forwarding.verify")}</Button>
                <Button size="small" type="link" onClick={() => void rulesApi.addForwarding(f.targetEmail).then(() => message.success(t("forwarding.codeSent")), fail)}>
                  {t("forwarding.resend")}
                </Button>
              </Space.Compact>
            )}
          </Space>
        </List.Item>
      )} />
      <Typography.Title level={5}>{t("forwarding.automatic")}</Typography.Title>
      <Radio.Group value={active?.id ?? ""} onChange={(e) => setActive((e.target.value as string) || null, active?.keepCopy ?? true)}>
        <Space direction="vertical">
          <Radio value="">{t("forwarding.off")}</Radio>
          {list.filter((x) => x.verified).map((x) => <Radio key={x.id} value={x.id}>{t("forwarding.to", { address: x.targetEmail })}</Radio>)}
        </Space>
      </Radio.Group>
      {active && (
        <Checkbox className="bm-settings-gap" checked={active.keepCopy} onChange={(e) => setActive(active.id, e.target.checked)}>{t("forwarding.keepCopy")}</Checkbox>
      )}
    </section>
  );
}
