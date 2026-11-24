import { useState } from "react";
import { App, Button, Checkbox, DatePicker, Drawer, Empty, Form, Input, Modal, Segmented, Select, Space, Spin, Tag, Tooltip, Typography } from "antd";
import { BellOutlined, CalendarOutlined, DeleteOutlined, MailOutlined, PlusOutlined, StarFilled, StarOutlined, UserOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import dayjs from "dayjs";
import { ApiError } from "../../api/client";
import { useUserFormat } from "../../session";
import { dueState, inputOf, taskKeys, tasksApi, type TaskItem, type TaskView } from "./tasksApi";
import { act, listConversations, mailKeys } from "../mail/mailApi";
import { eventColors } from "../calendar/calendarView";

/** To-dos like Outlook / Microsoft To Do: smart lists, quick add, important, due dates, tasks made from messages. */
export function TasksPage() {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [view, setView] = useState<TaskView | "flagged">("my");
  const [title, setTitle] = useState("");
  const [open, setOpen] = useState<TaskItem | null>(null);
  const list = useQuery({ queryKey: taskKeys.view(view as TaskView), queryFn: () => tasksApi.list(view as TaskView), enabled: view !== "flagged" });
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: fmt.timeZone }).format(new Date());
  const fail = (e: unknown) => message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
  const refresh = () => qc.invalidateQueries({ queryKey: taskKeys.all });

  const add = async () => {
    const value = title.trim();
    if (!value) return;
    try {
      await tasksApi.create({ title: value, notes: null, dueDate: view === "planned" ? today : null, important: view === "important", completed: false });
      setTitle("");
      await refresh();
    } catch (e) {
      fail(e);
    }
  };
  const change = async (task: TaskItem, patch: Parameters<typeof inputOf>[1]) => {
    try {
      await tasksApi.update(task.id, inputOf(task, patch));
      await refresh();
    } catch (e) {
      fail(e);
    }
  };
  const dueLabel = (d: string) => new Intl.DateTimeFormat(fmt.locale, { timeZone: "UTC", weekday: "short", day: "numeric", month: "short" }).format(new Date(d + "T00:00:00Z"));

  return (
    <section className="bm-tasks" aria-labelledby="tasks-title">
      <Typography.Title level={4} id="tasks-title">{t("tasks.title")}</Typography.Title>
      <Segmented value={view} onChange={(v) => setView(v as TaskView | "flagged")} aria-label={t("tasks.lists")} className="bm-tasks-views"
        options={(["my", "important", "planned", "flagged", "completed"] as const).map((v) => ({ value: v, label: t(`tasks.views.${v}`) }))} />
      {view === "flagged" ? <FlaggedMail onOpen={(threadId) => navigate(`/mail/starred?t=${threadId}`)} /> : <>
      {view !== "completed" && (
        <Space.Compact className="bm-tasks-add">
          <Input value={title} onChange={(e) => setTitle(e.target.value)} onPressEnter={() => void add()} placeholder={t("tasks.addPlaceholder")}
            aria-label={t("tasks.addPlaceholder")} maxLength={500} dir="auto" prefix={<PlusOutlined />} />
          <Button type="primary" onClick={() => void add()} disabled={!title.trim()}>{t("tasks.add")}</Button>
        </Space.Compact>
      )}
      {list.isPending ? <Spin /> : (list.data ?? []).length === 0 ? <Empty description={t(`tasks.empty.${view}`)} /> : (
        <ul className="bm-task-list">
          {(list.data ?? []).map((task) => {
            const due = task.dueDate ? dueState(task.dueDate, today) : null;
            return (
              <li key={task.id} className={"bm-task" + (task.completedAt ? " bm-task-done" : "")}>
                <Checkbox checked={task.completedAt !== null} aria-label={t(task.completedAt ? "tasks.markOpen" : "tasks.markDone", { title: task.title })}
                  onChange={(e) => void change(task, { completed: e.target.checked })} />
                {task.category && <span className="bm-cal-swatch" style={{ background: eventColors[task.category] }} aria-label={t(`calendar.colors.${task.category}`)} />}
                <button type="button" className="bm-task-main" onClick={() => setOpen(task)}>
                  <span className="bm-task-title" dir="auto">{task.title}</span>
                  <span className="bm-task-meta">
                    {task.threadId && <span><MailOutlined /> {t("tasks.fromMail")}</span>}
                    {task.assignedTo && <Tag icon={<UserOutlined />}>{t("tasks.assignedTo", { name: task.assignedTo })}</Tag>}
                    {task.assignedBy && <Tag icon={<UserOutlined />} color="purple">{t("tasks.assignedBy", { name: task.assignedBy })}</Tag>}
                    {task.dueDate && (
                      <Tag color={due === "overdue" ? "red" : due === "today" ? "blue" : undefined} icon={<CalendarOutlined />}>
                        {due === "today" ? t("tasks.today") : dueLabel(task.dueDate)}
                      </Tag>
                    )}
                    {task.reminderAt && !task.completedAt && <Tag icon={<BellOutlined />}>{dayjs(task.reminderAt).format("YYYY-MM-DD HH:mm")}</Tag>}
                    {task.notes && <span className="bm-muted">{t("tasks.hasNotes")}</span>}
                  </span>
                </button>
                <Tooltip title={t(task.important ? "tasks.unimportant" : "tasks.important")}>
                  <Button type="text" aria-pressed={task.important} aria-label={t(task.important ? "tasks.unimportant" : "tasks.important")}
                    icon={task.important ? <StarFilled className="bm-star-on" /> : <StarOutlined />} onClick={() => void change(task, { important: !task.important })} />
                </Tooltip>
              </li>
            );
          })}
        </ul>
      )}
      </>}
      {open && (
        <TaskDrawer task={open} onClose={() => setOpen(null)} onSaved={() => { setOpen(null); void refresh(); }}
          onOpenMail={(threadId) => navigate(`/mail/all?t=${threadId}`)} />
      )}
    </section>
  );
}

