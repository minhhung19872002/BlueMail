import { useEffect, useRef } from "react";
import { App, Button } from "antd";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { useUserFormat } from "../../session";
import { useSettings } from "../settings/settingsApi";
import { calendarApi } from "./calendarApi";
import { dueTaskReminders, taskReminderKey, tasksApi } from "../tasks/tasksApi";
import { dueReminders, reminderKey } from "./calendarView";

const ShownKey = "bm-cal-reminders";

/**
 * Meeting reminders like Outlook's reminder window: checks every minute (while BlueMail is open) for events whose
 * reminder time has come, shows them in the page and, when the user allowed desktop notifications, on the desktop.
 */
export function CalendarReminders() {
  const { t } = useTranslation();
  const { notification } = App.useApp();
  const fmt = useUserFormat();
  const settings = useSettings();
  const navigate = useNavigate();
  const desktop = useRef(false);
  desktop.current = settings?.desktopNotifications === true;

  useEffect(() => {
    let stopped = false;
    const shown = new Set<string>(JSON.parse(sessionStorage.getItem(ShownKey) ?? "[]") as string[]);
    const check = async () => {
      const now = Date.now();
      try {
        // The longest reminder is a day: look a day ahead (and back an hour for events already running).
        const events = await calendarApi.list(new Date(now - 3_600_000).toISOString(), new Date(now + 25 * 3_600_000).toISOString());
        if (stopped) return;
        for (const r of dueReminders(events, now, shown)) {
          shown.add(reminderKey(r.event));
          const when = new Intl.DateTimeFormat(fmt.locale, { timeZone: fmt.timeZone, timeStyle: "short", hourCycle: fmt.hourCycle ?? undefined })
            .format(new Date(r.event.start));
          const body = r.event.location ? t("calendar.remind.bodyAt", { time: when, place: r.event.location }) : t("calendar.remind.body", { time: when });
          notification.info({
            key: reminderKey(r.event), message: r.event.summary, description: body, duration: 0, placement: "bottomRight",
            btn: <Button size="small" onClick={() => { notification.destroy(reminderKey(r.event)); navigate("/calendar"); }}>{t("calendar.remind.open")}</Button>,
          });
          if (desktop.current && "Notification" in window && Notification.permission === "granted") {
            new Notification(r.event.summary, { body, tag: reminderKey(r.event) });
          }
        }
        // Task reminders (Outlook To Do): open tasks whose reminder time has come.
        const tasks = await tasksApi.list("my");
        if (stopped) return;
        for (const task of dueTaskReminders(tasks, now, shown)) {
          const key = taskReminderKey(task);
          shown.add(key);
          const body = t("tasks.remindBody");
          notification.info({
            key, message: task.title, description: body, duration: 0, placement: "bottomRight",
            btn: <Button size="small" onClick={() => { notification.destroy(key); navigate("/tasks"); }}>{t("tasks.remindOpen")}</Button>,
          });
          if (desktop.current && "Notification" in window && Notification.permission === "granted") {
            new Notification(task.title, { body, tag: key });
          }
        }
        sessionStorage.setItem(ShownKey, JSON.stringify([...shown].slice(-200)));
      } catch {
        // Offline or signed out: try again next minute.
      }
    };
    void check();
    const id = window.setInterval(() => void check(), 60_000);
    return () => {
      stopped = true;
      window.clearInterval(id);
    };
  }, [fmt.locale, fmt.timeZone, fmt.hourCycle, notification, navigate, t]);

  return null;
}
