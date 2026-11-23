import { useEffect, useRef, useState } from "react";
import { Alert, Spin } from "antd";
import { useTranslation } from "react-i18next";
import { GlobalWorkerOptions, getDocument } from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";

GlobalWorkerOptions.workerSrc = workerUrl;

const maxPages = 30;

/** PDF attachments rendered to canvases by pdf.js (PROMPT 6.3); no scripts, no forms. */
export function PdfPreview({ url }: { url: string }) {
  const { t } = useTranslation();
  const host = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<"loading" | "ready" | "error">("loading");

  useEffect(() => {
    let cancelled = false;
    // pdf.js 6 has no eval-based font path any more (CVE-2024-4367 class); XFA forms stay off.
    const task = getDocument({ url, enableXfa: false, disableAutoFetch: true });
    (async () => {
      try {
        const pdf = await task.promise;
        const el = host.current;
        if (!el || cancelled) return;
        el.replaceChildren();
        for (let i = 1; i <= Math.min(pdf.numPages, maxPages) && !cancelled; i++) {
          const page = await pdf.getPage(i);
          const viewport = page.getViewport({ scale: 1.25 });
          const canvas = document.createElement("canvas");
          canvas.width = viewport.width;
          canvas.height = viewport.height;
          canvas.className = "bm-pdf-page";
          el.appendChild(canvas);
          await page.render({ canvas, viewport }).promise;
        }
        if (!cancelled) setState("ready");
      } catch {
        if (!cancelled) setState("error");
      }
    })();
    return () => {
      cancelled = true;
      void task.destroy();
    };
  }, [url]);

  return (
    <div className="bm-pdf">
      {state === "loading" && <Spin />}
      {state === "error" && <Alert type="warning" showIcon message={t("mail.previewFailed")} />}
      <div ref={host} />
    </div>
  );
}
