# Getting Started: Build a New App on MMCA.Common

This is the step-by-step guide for standing up a **brand-new application** on the MMCA.Common
framework. MMCA.Common is a .NET 10 framework for DDD, Clean Architecture, and CQRS, shipped as
thirteen lockstep-versioned NuGet packages. Its core promise: **build a modular monolith now, and
extract a module into its own microservice later, without a rewrite.**

This guide builds **monolith-first** (the fastest path to a running app), then shows a fully worked
**extraction** of one module into its own service behind a gateway, so the "extract later" promise is
concrete rather than theoretical.

A complete reference app that follows every step lives at `../MMCA.Helpdesk` (a support-ticket app).
Wherever a step says "pattern source", that points at real, working code you can copy from MMCA.ADC,
MMCA.Store, or MMCA.Helpdesk.

> **Reading the framework itself:** for the *why* behind each pattern, read the relevant
> [ADR](ADRs/README.md). For a type-by-type tour of the framework internals, see the workspace
> onboarding guide under `Docs/Onboarding`. This guide is the consumer-facing "how do I start" path.

---

## What you will build

A modular monolith with two modules:

- **Identity** (the standard auth module present in every MMCA app): users and login; the **JWKS
  issuer** that signs RS256 tokens.
- **Tickets** (your business module): a `Ticket` aggregate with `TicketComment` children, exercised
  end-to-end and later extracted into its own service.

By the end you will have a green-building solution, applied EF migrations, a running Aspire stack, and
a passing architecture-fitness test, plus a worked path to pull Tickets out into a microservice.

---

## Phase 0: Prerequisites and decisions

**Install:**

