import { useEffect, useMemo, useRef, useState, lazy, Suspense } from "react";
import { Alert, App, Button, Card, Descriptions, Dropdown, Modal, Popover, Space, Spin, Table, Tag, Tooltip, Typography, type MenuProps } from "antd";
import {
  ArrowLeftOutlined, CaretDownOutlined, DeleteOutlined, FlagOutlined, DownloadOutlined, ExclamationCircleOutlined, EyeOutlined, FileZipOutlined, InboxOutlined,
  MailOutlined, MoreOutlined, PaperClipOutlined, PrinterOutlined, RollbackOutlined, ShareAltOutlined, StarFilled, StarOutlined, WarningOutlined,
} from "@ant-design/icons";
import DOMPurify from "dompurify";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { Attachment, AuthResultsSummary, LabelSummary, ThreadMessage } from "../../api/types";
import { bytes, dateTime } from "../../format";
import { useMe, useUserFormat } from "../../session";
import { act, getBody, getOriginal, getThread, mailKeys, trustImages, unsubscribe, type MailAction } from "./mailApi";
import { SnoozeMenu } from "./SnoozeMenu";
import { SweepButton } from "./Sweep";
import { QuickStepsMenu } from "./QuickSteps";
import { InviteCard } from "./InviteCard";
import { followUpDue, taskKeys, tasksApi, type FollowUp } from "../tasks/tasksApi";
import { useShortcuts } from "./ShortcutHost";
import { ApiError } from "../../api/client";
import { authWarnings, domainOf, isExternal, looksLike, senderDetails } from "./mailView";
const PdfPreview = lazy(() => import("./PdfPreview").then((m) => ({ default: m.PdfPreview })));
import { SpamReasons } from "./SpamReasons";
import { rulesApi } from "../settings/rulesApi";
import { composeApi } from "../compose/composeApi";
import { useCompose } from "../compose/composeStore";
import { confidentialApi } from "./confidentialApi";

interface Props {
  threadId: string;
  labels: LabelSummary[];
  systemKey: string | null;
  onClose: () => void;
}

