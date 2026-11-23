import { useEffect, useMemo, useRef, useState } from "react";
import { App, Button, Checkbox, Dropdown, Empty, Space, Spin, Tag, Tooltip, Typography, type MenuProps } from "antd";
import {
  DeleteOutlined, ExclamationCircleOutlined, FolderOpenOutlined, InboxOutlined, LeftOutlined, MailOutlined,
  PaperClipOutlined, ReloadOutlined, RightOutlined, StarFilled, StarOutlined, StopOutlined, TagOutlined, UndoOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { ConversationSummary, LabelSummary } from "../../api/types";
import { ApiError } from "../../api/client";
import { dateTime, listTime } from "../../format";
import { useMe, useUserFormat } from "../../session";
import { act, getSnoozed, listConversations, mailKeys, maxSelection, scopeKey, type ListScope, type MailAction } from "./mailApi";
import { SnoozeMenu } from "./SnoozeMenu";
import { useShortcuts } from "./ShortcutHost";
import { inverse } from "./shortcuts";
import { useSettings } from "../settings/settingsApi";
import { senderParts } from "./mailView";

export const dragType = "application/x-bluemail-threads";

interface Props {
  scope: ListScope;
  systemKey: string | null;
  labels: LabelSummary[];
  title: string;
  openThreadId: string | null;
  onOpen: (threadId: string, latestMessageId: string) => void;
}

/** Conversation list (PROMPT 6.2): cursor pages, selection, bulk and hover actions, drag to a label. */
export function ConversationList({ scope, systemKey, labels, title, openThreadId, onOpen }: Props) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const me = useMe();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const cursor = cursors[cursors.length - 1] ?? null;
  const key = scopeKey(scope);

  const settings = useSettings();
  const pageSize = settings?.pageSize ?? 50;
  const threads = settings?.conversationView ?? true;
  const page = useQuery({
    queryKey: [...mailKeys.list(key, cursor), pageSize, threads],
    queryFn: () => listConversations(scope, cursor, pageSize, threads),
    placeholderData: (prev) => prev,
  });
  const items = page.data?.items ?? [];
  const labelById = useMemo(() => new Map(labels.map((l) => [l.id, l])), [labels]);
  const userLabels = labels.filter((l) => l.systemKey === null);
  const start = (cursors.length - 1) * pageSize;

  const snoozed = useQuery({ queryKey: ["snoozed"], queryFn: getSnoozed, enabled: systemKey === "SNOOZED" });
  const snoozedUntil = useMemo(() => new Map((snoozed.data ?? []).map((s) => [s.threadId, s.untilAt])), [snoozed.data]);

  const run = async (action: MailAction, threadIds: string[], labelId?: string, snoozeUntil?: Date) => {
    if (threadIds.length === 0) return;
    if (threadIds.length > maxSelection) {
      message.warning(t("mail.selectionTooLarge", { max: maxSelection }));
      return;
    }
    try {
      await act({ action, threadIds, labelId, fromLabelId: action === "moveTo" ? scope.labelId ?? undefined : undefined, snoozeUntil: snoozeUntil?.toISOString() });
      if (action === "snooze" || action === "unsnooze") await qc.invalidateQueries({ queryKey: ["snoozed"] });
      setSelected(new Set());
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      for (const id of threadIds) await qc.invalidateQueries({ queryKey: mailKeys.thread(id) });
      const done: Partial<Record<MailAction, string>> = {
        archive: "mail.done.archived", trash: "mail.done.trashed", spam: "mail.done.spam", deleteForever: "mail.done.deleted",
        moveTo: "mail.done.moved", untrash: "mail.done.restored", notSpam: "mail.done.restored", snooze: "mail.done.snoozed", unsnooze: "mail.done.unsnoozed",
      };
      const k = done[action];
      if (k) message.success(t(k, { count: threadIds.length }));
    } catch (e) {
      message.error(e instanceof ApiError && e.title ? e.title : t("errors.actionFailed"));
    }
  };

  const sel = [...selected];
  // Keyboard (PROMPT 6.12): a focused row moves with j/k; actions go to the selection, or to the focused row.
  const [focused, setFocused] = useState(0);
  const [lastAction, setLastAction] = useState<{ action: MailAction; threadIds: string[] } | null>(null);
  const target = () => (sel.length > 0 ? sel : items[focused] ? [items[focused]!.threadId] : []);
  const act1 = (action: MailAction) => {
    const ids = target();
    setLastAction({ action, threadIds: ids });
    void run(action, ids);
  };
  useShortcuts({
    next: () => setFocused((f) => Math.max(0, Math.min(f + 1, items.length - 1))),
    prev: () => setFocused((f) => Math.max(f - 1, 0)),
    open: () => { const c = items[focused]; if (c) onOpen(c.threadId, c.latestMessageId); },
    select: () => { const c = items[focused]; if (c) setSelected((s) => { const n = new Set(s); if (n.has(c.threadId)) n.delete(c.threadId); else n.add(c.threadId); return n; }); },
    selectAll: () => setSelected(new Set(items.map((c) => c.threadId))),
    selectNone: () => setSelected(new Set()),
    archive: () => act1("archive"),
    trash: () => act1("trash"),
    spam: () => act1("spam"),
    star: () => act1(items[focused]?.starred && sel.length === 0 ? "unstar" : "star"),
    important: () => act1("important"),
    notImportant: () => act1("notImportant"),
    markRead: () => act1("markRead"),
    markUnread: () => act1("markUnread"),
    mute: () => act1("mute"),
    label: () => document.querySelector<HTMLElement>("[data-shortcut=label]")?.click(),
    move: () => document.querySelector<HTMLElement>("[data-shortcut=move]")?.click(),
    snooze: () => document.querySelector<HTMLElement>("[data-shortcut=snooze]")?.click(),
    undo: () => {
      const back = lastAction && inverse[lastAction.action];
      if (lastAction && back) {
        void run(back as MailAction, lastAction.threadIds);
        setLastAction(null);
      }
    },
  }, openThreadId === null);
  const allOnPage = items.length > 0 && items.every((c) => selected.has(c.threadId));
  const inTrash = systemKey === "TRASH";
  const inSpam = systemKey === "SPAM";

  const labelMenu = (action: MailAction): MenuProps => ({
    items: userLabels.length === 0
      ? [{ key: "none", label: t("mail.noUserLabels"), disabled: true }]
      : userLabels.map((l) => ({ key: l.id, label: l.name })),
    onClick: ({ key: id }) => void run(action, sel, id),
  });

  const toolbar = (
    <div className="bm-list-toolbar" role="toolbar" aria-label={t("mail.toolbar")}>
      <Checkbox
        checked={allOnPage}
        indeterminate={selected.size > 0 && !allOnPage}
        onChange={(e) => setSelected(e.target.checked ? new Set(items.map((c) => c.threadId)) : new Set())}
        aria-label={t("mail.selectPage")}
      />
      <Tooltip title={t("mail.refresh")}>
        <Button type="text" icon={<ReloadOutlined />} aria-label={t("mail.refresh")} onClick={() => void page.refetch()} />
      </Tooltip>
      {selected.size > 0 && (
        <Space size={0} wrap>
          {!inTrash && !inSpam && systemKey === "INBOX" && (
            <Tooltip title={t("mail.archive")}><Button type="text" icon={<InboxOutlined />} aria-label={t("mail.archive")} onClick={() => void run("archive", sel)} /></Tooltip>
          )}
          {!inSpam && !inTrash && (
            <Tooltip title={t("mail.reportSpam")}><Button type="text" icon={<ExclamationCircleOutlined />} aria-label={t("mail.reportSpam")} onClick={() => void run("spam", sel)} /></Tooltip>
          )}
          {inSpam && <Button type="text" icon={<UndoOutlined />} onClick={() => void run("notSpam", sel)}>{t("mail.notSpam")}</Button>}
          {inTrash ? (
            <>
              <Button type="text" icon={<UndoOutlined />} onClick={() => void run("untrash", sel)}>{t("mail.restore")}</Button>
              <Button type="text" danger icon={<StopOutlined />} onClick={() => void run("deleteForever", sel)}>{t("mail.deleteForever")}</Button>
            </>
          ) : (
            <Tooltip title={t("mail.delete")}><Button type="text" icon={<DeleteOutlined />} aria-label={t("mail.delete")} onClick={() => void run("trash", sel)} /></Tooltip>
          )}
          {inSpam && <Button type="text" danger icon={<StopOutlined />} onClick={() => void run("deleteForever", sel)}>{t("mail.deleteForever")}</Button>}
          {!inTrash && !inSpam && systemKey !== "SNOOZED" && <SnoozeMenu onSnooze={(until) => void run("snooze", sel, undefined, until)} />}
          {systemKey === "SNOOZED" && <Button type="text" onClick={() => void run("unsnooze", sel)}>{t("snooze.unsnooze")}</Button>}
          <Tooltip title={t("mail.markRead")}><Button type="text" icon={<MailOutlined />} aria-label={t("mail.markRead")} onClick={() => void run("markRead", sel)} /></Tooltip>
          <Button type="text" onClick={() => void run("markUnread", sel)}>{t("mail.markUnread")}</Button>
          <Dropdown menu={labelMenu("moveTo")} trigger={["click"]}>
            <Button type="text" icon={<FolderOpenOutlined />} aria-label={t("mail.moveTo")} data-shortcut="move">{t("mail.moveTo")}</Button>
          </Dropdown>
          <Dropdown menu={labelMenu("addLabel")} trigger={["click"]}>
            <Button type="text" icon={<TagOutlined />} aria-label={t("mail.labelAs")} data-shortcut="label">{t("mail.labelAs")}</Button>
          </Dropdown>
        </Space>
      )}
      <span className="bm-header-spacer" />
      {page.data && page.data.total > 0 && (
        <Typography.Text type="secondary" className="bm-range">
          {t("mail.range", { from: start + 1, to: start + items.length, total: page.data.total })}
        </Typography.Text>
      )}
      <Button type="text" icon={<LeftOutlined className="bm-flip-rtl" />} aria-label={t("mail.newer")} disabled={cursors.length <= 1}
        onClick={() => { setCursors((c) => c.slice(0, -1)); setSelected(new Set()); }} />
      <Button type="text" icon={<RightOutlined className="bm-flip-rtl" />} aria-label={t("mail.older")} disabled={!page.data?.nextCursor}
        onClick={() => { setCursors((c) => [...c, page.data!.nextCursor]); setSelected(new Set()); }} />
    </div>
  );

  if (page.isPending) return <>{toolbar}<Spin className="bm-list-spin" /></>;
  // A broken search query is explained (the server says what is wrong), never shown as "no results".
  if (page.isError) {
    const e = page.error;
    return <>{toolbar}<Empty description={e instanceof ApiError && e.code.startsWith("search_") ? e.title : t("errors.loadList")} /></>;
  }

  return (
    <>
      {toolbar}
      {allOnPage && page.data && page.data.total > items.length && (
        <div className="bm-select-all" role="status">
          {t("mail.allOnPageSelected", { count: items.length })}{" "}
          {page.data.total <= maxSelection
            ? <Button type="link" onClick={() => void selectAllMatching(scope, page.data!.total, setSelected)}>{t("mail.selectAllMatching", { count: page.data.total })}</Button>
            : <Typography.Text type="secondary">{t("mail.selectionTooLarge", { max: maxSelection })}</Typography.Text>}
        </div>
      )}
      {items.length === 0 ? (
        <Empty className="bm-empty" description={t("mail.emptyLabel", { label: title })} />
      ) : (
        <ul className="bm-conv-list" aria-label={title}>
          {items.map((c, i) => (
            <Row
              key={c.threadId}
              c={c}
              focused={i === focused && (settings?.keyboardShortcuts ?? false)}
              open={c.threadId === openThreadId}
              checked={selected.has(c.threadId)}
              onCheck={(v) => setSelected((s) => { const n = new Set(s); if (v) n.add(c.threadId); else n.delete(c.threadId); return n; })}
              onOpen={() => onOpen(c.threadId, c.latestMessageId)}
              onAction={(a) => void run(a, [c.threadId])}
              labels={c.labelIds.map((id) => labelById.get(id)).filter((l): l is LabelSummary => !!l && l.systemKey === null && l.showInMessageList)}
              dragIds={() => (selected.has(c.threadId) ? sel : [c.threadId])}
              myAddress={me.email}
              time={snoozedUntil.has(c.threadId) ? t("snooze.until", { time: dateTime(snoozedUntil.get(c.threadId)!, fmt) }) : listTime(c.lastMessageAt, fmt)}
              systemKey={systemKey}
            />
          ))}
        </ul>
      )}
    </>
  );
}

