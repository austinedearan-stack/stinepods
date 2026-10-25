package com.stinepods.core

/** One-line text form of actions, matching the "<" lines of protocol/vectors. Also used for logs. */
object Describe {
    fun of(a: Action): String = when (a) {
        is Action.Pause -> "pause ${a.app}"
        is Action.Resume -> "resume ${a.app}"
        is Action.Release -> "release ${a.h}"
        is Action.Take -> "take ${a.h}"
        is Action.Send -> "send " + of(a.m)
        is Action.Ask -> "ask ${a.id} ${a.app}"
        is Action.StartTimer -> "timer ${a.name} ${a.ms}"
        is Action.CancelTimer -> "cancel ${a.name}"
        is Action.Notify -> "notify ${a.text}"
    }

    fun of(m: Msg): String = when (m.t) {
        "config" -> "config ${m.headsets.orEmpty().joinToString(",")} prefer=${name(m.prefer)} return=${name(m.returnOnStop)}"
        "claim" -> "claim ${m.h} ${m.id}"
        "released" -> "released ${m.h} ${m.id ?: "-"} ${m.why}"
        "deny" -> "deny ${m.h} ${m.id} ${m.why}"
        "state" -> "state ${m.h} ${if (m.here == true) "here" else "away"}"
        else -> m.t
    }

    fun name(e: Enum<*>?) = e?.name?.lowercase() ?: "-"
}
