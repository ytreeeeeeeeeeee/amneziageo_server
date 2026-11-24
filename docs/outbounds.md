# The ways out

An outbound is a way traffic leaves the host: through the uplink of the host itself, through an
AmneziaWG tunnel the host holds as a client of another server, or through such a tunnel carried inside a
websocket to a wstunnel proxy, or through an existing interface configured outside the service. Every outbound carries a mark of its own;
a packet marked with it is routed out through that outbound. The panel keeps the outbounds in SQLite and
puts them on the host through the `ip` and `nft` tools.

## Rights

| Route | Right |
|---|---|
| `GET /api/outbounds` | `state:read` |
| `GET /api/outbounds/{id}` | `state:read` |
| `GET /api/outbounds/draft` | `routing:write` |
| `POST /api/outbounds/awg/keys` | `routing:write` |
| `POST /api/outbounds/awg/import` | `routing:write` |
| `POST /api/outbounds` | `routing:write` |
| `PUT /api/outbounds/{id}` | `routing:write` |
| `POST /api/outbounds/{id}/switch` | `routing:write` |
| `POST /api/outbounds/{id}/move` | `routing:write` |
| `POST /api/outbounds/{id}/apply` | `routing:write` |
| `POST /api/outbounds/{id}/probe` | `routing:write` |
| `POST /api/outbounds/apply` | `routing:write` |
| `DELETE /api/outbounds/{id}` | `routing:write` |

`routing:write` is the same right the geo databases are managed under, and it counts as sensitive: a
caller without a fresh login is refused. Keys are answered only to a caller that holds it.

## The kinds

| Kind | Carries |
|---|---|
| `local` | the uplink of the host, no interface of its own |
| `interface` | an existing interface, with explicitly selected IPv4/IPv6 support and optional gateways |
| `wg` | an AmneziaWG interface the host raises as a client of another server |
| `ws` | the same interface, with its UDP carried inside a websocket to a wstunnel proxy |

A fresh database is seeded with one `local` outbound named `direct`.

## Through an existing interface

Choose **Existing interface** in the channel form, or send `kind: "interface"` to the ordinary
outbound routes. The logical outbound name can differ from `interfaceName`. Both `ipv4` and `ipv6`
must be present in a request, and at least one must be true. `gateway4` and `gateway6` are optional;
an empty gateway installs a default route directly through the device. A gateway must be a unicast
address of an enabled family. Gateways are normalized before storage and application; for example,
`[fe80::1]` becomes `fe80::1`. Zone suffixes such as `%2` or `%eth0` are rejected: select the
device through `interfaceName` and enter the gateway address without a zone. Invalid addresses are
rejected. DNS and probe addresses must also belong to an enabled family.

```json
{
  "kind": "interface",
  "name": "ams-interface",
  "interfaceName": "tt-ams",
  "ipv4": true,
  "ipv6": false,
  "gateway4": "",
  "gateway6": "",
  "isEnabled": true,
  "closePrivate": true,
  "dns": ["1.1.1.1"],
  "probe": "1.1.1.1",
  "probeEvery": 30
}
```

The device must already exist and be up. The driver never creates, removes or reconfigures it:
addresses, MTU and existing routes outside the outbound's allocated table remain unchanged.
It reconciles only its own routing table and policy rules. Disabling or deleting the outbound
removes those entries while preserving the device. A missing or down device makes the outbound
unavailable; the background watch retries when it returns.
Switching between a managed AWG outbound and this type on the same device is refused before the
API saves the change: withdrawing the managed type would otherwise delete the device being reused.
Select a different device for such a type change.
An interface referenced by a managed outbound remains reserved in the database even when that outbound
is disabled: its later cleanup or startup must not delete a device reused by an external outbound.

Unsupported families get an unreachable default route, so marked packets cannot fall through to
the host's main table. Routing rules, priority/round/sticky balancers, the route tester and DNS
upstream selection use only members supporting the packet or name server's address family.
If none supports the family, traffic is held rather than sent through another route.

For externally managed interfaces, NAT, TCP MSS and private-network restrictions match the outbound
mark as well as the device. Two such outbounds can share a device with different private-network
settings. These entries do not install the AWG-wide ingress drop rules on an existing interface.
Its other firewall policies remain the responsibility of the interface's existing configuration.

Settings are stored in the separate `InterfaceOutbounds` table and are included in ordinary database
backups. It contains the device name, supported families and optional gateways, linked to the common
outbound row. Probing uses the common `probe` setting.
Downgrade is refused while interface rows exist, preventing unsupported base rows or data loss.

## Through a websocket

A `ws` outbound is for a network that carries web traffic and nothing else. The panel binds a loopback
port, points the interface at it, and every datagram travels to the proxy as one websocket message; the
proxy hands it to the server on its own loopback, at the port the outbound names. The websocket opens on
the first datagram and is opened again after a drop.