/** Reading pane (PROMPT 6.3). */
export function ThreadView({ threadId, labels, systemKey, onClose }: Props) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const thread = useQuery({ queryKey: mailKeys.thread(threadId), queryFn: () => getThread(threadId) });
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [showOlder, setShowOlder] = useState(false);
  const markedRead = useRef<string | null>(null);
  const me = useMe();
  const userFmt = useUserFormat();

  const msgs = thread.data?.messages ?? [];
  // Open by default: unread messages and the last one (PROMPT 6.3).
  const defaultOpen = useMemo(() => new Set(msgs.filter((m, i) => !m.message.seen || i === msgs.length - 1).map((m) => m.message.id)), [msgs]);

  useEffect(() => {
    if (!thread.data || markedRead.current === threadId) return;
    markedRead.current = threadId;
    if (thread.data.messages.some((m) => !m.message.seen)) {
      void act({ action: "markRead", threadIds: [threadId] }).then(() => {
        void qc.invalidateQueries({ queryKey: mailKeys.lists });
        void qc.invalidateQueries({ queryKey: mailKeys.labels });
      });
    }
  }, [thread.data, threadId, qc]);

  const run = async (action: MailAction, close = true, snoozeUntil?: Date) => {
    await act({ action, threadIds: [threadId], snoozeUntil: snoozeUntil?.toISOString() });
    if (action === "snooze" || action === "unsnooze") await qc.invalidateQueries({ queryKey: ["snoozed"] });
    await qc.invalidateQueries({ queryKey: mailKeys.lists });
    await qc.invalidateQueries({ queryKey: mailKeys.labels });
    await qc.invalidateQueries({ queryKey: mailKeys.thread(threadId) });
    if (close) onClose();
    else message.success(t("mail.done.saved"));
  };

  const lastId = msgs[msgs.length - 1]?.message.id;
  const openCompose = useCompose((s) => s.open);
  const replyWith = (mode: "reply" | "replyAll" | "forward") => {
    if (lastId) void composeApi.fromMessage(lastId, mode).then((initial) => openCompose({ initial }), () => message.error(t("errors.actionFailed")));
  };
  useShortcuts({
    back: onClose,
    close: onClose,
    reply: () => replyWith("reply"),
    replyAll: () => replyWith("replyAll"),
    forward: () => replyWith("forward"),
    archive: () => void run("archive"),
    trash: () => void run("trash"),
    spam: () => void run("spam"),
    star: () => void run(msgs.some((m) => m.message.starred) ? "unstar" : "star", false),
    important: () => void run("important", false),
    notImportant: () => void run("notImportant", false),
    markRead: () => void run("markRead", false),
    markUnread: () => void run("markUnread"),
    mute: () => void run("mute"),
    snooze: () => document.querySelector<HTMLElement>(".bm-thread [data-shortcut=snooze]")?.click(),
    nextMessage: () => setExpanded((s) => { const last = [...msgs].reverse().find((m) => !s.has(m.message.id) && !defaultOpen.has(m.message.id)); const n = new Set(s); if (last) n.add(last.message.id); return n; }),
    prevMessage: () => setExpanded(new Set()),
  });

  if (thread.isPending) return <Spin className="bm-list-spin" />;
  if (thread.isError || !thread.data) return <Alert type="warning" showIcon message={t("errors.loadThread")} action={<Button onClick={onClose}>{t("mail.back")}</Button>} />;

  const starred = msgs.some((m) => m.message.starred);
  // Unsubscribe from the newest message that offers it (List-Unsubscribe, RFC 2369 / 8058).
  const unsubscribable = [...msgs].reverse().find((m) => m.message.listUnsubscribe)?.message.id;
  const doUnsubscribe = async (messageId: string) => {
    try {
      const r = await unsubscribe(messageId);
      if (r.outcome === "OPEN_URL" && r.url) window.open(r.url, "_blank", "noopener,noreferrer");
      else message.success(t("mail.unsubscribed"));
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    }
  };
  // Sweep needs the other party: the newest sender that is not me, and the inbox to sweep.
  const sweepSender = [...msgs].reverse().map((m) => m.message.from?.address).find((a) => a && a.toLowerCase() !== me.email.toLowerCase());
  const inboxLabelId = labels.find((l) => l.systemKey === "INBOX")?.id;
  const userLabelIds = new Set(msgs.flatMap((m) => m.message.labelIds));
  const chips = labels.filter((l) => l.systemKey === null && userLabelIds.has(l.id));
  const hidden = !showOlder && msgs.length > 4 ? msgs.slice(1, -2).filter((m) => !defaultOpen.has(m.message.id)) : [];
  const hiddenIds = new Set(hidden.map((m) => m.message.id));

  return (
    <article className="bm-thread" aria-labelledby="bm-thread-subject">
      <div className="bm-list-toolbar" role="toolbar" aria-label={t("mail.toolbar")}>
        <Tooltip title={t("mail.back")}><Button type="text" icon={<ArrowLeftOutlined className="bm-flip-rtl" />} aria-label={t("mail.back")} onClick={onClose} /></Tooltip>
        {systemKey === "INBOX" && <Tooltip title={t("mail.archive")}><Button type="text" icon={<InboxOutlined />} aria-label={t("mail.archive")} onClick={() => void run("archive")} /></Tooltip>}
        {systemKey !== "SPAM" && <Tooltip title={t("mail.reportSpam")}><Button type="text" icon={<ExclamationCircleOutlined />} aria-label={t("mail.reportSpam")} onClick={() => void run("spam")} /></Tooltip>}
        {systemKey === "SPAM" && <Button type="text" onClick={() => void run("notSpam")}>{t("mail.notSpam")}</Button>}
        {systemKey !== "TRASH" && <Tooltip title={t("mail.delete")}><Button type="text" icon={<DeleteOutlined />} aria-label={t("mail.delete")} onClick={() => void run("trash")} /></Tooltip>}
        {systemKey === "INBOX" && sweepSender && inboxLabelId && <SweepButton sender={sweepSender} inboxLabelId={inboxLabelId} onDone={onClose} />}
        {systemKey !== "SNOOZED" && systemKey !== "TRASH" && systemKey !== "SPAM" && (
          <SnoozeMenu onSnooze={(until) => void run("snooze", true, until).then(() => message.success(t("mail.done.snoozed", { count: 1 })))} />
        )}
        {systemKey === "SNOOZED" && <Button type="text" onClick={() => void run("unsnooze")}>{t("snooze.unsnooze")}</Button>}
        {unsubscribable && (
          <Button type="text" onClick={() => void doUnsubscribe(unsubscribable)}>{t("mail.unsubscribe")}</Button>
        )}
        <QuickStepsMenu threadIds={[threadId]} labels={labels} onLeft={onClose} />
        <Dropdown trigger={["click"]} menu={{
          items: (["today", "tomorrow", "thisWeek", "nextWeek", "noDate"] as const).map((k) => ({ key: k, label: t(`mail.followUp.${k}`) })),
          onClick: ({ key }) => {
            // Outlook "Follow Up": the message is flagged (starred) and a dated task links to it.
            const today = new Intl.DateTimeFormat("en-CA", { timeZone: userFmt.timeZone }).format(new Date());
            const due = followUpDue(key as FollowUp, today);
            if (!lastId) return;
            void Promise.all([run("star", false), tasksApi.create({ title: null, notes: null, dueDate: due, important: false, completed: false, messageId: lastId })])
              .then(() => { void qc.invalidateQueries({ queryKey: taskKeys.all }); message.success(t("mail.followUp.done")); },
                () => message.error(t("errors.actionFailed")));
          },
        }}>
          <Tooltip title={t("mail.followUp.button")}><Button type="text" icon={<FlagOutlined />} aria-label={t("mail.followUp.button")} /></Tooltip>
        </Dropdown>
        <Tooltip title={t("mail.markUnread")}><Button type="text" icon={<MailOutlined />} aria-label={t("mail.markUnread")} onClick={() => void run("markUnread")} /></Tooltip>
        <Tooltip title={t("mail.print")}><Button type="text" icon={<PrinterOutlined />} aria-label={t("mail.print")} onClick={() => window.print()} /></Tooltip>
        <Dropdown trigger={["click"]} menu={{
          items: [
            { key: "mute", label: thread.data.muted ? t("mail.unmute") : t("mail.mute") },
            { key: "important", label: t("mail.important") },
            { key: "notImportant", label: t("mail.notImportant") },
            { key: "reportPhishing", label: t("mail.reportPhishing"), danger: true },
          ],
          onClick: ({ key }) => {
            if (key === "reportPhishing") {
              void run("reportPhishing").then(() => message.success(t("mail.phishingDone")));
              return;
            }
            void run(key === "mute" ? (thread.data!.muted ? "unmute" : "mute") : (key as MailAction), key === "mute");
          },
        }}>
          <Button type="text" icon={<MoreOutlined />} aria-label={t("mail.more")} />
        </Dropdown>
      </div>
      <header className="bm-thread-head">
        <Typography.Title level={4} id="bm-thread-subject" className="bm-thread-subject">
          {thread.data.subject || t("mail.noSubject")}
        </Typography.Title>
        {chips.map((l) => (
          <Tag key={l.id} closable onClose={() => void act({ action: "removeLabel", threadIds: [threadId], labelId: l.id }).then(() => qc.invalidateQueries({ queryKey: mailKeys.thread(threadId) }))}
            style={l.colorBg ? { background: l.colorBg, color: l.colorText ?? undefined, borderColor: l.colorBg } : undefined}>{l.name}</Tag>
        ))}
        <button type="button" className="bm-icon-btn" aria-pressed={starred} aria-label={starred ? t("mail.unstar") : t("mail.star")} onClick={() => void run(starred ? "unstar" : "star", false)}>
          {starred ? <StarFilled className="bm-star-on" /> : <StarOutlined />}
        </button>
      </header>
      {systemKey === "SCHEDULED" && msgs.length > 0 && <ScheduledBanner messageId={msgs[msgs.length - 1]!.message.id} />}
      {msgs.map((m, i) => {
        if (hiddenIds.has(m.message.id)) {
          return i === 1 ? (
            <Button key="older" className="bm-older" onClick={() => setShowOlder(true)}>{t("mail.olderMessages", { count: hidden.length })}</Button>
          ) : null;
        }
        const open = expanded.has(m.message.id) !== defaultOpen.has(m.message.id);
        return (
          <MessageItem
            key={m.message.id}
            m={m}
            inSpam={systemKey === "SPAM"}
            open={open}
            toggle={() => setExpanded((s) => { const n = new Set(s); if (n.has(m.message.id)) n.delete(m.message.id); else n.add(m.message.id); return n; })}
            fromHere={msgs.slice(i).map((x) => x.message.id)}
            onlyOne={msgs.length === 1}
            onChanged={(close) => {
              void qc.invalidateQueries({ queryKey: mailKeys.lists });
              void qc.invalidateQueries({ queryKey: mailKeys.labels });
              void qc.invalidateQueries({ queryKey: mailKeys.thread(threadId) });
              if (close) onClose();
            }}
          />
        );
      })}
    </article>
  );
}

