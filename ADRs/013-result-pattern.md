# ADR-013: Result Pattern over Exceptions for Flow Control

## Status
Accepted

## Context
Operations at every layer fail in *expected* ways: input is invalid, a domain invariant is broken, a
requested entity is missing, a uniqueness conflict occurs, the caller lacks permission. There are two
common ways to signal those: throw an exception and translate it near the edge, or return an explicit
value that the caller must inspect. Using exceptions for *expected* business outcomes has real costs:
the failure is invisible in the method signature, it is easy to forget to catch, it is comparatively
expensive on the throw path, and it conflates "the user asked for something we will not do" with "the
process is broken."

## Decision
Model expected failures as values using `Result` / `Result<T>` (`MMCA.Common.Shared.Abstractions`),
not exceptions.

- A `Result` is either success or failure; a failure carries one or more `Error` records (`Code`,
  `Message`, `Type` of type `ErrorType`, optional `Source` / `Target`).
- `ErrorType` is a **transport-agnostic** category: `Validation`, `Invariant`, `NotFound`, `Conflict`,
  `Unauthorized`, `Forbidden`, `UnprocessableEntity`, `Failure`. The domain never names an HTTP status.
- Domain factory methods and mutators return `Result<T>`; application command/query handlers thread
  results through the `Match()`, `Map()`, and `BindAsync()` combinators instead of `try`/`catch`.
- The transport mapping lives only at the edge. `ApiControllerBase.HandleFailure()` maps the first
  error's `ErrorType` to an HTTP status via a `FrozenDictionary` and returns an RFC 9457 ProblemDetails
  body carrying all errors. gRPC does the equivalent over the wire (`GrpcResultExceptionInterceptor`,
  ADR-007), so callers keep programming against `Result<T>` across a process boundary.
- Exceptions are reserved for the genuinely exceptional: programming errors (null-argument guards) and
  infrastructure faults (DB / transaction failures) that should abort the request rather than be
  modeled as a business outcome.

## Rationale
- **Failures are in the signature.** A method that can fail returns `Result<T>`, so the caller cannot
  silently ignore the failure path the way an uncaught exception allows.
- **Category, not status code, at the core.** `ErrorType` keeps Domain and Application transport-
  agnostic; only the API (or gRPC interceptor) translates it, so the same handler serves REST and gRPC.
- **Composable.** The railway-oriented combinators chain steps without an `IsFailure` check at every
  line, and short-circuit on the first failure.
- **Cheap and predictable.** No throw/catch on the common "won't do it" path.

## Trade-offs
- More ceremony at call sites than letting an exception bubble; the combinators absorb most of it.
- Two error channels coexist (Result for expected, exceptions for exceptional). The boundary is a
  judgment call: "could a well-behaved caller reasonably trigger this?" then return a `Result`,
  otherwise throw.
- The `ErrorType` to HTTP mapping is one-directional and first-error-wins for the *status code* (all
  errors still serialize into the ProblemDetails body).

## Related
ADR-007 (Result over the wire via gRPC), ADR-014 (the decorator pipeline returns `Result.Failure` to
short-circuit a command before it reaches the handler).
