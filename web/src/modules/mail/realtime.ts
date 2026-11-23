import { useEffect, useRef } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { accessToken } from "../../auth/auth";
import { config } from "../../config";
import { getChanges, mailKeys } from "./mailApi";

/**
 * Live mailbox (PROMPT 6.2): the hub only says "mailbox X changed"; the client then asks /mail/changes since the
 * last modseq it knows and refreshes what changed. After a reconnect the same call catches up, so a dropped
 * connection never loses a change and never reloads the whole mailbox.
 */
export function useMailboxLive(): void {
  const qc = useQueryClient();
  const { t } = useTranslation();
  const since = useRef<number | null>(null);
  const tRef = useRef(t);
  tRef.current = t;

  useEffect(() => {
    let disposed = false;
    const conn = new HubConnectionBuilder()
      .withUrl(config().apiBase + "/hubs/mail", { accessTokenFactory: async () => (await accessToken()) ?? "" })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    const catchUp = async (added: boolean) => {
      if (since.current === null) {
        since.current = (await getChanges(Number.MAX_SAFE_INTEGER)).highestModseq;
        return;
      }
      const c = await getChanges(since.current);
      since.current = c.highestModseq;
      await qc.invalidateQueries({ queryKey: mailKeys.lists });
      await qc.invalidateQueries({ queryKey: mailKeys.labels });
      const threads = new Set(c.changed.map((x) => x.threadId));
      for (const id of threads) await qc.invalidateQueries({ queryKey: mailKeys.thread(id) });
      if (c.tooOld || c.removed.length > 0) await qc.invalidateQueries({ queryKey: ["thread"] });
      if (added && document.hidden && "Notification" in window && Notification.permission === "granted") {
        // Content-free: the notification never shows sender or subject on a locked screen.
        new Notification(tRef.current("mail.notifyTitle"), { body: tRef.current("mail.notifyBody"), tag: "bm-new-mail" });
      }
    };

    conn.on("mailboxChanged", (e: { kind: string }) => void catchUp(e.kind === "message.added"));
    conn.onreconnected(async () => {
      await conn.invoke("Subscribe", null);
      await catchUp(false);
    });

    (async () => {
      try {
        await catchUp(false);
        await conn.start();
        if (disposed) return;
        await conn.invoke("Subscribe", null);
      } catch {
        // The list still works without live updates; the next navigation refetches.
      }
    })();

    return () => {
      disposed = true;
      if (conn.state !== HubConnectionState.Disconnected) void conn.stop();
    };
  }, [qc]);
}