| Setting | Holds |
|---|---|
| Websocket proxy | the proxy, as `proxy.example.net` or `wss://user:secret@proxy.example.net:8443/token` |
| Address, Port | the server behind the proxy, and the port the proxy hands the tunnel to |

A bare name takes the port of the server; an address carries the port, the path token and the basic-auth
credentials. Changing the proxy opens a new carrier and drops the old one; turning the outbound off takes
the carrier down. Every other setting is the one a `wg` outbound carries.

Another AmneziaGeo server takes the websocket on the port of the services of its endpoint, under the path of that
endpoint, and only with the token of the keys, see [services.md](services.md). A proxy that names no credentials
gets that token, counted from the private key of the outbound and the public key of the server, and then takes
whatever certificate the front presents; such a proxy is named as `wss://<host>:<port of the services>/<path>`, and
without a path it comes under `v1`, the path of the endpoints of the releases before.

## The settings of a tunnel

| Setting | Holds |
|---|---|
| Name | the name of the interface, up to 15 characters |
| Address, Port | where the server is reached |
| Interface address | the ranges the interface carries, as the server handed them out |
| Name servers | the servers reachable through the tunnel |
| MTU, Keepalive | the packet size and how often the path is held open |
| Close private networks | whether a client is kept out of the private ranges behind the tunnel, on for a new tunnel |
| Private key | the key of the interface; the public one is derived from it |
| Server public key | the key the server is known by |
| Preshared key | the key added on top of the handshake, when the server asks for one |
| Obfuscation | the AmneziaWG shape, which has to match the server exactly |

A tunnel with no obfuscation at all is a plain WireGuard tunnel, and nothing is sent to the kernel about
it. A tunnel that carries some of it is checked the way an endpoint is.

## The probe

Every outbound carries a probe of its own: the name server it asks over UDP, and how often. The question
goes out under the mark of the outbound, so it takes the way out the rules take, and an answer means that
way is open. The panel asks for `example.com` and waits two seconds.

| Setting | Holds |
|---|---|
| Name server | the server the probe asks, empty for no probe |
| Every, seconds | how often the probe goes out, from 5 to 3600 |

Two probes that miss in a row take the outbound off the balancers, and one answer brings it back. A probe
that cannot be marked, which is what happens without `CAP_NET_ADMIN`, leaves the verdict as it was, and the
age of the handshake stands in again.

`POST /api/outbounds/{id}/probe` sends one probe at once and answers with what the host holds for the
outbound. The panel does the same under `Probe now` in the row menu.

## Reading a client configuration

`POST /api/outbounds/awg/import` takes the `.conf` an AmneziaWG server hands its clients and answers with the
outbound it carries: keys, address, obfuscation and endpoint. Nothing is stored: the answer is a draft the
panel fills the form with. In the panel the same thing is the field at the bottom of the form.

## The mark, the table and the rule

Every outbound is given the first free mark from `0xA601` up, and a routing table of its own from `42601`
up. A `local` outbound looks the way out up in the main table instead. What lands on the host is:

- `ip rule add pref 10000+n fwmark <mark> lookup <table>`, above the main rule, so an unmarked packet
  keeps going the way it went before;
- `ip rule add pref 10255 fwmark 0xa600/0xffffff00 unreachable`, in both families, right below the rules of
  the outbounds, so a marked packet whose outbound is off, gone or without an interface is refused instead of
  falling through to the main table and leaving through the uplink;
- `default dev <name> table <table>` for a tunnel;
- one firewall table, `inet amneziageo_out`, that masquerades what leaves through every outbound that is
  on, holds a TCP segment down to what the path carries and filters what comes out of the tunnels.

The refusal covers the traffic of the rules, the probe, the resolver and every other socket under a mark:
while the interface of a tunnel is down, its table is empty and its probe misses, so the outbound stops
carrying traffic, a rule that holds keeps its traffic and a balancer moves to another member. A client whose
connection broke this way gets `network unreachable`, and a packet of a connection the host no longer tracks
is dropped rather than sent on under the address of the client, see [rules.md](rules.md).

Policy rules are checked separately for IPv4 and IPv6, including the table, action and full mark mask.
A rule at the outbound's priority with its mark but different settings is removed before the correct one
is installed. A failed removal or installation is reported and the next attempt checks both families again.

A network manager that starts over drops the rules it did not lay itself: systemd-networkd does, under
`ManageForeignRoutingPolicyRules=yes`, its default, whenever it is restarted. The watch that probes the
outbounds looks at both rules whenever the kernel tells of a change of the routing rules or the interfaces of the
host, and every five seconds when the panel does not hear the kernel, lays again the ones the panel had laid and
the host lost, and writes `the rule of '<name>' was gone and is laid again` to the log.

