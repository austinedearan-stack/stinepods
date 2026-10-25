package com.stinepods.app.link

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import com.stinepods.app.Diag
import java.io.IOException
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Keeps an RFCOMM link to the PC agent open (protocol/PROTOCOL.md), reconnecting with backoff. Works offline: no
 * Wi-Fi or internet involved. Callbacks run on the link thread; the owner hands them to its own thread.
 */
@SuppressLint("MissingPermission")
class LinkClient(
    private val onConnected: (Connection) -> Unit,
    private val onData: (Connection, ByteArray, Int) -> Unit,
    private val onClosed: (Connection) -> Unit,
) {
    companion object {
        val SERVICE: UUID = UUID.fromString("8198aff7-3480-44df-af7f-bf9bd001588c")
        private val BACKOFF_MS = longArrayOf(2_000, 5_000, 10_000, 30_000, 60_000)
        private const val CONNECT_TIMEOUT_MS = 12_000L
    }

    private val watchdog = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "stinepods-link-watchdog").apply { isDaemon = true } }

    private val lock = java.lang.Object()
    @Volatile private var target: BluetoothDevice? = null
    @Volatile private var running = false
    private var thread: Thread? = null
    private var attempt = 0

    /** Starts (or retargets) the link to [pc]; null stops it. */
    fun connectTo(pc: BluetoothDevice?) {
        synchronized(lock) {
            target = pc
            running = pc != null
            attempt = 0
            lock.notifyAll()
            if (running && thread?.isAlive != true) thread = Thread(::loop, "stinepods-link").apply { isDaemon = true; start() }
        }
    }

    /** Retry now instead of waiting out the backoff (e.g. the user opened the app). */
    fun kick() = synchronized(lock) { attempt = 0; lock.notifyAll() }

    private fun loop() {
        while (running) {
            val pc = target ?: break
            try {
                pc.createRfcommSocketToServiceRecord(SERVICE).use { socket ->
                    // connect() has no timeout and can hang for a minute when the PC's stack is unhappy; closing
                    // the socket from another thread aborts it.
                    val abort = watchdog.schedule({ runCatching { socket.close() } }, CONNECT_TIMEOUT_MS, TimeUnit.MILLISECONDS)
                    try {
                        socket.connect()
                    } finally {
                        abort.cancel(false)
                    }
                    attempt = 0
                    val conn = Connection(socket)
                    Diag.info("link: connected to ${pc.address}")
                    onConnected(conn)
                    val buf = ByteArray(4096)
                    try {
                        while (true) {
                            val n = socket.inputStream.read(buf)
                            if (n < 0) break
                            onData(conn, buf, n)
                        }
                    } catch (e: IOException) {
                        Diag.info("link: closed (${e.message})")
                    }
                    conn.close()
                    onClosed(conn)
                }
            } catch (e: IOException) {
                Diag.info("link: can't reach PC (${e.message})")
            }
            synchronized(lock) {
                if (!running) return
                lock.wait(BACKOFF_MS[minOf(attempt++, BACKOFF_MS.size - 1)])
            }
        }
    }

    class Connection(private val socket: BluetoothSocket) {
        @Volatile var closed = false
            private set

        fun send(frame: ByteArray): Boolean = synchronized(this) {
            if (closed) return false
            return try {
                socket.outputStream.write(frame)
                socket.outputStream.flush()
                true
            } catch (e: IOException) {
                close()
                false
            }
        }

        fun close() {
            closed = true
            runCatching { socket.close() }
        }
    }
}
