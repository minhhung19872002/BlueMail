import { useState } from "react";
import { Alert, App, Button, Input, Modal, Progress, Space, Typography } from "antd";
import { MailOutlined } from "@ant-design/icons";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { composeApi, emptyDraft } from "../compose/composeApi";
import { contactsApi, type Contact } from "./contactsApi";

export const MaxMerge = 100;

/** Fields of the merge: {{name}} (first word of the name, or the address), {{fullName}}, {{email}}. */
export function mergeText(template: string, c: { displayName: string; email: string }): string {
  const full = c.displayName.trim() || c.email;
  const first = c.displayName.trim().split(/\s+/)[0] || c.email;
  return template.replace(/\{\{\s*(name|fullName|email)\s*\}\}/g, (_, f: string) => (f === "name" ? first : f === "fullName" ? full : c.email));
}

const escapeHtml = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

/** Plain text with line breaks as safe HTML paragraphs (the merged values are text, never markup). */
export function textToHtml(text: string): string {
  return text.split(/\n{2,}/).map((p) => `<p>${escapeHtml(p).replace(/\n/g, "<br>")}</p>`).join("");
}

/**
 * Outlook "Mail Merge": one message per contact of a group, each with its own fields. Every message is an ordinary
 * draft sent through the usual path (limits, spam checks, signing, undo window), one after the other.
 */
export function MailMerge({ groupId, groupName }: { groupId: string; groupName: string }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [open, setOpen] = useState(false);
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("{{fullName}},\n\n");
  const [targets, setTargets] = useState<{ displayName: string; email: string }[] | null>(null);
  const [done, setDone] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);

  const load = async () => {
    setOpen(true);
    setDone(null);
    const all: Contact[] = [];
    for (let page = 1; all.length < MaxMerge + 1 && page <= 5; page++) {
      const r = await contactsApi.list("", groupId, false, page);
      all.push(...r.items);
      if (r.items.length < r.pageSize) break;
    }
    setTargets(all.filter((c) => c.emails.length > 0).map((c) => ({ displayName: c.displayName, email: c.emails[0]!.value })));
  };

  const send = async () => {
    if (!targets) return;
    setBusy(true);
    setDone(0);
    try {
      for (const c of targets.slice(0, MaxMerge)) {
        const state = { ...emptyDraft(), to: [{ name: c.displayName || null, address: c.email }], subject: mergeText(subject, c), html: textToHtml(mergeText(body, c)) };
        const draft = await composeApi.save(null, null, state);
        await composeApi.send(draft.id, { undoSeconds: 10 });
        setDone((n) => (n ?? 0) + 1);
      }
      message.success(t("mailMerge.sent", { count: Math.min(targets.length, MaxMerge) }));
      setOpen(false);
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  const preview = targets?.[0];
  return (
    <>
      <Button icon={<MailOutlined />} onClick={() => void load()}>{t("mailMerge.button")}</Button>
      {open && (
        <Modal open title={t("mailMerge.title", { group: groupName })} onCancel={() => !busy && setOpen(false)} okText={t("mailMerge.send", { count: Math.min(targets?.length ?? 0, MaxMerge) })}
          okButtonProps={{ loading: busy, disabled: !targets?.length || !subject.trim() || !body.trim() }} onOk={() => void send()} width={640} destroyOnHidden>
          <Typography.Paragraph type="secondary">
            {t("mailMerge.intro")}{" "}
            {(["name", "fullName", "email"] as const).map((f) => (
              <span key={f}><Typography.Text code>{`{{${f}}}`}</Typography.Text> {t(`mailMerge.fields.${f}`)} </span>
            ))}
          </Typography.Paragraph>
          {targets && targets.length > MaxMerge && <Alert type="warning" showIcon message={t("mailMerge.tooMany", { max: MaxMerge })} style={{ marginBlockEnd: 8 }} />}
          <Space direction="vertical" style={{ inlineSize: "100%" }}>
            <Input value={subject} onChange={(e) => setSubject(e.target.value)} placeholder={t("compose.subject")} aria-label={t("compose.subject")} maxLength={500} dir="auto" />
            <Input.TextArea value={body} onChange={(e) => setBody(e.target.value)} rows={8} aria-label={t("mailMerge.body")} dir="auto" />
            {preview && (
              <div className="bm-merge-preview">
                <Typography.Text type="secondary">{t("mailMerge.preview", { email: preview.email })}</Typography.Text>
                <Typography.Paragraph strong>{mergeText(subject, preview)}</Typography.Paragraph>
                <Typography.Paragraph className="bm-pre-wrap">{mergeText(body, preview)}</Typography.Paragraph>
              </div>
            )}
            {targets && targets.length === 0 && <Alert type="info" showIcon message={t("mailMerge.empty")} />}
            {done !== null && targets && <Progress percent={Math.round((done / Math.min(targets.length, MaxMerge)) * 100)} aria-label={t("common.progress")} />}
          </Space>
        </Modal>
      )}
    </>
  );
}
