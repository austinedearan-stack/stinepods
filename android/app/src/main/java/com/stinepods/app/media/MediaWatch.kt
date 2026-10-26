package com.stinepods.app.media

import android.content.ComponentName
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.AudioPlaybackConfiguration
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.SystemClock
import android.view.KeyEvent
import com.stinepods.app.BuildConfig
import com.stinepods.app.Diag

/**
 * Local playback (docs/spikes/S3-S4-media-detection.md).
 *
 * - **Full build** with notification-listener access: an app counts as playing when its media session says PLAYING
 *   *and* something is actually producing media audio (filters out Spotify Connect sessions playing elsewhere); apps
 *   are paused/resumed through their sessions. Audio without a session is reported as [OTHER].
 * - **Lite build** (no notification-listener permission, so Play Protect's fraud protection doesn't block sideloading
 *   it) or no access granted: any sustained media audio is reported as [OTHER], and pause/resume go through the
 *   standard media keys, like a headset's play/pause button.
 *
 * Callbacks on [handler].
 */
class MediaWatch(
    private val context: Context,
    private val handler: Handler,
    private val onPlayback: (app: String, playing: Boolean) -> Unit,
    private val onSeen: (app: String) -> Unit,
    /** Media audio just started on the phone; fires ~100 ms before the session reports PLAYING. */
    private val onLikelyStart: () -> Unit = {},
) {
    private val sessions = context.getSystemService(MediaSessionManager::class.java)
    private val audio = context.getSystemService(AudioManager::class.java)
    // By name: the listener class only exists in the full build.
    private val component = ComponentName(context.packageName, "com.stinepods.app.MediaAccess")
    private val controllers = mutableMapOf<MediaSession.Token, MediaController>()
    private val reported = mutableMapOf<String, Boolean>()
    private var players = 0
    var running = false
        private set
    /** Media sessions are visible (full build with access granted); otherwise everything is [OTHER]. */
    var sessionsVisible = false
        private set

    fun start() {
        if (!running) {
            audio.registerAudioPlaybackCallback(playbackCallback, handler)
            players = countPlayers(audio.activePlaybackConfigurations)
            running = true
        }
        if (BuildConfig.FULL && !sessionsVisible) {
            try {
                sessions.addOnActiveSessionsChangedListener(sessionsChanged, component, handler)
                sessionsVisible = true
                onSessions(sessions.getActiveSessions(component))
            } catch (e: SecurityException) {
                Diag.warn("media: no notification access yet; using media keys")
            }
        }
        recompute()
    }

    fun stop() {
        if (!running) return
        if (sessionsVisible) sessions.removeOnActiveSessionsChangedListener(sessionsChanged)
        audio.unregisterAudioPlaybackCallback(playbackCallback)
        controllers.values.forEach { it.unregisterCallback(controllerCallback) }
        controllers.clear()
        running = false
        sessionsVisible = false
    }

    /** Pauses an app; [OTHER] (or a lite build) uses the media key, which reaches whatever is playing. */
    fun pause(app: String) = control(app, KeyEvent.KEYCODE_MEDIA_PAUSE) { it.transportControls.pause() }
    fun play(app: String) = control(app, KeyEvent.KEYCODE_MEDIA_PLAY) { it.transportControls.play() }

    private fun control(app: String, key: Int, op: (MediaController) -> Unit): Boolean {
        val c = controllers.values.firstOrNull { it.packageName == app }
        if (c != null) {
            op(c)
            return true
        }
        // With sessions visible, OTHER is audio from an app without a session: a media key would reach some other
        // (paused) session instead, e.g. start Spotify. Without sessions, the media key is the right tool.
        if (app != OTHER || sessionsVisible) return false
        // The app ignored our pause and is still playing: a play key now would go to the last session (e.g. Spotify).
        if (key == KeyEvent.KEYCODE_MEDIA_PLAY && players > 0) return true
        audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, key))
        audio.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_UP, key))
        return true
    }

    private val sessionsChanged = MediaSessionManager.OnActiveSessionsChangedListener { onSessions(it.orEmpty()) }

    private fun onSessions(list: List<MediaController>) {
        val live = list.map { it.sessionToken }.toSet()
        controllers.keys.filter { it !in live }.forEach { controllers.remove(it)?.unregisterCallback(controllerCallback) }
        for (c in list) {
            if (controllers.containsKey(c.sessionToken)) continue
            controllers[c.sessionToken] = c
            c.registerCallback(controllerCallback, handler)
            onSeen(c.packageName)
        }
        recompute()
    }

    private val controllerCallback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) = recompute()
        override fun onSessionDestroyed() = recompute()
    }

    private val playbackCallback = object : AudioManager.AudioPlaybackCallback() {
        override fun onPlaybackConfigChanged(configs: MutableList<AudioPlaybackConfiguration>) {
            val before = players
            players = countPlayers(configs)
            if (before == 0 && players > 0) onLikelyStart()
            recompute()
        }
    }

    private fun countPlayers(configs: List<AudioPlaybackConfiguration>) = configs.count {
        it.audioAttributes.usage in MEDIA_USAGES
    }

    /** Derives each app's playing state and reports changes only (apps like Spotify repeat states many times a second). */
    private fun recompute() {
        val playingNow = controllers.values
            .filter { players > 0 && it.playbackState?.state == PlaybackState.STATE_PLAYING }
            .map { it.packageName }
            .toMutableSet()

        // Media audio with no session saying PLAYING: an app without media controls (Instagram, gallery videos,
        // games). Android hides which app it is. Wait a moment, because session apps start their audio ~100 ms
        // before reporting PLAYING, and short sounds shouldn't count.
        val unattributed = players > 0 && playingNow.isEmpty()
        if (!unattributed) {
            handler.removeCallbacks(otherConfirm)
            otherSince = 0
        } else if (otherSince == 0L) {
            otherSince = SystemClock.elapsedRealtime()
            handler.postDelayed(otherConfirm, OTHER_CONFIRM_MS)
        }
        if (unattributed && otherSince != 0L && SystemClock.elapsedRealtime() - otherSince >= OTHER_CONFIRM_MS) {
            playingNow += OTHER
        }

        val known = reported.keys + playingNow
        for (app in known) {
            val p = app in playingNow
            if (reported[app] == p) continue
            reported[app] = p
            onPlayback(app, p)
        }
    }

    private val otherConfirm = Runnable { recompute() }
    private var otherSince = 0L

    companion object {
        /** Engine app id for media audio not attributed to a media session (every app in the lite build). */
        const val OTHER = "other"
        private const val OTHER_CONFIRM_MS = 1000L
        private val MEDIA_USAGES = setOf(AudioAttributes.USAGE_MEDIA, AudioAttributes.USAGE_GAME, AudioAttributes.USAGE_UNKNOWN)
    }
}
