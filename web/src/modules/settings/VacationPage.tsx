import { useEffect, useState } from "react";
import { Alert, App, Button, Card, Checkbox, DatePicker, Input, Space, Switch, Typography } from "antd";
import dayjs, { type Dayjs } from "dayjs";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { MailEditor } from "../compose/LazyMailEditor";
import { rulesApi, rulesKeys, type Vacation } from "./rulesApi";

const noImages = async () => null;

/** Out-of-office reply (PROMPT 6.8, RFC 3834): sent once per sender every few days, never to lists or automatic mail. */
export function VacationPage() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: rulesKeys.vacation, queryFn: rulesApi.vacation });
  const [v, setV] = useState<Vacation | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (q.data && !v) setV(q.data); }, [q.data, v]);
  if (!v) return null;

  const save = async () => {
    setError(null);
    try {
      await rulesApi.saveVacation(v);
      await qc.invalidateQueries({ queryKey: rulesKeys.vacation });
      message.success(t("vacation.saved"));
    } catch (e) {
      setError(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };
  const day = (s: string | null) => (s ? dayjs(s) : null);
  const iso = (d: Dayjs | null, endOfDay: boolean) => (d ? (endOfDay ? d.endOf("day") : d.startOf("day")).toISOString() : null);

  return (
    <section aria-labelledby="vacation-title" className="bm-settings-form">
      <Typography.Title level={4} id="vacation-title">{t("vacation.title")}</Typography.Title>
      <Space direction="vertical" className="bm-full">
        <Switch checked={v.enabled} onChange={(enabled) => setV({ ...v, enabled })} checkedChildren={t("vacation.on")} unCheckedChildren={t("vacation.off")}
          aria-label={t("vacation.title")} />
        <Space wrap>
          <span>{t("vacation.from")}</span>
          <DatePicker value={day(v.startAt)} onChange={(d) => setV({ ...v, startAt: iso(d, false) })} aria-label={t("vacation.from")} />
          <span>{t("vacation.until")}</span>
          <DatePicker value={day(v.endAt)} onChange={(d) => setV({ ...v, endAt: iso(d, true) })} aria-label={t("vacation.until")} />
        </Space>
        <Input value={v.subject} maxLength={500} placeholder={t("vacation.subject")} aria-label={t("vacation.subject")} onChange={(e) => setV({ ...v, subject: e.target.value })} />
        <Card size="small">
          <MailEditor initialHtml={v.html} onChange={(html) => setV((x) => (x ? { ...x, html } : x))} onImage={noImages} ariaLabel={t("vacation.message")} />
        </Card>
        <Checkbox checked={v.onlyContacts} onChange={(e) => setV({ ...v, onlyContacts: e.target.checked })}>{t("vacation.onlyContacts")}</Checkbox>
        <Checkbox checked={v.onlyOrganization} onChange={(e) => setV({ ...v, onlyOrganization: e.target.checked })}>{t("vacation.onlyOrganization")}</Checkbox>
        <Typography.Text type="secondary">{t("vacation.rules")}</Typography.Text>
        {error && <Alert type="error" showIcon message={error} />}
        <Button type="primary" onClick={() => void save()}>{t("vacation.save")}</Button>
      </Space>
    </section>
  );
}
