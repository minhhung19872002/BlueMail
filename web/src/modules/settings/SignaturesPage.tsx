import { useState } from "react";
import { App, Button, Card, Checkbox, Empty, Input, List, Modal, Space, Tabs, Typography } from "antd";
import { DeleteOutlined, EditOutlined, PlusOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { composeApi, type Signature, type Template } from "../compose/composeApi";
import { MailEditor } from "../compose/LazyMailEditor";

/** Signatures and templates (PROMPT 6.4): several signatures, defaults for new mail and replies; reusable templates. */
export function SignaturesPage() {
  const { t } = useTranslation();
  return (
    <section aria-labelledby="sig-title">
      <Typography.Title level={4} id="sig-title">{t("signatures.title")}</Typography.Title>
      <Tabs items={[
        { key: "sig", label: t("signatures.signatures"), children: <Signatures /> },
        { key: "tpl", label: t("signatures.templates"), children: <Templates /> },
      ]} />
    </section>
  );
}

const noImages = async () => null;

function Signatures() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["signatures"], queryFn: composeApi.signatures });
  const [edit, setEdit] = useState<(Omit<Signature, "id"> & { id: string | null }) | null>(null);

  const save = async () => {
    if (!edit) return;
    try {
      await composeApi.saveSignature(edit);
      setEdit(null);
      await qc.invalidateQueries({ queryKey: ["signatures"] });
      message.success(t("signatures.saved"));
    } catch {
      message.error(t("signatures.nameTaken"));
    }
  };

  return (
    <>
      <Button icon={<PlusOutlined />} onClick={() => setEdit({ id: null, name: "", html: "", isDefaultNew: (q.data?.length ?? 0) === 0, isDefaultReply: (q.data?.length ?? 0) === 0, fromAddress: null })}>
        {t("signatures.new")}
      </Button>
      {q.data && q.data.length === 0 && <Empty description={t("signatures.none")} />}
      <List className="bm-settings-list" dataSource={q.data ?? []} renderItem={(s) => (
        <List.Item actions={[
          <Button key="e" type="text" icon={<EditOutlined />} aria-label={t("signatures.edit", { name: s.name })} onClick={() => setEdit(s)} />,
          <Button key="d" type="text" danger icon={<DeleteOutlined />} aria-label={t("signatures.delete", { name: s.name })}
            onClick={() => modal.confirm({ title: t("signatures.delete", { name: s.name }), okButtonProps: { danger: true },
              onOk: async () => { await composeApi.deleteSignature(s.id); await qc.invalidateQueries({ queryKey: ["signatures"] }); } })} />,
        ]}>
          <List.Item.Meta title={s.name} description={[s.isDefaultNew && t("signatures.defaultNew"), s.isDefaultReply && t("signatures.defaultReply")].filter(Boolean).join(" · ")} />
        </List.Item>
      )} />
      {edit && (
        <Modal open width="min(760px, 96vw)" title={edit.id ? t("signatures.editTitle") : t("signatures.new")} onCancel={() => setEdit(null)} onOk={() => void save()}
          okButtonProps={{ disabled: !edit.name.trim() }}>
          <Space direction="vertical" className="bm-full">
            <Input value={edit.name} maxLength={200} placeholder={t("signatures.name")} aria-label={t("signatures.name")} onChange={(e) => setEdit({ ...edit, name: e.target.value })} />
            <Card size="small">
              <MailEditor initialHtml={edit.html} onChange={(html) => setEdit((x) => (x ? { ...x, html } : x))} onImage={noImages} ariaLabel={t("signatures.content")} />
            </Card>
            <Checkbox checked={edit.isDefaultNew} onChange={(e) => setEdit({ ...edit, isDefaultNew: e.target.checked })}>{t("signatures.defaultNew")}</Checkbox>
            <Checkbox checked={edit.isDefaultReply} onChange={(e) => setEdit({ ...edit, isDefaultReply: e.target.checked })}>{t("signatures.defaultReply")}</Checkbox>
          </Space>
        </Modal>
      )}
    </>
  );
}

function Templates() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["templates"], queryFn: composeApi.templates });
  const [edit, setEdit] = useState<(Omit<Template, "id"> & { id: string | null }) | null>(null);

  const save = async () => {
    if (!edit) return;
    try {
      await composeApi.saveTemplate(edit);
      setEdit(null);
      await qc.invalidateQueries({ queryKey: ["templates"] });
      message.success(t("signatures.saved"));
    } catch {
      message.error(t("signatures.nameTaken"));
    }
  };

  return (
    <>
      <Button icon={<PlusOutlined />} onClick={() => setEdit({ id: null, name: "", subject: "", html: "" })}>{t("signatures.newTemplate")}</Button>
      {q.data && q.data.length === 0 && <Empty description={t("signatures.noTemplates")} />}
      <List className="bm-settings-list" dataSource={q.data ?? []} renderItem={(tp) => (
        <List.Item actions={[
          <Button key="e" type="text" icon={<EditOutlined />} aria-label={t("signatures.edit", { name: tp.name })} onClick={() => setEdit(tp)} />,
          <Button key="d" type="text" danger icon={<DeleteOutlined />} aria-label={t("signatures.delete", { name: tp.name })}
            onClick={() => modal.confirm({ title: t("signatures.delete", { name: tp.name }), okButtonProps: { danger: true },
              onOk: async () => { await composeApi.deleteTemplate(tp.id); await qc.invalidateQueries({ queryKey: ["templates"] }); } })} />,
        ]}>
          <List.Item.Meta title={tp.name} description={tp.subject} />
        </List.Item>
      )} />
      {edit && (
        <Modal open width="min(760px, 96vw)" title={edit.id ? t("signatures.editTemplate") : t("signatures.newTemplate")} onCancel={() => setEdit(null)} onOk={() => void save()}
          okButtonProps={{ disabled: !edit.name.trim() }}>
          <Space direction="vertical" className="bm-full">
            <Input value={edit.name} maxLength={200} placeholder={t("signatures.name")} aria-label={t("signatures.name")} onChange={(e) => setEdit({ ...edit, name: e.target.value })} />
            <Input value={edit.subject} maxLength={998} placeholder={t("compose.subject")} aria-label={t("compose.subject")} onChange={(e) => setEdit({ ...edit, subject: e.target.value })} />
            <Card size="small">
              <MailEditor initialHtml={edit.html} onChange={(html) => setEdit((x) => (x ? { ...x, html } : x))} onImage={noImages} ariaLabel={t("signatures.content")} />
            </Card>
          </Space>
        </Modal>
      )}
    </>
  );
}
