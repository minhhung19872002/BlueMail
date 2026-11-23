import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { App, Button, Checkbox, DatePicker, Dropdown, Input, Modal, Progress, Select, Space, Tag, Tooltip, Typography, type MenuProps } from "antd";
import {
  CloseOutlined, DeleteOutlined, DownOutlined, ExpandAltOutlined, FileTextOutlined, LockOutlined, MinusOutlined, PaperClipOutlined, ShrinkOutlined,
} from "@ant-design/icons";
import type { Editor } from "@tiptap/react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { Dayjs } from "dayjs";
import { ApiError } from "../../api/client";
import { uploadFile, type UploadHandle } from "../../api/uploads";
import { bytes, dateTime } from "../../format";
import { useMe, useUserFormat } from "../../session";
import { useUi } from "../../store/ui";
import { mailKeys } from "../mail/mailApi";
import { confidentialDays } from "../mail/confidentialApi";
import { scheduleSuggestions, sendWarnings, toEditorHtml, toStoredHtml, uploadIdsIn, type Recipient, type SendWarning } from "./compose";
import { composeApi, emptyDraft, type DraftAttachment, type DraftState } from "./composeApi";
import { MailEditor } from "./MailEditor";
import { RecipientField } from "./RecipientField";
import { useCompose, type ComposeWindowState } from "./composeStore";

const linkThreshold = 25 * 1024 * 1024;

interface Uploading {
  key: string;
  name: string;
  sent: number;
  total: number;
  handle: UploadHandle;
}

