#!/bin/sh
# Upgrades also run this; only stop on real removal.
case "$1" in
  upgrade|1) exit 0 ;;
esac
if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  systemctl disable --now bds-headless.service >/dev/null 2>&1 || true
fi
