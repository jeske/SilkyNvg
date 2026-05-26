#!/bin/bash
# mac-veldrid.sh — Launch the Veldrid Example on macOS
# Auto-discovers the best backend (Metal on Apple Silicon).
# Pass --opengl or --metal to override.
#
# Usage:
#   ./cmd/mac-veldrid.sh              # auto-select best backend
#   ./cmd/mac-veldrid.sh --opengl     # force OpenGL
#   ./cmd/mac-veldrid.sh --metal      # force Metal

set -e
cd "$(dirname "$0")/.."

DOTNET_ROLL_FORWARD=LatestMajor exec dotnet run \
    --project "samples/Veldrid Example/Veldrid Example.csproj" \
    --roll-forward LatestMajor \
    -- "$@"
