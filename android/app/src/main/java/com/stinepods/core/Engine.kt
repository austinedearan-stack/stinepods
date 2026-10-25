package com.stinepods.core

// Kotlin port of desktop/StinePods.Core/Engine.cs. Both are specified by protocol/vectors/engine.txt; keep them in step.

enum class Role { PHONE, PC }
enum class AppRule { AUTO, ASK, IGNORE }
enum class Prefer { PHONE, PC, NONE }
enum class ReturnOnStop { STAY, RETURN }

class Settings(
    val role: Role,
    var headsets: List<String> = emptyList(),
    var prefer: Prefer = Prefer.PHONE,
    var returnOnStop: ReturnOnStop = ReturnOnStop.STAY,
    var callsTakeHeadset: Boolean = false,
    var apps: Map<String, AppRule> = emptyMap(),
) {
    fun ruleFor(app: String) = apps[app] ?: AppRule.AUTO
}

/** A link-protocol message (see protocol/PROTOCOL.md). Only the fields a type uses are set. */
data class Msg(
    val t: String,
    val h: String? = null,
    val id: Int? = null,
    val why: String? = null,
    /** `state`: whether the headset is connected to the sender. */
    val here: Boolean? = null,
    val headsets: List<String>? = null,
    val prefer: Prefer? = null,
    val returnOnStop: ReturnOnStop? = null,
)

sealed interface Event {
    data class PeerLink(val up: Boolean) : Event
    /** The headset's link to *this* device came up or went down. */
    data class HeadsetLink(val h: String, val here: Boolean) : Event
    /** Audible local playback of an app started or stopped. */
    data class Playback(val app: String, val playing: Boolean) : Event
    data class Received(val m: Msg) : Event
    data class Answer(val askId: Int, val yes: Boolean) : Event
    data class TimerFired(val name: String) : Event
    data class Call(val active: Boolean) : Event
}

sealed interface Action {
    data class Pause(val app: String) : Action
    data class Resume(val app: String) : Action
    data class Release(val h: String) : Action
    data class Take(val h: String) : Action
    data class Send(val m: Msg) : Action
    data class Ask(val id: Int, val app: String) : Action
    data class StartTimer(val name: String, val ms: Int) : Action
    data class CancelTimer(val name: String) : Action
    data class Notify(val text: String) : Action
}

/**
 * The hand-off coordinator. Pure and single-threaded: feed it events with the current time, perform the actions it
 * returns. Platform code owns all I/O, timers and threading.
 */
class Engine(val settings: Settings) {
    companion object {
        const val CLAIM_TIMEOUT_MS = 2500
        const val TAKE_TIMEOUT_MS = 8000
        const val RELEASE_TIMEOUT_MS = 4000
        const val ASK_TIMEOUT_MS = 15000
        const val RETURN_DELAY_MS = 10000
        const val COOLDOWN_MS = 3000
        const val EXPECT_WINDOW_MS = 3000
    }

    private enum class Phase { ASK_WAIT, AWAIT_RELEASE, AWAIT_CONNECT }

    private class Switch(
        val h: String,
        var phase: Phase,
        val app: String? = null,     // paused here, resumed once the headset is connected
        var claimId: Int? = null,
        val askId: Int = 0,
        var viaPeer: Boolean = false, // taken from the peer (enables return-on-stop)
    )

    private data class Releasing(val h: String, val id: Int?, val why: String)
    private data class Expect(val playing: Boolean, val until: Long)

    private val here = mutableSetOf<String>()
    private val peerHas = mutableSetOf<String>()   // headsets the peer says are connected to it (dual-connection headsets)
    private val playing = mutableSetOf<String>()
    private val expect = mutableMapOf<String, Expect>()
    private var peerUp = false
    private var callActive = false
    private var returnArmed = false
    private var pending: Switch? = null
    private var releasing: Releasing? = null
    private var pausedForPeer: Pair<String, String>? = null   // (app, headset)
    private var takenFrom: String? = null
    private var cooldownUntil = 0L
    private var nextClaimId = 0
    private var nextAskId = 0

