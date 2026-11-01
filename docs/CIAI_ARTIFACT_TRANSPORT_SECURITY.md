<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# CiAi Artifact Transport Security

## 01 — What

Foundry's artifact retrieval transport is a host-owned security boundary. A validated notice supplies one immutable HTTPS URI, but URI validation alone is not sufficient to prevent server-side request forgery (SSRF): DNS can resolve a permitted hostname to a private address, and DNS answers can change between validation and connection.

PinnedHttpsArtifactHttpClientFactory creates a direct HttpClient for one registered HTTPS origin. Its connection callback resolves the host at connection time, rejects the entire DNS answer set if any address is not publicly routable, and connects to one of the vetted IP addresses. The request URI remains the original hostname, so the HTTP stack still performs TLS certificate validation and uses the hostname for SNI.

## 02 — Why

The transport must defend against:

- loopback, private, link-local, carrier-grade NAT, documentation, multicast, reserved, and other special-purpose IP ranges;
- DNS rebinding between a preflight lookup and the actual socket connection;
- redirects to a different host or address;
- ambient proxy configuration that could route around direct connection policy;
- cleartext HTTP, non-standard ports, embedded credentials, and query-string tokens.

The host must still compare the requested URI's scheme, host, port, and origin against the exact trusted provider registration. This factory binds connections to one registered DNS hostname; it does not decide which provider is trusted.

## 03 — How

1. Register one provider origin as a credential-free https://hostname/ URI on port 443.
2. Create a dedicated client with PinnedHttpsArtifactHttpClientFactory.Create(registeredOrigin).
3. Send only the exact retrieval URI already validated against that origin; do not construct a new URI from callback fields.
4. Keep automatic redirects disabled. Reject any 3xx response; never follow a redirect manually.
5. Require HTTP 200 and the expected application/json response media type.
6. Stream no more than the notice's declared size plus one byte, with a hard 16 MiB maximum and a bounded timeout.
7. Pass the exact bytes to CiAiArtifactContentVerifier.Verify; do not parse or consume the report before digest and size verification.

The factory's ConnectCallback resolves DNS for each new connection and validates the complete returned address set immediately before connecting. It pins the socket to a vetted address while preserving the original hostname for TLS. The handler disables proxy use, redirects, cookies, and automatic decompression.

The address classifier admits IPv4 addresses outside listed special-purpose ranges and IPv6 global-unicast addresses in 2000::/3, excluding documentation and transition ranges. This is a conservative application policy, not a replacement for network egress controls. Production deployment should additionally enforce outbound firewall rules and provider-origin allowlists.

## 04 — See it in a minute

The contract tests cover public IPv4/IPv6 examples, private and special-purpose address rejection, strict HTTPS-origin validation, and client construction. They do not perform live DNS, socket, TLS, provider authentication, or artifact retrieval tests.

## 05 — Choose your next step

- Add a concrete adapter implementing ICiAiArtifactTransport on top of this client factory when the ingress/retrieval contract is integrated.
- Add a production HTTPS callback host and JWT authenticator using the approved host identity provider; credentials must remain outside prompts and Foundry domain state.
- Run deployment-level egress and DNS-rebinding tests against a controlled provider endpoint.

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="320" /></p>
