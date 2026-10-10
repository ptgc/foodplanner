# ADR 0003: Containers on Azure Container Apps, nginx web container

Status: accepted

## Context
Everything should run in containers locally and in Azure.

## Decision
API and web as containers on Container Apps, images in ACR. nginx serves the SPA and proxies /api for a single origin.

## Consequences
No CORS; first-party cookies; slightly more infra than Static Web Apps.
