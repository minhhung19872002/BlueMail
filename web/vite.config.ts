import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Dev server: the API and BlueID run as separate processes; /api is proxied so the SPA calls same-origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: process.env.BM_DEV_API ?? "http://localhost:5080", changeOrigin: true },
      "/hubs": { target: process.env.BM_DEV_API ?? "http://localhost:5080", changeOrigin: true, ws: true },
      "/hangfire": { target: process.env.BM_DEV_API ?? "http://localhost:5080", changeOrigin: true },
    },
  },
  build: { sourcemap: true, target: "es2022" },
  test: { environment: "jsdom", include: ["src/**/*.test.ts", "src/**/*.test.tsx"] },
});
