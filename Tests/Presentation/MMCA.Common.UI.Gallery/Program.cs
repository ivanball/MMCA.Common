using MMCA.Common.UI.Gallery;

// The whole host build lives in GalleryHost.BuildApp so MMCA.Common.UI.E2E.Tests can self-host
// the same configured app in-process (StartAsync on an ephemeral Kestrel port) without a separate
// `dotnet run` + health-poll — avoiding the cold-start/background-process fragility that bit ADC's
// e2e.yml.
var app = GalleryHost.BuildApp(args);
await app.RunAsync();
