import { lazy, Suspense, type ComponentProps } from "react";
import { Skeleton } from "antd";

// The rich text editor (tiptap + ProseMirror) is the largest part of the app: it loads the first time a compose
// window, signature or vacation editor opens, with a placeholder in its own place instead of blanking the page.
const Editor = lazy(() => import("./MailEditor").then((m) => ({ default: m.MailEditor })));

export function MailEditor(props: ComponentProps<typeof Editor>) {
  return (
    <Suspense fallback={<Skeleton active paragraph={{ rows: 4 }} />}>
      <Editor {...props} />
    </Suspense>
  );
}
