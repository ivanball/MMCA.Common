// Saves a file the caller already fetched (ApiFileDownloadButton with RequiresAuthentication) on a
// browser head. An authenticated endpoint needs the caller's bearer token, so a plain download link
// cannot reach it; the component fetches the bytes through its authenticated API client and hands
// them here as a stream, and this module turns them into a temporary object URL the browser saves
// under the given name. Native heads never call it: they stage the file and open the share sheet.
export async function saveFile(fileName, contentType, contentStreamReference) {
    const buffer = await contentStreamReference.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
    try {
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName;
        anchor.rel = 'noopener';
        anchor.style.display = 'none';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
    } finally {
        // Revoked on the next tick: the click has already handed the URL to the download manager.
        setTimeout(() => URL.revokeObjectURL(url), 0);
    }
}
