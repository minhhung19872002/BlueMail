import { useEffect } from "react";
import { Select } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { api } from "../api/client";
import { useMailbox } from "../store/mailbox";

export interface MyMailbox {
  id: string;
  email: string;
  name: string;
  kind: "own" | "shared";
  canSendAs: boolean;
  canSendOnBehalf: boolean;
}

/** Switches the mail views between the user's mailbox and the shared mailboxes they were granted (Outlook: folder pane). */
export function MailboxSwitcher({ collapsed }: { collapsed: boolean }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { mailboxId, setMailbox } = useMailbox();
  const boxes = useQuery({ queryKey: ["myMailboxes"], queryFn: () => api.get<MyMailbox[]>("/api/v1/mail/mailboxes"), staleTime: 300_000 });
  const list = boxes.data ?? [];
  // A grant that was withdrawn: back to the own mailbox.
  const withdrawn = boxes.isSuccess && mailboxId !== null && !list.some((b) => b.id === mailboxId);
  useEffect(() => {
    if (withdrawn) setMailbox(null);
  }, [withdrawn, setMailbox]);
  if (list.length < 2 || collapsed) return null;
  const own = list.find((b) => b.kind === "own");
  return (
    <div className="bm-mailbox-switch">
      <Select aria-label={t("nav.mailbox")} value={mailboxId ?? own?.id} popupMatchSelectWidth={false} style={{ inlineSize: "100%" }}
        options={list.map((b) => ({ value: b.id, label: b.kind === "own" ? b.email : `${b.name} <${b.email}>` }))}
        onChange={(id) => {
          setMailbox(id === own?.id ? null : id);
          // Everything cached belongs to the previous mailbox.
          qc.removeQueries({ predicate: (q) => ["conversations", "labels", "thread", "body"].includes(String(q.queryKey[0])) });
          navigate("/mail/inbox");
        }} />
    </div>
  );
}