    val isPeerUp get() = peerUp
    val busy get() = pending != null
    fun isHere(h: String) = h in here

    private val headset get() = settings.headsets.firstOrNull()

    fun handle(e: Event, now: Long): List<Action> {
        val a = mutableListOf<Action>()
        when (e) {
            is Event.PeerLink -> onPeerLink(e.up, a)
            is Event.HeadsetLink -> onHeadsetLink(e.h, e.here, now, a)
            is Event.Playback -> if (e.playing) onPlay(e.app, now, a) else onStop(e.app, now, a)
            is Event.Received -> onMessage(e.m, now, a)
            is Event.Answer -> onAnswer(e.askId, e.yes, now, a)
            is Event.TimerFired -> onTimer(e.name, now, a)
            is Event.Call -> onCall(e.active, a)
        }
        return a
    }

    private fun onPeerLink(up: Boolean, a: MutableList<Action>) {
        peerUp = up
        if (up) {
            if (settings.role == Role.PHONE) {
                a += Action.Send(Msg("config", headsets = settings.headsets.toList(), prefer = settings.prefer, returnOnStop = settings.returnOnStop))
                sendStates(a)
            }
            return
        }
        peerHas.clear()
        // Nobody left to answer a claim: take the headset on our own.
        val p = pending
        if (p != null && p.phase == Phase.AWAIT_RELEASE) {
            a += Action.CancelTimer("claim")
            beginTake(p, a)
        }
        releasing = null
    }

    private fun onPlay(app: String, now: Long, a: MutableList<Action>) {
        if (expected(app, true, now)) return
        playing += app
        disarmReturn(a)
        val h = headset ?: return
        // Already connected here does nothing, unless the peer holds a second connection to the same headset:
        // then its stream may be the one being heard, so ask it to let go.
        if ((h in here && h !in peerHas) || pending != null || now < cooldownUntil) return
        val rule = settings.ruleFor(app)
        if (rule == AppRule.IGNORE) return

        // Pause straight away so nothing is missed while the headset moves; resumed once it's connected here.
        pauseApp(app, now, a)
        if (rule == AppRule.ASK) {
            val s = Switch(h, Phase.ASK_WAIT, app, askId = ++nextAskId)
            pending = s
            a += Action.Ask(s.askId, app)
            a += Action.StartTimer("ask", ASK_TIMEOUT_MS)
            return
        }
        beginClaim(Switch(h, Phase.AWAIT_RELEASE, app), a)
    }

    private fun onStop(app: String, now: Long, a: MutableList<Action>) {
        if (expected(app, false, now)) return
        playing -= app
        val h = takenFrom
        if (settings.returnOnStop == ReturnOnStop.RETURN && playing.isEmpty() && peerUp &&
            h != null && h in here && pending == null
        ) {
            a += Action.StartTimer("return", RETURN_DELAY_MS)
            returnArmed = true
        }
    }

    private fun disarmReturn(a: MutableList<Action>) {
        if (!returnArmed) return
        a += Action.CancelTimer("return")
        returnArmed = false
    }

    private fun onAnswer(askId: Int, yes: Boolean, now: Long, a: MutableList<Action>) {
        val p = pending
        if (p == null || p.phase != Phase.ASK_WAIT || p.askId != askId) return
        a += Action.CancelTimer("ask")
        if (yes) {
            p.phase = Phase.AWAIT_RELEASE
            beginClaim(p, a)
        } else {
            pending = null
            p.app?.let { resumeApp(it, now, a) }
        }
    }

    private fun onCall(active: Boolean, a: MutableList<Action>) {
        callActive = active
        val h = headset
        if (active && settings.role == Role.PHONE && settings.callsTakeHeadset && h != null && h !in here && pending == null) {
            beginClaim(Switch(h, Phase.AWAIT_RELEASE), a)
        }
    }

