# ADR 0001: React Router v7 SPA frontend

Status: accepted

## Context
Needs offline/PWA, wake lock, microphone, rich mobile UI. Architect has some React/TS experience; Blazor considered.

## Decision
React Router v7 in SPA mode (ssr: false), TypeScript strict. Generated OpenAPI client.

## Consequences
Two languages; mitigated by generated client. Blazor rejected due to PWA/offline/browser API friction and WASM startup.
