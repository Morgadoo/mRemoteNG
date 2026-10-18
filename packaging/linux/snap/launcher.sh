#!/bin/sh
# Staged libraries (FreeRDP, unixODBC, ICU) live under $SNAP/usr/lib/<triplet>.
case "$(uname -m)" in
  x86_64) TRIPLET=x86_64-linux-gnu ;;
  aarch64) TRIPLET=aarch64-linux-gnu ;;
  *) TRIPLET="$(uname -m)-linux-gnu" ;;
esac
export LD_LIBRARY_PATH="$SNAP/usr/lib/$TRIPLET:$SNAP/usr/lib/$TRIPLET/freerdp3${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$SNAP/lib/mremoteng/mRemoteNG.Avalonia" "$@"