    private fun beginClaim(s: Switch, a: MutableList<Action>) {
        pending = s
        if (!peerUp) {
            beginTake(s, a)
            return
        }
        s.phase = Phase.AWAIT_RELEASE
        s.claimId = ++nextClaimId
        s.viaPeer = true
        a += Action.Send(Msg("claim", s.h, s.claimId))
        a += Action.StartTimer("claim", CLAIM_TIMEOUT_MS)
    }

    private fun beginTake(s: Switch, a: MutableList<Action>) {
        pending = s
        s.phase = Phase.AWAIT_CONNECT
        a += Action.Take(s.h)
        a += Action.StartTimer("take", TAKE_TIMEOUT_MS)
    }

    private fun onMessage(m: Msg, now: Long, a: MutableList<Action>) {
        when (m.t) {
            "config" -> if (settings.role == Role.PC) {
                m.headsets?.let { settings.headsets = it.toList() }
                m.prefer?.let { settings.prefer = it }
                m.returnOnStop?.let { settings.returnOnStop = it }
                sendStates(a)
            }
            "state" -> {
                val h = m.h
                val isHere = m.here
                if (h != null && isHere != null) if (isHere) peerHas += h else peerHas -= h
            }
            "claim" -> m.h?.let { onClaim(it, m.id, now, a) }
            "released" -> m.h?.let { onReleased(it, m.why, now) }?.let(a::addAll)
            "deny" -> {
                val p = pending
                if (p != null && p.phase == Phase.AWAIT_RELEASE && p.h == m.h && p.claimId == m.id) {
                    a += Action.CancelTimer("claim")
                    a += Action.Notify(if (m.why == "call") "Headset is busy: phone call in progress" else "Headset switch refused")
                    pending = null
                }
            }
        }
    }

    private fun onClaim(h: String, id: Int?, now: Long, a: MutableList<Action>) {
        if (callActive && settings.role == Role.PHONE) {
            a += Action.Send(Msg("deny", h, id, "call"))
            return
        }
        val p = pending
        if (p != null && p.h == h) {
            // Both sides claimed at once: the phone wins, the PC yields (its app stays paused).
            if (settings.role == Role.PHONE && p.phase == Phase.AWAIT_RELEASE) return
            a += Action.CancelTimer(
                when (p.phase) {
                    Phase.ASK_WAIT -> "ask"
                    Phase.AWAIT_RELEASE -> "claim"
                    Phase.AWAIT_CONNECT -> "take"
                },
            )
            pending = null
        }
        for (app in playing.toList()) {
            pauseApp(app, now, a)
            pausedForPeer = app to h
        }
        playing.clear()
        takenFrom = null
        disarmReturn(a)
        if (h in here) {
            releasing = Releasing(h, id, "claim")
            a += Action.Release(h)
            a += Action.StartTimer("release", RELEASE_TIMEOUT_MS)
        } else {
            a += Action.Send(Msg("released", h, id, "claim"))
        }
    }

    private fun onReleased(h: String, why: String?, now: Long): List<Action> {
        val a = mutableListOf<Action>()
        val p = pending
        if (p != null && p.phase == Phase.AWAIT_RELEASE && p.h == h) {
            a += Action.CancelTimer("claim")
            takeOrComplete(p, now, a)
            return a
        }
        if (p != null || h in here) return a
        val mine = if (settings.role == Role.PHONE) Prefer.PHONE else Prefer.PC
        if (why == "return" || (why == "prefer" && settings.prefer == mine)) {
            val pf = pausedForPeer
            val app = if (why == "return" && pf != null && pf.second == h) pf.first else null
            pausedForPeer = null
            beginTake(Switch(h, Phase.AWAIT_CONNECT, app), a)
        }
        return a
    }

