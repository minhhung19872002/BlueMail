import { useState } from "react";
import { App, Button, Modal, Radio, Space, Tooltip, Typography } from "antd";
import { ClearOutlined } from "@ant-design/icons";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { getBulkJob, mailKeys, startBulkJob } from "./mailApi";
import { rulesApi } from "../settings/rulesApi";

export type SweepMode = "all" | "allAndBlock" | "older";

/** The search a sweep runs over the inbox (exported for tests). */
export function sweepQuery(sender: string, mode: SweepMode): string {
  const from = `from:${/[\s"()]/.test(sender) ? `"${sender.replace(/"/g, "")}"` : sender}`;
  return mode === "older" ? `${from} older_than:10d` : from;
}

/**
 * "Sweep" of Outlook on the web: move every conversation from one sender out of the inbox into Trash, optionally
 * blocking the sender too, or only the ones older than 10 days. Runs as a bulk job (any number of conversations).
 */
export function SweepButton({ sender, inboxLabelId, onDone }: { sender: string; inboxLabelId: string; onDone: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<SweepMode>("all");
  const [busy, setBusy] = useState(false);

  const run = async () => {
    setBusy(true);
    const key = "bm-sweep";
    try {
      if (mode === "allAndBlock") await rulesApi.block(sender);
      let job = await startBulkJob({ action: "trash", scopeLabelId: inboxLabelId, query: sweepQuery(sender, mode) });
      setOpen(false);
      while (job.status === "QUEUED" || job.status === "RUNNING") {
        message.loading({ key, duration: 0, content: job.total > 0 ? t("mail.bulk.running", { done: job.done, total: job.total }) : t("mail.bulk.preparing") });
        await new Promise((r) => setTimeout(r, 1000));
        job = await getBulkJob(job.id);
      }
      if (job.status === "DONE") message.success({ key, content: t("mail.sweep.done", { count: job.done, address: sender }) });
      else message.error({ key, content: t("mail.bulk.failed", { done: job.done, total: job.total }) });
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      onDone();
    } catch (e) {
      message.error({ key, content: e instanceof ApiError ? e.title : t("errors.actionFailed") });
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Tooltip title={t("mail.sweep.button")}>
        <Button type="text" icon={<ClearOutlined />} aria-label={t("mail.sweep.button")} onClick={() => setOpen(true)} />
      </Tooltip>
      <Modal open={open} title={t("mail.sweep.title")} okText={t("mail.sweep.ok")} okButtonProps={{ danger: true, loading: busy }}
        onOk={() => void run()} onCancel={() => setOpen(false)} destroyOnHidden>
        <Typography.Paragraph>{t("mail.sweep.intro", { address: sender })}</Typography.Paragraph>
        <Radio.Group value={mode} onChange={(e) => setMode(e.target.value as SweepMode)}>
          <Space direction="vertical">
            <Radio value="all">{t("mail.sweep.all")}</Radio>
            <Radio value="allAndBlock">{t("mail.sweep.allAndBlock")}</Radio>
            <Radio value="older">{t("mail.sweep.older")}</Radio>
          </Space>
        </Radio.Group>
      </Modal>
    </>
  );
}
