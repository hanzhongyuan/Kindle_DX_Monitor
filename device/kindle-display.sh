#!/bin/sh
# Start the DX viewer and restore the normal reader on exit/disconnect.
cd /mnt/us/kindlevncviewer || exit 1
export LD_LIBRARY_PATH=/mnt/us/kindlevncviewer

pidfile=/tmp/kindle-display.pid
if [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; then
    echo "Kindle display is already running."
    exit 1
fi
echo $$ > "$pidfile"
viewer_pid=
old_screensaver=$(lipc-get-prop com.lab126.powerd preventScreenSaver 2>/dev/null)
case "$old_screensaver" in 0|1) ;; *) old_screensaver=0 ;; esac

cleanup() {
    trap - 0 HUP INT TERM
    if [ -n "$viewer_pid" ]; then
        kill "$viewer_pid" 2>/dev/null
        wait "$viewer_pid" 2>/dev/null
    fi
    lipc-set-prop com.lab126.powerd preventScreenSaver "$old_screensaver" 2>/dev/null
    rm -f "$pidfile"
    # The author's original launcher uses two Menu key events to redraw the reader.
    if [ -w /proc/keypad ]; then
        echo 'send 139' > /proc/keypad
        echo 'send 139' > /proc/keypad
    fi
}
trap cleanup 0
trap 'exit 0' HUP INT TERM
lipc-set-prop com.lab126.powerd preventScreenSaver 1
./kindlevncviewer -config config.lua "${1:-127.0.0.1:5}" &
viewer_pid=$!
wait "$viewer_pid"
exit $?
