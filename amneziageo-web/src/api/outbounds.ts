import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { client } from "./client"

import type { Outbound, OutboundDraft, OutboundKind } from "./outboundTypes"
export type { Outbound, OutboundDraft, OutboundKind } from "./outboundTypes"
export type { OutboundState } from "./outboundContracts"

export function useOutbounds() {
  return useQuery({
    queryKey: ["outbounds"],
    queryFn: async () => (await client.get<Outbound[]>("/outbounds")).data,
  })
}

export function useFreshOutbound(enabled: boolean, name: string, kind: OutboundKind) {
  return useQuery({
    queryKey: ["outbounds", "draft", name, kind],
    queryFn: async () =>
      (await client.get<Outbound>(`/outbounds/draft?name=${encodeURIComponent(name)}&kind=${kind}`)).data,
    enabled,
    gcTime: 0,
    staleTime: 0,
  })
}

export function useAddOutbound() {
  return useRefreshing((draft: OutboundDraft) => client.post("/outbounds", draft))
}

export function useChangeOutbound() {
  return useRefreshing(({ id, draft }: { id: number; draft: OutboundDraft }) => client.put(`/outbounds/${id}`, draft))
}

export function useRemoveOutbound() {
  return useRefreshing((id: number) => client.delete(`/outbounds/${id}`))
}

export function useMoveOutbound() {
  return useRefreshing(({ id, up }: { id: number; up: boolean }) => client.post(`/outbounds/${id}/move`, { up }))
}

export function useSwitchOutbound() {
  return useRefreshing(({ id, on }: { id: number; on: boolean }) => client.post(`/outbounds/${id}/switch`, { on }))
}

export function useApplyOutbound() {
  return useRefreshing((id: number) => client.post(`/outbounds/${id}/apply`))
}

export function useProbeOutbound() {
  return useRefreshing((id: number) => client.post(`/outbounds/${id}/probe`))
}

export { draftOf, reuseDraft } from "./outboundTypes"

function useRefreshing<TArgs>(call: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: call,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["outbounds"] })
      await queryClient.invalidateQueries({ queryKey: ["balancers"] })
      await queryClient.invalidateQueries({ queryKey: ["rules"] })
    },
  })
}