function TaskDrawer({ task, onClose, onSaved, onOpenMail }: { task: TaskItem; onClose: () => void; onSaved: () => void; onOpenMail: (threadId: string) => void }) {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const [assigning, setAssigning] = useState(false);
  const [assignee, setAssignee] = useState("");
  const [form] = Form.useForm<{ title: string; notes?: string; due: dayjs.Dayjs | null; category: string | null; remind: dayjs.Dayjs | null }>();
  const save = async (v: { title: string; notes?: string; due: dayjs.Dayjs | null; category: string | null; remind: dayjs.Dayjs | null }) => {
    try {
      await tasksApi.update(task.id, inputOf(task, { title: v.title.trim(), notes: v.notes?.trim() || null, dueDate: v.due ? v.due.format("YYYY-MM-DD") : null, category: v.category ?? null,
        reminderAt: v.remind ? v.remind.toISOString() : null }));
      onSaved();
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
    }
  };
  const remove = () => modal.confirm({
    title: t("tasks.deleteTitle", { title: task.title }), okButtonProps: { danger: true },
    onOk: () => tasksApi.remove(task.id).then(onSaved, (e) => message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"))),
  });
  return (
    <Drawer open title={t("tasks.details")} onClose={onClose} width={420} extra={
      <Space>
        <Button danger icon={<DeleteOutlined />} onClick={remove} aria-label={t("tasks.delete")} />
        <Button type="primary" onClick={() => form.submit()}>{t("tasks.save")}</Button>
      </Space>
    }>
      <Form form={form} layout="vertical" onFinish={(v) => void save(v)} requiredMark={false}
        initialValues={{ title: task.title, notes: task.notes ?? undefined, due: task.dueDate ? dayjs(task.dueDate) : null, category: task.category, remind: task.reminderAt ? dayjs(task.reminderAt) : null }}>
        <Form.Item name="title" label={t("tasks.fields.title")} rules={[{ required: true, whitespace: true, message: t("tasks.fields.titleRequired") }, { max: 500 }]}>
          <Input dir="auto" />
        </Form.Item>
        <Form.Item name="due" label={t("tasks.fields.due")}><DatePicker format="YYYY-MM-DD" /></Form.Item>
        <Form.Item name="remind" label={t("tasks.fields.remind")}><DatePicker showTime={{ format: "HH:mm", minuteStep: 5 }} format="YYYY-MM-DD HH:mm" /></Form.Item>
        <Form.Item name="category" label={t("tasks.fields.category")}>
          <Select allowClear placeholder={t("tasks.noCategory")} options={Object.entries(eventColors).map(([k, c]) => ({
            value: k, label: <span><span className="bm-cal-swatch" style={{ background: c }} aria-hidden /> {t(`calendar.colors.${k}`)}</span>,
          }))} />
        </Form.Item>
        <Form.Item name="notes" label={t("tasks.fields.notes")}><Input.TextArea rows={6} maxLength={8000} dir="auto" /></Form.Item>
      </Form>
      <Space wrap>
        {task.threadId && <Button icon={<MailOutlined />} onClick={() => onOpenMail(task.threadId!)}>{t("tasks.openMail")}</Button>}
        {!task.assignedTo && !task.assignedBy && !task.completedAt && <Button icon={<UserOutlined />} onClick={() => setAssigning(true)}>{t("tasks.assign")}</Button>}
      </Space>
      {task.assignedTo && <Typography.Paragraph type="secondary" className="bm-mt-8">{t("tasks.assignedNote", { name: task.assignedTo })}</Typography.Paragraph>}
      {assigning && (
        <Modal open title={t("tasks.assign")} onCancel={() => setAssigning(false)} okButtonProps={{ disabled: !assignee.trim() }}
          onOk={async () => {
            try {
              await tasksApi.assign(task.id, assignee.trim());
              message.success(t("tasks.assignedTo", { name: assignee.trim() }));
              setAssigning(false);
              onSaved();
            } catch (e) {
              message.error(e instanceof ApiError ? e.title : t("errors.actionFailed"));
            }
          }}>
          <Typography.Paragraph type="secondary">{t("tasks.assignHelp")}</Typography.Paragraph>
          <Input autoFocus type="email" value={assignee} onChange={(e) => setAssignee(e.target.value)} placeholder={t("tasks.assignEmail")} aria-label={t("tasks.assignEmail")} maxLength={320} />
        </Modal>
      )}
    </Drawer>
  );
}

/** Flagged (starred) messages, which Outlook lists in To Do; completing one removes the flag. */
function FlaggedMail({ onOpen }: { onOpen: (threadId: string) => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const list = useQuery({ queryKey: [...mailKeys.lists, "flagged-tasks"], queryFn: () => listConversations({ labelId: null, q: "is:starred" }, null, 100) });
  const done = (threadId: string) =>
    act({ action: "unstar", threadIds: [threadId] }).then(() => qc.invalidateQueries({ queryKey: mailKeys.lists }), () => message.error(t("errors.actionFailed")));
  if (list.isPending) return <Spin />;
  const items = list.data?.items ?? [];
  if (items.length === 0) return <Empty description={t("tasks.empty.flagged")} />;
  return (
    <ul className="bm-task-list">
      {items.map((c) => (
        <li key={c.threadId} className="bm-task">
          <Checkbox aria-label={t("tasks.markDone", { title: c.subject ?? "" })} onChange={() => void done(c.threadId)} />
          <button type="button" className="bm-task-main" onClick={() => onOpen(c.threadId)}>
            <span className="bm-task-title" dir="auto">{c.subject || t("mail.noSubject")}</span>
            <span className="bm-task-meta"><span><MailOutlined /> {c.participants.map((p) => p.name || p.address).join(", ")}</span></span>
          </button>
        </li>
      ))}
    </ul>
  );
}

