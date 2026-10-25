package com.stinepods.app

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioManager
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.SystemClock
import com.stinepods.app.bt.Profiles
import com.stinepods.app.link.LinkClient
import com.stinepods.app.media.MediaWatch
import com.stinepods.core.Action
import com.stinepods.core.Describe
import com.stinepods.core.Engine
import com.stinepods.core.Event
import com.stinepods.core.FrameReader
import com.stinepods.core.LinkEvent
import com.stinepods.core.Msg
import com.stinepods.core.Role
import com.stinepods.core.Session
import com.stinepods.core.Step

/**
 * The phone side of StinePods: a foreground service that wires Bluetooth, media and the PC link to the [Engine].
 * Everything runs on one handler thread; nothing polls. Pairing codes and Ask prompts are notifications with actions.
 */
@SuppressLint("MissingPermission")
class StinePodsService : Service() {
    companion object {
        const val ACTION_RELOAD = "com.stinepods.RELOAD"
        const val ACTION_TOGGLE = "com.stinepods.TOGGLE"
        private const val ACTION_PAIR = "com.stinepods.PAIR"
        private const val ACTION_ANSWER = "com.stinepods.ANSWER"
        private const val EXTRA_YES = "yes"
        private const val EXTRA_ID = "id"
        private const val CH_STATUS = "status"
        private const val CH_PROMPT = "prompt"
        private const val N_STATUS = 1
        private const val N_PAIR = 2
        private const val N_ASK = 3
        private const val N_NOTE = 4
        private const val KEEP_ALIVE_MS = 4000L

        fun start(context: Context, action: String? = null) {
            context.startForegroundService(Intent(context, StinePodsService::class.java).setAction(action))
        }
    }

    private val thread = HandlerThread("stinepods").apply { start() }
    private val handler = Handler(thread.looper)
    private val timers = mutableMapOf<String, Runnable>()

    private lateinit var prefs: Prefs
    private lateinit var vault: KeyVault
    private lateinit var engine: Engine
    private lateinit var profiles: Profiles
    private lateinit var media: MediaWatch
    private lateinit var link: LinkClient
    private lateinit var notifications: NotificationManager
    private lateinit var audio: AudioManager

