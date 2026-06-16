// Parallelization is ENABLED (the DisableTestParallelization workaround from slice 05 is removed). The
// audit hash-chain trigger (platform.append_to_audit_chain) is now concurrency-safe: it takes a fixed
// pg_advisory_xact_lock before reading the chain head, so concurrent audit_log INSERTs serialize and the
// chain cannot fork (slice 03b fix). A dedicated concurrency test proves the chain stays verifiable under
// parallel writes. Test fixtures isolate their data with per-instance GUIDs.
