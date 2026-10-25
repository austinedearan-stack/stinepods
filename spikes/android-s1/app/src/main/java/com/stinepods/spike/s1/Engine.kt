package com.stinepods.spike.s1

import android.annotation.SuppressLint
import android.bluetooth.BluetoothA2dp
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothHeadset
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioManager
import android.media.AudioPlaybackConfiguration
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.SystemClock
import android.util.Log
import java.util.UUID

/**
 * Phase 0 spike engine (S1 connect/release, S4 media, S5 link). Lives in the app process so it can be driven
 * either from [MainActivity] (adb `am start`, brings the app to the front) or from [Listener]
 * (adb `am broadcast -a com.stinepods.spike.s1.CMD`, stays in the background so foreground-only apps keep playing).
 *
 * extras: op = list|connect|disconnect|active|release|take|media|pause|play|link
 *         method = reflect|policy|sdp, mac = device address, pkg = media app package part, count/gap for link
 *
 * Results go to logcat tag "S1". Timings are ms since the call was made.
 */
@SuppressLint("MissingPermission")
object Engine {

    private lateinit var ctx: Context
    private lateinit var adapter: BluetoothAdapter
    private var a2dp: BluetoothA2dp? = null
    private var headset: BluetoothHeadset? = null
    private val pending = mutableListOf<Intent>()
    private var t0 = 0L

    /** Optional on-screen log sink, set while [MainActivity] is visible. */
    var screen: ((String) -> Unit)? = null

    fun submit(context: Context, intent: Intent) {
        if (!::ctx.isInitialized) init(context.applicationContext)
        if (a2dp == null) pending += intent else handle(intent)
    }

    private fun init(context: Context) {
        ctx = context
        adapter = ctx.getSystemService(BluetoothManager::class.java).adapter
        ctx.registerReceiver(receiver, IntentFilter().apply {
            addAction(BluetoothA2dp.ACTION_CONNECTION_STATE_CHANGED)
            addAction(BluetoothHeadset.ACTION_CONNECTION_STATE_CHANGED)
            addAction(BluetoothA2dp.ACTION_PLAYING_STATE_CHANGED)
            addAction(ACTION_ACTIVE_DEVICE_CHANGED)
            addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
            addAction(BluetoothDevice.ACTION_ACL_DISCONNECTED)
            addAction(BluetoothDevice.ACTION_UUID)
        })
        adapter.getProfileProxy(ctx, object : BluetoothProfile.ServiceListener {
            override fun onServiceConnected(profile: Int, proxy: BluetoothProfile) {
                a2dp = proxy as BluetoothA2dp
                log("A2DP proxy ready (bt enabled=${adapter.isEnabled})")
                pending.forEach(::handle)
                pending.clear()
            }

            override fun onServiceDisconnected(profile: Int) {
                a2dp = null
                log("A2DP proxy lost")
            }
        }, BluetoothProfile.A2DP)
        adapter.getProfileProxy(ctx, object : BluetoothProfile.ServiceListener {
            override fun onServiceConnected(profile: Int, proxy: BluetoothProfile) {
                headset = proxy as BluetoothHeadset
                log("HFP proxy ready")
            }

            override fun onServiceDisconnected(profile: Int) {
                headset = null
            }
        }, BluetoothProfile.HEADSET)
    }

