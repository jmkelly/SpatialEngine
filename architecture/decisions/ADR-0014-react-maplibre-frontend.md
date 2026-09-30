---
status: accepted
date: 2026-08-30
deciders: maintainer + agent
summary: React + TypeScript + MapLibre frontend; talks only to the public host API.
---

# ADR-0014: React, TypeScript and MapLibre form the first frontend

## Context

The first frontend must be deliverable in a normal browser, render GIS data
well, and depend only on the public host API.

## Decision

The workbench is React with TypeScript, MapLibre GL JS, a generated
TypeScript SDK and engine-neutral application state. All communication uses
the public spatial host API.

## Consequences

- One web client runs in browsers and later unchanged inside Tauri.
- No spatial logic lives in the client; it only renders and orchestrates.
- Browser tests (Playwright) run without any desktop shell.