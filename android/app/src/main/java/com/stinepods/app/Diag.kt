package com.stinepods.app

import android.util.Log
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Logging (logcat tag "StinePods") plus the live status line shown in the UI. The last few hundred lines are kept
 * in memory so they can be shared from the app or forwarded to the PC over the link, which is how phones without a
 * USB debugging connection are diagnosed. Logs never leave the user's own devices (docs/SECURITY.md #9).
 */
object Diag {
    private const val TAG = "StinePods"
    private const val KEEP = 400

    private val lines = ArrayDeque<String>()
    private val time = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    /** Receives each new line (set while the PC link is up); must not log itself. */
    @Volatile
    var sink: ((String) -> Unit)? = null

    fun info(m: String) { Log.i(TAG, m); keep("I", m) }
    fun warn(m: String) { Log.w(TAG, m); keep("W", m) }

    private fun keep(level: String, m: String) {
        val line = "${synchronized(time) { time.format(Date()) }} $level $m"
        synchronized(lines) {
            lines.addLast(line)
            while (lines.size > KEEP) lines.removeFirst()
        }
        sink?.invoke(line)
    }

    fun recent(max: Int = KEEP): List<String> = synchronized(lines) { lines.toList().takeLast(max) }

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
