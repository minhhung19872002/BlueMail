import { App, Button, Table, Tag, Typography } from "antd";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import type { SessionInfo } from "../../api/types";
import { config } from "../../config";
import { dateTime } from "../../format";
import { useMe, useUserFormat } from "../../session";
import { signOut } from "../../auth/auth";

export function SessionsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const sessions = useQuery({ queryKey: ["sessions"], queryFn: () => api.get<SessionInfo[]>("/api/v1/account/sessions") });
  const revoke = useMutation({
    mutationFn: (s: SessionInfo) => api.delete<boolean>(`/api/v1/account/sessions/${s.id}`),
    onSuccess: async (_, s) => {
      if (s.isCurrent) {
        await signOut();
        return;
      }
      void message.success(t("sessions.revoked"));
      await qc.invalidateQueries({ queryKey: ["sessions"] });
    },
    onError: (e) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")),
  });

  return (
    <section aria-labelledby="sessions-title">
      <Typography.Title level={4} id="sessions-title">{t("sessions.title")}</Typography.Title>
      <Typography.Paragraph>
        {me.twoFactorEnabled ? t("sessions.twoFactorOn") : t("sessions.twoFactorOff")}{" "}
        <a href={`${config().identityUrl}/account/2fa`}>{t("sessions.manageTwoFactor")}</a>
        {" · "}
        <a href={`${config().identityUrl}/account/change-password`}>{t("sessions.changePassword")}</a>
      </Typography.Paragraph>
      <Table<SessionInfo>
        rowKey="id"
        loading={sessions.isPending}
        dataSource={sessions.data ?? []}
        pagination={false}
        scroll={{ x: true }}
        columns={[
          {
            title: t("sessions.device"),
            render: (_, s) => (
              <>
                {s.device ?? t("sessions.unknownDevice")} {s.isCurrent && <Tag>{t("sessions.thisDevice")}</Tag>}
              </>
            ),
          },
          { title: t("sessions.ip"), dataIndex: "ip" },
          { title: t("sessions.signedIn"), render: (_, s) => dateTime(s.createdAt, fmt) },
          { title: t("sessions.lastSeen"), render: (_, s) => dateTime(s.lastSeenAt, fmt) },
          {
            title: <span className="bm-visually-hidden">{t("common.actions")}</span>,
            render: (_, s) => (
              <Button danger size="small" loading={revoke.isPending && revoke.variables?.id === s.id} onClick={() => revoke.mutate(s)}>
                {t("sessions.signOut")}
              </Button>
            ),
          },
        ]}
      />
    </section>
  );
}
