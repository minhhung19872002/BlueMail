import { useState } from "react";
import { App, Button, Dropdown, Input, Modal } from "antd";
import { AppstoreOutlined } from "@ant-design/icons";
import { useTranslation } from "react-i18next";
import { useSaveSettings, useSettings, type CalendarGroup } from "../settings/settingsApi";

/**
 * Outlook "Calendar groups": open a saved set of shared calendars at once, save the calendars shown now as a group,
 * or remove a group. Members who no longer share their calendar are simply not shown.
 */
export function CalendarGroups({ shown, available, onOpen }: { shown: string[]; available: string[]; onOpen: (members: string[]) => void }) {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const settings = useSettings();
  const save = useSaveSettings();
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState("");
  const groups = settings?.calendarGroups ?? [];

  const store = async (next: CalendarGroup[]) => {
    try {
      await save({ calendarGroups: next });
    } catch {
      message.error(t("errors.actionFailed"));
    }
  };

  return (
    <>
      <Dropdown trigger={["click"]} menu={{
        items: [
          ...groups.map((g) => ({ key: g.id, label: `${g.name} (${g.members.filter((m) => available.includes(m)).length})` })),
          ...(groups.length ? [{ type: "divider" as const }] : []),
          { key: "__save", label: t("calendar.groups.save"), disabled: shown.length === 0 },
          ...(groups.length ? [{ key: "__delete", label: t("calendar.groups.delete"), children: groups.map((g) => ({ key: `del:${g.id}`, label: g.name, danger: true })) }] : []),
        ],
        onClick: ({ key }) => {
          if (key === "__save") { setName(""); setNaming(true); return; }
          if (key.startsWith("del:")) {
            const g = groups.find((x) => x.id === key.slice(4));
            if (g) {
              modal.confirm({
                title: t("calendar.groups.deleteOne", { name: g.name }),
                okButtonProps: { danger: true },
                onOk: () => store(groups.filter((x) => x.id !== g.id)),
              });
            }
            return;
          }
          const g = groups.find((x) => x.id === key);
          if (g) onOpen(g.members.filter((m) => available.includes(m)));
        },
      }}>
        <Button icon={<AppstoreOutlined />}>{t("calendar.groups.button")}</Button>
      </Dropdown>
      {naming && (
        <Modal open title={t("calendar.groups.save")} onCancel={() => setNaming(false)} okButtonProps={{ disabled: !name.trim() }}
          onOk={async () => {
            await store([...groups.filter((g) => g.name !== name.trim()), { id: crypto.randomUUID().replace(/-/g, ""), name: name.trim(), members: shown }]);
            setNaming(false);
          }}>
          <Input autoFocus value={name} maxLength={40} onChange={(e) => setName(e.target.value)} aria-label={t("calendar.groups.name")}
            placeholder={t("calendar.groups.name")} dir="auto" />
        </Modal>
      )}
    </>
  );
}
