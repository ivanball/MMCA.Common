// Entry point for the in-repo FACTS.md generator/verifier.
// All logic lives in FactsGenerator (shared, linked into the workspace Tools/invtool too).
//   dotnet run --project build/facts -- <MMCA.Common repo root> [outputPath] [--check]
FactsGenerator.Run(args);
