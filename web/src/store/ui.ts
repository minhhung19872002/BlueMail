import { create } from "zustand";
import { persist } from "zustand/middleware";

export type ThemePreference = "light" | "dark" | "system";
export type Density = "default" | "comfortable" | "compact";

interface UiState {
  theme: ThemePreference;
  density: Density;
  sidebarCollapsed: boolean;
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
      setTheme: (theme) => set({ theme }),
      setDensity: (density) => set({ density }),
      toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),
    }),
    { name: "bm-ui" },
  ),
);
