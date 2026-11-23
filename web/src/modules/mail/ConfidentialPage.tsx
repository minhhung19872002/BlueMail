import { useState } from "react";
import { useParams } from "react-router-dom";
import { Alert, App, Button, Card, Input, Result, Space, Spin, Typography } from "antd";
import { LockOutlined, PaperClipOutlined } from "@ant-design/icons";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { bytes } from "../../format";
import { confidentialApi, type ConfidentialContent } from "./confidentialApi";
import { MessageFrame } from "./ThreadView";

/**
 * The page a recipient of a confidential message opens (Phase 12): no BlueMail account, the personal link is the
 * credential; with a one-time code when the sender asked for it. Says plainly that copying cannot be prevented.
 */
export function ConfidentialPage() {
  const { token = "" } = useParams();
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const [code, setCode] = useState("");
  const [content, setContent] = useState<ConfidentialContent | null>(null);
  const meta = useQuery({ queryKey: ["confidential", token], queryFn: () => confidentialApi.meta(token), retry: false });
  const sendCode = useMutation({
    mutationFn: () => confidentialApi.sendCode(token),
    onSuccess: () => message.success(t("confidential.codeSent", { address: meta.data?.recipient ?? "" })),
    onError: (e) => message.error(e instanceof ApiError ? e.title : t("errors.generic")),
  });
  const open = useMutation({
    mutationFn: () => confidentialApi.open(token, meta.data?.requireCode ? code.trim() : null),
    onSuccess: setContent,
    onError: (e) => message.error(e instanceof ApiError ? e.title : t("errors.generic")),
  });
  const when = (iso: string) => new Date(iso).toLocaleString(i18n.language);

  if (meta.isPending) return <Spin fullscreen />;
  if (meta.isError) {
    const e = meta.error;
    return <Result status="warning" title={e instanceof ApiError ? e.title : t("errors.generic")} />;
  }

  const m = meta.data;
  return (
    <main className="bm-confidential">
      <Card>
        <Space direction="vertical" size="middle" style={{ width: "100%" }}>
          <Typography.Title level={4}><LockOutlined /> {content?.subject ?? m.subject}</Typography.Title>
          <Typography.Text type="secondary">
            {t("confidential.from", { from: content?.from ?? m.from })} · {t("confidential.expires", { when: when(content?.expiresAt ?? m.expiresAt) })}
          </Typography.Text>
          <Alert type="info" showIcon message={t("confidential.noCopyProtection")} />
          {!content && (
            <Space direction="vertical">
              {m.requireCode && (
                <>
                  <Typography.Text>{t("confidential.codeNeeded", { address: m.recipient })}</Typography.Text>
                  <Space wrap>
                    <Button onClick={() => sendCode.mutate()} loading={sendCode.isPending}>{t("confidential.sendCode")}</Button>
                    <Input
                      aria-label={t("confidential.code")}
                      placeholder={t("confidential.code")}
                      inputMode="numeric"
                      autoComplete="one-time-code"
                      maxLength={6}
                      value={code}
                      onChange={(e) => setCode(e.target.value.replace(/\D/g, ""))}
                      onPressEnter={() => code.length === 6 && open.mutate()}
                      style={{ width: 140 }}
                    />
                  </Space>
                </>
              )}
              <Button type="primary" onClick={() => open.mutate()} loading={open.isPending} disabled={m.requireCode && code.length !== 6}>
                {t("confidential.open")}
              </Button>
            </Space>
          )}
          {content && (
            <>
              {content.htmlDocument ? (
                <MessageFrame html={content.htmlDocument} title={t("mail.messageBody")} />
              ) : (
                content.text && <pre className="bm-msg-text" dir="auto">{content.text}</pre>
              )}
              {content.files.length > 0 && (
                <Space wrap aria-label={t("mail.attachments")}>
                  {content.files.map((f) => (
                    <Button key={f.url} icon={<PaperClipOutlined />} href={f.url}>{f.name} ({bytes(f.size, i18n.language)})</Button>
                  ))}
                </Space>
              )}
            </>
          )}
        </Space>
      </Card>
    </main>
  );
}
