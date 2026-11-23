import { useState } from "react";
import { Button, DatePicker, Dropdown, Modal, Tooltip } from "antd";
import { ClockCircleOutlined } from "@ant-design/icons";
import dayjs, { type Dayjs } from "dayjs";
import { useTranslation } from "react-i18next";
import { useMe, useUserFormat } from "../../session";
import { dateTime } from "../../format";
import { snoozePresets } from "./snooze";

/** "Snooze until…" with Gmail's presets and a date-time picker (PROMPT 6.5). */
export function SnoozeMenu({ onSnooze }: { onSnooze: (until: Date) => void }) {
  const { t } = useTranslation();
  const me = useMe();
  const fmt = useUserFormat();
  const [picking, setPicking] = useState(false);
  const [custom, setCustom] = useState<Dayjs | null>(null);
  const presets = snoozePresets(dayjs(), me.timeZone || "UTC");

  return (
    <>
      <Dropdown trigger={["click"]} menu={{
        items: [
          ...presets.map((p) => ({ key: p.key, label: <span className="bm-snooze-item"><span>{t(`snooze.${p.key}`)}</span><span className="bm-muted">{dateTime(p.at.toISOString(), fmt)}</span></span> })),
          { type: "divider" as const },
          { key: "pick", label: t("snooze.pick") },
        ],
        onClick: ({ key }) => {
          if (key === "pick") setPicking(true);
          else onSnooze(presets.find((p) => p.key === key)!.at.toDate());
        },
      }}>
        <Tooltip title={t("snooze.title")}><Button type="text" icon={<ClockCircleOutlined />} aria-label={t("snooze.title")} data-shortcut="snooze" /></Tooltip>
      </Dropdown>
      <Modal open={picking} title={t("snooze.pick")} onCancel={() => setPicking(false)} okButtonProps={{ disabled: !custom || custom.isBefore(dayjs()) }}
        onOk={() => { if (custom) onSnooze(custom.toDate()); setPicking(false); }}>
        <DatePicker showTime={{ format: "HH:mm", minuteStep: 5 }} format="YYYY-MM-DD HH:mm" value={custom} onChange={setCustom} className="bm-full"
          disabledDate={(d) => d.isBefore(dayjs().startOf("day"))} aria-label={t("snooze.pick")} />
      </Modal>
    </>
  );
}
