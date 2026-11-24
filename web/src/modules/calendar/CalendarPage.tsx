import { useEffect, useMemo, useRef, useState } from "react";
import { App, Button, DatePicker, Form, Input, Modal, Segmented, Select, Space, Spin, Switch, Tag, Tooltip, Typography } from "antd";
import { CalendarOutlined, DeleteOutlined, EditOutlined, EnvironmentOutlined, LeftOutlined, PlusOutlined, RightOutlined, SyncOutlined, TeamOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import dayjs, { type Dayjs } from "dayjs";
import { ApiError } from "../../api/client";
import { useMe, useUserFormat } from "../../session";
import { calendarApi, calendarKeys, type CalendarEvent, type EventInput } from "./calendarApi";
import { availability, eventColors, eventDates, layoutDay, repeatKind, ruleFor, step, visibleDays, type CalendarView, type RepeatKind } from "./calendarView";

const HourPx = 48;

/** Calendar like Outlook on the web: day, week and month views, events with guests (invitations go out by mail). */
export function CalendarPage() {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const me = useMe();
  const tz = fmt.timeZone;
  const weekStart = me.weekStart ?? 1;
  const [view, setView] = useState<CalendarView>(() => (window.innerWidth < 768 ? "day" : "week"));
  const [anchor, setAnchor] = useState<Dayjs>(() => dayjs().tz(tz));
  const [open, setOpen] = useState<CalendarEvent | null>(null);
  const [editing, setEditing] = useState<{ event: CalendarEvent | null; start: Dayjs; allDay: boolean } | null>(null);

  const days = useMemo(() => visibleDays(view, anchor, weekStart), [view, anchor, weekStart]);
  const from = days[0]!.toISOString();
  const to = days[days.length - 1]!.add(1, "day").toISOString();
  const events = useQuery({ queryKey: calendarKeys.range(from, to), queryFn: () => calendarApi.list(from, to) });

  const title = useMemo(() => {
    if (view === "month") return new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, month: "long", year: "numeric" }).format(anchor.toDate());
    if (view === "day") return new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, dateStyle: "full" }).format(anchor.toDate());
    const f = new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, day: "numeric", month: "short", year: "numeric" });
    return `${f.format(days[0]!.toDate())} – ${f.format(days[days.length - 1]!.toDate())}`;
  }, [view, anchor, days, fmt.locale, tz]);

  const byDate = useMemo(() => {
    const map = new Map<string, CalendarEvent[]>();
    for (const e of events.data ?? []) {
      for (const d of eventDates(e, tz)) map.set(d, [...(map.get(d) ?? []), e]);
    }
    return map;
  }, [events.data, tz]);

  const newAt = (start: Dayjs, allDay = false) => setEditing({ event: null, start, allDay });

  return (
    <section className="bm-cal" aria-labelledby="cal-title">
      <div className="bm-cal-toolbar" role="toolbar" aria-label={t("calendar.toolbar")}>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => newAt(dayjs().tz(tz).add(1, "hour").startOf("hour"))}>{t("calendar.newEvent")}</Button>
        <Button onClick={() => setAnchor(dayjs().tz(tz))}>{t("calendar.today")}</Button>
        <Button type="text" icon={<LeftOutlined className="bm-flip-rtl" />} aria-label={t("calendar.previous")} onClick={() => setAnchor((a) => step(view, a, -1))} />
        <Button type="text" icon={<RightOutlined className="bm-flip-rtl" />} aria-label={t("calendar.next")} onClick={() => setAnchor((a) => step(view, a, 1))} />
        <DatePicker aria-label={t("calendar.goTo")} placeholder={t("calendar.goTo")} allowClear={false} format="YYYY-MM-DD"
          onChange={(d) => { if (d) setAnchor(dayjs.tz(d.format("YYYY-MM-DD"), tz).hour(12)); }} />
        <Typography.Title level={4} id="cal-title" className="bm-cal-title">{title}</Typography.Title>
        <Segmented value={view} onChange={(v) => setView(v as CalendarView)} aria-label={t("calendar.view")}
          options={(["day", "workweek", "week", "month"] as const).map((v) => ({ value: v, label: t(`calendar.views.${v}`) }))} />
      </div>
      {events.isPending ? <Spin /> : view === "month" ? (
        <MonthGrid days={days} anchor={anchor} byDate={byDate} tz={tz} onOpen={setOpen} onDay={(d) => { setAnchor(d); setView("day"); }} onNew={(d) => newAt(d, true)} />
      ) : (
        <TimeGrid days={days} byDate={byDate} events={events.data ?? []} tz={tz} onOpen={setOpen} onNew={newAt} />
      )}
      {open && <EventDetails event={open} onClose={() => setOpen(null)} onEdit={() => { setEditing({ event: open, start: dayjs(open.start).tz(tz), allDay: open.allDay }); setOpen(null); }} />}
      {editing && <EventEditor key={editing.event?.id ?? "new"} initial={editing} tz={tz} onClose={() => setEditing(null)} />}
    </section>
  );
}