- **.NET 10 SDK** (the framework targets `net10.0` with `LangVersion: preview` for C# extension types).
- **SQL Server** reachable locally (LocalDB, a container, or the one Aspire starts for you).
- **Docker Desktop** (Aspire provisions SQL Server, Redis, and RabbitMQ as containers for local runs).
- **EF Core tools:** `dotnet tool install --global dotnet-ef`.

**Decide how to consume MMCA.Common (two modes, switchable in one file):**

1. **NuGet (GitHub Packages)** is the production path for any standalone app. It needs a `GITHUB_TOKEN`
   environment variable with `packages:read` scope, and a `nuget.config` that maps the `MMCA.*`
   pattern to the GitHub feed (shown in Phase 1).
2. **Local source (`UseLocalMMCA`)** references `../MMCA.Common/Source/` directly via `local.props`.
   Use this when your app sits in the same workspace as MMCA.Common and you want to co-develop the
   framework and the app together. It needs no token (MMCA.Common itself restores only from nuget.org).

> When using local source mode, after editing MMCA.Common source you must **rebuild MMCA.Common in
> Debug** before your app, or the IDE binds the stale last-built Debug reference assembly and reports
> phantom `CS0103` errors against new members. Build MMCA.Common with `-c Debug`, then build your app.

**Pick the framework version.** All thirteen packages move together. The current consumers are on
`1.77.0`. Choose one version and use it for every `MMCA.Common.*` entry (Phase 1). See
[ADR-016](ADRs/016-lockstep-versioning-masstransit-pin.md): there is no phased rollout and no version
skew across the thirteen packages.

---

## Phase 1: Create the solution and the build plumbing

The plumbing files are the load-bearing, easy-to-get-wrong part. Copy them from
`MMCA.ADC` (or `MMCA.Store`) and trim. Lay out the repo like this:

```
MMCA.Helpdesk/
  MMCA.Helpdesk.slnx
  Directory.Build.props
  Directory.Packages.props
  global.json
  nuget.config
  local.props.template            (copy to local.props for local-source mode; local.props is gitignored)
  .editorconfig                   (copy MMCA.ADC's verbatim; it drives the 5 analyzers)
  .gitignore
  Source/
    Modules/                      (one folder per business module)
    Hosts/                        (runnable entry points)
    Hosting/                      (Aspire AppHost + per-DB migrations projects)
    Services/                     (added later, in the extraction phase)
  Tests/
    Modules/  Architecture/  Integration/  E2E/
```

### `Directory.Packages.props` (Central Package Management)

Versions live here, not in individual `.csproj` files. List **all thirteen** `MMCA.Common.*` packages
at one version, and keep **MassTransit pinned to v8** (v9 needs a commercial license, enforced by a
build gate in MMCA.Common, see [ADR-016](ADRs/016-lockstep-versioning-masstransit-pin.md)):

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- MMCA Common packages: all thirteen at one version, bumped in lockstep -->
    <PackageVersion Include="MMCA.Common.Shared" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Domain" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Application" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Infrastructure" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.API" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Grpc" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.UI" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Aspire" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Aspire.Hosting" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Testing" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Testing.E2E" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Testing.UI" Version="1.77.0" />
    <PackageVersion Include="MMCA.Common.Testing.Architecture" Version="1.77.0" />
    <!-- Third-party versions: copy the relevant rows from MMCA.ADC/Directory.Packages.props -->
    <!-- (EF Core, FluentValidation, Riok.Mapperly, Scrutor, xunit.v3, Aspire.*, Yarp, the 5 analyzers, etc.) -->
  </ItemGroup>
</Project>
```

Then in each `.csproj` you reference a package with **no version**:
`<PackageReference Include="MMCA.Common.Domain" />`.

### `Directory.Build.props`

This sets the language/build mode, wires the five analyzers at error severity, links the per-module
identifier-alias files into every project, and declares the `.Contracts` gRPC convention. Copy
`MMCA.ADC/Directory.Build.props` and adapt the module-alias `<Compile Include ... Link>` block to your
modules. The critical pieces:

```xml
<Project>
  <!-- Optional: local.props enables local MMCA source instead of NuGet packages -->
  <Import Project="local.props" Condition="Exists('local.props')" />

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <AnalysisMode>All</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591;RMG020;EXTEXP0001</NoWarn>
  </PropertyGroup>

  <!-- The five analyzers, all at error severity (Meziantou, VS.Threading, Roslynator, Sonar, StyleCop) -->
  <ItemGroup Condition="'$(MSBuildProjectExtension)' != '.dcproj'">
    <PackageReference Include="Meziantou.Analyzer"> ... </PackageReference>
    <PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers"> ... </PackageReference>
    <PackageReference Include="Roslynator.Analyzers"> ... </PackageReference>
    <PackageReference Include="SonarAnalyzer.CSharp"> ... </PackageReference>
    <PackageReference Include="StyleCop.Analyzers"> ... </PackageReference>
  </ItemGroup>

  <!-- Identifier-type aliases linked into all projects (one block per module Shared project) -->
  <ItemGroup Condition="'$(MSBuildProjectExtension)' != '.dcproj'">
    <Compile Include="$(MSBuildThisFileDirectory)Source\Modules\Tickets\MMCA.Helpdesk.Tickets.Shared\MMCA.Helpdesk.Tickets.GlobalUsings.IdentifierType.cs"
             Link="GlobalUsings\MMCA.Helpdesk.Tickets.GlobalUsings.IdentifierType.cs"
             Condition="'$(MSBuildProjectName)' != 'MMCA.Helpdesk.Tickets.Shared'" />
    <!-- ...and one for Identity -->
  </ItemGroup>

  <!-- .Contracts convention: any *.Contracts project auto-compiles Protos/**/*.proto (server + client) -->
  <ItemGroup Condition="$(MSBuildProjectName.EndsWith('.Contracts'))">
    <PackageReference Include="Grpc.Tools"> ... </PackageReference>
    <PackageReference Include="Google.Protobuf" />
    <PackageReference Include="Grpc.Net.ClientFactory" />
    <Protobuf Include="Protos\**\*.proto" GrpcServices="Both" />
  </ItemGroup>
</Project>
```

> **Why the identifier-alias linking matters.** Each module declares `global using
> {Entity}IdentifierType = int;` (or `Guid`) in one file in its `*.Shared` project. The
> `<Compile Include ... Link>` block makes that alias visible in **every** project solution-wide.
> Always use the alias (`TicketIdentifierType`), never the raw `int`. See the Entity Identifier
> Convention in `MMCA.Common/CLAUDE.md`.

### `global.json`, `nuget.config`, `local.props.template`

```jsonc
// global.json: all three apps run on Microsoft Testing Platform (xUnit v3), not VSTest
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

```xml
<!-- nuget.config (NuGet mode): MMCA.* from GitHub Packages, everything else from nuget.org -->
<configuration>
  <packageSources>
    <add key="github-mmca" value="https://nuget.pkg.github.com/<your-org>/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <auditSources>            <!-- GitHub Packages serves no vuln data; restrict audit to nuget.org -->
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </auditSources>
  <packageSourceMapping>
    <packageSource key="github-mmca"><package pattern="MMCA.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
  <packageSourceCredentials>
    <github-mmca>
      <add key="Username" value="<your-user>" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </github-mmca>
  </packageSourceCredentials>
</configuration>
```

```xml
<!-- local.props.template: copy to local.props (gitignored) to build against MMCA.Common source -->
<Project>
  <PropertyGroup>
    <UseLocalMMCA>true</UseLocalMMCA>
    <LocalMMCAPath>$(MSBuildThisFileDirectory)..\MMCA.Common\Source\</LocalMMCAPath>
  </PropertyGroup>
</Project>
```

**Checkpoint:** `dotnet build MMCA.Helpdesk.slnx` succeeds on an empty solution (no projects yet, but
the plumbing parses).

---

## Phase 2: Scaffold the module project set

Each business module is a set of layered projects under `Source/Modules/<Module>/`. Mirror the ADC
breakdown (`MMCA.ADC/Source/Modules/Conference/`). For **Tickets**:

| Project | References | Holds |
|---|---|---|
| `MMCA.Helpdesk.Tickets.Shared` | `MMCA.Common.Shared`, `MMCA.Common.Domain` | DTOs, request records, **identifier aliases**, integration events, disabled stubs |
| `MMCA.Helpdesk.Tickets.Domain` | `MMCA.Common.Domain` | aggregate, child entities, invariants, domain events |
| `MMCA.Helpdesk.Tickets.Application` | `MMCA.Common.Application`, Riok.Mapperly, the Shared + Domain projects | use cases (command/query + handler), validators, mappers, module DI |
| `MMCA.Helpdesk.Tickets.Infrastructure` | `MMCA.Common.Infrastructure`, the Application project | EF entity configurations, infra DI |
| `MMCA.Helpdesk.Tickets.API` | `MMCA.Common.API`, the Application + Infrastructure projects | REST controllers, module API DI |

The layering is enforced twice by the framework (compile-time MSBuild guard + the NetArchTest rules in
Phase 6), so a forbidden reference fails the build. See
[ADR-015](ADRs/015-architecture-fitness-functions.md).

For **Identity**, the fastest start is to copy MMCA.ADC's or MMCA.Store's Identity module and rename
the namespaces (Store's Identity is local-credential + RS256 only, which is the simpler base). Identity
is intentionally generic across apps.

---

## Phase 3: The vertical slice end-to-end (the heart of it)

Implement Tickets create and read. This traces the same path for every feature you will ever add.
Pattern source for each step is the ADC `Event` aggregate.

### 3a. Domain: aggregate, invariants, events

Entities inherit the framework hierarchy: `BaseEntity<TId>` to `AuditableBaseEntity<TId>` (adds
soft-delete `IsDeleted` + audit fields) to `AuditableAggregateRootEntity<TId>` (adds domain events and
child-collection helpers). **Aggregates use factory methods that return `Result<T>`, never public
constructors.** See [ADR-013](ADRs/013-result-pattern.md).

```csharp
// Source/Modules/Tickets/MMCA.Helpdesk.Tickets.Domain/Tickets/Ticket.cs
public sealed class Ticket : AuditableAggregateRootEntity<TicketIdentifierType>
{
    private readonly List<TicketComment> _comments = [];
    public string Title { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public TicketStatus Status { get; private set; }
    public UserIdentifierType RequesterUserId { get; private set; }
    public IReadOnlyCollection<TicketComment> Comments => _comments.AsReadOnly();

    private Ticket() { }   // EF materializer

    public static Result<Ticket> Open(string title, string description, UserIdentifierType requesterUserId)
    {
        var validation = Result.Combine(
            TicketInvariants.TitleIsValid(title),
            TicketInvariants.DescriptionIsValid(description));
        if (validation.IsFailure) { return Result.Failure<Ticket>(validation.Errors); }

        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            Status = TicketStatus.Open,
            RequesterUserId = requesterUserId,
        };
        ticket.AddDomainEvent(new TicketOpened(ticket.Id, requesterUserId));   // dispatched after SaveChanges
        return Result.Success(ticket);
    }

    public Result ChangeStatus(TicketStatus next)
    {
        // ...guard illegal transitions, return Error.Invariant(...) on violation...
        Status = next;
        AddDomainEvent(new TicketStatusChanged(Id, next));
        return Result.Success();
    }
}
```

Invariants are static methods returning `Result`, combined with `Result.Combine(...)`:

```csharp
public static class TicketInvariants
{
    public const int TitleMaxLength = 200;
    public static Result TitleIsValid(string title) =>
        string.IsNullOrWhiteSpace(title) || title.Length > TitleMaxLength
            ? Result.Failure(Error.Validation("Ticket.Title", $"Title is required and <= {TitleMaxLength} chars."))
            : Result.Success();
}
```

`TicketComment` inherits `AuditableBaseEntity<TicketCommentIdentifierType>`. Manage children through
the aggregate root using the inherited `SetItems<T>()` / `GetChildOrNotFound<T>()` helpers rather than
mutating the list directly.

### 3b. Shared: DTO, request, integration event, aliases

```csharp
// MMCA.Helpdesk.Tickets.GlobalUsings.IdentifierType.cs  (linked solution-wide via Directory.Build.props)
global using TicketIdentifierType = int;
global using TicketCommentIdentifierType = int;
```

```csharp
public sealed record TicketDTO(int Id, string Title, string Description, string Status, int RequesterUserId);

// ICacheInvalidating: this command evicts cached Ticket reads on success (see decorator pipeline)
public sealed record TicketCreateRequest : ICreateRequest, ICacheInvalidating
{
    public string CachePrefix => $"{typeof(Ticket).FullName}:";
    public required string Title { get; init; }
    public required string Description { get; init; }
}

// Integration events carry SchemaVersion (default 1, fitness-enforced): see ADR-010
public sealed record TicketOpenedIntegrationEvent(int TicketId, int RequesterUserId) { public int SchemaVersion => 1; }
```

### 3c. Application: use case, validator, mapper, DI

A command handler implements `ICommandHandler<TCommand, TResult>` and stays thin: the decorator
pipeline supplies logging, caching, validation, and the transaction around it. A FluentValidation
validator and a Mapperly DTO mapper are auto-discovered by convention scanning.

```csharp
public sealed class CreateTicketHandler(IUnitOfWork unitOfWork, TicketDTOMapper mapper)
    : ICommandHandler<TicketCreateRequest, Result<TicketDTO>>
{
    public async Task<Result<TicketDTO>> HandleAsync(TicketCreateRequest command, CancellationToken cancellationToken)
    {
        var created = Ticket.Open(command.Title, command.Description, /* current user */ default);
        if (created.IsFailure) { return Result.Failure<TicketDTO>(created.Errors); }

        var repository = unitOfWork.GetRepository<Ticket, TicketIdentifierType>();
        await repository.AddAsync(created.Value!, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);   // stamps audit, captures events to outbox, dispatches
        return Result.Success(mapper.MapToDTO(created.Value!));
    }
}
```

Module DI uses C# extension types (the framework's registration idiom). `ScanModuleApplicationServices`
finds your handlers, validators, mappers, and domain-event handlers by convention:

```csharp
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddModuleTicketsApplication(ApplicationSettings applicationSettings)
        {
            // explicit registrations (query services, navigation populators, delete handlers) go here, then:
            services.ScanModuleApplicationServices<ClassReference>();   // ClassReference = an anchor type in this assembly
            return services;
        }
    }
}
```

### 3d. Infrastructure: EF configuration, no per-app DbContext

There is **one** concrete context in the whole system, the framework's sealed `SQLServerDbContext`.
Do **not** create a per-module or per-app DbContext class (see
[ADR-006](ADRs/006-database-per-service.md) and the "Don't split SQLServerDbContext" rule). You supply
EF configurations that inherit `EntityTypeConfigurationSQLServer<TEntity, TId>` (the base configures
`Id`, `IsDeleted`, audit fields, and the soft-delete filter), and you register your module's
configuration assembly so the context discovers them.

```csharp
internal sealed class TicketConfiguration : EntityTypeConfigurationSQLServer<Ticket, TicketIdentifierType>
{
    public override void Configure(EntityTypeBuilder<Ticket> builder)
    {
        base.Configure(builder);   // Id, IsDeleted, audit fields, soft-delete query filter
        builder.Property(t => t.Title).HasMaxLength(TicketInvariants.TitleMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>();
        builder.HasMany(t => t.Comments).WithOne().HasForeignKey(c => c.TicketId);
    }
}
```

You obtain a repository through `IUnitOfWork.GetRepository<Ticket, TicketIdentifierType>()`; you never
hand-write a DbContext or a repository class.

### 3e. API: controller and error mapping

Controllers derive a framework base (`ApiControllerBase` or the aggregate controller base) and inject
handlers directly. On failure, `HandleFailure(result.Errors)` maps the transport-agnostic `ErrorType`
to the right HTTP status as RFC 9457 ProblemDetails (Validation/Invariant to 400, NotFound to 404,
Conflict to 409, Unauthorized to 401, Forbidden to 403). See
[ADR-013](ADRs/013-result-pattern.md).

```csharp
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
public sealed class TicketsController(ICommandHandler<TicketCreateRequest, Result<TicketDTO>> createHandler)
    : ApiControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateAsync(TicketCreateRequest request, CancellationToken cancellationToken)
    {
        var result = await createHandler.HandleAsync(request, cancellationToken);
        return result.IsFailure ? HandleFailure(result.Errors) : CreatedAtAction(nameof(CreateAsync), result.Value);
    }
}
```

### What the pipeline does for you

Once `AddApplicationDecorators()` runs (Phase 5), every handler is wrapped by the Scrutor decorator
chain (outermost first). See [ADR-014](ADRs/014-cqrs-decorator-pipeline.md):

```
Commands: FeatureGate -> Logging -> Caching -> Validating -> Transactional -> your handler
Queries:  FeatureGate -> Logging -> Caching -> your handler
```

The order is load-bearing: validation runs before the transaction opens; cache invalidation happens
after a successful commit (outside the transaction); a business `Result.Failure` commits the
transaction but skips cache invalidation; an exception rolls the transaction back.

---

## Phase 4: DbContext model and migrations

Create **one migrations project per (future) service database**, even while you are a monolith. This
costs nothing now and means extraction (Phase 8) needs zero migration rework. Pattern source:
`MMCA.ADC/Source/Hosting/MMCA.ADC.Migrations.SqlServer.Conference/`.

```
Source/Hosting/MMCA.Helpdesk.Migrations.SqlServer.Tickets/
  MMCA.Helpdesk.Migrations.SqlServer.Tickets.csproj   (refs EF Design + SqlServer + the Tickets.Infrastructure project)
  DesignTimeSQLServerDbContextFactory.cs
  Migrations/   (generated)
```

The design-time factory uses the framework helper so `dotnet ef` can build a per-source context:

```csharp
public sealed class DesignTimeSQLServerDbContextFactory : IDesignTimeDbContextFactory<SQLServerDbContext>
{
    public SQLServerDbContext CreateDbContext(string[] args) =>
        DesignTimeDbContextHelper.CreateSqlServer(args, options =>
        {
            options.DataSourceName = "Tickets";
            options.DataSources["Tickets"] = new DataSourceEntrySettings
            {
                SQLServerConnectionString = Environment.GetEnvironmentVariable("HELPDESK_TICKETS_SQL")
                    ?? "Server=localhost;Database=Helpdesk_Tickets;Trusted_Connection=True;TrustServerCertificate=True",
                SQLServerMigrationsAssembly = typeof(DesignTimeSQLServerDbContextFactory).Assembly.GetName().Name!,
            };
            options.AddConfigurationAssembly(typeof(MMCA.Helpdesk.Tickets.Infrastructure.AssemblyReference).Assembly);
        });
}
```

Add the first migration (run per migrations project, always `--context SQLServerDbContext`):

```bash
dotnet ef migrations add InitialCreate \
  --project Source/Hosting/MMCA.Helpdesk.Migrations.SqlServer.Tickets \
  --startup-project Source/Hosting/MMCA.Helpdesk.Migrations.SqlServer.Tickets \
  --context SQLServerDbContext
```

At runtime the host applies migrations via the framework's `InitializeDatabaseAsync(...)` driven by
`ApplicationSettings.DatabaseInitStrategy`: `Migrate` (production, the host is the sole migrator),
`EnsureCreated` (quick local), or `None` (throws if migrations are pending, a safety check).

> **Monolith collapse:** with no `DataSources` section in config, every entity collapses onto one
> physical database (one context, FK constraints intact) and behaves exactly like a classic
> single-DB monolith. The same configurations and migrations later route to separate databases when
> you add `DataSources` entries in the extraction phase. This collapse is what makes "monolith now,
> services later" free. See [ADR-006](ADRs/006-database-per-service.md).

---

## Phase 5: Compose the monolith host and run it

### The Web host

Create `Source/Hosts/MMCA.Helpdesk.Web` (a Blazor or Web API host). Its `Program.cs` follows the
**fixed DI sequence** (decorators must wrap handlers that already exist, so `AddApplicationDecorators()`
comes after all module scans and `ScanModuleApplicationServices` calls):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();        // from MMCA.Common.Aspire: OpenTelemetry, health checks, resilience

var services = builder.Services;
services.AddOptions<ApplicationSettings>().Bind(builder.Configuration.GetSection(ApplicationSettings.SectionName));

services
    .AddApplication()                                   // core services, event dispatcher
    .AddModuleTicketsApplication(applicationSettings)   // your module scans (each calls ScanModuleApplicationServices)
    .AddModuleIdentityApplication(applicationSettings)
    .AddApplicationDecorators()                         // MUST be last
    .AddInfrastructure(builder.Configuration)           // repos, UoW, context, caching, outbox
    .AddAPI(modulesSettings);                           // controllers, idempotency, exception handlers

services.AddForwardedJwtBearer(authority: identityIssuer, audience: "helpdesk");   // validates RS256 via JWKS

var moduleLoader = new ModuleLoader();
moduleLoader.DiscoverAndRegister(services, builder.Configuration, applicationSettings, modulesSettings, builder.Environment.EnvironmentName);

var app = builder.Build();
await app.Services.InitializeDatabaseAsync(applicationSettings, moduleLoader);   // applies migrations / seeds
app.MapDefaultEndpoints();             // /health, /alive
app.UseCommonMiddlewarePipeline();     // exception -> correlation -> auth -> output-cache -> controllers
await app.RunAsync();
```

Modules are discovered by `ModuleLoader` and registered in topological dependency order (Kahn's
algorithm) from their `IModule` implementations. A module declares its `Name`, `Dependencies`, and
`Register(...)`; disabled peers get stub registrations so cross-module interfaces still resolve. See
[ADR-008](ADRs/008-service-extraction-topology.md). Monolith config: no `DataSources` section (one DB),
and no broker, so the framework selects the `InProcessMessageBus`.

### The Aspire AppHost

`Source/Hosting/MMCA.Helpdesk.AppHost` orchestrates the local stack. For the monolith it is small:

```csharp
using MMCA.Common.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var sql = builder.AddSqlServer("sql").WithLifetime(ContainerLifetime.Persistent);
var db = sql.AddDatabase("helpdesk", "Helpdesk");

builder.AddProject<Projects.MMCA_Helpdesk_Web>("web")
    .WithDataSource(db, "Tickets")   // injects ConnectionStrings__SQLServerConnectionString (collapses to one DB)
    .WaitFor(sql)                    // wait on the SQL server, NOT the database (see warning below)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
```

> **`WaitFor` the SQL server, not the database resource.** The host creates the database via EF
> `Migrate` at startup, so `WaitFor(db)` deadlocks: the `db` resource is never "healthy" until the
> database exists, but the only thing that creates it is the host that is waiting on it. The app
> resource sits at "Waiting" forever. Wait on the `sql` server resource (healthy once the container
> accepts connections) and let EF create the database. This mirrors MMCA.ADC/Store, which `WaitFor`
> the broker and peer services but never the database resource.

The AppHost also needs a `Properties/launchSettings.json` (the AppHost template always ships one).
Without it, the Aspire **dashboard endpoints are never configured**, so on F5 the dashboard never
opens, no browser launches, and the AppHost appears to hang at control-plane init. Copy ADC's and give
it its own ports:

```jsonc
// Source/Hosting/MMCA.Helpdesk.AppHost/Properties/launchSettings.json
{
  "profiles": {
    "https": {
      "commandName": "Project",
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17300;http://localhost:15300",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21300",
        "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22300"
      }
    }
  }
}
```

Run it:

```bash
dotnet run --project Source/Hosting/MMCA.Helpdesk.AppHost
```

> **Run it interactively, from a real terminal.** The Aspire AppHost stalls at control-plane init if
> launched from a headless or background shell (no dashboard appears). Use an interactive terminal for
> any manual verification.

Open the Aspire dashboard, then exercise the slice: `POST /Tickets`, then `GET /Tickets`. Confirm 201
then 200, that audit fields are stamped, that soft-deleted rows are filtered out, and that an outbox
row was written for `TicketOpened`.

---

## Phase 6: Tests and the architecture-fitness map

### Test projects

- `Tests/Modules/Tickets/...Domain.Tests` and `...Application.Tests`: xUnit v3 + AwesomeAssertions.
  Test factory methods, invariants, state transitions, and domain events. These run anywhere (no DB).
- `Tests/Integration/...IntegrationTests`: boot the host with `WebApplicationFactory` and use
  `IntegrationTestBase<TFixture>` plus `JwtTokenGenerator` (both from `MMCA.Common.Testing`). These
  need a reachable SQL Server, so run them in an environment that has one (Aspire, a container, or CI
  with a SQL service); they cannot run where no SQL is reachable.

### The architecture-fitness map (mandatory)

The framework enforces layering and module isolation by running the **same** NetArchTest rule library
(shipped in `MMCA.Common.Testing.Architecture`) against a per-repo map. You implement one
`IArchitectureMap` by subclassing `ArchitectureMapBase`: declare a `RepoToken` and list every layer
assembly. See [ADR-015](ADRs/015-architecture-fitness-functions.md). Pattern source: the
`*.Architecture.Tests` project in ADC or Store.

```csharp
public sealed class HelpdeskArchitectureMap : ArchitectureMapBase
{
    public override string RepoToken => "MMCA.Helpdesk";

    protected override IEnumerable<LayerRef> DefineLayers() =>
    [
        Framework(Layer.Shared, typeof(MMCA.Common.Shared.AssemblyReference).Assembly),
        Framework(Layer.Domain, typeof(MMCA.Common.Domain.AssemblyReference).Assembly),
        // ...the rest of the framework layers...
        Module("Tickets", Layer.Domain, typeof(MMCA.Helpdesk.Tickets.Domain.ClassReference).Assembly),
        Module("Tickets", Layer.Application, typeof(MMCA.Helpdesk.Tickets.Application.ClassReference).Assembly),
        // ...Tickets Infrastructure/Api/Shared, and the same for Identity...
    ];
}
```

Each test class is a tiny sealed subclass of a framework `*TestsBase` that supplies your map. The rules
then assert: the layer dependency flow, no cross-module internal references, and that MassTransit/gRPC
never leak into Domain/Application/Shared (transport stays at the edges).

**Checkpoint:** `dotnet build MMCA.Helpdesk.slnx` is warning-free (the five analyzers at error
severity), and the Domain/Application/Architecture test projects pass.

---

## Phase 7: Upgrading the framework version

When a new MMCA.Common release ships, upgrade in **one pass**: bump **every** `MMCA.Common.*` entry in
`Directory.Packages.props` to the new version together. There is no phased rollout and no per-package
skew (your app has no lock file, so the bump is the whole upgrade). Keep MassTransit at v8. See
[ADR-016](ADRs/016-lockstep-versioning-masstransit-pin.md) and `MMCA.Common/VERSIONING.md`.

For local framework co-development, flip `UseLocalMMCA` in `local.props`, and remember to rebuild
MMCA.Common in Debug before your app after editing framework source.

---

## Phase 8: Extract a module into its own service (the payoff)

Now make the "extract later, without a rewrite" promise concrete. We pull **Tickets** out of the
monolith into its own service behind a gateway. The Tickets Domain, Application, Shared, Infrastructure,
and API code is **unchanged**: only host wiring and transport are added. This works because the
application talks to abstractions (`IUnitOfWork`, `IMessageBus`, gRPC service interfaces) and the
framework keeps transport at the edges. See [ADR-008](ADRs/008-service-extraction-topology.md).

### 8a. A service host per module

Create `Source/Services/MMCA.Helpdesk.Tickets.Service` (and one for Identity). Each boots exactly one
module (`Modules:Tickets:Enabled=true`). Its `Program.cs` is the same DI sequence as the monolith host,
plus: **Http2-only Kestrel** on cleartext (h2c) for gRPC, `AddGrpcServiceDefaults()`, broker messaging,
and JWKS-validated auth. See [ADR-012](ADRs/012-grpc-host-transport.md) (Profile A). Pattern source:
`MMCA.ADC/Source/Services/MMCA.ADC.Conference.Service/Program.cs`.

```csharp
builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http2));
// ...same AddApplication/AddInfrastructure/AddAPI/ModuleLoader sequence...
services.AddGrpcServiceDefaults();
services.AddBrokerMessaging(builder.Configuration, x => x.RegisterIntegrationEventConsumer<SomeEvent>());
```

### 8b. A `.Contracts` project for synchronous calls

If Tickets needs a synchronous answer from Identity (for example, the requester's display name),
define it in `Source/Services/MMCA.Helpdesk.Identity.Contracts` as a `.proto`. The `.Contracts`
convention (from `Directory.Build.props`) auto-compiles it into server + client stubs. The consumer
registers a typed client with `AddTypedGrpcClient<TClient>(serviceName)` (from `MMCA.Common.Grpc`),
which resolves `http://identity` via Aspire service discovery over h2c, forwards the caller's JWT, and
wraps calls in the standard Polly pipeline. Failures cross the wire as `Result` via
`GrpcResultExceptionInterceptor`. See [ADR-007](ADRs/007-grpc-extraction.md).

