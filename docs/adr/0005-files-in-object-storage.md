# 0005: File bytes in object storage, metadata in SQL

**Context.** FO's production database filled up with uploaded files (September 2026).

**Decision.** There is no database provider. Bytes go to S3-compatible storage (R2/S3/MinIO) or Azure Blob, with local
disk for development only. SQL keeps a `StoredFile` row that records the provider, so files survive a change of default
provider. Uploads are validated by size, extension and magic bytes, and hashed with SHA-256.

**Consequences.** Backups of the database stay small. Object storage needs its own backup and lifecycle policy, and
soft-deleted files need a purge job.
