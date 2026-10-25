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
import com.stinepods.app.Diag
import com.stinepods.app.MediaAccess

/**
 * Local playback (docs/spikes/S3-S4-media-detection.md). An app counts as playing when its media session says
 * PLAYING *and* something is actually producing media audio on the phone, which filters out Spotify Connect sessions
 * that are playing on another device. Needs notification-listener access ([MediaAccess]). Callbacks on [handler].
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
    private val component = ComponentName(context, MediaAccess::class.java)
    private val controllers = mutableMapOf<MediaSession.Token, MediaController>()
    private val reported = mutableMapOf<String, Boolean>()
    private var players = 0
    var running = false
        private set

    /** Returns false when notification-listener access hasn't been granted yet. */
    fun start(): Boolean {
        if (running) return true
        try {
            sessions.addOnActiveSessionsChangedListener(sessionsChanged, component, handler)
            onSessions(sessions.getActiveSessions(component))
        } catch (e: SecurityException) {
            Diag.warn("media: no notification access yet")
            return false
        }
        audio.registerAudioPlaybackCallback(playbackCallback, handler)
        players = countPlayers(audio.activePlaybackConfigurations)
        running = true
        recompute()
        return true
    }

    fun stop() {
        if (!running) return
        sessions.removeOnActiveSessionsChangedListener(sessionsChanged)
        audio.unregisterAudioPlaybackCallback(playbackCallback)
        controllers.values.forEach { it.unregisterCallback(controllerCallback) }
        controllers.clear()
        running = false
    }

    fun pause(app: String) = control(app) { it.transportControls.pause() }
    fun play(app: String) = control(app) { it.transportControls.play() }

    private fun control(app: String, op: (MediaController) -> Unit): Boolean {
        val c = controllers.values.firstOrNull { it.packageName == app } ?: return false
        op(c)
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
            otherSince = android.os.SystemClock.elapsedRealtime()
            handler.postDelayed(otherConfirm, OTHER_CONFIRM_MS)
        }
        if (unattributed && otherSince != 0L && android.os.SystemClock.elapsedRealtime() - otherSince >= OTHER_CONFIRM_MS) {
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
        /** Engine app id for media audio from apps without a media session. */
        const val OTHER = "other"
        private const val OTHER_CONFIRM_MS = 1000L
        private val MEDIA_USAGES = setOf(AudioAttributes.USAGE_MEDIA, AudioAttributes.USAGE_GAME, AudioAttributes.USAGE_UNKNOWN)
    }
}
