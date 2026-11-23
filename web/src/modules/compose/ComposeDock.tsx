import { ComposeWindow } from "./ComposeWindow";
import { useCompose } from "./composeStore";

/** Compose windows float at the bottom end of the screen, side by side (PROMPT 6.4). */
export function ComposeDock() {
  const windows = useCompose((s) => s.windows);
  if (windows.length === 0) return null;
  return (
    <div className="bm-compose-dock">
      {windows.map((w) => <ComposeWindow key={w.key} w={w} />)}
    </div>
  );
}
