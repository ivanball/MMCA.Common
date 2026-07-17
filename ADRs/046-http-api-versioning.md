# ADR-046: HTTP API Versioning Strategy

## Status
Accepted (2026-07-15).

## Context
The framework's REST surface is served by controllers hosted in extracted service processes behind a
YARP gateway. As those services evolve, a response shape has to be able to change without breaking a
client still coded against the old shape. HTTP APIs need a versioning axis of their own: one that a
caller selects per request and that the service can advertise and deprecate over time. This is a
different concern from how asynchronous integration events evolve on the wire (ADR-010): that axis is
resolved by consumers from a `SchemaVersion` carried in the serialized event, never chosen by a
caller. The HTTP axis is request-time, client-selected, and reported back in response headers.

Without a shared decision, each host would wire `Asp.Versioning` differently (URL-segment vs. query
vs. header, different default-version behavior, inconsistent deprecation reporting), and there would
be no proof that the machinery actually works past a single version. A "we support versioning"
claim that only ever ships `v1.0` is untestable and erodes silently.

## Decision
Standardize one header-based API-versioning setup in `MMCA.Common.API`, adopt it in every service
host through a single registration call, and keep it exercised by a shared fitness contract that
proves two live versions coexist.

- **One registration wires the whole policy.** `AddCommonApiVersioning`
  (`Source/Presentation/MMCA.Common.API/Startup/WebApplicationBuilderExtensions.cs:76`) sets the
  default version to `1.0`
  (`WebApplicationBuilderExtensions.cs:80`), assumes it when a caller sends no version
  (`AssumeDefaultVersionWhenUnspecified = true`, `WebApplicationBuilderExtensions.cs:81`), reports
  the supported/deprecated versions on every response (`ReportApiVersions = true`,
  `WebApplicationBuilderExtensions.cs:82`), and selects the version from an `api-version` request
  header (`new HeaderApiVersionReader("api-version")`, `WebApplicationBuilderExtensions.cs:83`). The
  reader is header-based deliberately: routes and query strings stay version-free, so a caller opts
  into a newer shape by adding one header rather than changing the URL.
- **The API explorer is wired for versioned OpenAPI.** The same call chains `.AddMvc()` then
  `.AddApiExplorer` (`WebApplicationBuilderExtensions.cs:84`), formatting version groups as `'v'VVV`
  (`WebApplicationBuilderExtensions.cs:87`) and mirroring the default-version behavior for the
  explorer (`WebApplicationBuilderExtensions.cs:89`). That group format feeds the `v1` OpenAPI
  document `AddCommonOpenApi` registers (`WebApplicationBuilderExtensions.cs:166`), which
  `MapCommonOpenApi` serves at `/openapi/v1.json` outside Production only
  (`Source/Presentation/MMCA.Common.API/Startup/OpenApiEndpointExtensions.cs:28`,
  `OpenApiEndpointExtensions.cs:30`).
- **A shipped exemplar proves two versions coexist.** `ServiceInfoControllerBase`
  (`Source/Presentation/MMCA.Common.API/Controllers/ServiceInfoControllerBase.cs:30`) serves the same
  `/ServiceInfo` route under two versions selected by the header: `GetV1` is mapped to `1.0`
  (`[MapToApiVersion("1.0")]`, `ServiceInfoControllerBase.cs:40`) and returns the minimal
  `ServiceInfoResponse` shape (`ServiceInfoControllerBase.cs:51`); `GetV2` is mapped to `2.0`
  (`[MapToApiVersion("2.0")]`, `ServiceInfoControllerBase.cs:45`) and returns the evolved
  `ServiceInfoV2Response`, a superset that also advertises the supported and deprecated version lists
  (`ServiceInfoControllerBase.cs:54`). The base is anonymous and read-only.
- **The class-level version attributes live on the per-service subclass.** Class-level
  routing/versioning attributes are not reliably inherited, so each host supplies a sealed subclass
  carrying them. ADC's `ServiceInfoController` declares `[ApiVersion("1.0", Deprecated = true)]` and
  `[ApiVersion("2.0")]` and sets the service name to `"Conference"`
  (`MMCA.ADC/Source/Modules/Conference/MMCA.ADC.Conference.API/Controllers/ServiceInfoController.cs:18`,
  `ServiceInfoController.cs:19`, `ServiceInfoController.cs:23`); Store's mirror sets `"Catalog"`
  (`MMCA.Store/Source/Modules/Catalog/MMCA.Store.Catalog.API/Controllers/ServiceInfoController.cs:18`,
  `ServiceInfoController.cs:23`). `1.0` is declared deprecated so the deprecation-reporting path is
  live rather than theoretical.
