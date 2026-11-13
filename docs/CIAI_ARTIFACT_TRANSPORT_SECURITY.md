<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Transport Security

## Purpose

`PinnedHttpsArtifactHttpClientFactory` is the socket-level transport foundation for one registered artifact origin. `PinnedHttpsCiAiArtifactTransport` now composes one pinned client per explicitly registered origin and implements `ICiAiArtifactTransport`. Both live in the Foundry host assembly, not the provider-neutral workflow contracts.

## Guarantees

- Accepts only a DNS hostname registered as an HTTPS origin on port 443, with no path, credentials, query, or fragment.
- Disables automatic redirects, cookies, ambient proxy routing, and automatic decompression.
- Resolves DNS for each new connection and rejects the complete answer set if any address is outside the public-unicast policy.
- Connects the socket to a vetted IP while retaining the registered DNS hostname for TLS certificate validation and SNI.
- Bounds connection establishment and total client timeout.

The address classifier is deliberately conservative. It rejects private, loopback, link-local, carrier-grade NAT, documentation, multicast, reserved, and transition ranges. For IPv6 it does not treat the whole 2000::/3 aggregate as sufficient: the currently unallocated/reserved 2D00::/8 through 3FFF::/16 portion is rejected as well. This policy is based on the IANA IPv6 Global Unicast Address Space registry (last updated 2025-10-10); a deployment must keep the classifier aligned with current IANA allocations and its own egress policy.

## Composition rules

The factory creates a client for exactly one origin. `PinnedHttpsCiAiArtifactTransport.Create` accepts a non-empty, duplicate-free set of registered origins and creates a separate pinned client for each. Requests are accepted only when their HTTPS DNS origin is in that allowlist; the supplied absolute URI is sent unchanged. Redirects are not followed. If a provider legitimately requires a redirect to an object-storage origin, the host must validate that destination against a separate, explicit origin allowlist and issue a new request with a client pinned to that origin. The adapter does not attach credentials and never forwards source-origin Authorization headers or cookies to a different host.

Do not use this client for arbitrary URLs or accept the origin from an untrusted notice without comparing it to the persisted request mapping. Do not weaken the origin policy to make a failing retrieval succeed.

## Limits

The transport adapter is a concrete implementation of `ICiAiArtifactTransport`, but it is not a complete production host. It does not issue credentials, authenticate CiAi callbacks, implement JWT validation, host HTTPS ingress, provide remote acknowledgement, or prove a live DNS/socket/TLS retrieval against a provider endpoint. The `CiAiArtifactRetrievalCoordinator` remains responsible for bounded response streaming, status/media checks, exact URI checks, expiry, byte-count and digest verification, and durable receipt handling.

The exact-head Foundry CI run now compiles the concrete adapter and exercises its origin allowlist, invalid-URI, duplicate-registration, and disposal behavior. Those tests do not open a real network connection. A controlled integration endpoint and provider-specific redirect/download flow are still required before production readiness is claimed.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="240" /></p>
