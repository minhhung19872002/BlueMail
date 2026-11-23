import { create } from "zustand";
import type { DraftState } from "./composeApi";

/** One compose window (PROMPT 6.4: several can be open; minimise, maximise). */
export interface ComposeWindowState {
  key: string;
  draftId: string | null;
  initial: DraftState | null;
  minimized: boolean;
  maximized: boolean;
}

interface ComposeStore {
  windows: ComposeWindowState[];
  open: (w: { draftId?: string | null; initial?: DraftState | null }) => void;
  close: (key: string) => void;
  update: (key: string, patch: Partial<ComposeWindowState>) => void;
}

const maxWindows = 4;

export const useCompose = create<ComposeStore>()((set) => ({
  windows: [],
  open: ({ draftId = null, initial = null }) =>
    set((s) => {
      const existing = draftId ? s.windows.find((w) => w.draftId === draftId) : undefined;
      if (existing) return { windows: s.windows.map((w) => (w === existing ? { ...w, minimized: false } : w)) };
      const w: ComposeWindowState = { key: crypto.randomUUID(), draftId, initial, minimized: false, maximized: false };
      // The oldest window collapses when too many are open, like Gmail.
      const windows = [...s.windows.map((x) => ({ ...x, minimized: s.windows.length + 1 > maxWindows ? true : x.minimized })), w];
      return { windows };
    }),
  close: (key) => set((s) => ({ windows: s.windows.filter((w) => w.key !== key) })),
  update: (key, patch) => set((s) => ({ windows: s.windows.map((w) => (w.key === key ? { ...w, ...patch } : w)) })),
}));
