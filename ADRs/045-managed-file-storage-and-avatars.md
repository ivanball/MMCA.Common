# ADR-045: Managed File Storage and User Avatars

## Status
Accepted (2026-07-11). Records the BR-116 amendment (ADC): avatar photos are IN scope, powered
by two new framework seams. The framework legs are implemented; each consumer provisions its
own storage account and wires the upload endpoints.

## Context
The MAUI capability program (ADR-042) brought MediaPicker/camera within reach, and ADC amended
BR-116 to include user avatar photos. That needs binary blob storage (the databases store
entities, not images), untrusted-image handling (uploads are attacker-controlled bytes; EXIF
metadata carries GPS coordinates, which are PII), and a client affordance that is native
pick/capture on phones and a plain file input on the web.

The framework had no file-storage abstraction: anything blob-shaped would otherwise be written
directly against the Azure SDK inside a module, unusable by the next consumer and untestable.

## Decision
- **`IFileStorageService`** (Application): upload-by-blob-name returning the public URI, plus
  idempotent delete. Default is an unconfigured Null implementation whose uploads fail with a
  clear error; `AddAzureBlobFileStorage(configuration)` swaps in the Azure Blob implementation
  when the `FileStorage` section is complete (`ContainerName` + either `ServiceUri` for
  DefaultAzureCredential/managed-identity auth, the production path, or `ConnectionString` for
  local Azurite). The container is provisioned by infrastructure, never created by the app.
- **`IImageProcessor`** (Application) with `ImageSharpImageProcessor` (Infrastructure, always
  registered - it has no external dependency): decode, auto-orient, center-crop to an exact
  square, strip ALL metadata, re-encode as JPEG. Full re-encode is the security boundary: only
  pixels survive, killing EXIF GPS and polyglot payloads in one move. Undecodable content is a
  validation failure, not an exception. ImageSharp ships under the Six Labors Split License
  (Apache 2.0 terms for open-source / small-revenue use, which covers this project).
- **`IMediaPickerService`** (UI capability, ADR-042 pattern): native photo pick/capture with the
  permission flow encapsulated; cancelled/denied returns null. Web heads keep the Null default
  (`IsSupported == false`) and render an `InputFile` instead - an affordance switch, not a
  degraded path.
- **Avatar contract (BR-116a, applied per consumer)**: one avatar per user; accept jpeg/png/webp
  up to 2 MB; server re-encodes to 256x256 JPEG via `IImageProcessor` (client-declared content
  types are advisory only); blob name `{userId}-{random8}.jpg` in the infrastructure-provisioned
  public-read `avatars` container (so the URL path reads `avatars/{userId}-{random8}.jpg`);
  upload deletes the previous blob; the URL lives on the user entity as `[Pii]`,
  nulled on anonymize with the blob deleted; exported in the GDPR data export.

## Consequences
- The avatars container is public-read by design: avatar URLs render in `<img>` tags on
  anonymous-visible surfaces without SAS plumbing. The random blob suffix prevents enumeration;
  the trade-off (anyone with the URL can fetch the image) is accepted and documented in the
  consumer's privacy policy.
- A replaced or deleted avatar deletes its blob, but CDN/browser caches may serve the old URL
  briefly; the random suffix means the new upload never reuses the old URL, so staleness is
  bounded by cache TTLs.
- `DefaultAzureCredential` in production means the storage account needs a data-plane role
  (Storage Blob Data Contributor) for the app identity - a bicep-level grant, not a secret.
- ImageSharp joins the Infrastructure dependency set (vuln-audited like everything else); the
  license note above must be revisited if the project's revenue posture changes.
