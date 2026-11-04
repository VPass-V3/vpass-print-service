#!/bin/sh
# Register / restart the systemd unit. Pair afterwards: sudo vpass-print pair <code> --region <au|uk|us|ca>
/usr/local/vpass-print-service/vpass-print install || true
exit 0
