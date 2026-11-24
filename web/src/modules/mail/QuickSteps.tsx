import { useState } from "react";
import { App, Button, Dropdown, Input, Modal, Select, Space, Tooltip, Typography } from "antd";
import { DeleteOutlined, PlusOutlined, ThunderboltOutlined } from "@ant-design/icons";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { LabelSummary } from "../../api/types";
import { useSaveSettings, useSettings, type QuickStep, type QuickStepAction } from "../settings/settingsApi";
import { act, mailKeys, type MailAction } from "./mailApi";

/** Actions a Quick Step may contain (the server keeps the same list: reversible actions only). */
export const quickStepActions = [
  "markRead", "markUnread", "star", "unstar", "important", "notImportant", "archive", "moveToInbox", "trash", "addLabel", "removeLabel", "moveTo", "mute",
] as const;
const needsLabel = (a: string) => a === "addLabel" || a === "removeLabel" || a === "moveTo";
export const MaxSteps = 20;
export const MaxActions = 5;

/** True when running the step takes the conversation out of the list being read (so the reading pane closes). */
export function leavesList(step: QuickStep): boolean {
  return step.actions.some((a) => a.action === "archive" || a.action === "trash" || a.action === "moveTo");
}

/** Runs the actions in order on the conversations; stops at the first failure. */
export async function runQuickStep(step: QuickStep, threadIds: string[]): Promise<void> {
  for (const a of step.actions) {
    await act({ action: a.action as MailAction, threadIds, labelId: a.labelId ?? undefined });
  }
}

/** Outlook "Quick Steps": the thread toolbar menu, with "Manage" to edit the steps. */
export function QuickStepsMenu({ threadIds, labels, onLeft }: { threadIds: string[]; labels: LabelSummary[]; onLeft: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const settings = useSettings();
  const [managing, setManaging] = useState(false);
  const steps = settings?.quickSteps ?? [];

  const run = async (step: QuickStep) => {
    try {
      await runQuickStep(step, threadIds);
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      message.success(t("quickSteps.done", { name: step.name }));
      if (leavesList(step)) onLeft();
      else await qc.invalidateQueries({ queryKey: ["thread"] });
    } catch {
      message.error(t("errors.actionFailed"));
    }
  };

  return (
    <>
      <Dropdown trigger={["click"]} menu={{
        items: [
          ...steps.map((s) => ({ key: s.id, label: s.name })),
          ...(steps.length > 0 ? [{ type: "divider" as const }] : []),
          { key: "__manage", label: t("quickSteps.manage") },
        ],
        onClick: ({ key }) => {
          if (key === "__manage") setManaging(true);
          else if (steps.find((s) => s.id === key)) void run(steps.find((s) => s.id === key)!);
        },
      }}>
        <Tooltip title={t("quickSteps.title")}><Button type="text" icon={<ThunderboltOutlined />} aria-label={t("quickSteps.title")} /></Tooltip>
      </Dropdown>
      {managing && <QuickStepsEditor steps={steps} labels={labels} onClose={() => setManaging(false)} />}
    </>
  );
}

function QuickStepsEditor({ steps, labels, onClose }: { steps: QuickStep[]; labels: LabelSummary[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const save = useSaveSettings();
  const [draft, setDraft] = useState<QuickStep[]>(() => steps.map((s) => ({ ...s, actions: s.actions.map((a) => ({ ...a })) })));
  const [busy, setBusy] = useState(false);
  const userLabels = labels.filter((l) => l.systemKey === null);
  const update = (i: number, s: QuickStep) => setDraft((d) => d.map((x, j) => (j === i ? s : x)));
  const setAction = (i: number, k: number, a: QuickStepAction) =>
    update(i, { ...draft[i]!, actions: draft[i]!.actions.map((x, j) => (j === k ? a : x)) });

  const valid = draft.every((s) => s.name.trim() && s.actions.length > 0 && s.actions.every((a) => !needsLabel(a.action) || a.labelId));
  const submit = async () => {
    setBusy(true);
    try {
      await save({ quickSteps: draft.map((s) => ({ ...s, name: s.name.trim() })) });
      message.success(t("quickSteps.saved"));
      onClose();
    } catch {
      message.error(t("errors.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open title={t("quickSteps.manage")} onCancel={onClose} onOk={() => void submit()} okText={t("quickSteps.save")}
      okButtonProps={{ loading: busy, disabled: !valid }} width={640} destroyOnHidden>
      <Typography.Paragraph type="secondary">{t("quickSteps.intro")}</Typography.Paragraph>
      <Space direction="vertical" style={{ inlineSize: "100%" }} size="middle">
        {draft.map((s, i) => (
          <div key={s.id} className="bm-quickstep">
            <Space.Compact style={{ inlineSize: "100%" }}>
              <Input value={s.name} maxLength={40} placeholder={t("quickSteps.name")} aria-label={t("quickSteps.name")} dir="auto"
                onChange={(e) => update(i, { ...s, name: e.target.value })} />
              <Button danger icon={<DeleteOutlined />} aria-label={t("quickSteps.remove", { name: s.name })}
                onClick={() => setDraft((d) => d.filter((_, j) => j !== i))} />
            </Space.Compact>
            {s.actions.map((a, k) => (
              <Space key={k} wrap className="bm-quickstep-action">
                <Select style={{ minInlineSize: 200 }} value={a.action} aria-label={t("quickSteps.action")}
                  options={quickStepActions.map((x) => ({ value: x, label: t(`quickSteps.actions.${x}`) }))}
                  onChange={(v) => setAction(i, k, { action: v, labelId: needsLabel(v) ? a.labelId : null })} />
                {needsLabel(a.action) && (
                  <Select style={{ minInlineSize: 180 }} value={a.labelId ?? undefined} placeholder={t("quickSteps.label")} aria-label={t("quickSteps.label")}
                    options={userLabels.map((l) => ({ value: l.id, label: l.name }))} onChange={(v) => setAction(i, k, { ...a, labelId: v })} />
                )}
                <Button type="text" icon={<DeleteOutlined />} aria-label={t("quickSteps.removeAction")}
                  onClick={() => update(i, { ...s, actions: s.actions.filter((_, j) => j !== k) })} />
              </Space>
            ))}
            {s.actions.length < MaxActions && (
              <Button size="small" type="link" icon={<PlusOutlined />} onClick={() => update(i, { ...s, actions: [...s.actions, { action: "markRead", labelId: null }] })}>
                {t("quickSteps.addAction")}
              </Button>
            )}
          </div>
        ))}
        {draft.length < MaxSteps && (
          <Button icon={<PlusOutlined />} onClick={() => setDraft((d) => [...d, { id: crypto.randomUUID().replace(/-/g, ""), name: "", actions: [{ action: "markRead", labelId: null }, { action: "archive", labelId: null }] }])}>
            {t("quickSteps.new")}
          </Button>
        )}
      </Space>
    </Modal>
  );
}