The interface itself is given no mark: the mark is what sends a packet into the tunnel, not what the
tunnel puts on its own packets, so there is no loop to break. The interface takes each of its addresses
alone, as `/32` or `/128`, whatever mask the server handed it with, so the network behind the tunnel is
reached through the rules and never through a route of the main table.

## What a tunnel lets back

The server at the other end of a tunnel is trusted with what the host sends into it and with nothing
else. Out of a tunnel the host takes only what answers a connection it made or passed on: a new
connection that comes out of a tunnel is dropped, both on its way to the host and on its way through it.
Nor does the host pass on what came in on the uplink and would leave through it again, so the
masquerading of a `local` outbound covers the clients and the host alone.

```
chain input {
    type filter hook input priority filter; policy accept;
    iifname { "awgbor" } ct state established,related accept
    iifname { "awgbor" } drop
}
```

A tunnel with **Close private networks** on also rejects a new connection a client opens through it to
a private range: `10.0.0.0/8`, `100.64.0.0/10`, `169.254.0.0/16`, `172.16.0.0/12`, `192.168.0.0/16`,
`fc00::/7` and `fe80::/10`, which is where the tunnel network of the server and whatever stands behind it
live. With it off, a rule that sends such a range into the tunnel takes the client there, under the
address of the tunnel.

## Putting it on the host

`OutboundConfig` is the abstract contract for identity, routing, DNS and probe settings.
`Name` is the identity used by rules, balancers and the resolver. `InterfaceName` is the network device
used by the shared firewall, or an empty string for the host's uplink; `UsesInterface` is derived from it.
AWG explicitly keeps `InterfaceName == Name`, preserving its existing settings and database format.
Another implementation can name its outbound `exit-ams` and use `tt-ams` without changing AWG or adding
an interface field to the common API. `OwnsInterface` declares whether the driver creates and removes the
device: AWG owns its interfaces, while an external-interface implementation does not. Two external outbounds
may share a device; a device held by an owning driver cannot be taken over by another outbound.
Interface cleanup remains the driver's responsibility;
this contract alone does not register an existing interface as an outbound.
`LocalOutboundConfig` describes the way out through the host itself; `AwgOutboundConfig` derives from the same contract and adds the server, keys, interface addresses, MTU, keepalive, obfuscation and websocket proxy.
`InterfaceOutboundConfig` carries the device, supported families and optional gateways. Existing AWG
and local configurations retain their previous family behavior and wire format.
`OutboundEntity`, `OutboundRequest`, `OutboundResponse` and `OutboundState` contain only common fields.
Their AWG counterparts own the protocol fields, including the peer endpoint and handshake diagnostics.
The common configuration, entity, request and response types are abstract. Local outbounds have their own
`LocalOutboundConfig`, `LocalOutboundEntity`, `LocalOutboundRequest`, `LocalOutboundResponse` and state types.
All outbound entities are declared in `Entities.cs`.

EF uses TPT inheritance: `Outbounds` stores common settings, `LocalOutbounds` identifies local rows,
and `AwgOutbounds` stores keys and tunnel settings. The local migration creates the matching derived rows
for existing local outbounds without changing their identity or settings.
The migration copies existing `wg` and `ws` rows before removing their protocol columns from `Outbounds`;
the downgrade restores these columns and their contents.

JSON requests use `kind` (`local`, `wg`, `ws`) to select the request subtype. A local request and response
have no server, key, obfuscation or handshake fields; sending protocol fields in a local request is refused.
AWG and WS retain these fields in their own response subtype. Lists can contain all subtypes; key visibility
still follows the caller's permissions. Key generation and config import use `/api/outbounds/awg/keys` and
`/api/outbounds/awg/import`. A draft without a requested kind now starts as `local`.

Persistence and response factories are registered alongside the implementations. The shared store,
rules, firewall, resolver and probes use the common contracts and the `UsesInterface` capability.

`OutboundHost` selects an `IOutboundDriver` by kind. `AwgOutboundDriver` prepares, restores and removes
the interfaces and routes of both `wg` and `ws` outbounds and reads their handshakes and counters;
`LocalOutboundDriver` uses the main routing table without creating an interface. The host keeps policy
rules, the refusal rule and the firewall shared, and combines each driver's state with the verdict from
`ProbeLive`. `ProbeRunner` and `IProbeLink` probe every kind under its outbound mark independently of the
driver. Drivers are registered in `OutboundServices`; adding a kind also requires its settings to be
accepted by outbound validation and represented in the API and panel.

Probe readings are cached by outbound ID and belong to the saved configuration revision, kind, interface,
mark, table and probe server they checked. Renaming an outbound or assigning its former name to another
outbound cannot mix their readings. Disabling a probe immediately stops using its previous verdict. A changed
revision starts a fresh failure count, and a late answer from an older revision cannot replace the newer verdict.

