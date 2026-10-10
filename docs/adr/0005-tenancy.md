# ADR 0005: Household tenancy and roles

Status: accepted

## Context
Multi-family from day one.

## Decision
Shared schema, HouseholdId on every row, EF global query filter via ICurrentHousehold, global catalog rows with null HouseholdId. Roles per Membership: Owner, Adult, Child. Policy-based authorization. Kid profiles are credential-less profiles on a logged-in device; PIN to switch back to an adult.

## Consequences
Mandatory isolation test suite; optional SQL row-level security later.