/** One compose window (PROMPT 6.4). */
export function ComposeWindow({ w }: { w: ComposeWindowState }) {
  const { t } = useTranslation();
  const { message, modal, notification } = App.useApp();
  const me = useMe();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const undoSeconds = useUi((s) => s.undoSeconds);
  const { close, update, open } = useCompose();
  const [state, setState] = useState<DraftState | null>(null);
  const [draftId, setDraftId] = useState<string | null>(w.draftId);
  const version = useRef<number | null>(null);
  const dirty = useRef(false);
  const saving = useRef<Promise<void> | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [showCc, setShowCc] = useState(false);
  const [uploading, setUploading] = useState<Uploading[]>([]);
  const previews = useRef(new Map<string, string>());
  const editorRef = useRef<Editor | null>(null);
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [custom, setCustom] = useState<Dayjs | null>(null);
  const fromOptions = useQuery({ queryKey: ["fromOptions"], queryFn: composeApi.fromOptions, staleTime: 300_000 });
  const signatures = useQuery({ queryKey: ["signatures"], queryFn: composeApi.signatures, staleTime: 60_000 });
  const templates = useQuery({ queryKey: ["templates"], queryFn: composeApi.templates, staleTime: 60_000 });

  // Load: an existing draft, or a new state (reply/forward prefill or empty) with the default signature.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      let s: DraftState;
      if (w.draftId) {
        const d = await composeApi.get(w.draftId);
        version.current = d.version;
        s = d.state;
        for (const id of uploadIdsIn(s.html)) {
          const blob = await composeApi.uploadPreview(id).catch(() => null);
          if (blob) previews.current.set(id, URL.createObjectURL(blob));
        }
        s = { ...s, html: toEditorHtml(s.html, (id) => previews.current.get(id)) };
      } else {
        s = w.initial ?? emptyDraft();
        const sigs = signatures.data ?? (await composeApi.signatures().catch(() => []));
        const isReply = s.mode === "reply" || s.mode === "replyAll" || s.mode === "forward";
        const sig = sigs.find((x) => (isReply ? x.isDefaultReply : x.isDefaultNew) && (!x.fromAddress || x.fromAddress === s.from)) ?? null;
        if (sig) {
          const block = `<p></p><div class="bm-signature">--<br>${sig.html}</div>`;
          // Above the quoted text for replies (Gmail's default), at the end otherwise.
          s = { ...s, html: isReply ? s.html.replace(/^<p><\/p>/, `<p></p>${block}`) : (s.html || "<p></p>") + block };
        }
      }
      if (!cancelled) {
        setState(s);
        setShowCc(s.cc.length > 0 || s.bcc.length > 0);
      }
    })().catch(() => {
      message.error(t("compose.loadFailed"));
      close(w.key);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [w.key]);

  useEffect(() => () => previews.current.forEach((u) => URL.revokeObjectURL(u)), []);

  const patch = useCallback((p: Partial<DraftState>) => {
    dirty.current = true;
    setState((s) => (s ? { ...s, ...p } : s));
  }, []);

  const stateRef = useRef(state);
  stateRef.current = state;

  /** Saves the draft now (serialised: one save at a time). */
  const save = useCallback(async (): Promise<string | null> => {
    if (saving.current) await saving.current;
    const s = stateRef.current;
    if (!s || (!dirty.current && draftId)) return draftId;
    dirty.current = false;
    let id = draftId;
    const run = (async () => {
      const d = await composeApi.save(id, version.current, { ...s, html: toStoredHtml(s.html) });
      id = d.id;
      version.current = d.version;
      setDraftId(d.id);
      update(w.key, { draftId: d.id });
      setSavedAt(new Date().toISOString());
    })();
    saving.current = run;
    try {
      await run;
    } catch (e) {
      dirty.current = true;
      if (e instanceof ApiError && e.code === "draft_changed") message.warning(t("compose.changedElsewhere"));
      throw e;
    } finally {
      saving.current = null;
    }
    return id;
  }, [draftId, message, t, update, w.key]);

  // Autosave a few seconds after the last change (PROMPT 6.4).
  useEffect(() => {
    const h = window.setInterval(() => {
      if (dirty.current) void save().catch(() => undefined);
    }, 3000);
    return () => window.clearInterval(h);
  }, [save]);

  const onImage = useCallback(async (file: File) => {
    return new Promise<{ id: string; url: string } | null>((resolve) => {
      const key = crypto.randomUUID();
      const handle = uploadFile(file, {
        progress: (sent, total) => setUploading((u) => u.map((x) => (x.key === key ? { ...x, sent, total } : x))),
        done: (id) => {
          setUploading((u) => u.filter((x) => x.key !== key));
          const url = URL.createObjectURL(file);
          previews.current.set(id, url);
          setState((s) => s ? { ...s, attachments: [...s.attachments, { kind: "upload", uploadId: id, messageId: null, partPath: null, fileName: file.name, contentType: file.type, size: file.size, inline: true }] } : s);
          dirty.current = true;
          resolve({ id, url });
        },
        error: () => {
          setUploading((u) => u.filter((x) => x.key !== key));
          message.error(t("compose.uploadFailed", { name: file.name }));
          resolve(null);
        },
      });
      setUploading((u) => [...u, { key, name: file.name, sent: 0, total: file.size, handle }]);
    });
  }, [message, t]);

  const attachFiles = (files: FileList | File[] | null) => {
    for (const file of [...(files ?? [])]) {
      const key = crypto.randomUUID();
      const handle = uploadFile(file, {
        progress: (sent, total) => setUploading((u) => u.map((x) => (x.key === key ? { ...x, sent, total } : x))),
        done: (id) => {
          setUploading((u) => u.filter((x) => x.key !== key));
          patch({ attachments: [...(stateRef.current?.attachments ?? []), { kind: "upload", uploadId: id, messageId: null, partPath: null, fileName: file.name, contentType: file.type || "application/octet-stream", size: file.size, inline: false }] });
        },
        error: () => {
          setUploading((u) => u.filter((x) => x.key !== key));
          message.error(t("compose.uploadFailed", { name: file.name }));
        },
      });
      setUploading((u) => [...u, { key, name: file.name, sent: 0, total: file.size, handle }]);
    }
  };

  const recipients = useMemo<Recipient[]>(() => (state ? [...state.to, ...state.cc, ...state.bcc] : []), [state]);

  const send = async (opts: { scheduleAt?: Date; archive?: boolean }) => {
    if (!state) return;
    if (uploading.length > 0) {
      message.warning(t("compose.waitUploads"));
      return;
    }
    if (recipients.length === 0) {
      message.warning(t("compose.noRecipients"));
      return;
    }
    const warnings = sendWarnings({
      subject: state.subject, html: state.html, attachments: state.attachments.filter((a) => !a.inline).length, recipients, ourDomains: me.domains, mode: state.mode,
    });
    if (warnings.length > 0 && !(await confirmWarnings(warnings))) return;
    try {
      dirty.current = true;
      const id = await save();
      if (!id) return;
      const r = await composeApi.send(id, { scheduleAt: opts.scheduleAt?.toISOString(), archive: opts.archive ?? false, undoSeconds });
      close(w.key);
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      const reopen = async () => {
        try {
          const draft = await composeApi.cancel(r.outgoingId);
          notification.destroy(r.outgoingId);
          open({ draftId: draft });
          await qc.invalidateQueries({ queryKey: mailKeys.lists });
        } catch {
          message.error(t("compose.tooLateToUndo"));
        }
      };
      notification.open({
        key: r.outgoingId,
        message: r.scheduled ? t("compose.scheduledFor", { when: dateTime(r.sendAt, fmt) }) : t("compose.sent"),
        btn: <Button size="small" type="link" onClick={() => void reopen()}>{r.scheduled ? t("compose.cancelSchedule") : t("compose.undo")}</Button>,
        duration: r.scheduled ? 8 : undoSeconds,
        placement: "bottomLeft",
      });
    } catch (e) {
      const code = e instanceof ApiError ? e.code : "";
      message.error(code && t(`compose.error.${code}`, { defaultValue: "" }) ? t(`compose.error.${code}`) : e instanceof ApiError && e.title ? e.title : t("errors.actionFailed"));
    }
  };

  const confirmWarnings = (warnings: SendWarning[]) =>
    new Promise<boolean>((resolve) => {
      modal.confirm({
        title: t("compose.warn.title"),
        content: <ul>{warnings.map((x) => <li key={x}>{t(`compose.warn.${x}`)}</li>)}</ul>,
        okText: t("compose.sendAnyway"),
        cancelText: t("compose.keepEditing"),
        onOk: () => resolve(true),
        onCancel: () => resolve(false),
      });
    });

  const discard = async () => {
    uploading.forEach((u) => u.handle.abort());
    close(w.key);
    if (draftId) {
      await composeApi.discard(draftId).catch(() => undefined);
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
    }
    message.info(t("compose.discarded"));
  };

  const closeWindow = async () => {
    close(w.key);
    if (dirty.current) await save().catch(() => undefined);
    await qc.invalidateQueries({ queryKey: mailKeys.lists });
  };

  const now = new Date();
  const sendMenu: MenuProps = {
    items: [
      ...scheduleSuggestions(now, me.timeZone).map((s) => ({ key: "at:" + s.at.toISOString(), label: `${t(`compose.schedule.${s.key}`)} · ${dateTime(s.at.toISOString(), fmt)}` })),
      { key: "custom", label: t("compose.schedule.custom") },
      ...(state?.replyToMessageId ? [{ type: "divider" as const }, { key: "archive", label: t("compose.sendAndArchive") }] : []),
    ],
    onClick: ({ key }) => {
      if (key === "custom") setScheduleOpen(true);
      else if (key === "archive") void send({ archive: true });
      else if (key.startsWith("at:")) void send({ scheduleAt: new Date(key.slice(3)) });
    },
  };

  const title = state?.subject || t("compose.newMessage");
  if (w.minimized) {
    return (
      <div className="bm-compose bm-compose-min" role="dialog" aria-label={title}>
        <button type="button" className="bm-compose-head" onClick={() => update(w.key, { minimized: false })}>{title}</button>
        <Button size="small" type="text" icon={<CloseOutlined />} aria-label={t("compose.close")} onClick={() => void closeWindow()} />
      </div>
    );
  }

  return (
    <div
      className={`bm-compose${w.maximized ? " bm-compose-max" : ""}`}
      role="dialog"
      aria-label={title}
      onKeyDown={(e) => {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
          e.preventDefault();
          void send({});
        }
      }}
      onDragOver={(e) => e.dataTransfer.types.includes("Files") && e.preventDefault()}
      onDrop={(e) => {
        if (e.dataTransfer.files.length && !(e.target as HTMLElement).closest(".bm-editor")) {
          e.preventDefault();
          attachFiles(e.dataTransfer.files);
        }
      }}
    >
      <div className="bm-compose-bar">
        <span className="bm-compose-title">{title}</span>
        <Space size={0}>
          <Button size="small" type="text" icon={<MinusOutlined />} aria-label={t("compose.minimize")} onClick={() => update(w.key, { minimized: true })} />
          <Button size="small" type="text" icon={w.maximized ? <ShrinkOutlined /> : <ExpandAltOutlined />} aria-label={w.maximized ? t("compose.restore") : t("compose.maximize")}
            onClick={() => update(w.key, { maximized: !w.maximized })} />
          <Button size="small" type="text" icon={<CloseOutlined />} aria-label={t("compose.close")} onClick={() => void closeWindow()} />
        </Space>
      </div>
      {!state ? (
        <Typography.Text type="secondary" className="bm-compose-loading">{t("compose.loading")}</Typography.Text>
      ) : (
        <>
          <div className="bm-compose-fields">
            {(fromOptions.data?.length ?? 0) > 1 && (
              <div className="bm-rcpt-field">
                <span className="bm-rcpt-label">{t("compose.from")}</span>
                <Select variant="borderless" className="bm-from" value={state.from ?? fromOptions.data?.[0]?.address} onChange={(v) => patch({ from: v })}
                  aria-label={t("compose.from")}
                  options={fromOptions.data?.map((o) => ({ value: o.address, label: `${o.name} <${o.address}>` }))} />
              </div>
            )}
            <RecipientField label={t("compose.to")} field="to" value={state.to} onChange={(v) => patch({ to: v })} ourDomains={me.domains}
              autoFocus={state.mode === "new" || state.mode === "forward"} onMovedIn={(r, from) => patch({ [from]: state[from].filter((x) => x.address !== r.address) })} />
            {!showCc && <Button type="link" size="small" className="bm-cc-toggle" onClick={() => setShowCc(true)}>{t("compose.ccBcc")}</Button>}
            {showCc && (
              <>
                <RecipientField label={t("compose.cc")} field="cc" value={state.cc} onChange={(v) => patch({ cc: v })} ourDomains={me.domains}
                  onMovedIn={(r, from) => patch({ [from]: state[from].filter((x) => x.address !== r.address) })} />
                <RecipientField label={t("compose.bcc")} field="bcc" value={state.bcc} onChange={(v) => patch({ bcc: v })} ourDomains={me.domains}
                  onMovedIn={(r, from) => patch({ [from]: state[from].filter((x) => x.address !== r.address) })} />
              </>
            )}
            <Input variant="borderless" className="bm-subject" value={state.subject} maxLength={998} placeholder={t("compose.subject")} aria-label={t("compose.subject")}
              onChange={(e) => patch({ subject: e.target.value })} />
          </div>
          <MailEditor initialHtml={state.html} onChange={(html) => patch({ html })} onImage={onImage} ariaLabel={t("compose.body")}
            onReady={(e) => { editorRef.current = e; }} />
          <AttachmentList attachments={state.attachments.filter((a) => !a.inline)} uploading={uploading}
            onRemove={(a) => patch({ attachments: state.attachments.filter((x) => x !== a) })}
            onCancel={(u) => { u.handle.abort(); setUploading((x) => x.filter((y) => y.key !== u.key)); }} />
          <div className="bm-compose-foot">
            <Space.Compact>
              <Button type="primary" onClick={() => void send({})}>{t("compose.send")}</Button>
              <Dropdown menu={sendMenu} trigger={["click"]}>
                <Button type="primary" icon={<DownOutlined />} aria-label={t("compose.moreSendOptions")} />
              </Dropdown>
            </Space.Compact>
            <Tooltip title={t("compose.attach")}>
              <Button type="text" icon={<PaperClipOutlined />} aria-label={t("compose.attach")} onClick={() => {
                const input = document.createElement("input");
                input.type = "file";
                input.multiple = true;
                input.onchange = () => attachFiles(input.files);
                input.click();
              }} />
            </Tooltip>
            <Dropdown trigger={["click"]} menu={{
              items: [
                ...(templates.data ?? []).map((tp) => ({ key: "ins:" + tp.id, label: t("compose.insertTemplate", { name: tp.name }) })),
                ...((templates.data?.length ?? 0) > 0 ? [{ type: "divider" as const }] : []),
                { key: "save", label: t("compose.saveAsTemplate") },
              ],
              onClick: ({ key }) => {
                if (key === "save") {
                  let name = state.subject || t("compose.newMessage");
                  modal.confirm({
                    title: t("compose.saveAsTemplate"),
                    content: <Input defaultValue={name} onChange={(e) => { name = e.target.value; }} aria-label={t("compose.templateName")} />,
                    onOk: async () => {
                      await composeApi.saveTemplate({ id: null, name, subject: state.subject, html: toStoredHtml(state.html) });
                      await qc.invalidateQueries({ queryKey: ["templates"] });
                      message.success(t("compose.templateSaved"));
                    },
                  });
                } else {
                  const tp = templates.data?.find((x) => "ins:" + x.id === key);
                  if (!tp) return;
                  if (!state.subject) patch({ subject: tp.subject });
                  editorRef.current?.chain().focus().insertContent(tp.html).run();
                }
              },
            }}>
              <Button type="text" icon={<FileTextOutlined />} aria-label={t("compose.templates")} />
            </Dropdown>
            <ConfidentialButton value={state.confidential ?? null} onChange={(v) => patch({ confidential: v })} />
            <span className="bm-header-spacer" />
            {savedAt && <Typography.Text type="secondary" className="bm-saved">{t("compose.saved")}</Typography.Text>}
            <Tooltip title={t("compose.discard")}>
              <Button type="text" icon={<DeleteOutlined />} aria-label={t("compose.discard")} onClick={() => void discard()} />
            </Tooltip>
          </div>
          <Modal open={scheduleOpen} title={t("compose.schedule.custom")} onCancel={() => setScheduleOpen(false)} okButtonProps={{ disabled: !custom }}
            onOk={() => {
              if (!custom) return;
              setScheduleOpen(false);
              void send({ scheduleAt: custom.toDate() });
            }}>
            <DatePicker showTime={{ format: "HH:mm" }} value={custom} onChange={setCustom} disabledDate={(d) => d.isBefore(new Date(), "day")} className="bm-full" />
            <Typography.Paragraph type="secondary" className="bm-hint">{t("compose.schedule.zone", { zone: me.timeZone })}</Typography.Paragraph>
          </Modal>
        </>
      )}
    </div>
  );
}

