<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Ingress Adapter Boundary

## Purpose

`CiAiArtifactNoticeIngress` is the host-neutral coordinator between an authenticated HTTP adapter and Foundry's existing notice validator and receipt ledger. It prevents an adapter from accidentally persisting a notice before validating it against the exact dispatch intent.

The ingress sequence is:

1. The host adapter receives the HTTP request over its configured HTTPS endpoint and enforces the 64 KiB body limit while reading.
2. A request-scoped `ICiAiArtifactNoticeAuthenticator` validates the host's configured authentication policy and returns the registered sender identity. The implementation must validate JWT signature, issuer, audience, subject, scope, expiry/not-before, and sender repository authorization. It must not return an identity derived from untrusted body fields.
3. Foundry strictly decodes the exact raw body as UTF-8, extracts the receipt identity, and validates the complete v1 notice against trusted provider registration and the persisted, exact-run-bound dispatch mapping.
4. Only after successful authentication and validation does Foundry append the receipt to `JsonlCiAiArtifactNoticeReceiptLedger`.
5. The HTTP adapter may acknowledge acceptance only after `AcceptAsync` returns. It should map authentication failures to 401/403 as appropriate, malformed or mismatched notices to a client error, and unexpected persistence failures to a server error without claiming acceptance.

## Host adapter responsibilities

The core does not host an HTTP listener, terminate TLS, parse Authorization headers, validate JWTs, provision signing keys, or choose issuer/audience values. The host must supply those controls and configure the authenticator per request. Authentication credentials and raw bearer tokens must never be passed into the model, persisted in Foundry's notice ledger, or included in diagnostics.

The authenticator is a trust boundary, not a JWT implementation. A fake authenticator is appropriate for deterministic tests only. Production readiness requires a concrete host adapter and security tests against the deployment's actual issuer, key rotation, audience, scope, sender registration, and expiry policy.

The ingress coordinator accepts the exact request body as bytes. It rejects empty/oversized bodies and invalid UTF-8 before notice validation. The receipt ledger retains the normalized payload string and its SHA-256; strict UTF-8 decoding ensures a valid body can be re-encoded without changing its byte sequence. The sender identity is obtained only from the host authenticator.

## Bounded artifact retrieval

After ingress accepts a notice, `CiAiArtifactRetrievalCoordinator` can retrieve its `CiAiValidatedArtifactNotice` through a host-owned `ICiAiArtifactTransport`. The coordinator:

- passes the validated retrieval URI to the transport unchanged;
- rejects a response marked as redirected or whose final URI differs from the validated URI;
- accepts only HTTP 200 and `application/json`;
- streams no more than the manifest's declared byte count plus one detection byte, with a 16 MiB ceiling;
- applies a bounded cancellation timeout (30 seconds by default; host-configurable up to five minutes); and
- passes the exact bytes to `CiAiArtifactContentVerifier` for expiry, exact length, and fixed-time SHA-256 verification.

The transport remains a security-critical host boundary. It must disable automatic redirects and enforce DNS/IP restrictions against the actual connected address, including loopback, private, link-local, multicast, reserved, IPv4-mapped IPv6, and DNS-rebinding cases. It must not treat a prior DNS lookup as sufficient; address policy must be enforced at connection time. The coordinator cannot prove that a transport implementation obeys these rules. The current implementation defines the boundary and deterministic fake-transport tests; it does not provide a production HTTP client, DNS resolver, socket pinning, or live network access.

## Guarantees and limits

- Authentication is invoked before body parsing or receipt mutation.
- Unknown, duplicate, malformed, expired, unbound, or mismatched notice data is rejected by the v1 validator before a receipt is appended.
- Cancellation before persistence does not append a receipt.
- An exact retry while its dispatch mapping remains active returns the original receipt.
- The durable receipt confirms notice acceptance only. It is not proof that the artifact was retrieved, that its digest matches, or that the CI evidence is authoritative.
- A retrieved artifact is not yet an evaluated evidence packet. Schema validation and exact-run/policy evaluation must happen before it can influence Foundry lifecycle decisions.
- The receipt and mapping journals remain local-filesystem, single-process stores. This work does not provide distributed locking, HTTPS hosting, JWT validation, a production artifact transport, remote acknowledgement delivery, or cross-process recovery.

---

<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
