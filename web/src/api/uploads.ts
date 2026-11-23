import { Upload } from "tus-js-client";
import { accessToken } from "../auth/auth";
import { config } from "../config";

// Resumable uploads (tus 1.0) for compose attachments (PROMPT 6.4): a dropped connection resumes from the last
// acknowledged byte instead of starting over.

export interface UploadHandle {
  abort: () => void;
}

export function uploadFile(
  file: File,
  on: { progress: (sent: number, total: number) => void; done: (uploadId: string) => void; error: (e: Error) => void },
): UploadHandle {
  const up = new Upload(file, {
    endpoint: config().apiBase + "/api/v1/uploads",
    chunkSize: 8 * 1024 * 1024,
    retryDelays: [0, 1000, 3000, 5000, 10000, 20000],
    metadata: { filename: file.name, filetype: file.type || "application/octet-stream" },
    removeFingerprintOnSuccess: true,
    onBeforeRequest: async (req) => {
      const token = await accessToken();
      if (token) req.setHeader("Authorization", `Bearer ${token}`);
    },
    onProgress: on.progress,
    onSuccess: () => {
      const id = (up.url ?? "").split("/").pop() ?? "";
      on.done(id);
    },
    onError: on.error,
  });
  void up.findPreviousUploads().then((previous) => {
    if (previous.length > 0) up.resumeFromPreviousUpload(previous[0]!);
    up.start();
  });
  return { abort: () => void up.abort(true) };
}
