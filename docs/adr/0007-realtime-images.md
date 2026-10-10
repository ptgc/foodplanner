# ADR 0007: SignalR and Blob storage

Status: accepted

## Context
Shared live shopping list; image-heavy UI.

## Decision
SignalR (Azure SignalR Service when scaling out). Images in Blob Storage, resized to fixed sizes on upload, served via CDN.

## Consequences
Fixed sizes only; no on-the-fly resizing.
