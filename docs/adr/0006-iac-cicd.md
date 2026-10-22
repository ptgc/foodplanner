# ADR 0006: Bicep and GitHub Actions

Status: accepted

## Context
Architect prefers IaC and knows Terraform.

## Decision
Bicep with Azure Verified Modules, .bicepparam per env, what-if on PR. GitHub Actions with OIDC federated credentials. Deploy dev automatically; prod behind Environment approval. Migration bundles run as a pipeline step.

## Consequences
No state file; consider deployment stacks for deletion tracking. Environments map to branches as set out in ADR 0009: push to `dev` deploys dev; `test` and `prod` deploy from their own branches, `prod` behind Environment approval.
