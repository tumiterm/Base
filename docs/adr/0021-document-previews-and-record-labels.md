# 0021: In-browser document previews and record labels

**Context.** Documents could only be downloaded, so checking a payslip or a scan meant a file in the downloads folder
every time. Documents linked to a record showed the record's type and GUID, which tells a person nothing.

**Decision.**
- **Preview endpoint.** `GET documents/{id}/preview` returns the version inline after the same access checks as a
  download. It always streams through the application, never redirects to object storage, because a pre-signed link
  is another origin and forces a download.
- **What is previewed** (`FilePreviews`): PDF, PNG/JPEG/GIF/WebP/BMP/AVIF images, plain text, CSV, JSON, XML,
  generated HTML, MP4/WebM video and common audio. Text-like files are served as `text/plain`. SVG is never
  previewed because it can carry script. Office files show a clear "no preview, download it" state: converting them
  needs a licensed or heavy engine, which the base doesn't take on.
- **Framing safely.** The page CSP stays strict. A preview response gets its own policy: framed by the application's
  own pages only (`frame-ancestors 'self'`, `X-Frame-Options: SAMEORIGIN`), and, for everything except PDF (browsers
  won't render PDFs in a sandbox), `sandbox` with `default-src 'none'`, so nothing in the file can run. Text and HTML
  frames are sandboxed by the `iframe` too.
- **Where.** On the document page, a viewer for any version; from lists and the record's Documents tab, a preview
  drawer. `Documents.PreviewEnabled` switches it all off.
- **Record labels.** `IRecordLabelProvider` (one per linkable entity type, returning only records the user may see)
  and `IRecordLabels` turn `(EntityType, Id)` pointers into names, one query per type. Web maps types to pages and
  icons (`RecordLinks`). Unknown or hidden records show their type only, never an id. Exports use the same labels.

**Consequences.**
- Previews cost the application's bandwidth instead of the storage provider's. Range requests keep large PDFs usable.
- Each module that lets documents (or anything else) point at its records registers a label provider and a route;
  `docs/vertical-slice.md` lists it.
