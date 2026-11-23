import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Modal, Table } from "antd";
import { useTranslation } from "react-i18next";
import { useCompose } from "../compose/composeStore";
import { helpRows, isTyping, shortcutEvent, ShortcutReader, type Command } from "./shortcuts";

/** Calls the handlers for the commands a component owns while it is mounted. */
export function useShortcuts(handlers: Partial<Record<Command, () => void>>, enabled = true) {
  const ref = useRef(handlers);
  ref.current = handlers;
  useEffect(() => {
    if (!enabled) return;
    const on = (e: Event) => {
      const cmd = (e as CustomEvent<Command>).detail;
      const h = ref.current[cmd];
      if (h) {
        e.preventDefault(); // tells the host that someone handled it
        h();
      }
    };
    window.addEventListener(shortcutEvent, on);
    return () => window.removeEventListener(shortcutEvent, on);
  }, [enabled]);
}

/** Global keyboard shortcuts (PROMPT 6.12), on when the user enabled them in Settings. */
export function ShortcutHost({ enabled }: { enabled: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const openCompose = useCompose((s) => s.open);
  const [help, setHelp] = useState(false);

  useEffect(() => {
    if (!enabled) return;
    const reader = new ShortcutReader();
    const onKey = (e: KeyboardEvent) => {
      if (isTyping(e.target)) return;
      const cmd = reader.read(e.key, { ctrl: e.ctrlKey, alt: e.altKey, meta: e.metaKey });
      if (!cmd) return;
      const shell: Partial<Record<Command, () => void>> = {
        compose: () => openCompose({}),
        search: () => document.querySelector<HTMLInputElement>("form[role=search] input")?.focus(),
        help: () => setHelp(true),
        goInbox: () => navigate("/mail/inbox"),
        goStarred: () => navigate("/mail/starred"),
        goSent: () => navigate("/mail/sent"),
        goDrafts: () => navigate("/mail/drafts"),
        goAll: () => navigate("/mail/all"),
      };
      if (shell[cmd]) {
        e.preventDefault();
        shell[cmd]!();
        return;
      }
      // Everything else belongs to the list or the reading pane, whichever is listening.
      const ev = new CustomEvent<Command>(shortcutEvent, { detail: cmd, cancelable: true });
      if (!window.dispatchEvent(ev)) e.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [enabled, navigate, openCompose]);

  return (
    <Modal open={help} title={t("shortcuts.title")} footer={null} onCancel={() => setHelp(false)} width="min(640px, 96vw)">
      <Table size="small" pagination={false} rowKey="keys" dataSource={helpRows}
        columns={[
          { title: t("shortcuts.key"), dataIndex: "keys", width: 120, render: (k: string) => <kbd>{k}</kbd> },
          { title: t("shortcuts.action"), dataIndex: "command", render: (c: Command) => t(`shortcuts.cmd.${c}`) },
        ]} />
    </Modal>
  );
}