function useTimeLabel() {
  const fmt = useUserFormat();
  return (iso: string) => new Intl.DateTimeFormat(fmt.locale, { timeZone: fmt.timeZone, timeStyle: "short", hourCycle: fmt.hourCycle ?? undefined }).format(new Date(iso));
}

function Chip({ e, onOpen }: { e: CalendarEvent; onOpen: (e: CalendarEvent) => void }) {
  const time = useTimeLabel();
  const color = eventColors[e.color ?? "blue"] ?? eventColors.blue;
  return (
    <button type="button" className={"bm-cal-chip" + (e.myResponse === "TENTATIVE" ? " bm-cal-tentative" : "")} style={{ borderInlineStartColor: color }}
      onClick={(ev) => { ev.stopPropagation(); onOpen(e); }}>
      {!e.allDay && <span className="bm-cal-chip-time">{time(e.start)}</span>} {e.summary}
    </button>
  );
}

function MonthGrid({ days, anchor, byDate, tz, onOpen, onDay, onNew }: {
  days: Dayjs[]; anchor: Dayjs; byDate: Map<string, CalendarEvent[]>; tz: string;
  onOpen: (e: CalendarEvent) => void; onDay: (d: Dayjs) => void; onNew: (d: Dayjs) => void;
}) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const today = dayjs().tz(tz).format("YYYY-MM-DD");
  const weekday = new Intl.DateTimeFormat(fmt.locale, { timeZone: "UTC", weekday: "short" });
  return (
    <div className="bm-cal-month" role="grid" aria-label={t("calendar.views.month")}>
      <div className="bm-cal-month-head" role="row">
        {days.slice(0, 7).map((d) => <div key={d.day()} role="columnheader">{weekday.format(new Date(Date.UTC(2026, 10, 1 + ((d.day() + 7) % 7))))}</div>)}
      </div>
      {Array.from({ length: 6 }, (_, w) => (
        <div key={w} className="bm-cal-month-row" role="row">
          {days.slice(w * 7, w * 7 + 7).map((d) => {
            const key = d.format("YYYY-MM-DD");
            const list = byDate.get(key) ?? [];
            return (
              <div key={key} role="gridcell" className={"bm-cal-cell" + (d.month() !== anchor.month() ? " bm-cal-other" : "") + (key === today ? " bm-cal-today" : "")}
                onDoubleClick={() => onNew(d)}>
                <button type="button" className="bm-cal-daynum" onClick={() => onDay(d)} aria-label={d.format("YYYY-MM-DD")}>{d.date()}</button>
                {list.slice(0, 3).map((e) => <Chip key={e.id + e.start} e={e} onOpen={onOpen} />)}
                {list.length > 3 && <button type="button" className="bm-cal-more" onClick={() => onDay(d)}>{t("calendar.more", { count: list.length - 3 })}</button>}
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}

function TimeGrid({ days, byDate, events, tz, onOpen, onNew }: {
  days: Dayjs[]; byDate: Map<string, CalendarEvent[]>; events: CalendarEvent[]; tz: string;
  onOpen: (e: CalendarEvent) => void; onNew: (start: Dayjs, allDay?: boolean) => void;
}) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const time = useTimeLabel();
  const scroller = useRef<HTMLDivElement>(null);
  const [now, setNow] = useState(() => dayjs());
  useEffect(() => {
    scroller.current?.scrollTo({ top: 7 * HourPx });
    const id = window.setInterval(() => setNow(dayjs()), 60_000);
    return () => window.clearInterval(id);
  }, []);
  const head = new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, weekday: "short", day: "numeric" });
  const hourLabel = (h: number) => new Intl.DateTimeFormat(fmt.locale, { timeZone: "UTC", hour: "numeric", hourCycle: fmt.hourCycle ?? undefined })
    .format(new Date(Date.UTC(2026, 0, 1, h)));
  const today = now.tz(tz).format("YYYY-MM-DD");
  const nowMinutes = now.tz(tz).hour() * 60 + now.tz(tz).minute();
  return (
    <div className="bm-cal-week" style={{ ["--bm-cal-days" as string]: days.length }}>
      <div className="bm-cal-week-head">
        <div />
        {days.map((d) => (
          <div key={d.valueOf()} className={"bm-cal-week-day" + (d.format("YYYY-MM-DD") === today ? " bm-cal-today" : "")}>{head.format(d.toDate())}</div>
        ))}
      </div>
      <div className="bm-cal-allday" aria-label={t("calendar.allDay")}>
        <div className="bm-cal-gutter">{t("calendar.allDay")}</div>
        {days.map((d) => (
          <div key={d.valueOf()} className="bm-cal-allday-cell" onDoubleClick={() => onNew(d, true)}>
            {(byDate.get(d.format("YYYY-MM-DD")) ?? []).filter((e) => e.allDay).map((e) => <Chip key={e.id + e.start} e={e} onOpen={onOpen} />)}
          </div>
        ))}
      </div>
      <div className="bm-cal-scroll" ref={scroller}>
        <div className="bm-cal-hours">
          {Array.from({ length: 24 }, (_, h) => <div key={h} className="bm-cal-hour" style={{ blockSize: HourPx }}>{h > 0 ? hourLabel(h) : ""}</div>)}
        </div>
        {days.map((d) => {
          const key = d.format("YYYY-MM-DD");
          const placed = layoutDay(events.filter((e) => !e.allDay), d, tz);
          return (
            <div key={key} className="bm-cal-col" style={{ blockSize: 24 * HourPx }} role="group" aria-label={head.format(d.toDate())}
              onClick={(ev) => {
                const y = ev.clientY - (ev.currentTarget as HTMLElement).getBoundingClientRect().top;
                const minutes = Math.floor(y / HourPx * 2) * 30;
                onNew(d.tz(tz, true).startOf("day").add(minutes, "minute"));
              }}>
              {key === today && <div className="bm-cal-now" style={{ insetBlockStart: (nowMinutes / 60) * HourPx }} aria-hidden />}
              {placed.map((p) => {
                const color = eventColors[p.item.color ?? "blue"] ?? eventColors.blue;
                return (
                  <button type="button" key={p.item.id + p.item.start} className={"bm-cal-event" + (p.item.myResponse === "TENTATIVE" ? " bm-cal-tentative" : "")}
                    style={{ insetBlockStart: (p.top / 60) * HourPx, blockSize: Math.max(18, (p.height / 60) * HourPx - 2),
                      insetInlineStart: `calc(${(100 / p.columns) * p.column}% + 2px)`, inlineSize: `calc(${100 / p.columns}% - 4px)`,
                      background: `color-mix(in srgb, ${color} 18%, var(--bm-surface))`, borderInlineStartColor: color }}
                    onClick={(ev) => { ev.stopPropagation(); onOpen(p.item); }}>
                    <span className="bm-strong">{p.item.summary}</span>
                    <span className="bm-cal-event-time">{time(p.item.start)} – {time(p.item.end)}</span>
                    {p.item.location && <span className="bm-cal-event-time">{p.item.location}</span>}
                  </button>
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function EventDetails({ event, onClose, onEdit }: { event: CalendarEvent; onClose: () => void; onEdit: () => void }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const when = event.allDay
    ? new Intl.DateTimeFormat(fmt.locale, { timeZone: "UTC", dateStyle: "full" }).format(new Date(event.start))
    : `${new Intl.DateTimeFormat(fmt.locale, { timeZone: fmt.timeZone, dateStyle: "full", timeStyle: "short", hourCycle: fmt.hourCycle ?? undefined }).format(new Date(event.start))} – ${
      new Intl.DateTimeFormat(fmt.locale, { timeZone: fmt.timeZone, timeStyle: "short", hourCycle: fmt.hourCycle ?? undefined }).format(new Date(event.end))}`;
  const remove = (occurrence?: string) =>
    calendarApi.remove(event.id, occurrence).then(async () => {
      await qc.invalidateQueries({ queryKey: calendarKeys.all });
      message.success(t("calendar.deleted"));
      onClose();
    }, (e) => message.error(e instanceof ApiError ? e.title : t("errors.actionFailed")));
  const askDelete = () => {
    if (!event.recurring) {
      modal.confirm({ title: t("calendar.deleteTitle"), content: event.isOrganizer && event.attendees.length > 0 ? t("calendar.deleteNotice") : undefined,
        okButtonProps: { danger: true }, onOk: () => remove() });
      return;
    }
    const m = modal.confirm({
      title: t("calendar.deleteRecurring"),
      footer: (
        <Space style={{ marginBlockStart: 16 }}>
          <Button onClick={() => { m.destroy(); void remove(event.start); }}>{t("calendar.deleteThis")}</Button>
          <Button danger onClick={() => { m.destroy(); void remove(); }}>{t("calendar.deleteSeries")}</Button>
          <Button onClick={() => m.destroy()}>{t("common.cancel")}</Button>
        </Space>
      ),
    });
  };
  return (
    <Modal open title={event.summary} onCancel={onClose} footer={
      <Space>
        {event.isOrganizer && <Button icon={<EditOutlined />} onClick={onEdit}>{t("calendar.edit")}</Button>}
        <Button danger icon={<DeleteOutlined />} onClick={askDelete}>{t("calendar.delete")}</Button>
      </Space>
    }>
      <Space direction="vertical" size={6} style={{ inlineSize: "100%" }}>
        <Typography.Text><CalendarOutlined /> {when} {event.recurring && <Tooltip title={t("invite.recurring")}><SyncOutlined aria-label={t("invite.recurring")} /></Tooltip>}</Typography.Text>
        {event.location && <Typography.Text><EnvironmentOutlined /> {event.location}</Typography.Text>}
        {!event.isOrganizer && event.organizerEmail && <Typography.Text type="secondary">{t("invite.organizer", { name: event.organizerName || event.organizerEmail })}</Typography.Text>}
        {event.myResponse && <Typography.Text type="secondary">{t(`invite.yours.${event.myResponse}`)}</Typography.Text>}
        {event.attendees.length > 0 && (
          <div>
            <Typography.Text type="secondary"><TeamOutlined /> {t("invite.attendees", { count: event.attendees.length })}</Typography.Text>
            <ul className="bm-cal-guests">
              {event.attendees.map((a) => (
                <li key={a.address}>{a.name || a.address} <Tag>{t(`calendar.response.${a.response ?? "NEEDS-ACTION"}`)}</Tag></li>
              ))}
            </ul>
          </div>
        )}
        {event.description && <Typography.Paragraph className="bm-pre-wrap">{event.description}</Typography.Paragraph>}
      </Space>
    </Modal>
  );
}

interface EditorValues {
  summary: string;
  allDay: boolean;
  start: Dayjs;
  end: Dayjs;
  repeat: RepeatKind;
  location?: string;
  guests: string[];
  reminder: number | null;
  color: string;
  description?: string;
}

/** Picker values are wall times in the user's zone (the browser's zone may differ). */
const toPicker = (d: Dayjs, tz: string) => dayjs(d.tz(tz).format("YYYY-MM-DDTHH:mm"));
const fromPicker = (d: Dayjs, tz: string) => dayjs.tz(d.format("YYYY-MM-DDTHH:mm"), tz);

function EventEditor({ initial, tz, onClose }: { initial: { event: CalendarEvent | null; start: Dayjs; allDay: boolean }; tz: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm<EditorValues>();
  const [busy, setBusy] = useState(false);
  const e = initial.event;
  const allDay = Form.useWatch("allDay", form) ?? initial.allDay;
  // Scheduling assistant (Outlook): guests of the organisation free or busy at the chosen time.
  const guests = (Form.useWatch("guests", form) as string[] | undefined) ?? [];
  const watchStart = Form.useWatch("start", form) as Dayjs | undefined;
  const watchEnd = Form.useWatch("end", form) as Dayjs | undefined;
  const span = watchStart && watchEnd && !allDay ? { s: fromPicker(watchStart, tz).toISOString(), e: fromPicker(watchEnd, tz).toISOString() } : null;
  const validGuests = guests.map((g) => g.trim().toLowerCase()).filter((g) => /^[^\s@]+@[^\s@]+$/.test(g)).slice(0, 20);
  const freeBusy = useQuery({
    queryKey: ["freebusy", validGuests.join(","), span?.s, span?.e],
    queryFn: () => calendarApi.freeBusy(validGuests, dayjs(span!.s).startOf("day").toISOString(), dayjs(span!.s).startOf("day").add(1, "day").toISOString()),
    enabled: span !== null && validGuests.length > 0 && dayjs(span.e).diff(dayjs(span.s), "day") < 1,
  });
  const start = e ? (e.allDay ? dayjs.utc(e.start) : dayjs(e.start).tz(tz)) : initial.start;
  const end = e ? (e.allDay ? dayjs.utc(e.end).subtract(1, "day") : dayjs(e.end).tz(tz)) : initial.allDay ? initial.start : initial.start.add(30, "minute");
  const initialValues: EditorValues = {
    summary: e?.summary ?? "", allDay: initial.allDay,
    start: e?.allDay ? dayjs(start.format("YYYY-MM-DD")) : toPicker(start, tz), end: e?.allDay ? dayjs(end.format("YYYY-MM-DD")) : toPicker(end, tz),
    repeat: repeatKind(e?.recurrenceRule ?? null), location: e?.location ?? undefined, guests: e?.attendees.map((a) => a.address) ?? [],
    reminder: e ? e.reminderMinutes : 10, color: e?.color ?? "blue", description: e?.description ?? undefined,
  };

  const previousStart = useRef<Dayjs>(initialValues.start);

  const save = async (v: EditorValues) => {
    const input: EventInput = {
      summary: v.summary.trim(),
      location: v.location?.trim() || null,
      description: v.description?.trim() || null,
      start: v.allDay ? `${v.start.format("YYYY-MM-DD")}T00:00:00Z` : fromPicker(v.start, tz).toISOString(),
      end: v.allDay ? `${v.end.add(1, "day").format("YYYY-MM-DD")}T00:00:00Z` : fromPicker(v.end, tz).toISOString(),
      allDay: v.allDay,
      timeZone: tz,
      recurrenceRule: ruleFor(v.repeat, e?.recurrenceRule ?? null),
      attendees: v.guests.map((address) => ({ name: e?.attendees.find((a) => a.address === address)?.name ?? null, address: address.trim() })),
      reminderMinutes: v.reminder,
      color: v.color,
    };
    setBusy(true);
    try {
      if (e) await calendarApi.update(e.id, input);
      else await calendarApi.create(input);
      await qc.invalidateQueries({ queryKey: calendarKeys.all });
      message.success(input.attendees.length > 0 ? t("calendar.savedInvited", { count: input.attendees.length }) : t("calendar.saved"));
      onClose();
    } catch (err) {
      message.error(err instanceof ApiError ? err.title : t("errors.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open title={e ? t("calendar.editEvent") : t("calendar.newEvent")} onCancel={onClose} okText={t("calendar.save")}
      okButtonProps={{ loading: busy }} onOk={() => form.submit()} destroyOnHidden>
      <Form form={form} layout="vertical" initialValues={initialValues} onFinish={(v) => void save(v)} requiredMark={false}>
        <Form.Item name="summary" label={t("calendar.fields.title")} rules={[{ required: true, whitespace: true, message: t("calendar.fields.titleRequired") }, { max: 500 }]}>
          <Input autoFocus dir="auto" />
        </Form.Item>
        <Form.Item name="allDay" label={t("calendar.allDay")} valuePropName="checked"><Switch /></Form.Item>
        <Space wrap>
          <Form.Item name="start" label={t("calendar.fields.start")} rules={[{ required: true }]}>
            <DatePicker showTime={allDay ? false : { format: "HH:mm", minuteStep: 5 }} format={allDay ? "YYYY-MM-DD" : "YYYY-MM-DD HH:mm"} allowClear={false}
              onChange={(v) => {
                // Keep the length when the start moves (the form already holds the new start here, so use the previous one).
                if (!v) return;
                const len = (form.getFieldValue("end") as Dayjs).diff(previousStart.current);
                form.setFieldValue("end", v.add(len > 0 ? len : allDay ? 0 : 30 * 60_000, "millisecond"));
                previousStart.current = v;
                void form.validateFields(["end"]).catch(() => undefined);
              }} />
          </Form.Item>
          <Form.Item name="end" label={t("calendar.fields.end")} dependencies={["start"]} rules={[{ required: true }, ({ getFieldValue }) => ({
            validator: (_, value: Dayjs) => !value || (allDay ? !value.isBefore(getFieldValue("start"), "day") : value.isAfter(getFieldValue("start")))
              ? Promise.resolve() : Promise.reject(new Error(t("calendar.fields.endAfterStart"))),
          })]}>
            <DatePicker showTime={allDay ? false : { format: "HH:mm", minuteStep: 5 }} format={allDay ? "YYYY-MM-DD" : "YYYY-MM-DD HH:mm"} allowClear={false} />
          </Form.Item>
        </Space>
        <Form.Item name="repeat" label={t("calendar.fields.repeat")}>
          <Select options={(["none", "daily", "weekdays", "weekly", "monthly", "yearly", ...(repeatKind(e?.recurrenceRule ?? null) === "custom" ? ["custom"] : [])] as RepeatKind[])
            .map((k) => ({ value: k, label: t(`calendar.repeat.${k}`) }))} />
        </Form.Item>
        <Form.Item name="location" label={t("calendar.fields.location")}><Input dir="auto" maxLength={500} /></Form.Item>
        <Form.Item name="guests" label={t("calendar.fields.guests")} extra={t("calendar.fields.guestsHint")} rules={[{
          validator: (_, v: string[]) => (v ?? []).every((a) => /^[^\s@]+@[^\s@]+$/.test(a.trim())) ? Promise.resolve() : Promise.reject(new Error(t("calendar.fields.badGuest"))),
        }]}>
          <Select mode="tags" tokenSeparators={[",", ";", " "]} open={false} suffixIcon={null} aria-label={t("calendar.fields.guests")} />
        </Form.Item>
        {span && freeBusy.data && validGuests.length > 0 && (
          <div className="bm-cal-availability" aria-live="polite">
            {validGuests.map((g) => {
              const a = availability(freeBusy.data.find((f) => f.address === g), span.s, span.e);
              return <Tag key={g} color={a === "busy" ? "red" : a === "free" ? "green" : undefined}>{g}: {t(`calendar.availability.${a}`)}</Tag>;
            })}
          </div>
        )}
        <Space wrap>
          <Form.Item name="reminder" label={t("calendar.fields.reminder")}>
            <Select style={{ minInlineSize: 160 }} options={[null, 0, 5, 10, 15, 30, 60, 120, 1440].map((m) => ({ value: m, label: m === null ? t("calendar.reminder.none") : t("calendar.reminder.before", { count: m }) }))} />
          </Form.Item>
          <Form.Item name="color" label={t("calendar.fields.color")}>
            <Select style={{ minInlineSize: 140 }} options={Object.entries(eventColors).map(([k, c]) => ({
              value: k, label: <span><span className="bm-cal-swatch" style={{ background: c }} aria-hidden /> {t(`calendar.colors.${k}`)}</span>,
            }))} />
          </Form.Item>
        </Space>
        <Form.Item name="description" label={t("calendar.fields.description")}><Input.TextArea rows={3} maxLength={8000} dir="auto" /></Form.Item>
      </Form>
    </Modal>
  );
}
