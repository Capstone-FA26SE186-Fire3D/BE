# Worker cleanup permissions

Pending registration cleanup is a legacy maintenance operation. OTP registration
does not create an account before verification. `cleanup_pending_registrations`
accepts a batch of 1–100, locks lifecycle/user/verification, rechecks expiry using
database time and preserves accounts with business/history references. It never
cascades audit or support history. Only an empty organization owned by that
registration can be removed. FK failure retains the account and its tokens.

Runtime roles receive EXECUTE only. The restricted NOLOGIN function owner can
read FK references and remove maintenance children; API DELETE on users and
organizations remains revoked. Browser roles cannot invoke maintenance gates.
Permission errors are logged as SQLSTATE and retried after 60 seconds.

Deploy the forward migration before the matching binary. Check effective login
membership, function ownership, RLS and restored temporary migration grants.
The tests use disposable PostgreSQL and actual migration history; passing them
does not prove Supabase grants or a deployed worker are up to date.

Avatar mutations enqueue through `avatar_cleanup_gate` in their existing
transaction. The API has no direct queue DML. Claim reconciles abandoned copy
candidates, leases one job, and the worker renews before a bounded 45-second S3
delete. NotFound succeeds. Complete/retry reject expired or superseded leases.
RLS reference reads run as a restricted owner that sees every protecting profile
and intent. Protected jobs remain queued. Staging/candidate grace is one hour;
this is not proof an indefinitely stalled storage request has stopped.
Logs contain job ID/attempt/error type/SQLSTATE, never raw object keys.
