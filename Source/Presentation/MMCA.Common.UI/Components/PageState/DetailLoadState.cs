namespace MMCA.Common.UI.Components.PageState;

/// <summary>
/// Where a single-record page's load stands. "The record does not exist" and "the record could not
/// be fetched" are separate states on purpose: a failed load on venue Wi-Fi is the common case, and
/// telling the reader the record is missing tells them not to try again.
/// </summary>
public enum DetailLoadState
{
    /// <summary>A load is in flight and nothing has been shown yet.</summary>
    Loading,

    /// <summary>The record is loaded and the page renders it.</summary>
    Loaded,

    /// <summary>The server answered that the record does not exist (HTTP 404).</summary>
    NotFound,

    /// <summary>The load failed for any other reason; the page offers a retry.</summary>
    Failed,
}