/** Confidential mode toggle (Phase 12): link lifetime and whether the recipient needs a code sent by mail. */
function ConfidentialButton({ value, onChange }: { value: DraftState["confidential"] | null; onChange: (v: DraftState["confidential"] | null) => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [days, setDays] = useState(value?.expiresDays ?? 7);
  const [code, setCode] = useState(value?.requireCode ?? false);
  return (
    <>
      <Tooltip title={value ? t("confidential.on", { count: value.expiresDays }) : t("confidential.title")}>
        <Button type={value ? "link" : "text"} icon={<LockOutlined />} aria-pressed={!!value} aria-label={t("confidential.title")} onClick={() => setOpen(true)} />
      </Tooltip>
      <Modal open={open} title={t("confidential.title")} onCancel={() => setOpen(false)}
        footer={[
          value && <Button key="off" onClick={() => { onChange(null); setOpen(false); }}>{t("confidential.turnOff")}</Button>,
          <Button key="ok" type="primary" onClick={() => { onChange({ expiresDays: days, requireCode: code }); setOpen(false); }}>{t("confidential.apply")}</Button>,
        ]}>
        <Space direction="vertical">
          <Typography.Paragraph type="secondary">{t("confidential.explain")}</Typography.Paragraph>
          <label>
            {t("confidential.expiry")}{" "}
            <Select value={days} onChange={setDays} style={{ minWidth: 160 }}
              options={confidentialDays.map((d) => ({ value: d, label: t("confidential.days", { count: d }) }))} />
          </label>
          <Checkbox checked={code} onChange={(e) => setCode(e.target.checked)}>{t("confidential.requireCode")}</Checkbox>
        </Space>
      </Modal>
    </>
  );
}

function AttachmentList({ attachments, uploading, onRemove, onCancel }: {
  attachments: DraftAttachment[];
  uploading: Uploading[];
  onRemove: (a: DraftAttachment) => void;
  onCancel: (u: Uploading) => void;
}) {
  const { t } = useTranslation();
  const me = useMe();
  if (attachments.length === 0 && uploading.length === 0) return null;
  return (
    <div className="bm-compose-atts" aria-label={t("mail.attachments")}>
      {attachments.map((a) => (
        <Tag key={(a.uploadId ?? "") + (a.partPath ?? "") + a.fileName} closable onClose={() => onRemove(a)} icon={<PaperClipOutlined />}>
          {a.fileName} · {bytes(a.size, me.locale)}{a.size > linkThreshold ? ` · ${t("compose.sentAsLink")}` : ""}
        </Tag>
      ))}
      {uploading.map((u) => (
        <div key={u.key} className="bm-upload-row">
          <span className="bm-upload-name">{u.name}</span>
          <Progress percent={u.total ? Math.round((u.sent / u.total) * 100) : 0} size="small" className="bm-upload-progress" />
          <Button size="small" type="text" icon={<CloseOutlined />} aria-label={t("compose.cancelUpload", { name: u.name })} onClick={() => onCancel(u)} />
        </div>
      ))}
    </div>
  );
}
