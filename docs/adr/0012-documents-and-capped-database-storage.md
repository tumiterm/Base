# 0012: Documents on top of files, capped database storage, engine-free generation

**Context.** Most projects need more than raw uploads. They need titled documents attached to records, with versions,
sign-off and retention, and they need to produce documents such as letters as PDFs. Some small files (signatures,
avatars, logos) are simpler to keep with the data than in a separate store. ADR 0005 banned database storage because
FO's production database filled up with uploads. The base must also not depend on a licensed report engine.

**Decision.**
- `IDocumentService` sits on top of `IFileService`. A `Document` has a type, a title, tags, an optional link to any
  record, a lifecycle (Draft → Final → Archived) and immutable `DocumentVersion`s. Each version's bytes are a
  `StoredFile`, so every upload goes through the same checks. Only drafts take new versions. Access is decided per
  document by `IDocumentAccessPolicy`, which projects can replace.
- Document types live in a catalogue (`DocumentTypeCatalog`). It sets the allowed extensions for each type and how
  long its documents are kept (archive after N days, delete after N days). A daily job applies the retention rules.
- **Amends ADR 0005.** A `Database` storage provider exists, but only for the categories that
  `Files.CategoryProviders` sends to it. It can't be the default provider. Settings cap the size of each file
  (`Files.Database.MaxFileSizeKb`, 1 MB by default) and the total per tenant (`Files.Database.TenantQuotaMb`,
  100 MB by default). Administrators change both at runtime, with no deployment. The bytes live in their own
  `FileContents` table, so metadata queries never read them.
- Deleted and expired files are purged after `Files.DeletedRetentionDays`, which frees space in object storage and
  in the database quota.
- `IFileUploadCheck` is a hook that runs every upload through extra checks before it is stored. ClamAV (clamd
  INSTREAM) is built in. It is switched on by configuring a host, and it fails closed.
- Generation: templates are HTML files with `{{ Variable }}` placeholders (values are HTML-encoded). There is one
  `IDocumentRenderer` per output format. HTML is built in. PDF comes from Gotenberg (MIT, a separate service).
  Projects add other engines, such as QuestPDF or a Word engine, as a renderer. Generated output is stored as a
  document through the same pipeline as uploads.

**Consequences.** Storing a file in the database is a deliberate, per-category choice with limits an administrator can
see and change, so it can't quietly fill the database again. The quota is a soft limit: two uploads at the same moment
can both pass it. A PDF needs the Gotenberg container. The only built-in format is HTML, which a browser can print to
PDF. The Gotenberg and ClamAV integrations were checked against real containers. The request formats are pinned by
tests that use fakes.
