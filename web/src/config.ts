// Runtime configuration (served as /config.json by the web container, so one build fits every install).
export interface RuntimeConfig {
  identityUrl: string;
  apiBase: string;
}

let current: RuntimeConfig | null = null;

export async function loadConfig(): Promise<RuntimeConfig> {
  if (current) return current;
  const res = await fetch("/config.json", { cache: "no-store" });
  if (!res.ok) throw new Error(`config.json: HTTP ${res.status}`);
  current = (await res.json()) as RuntimeConfig;
  return current;
}

export function config(): RuntimeConfig {
  if (!current) throw new Error("Configuration not loaded");
  return current;
}
