# 0014: One job scheduler, dependency health checks, a recorded error log

**Context.** Each housekeeping task (audit, notification, file and document retention, outbox cleanup) ran on its
own timer inside its own background service. On several instances each one ran everywhere, nothing recorded whether
it ran, and a failure only showed up in the logs. Health meant one database check. "Quote reference E-..." pointed at
a log line that support had to find by hand.

**Decision.**
- **One scheduler.** Housekeeping is an `IScheduledJob` (code, interval, `RunAsync` returning a summary).
  `JobSchedulerService` runs due jobs on whichever instance wins the job's lease, a conditional UPDATE on
  `ScheduledJobs`. Each run gets a fresh scope, system scope and the "System" user, and is recorded in
  `ScheduledJobRuns` (kept for `Jobs:HistoryDays`). "Run now" makes a job due and wakes the scheduler. Hangfire stays
  optional, for project jobs that need queues and retries.
- **Health checks for every dependency.**
  - The checks: storage providers, the database-storage quota (degraded at 80%), TCP reachability of switched-on
    notification providers (no message sent, no credentials used), Gotenberg, ClamAV (PING), outbox backlog and dead
    letters, scheduled jobs, and Hangfire.
  - Only the database is tagged `ready`: a broken SMTP server must not take the app out of the load balancer.
  - Everything appears on `/admin/system` and `/api/system/health`; both need `System.Health.View`.
- **Errors are recorded.** The background error reporter writes every unhandled error to `ErrorLog`, then emails it
  (throttled as before). It runs off the request, so a database outage can't loop.
  - Errors can be searched by ticket. They link to the trace and the log search through `Observability:*UrlTemplate`.
  - Each tenant sees its own errors, plus errors from before a tenant was resolved.
  - Errors are kept for `System.ErrorRetentionDays`.
- **Database health.** `SlowQueryInterceptor` logs commands over `Database:SlowQueryThresholdMs` with their SQL (no
  parameter values) and trace id, and counts them on the `Base` meter. EF Core's own meter is exported as well.
- **Searchable logs.** Seq in docker-compose (profile `logs`). Development sends Serilog to it.

**Consequences.** New housekeeping is one class and one DI line, and appears on the system page with its history.
A job that runs longer than its lease (an hour at least) could start a second time on another instance; jobs must be
re-runnable anyway. Connection-pool statistics are not exported: SqlClient publishes them as event counters, not as a
meter.