    private fun handle(intent: Intent) {
        val op = intent.getStringExtra("op") ?: "list"
        val method = intent.getStringExtra("method") ?: "reflect"
        val dev = intent.getStringExtra("mac")?.let { adapter.getRemoteDevice(it.uppercase()) }
        log("---- op=$op method=$method dev=${dev?.name}")
        if (op in MEDIA_OPS) {
            media(op, intent.getStringExtra("pkg") ?: "")
            return
        }
        if (op == "link") {
            link(dev ?: return log("missing --es mac (the PC)"), intent.getIntExtra("count", 100), intent.getIntExtra("gap", 50))
            return
        }
        if (op != "list" && dev == null) {
            log("missing --es mac")
            return
        }
        t0 = SystemClock.elapsedRealtime()
        try {
            when (op) {
                "list" -> list()
                "connect" -> when (method) {
                    "reflect" -> log("connect() -> ${call("connect", dev!!)}")
                    "policy" -> log("setConnectionPolicy(ALLOWED) -> ${call("setConnectionPolicy", dev!!, 100)}")
                    "sdp" -> log("fetchUuidsWithSdp() -> ${dev!!.fetchUuidsWithSdp()}")
                    else -> log("unknown method $method")
                }
                "disconnect" -> when (method) {
                    "reflect" -> log("disconnect() -> ${call("disconnect", dev!!)}")
                    "policy" -> log("setConnectionPolicy(FORBIDDEN) -> ${call("setConnectionPolicy", dev!!, 0)}")
                    else -> log("unknown method $method")
                }
                "active" -> log("setActiveDevice() -> ${call("setActiveDevice", dev!!)}")
                // Full hand-over: both profiles must go, or the HFP link keeps the headset tied to us.
                "release" -> {
                    log("hfp.disconnect() -> ${call("disconnect", dev!!, target = headset)}")
                    log("a2dp.disconnect() -> ${call("disconnect", dev)}")
                }
                "take" -> {
                    log("a2dp.connect() -> ${call("connect", dev!!)}")
                    if (intent.getBooleanExtra("hfp", true)) log("hfp.connect() -> ${call("connect", dev, target = headset)}")
                }
                else -> log("unknown op $op")
            }
        } catch (e: Throwable) {
            // InvocationTargetException wraps the real failure (usually SecurityException).
            log("FAILED after ${elapsed()}ms: ${e.cause ?: e}")
        }
    }

    // ---- S5: RFCOMM control link to the PC ----

    /** Connects to the PC's spike service, sends [count] pings [gap] ms apart, and logs round-trip times. */
    private fun link(pc: BluetoothDevice, count: Int, gap: Int) = Thread {
        val start = SystemClock.elapsedRealtime()
        try {
            pc.createRfcommSocketToServiceRecord(SPIKE_SERVICE).use { socket ->
                socket.connect()
                log("link connected in ${SystemClock.elapsedRealtime() - start}ms")
                val input = socket.inputStream.bufferedReader()
                val output = socket.outputStream
                val rtts = mutableListOf<Long>()
                repeat(count) { i ->
                    val t = SystemClock.elapsedRealtimeNanos()
                    output.write("ping $i\n".toByteArray())
                    output.flush()
                    val echo = input.readLine() ?: error("link closed")
                    check(echo == "ping $i") { "bad echo '$echo'" }
                    rtts += (SystemClock.elapsedRealtimeNanos() - t) / 1_000_000
                    Thread.sleep(gap.toLong())
                }
                rtts.sort()
                log("rtt ms over $count pings: min=${rtts.first()} median=${rtts[count / 2]} p95=${rtts[count * 95 / 100]} max=${rtts.last()}")
            }
        } catch (e: Exception) {
            log("link FAILED after ${SystemClock.elapsedRealtime() - start}ms: $e")
        }
    }.start()

    // ---- S4: media sessions (needs notification-listener access) ----

    private val sessions by lazy { ctx.getSystemService(MediaSessionManager::class.java) }
    private val listenerComponent by lazy { ComponentName(ctx, Listener::class.java) }
    private val controllers = mutableMapOf<MediaSession.Token, MediaController>()
    private var watchingMedia = false

    private fun media(op: String, pkg: String) {
        try {
            watchMedia()
        } catch (e: SecurityException) {
            log("no notification-listener access: $e")
            return
        }
        if (op == "media") return
        val c = controllers.values.firstOrNull { it.packageName.contains(pkg) }
        if (c == null) {
            log("no session for '$pkg'")
            return
        }
        t0 = SystemClock.elapsedRealtime()
        if (op == "pause") c.transportControls.pause() else c.transportControls.play()
        log("$op sent to ${c.packageName}")
    }

    private fun watchMedia() {
        if (watchingMedia) return
        sessions.addOnActiveSessionsChangedListener({ onSessions(it.orEmpty()) }, listenerComponent)
        onSessions(sessions.getActiveSessions(listenerComponent))
        // Fallback signal: any app's audio output, including apps without a media session (no package names here).
        ctx.getSystemService(AudioManager::class.java).registerAudioPlaybackCallback(object : AudioManager.AudioPlaybackCallback() {
            override fun onPlaybackConfigChanged(configs: MutableList<AudioPlaybackConfiguration>) {
                log("+${elapsed()}ms audio players=${configs.size} usages=${configs.map { it.audioAttributes.usage }}")
            }
        }, null)
        watchingMedia = true
    }

