import { Alert, App, Button, Card, Space, Spin, Tooltip, Typography } from "antd";
import { CalendarOutlined, CheckOutlined, CloseOutlined, EnvironmentOutlined, QuestionOutlined, SyncOutlined, TeamOutlined } from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { useUserFormat } from "../../session";
import type { UserFormat } from "../../format";
import { mailKeys } from "./mailApi";

export type InviteResponse = "ACCEPTED" | "TENTATIVE" | "DECLINED";

export interface InvitePerson {
  name: string | null;
  address: string;
  response: string | null;
}

export interface Invite {
  method: string;
  uid: string;
  sequence: number;
  summary: string | null;
  location: string | null;
  description: string | null;
  start: string | null;
  end: string | null;
  allDay: boolean;
  timeZone: string | null;
  recurring: boolean;
  organizer: InvitePerson | null;
  attendees: InvitePerson[];
  attendeeCount: number;
  cancelled: boolean;
  iAmOrganizer: boolean;
  canRespond: boolean;
  myResponse: InviteResponse | null;
  respondedAt: string | null;
}

export const inviteApi = {
  get: (messageId: string) => api.get<Invite | null>(`/api/v1/mail/messages/${messageId}/invite`),
  respond: (messageId: string, response: InviteResponse, sendReply = true) =>
    api.post<Invite>(`/api/v1/mail/messages/${messageId}/invite/respond`, { response, sendReply }),
};

/**
 * When an event happens, in the reader's time zone: "Thu, 26 Nov 2026, 09:00 – 10:00", the end date only when it
 * differs; all-day events by date (their end is exclusive, RFC 5545 3.6.1).
 */
export function eventWhen(start: string, end: string | null, allDay: boolean, f: UserFormat): string {
  if (allDay) {
    // All-day dates are calendar dates, not instants: format them in UTC so no zone shifts the day.
    const day = (iso: string) => new Intl.DateTimeFormat(f.locale, { timeZone: "UTC", weekday: "short", day: "numeric", month: "short", year: "numeric" }).format(new Date(iso));
    if (!end) return day(start);
    const last = new Date(new Date(end).getTime() - 86_400_000).toISOString();
    return last.slice(0, 10) <= start.slice(0, 10) ? day(start) : `${day(start)} – ${day(last)}`;
  }
  const dayOpts: Intl.DateTimeFormatOptions = { timeZone: f.timeZone, weekday: "short", day: "numeric", month: "short", year: "numeric" };
  const timeOpts: Intl.DateTimeFormatOptions = { timeZone: f.timeZone, timeStyle: "short", hourCycle: f.hourCycle ?? undefined };
  const dayOf = (iso: string) => new Intl.DateTimeFormat("en-CA", { timeZone: f.timeZone }).format(new Date(iso));
  const s = new Date(start);
  const head = `${new Intl.DateTimeFormat(f.locale, dayOpts).format(s)}, ${new Intl.DateTimeFormat(f.locale, timeOpts).format(s)}`;
  if (!end) return head;
  const e = new Date(end);
  const tail = dayOf(start) === dayOf(end)
    ? new Intl.DateTimeFormat(f.locale, timeOpts).format(e)
    : `${new Intl.DateTimeFormat(f.locale, dayOpts).format(e)}, ${new Intl.DateTimeFormat(f.locale, timeOpts).format(e)}`;
  return `${head} – ${tail}`;
}

const icons: Record<InviteResponse, JSX.Element> = { ACCEPTED: <CheckOutlined />, TENTATIVE: <QuestionOutlined />, DECLINED: <CloseOutlined /> };

/** The meeting card above a message that carries an invitation (PROMPT 6.3, iMIP RFC 6047). */
export function InviteCard({ messageId }: { messageId: string }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const key = ["invite", messageId];
  const invite = useQuery({ queryKey: key, queryFn: () => inviteApi.get(messageId) });
  const respond = useMutation({
    mutationFn: (r: InviteResponse) => inviteApi.respond(messageId, r),
    onSuccess: (data, r) => {
      qc.setQueryData(key, data);
      void qc.invalidateQueries({ queryKey: mailKeys.lists });
      message.success(t(`invite.sent.${r}`));
    },
    onError: (e) => message.error(e instanceof ApiError ? e.title : t("errors.actionFailed")),
  });

  if (invite.isPending) return <Spin size="small" />;
  const inv = invite.data;
  if (!inv) return null;

  const who = (p: InvitePerson) => p.name || p.address;
  const replied = inv.method === "REPLY" ? inv.attendees[0] : undefined;
  return (
    <Card className="bm-invite" size="small" role="group" aria-label={t("invite.label")}>
      <Space direction="vertical" size={4} style={{ inlineSize: "100%" }}>
        <Typography.Text strong className="bm-invite-title"><CalendarOutlined /> {inv.summary ?? t("invite.untitled")}</Typography.Text>
        {inv.start && (
          <Typography.Text>
            {eventWhen(inv.start, inv.end, inv.allDay, fmt)}
            {inv.recurring && <Tooltip title={t("invite.recurring")}> <SyncOutlined aria-label={t("invite.recurring")} /></Tooltip>}
          </Typography.Text>
        )}
        {inv.location && <Typography.Text type="secondary"><EnvironmentOutlined /> {inv.location}</Typography.Text>}
        {inv.organizer && (
          <Typography.Text type="secondary">
            {t("invite.organizer", { name: who(inv.organizer) })}
            {inv.attendeeCount > 0 && <> · <TeamOutlined /> {t("invite.attendees", { count: inv.attendeeCount })}</>}
          </Typography.Text>
        )}
        {inv.cancelled && <Alert type="warning" showIcon message={t("invite.cancelled")} />}
        {replied && <Alert type="info" showIcon message={t(`invite.replied.${replied.response ?? "OTHER"}`, { name: who(replied) })} />}
        {inv.canRespond && (
          <Space wrap>
            {(["ACCEPTED", "TENTATIVE", "DECLINED"] as const).map((r) => (
              <Button key={r} icon={icons[r]} type={inv.myResponse === r ? "primary" : "default"} aria-pressed={inv.myResponse === r}
                loading={respond.isPending && respond.variables === r} disabled={respond.isPending} onClick={() => respond.mutate(r)}>
                {t(`invite.answer.${r}`)}
              </Button>
            ))}
          </Space>
        )}
        {inv.myResponse && <Typography.Text type="secondary">{t(`invite.yours.${inv.myResponse}`)}</Typography.Text>}
      </Space>
    </Card>
  );
}
