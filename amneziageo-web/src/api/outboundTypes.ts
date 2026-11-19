import { localDraftOf } from "./localOutbounds"
import { interfaceDraftOf } from "./interfaceOutbounds"
import type { InterfaceOutbound, InterfaceOutboundDraft } from "./interfaceOutbounds"
import { awgDraftOf, changeAwgKind } from "./awgOutbounds"
import type { LocalOutbound, LocalOutboundDraft } from "./localOutbounds"
import type { AwgOutbound, AwgOutboundDraft } from "./awgOutbounds"

// The composition root lists the supported wire implementations.
export type Outbound = LocalOutbound | AwgOutbound | InterfaceOutbound
export type OutboundDraft = LocalOutboundDraft | AwgOutboundDraft | InterfaceOutboundDraft
export type OutboundKind = Outbound["kind"]

export function draftOf(outbound: Outbound): OutboundDraft {
  if (outbound.kind === "interface") {
    return interfaceDraftOf(outbound)
  }
  return outbound.kind === "local" ? localDraftOf(outbound) : awgDraftOf(outbound)
}

export function reuseDraft(draft: OutboundDraft, kind: OutboundKind): OutboundDraft | null {
  if (draft.kind === kind) {
    return draft
  }

  return (draft.kind === "wg" || draft.kind === "ws") && (kind === "wg" || kind === "ws")
    ? changeAwgKind(draft, kind) : null
}
