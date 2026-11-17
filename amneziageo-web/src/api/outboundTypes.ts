import { localDraftOf } from "./localOutbounds"
import { awgDraftOf, changeAwgKind } from "./awgOutbounds"
import type { LocalOutbound, LocalOutboundDraft } from "./localOutbounds"
import type { AwgOutbound, AwgOutboundDraft } from "./awgOutbounds"

// The composition root lists the supported wire implementations.
export type Outbound = LocalOutbound | AwgOutbound
export type OutboundDraft = LocalOutboundDraft | AwgOutboundDraft
export type OutboundKind = Outbound["kind"]

export function draftOf(outbound: Outbound): OutboundDraft {
  return outbound.kind === "local" ? localDraftOf(outbound) : awgDraftOf(outbound)
}

export function reuseDraft(draft: OutboundDraft, kind: OutboundKind): OutboundDraft | null {
  if (draft.kind === kind) {
    return draft
  }

  return draft.kind !== "local" && kind !== "local" ? changeAwgKind(draft, kind) : null
}