    private var conn: LinkClient.Connection? = null
    private var session: Session? = null
    private var reader: FrameReader? = null
    private var peerName = ""
    private var inCall = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        notifications = getSystemService(NotificationManager::class.java)
        notifications.createNotificationChannel(NotificationChannel(CH_STATUS, "Status", NotificationManager.IMPORTANCE_MIN))
        notifications.createNotificationChannel(NotificationChannel(CH_PROMPT, "Switch prompts and pairing", NotificationManager.IMPORTANCE_HIGH))
        startForeground(N_STATUS, statusNotification("Starting…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        handler.post(::setUp)
    }

    private fun setUp() {
        Compat.init()
        prefs = Prefs(this)
        vault = KeyVault(this)
        audio = getSystemService(AudioManager::class.java)
        engine = Engine(prefs.settings())
        profiles = Profiles(this, handler) { h, here -> feed(Event.HeadsetLink(h, here)) }
        profiles.onReady = { profiles.watch(engine.settings.headsets) }
        profiles.start()
        media = MediaWatch(this, handler, ::onPlayback,
            onSeen = { app -> if (app !in prefs.seenApps) prefs.seenApps = prefs.seenApps + app },
            onLikelyStart = ::ping,
        )
        media.start()
        // Calls (cellular or VoIP) show up as an audio mode change; no phone-state permission needed. Android 12+
        // reports mode changes; before that the mode is checked whenever it matters (see onData).
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.S) {
            audio.addOnModeChangedListener({ r -> handler.post(r) }) { onAudioMode() }
        }
        link = LinkClient(
            onConnected = { c -> handler.post { onConnected(c) } },
            onData = { c, buf, n -> val copy = buf.copyOf(n); handler.post { onData(c, copy) } },
            onClosed = { c -> handler.post { onClosed(c) } },
        )
        connectLink()
        updateStatus()
        Diag.info("service: started, headset ${prefs.headset}, pc ${prefs.pc}")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val i = intent ?: return START_STICKY
        handler.post {
            if (!::engine.isInitialized) return@post
            when (i.action) {
                ACTION_RELOAD -> reload()
                ACTION_TOGGLE -> { prefs.enabled = !prefs.enabled; updateStatus() }
                ACTION_PAIR -> {
                    notifications.cancel(N_PAIR)
                    val s = session ?: return@post
                    conn?.let { apply(it, s, s.confirmPairing(i.getBooleanExtra(EXTRA_YES, false))) }
                }
                ACTION_ANSWER -> {
                    notifications.cancel(N_ASK)
                    feed(Event.Answer(i.getIntExtra(EXTRA_ID, -1), i.getBooleanExtra(EXTRA_YES, false)))
                }
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        handler.post {
            link.connectTo(null)
            conn?.close()
            media.stop()
            profiles.stop()
            thread.quitSafely()
        }
        super.onDestroy()
    }

    // ---- engine ----

    private fun now() = SystemClock.elapsedRealtime()

    private fun feed(e: Event) {
        val actions = engine.handle(e, now())
        Diag.info("> $e" + if (actions.isEmpty()) "" else "  < " + actions.joinToString(" | ", transform = Describe::of))
        actions.forEach(::execute)
        updateStatus()
    }

    private fun onPlayback(app: String, playing: Boolean) {
        if (playing) playingHere += app else playingHere -= app
        updateKeepAlive()
        if (playing && !prefs.enabled) return Diag.info("switching off: ignoring $app")
        feed(Event.Playback(app, playing))
    }

    // The link sleeps (Bluetooth sniff) after ~5-8 s of silence and then needs up to ~1.2 s to deliver a message
    // (docs/spikes/S7-link-latency.md). While something plays here a switch is likely, so keep the link awake.
    private val playingHere = mutableSetOf<String>()
    private var keepingAlive = false
    private val keepAlive = object : Runnable {
        override fun run() {
            ping()
            handler.postDelayed(this, KEEP_ALIVE_MS)
        }
    }

    private fun updateKeepAlive() {
        val want = playingHere.isNotEmpty() && session?.ready == true
        if (want == keepingAlive) return
        keepingAlive = want
        if (want) handler.postDelayed(keepAlive, KEEP_ALIVE_MS) else handler.removeCallbacks(keepAlive)
    }

    private fun ping() {
        val s = session?.takeIf { it.ready } ?: return
        conn?.send(s.ping())
    }

    private fun execute(a: Action) {
        when (a) {
            // "other" goes through the media keys where that is safe (see MediaWatch); apps that ignore them keep playing.
            is Action.Pause -> if (!media.pause(a.app) && a.app != MediaWatch.OTHER) Diag.warn("pause ${a.app}: no session")
            is Action.Resume -> if (!media.play(a.app) && a.app != MediaWatch.OTHER) Diag.warn("resume ${a.app}: no session")
            is Action.Release -> profiles.release(a.h)
            // A take refused outright is reported like its timeout, so paused media resumes straight away.
            is Action.Take -> if (!profiles.take(a.h)) handler.post { fireNow("take") }
            is Action.Send -> session?.takeIf { it.ready }?.let { s -> conn?.send(s.send(a.m)) }
            is Action.Ask -> prompt(
                N_ASK, "Switch headphones to this phone?",
                if (a.app == MediaWatch.OTHER) (if (BuildConfig.FULL) "Audio started on this phone." else "Media started playing on this phone.") else "${label(a.app)} started playing.",
                "Switch" to answer(a.id, true), "Not now" to answer(a.id, false),
            )
            is Action.StartTimer -> {
                timers.remove(a.name)?.let(handler::removeCallbacks)
                val r = Runnable { timers.remove(a.name); feed(Event.TimerFired(a.name)) }
                timers[a.name] = r
                handler.postDelayed(r, a.ms.toLong())
            }
            is Action.CancelTimer -> timers.remove(a.name)?.let(handler::removeCallbacks)
            is Action.Notify -> notifications.notify(N_NOTE, Notification.Builder(this, CH_PROMPT)
                .setSmallIcon(R.drawable.ic_stat).setContentTitle("StinePods").setContentText(a.text).setAutoCancel(true).build())
        }
    }

    /** Fires a pending engine timer early. */
    private fun fireNow(name: String) {
        val r = timers.remove(name) ?: return
        handler.removeCallbacks(r)
        feed(Event.TimerFired(name))
    }

    private fun onAudioMode() {
        val call = audio.mode == AudioManager.MODE_IN_CALL || audio.mode == AudioManager.MODE_IN_COMMUNICATION
        if (call == inCall) return
        inCall = call
        feed(Event.Call(call))
    }

    /** Settings changed in the UI: update the engine in place and tell the PC. */
    private fun reload() {
        val fresh = prefs.settings()
        engine.settings.headsets = fresh.headsets
        engine.settings.prefer = fresh.prefer
        engine.settings.returnOnStop = fresh.returnOnStop
        engine.settings.callsTakeHeadset = fresh.callsTakeHeadset
        engine.settings.apps = fresh.apps
        profiles.watch(fresh.headsets)
        media.start()
        session?.takeIf { it.ready }?.let { s ->
            conn?.send(s.send(Msg("config", headsets = fresh.headsets, prefer = fresh.prefer, returnOnStop = fresh.returnOnStop)))
        }
        connectLink()
        link.kick()
        updateStatus()
    }

    private fun connectLink() {
        val pc = prefs.pc
        link.connectTo(pc?.let { runCatching { profiles.device(it) }.getOrNull() })
    }

    // ---- PC link ----

    private fun onConnected(c: LinkClient.Connection) {
        conn?.close()
        conn = c
        reader = FrameReader()
        val name = profiles.adapter?.name ?: android.os.Build.MODEL
        val s = Session(Role.PHONE, prefs.deviceId, name, vault)
        session = s
        s.start().forEach(c::send)
    }

    private fun onData(c: LinkClient.Connection, data: ByteArray) {
        val s = session
        val r = reader
        if (c != conn || s == null || r == null) return
        val frames = try {
            r.push(data)
        } catch (e: java.io.IOException) {
            Diag.warn("link: malformed stream, closing")
            c.close()
            return
        }
        frames.forEach { apply(c, s, s.receive(it)) }
    }

    private fun apply(c: LinkClient.Connection, s: Session, step: Step) {
        step.out.forEach(c::send)
        for (e in step.events) when (e) {
            is LinkEvent.PairingCode -> prompt(
                N_PAIR, "Pair with ${e.peerName}?",
                "Check the PC shows the same code: %03d %03d".format(e.code / 1000, e.code % 1000),
                "Codes match" to pair(true), "Cancel" to pair(false),
            )
            is LinkEvent.Ready -> {
                peerName = e.peerName
                notifications.cancel(N_PAIR)
                Diag.info("link: ready with ${e.peerName}")
                feed(Event.PeerLink(true))
                updateKeepAlive()
                startLogForwarding(c, s)
            }
            is LinkEvent.Incoming -> {
                if (e.m.t == "claim") onAudioMode()   // Android 10-11: refresh call state before deciding
                feed(Event.Received(e.m))
            }
            is LinkEvent.Failed -> {
                Diag.warn("link: ${e.reason}")
                notifications.cancel(N_PAIR)
                c.close()
            }
        }
    }

    /**
     * Sends this phone's diagnostic log to the PC (which writes it to its own log), so phones without a USB debugging
     * connection can still be diagnosed. Recent history first, then live lines.
     */
    private fun startLogForwarding(c: LinkClient.Connection, s: Session) {
        val send: (String) -> Unit = { line ->
            handler.post { if (s == session && c == conn && s.ready) c.send(s.send(Msg("log", text = line))) }
        }
        Diag.recent(80).forEach(send)
        Diag.sink = send
    }

    private fun onClosed(c: LinkClient.Connection) {
        if (c != conn) return
        Diag.sink = null
        val wasReady = session?.ready == true
        conn = null
        session = null
        reader = null
        updateKeepAlive()
        if (wasReady) feed(Event.PeerLink(false)) else updateStatus()
    }

    // ---- notifications ----

    private fun updateStatus() {
        val pcText = if (session?.ready == true) "PC: $peerName" else if (prefs.pc == null) "no PC chosen" else "PC not connected"
        val h = prefs.headset
        val hsText = when {
            h == null -> "no headphones chosen"
            profiles.isHere(h) -> "headphones here"
            else -> "headphones elsewhere"
        }
        val text = (if (prefs.enabled) "" else "Paused · ") + "$pcText · $hsText"
        Diag.setStatus(text)
        notifications.notify(N_STATUS, statusNotification(text))
    }

    private fun statusNotification(text: String): Notification = Notification.Builder(this, CH_STATUS)
        .setSmallIcon(R.drawable.ic_stat)
        .setContentTitle("StinePods")
        .setContentText(text)
        .setOngoing(true)
        .setContentIntent(PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE))
        .build()

    private fun prompt(id: Int, title: String, text: String, vararg actions: Pair<String, PendingIntent>) {
        val b = Notification.Builder(this, CH_PROMPT)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(text)
            .setCategory(Notification.CATEGORY_STATUS)
            .setAutoCancel(true)
        actions.forEach { (label, pi) -> b.addAction(Notification.Action.Builder(null, label, pi).build()) }
        notifications.notify(id, b.build())
    }

    private fun pair(yes: Boolean) = command(ACTION_PAIR, if (yes) 1 else 2) { putExtra(EXTRA_YES, yes) }
    private fun answer(id: Int, yes: Boolean) = command(ACTION_ANSWER, 10 + id * 2 + if (yes) 1 else 0) {
        putExtra(EXTRA_ID, id); putExtra(EXTRA_YES, yes)
    }

    private fun command(action: String, requestCode: Int, extras: Intent.() -> Unit): PendingIntent =
        PendingIntent.getService(
            this, requestCode, Intent(this, StinePodsService::class.java).setAction(action).apply(extras),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

    private fun label(app: String) = runCatching {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(app, 0)).toString()
    }.getOrDefault(app)
}
