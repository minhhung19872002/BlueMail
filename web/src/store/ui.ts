import { create } from "zustand";
import { persist } from "zustand/middleware";

export type ThemePreference = "light" | "dark" | "system";
export type Density = "default" | "comfortable" | "compact";
export type ReadingPane = "none" | "right" | "below";

interface UiState {
  theme: ThemePreference;
  density: Density;
  sidebarCollapsed: boolean;
  readingPane: ReadingPane;
  threaded: boolean;
  setReadingPane: (p: ReadingPane) => void;
  setTheme: (t: ThemePreference) => void;
  setDensity: (d: Density) => void;
  toggleSidebar: () => void;
}

// Device-local display preferences. Account-level settings (language, time zone) come from the API.
export const useUi = create<UiState>()(
  persist(
    (set) => ({
      theme: "system",
      density: "default",
      sidebarCollapsed: false,
      readingPane: "none",
      threaded: true,
      setReadingPane: (readingPane) => set({ readingPane }),
      setTheme: (theme) => set({ theme }),
      setDensity: (density) => set({ density }),
      toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),
    }),
    { name: "bm-ui" },
  ),
);
