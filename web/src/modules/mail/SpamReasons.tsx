import { useState } from "react";
import { Alert, Button, Modal, Table, Typography } from "antd";
import { useTranslation } from "react-i18next";
import { parseSpamReport, type SpamHit } from "./mailView";

/** "Why is this message in Spam?" (PROMPT 6.9): the rules that fired, in plain language, with their points. */
export function SpamReasons({ report, inSpam }: { report: string | null; inSpam: boolean }) {
  const { t, i18n } = useTranslation();
  const [open, setOpen] = useState(false);
  const parsed = parseSpamReport(report);
  if (!inSpam) return null;

  const describe = (h: SpamHit) => {
    const key = `spam.rule.${h.code}`;
    return i18n.exists(key) ? t(key, { detail: h.detail ?? "" }) : t("spam.unknownRule", { code: h.code });
  };
  const num = (n: number) => n.toLocaleString(i18n.language, { maximumFractionDigits: 1, signDisplay: "exceptZero" });

  return (
    <>
      <Alert className="bm-banner" type="warning" showIcon message={t("spam.why")}
        action={<Button size="small" onClick={() => setOpen(true)}>{t("spam.title")}</Button>} />
      <Modal open={open} title={t("spam.title")} onCancel={() => setOpen(false)} footer={null} width={640}>
        {!parsed ? (
          <Typography.Paragraph>{t("spam.noReport")}</Typography.Paragraph>
        ) : (
          <>
            <Typography.Paragraph>
              {t("spam.summary", { score: num(parsed.score).replace(/^\+/, ""), threshold: parsed.threshold })}
            </Typography.Paragraph>
            <Table<SpamHit>
              size="small"
              pagination={false}
              rowKey={(h) => h.code}
              dataSource={[...parsed.hits].sort((a, b) => b.score - a.score)}
              columns={[
                { title: t("spam.colReason"), key: "reason", render: (_, h) => describe(h) },
                { title: t("spam.colPoints"), key: "points", align: "end", width: 80, render: (_, h) => <span className={h.score > 0 ? "bm-spam-plus" : "bm-spam-minus"}>{num(h.score)}</span> },
              ]}
            />
            <Typography.Paragraph type="secondary" className="bm-spam-hint">{t("spam.learnHint")}</Typography.Paragraph>
          </>
        )}
      </Modal>
    </>
  );
}
