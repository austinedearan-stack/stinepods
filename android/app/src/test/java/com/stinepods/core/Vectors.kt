package com.stinepods.core

import java.io.File

/** Reads protocol/vectors (shared with the C# tests). The repo root comes from the "stinepods.root" property. */
object Vectors {
    private val root = File(System.getProperty("stinepods.root") ?: "../..")

    fun file(name: String) = File(root, "protocol/vectors/$name")

    val scenarios: Map<String, List<Pair<Int, String>>> by lazy {
        val result = linkedMapOf<String, MutableList<Pair<Int, String>>>()
        var current: MutableList<Pair<Int, String>>? = null
        file("engine.txt").readLines().forEachIndexed { i, raw ->
            val line = raw.trim()
            when {
                line.isEmpty() || line.startsWith("#") -> Unit
                line.startsWith("scenario ") -> current = mutableListOf<Pair<Int, String>>().also { result[line.substring(9)] = it }
                line == "end" -> current = null
                else -> (current ?: error("line ${i + 1} outside a scenario")) += (i + 1) to line
            }
        }
        result
    }

    fun parseSettings(text: String): Settings {
        val kv = text.trim().split(" ").filter { it.isNotEmpty() }.associate { it.substringBefore('=') to it.substringAfter('=') }
        val s = Settings(role = if (kv["role"] == "pc") Role.PC else Role.PHONE)
        kv["headsets"]?.let { s.headsets = it.split(",") }
        kv["prefer"]?.let { s.prefer = Prefer.valueOf(it.uppercase()) }
        kv["return"]?.let { s.returnOnStop = ReturnOnStop.valueOf(it.uppercase()) }
        kv["calls"]?.let { s.callsTakeHeadset = it == "on" }
        kv["apps"]?.let { apps -> s.apps = apps.split(",").associate { it.substringBefore(':') to AppRule.valueOf(it.substringAfter(':').uppercase()) } }
        return s
    }

    fun parseEvent(text: String): Event {
        val w = text.split(" ")
        return when (w[0]) {
            "peer" -> Event.PeerLink(w[1] == "up")
            "headset" -> Event.HeadsetLink(w[1], w[2] == "here")
            "play" -> Event.Playback(w[1], true)
            "stop" -> Event.Playback(w[1], false)
            "answer" -> Event.Answer(w[1].toInt(), w[2] == "yes")
            "timer" -> Event.TimerFired(w[1])
            "call" -> Event.Call(w[1] == "on")
            "recv" -> Event.Received(parseMsg(w.drop(1)))
            else -> error("bad event: $text")
        }
    }

    private fun parseMsg(w: List<String>): Msg = when (w[0]) {
        "claim" -> Msg("claim", w[1], w[2].toInt())
        "released" -> Msg("released", w[1], w[2].takeIf { it != "-" }?.toInt(), w[3])
        "deny" -> Msg("deny", w[1], w[2].toInt(), w[3])
        "state" -> Msg("state", w[1], here = w[2] == "here")
        "config" -> Msg(
            "config",
            headsets = w[1].split(","),
            prefer = Prefer.valueOf(w[2].removePrefix("prefer=").uppercase()),
            returnOnStop = ReturnOnStop.valueOf(w[3].removePrefix("return=").uppercase()),
        )
        else -> error("bad message: $w")
    }
}
