#!/bin/sh
[ -f /var/lib/bds-headless/autostart ] && exit 0
case "$(systemctl is-system-running 2>/dev/null)" in
  initializing|starting) exit 1 ;;
  *) exit 0 ;;
esac
