import { useEffect, useState } from "react";
import { Button, Divider, Dropdown, Input, Popover, Space, Tooltip } from "antd";
import {
  AlignCenterOutlined, AlignLeftOutlined, AlignRightOutlined, BoldOutlined, ClearOutlined, FontColorsOutlined, FontSizeOutlined,
  ItalicOutlined, LinkOutlined, OrderedListOutlined, PictureOutlined, StrikethroughOutlined, TableOutlined, UnderlineOutlined,
  UnorderedListOutlined, MenuFoldOutlined, MenuUnfoldOutlined,
} from "@ant-design/icons";
import { EditorContent, useEditor, type Editor } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import TextAlign from "@tiptap/extension-text-align";
import { TextStyleKit } from "@tiptap/extension-text-style";
import Image from "@tiptap/extension-image";
import { TableKit } from "@tiptap/extension-table";
import { Placeholder } from "@tiptap/extensions";
import { useTranslation } from "react-i18next";
import { fontFamilies, fontSizes, textColors } from "./editorPalette";

/** Image node that remembers which upload it came from (data-upload), so the stored HTML can use cid: references. */
const UploadImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      "data-upload": { default: null, parseHTML: (el) => el.getAttribute("data-upload"), renderHTML: (a) => (a["data-upload"] ? { "data-upload": a["data-upload"] } : {}) },
    };
  },
});

interface Props {
  initialHtml: string;
  onChange: (html: string) => void;
  /** Uploads a pasted/dropped/picked image; returns the upload id and a displayable URL. */
  onImage: (file: File) => Promise<{ id: string; url: string } | null>;
  onReady?: (editor: Editor) => void;
  ariaLabel: string;
}

/** The message editor (PROMPT 6.4): TipTap with only free extensions; the browser's spell checker stays on. */
export function MailEditor({ initialHtml, onChange, onImage, onReady, ariaLabel }: Props) {
  const { t } = useTranslation();
  const editor = useEditor({
    extensions: [
      StarterKit.configure({ link: { openOnClick: false, autolink: true, protocols: ["mailto", "tel"], defaultProtocol: "https" } }),
      TextAlign.configure({ types: ["heading", "paragraph"] }),
      TextStyleKit,
      UploadImage.configure({ inline: true, allowBase64: false }),
      TableKit.configure({ table: { resizable: false } }),
      Placeholder.configure({ placeholder: t("compose.bodyPlaceholder") }),
    ],
    content: initialHtml,
    editorProps: {
      attributes: { class: "bm-editor", spellcheck: "true", "aria-label": ariaLabel, role: "textbox", "aria-multiline": "true" },
      handlePaste: (_view, event) => insertImages(event.clipboardData?.files),
      handleDrop: (_view, event) => insertImages((event as DragEvent).dataTransfer?.files),
    },
    onUpdate: ({ editor: e }) => onChange(e.getHTML()),
  });

  function insertImages(files: FileList | null | undefined): boolean {
    const images = [...(files ?? [])].filter((f) => f.type.startsWith("image/"));
    if (images.length === 0 || !editor) return false;
    for (const f of images) {
      void onImage(f).then((r) => {
        if (r) editor.chain().focus().setImage({ src: r.url, alt: f.name }).updateAttributes("image", { "data-upload": r.id }).run();
      });
    }
    return true;
  }

  useEffect(() => {
    if (editor && onReady) onReady(editor);
  }, [editor, onReady]);

  if (!editor) return null;
  return (
    <div className="bm-editor-wrap">
      <EditorContent editor={editor} />
      <Toolbar editor={editor} onPickImage={(files) => insertImages(files)} />
    </div>
  );
}