/** Sender details like Gmail's "show details" / Outlook's message details (PROMPT 6.3). */
function SenderDetailsTable({ m }: { m: ThreadMessage["message"] }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const d = senderDetails(m.authResults);
  const addr = (a: { name: string | null; address: string }) => (a.name ? `${a.name} <${a.address}>` : a.address);
  const items = [
    { key: "from", label: t("mail.details.from"), children: m.from ? addr(m.from) : "—" },
    { key: "to", label: t("mail.details.to"), children: m.to.map(addr).join(", ") || "—" },
    ...(m.cc.length > 0 ? [{ key: "cc", label: t("mail.details.cc"), children: m.cc.map(addr).join(", ") }] : []),
    { key: "date", label: t("mail.details.date"), children: dateTime(m.receivedAt, fmt) },
    { key: "subject", label: t("mail.details.subject"), children: m.subject ?? "—" },
    ...(d.mailedBy ? [{ key: "mailedBy", label: t("mail.details.mailedBy"), children: d.mailedBy }] : []),
    ...(d.signedBy.length > 0 ? [{ key: "signedBy", label: t("mail.details.signedBy"), children: d.signedBy.join(", ") }] : []),
    ...(d.tls !== null ? [{ key: "tls", label: t("mail.details.security"), children: t(d.tls ? "mail.details.tls" : "mail.details.noTls") }] : []),
  ];
  return <Descriptions className="bm-details" size="small" column={1} items={items} />;
}

