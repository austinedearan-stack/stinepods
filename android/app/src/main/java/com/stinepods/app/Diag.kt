package com.stinepods.app

import android.util.Log

/** Logging (logcat tag "StinePods"; local only, docs/SECURITY.md #9) plus the live status line shown in the UI. */
object Diag {
    private const val TAG = "StinePods"

    fun info(m: String) { Log.i(TAG, m) }
    fun warn(m: String) { Log.w(TAG, m) }

    @Volatile
    var status: String = "Starting…"
        private set
    private val listeners = mutableSetOf<(String) -> Unit>()

    fun setStatus(s: String) {
        status = s
        synchronized(listeners) { listeners.toList() }.forEach { it(s) }
    }

    fun listen(l: (String) -> Unit) { synchronized(listeners) { listeners += l } }
    fun unlisten(l: (String) -> Unit) { synchronized(listeners) { listeners -= l } }
}