### 8c. A YARP gateway

`Source/Hosts/MMCA.Helpdesk.Gateway` is a pure reverse proxy (no DbContext, no controllers). It maps
URL prefixes to backend services. The route map here is the source of truth for which service owns
which endpoint:

```csharp
app.MapForwarder("/Tickets/{**catch-all}", "http://tickets", http2Config);
app.MapForwarder("/Auth/{**catch-all}", "http://identity", http2Config);
app.MapForwarder("/.well-known/{**catch-all}", "http://identity", http2Config);   // JWKS, routed through the gateway
```

Set `ForwardHttp2 = true` (and `RequestVersionExact`) on the gRPC/JWKS routes so the proxy speaks
HTTP/2 to the Http2-only services. See [ADR-012](ADRs/012-grpc-host-transport.md).

### 8d. The AppHost grows up

Now wire the distributed topology with the `MMCA.Common.Aspire.Hosting` extensions:

```csharp
var sql = builder.AddSqlServer("sql").WithLifetime(ContainerLifetime.Persistent);
var identityDb = sql.AddDatabase("helpdesk-identity", "Helpdesk_Identity");
var ticketsDb  = sql.AddDatabase("helpdesk-tickets",  "Helpdesk_Tickets");
var redis  = builder.AddRedis("redis").WithLifetime(ContainerLifetime.Persistent);
var broker = builder.AddMessageBroker().WithLifetime(ContainerLifetime.Persistent);   // RabbitMQ

var identity = builder.AddProject<Projects.MMCA_Helpdesk_Identity_Service>("identity")
    .WithDataSource(identityDb, "Identity").WithReference(redis).WithBroker(broker).WithExternalHttpEndpoints();

var tickets = builder.AddProject<Projects.MMCA_Helpdesk_Tickets_Service>("tickets")
    .WithDataSource(ticketsDb, "Tickets").WithReference(redis).WithBroker(broker)
    .WithReference(identity).WaitFor(identity).WithExternalHttpEndpoints();

var gateway = builder.AddProject<Projects.MMCA_Helpdesk_Gateway>("gateway")
    .WithReference(identity).WithReference(tickets).WithExternalHttpEndpoints()
    .WithEndpoint("https", e => e.Port = 6001);

tickets.WithJwksDiscovery(identity, gateway);   // two-arg gateway form: tickets validates Identity's JWKS through the gateway
```