function MessageItem({ m, open, toggle, inSpam, fromHere, onlyOne, onChanged }: {
  m: ThreadMessage; open: boolean; toggle: () => void; inSpam: boolean;
  /** This message and the ones after it ("mark unread from here"). */
  fromHere: string[];
  onlyOne: boolean;
  onChanged: (close: boolean) => void;
}) {
  const { t } = useTranslation();
  const me = useMe();
  const fmt = useUserFormat();
  const { message, modal } = App.useApp();
  const [showImages, setShowImages] = useState(false);
  const [original, setOriginal] = useState(false);
  const msg = m.message;
  const body = useQuery({ queryKey: mailKeys.body(msg.id, showImages), queryFn: () => getBody(msg.id, showImages), enabled: open });
  const qc = useQueryClient();
  const openCompose = useCompose((s) => s.open);

  const warnings = authWarnings(msg.authResults);
  const fromDomain = msg.from ? domainOf(msg.from.address) : "";
  const imitated = fromDomain ? looksLike(fromDomain, me.domains) : null;
  const external = me.externalSenderBanner && msg.from ? isExternal(msg.from.address, me.domains) : false;
  const files = m.attachments.filter((a) => !a.inline);
  const dangerous = m.attachments.some((a) => a.virusStatus === "INFECTED" || a.virusStatus === "SUSPICIOUS");

  if (!open) {
    return (
      <button type="button" className="bm-msg-collapsed" onClick={toggle}>
        <span className={msg.seen ? undefined : "bm-strong"}>{msg.from?.name || msg.from?.address || t("mail.unknownSender")}</span>
        <span className="bm-snippet">{msg.snippet}</span>
        <span className="bm-conv-time">{dateTime(msg.receivedAt, fmt)}</span>
      </button>
    );
  }

  const sender = msg.from?.address;
  const startCompose = (mode: "reply" | "replyAll" | "forward" | "forwardAttachment") =>
    void composeApi.fromMessage(msg.id, mode).then((initial) => openCompose({ initial }), () => message.error(t("errors.actionFailed")));
  const more: MenuProps = {
    items: [
      // Per-message actions like Outlook's "..." on a message in a conversation.
      { key: "reply", label: t("compose.reply") },
      { key: "replyAll", label: t("compose.replyAll") },
      { key: "forward", label: t("compose.forward") },
      { key: "forwardAttachment", label: t("compose.forwardAttachment") },
      { type: "divider" },
      { key: "deleteOne", label: t("mail.deleteThisMessage") },
      { key: "unreadFromHere", label: t("mail.markUnreadFromHere") },
      { key: "toTask", label: t("tasks.addFromMail") },
      { type: "divider" },
      { key: "original", label: t("mail.showOriginal") },
      { key: "eml", label: <a href={m.rawUrl} download>{t("mail.downloadEml")}</a> },
      ...(sender && sender.toLowerCase() !== me.email.toLowerCase() ? [{ key: "block", danger: true, label: t("mail.blockSender", { address: sender }) }] : []),
    ],
    onClick: ({ key }) => {
      if (key === "reply" || key === "replyAll" || key === "forward" || key === "forwardAttachment") startCompose(key);
      if (key === "deleteOne") {
        // Only this message goes to Trash; the rest of the conversation stays (PROMPT 6.3).
        void act({ action: "trash", messageIds: [msg.id] }).then(() => { message.success(t("mail.done.deletedOne")); onChanged(onlyOne); },
          () => message.error(t("errors.actionFailed")));
      }
      if (key === "unreadFromHere") {
        void act({ action: "markUnread", messageIds: fromHere }).then(() => onChanged(true), () => message.error(t("errors.actionFailed")));
      }
      if (key === "toTask") {
        void tasksApi.fromMessage(msg.id).then(() => { void qc.invalidateQueries({ queryKey: taskKeys.all }); message.success(t("tasks.addedFromMail")); },
          () => message.error(t("errors.actionFailed")));
      }
      if (key === "original") setOriginal(true);
      if (key === "block" && sender) {
        modal.confirm({
          title: t("mail.blockSender", { address: sender }),
          content: t("mail.blockSenderBody"),
          okButtonProps: { danger: true },
          onOk: () => rulesApi.block(sender).then(() => message.success(t("mail.blocked", { address: sender }))),
        });
      }
    },
  };

  return (
    <Card className="bm-msg" size="small" variant="borderless">
      <div className="bm-msg-head">
        <button type="button" className="bm-msg-from" onClick={toggle} aria-expanded>
          <span className="bm-strong">{msg.from?.name || msg.from?.address}</span>
          {msg.from?.name && <span className="bm-muted"> &lt;{msg.from.address}&gt;</span>}
        </button>
        <span className="bm-header-spacer" />
        <span className="bm-conv-time">{dateTime(msg.receivedAt, fmt)}</span>
        <Dropdown menu={more} trigger={["click"]}><Button type="text" size="small" icon={<MoreOutlined />} aria-label={t("mail.more")} /></Dropdown>
      </div>
      <Typography.Text type="secondary" className="bm-msg-to">
        {t("mail.toLine", { to: [...msg.to, ...msg.cc].map((a) => a.name || a.address).join(", ") || "—" })}
        <Popover trigger="click" placement="bottomLeft" content={<SenderDetailsTable m={msg} />}>
          <Button type="text" size="small" icon={<CaretDownOutlined />} aria-label={t("mail.details.show")} />
        </Popover>
      </Typography.Text>

      <SpamReasons report={msg.spamReport} inSpam={inSpam} />
      {warnings.includes("dmarc") && <Alert className="bm-banner" type="error" showIcon icon={<WarningOutlined />} message={t("mail.warn.dmarc", { domain: fromDomain })} />}
      {!warnings.includes("dmarc") && warnings.length > 0 && <Alert className="bm-banner" type="warning" showIcon message={t("mail.warn.auth")} />}
      {imitated && <Alert className="bm-banner" type="error" showIcon message={t("mail.warn.lookalike", { domain: fromDomain, ours: imitated })} />}
      {external && !imitated && <Alert className="bm-banner" type="info" showIcon message={t("mail.warn.external")} />}
      {dangerous && <Alert className="bm-banner" type="error" showIcon message={t("mail.warn.dangerousAttachment")} />}

      {body.isPending && <Spin />}
      {body.data?.hasRemoteImages && !body.data.remoteImagesShown && (
        <Alert className="bm-banner" type="info" message={t("mail.imagesBlocked")} action={
          <Space wrap>
            <Button size="small" onClick={() => setShowImages(true)}>{t("mail.showImages")}</Button>
            <Button size="small" onClick={() => void trustImages(msg.id).then(() => { setShowImages(true); void qc.invalidateQueries({ queryKey: ["body", msg.id] }); })}>
              {t("mail.alwaysShowImages")}
            </Button>
          </Space>
        } />
      )}
      {(body.data?.calendar || m.attachments.some((a) => /^(text\/calendar|application\/ics)/i.test(a.contentType) || /\.ics$/i.test(a.fileName ?? ""))) && (
        <InviteCard messageId={msg.id} />
      )}
      {body.data?.htmlDocument ? (
        <MessageFrame html={body.data.htmlDocument} title={t("mail.messageBody")} />
      ) : (
        body.data?.text && <pre className="bm-msg-text" dir="auto">{body.data.text}</pre>
      )}

      {files.length > 0 && (
        <div className="bm-attachments" aria-label={t("mail.attachments")}>
          <Space wrap>
            {files.map((a) => <AttachmentCard key={a.partPath} a={a} />)}
          </Space>
          {body.data?.zipUrl && (
            <Button icon={<FileZipOutlined />} href={body.data.zipUrl} className="bm-zip">{t("mail.downloadAll")}</Button>
          )}
        </div>
      )}
      {sender && msg.rfcMessageId && sender.toLowerCase() === me.email.toLowerCase() && <ConfidentialBanner rfcMessageId={msg.rfcMessageId} />}
      <ReplyBar messageId={msg.id} />
      {original && <OriginalModal id={msg.id} onClose={() => setOriginal(false)} />}
    </Card>
  );
}