    private fun onHeadsetLink(h: String, isHere: Boolean, now: Long, a: MutableList<Action>) {
        if (peerUp && h in settings.headsets) a += Action.Send(Msg("state", h, here = isHere))
        if (!isHere) {
            here -= h
            val r = releasing
            if (r != null && r.h == h) {
                a += Action.CancelTimer("release")
                if (peerUp) a += Action.Send(Msg("released", h, r.id, r.why))
                releasing = null
                // Apps may auto-resume on the local speaker when the headset leaves; don't claim it straight back.
                cooldownUntil = now + COOLDOWN_MS
            }
            if (takenFrom == h) takenFrom = null
            return
        }

        here += h
        val p = pending
        if (p != null && p.phase == Phase.AWAIT_CONNECT && p.h == h) {
            a += Action.CancelTimer("take")
            complete(p, now, a)
            return
        }
        if (p != null || releasing != null) return

        // Picked up on its own (e.g. headset just powered on): hand it to the preferred device if we're idle.
        val other = if (settings.role == Role.PHONE) Prefer.PC else Prefer.PHONE
        if (settings.prefer == other && peerUp && playing.isEmpty() && h in settings.headsets) {
            releasing = Releasing(h, null, "prefer")
            a += Action.Release(h)
            a += Action.StartTimer("release", RELEASE_TIMEOUT_MS)
        }
    }

    private fun onTimer(name: String, now: Long, a: MutableList<Action>) {
        val p = pending
        when (name) {
            "claim" -> if (p != null && p.phase == Phase.AWAIT_RELEASE) takeOrComplete(p, now, a)   // peer didn't answer
            "take" -> if (p != null && p.phase == Phase.AWAIT_CONNECT) {
                a += Action.Notify("Couldn't connect the headset")
                pending = null
                // Don't leave the user's media paused: let it carry on where it is (speaker or wherever).
                p.app?.let { resumeApp(it, now, a) }
            }
            "ask" -> if (p != null && p.phase == Phase.ASK_WAIT) {
                pending = null
                p.app?.let { resumeApp(it, now, a) }
            }
            "release" -> releasing?.let { r ->
                if (peerUp) a += Action.Send(Msg("released", r.h, r.id, r.why))
                releasing = null
            }
            "return" -> {
                returnArmed = false
                val h = takenFrom ?: return
                if (h !in here || playing.isNotEmpty() || p != null || !peerUp) return
                releasing = Releasing(h, null, "return")
                takenFrom = null
                a += Action.Release(h)
                a += Action.StartTimer("release", RELEASE_TIMEOUT_MS)
            }
        }
    }

    /** The peer let go: connect, or if we already hold a connection (dual-connection headset) we're done. */
    private fun takeOrComplete(p: Switch, now: Long, a: MutableList<Action>) {
        if (p.h in here) complete(p, now, a) else beginTake(p, a)
    }

    private fun complete(p: Switch, now: Long, a: MutableList<Action>) {
        p.app?.let { resumeApp(it, now, a) }
        takenFrom = if (p.viaPeer) p.h else null
        cooldownUntil = now + COOLDOWN_MS
        pending = null
    }

    private fun sendStates(a: MutableList<Action>) {
        for (h in settings.headsets) a += Action.Send(Msg("state", h, here = h in here))
    }

    private fun pauseApp(app: String, now: Long, a: MutableList<Action>) {
        a += Action.Pause(app)
        expect[app] = Expect(false, now + EXPECT_WINDOW_MS)
        playing -= app
    }

    private fun resumeApp(app: String, now: Long, a: MutableList<Action>) {
        a += Action.Resume(app)
        expect[app] = Expect(true, now + EXPECT_WINDOW_MS)
        playing += app
    }

    /** Swallows the playback event our own pause/resume command caused, so it can't trigger a switch. */
    private fun expected(app: String, isPlaying: Boolean, now: Long): Boolean {
        val x = expect[app] ?: return false
        if (now > x.until) {
            expect -= app
            return false
        }
        if (x.playing != isPlaying) return false
        expect -= app
        return true
    }
}
