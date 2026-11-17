import { useMutation } from "@tanstack/react-query"
import { commonDraftOf } from "./outboundContracts"
import { client } from "./client"
import type { KeyPair, Obfuscation } from "./configs"
import type { OutboundBase, OutboundDraftBase, OutboundState } from "./outboundContracts"

export interface AwgOutboundState extends OutboundState {
  endpoint?: string
  lastHandshake?: string | null
}

export interface AwgOutbound extends OutboundBase {
  kind: "wg" | "ws"
  host: string
  port: number
  proxy: string
  publicKey: string
  peerKey: string
  privateKey: string | null
  presharedKey: string | null
  address: string[]
  mtu: number
  keepalive: number
  obfuscation: Obfuscation
  state: AwgOutboundState | null
}

export interface AwgOutboundDraft extends OutboundDraftBase {
  kind: "wg" | "ws"
  host: string
  port: number
  proxy: string
  privateKey: string
  peerKey: string
  presharedKey: string
  address: string[]
  mtu: number
  keepalive: number
  obfuscation: Obfuscation
}

export function useImportOutbound() {
  return useMutation({
    mutationFn: async ({ name, config }: { name: string; config: string }) =>
      (await client.post<AwgOutbound>("/outbounds/awg/import", { name, config })).data,
  })
}

export function useOutboundKeys() {
  return useMutation({
    mutationFn: async () => (await client.post<KeyPair>("/outbounds/awg/keys")).data,
  })
}

export function awgDraftOf(outbound: AwgOutbound): AwgOutboundDraft {
  return {
    ...commonDraftOf(outbound),
    kind: outbound.kind,
    host: outbound.host,
    port: outbound.port,
    proxy: outbound.proxy,
    privateKey: outbound.privateKey ?? "",
    peerKey: outbound.peerKey,
    presharedKey: outbound.presharedKey ?? "",
    address: outbound.address,
    mtu: outbound.mtu,
    keepalive: outbound.keepalive,
    obfuscation: outbound.obfuscation,
  }
}

export function changeAwgKind(draft: AwgOutboundDraft, kind: "wg" | "ws"): AwgOutboundDraft {
  return {
    ...draft,
    kind,
    proxy: kind === "ws" ? draft.proxy : "",
  }
}