/**
 * The sanitized document (server: HtmlSanitizer) passes DOMPurify again, then goes into an iframe sandbox without
 * allow-scripts and allow-same-origin (PROMPT 6.3). Links may open new tabs, isolated from BlueMail.
 */
export function MessageFrame({ html, title }: { html: string; title: string }) {
  const doc = useMemo(() => {
    const parsed = new DOMParser().parseFromString(html, "text/html");
    const meta = [...parsed.head.querySelectorAll("meta, style")].map((e) => e.outerHTML).join("");
    const clean = DOMPurify.sanitize(parsed.body.innerHTML, { FORBID_TAGS: ["style", "form", "input", "button"], ADD_ATTR: ["target"] });
    return `<!doctype html><html><head>${meta}</head><body>${clean}</body></html>`;
  }, [html]);
  return (
    <iframe
      className="bm-msg-frame"
      title={title}
      sandbox="allow-popups allow-popups-to-escape-sandbox"
      referrerPolicy="no-referrer"
      srcDoc={doc}
    />
  );
}

/** Reply / reply all / forward (PROMPT 6.3, 6.4): opens a compose window prefilled by the server. */
function ReplyBar({ messageId }: { messageId: string }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const open = useCompose((s) => s.open);
  const start = (mode: "reply" | "replyAll" | "forward") =>
    void composeApi.fromMessage(messageId, mode).then((initial) => open({ initial }), () => message.error(t("errors.actionFailed")));
  return (
    <Space className="bm-reply-bar" wrap>
      <Button icon={<RollbackOutlined />} onClick={() => start("reply")}>{t("compose.reply")}</Button>
      <Button icon={<RollbackOutlined />} onClick={() => start("replyAll")}>{t("compose.replyAll")}</Button>
      <Button icon={<ShareAltOutlined className="bm-flip-rtl" />} onClick={() => start("forward")}>{t("compose.forward")}</Button>
    </Space>
  );
}

