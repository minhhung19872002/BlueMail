import { useParams } from "react-router-dom";
import { Empty, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../../api/client";
import type { Labels } from "../../api/types";
import { systemViews } from "./labels";

/**
 * Label view. Phase 1 shows the label and its real message count from the mail store; the conversation
 * list arrives with message delivery in later phases.
 */
export function MailboxPage() {
  const { t } = useTranslation();
  const { label = "inbox" } = useParams();
  const labels = useQuery({ queryKey: ["labels"], queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const view = systemViews.find((v) => v.route === label);
  const title = view ? t(view.i18nKey) : labels.data?.labels.find((l) => l.id === label)?.name ?? "";
  const summary = view?.systemKey ? labels.data?.labels.find((l) => l.systemKey === view.systemKey) : undefined;
  const total = summary?.total ?? 0;

  return (
    <section aria-labelledby="mailbox-title">
      <Typography.Title level={4} id="mailbox-title">{title}</Typography.Title>
      {labels.isSuccess && total === 0 && (
        <Empty className="bm-empty" description={t("mail.emptyLabel", { label: title })} />
      )}
      {labels.isSuccess && total > 0 && (
        <Typography.Text>{t("mail.labelCount", { count: total })}</Typography.Text>
      )}
    </section>
  );
}
