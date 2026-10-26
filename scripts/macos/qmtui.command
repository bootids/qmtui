#!/bin/bash
# Finder opens .command files in Terminal; the application itself remains a TUI.
exec /usr/local/bin/qmtui "$@"