/** In "Scheduled": when it will go, and a way to stop it (it goes back to Drafts and opens in the editor). */
/** A confidential message I sent (Phase 12): until when it can be opened, who opened it, withdraw or restore access. */
function ConfidentialBanner({ rfcMessageId }: { rfcMessageId: string }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["confidential-status", rfcMessageId], queryFn: () => confidentialApi.status(rfcMessageId) });
  if (!q.data) return null;
  const s = q.data;
  const opened = s.recipients.filter((r) => r.opens > 0).map((r) => r.address);
  const toggle = () => void confidentialApi.setRevoked(s.id, !s.revokedAt).then(() => qc.invalidateQueries({ queryKey: ["confidential-status", rfcMessageId] }));
  return (
    <Alert className="bm-banner" type={s.revokedAt ? "warning" : "info"} showIcon
      message={s.revokedAt ? t("confidential.revoked") : t("confidential.sentUntil", { when: dateTime(s.expiresAt, fmt) })}
      description={opened.length > 0 ? t("confidential.openedBy", { list: opened.join(", ") }) : t("confidential.notOpened")}
      action={<Button size="small" onClick={toggle}>{s.revokedAt ? t("confidential.restore") : t("confidential.revoke")}</Button>} />
  );
}

function ScheduledBanner({ messageId }: { messageId: string }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const open = useCompose((s) => s.open);
  const q = useQuery({
    queryKey: ["scheduled", messageId],
    queryFn: async () => {
      const draft = await composeApi.byMessage(messageId);
      const pending = await composeApi.pending();
      return pending.find((p) => p.draftId === draft.id) ?? null;
    },
  });
  if (!q.data) return null;
  return (
    <Alert className="bm-banner" type="info" showIcon message={t("compose.scheduledFor", { when: dateTime(q.data.sendAt, fmt) })}
      action={<Button size="small" onClick={() => void composeApi.cancel(q.data!.id).then(async (draftId) => {
        await qc.invalidateQueries({ queryKey: mailKeys.lists });
        await qc.invalidateQueries({ queryKey: mailKeys.labels });
        open({ draftId });
      })}>{t("compose.cancelSchedule")}</Button>} />
  );
}

