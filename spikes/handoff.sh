#!/usr/bin/env bash
# Phase 0 spike S6: scripted end-to-end hand-off using the spike tools, mimicking what the coordinator will do:
# pause media on the losing side -> release the headset -> take it on the winning side -> resume media there.
# adb and process start-up add overhead the real agents won't have, so these timings are an upper bound.
#
# usage: handoff.sh to-pc|to-phone <headset-name-part> <headset-mac> <phone-app-part> <pc-app-part>
set -u
dir=$(cd "$(dirname "$0")" && pwd)
s2="$dir/windows-s2/bin/Release/net10.0-windows10.0.19041.0/s2.exe"
s3="$dir/windows-s3/bin/Release/net10.0-windows10.0.19041.0/s3.exe"
adb="timeout 15 ${ANDROID_HOME:-/c/Android/Sdk}/platform-tools/adb.exe"
dirn=$1 headset=$2 mac=$3 phoneapp=$4 pcapp=$5

start=$(date +%s%3N)
step() { echo "$(( $(date +%s%3N) - start ))ms  $*"; }
# Background route (via the spike's notification listener) so foreground-only apps like free YouTube keep playing.
phone() { $adb shell am broadcast -a com.stinepods.spike.s1.CMD -p com.stinepods.spike.s1 --es op "$1" --es mac "$mac" --es pkg "$phoneapp" >/dev/null 2>&1; }
# Wait (up to $2 s) for a phone log line matching $1.
await_phone() { for _ in $(seq 1 $(( $2 * 10 ))); do $adb logcat -d -s S1:I | grep -qE -- "$1" && return 0; sleep 0.1; done; return 1; }

$adb logcat -c
if [ "$dirn" = to-pc ]; then
  phone pause;                                         step "phone: pause sent ($phoneapp)"
  phone release
  await_phone 'ACL_DISCONNECTED' 5 && step "phone: headset released" || step "phone: release NOT confirmed"
  $s2 take-sdp "$headset" 10 | grep -qE 'goal reached' && step "pc: headset connected (stereo active)" || step "pc: take FAILED"
  $s3 play "$pcapp" 5 | grep -qE 'confirmed' && step "pc: resumed ($pcapp)" || step "pc: resume NOT confirmed"
else
  $s3 pause "$pcapp" 5 | grep -qE 'confirmed' && step "pc: paused ($pcapp)" || step "pc: pause NOT confirmed"
  $s2 release-hci "$headset" 6 | grep -qE 'Disconnected' && step "pc: headset released" || step "pc: release NOT confirmed"
  phone take
  await_phone 'A2DP CONNECTION_STATE_CHANGED.*CONNECTED$' 8 && step "phone: headset connected" || step "phone: take FAILED"
  $adb logcat -c; phone play
  await_phone '-> PLAYING' 5 && step "phone: resumed ($phoneapp)" || step "phone: resume NOT confirmed"
fi
step "done"
