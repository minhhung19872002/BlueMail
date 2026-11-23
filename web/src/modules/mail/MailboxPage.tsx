import { useParams, useSearchParams } from "react-router-dom";
import { Grid, Spin, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../../api/client";
import type { Labels } from "../../api/types";
import { useUi } from "../../store/ui";
import { systemViews } from "./labels";
import { ConversationList } from "./ConversationList";
import { ThreadView } from "./ThreadView";
import { mailKeys } from "./mailApi";
import { composeApi } from "../compose/composeApi";
import { useCompose } from "../compose/composeStore";

/**
 * A list (system view or user label) with its reading pane. The open conversation is in the URL (?t=), so
 * back/forward and shared links work; the pane sits right, below, or replaces the list (PROMPT 6.1).
 */
export function MailboxPage() {
  const { t } = useTranslation();
  const { label = "inbox", labelId } = useParams();
  const [search, setSearch] = useSearchParams();
  const pane = useUi((s) => s.readingPane);
  const screens = Grid.useBreakpoint();
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const threadId = search.get("t");
  const openCompose = useCompose((s) => s.open);

  if (!labels.data) return <Spin className="bm-list-spin" />;
  const all = labels.data.labels;
  const view = labelId ? undefined : systemViews.find((v) => v.route === label);
  const userLabel = labelId ? all.find((l) => l.id === labelId) : undefined;
  const systemLabel = view?.systemKey ? all.find((l) => l.systemKey === view.systemKey) : undefined;
  const scopeLabelId = userLabel?.id ?? systemLabel?.id ?? null;
  if (!view && !userLabel) return <Typography.Text>{t("mail.labelNotFound")}</Typography.Text>;
  if (view && view.systemKey && !systemLabel) return <Typography.Text>{t("mail.labelNotFound")}</Typography.Text>;

  const title = view ? t(view.i18nKey) : userLabel!.name;
  // Drafts open in the editor, everything else in the reading pane.
  const open = (id: string, latestMessageId: string) => {
    if (view?.systemKey === "DRAFTS") {
      void composeApi.byMessage(latestMessageId).then((d) => openCompose({ draftId: d.id }), () => setSearch((s) => { s.set("t", id); return s; }));
      return;
    }
    setSearch((s) => { s.set("t", id); return s; });
  };
  const close = () => setSearch((s) => { s.delete("t"); return s; });
  // Split panes only where there is room (≥ 1200 px); phones and the "none" setting show one at a time.
  const split = pane !== "none" && screens.xl === true;

  const list = (
    <ConversationList
      key={scopeLabelId ?? "all"}
      scope={{ labelId: view?.route === "all" ? null : scopeLabelId }}
      systemKey={view?.systemKey ?? null}
      labels={all}
      title={title}
      openThreadId={threadId}
      onOpen={open}
    />
  );
  const reader = threadId ? <ThreadView key={threadId} threadId={threadId} labels={all} systemKey={view?.systemKey ?? null} onClose={close} /> : null;

  return (
    <section aria-labelledby="mailbox-title" className={split ? `bm-split bm-split-${pane}` : undefined}>
      <Typography.Title level={4} id="mailbox-title" className={split || !threadId ? undefined : "bm-visually-hidden"}>{title}</Typography.Title>
      {split ? (
        <>
          <div className="bm-split-list">{list}</div>
          <div className="bm-split-reader">{reader ?? <Typography.Text type="secondary" className="bm-empty">{t("mail.nothingSelected")}</Typography.Text>}</div>
        </>
      ) : (
        reader ?? list
      )}
    </section>
  );
}