function AttachmentCard({ a }: { a: Attachment }) {
  const { t } = useTranslation();
  const me = useMe();
  const [preview, setPreview] = useState(false);
  const infected = a.virusStatus === "INFECTED";
  return (
    <Card size="small" className={`bm-att-card${infected ? " bm-att-infected" : ""}`}>
      <Space direction="vertical" size={2}>
        <Typography.Text strong ellipsis={{ tooltip: a.fileName }} className="bm-att-name"><PaperClipOutlined /> {a.fileName || t("mail.unnamedFile")}</Typography.Text>
        <Typography.Text type="secondary">{bytes(a.size, me.locale)}</Typography.Text>
        {infected ? (
          <Typography.Text type="danger">{t("mail.attachmentInfected")}</Typography.Text>
        ) : (
          <Space>
            {a.viewUrl && <Button size="small" icon={<EyeOutlined />} onClick={() => setPreview(true)}>{t("mail.view")}</Button>}
            {a.downloadUrl && <Button size="small" icon={<DownloadOutlined />} href={a.downloadUrl}>{t("mail.download")}</Button>}
          </Space>
        )}
        {a.virusStatus === "SUSPICIOUS" && <Typography.Text type="warning">{t("mail.attachmentSuspicious")}</Typography.Text>}
      </Space>
      {preview && a.viewUrl && (
        <Modal open title={a.fileName} footer={null} width="min(960px, 96vw)" onCancel={() => setPreview(false)} destroyOnHidden>
          {a.contentType.startsWith("image/") && <img className="bm-preview-img" src={a.viewUrl} alt={a.fileName ?? ""} />}
          {a.contentType === "application/pdf" && <Suspense fallback={<Spin />}><PdfPreview url={a.viewUrl} /></Suspense>}
          {a.contentType === "text/plain" && <iframe className="bm-preview-text" title={a.fileName ?? ""} sandbox="" src={a.viewUrl} />}
        </Modal>
      )}
    </Card>
  );
}

