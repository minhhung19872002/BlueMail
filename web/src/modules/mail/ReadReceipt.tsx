import { useEffect, useRef, useState } from "react";
import { Alert, App, Button, Space } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { MessageView } from "../../api/types";
import { ApiError } from "../../api/client";
import { useSettings, type MailSettings } from "../settings/settingsApi";
import { composeApi } from "../compose/composeApi";
import { answerReadReceipt, mailKeys } from "./mailApi";

export type ReadReceiptPrompt = "ask" | "auto" | null;

/**
 * What to do with a read receipt request, as Outlook: nothing when none is asked, it was answered (here or in another
 * mail program), the mail is in Spam, it is our own, or the user never sends; send without asking only with ALWAYS and when
 * the receipt goes back to the sender (RFC 8098 2.1: a different address is asked about).
 */
export function readReceiptPrompt(m: Pick<MessageView, "readReceiptTo" | "readReceiptSent" | "from" | "draft">, setting: MailSettings["readReceipts"],
  myAddresses: string[], inSpam: boolean): ReadReceiptPrompt {
  const to = m.readReceiptTo?.toLowerCase();
  if (!to || m.readReceiptSent || m.draft || inSpam || setting === "NEVER") return null;
  if (myAddresses.some((a) => a.toLowerCase() === to)) return null;
  return setting === "ALWAYS" && m.from?.address.toLowerCase() === to ? "auto" : "ask";
}

/** "The sender has requested a read receipt. Send / Don't send" above an open message. */
export function ReadReceiptBanner({ m, threadId, inSpam }: { m: MessageView; threadId: string; inSpam: boolean }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const settings = useSettings();
  // The addresses I send from (same cache as the compose window): my own request is never answered.
  const mine = useQuery({ queryKey: ["fromOptions"], queryFn: composeApi.fromOptions, staleTime: 300_000, enabled: !!m.readReceiptTo });
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const autoSent = useRef(false);
  const prompt = settings && mine.data ? readReceiptPrompt(m, settings.readReceipts, mine.data.map((o) => o.address), inSpam) : null;

  const answer = async (send: boolean, automatic: boolean) => {
    setBusy(true);
    try {
      const r = await answerReadReceipt(m.id, send, automatic);
      setDone(true);
      if (r.outcome === "SENT" && !automatic) message.success(t("mail.receipt.sent"));
      void qc.invalidateQueries({ queryKey: mailKeys.thread(threadId) });
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    if (prompt !== "auto" || autoSent.current) return;
    autoSent.current = true;
    void answer(true, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prompt]);

  if (prompt !== "ask" || done) return null;
  return (
    <Alert className="bm-banner" type="info" showIcon message={t("mail.receipt.requested", { address: m.readReceiptTo })} action={
      <Space wrap>
        <Button size="small" type="primary" loading={busy} onClick={() => void answer(true, false)}>{t("mail.receipt.send")}</Button>
        <Button size="small" disabled={busy} onClick={() => void answer(false, false)}>{t("mail.receipt.decline")}</Button>
      </Space>
    } />
  );
}