async function selectAllMatching(scope: ListScope, total: number, set: (s: Set<string>) => void) {
  const ids: string[] = [];
  let cursor: string | null = null;
  do {
    const p = await listConversations(scope, cursor, 100);
    ids.push(...p.items.map((c) => c.threadId));
    cursor = p.nextCursor;
  } while (cursor && ids.length < total && ids.length < maxSelection);
  set(new Set(ids));
}

interface RowProps {
  c: ConversationSummary;
  open: boolean;
  checked: boolean;
  onCheck: (v: boolean) => void;
  onOpen: () => void;
  onAction: (a: MailAction) => void;
  labels: LabelSummary[];
  dragIds: () => string[];
  myAddress: string;
  time: string;
  systemKey: string | null;
  focused: boolean;
}

function Row({ c, open, checked, onCheck, onOpen, onAction, labels, dragIds, myAddress, time, systemKey, focused }: RowProps) {
  const { t } = useTranslation();
  const ref = useRef<HTMLLIElement>(null);
  // The keyboard cursor (j/k) is visible and keeps the row on screen.
  useEffect(() => { if (focused) ref.current?.scrollIntoView({ block: "nearest" }); }, [focused]);
  const unread = c.unreadCount > 0;
  const senders = senderParts(c.participants, myAddress);
  return (
    <li
      ref={ref}
      className={`bm-conv${unread ? " bm-unread" : ""}${open ? " bm-open" : ""}${checked ? " bm-checked" : ""}${focused ? " bm-kbd-focus" : ""}`}
      draggable
      onDragStart={(e) => {
        e.dataTransfer.setData(dragType, JSON.stringify(dragIds()));
        e.dataTransfer.effectAllowed = "move";
      }}
    >
      <Checkbox checked={checked} onChange={(e) => onCheck(e.target.checked)} aria-label={t("mail.selectConversation")} />
      <button type="button" className="bm-icon-btn" aria-pressed={c.starred} aria-label={c.starred ? t("mail.unstar") : t("mail.star")}
        onClick={() => onAction(c.starred ? "unstar" : "star")}>
        {c.starred ? <StarFilled className="bm-star-on" /> : <StarOutlined />}
      </button>
      <button type="button" className="bm-icon-btn" aria-pressed={c.important} aria-label={c.important ? t("mail.notImportant") : t("mail.important")}
        onClick={() => onAction(c.important ? "notImportant" : "important")}>
        <span className={c.important ? "bm-important-on" : "bm-important-off"} aria-hidden>❯</span>
      </button>
      <button type="button" className="bm-conv-main" onClick={onOpen} aria-current={open ? "true" : undefined}>
        <span className="bm-conv-from">
          {senders.map((s, i) => (
            <span key={i} className={s.unread ? "bm-strong" : undefined}>{i > 0 ? ", " : ""}{s.me ? t("mail.me") : s.text}</span>
          ))}
          {c.messageCount > 1 && <span className="bm-conv-count">{c.messageCount}</span>}
        </span>
        <span className="bm-conv-text">
          {labels.map((l) => (
            <Tag key={l.id} className="bm-label-chip" style={l.colorBg ? { background: l.colorBg, color: l.colorText ?? undefined, borderColor: l.colorBg } : undefined}>{l.name}</Tag>
          ))}
          <span className={unread ? "bm-strong" : undefined}>{c.subject || t("mail.noSubject")}</span>
          {c.snippet && <span className="bm-snippet"> — {c.snippet}</span>}
          {c.attachmentNames.length > 0 && (
            <span className="bm-att-chips">
              {c.attachmentNames.slice(0, 3).map((n) => <Tag key={n} icon={<PaperClipOutlined />}>{n}</Tag>)}
            </span>
          )}
        </span>
      </button>
      <span className="bm-conv-time">
        {c.hasAttachments && <PaperClipOutlined aria-label={t("mail.hasAttachments")} />} {time}
      </span>
      <span className="bm-conv-hover" role="group" aria-label={t("mail.quickActions")}>
        {systemKey === "INBOX" && (
          <Tooltip title={t("mail.archive")}><Button size="small" type="text" icon={<InboxOutlined />} aria-label={t("mail.archive")} onClick={() => onAction("archive")} /></Tooltip>
        )}
        {systemKey !== "TRASH" && (
          <Tooltip title={t("mail.delete")}><Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("mail.delete")} onClick={() => onAction("trash")} /></Tooltip>
        )}
        <Tooltip title={unread ? t("mail.markRead") : t("mail.markUnread")}>
          <Button size="small" type="text" icon={<MailOutlined />} aria-label={unread ? t("mail.markRead") : t("mail.markUnread")} onClick={() => onAction(unread ? "markRead" : "markUnread")} />
        </Tooltip>
      </span>
    </li>
  );
}