function Toolbar({ editor, onPickImage }: { editor: Editor; onPickImage: (f: FileList | null) => void }) {
  const { t } = useTranslation();
  const [link, setLink] = useState("");
  const [, force] = useState(0);
  useEffect(() => {
    const h = () => force((n) => n + 1);
    editor.on("selectionUpdate", h);
    editor.on("transaction", h);
    return () => {
      editor.off("selectionUpdate", h);
      editor.off("transaction", h);
    };
  }, [editor]);

  const b = (label: string, icon: React.ReactNode, active: boolean, run: () => void) => (
    <Tooltip title={label}>
      <Button size="small" type={active ? "primary" : "text"} icon={icon} aria-label={label} aria-pressed={active} onClick={run} />
    </Tooltip>
  );
  const c = () => editor.chain().focus();

  return (
    <div className="bm-editor-toolbar" role="toolbar" aria-label={t("compose.formatting")}>
      <Space size={0} wrap>
        <Dropdown trigger={["click"]} menu={{
          items: fontFamilies.map((f) => ({ key: f.key, label: t(`compose.font.${f.key}`) })),
          onClick: ({ key }) => c().setFontFamily(fontFamilies.find((f) => f.key === key)!.css).run(),
        }}>
          <Button size="small" type="text" aria-label={t("compose.fontFamily")}>{t("compose.fontFamily")}</Button>
        </Dropdown>
        <Dropdown trigger={["click"]} menu={{
          items: fontSizes.map((f) => ({ key: f.key, label: t(`compose.size.${f.key}`) })),
          onClick: ({ key }) => {
            const size = fontSizes.find((f) => f.key === key)!.css;
            if (size) c().setFontSize(size).run();
            else c().unsetFontSize().run();
          },
        }}>
          <Button size="small" type="text" icon={<FontSizeOutlined />} aria-label={t("compose.fontSize")} />
        </Dropdown>
        <Divider type="vertical" />
        {b(t("compose.bold"), <BoldOutlined />, editor.isActive("bold"), () => c().toggleBold().run())}
        {b(t("compose.italic"), <ItalicOutlined />, editor.isActive("italic"), () => c().toggleItalic().run())}
        {b(t("compose.underline"), <UnderlineOutlined />, editor.isActive("underline"), () => c().toggleUnderline().run())}
        {b(t("compose.strike"), <StrikethroughOutlined />, editor.isActive("strike"), () => c().toggleStrike().run())}
        <Popover trigger="click" content={
          <div className="bm-palette" role="radiogroup" aria-label={t("compose.textColor")}>
            {textColors.map((col, i) => (
              <button key={col} type="button" className="bm-swatch" style={{ background: col }} aria-label={t("mail.colorN", { n: i + 1 })}
                onClick={() => c().setColor(col).run()} />
            ))}
          </div>
        }>
          <Button size="small" type="text" icon={<FontColorsOutlined />} aria-label={t("compose.textColor")} />
        </Popover>
        <Divider type="vertical" />
        {b(t("compose.alignStart"), <AlignLeftOutlined />, editor.isActive({ textAlign: "left" }), () => c().setTextAlign("left").run())}
        {b(t("compose.alignCenter"), <AlignCenterOutlined />, editor.isActive({ textAlign: "center" }), () => c().setTextAlign("center").run())}
        {b(t("compose.alignEnd"), <AlignRightOutlined />, editor.isActive({ textAlign: "right" }), () => c().setTextAlign("right").run())}
        {b(t("compose.bulletList"), <UnorderedListOutlined />, editor.isActive("bulletList"), () => c().toggleBulletList().run())}
        {b(t("compose.orderedList"), <OrderedListOutlined />, editor.isActive("orderedList"), () => c().toggleOrderedList().run())}
        {b(t("compose.indent"), <MenuUnfoldOutlined className="bm-flip-rtl" />, false, () => c().sinkListItem("listItem").run())}
        {b(t("compose.outdent"), <MenuFoldOutlined className="bm-flip-rtl" />, false, () => c().liftListItem("listItem").run())}
        {b(t("compose.quote"), <span aria-hidden className="bm-quote-icon">❝</span>, editor.isActive("blockquote"), () => c().toggleBlockquote().run())}
        <Popover trigger="click" content={
          <Space.Compact>
            <Input size="small" value={link} onChange={(e) => setLink(e.target.value)} placeholder={t("compose.linkPlaceholder")} aria-label={t("compose.link")} />
            <Button size="small" type="primary" onClick={() => {
              const href = /^(https?:|mailto:|tel:)/i.test(link) ? link : `https://${link}`;
              if (link.trim()) c().extendMarkRange("link").setLink({ href }).run();
              else c().unsetLink().run();
              setLink("");
            }}>{t("compose.apply")}</Button>
          </Space.Compact>
        }>
          {b(t("compose.link"), <LinkOutlined />, editor.isActive("link"), () => setLink(editor.getAttributes("link").href ?? ""))}
        </Popover>
        <Tooltip title={t("compose.image")}>
          <Button size="small" type="text" icon={<PictureOutlined />} aria-label={t("compose.image")} onClick={() => {
            const input = document.createElement("input");
            input.type = "file";
            input.accept = "image/png,image/jpeg,image/gif,image/webp";
            input.multiple = true;
            input.onchange = () => onPickImage(input.files);
            input.click();
          }} />
        </Tooltip>
        {b(t("compose.table"), <TableOutlined />, editor.isActive("table"), () => c().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run())}
        {b(t("compose.clearFormatting"), <ClearOutlined />, false, () => c().unsetAllMarks().clearNodes().run())}
      </Space>
    </div>
  );
}
