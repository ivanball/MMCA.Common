namespace MMCA.Common.API.Resources;

/// <summary>
/// Resource anchor type for the framework's error-code translations. Its <c>.resx</c> siblings
/// (<c>ErrorResources.resx</c> / <c>ErrorResources.es.resx</c>) are keyed by domain error <c>Code</c>
/// (e.g. <c>"PhoneNumber.Empty"</c>) and resolved by <see cref="Localization.IErrorLocalizer"/> via
/// <c>IStringLocalizerFactory.Create(typeof(ErrorResources))</c> (ADR-027).
/// </summary>
public sealed class ErrorResources
{
}
