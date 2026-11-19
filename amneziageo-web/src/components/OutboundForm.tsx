import { useState } from "react"
import { complaint } from "@/api/auth"
import { draftOf, reuseDraft } from "@/api/outbounds"
import { client } from "@/api/client"
import type { Outbound } from "@/api/outbounds"
import type { OutboundDraftBase } from "@/api/outboundContracts"
import type { OutboundDraft, OutboundKind } from "@/api/outbounds"
import { WayUse } from "@/components/Holders"
import { AwgOutboundFields } from "@/components/AwgOutboundFields"
import { InterfaceOutboundFields } from "@/components/InterfaceOutboundFields"
import { Count, Line, Part, Switch } from "@/components/fields"
import { danger, field, footer, label, primary, secondary } from "@/components/styles"
import { useText } from "@/i18n"
import type { TextKey } from "@/i18n"
import { same } from "@/store/draftSlice"

type HeldDraft = {
  draft: OutboundDraft
  publicKey: string
}

export function OutboundForm({
  start,
  source,
  self,
  pending,
  error,
  onSave,
  onClose,
  onRemove,
}: {
  start: OutboundDraft
  source?: Outbound
  self?: string
  pending: boolean
  error: unknown
  onSave: (draft: OutboundDraft) => void
  onClose: () => void
  onRemove?: () => void
}) {
  const t = useText()
  const [form, setForm] = useState<HeldDraft & {
    drafts: Partial<Record<OutboundKind, HeldDraft>>
  }>(() => ({
    draft: start,
    publicKey: source !== undefined && (source.kind === "wg" || source.kind === "ws") ? source.publicKey : "",
    drafts: {},
  }))
  const { draft, publicKey } = form
  const [kindPending, setKindPending] = useState(false)
  const [kindError, setKindError] = useState<unknown>(null)
  const edited = !same(draft, start)

  function setDraft(next: OutboundDraft) {
    setForm((current) => ({ ...current, draft: next }))
  }

  function setPublicKey(next: string) {
    setForm((current) => ({ ...current, publicKey: next }))
  }

  function put(change: Partial<Omit<OutboundDraftBase, "kind">>) {
    setForm((current) => ({ ...current, draft: { ...current.draft, ...change } }))
  }

  function switchDraft(next: OutboundDraft, nextPublicKey: string) {
    setForm((current) => ({
      draft: {
        ...next,
        name: current.draft.name,
        isEnabled: current.draft.isEnabled,
        dns: current.draft.dns,
        probe: current.draft.probe,
        probeEvery: current.draft.probeEvery,
        closePrivate: current.draft.closePrivate,
        rules: current.draft.rules,
        balancers: current.draft.balancers,
      },
      publicKey: nextPublicKey,
      drafts: {
        ...current.drafts,
        [current.draft.kind]: { draft: current.draft, publicKey: current.publicKey },
      },
    }))
  }

  async function changeKind(kind: OutboundKind) {
    const held = form.drafts[kind]
    const carried = held?.draft ?? reuseDraft(draft, kind)

    if (carried !== null) {
      switchDraft(carried, held?.publicKey ?? publicKey)
      setKindError(null)
      return
    }
    setKindPending(true)
    try {
      const response = await client.get<Outbound>(
        `/outbounds/draft?name=${encodeURIComponent(draft.name)}&kind=${kind}`,
      )
      const fresh = draftOf(response.data)

      switchDraft(fresh, response.data.kind === "wg" || response.data.kind === "ws" ? response.data.publicKey : "")
      setKindError(null)
    } catch (failure) {
      setKindError(failure)
    } finally {
      setKindPending(false)
    }
  }

  return (
    <div className="mt-4 flex flex-col gap-4">
      <Part title={t("outbounds.settings")}>
        <div className="sm:col-span-2">
          <Switch
            id="outbound-enabled"
            caption={t("outbounds.enabled")}
            value={draft.isEnabled}
            onChange={(value) => put({ isEnabled: value })}
          />
        </div>
        <Line
          id="outbound-name"
          caption={t("outbounds.name")}
          value={draft.name}
          onChange={(value) => put({ name: value })}
        />
        <div>
          <label className={label} htmlFor="outbound-kind">
            {t("outbounds.kind")}
          </label>
          <select
            id="outbound-kind"
            disabled={pending || kindPending}
            value={draft.kind}
            onChange={(e) => void changeKind(e.target.value as OutboundKind)}
            className={`mt-1 ${field}`}
          >
            <option value="local">{t("outbounds.kindLocal")}</option>
            <option value="interface">{t("outbounds.kindInterface")}</option>
            <option value="wg">{t("outbounds.kindWg")}</option>
            <option value="ws">{t("outbounds.kindWs")}</option>
          </select>
        </div>
      </Part>

      {self !== undefined && (
        <WayUse
          name={self}
          rules={draft.rules}
          balancers={draft.balancers}
          onRules={(rules) => put({ rules })}
          onBalancers={(balancers) => put({ balancers })}
        />
      )}

      <Part title={t("outbounds.probe")}>
        <Line
          id="outbound-probe"
          caption={t("outbounds.probeServer")}
          value={draft.probe}
          onChange={(value) => put({ probe: value })}
        />
        <Count
          id="outbound-probe-every"
          caption={t("outbounds.probeEvery")}
          value={draft.probeEvery}
          onChange={(value) => put({ probeEvery: value })}
        />
      </Part>

      {draft.kind === "interface" && <InterfaceOutboundFields draft={draft} setDraft={setDraft} />}
      {(draft.kind === "wg" || draft.kind === "ws") && <AwgOutboundFields
        draft={draft} setDraft={setDraft}
        publicKey={publicKey} setPublicKey={setPublicKey}
      />}

      {kindError !== null && <div className="text-sm text-alarm">{t(complaint(kindError) as TextKey)}</div>}
      {error !== null && error !== undefined && (
        <div className="text-sm text-alarm">{t(complaint(error) as TextKey)}</div>
      )}

      <div className={footer}>
        {onRemove !== undefined && (
          <button type="button" onClick={onRemove} className={`mr-auto ${danger}`}>
            {t("outbounds.remove")}
          </button>
        )}
        <button type="button" onClick={onClose} disabled={!edited || pending} className={secondary}>
          {t("outbounds.cancel")}
        </button>
        <button
          type="button"
          onClick={() => onSave(draft)}
          disabled={!edited || pending || kindPending || draft.name.length === 0
            || (draft.kind === "interface" && (!draft.interfaceName.trim() || (!draft.ipv4 && !draft.ipv6)))}
          className={primary}
        >
          {pending ? t("outbounds.busy") : t("outbounds.save")}
        </button>
      </div>
    </div>
  )
}
