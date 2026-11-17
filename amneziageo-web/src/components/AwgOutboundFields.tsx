import { useState } from "react"
import { complaint } from "@/api/auth"
import type { Obfuscation } from "@/api/configs"
import { useImportOutbound, useOutboundKeys } from "@/api/awgOutbounds"
import type { AwgOutboundDraft } from "@/api/awgOutbounds"
import { ObfuscationFields } from "@/components/Obfuscation"
import { Count, Flag, Line, ListLine, Part } from "@/components/fields"
import { field, label, secondary } from "@/components/styles"
import { useText } from "@/i18n"
import type { TextKey } from "@/i18n"

export function AwgOutboundFields({ draft, setDraft, publicKey, setPublicKey }: {
  draft: AwgOutboundDraft
  setDraft: (draft: AwgOutboundDraft) => void
  publicKey: string
  setPublicKey: (publicKey: string) => void
}) {
  const t = useText()
  const keys = useOutboundKeys()
  const read = useImportOutbound()
  const [text, setText] = useState("")

  function put(change: Partial<AwgOutboundDraft>) {
    setDraft({ ...draft, ...change })
  }
  function twist(change: Partial<Obfuscation>) {
    setDraft({ ...draft, obfuscation: { ...draft.obfuscation, ...change } })
  }

  async function pair() {
    const made = await keys.mutateAsync()
    setDraft({ ...draft, privateKey: made.privateKey })
    setPublicKey(made.publicKey)
  }

  async function take() {
    const found = await read.mutateAsync({ name: draft.name, config: text })
    setDraft({
      ...draft,
      kind: found.kind,
      host: found.host,
      proxy: found.proxy,
      port: found.port,
      privateKey: found.privateKey ?? "",
      peerKey: found.peerKey,
      presharedKey: found.presharedKey ?? "",
      address: found.address,
      dns: found.dns,
      mtu: found.mtu,
      keepalive: found.keepalive,
      obfuscation: found.obfuscation,
    })
    setPublicKey(found.publicKey)
    setText("")
  }

  return <>
    <Part title={t("outbounds.server")}>
      <Line
        id="outbound-host"
        caption={t("outbounds.host")}
        value={draft.host}
        onChange={(value) => put({ host: value })}
      />
      <Count
        id="outbound-port"
        caption={t("outbounds.port")}
        value={draft.port}
        onChange={(value) => put({ port: value })}
      />
      {draft.kind === "ws" && (
        <Line
          id="outbound-proxy"
          caption={t("outbounds.proxy")}
          value={draft.proxy}
          onChange={(value) => put({ proxy: value })}
          wide
        />
      )}
      <ListLine
        id="outbound-address"
        caption={t("outbounds.address")}
        value={draft.address}
        onChange={(address) => put({ address })}
        wide
      />
      <ListLine
        id="outbound-dns"
        caption={t("outbounds.dns")}
        value={draft.dns}
        onChange={(dns) => put({ dns })}
      />
      <Count
        id="outbound-mtu"
        caption={t("outbounds.mtu")}
        value={draft.mtu}
        onChange={(value) => put({ mtu: value })}
      />
      <Count
        id="outbound-keepalive"
        caption={t("outbounds.keepalive")}
        value={draft.keepalive}
        onChange={(value) => put({ keepalive: value })}
      />
      <Flag
        id="outbound-close-private"
        caption={t("outbounds.closePrivate")}
        value={draft.closePrivate}
        onChange={(value) => put({ closePrivate: value })}
      />
    </Part>

    <Part title={t("outbounds.keys")}>
      <Line
        id="outbound-private"
        caption={t("outbounds.private")}
        value={draft.privateKey}
        onChange={(value) => put({ privateKey: value.trim() })}
        wide
      />
      <div className="sm:col-span-2 flex items-end gap-3">
        <div className="min-w-0 flex-1">
          <span className={label}>{t("outbounds.public")}</span>
          <div className={`mt-1 truncate ${field}`}>{publicKey}</div>
        </div>
        <button type="button" onClick={() => void pair()} disabled={keys.isPending} className={secondary}>
          {t("outbounds.newKeys")}
        </button>
      </div>
      <Line
        id="outbound-peer"
        caption={t("outbounds.peerKey")}
        value={draft.peerKey}
        onChange={(value) => put({ peerKey: value.trim() })}
        wide
      />
      <Line
        id="outbound-preshared"
        caption={t("outbounds.preshared")}
        value={draft.presharedKey}
        onChange={(value) => put({ presharedKey: value.trim() })}
        wide
      />
    </Part>

    <Part title={t("outbounds.obfuscation")}>
      <ObfuscationFields id="outbound" cover={draft.obfuscation} onChange={twist} />
    </Part>

    <Part title={t("outbounds.import")}>
      <div className="sm:col-span-2">
        <label className={label} htmlFor="outbound-config">
          {t("outbounds.paste")}
        </label>
        <textarea
          id="outbound-config"
          value={text}
          rows={5}
          onChange={(e) => setText(e.target.value)}
          className={`mt-1 font-mono text-xs ${field}`}
        />
      </div>
      <div className="sm:col-span-2 flex justify-end">
        <button
          type="button"
          onClick={() => void take()}
          disabled={read.isPending || text.trim().length === 0}
          className={secondary}
        >
          {t("outbounds.read")}
        </button>
      </div>
      {read.error !== null && read.error !== undefined && (
        <div className="text-sm text-alarm sm:col-span-2">{t(complaint(read.error) as TextKey)}</div>
      )}
    </Part>

  </>
}