    private fun onSessions(list: List<MediaController>) {
        for (c in list) {
            if (controllers.containsKey(c.sessionToken)) continue
            controllers[c.sessionToken] = c
            log("session ${c.packageName}: ${playbackName(c.playbackState)}")
            c.registerCallback(object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) {
                    log("+${elapsed()}ms ${c.packageName} -> ${playbackName(state)}")
                }

                override fun onSessionDestroyed() {
                    controllers.remove(c.sessionToken)
                    log("session ${c.packageName} destroyed")
                }
            })
        }
    }

    private fun playbackName(s: PlaybackState?) = when (s?.state) {
        null -> "none"
        PlaybackState.STATE_PLAYING -> "PLAYING"
        PlaybackState.STATE_PAUSED -> "PAUSED"
        PlaybackState.STATE_STOPPED -> "STOPPED"
        PlaybackState.STATE_BUFFERING -> "BUFFERING"
        PlaybackState.STATE_ERROR -> "ERROR"
        else -> "state ${s.state}"
    }

    private fun list() {
        val proxy = a2dp ?: return
        for (d in adapter.bondedDevices) {
            log("${d.address} ${d.name} a2dp=${stateName(proxy.getConnectionState(d))}")
        }
        log("connected=${proxy.connectedDevices.map { it.name }}")
        val active = runCatching { call("getActiveDevice") as BluetoothDevice? }
        log("active=${active.getOrNull()?.name} ${active.exceptionOrNull() ?: ""}")
    }

    /** Reflective call on a profile proxy (A2DP by default); Int args map to primitive int, everything else to BluetoothDevice. */
    private fun call(name: String, vararg args: Any, target: BluetoothProfile? = a2dp): Any? {
        val proxy = target ?: error("profile proxy not ready")
        val types = args.map { if (it is Int) Int::class.javaPrimitiveType!! else BluetoothDevice::class.java }
        val m = proxy.javaClass.getMethod(name, *types.toTypedArray())
        return m.invoke(proxy, *args)
    }

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            @Suppress("DEPRECATION")
            val d = intent.getParcelableExtra<BluetoothDevice>(BluetoothDevice.EXTRA_DEVICE)
            val state = intent.getIntExtra(BluetoothProfile.EXTRA_STATE, Int.MIN_VALUE)
            val extra = if (state == Int.MIN_VALUE) "" else " state=${stateName(state)}"
            val profile = when {
                intent.action!!.contains("headset") -> "HFP "
                intent.action!!.contains("a2dp") -> "A2DP "
                else -> ""
            }
            log("+${elapsed()}ms $profile${intent.action!!.substringAfterLast('.')} ${d?.name}$extra")
        }
    }

    private fun elapsed() = SystemClock.elapsedRealtime() - t0

    private fun stateName(s: Int) = when (s) {
        BluetoothProfile.STATE_DISCONNECTED -> "DISCONNECTED"
        BluetoothProfile.STATE_CONNECTING -> "CONNECTING"
        BluetoothProfile.STATE_CONNECTED -> "CONNECTED"
        BluetoothProfile.STATE_DISCONNECTING -> "DISCONNECTING"
        BluetoothA2dp.STATE_PLAYING -> "PLAYING"
        BluetoothA2dp.STATE_NOT_PLAYING -> "NOT_PLAYING"
        else -> s.toString()
    }

    private fun log(msg: String) {
        Log.i(TAG, msg)
        screen?.invoke(msg)
    }

    const val CMD_ACTION = "com.stinepods.spike.s1.CMD"
    private const val TAG = "S1"
    private val MEDIA_OPS = setOf("media", "pause", "play")
    private val SPIKE_SERVICE: UUID = UUID.fromString("8198aff7-3480-44df-af7f-bf9bd001588c")
    private const val ACTION_ACTIVE_DEVICE_CHANGED = "android.bluetooth.a2dp.profile.action.ACTIVE_DEVICE_CHANGED"
}
