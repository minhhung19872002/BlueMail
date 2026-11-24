import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";

/**
 * The mailbox the mail views work on: null = the user's own, else a shared mailbox they were granted. Kept per browser
 * tab (sessionStorage), so two tabs can show two mailboxes.
 */
interface MailboxState {
  mailboxId: string | null;
  setMailbox: (id: string | null) => void;
}

export const useMailbox = create<MailboxState>()(
  persist((set) => ({ mailboxId: null, setMailbox: (mailboxId) => set({ mailboxId }) }), {
    name: "bm-mailbox",
    storage: createJSONStorage(() => sessionStorage),
  }),
);

/** Paths that act on "the mailbox" and so carry the chosen one; signed content URLs and the mailbox list do not. */
const scoped = /^\/api\/v1\/mail\/(?!mailboxes|p\/|img\/)/;

/** Adds ?mailboxId= to a mail API path when a shared mailbox is open. */
export function withMailbox(path: string, mailboxId: string | null = useMailbox.getState().mailboxId): string {
  if (!mailboxId || !scoped.test(path) || /[?&]mailboxId=/.test(path)) return path;
  return path + (path.includes("?") ? "&" : "?") + "mailboxId=" + encodeURIComponent(mailboxId);
}