> Use the **two-argument** `WithJwksDiscovery(identity, gateway)` form. The single-argument form points
> the backchannel directly at the Http2-only Identity HTTPS endpoint and fails the local ALPN
> negotiation; routing JWKS through the gateway (which terminates TLS) is what works.

What changed for the application code: nothing. `WithDataSource` now gives each service its own database
(`Helpdesk_Identity`, `Helpdesk_Tickets`, each with its own `OutboxMessages` table, so services never
race for each other's outbox rows, see [ADR-006](ADRs/006-database-per-service.md)). The
`TicketOpenedIntegrationEvent` you wrote in Phase 3 now flows monolith-to-broker over MassTransit
instead of in-process, selected purely by configuration. Cross-service references become scalar columns
plus eventual consistency through the outbox, never cross-database foreign keys.

---

## Verification checklist

1. **Build green:** `dotnet build MMCA.Helpdesk.slnx` with no warnings (TreatWarningsAsErrors + five
   analyzers). This is the primary automatable gate.
2. **Unit + architecture tests pass:** run the Domain/Application/Architecture test projects via the
   Microsoft Testing Platform (`dotnet test --project <Tests.csproj>`). The `IArchitectureMap` rules
   must be green.
3. **Migrations:** `dotnet ef migrations add InitialCreate ...` succeeds for each migrations project.
4. **Run (interactive):** `dotnet run --project ...AppHost`, open the dashboard, `POST /Tickets` then
   `GET /Tickets`; confirm 201/200, stamped audit fields, soft-delete filtering, and an outbox row for
   `TicketOpened`.
5. **Extraction smoke:** after Phase 8, the dashboard shows Identity + Tickets + Gateway healthy; a
   request through the gateway to Tickets succeeds and JWKS-validates; the integration event is
   delivered over the broker.

---

## Where to look next

- **The ADRs** ([ADRs/README.md](ADRs/README.md)): the *why* behind every pattern you just used.
- **`MMCA.Common/CLAUDE.md`**: the framework's layer rules, DI sequence, and extension points in depth.
- **`Docs/Onboarding`**: a type-by-type tour of the framework internals.
- **MMCA.ADC and MMCA.Store**: two complete, production apps to copy patterns from. ADC is the richer
  template (four modules, OAuth social login, SignalR notifications); Store is the simpler one.
