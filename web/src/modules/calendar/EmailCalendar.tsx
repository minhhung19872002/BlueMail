import { useState } from "react";
import { App, Button, Form, Modal, Radio, Select } from "antd";
import { MailOutlined } from "@ant-design/icons";
import { useTranslation } from "react-i18next";
import dayjs from "dayjs";
import { api } from "../../api/client";
import { uploadFile } from "../../api/uploads";
import { useMe, useUserFormat } from "../../session";
import { asGuid } from "../compose/compose";
import { emptyDraft } from "../compose/composeApi";
import { useCompose } from "../compose/composeStore";
import { calendarApi } from "./calendarApi";
import { calendarHtml, escapeHtml, rangeDays, type Detail, type Range } from "./calendarMail";

/** Outlook "E-mail Calendar": a new message with the chosen days of my calendar in the body and as an .ics file. */
export function EmailCalendar({ tz }: { tz: string }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const me = useMe();
  const openCompose = useCompose((s) => s.open);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [range, setRange] = useState<Range>("next7");
  const [detail, setDetail] = useState<Detail>("BUSY");

  const make = async () => {
    setBusy(true);
    try {
      const [from, to] = rangeDays(range, dayjs().tz(tz));
      const [events, ics] = await Promise.all([
        calendarApi.list(from.toISOString(), to.toISOString()),
        api.blob(`/api/v1/calendar/export.ics?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&detail=${detail}`),
      ]);
      const dayFmt = new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, weekday: "long", day: "numeric", month: "long", year: "numeric" });
      const timeFmt = new Intl.DateTimeFormat(fmt.locale, { timeZone: tz, hour: "2-digit", minute: "2-digit", hourCycle: fmt.hourCycle ?? undefined });
      const mine = events.filter((e) => !e.owner).sort((a, b) => a.start.localeCompare(b.start));
      const html = `<p>${escapeHtml(t("calendar.email.intro", { from: dayFmt.format(from.toDate()), to: dayFmt.format(to.subtract(1, "day").toDate()) }))}</p>` +
        calendarHtml(mine, detail, {
          day: (iso) => dayFmt.format(new Date(iso)), time: (iso) => timeFmt.format(new Date(iso)),
          allDay: t("calendar.allDay"), busy: t("calendar.email.busy"), nothing: t("calendar.nothingPlanned"),
        });
      const file = new File([ics], "calendar.ics", { type: "text/calendar" });
      const uploadId = await new Promise<string>((resolve, reject) => {
        uploadFile(file, { progress: () => {}, done: resolve, error: reject });
      });
      openCompose({
        initial: {
          ...emptyDraft(),
          subject: t("calendar.email.subject", { name: me.displayName }),
          html,
          attachments: [{ kind: "upload", uploadId: asGuid(uploadId), messageId: null, partPath: null, fileName: file.name, contentType: file.type, size: file.size, inline: false }],
        },
      });
      setOpen(false);
    } catch {
      message.error(t("errors.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button icon={<MailOutlined />} onClick={() => setOpen(true)}>{t("calendar.email.button")}</Button>
      {open && (
        <Modal open title={t("calendar.email.title")} onCancel={() => !busy && setOpen(false)} onOk={() => void make()} okText={t("calendar.email.create")}
          okButtonProps={{ loading: busy }}>
          <Form layout="vertical">
            <Form.Item label={t("calendar.email.range")}>
              <Select value={range} onChange={setRange} aria-label={t("calendar.email.range")}
                options={(["today", "tomorrow", "next7", "next30"] as const).map((r) => ({ value: r, label: t(`calendar.email.ranges.${r}`) }))} />
            </Form.Item>
            <Form.Item label={t("calendar.email.detail")}>
              <Radio.Group value={detail} onChange={(e) => setDetail(e.target.value as Detail)}>
                <Radio value="BUSY">{t("calendar.email.details.BUSY")}</Radio>
                <Radio value="FULL">{t("calendar.email.details.FULL")}</Radio>
              </Radio.Group>
            </Form.Item>
          </Form>
        </Modal>
      )}
    </>
  );
}
