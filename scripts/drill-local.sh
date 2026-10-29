#!/bin/bash
# M5 failure drills against the local compose stack (TDD M5 + failure-modes table): platform killed mid-run,
# outage longer than every retry (DLQ path), and a job whose queue message never published (sweeper path).
# They stop and restart containers, so they run on their own, after the main suite.
set -euo pipefail
cd "$(dirname "$0")/.."
DOCBRIDGE_DRILLS=local dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5DrillTests"
