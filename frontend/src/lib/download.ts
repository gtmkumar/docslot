// Trigger a browser download of an in-memory text payload (CSV export, etc.).
// Kept in lib so any feature can reuse it (the CSV exports fetch WITH auth headers
// into a transient blob, so a bare <a href> link wouldn't authenticate — the caller
// hands the {fileName, content} here to save it). Revokes the object URL after the
// click so we don't leak blobs.

export function downloadTextFile(fileName: string, content: string, mime = 'text/csv;charset=utf-8;'): void {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