function OriginalModal({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const q = useQuery({ queryKey: ["original", id], queryFn: () => getOriginal(id) });
  let auth: AuthResultsSummary | null = null;
  try {
    auth = q.data?.authResults ? (JSON.parse(q.data.authResults) as AuthResultsSummary) : null;
  } catch {
    auth = null;
  }
  const rows = auth ? [
    { key: "spf", check: "SPF", result: auth.spf ?? "none", detail: "" },
    ...(auth.dkim ?? []).map((d, i) => ({ key: `dkim${i}`, check: "DKIM", result: d.result, detail: d.domain ?? "" })),
    { key: "dmarc", check: "DMARC", result: auth.dmarc ?? "none", detail: auth.fromDomain ?? "" },
    { key: "arc", check: "ARC", result: auth.arc ?? "none", detail: "" },
  ] : [];
  return (
    <Modal open title={t("mail.showOriginal")} footer={q.data && <Button icon={<DownloadOutlined />} href={q.data.rawUrl}>{t("mail.downloadEml")}</Button>}
      width="min(1000px, 96vw)" onCancel={onClose}>
      {q.isPending && <Spin />}
      {auth && (
        <Table size="small" pagination={false} rowKey="key" dataSource={rows} columns={[
          { title: t("mail.check"), dataIndex: "check" },
          { title: t("mail.result"), dataIndex: "result", render: (r: string) => <Tag color={r === "pass" ? "success" : r === "none" ? "default" : "error"}>{r}</Tag> },
          { title: t("mail.detail"), dataIndex: "detail" },
        ]} />
      )}
      {q.data && (
        <>
          <Descriptions size="small" column={1} items={[{ key: "size", label: t("mail.size"), children: q.data.size }]} />
          <pre className="bm-original" dir="ltr">{q.data.headers}</pre>
        </>
      )}
    </Modal>
  );
}