The database holds the desired configuration. The host separately records the resources it may hold and
the outcome of the last operation for each outbound ID and mark. A failed initial Apply, reconfiguration,
switch-off or interrupted operation stays unavailable until Apply or the background watch completes it.
A saved revision is not reported as applied merely because an older revision still has a live interface.
Cleanup is retried even after the database row is removed, and a second edit also cleans a partially applied
replacement. Firewall protection for retained resources remains until cleanup succeeds, even when the
desired outbound is disabled or removed. Names, marks and managed interfaces remain reserved by their
resource owner during cleanup; API create and edit requests that conflict are refused before writing the
database. Host operations also check ownership, so an unapplied conflicting configuration cannot delete
another outbound's interface. This resource ownership is kept in memory: a process
crash can leave an unused old interface to be removed separately, while startup still reconciles policy
rules against the saved configuration.

Adding, changing, turning on and off, and removing an outbound each put it on the host straight away.
Renaming an outbound writes the new name into the rules and the balancers that name it and into the settings of
the resolver, the saved and the running ones alike, so nothing that left through the outbound loses it. An
outbound that a rule, a balancer or the resolver leaves through is not removed: the panel answers
`outbound-in-use` and names what holds it, and the page that removes it names every rule, balancer and the
resolver at once. The part `Use` of the settings names the rules that leave through the outbound and the balancers
it is in, each a link: a rule leaving through another way is moved onto the outbound there, a rule is not taken
off it (another way is picked in the rule, `way-keeps-rules`), and only a rule that leaves through a way can be
moved (`rule-not-out`); a balancer added there takes the outbound last, one taken off drops it, and the last
outbound of a balancer stays (`no-members`). `PUT /api/outbounds/{id}` takes them in `rules` and `balancers`, the
numbers of every rule and balancer that holds the outbound after the request; a request without a field leaves
that side as it is. The resolver holds both the outbound its settings name and the one
it asks through right now: while the two differ, that is while `pending` stands, neither of them is removed,
and the refusal of the running one ends with `until it is restarted`.
`POST /api/outbounds/apply` goes over every outbound, rewrites the firewall table and lays the rules again;
the panel does the same when it starts, so the outbounds and the rules come back after a reboot. An interface
that already carries the server of its outbound and no other peer keeps it, with the handshake and the
counters, so the panel starting over does not break the tunnel. An interface that carries another obfuscation
is laid anew instead, with the keys, the server and the addresses of the outbound: the kernel takes no field of
the obfuscation away and refuses some changes of it in place, so moving an outbound to a server with another
profile would leave the interface half old. An outbound that is turned off is taken off the host: the rule
goes, the table is cleared, and the interface is removed.

The panel checks enabled outbounds a quarter of a second after a kernel notification about an interface, route
or policy rule, and every five seconds without a notification. If an AWG or WebSocket tunnel interface is
up, the check restores one default route through the tunnel per IP family. It removes competing defaults and
keeps more specific routes in the table. A down interface is brought up
with its addresses and routes. A missing interface is recreated with its keys, addresses, routes and, for a
`ws` outbound, its carrier. The outbound carries nothing until the check runs. An interface
the host refuses is tried again once a minute, or at once when the outbounds change. A change of
the outbounds through the panel and this round take turns, so an outbound that was just turned off is not laid
again behind the change.

Raising an interface, writing keys to it and changing routing rules need `CAP_NET_ADMIN`. Interface
errors are reported in the outbound's `fault`. If `nftables` refuses the shared outbound firewall or the
routing rules, the affected outbound mutation or apply request returns `409 host-refused`. A committed
create, edit, switch or removal includes the outbound ID in `savedId`. A manual apply reports `savedId: null`.
The panel refreshes the affected lists after a conflict and shows when the database change was saved.

Enabled outbounds stop being eligible for routing while the shared outbound firewall is pending. The
background check retries the routing rules independently of that firewall and any database changes. Setting
`Routing:Sudo` to true runs `ip` and `nft` through `sudo -n`.

## What the panel shows

The table names the kind, the server, the mark and the table, the last handshake and the traffic each way.
A `ws` outbound names the server behind the proxy, and the proxy itself stands in the form.
A tunnel counts as alive while its handshake is under three minutes old and its interface is up; while the
interface is down or gone the state says which of the two it is in `fault`, and the panel shows the outbound
as refused with that text behind it. `isAlive` also requires the shared outbound firewall to be applied.
`carries` additionally requires a passing probe when one is configured. A rule stays off the host with
`outbound-down` when its outbound, or every member of its balancer, cannot carry traffic.
An outbound through the host has no interface of its own, so there is nothing to count the bytes on: `rxBytes` and
`txBytes` come back empty rather
than as zeros, and the panel leaves the traffic of such an outbound blank.