- **A shared fitness contract keeps the machinery exercised.**
  `ServiceInfoVersioningContractTestsBase<TFixture>`
  (`Source/Hosting/MMCA.Common.Testing/ServiceInfoVersioningContractTestsBase.cs:19`) sends
  `api-version: 1.0` and `2.0` over the real host and asserts the v1.0 minimal shape carries an
  `api-deprecated-versions` header (`ServiceInfoVersioningContractTestsBase.cs:38`) while the v2.0
  evolved shape carries `api-supported-versions` (`ServiceInfoVersioningContractTestsBase.cs:54`).
  Because the controller ships in `MMCA.Common.API`, the whole test body is identical across repos:
  each consumer's subclass supplies only its fixture (for example ADC's `ApiVersioningTests`,
  `MMCA.ADC/Tests/Integration/MMCA.ADC.Conference.IntegrationTests/Contract/ApiVersioningTests.cs:14`,
  and Store's equivalent at
  `MMCA.Store/Tests/Integration/MMCA.Store.Catalog.IntegrationTests/Contract/ApiVersioningTests.cs`).
  This is the rubric SS9 fitness check: without a second working version, everything above would be
  asserted rather than proven.
- **Every REST host adopts it the same way.** The extracted services call `AddCommonApiVersioning`
  in their startup: ADC's Conference
  (`MMCA.ADC/Source/Services/MMCA.ADC.Conference.Service/Program.cs:134`) and Identity
  (`MMCA.ADC/Source/Services/MMCA.ADC.Identity.Service/Program.cs:126`) hosts, Store's Catalog host
  (`MMCA.Store/Source/Services/MMCA.Store.Catalog.Service/Program.cs:94`), and the same call is made
  by the other extracted hosts and by the monolith reference host
  (`MMCA.Helpdesk/Source/Hosts/MMCA.Helpdesk.Web/Program.cs:31`).

Application controllers beyond `ServiceInfo` declare `[ApiVersion("1.0")]` today: the second version
exists on the discovery endpoint to keep the versioning path honest, not because any business
resource has yet needed to evolve its shape.

## Rationale
- **Header selection keeps URLs stable.** Routing stays version-free, so gateway route maps, client
  URL builders, and OpenAPI paths do not fork per version; a caller opts into a newer shape with one
  header.
- **Assume-default keeps existing callers working.** `AssumeDefaultVersionWhenUnspecified` means a
  client that never sends the header keeps getting `1.0`, so introducing versioning was not a
  breaking change for any existing caller.
- **Report-and-deprecate makes evolution visible.** `ReportApiVersions` plus a deprecated `1.0`
  means a client can see, from response headers alone, which versions a service still supports and
  which are on the way out, without reading a changelog.
- **A living exemplar beats a claim.** A single-version API cannot demonstrate that its versioning
  works. Shipping `ServiceInfo` with a real, deprecated `1.0` alongside `2.0` gives the fitness
  contract something concrete to assert, the same invariant-over-discipline posture the framework
  prefers (ADR-015).
- **Define once, adopt everywhere.** Putting the whole policy behind `AddCommonApiVersioning` and the
  exemplar behind `ServiceInfoControllerBase` means every host is versioned identically by one call
  and one subclass, so the reader/default/reporting choices cannot drift apart between services.

## Trade-offs
- **The class-level version attributes are not inherited.** Each per-service subclass must repeat the
  `[ApiVersion(...)]` and routing attributes (the same inheritance caveat ADR-034 and ADR-036 note
  for controller convention attributes); the shared behavior lives in the base, but the attributes do
  not.
- **Adoption is per host.** A new REST host that forgets `AddCommonApiVersioning` gets no versioning
  and no reported versions, the same audit-the-inventory caveat other opt-in framework registrations
  carry; the shared fitness contract only guards a host once its subclass is added.
- **Header versioning is less discoverable than a URL segment.** A version chosen by header does not
  show up in a copied URL or a browser address bar, so the version in play is only visible to a
  caller that reads request/response headers.
- **The OpenAPI document is single-version and dev/CI only.** `MapCommonOpenApi` serves one `v1`
  document and is a no-op in Production (`OpenApiEndpointExtensions.cs:30`), so the machine-readable
  contract does not yet enumerate the `2.0` discovery shape and is not a public production surface.

## Related
ADR-010 (integration-event schema versioning: the asynchronous, `SchemaVersion`-carried,
consumer-resolved axis this deliberately contrasts with; HTTP versioning here is request-time and
client-selected), ADR-015 (the fitness-function approach that
`ServiceInfoVersioningContractTestsBase` embodies, keeping the versioning machinery exercised rather
than asserted), ADR-034 and ADR-036 (controller-convention decisions that note the same class-level
`[ApiVersion]` non-inheritance handled by the per-service subclass).
</content>
