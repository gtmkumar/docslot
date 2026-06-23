---
name: feedback-execsqlraw-brace-literals
description: ExecuteSqlRaw/SqlQueryRaw treat the SQL as a composite format string when params are passed — literal {} braces throw FormatException
metadata:
  type: feedback
---

When calling `db.Database.ExecuteSqlRawAsync(sql, params...)` (or `SqlQueryRaw`) with one or more parameters, EF Core's `RawSqlCommandBuilder.Build` runs the SQL through `string.Format`. Any literal curly braces in the SQL (e.g. the empty-jsonb literal `'{}'::jsonb`) are parsed as format placeholders → `System.FormatException: Input string was not in a correct format ... Expected an ASCII digit` → HTTP 500.

**Why:** the `{}` collides with `{0}`-style composite formatting that EF applies only when parameters are present (the same SQL with no params is fine).

**How to apply:** never put literal `{`/`}` in raw SQL that also carries parameters. Produce empty jsonb with `jsonb_build_object()` (no args → `{}`) instead of `'{}'::jsonb`; for arrays use `jsonb_build_array()`. If braces are unavoidable, double them (`{{` `}}`). Hit while implementing per-day business_hours merge in [[project_rbac_rls_definer_writes]]'s sibling Settings slice: `business_hours = COALESCE(business_hours, jsonb_build_object()) || COALESCE(@p_business::jsonb, jsonb_build_object())` (FacilitySettingsServices.cs, SettingsRepository).
