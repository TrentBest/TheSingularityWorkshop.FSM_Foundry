<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Transport Security

## Purpose

`PinnedHttpsArtifactHttpClientFactory` is the socket-level transport foundation for one registered artifact origin. It is intentionally placed in the Foundry host assembly, not the provider-neutral workflow contracts.

## Guarantees

- Accepts only a DNS hostname registered as an HTTPS origin on port 443, with no path, credentials, query, or fragment.
- Disables automatic redirects, cookies, ambient proxy routing, and automatic decompression.
- Resolves DNS for each new connection and rejects the complete answer set if any address is outside the public-unicast policy.
- Connects the socket to a vetted IP while retaining the registered DNS hostname for TLS certificate validation and SNI.
- Bounds connection establishment and total client timeout.

The address classifier is deliberately conservative. It rejects private, loopback, link-local, carrier-grade NAT, documentation, multicast, reserved, and transition ranges. For IPv6 it does not treat the whole 2000::/3 aggregate as sufficient: the currently unallocated/reserved 2D00::/8 through 3FFF::/16 portion is rejected as well. This policy is based on the IANA IPv6 Global Unicast Address Space registry (last updated 2025-10-10); a deployment must keep the classifier aligned with current IANA allocations and its own egress policy.

## Composition rules

The factory creates a client for exactly one origin. The host must validate the requested URI against its persisted provider registration before choosing that client. Redirects are not followed. If a provider legitimately requires a redirect to an object-storage origin, the host must validate that destination against a separate, explicit origin allowlist and issue a new request with a client pinned to that origin. It must never forward the original origin's Authorization header, cookies, or other credentials to a different host.

Do not use this client for arbitrary URLs or accept the origin from an untrusted notice without comparing it to the persisted request mapping. Do not weaken the origin policy to make a failing retrieval succeed.

## Limits

This factory is not, by itself, an artifact transport adapter or a complete production host. It does not issue credentials, authenticate CiAi callbacks, implement JWT validation, implement the `ICiAiArtifactTransport` interface, host HTTPS ingress, provide remote acknowledgement, or prove a live DNS/socket/TLS retrieval. The caller remains responsible for bounded response streaming, status/media checks, exact URI checks, expiry, byte-count and digest verification, and durable receipt handling.

The contract-level integration test uses fakes; this transport foundation must be composed with a concrete adapter and exercised against a controlled integration endpoint before production readiness is claimed.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="240" /></p>
