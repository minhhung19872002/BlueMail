import type { DragEvent } from "react";
import { App, Tabs } from "antd";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { ConversationPage, LabelSummary } from "../../api/types";
import { dragType } from "./ConversationList";
import { act, mailKeys } from "./mailApi";
import { noActions, rulesApi, rulesKeys } from "../settings/rulesApi";
import type { Category } from "../settings/settingsApi";

/**
 * Inbox tabs (PROMPT 6.6). Dropping conversations on another tab moves them there and offers a personal rule
 * for future mail from the same senders (a filter "from:… → category").
 */
export function InboxTabs({ categories, active, labels, onChange }: { categories: Category[]; active: Category; labels: LabelSummary[]; onChange: (c: Category) => void }) {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const labelOf = (c: Category) => labels.find((l) => l.systemKey === `CATEGORY_${c}`);

  const senders = (threadIds: string[]): string[] => {
    const pages = qc.getQueriesData<ConversationPage>({ queryKey: mailKeys.lists }).map(([, d]) => d).filter((d): d is ConversationPage => !!d);
    const items = pages.flatMap((p) => p.items).filter((c) => threadIds.includes(c.threadId));
    return [...new Set(items.flatMap((c) => c.participants.slice(-1).map((p) => p.address.toLowerCase())))];
  };

  const drop = (target: Category) => ({
    onDragOver: (e: DragEvent) => {
      if (e.dataTransfer.types.includes(dragType)) e.preventDefault();
    },
    onDrop: (e: DragEvent) => {
      const raw = e.dataTransfer.getData(dragType);
      const to = labelOf(target);
      const from = labelOf(active);
      if (!raw || !to || target === active) return;
      e.preventDefault();
      const threadIds = JSON.parse(raw) as string[];
      const who = senders(threadIds);
      void act({ action: "moveTo", threadIds, labelId: to.id, fromLabelId: from?.id })
        .then(() => qc.invalidateQueries({ queryKey: mailKeys.lists }))
        .then(() => {
          if (who.length === 0) return;
          modal.confirm({
            title: t("tabs.ruleTitle", { tab: t(`filters.cat.${target}`) }),
            content: t("tabs.ruleBody", { senders: who.join(", ") }),
            okText: t("tabs.ruleYes"),
            cancelText: t("tabs.ruleNo"),
            onOk: async () => {
              await rulesApi.saveFilter({ id: null, criteria: `from:(${who.join(" OR ")})`, actions: { ...noActions, category: target }, isEnabled: true });
              await qc.invalidateQueries({ queryKey: rulesKeys.filters });
              message.success(t("tabs.ruleSaved"));
            },
          });
        });
    },
  });

  return (
    <Tabs
      className="bm-inbox-tabs"
      activeKey={active}
      onChange={(k) => onChange(k as Category)}
      items={categories.map((c) => ({ key: c, label: <span {...drop(c)}>{t(`filters.cat.${c}`)}</span> }))}
    />
  );
}
