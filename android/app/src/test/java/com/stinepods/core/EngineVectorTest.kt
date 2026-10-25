package com.stinepods.core

import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.junit.runners.Parameterized

/** Runs every scenario in protocol/vectors/engine.txt against the Kotlin engine. */
@RunWith(Parameterized::class)
class EngineVectorTest(private val name: String) {
    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun scenarios() = Vectors.scenarios.keys.toList()
    }

    @Test
    fun scenario() {
        var engine: Engine? = null
        var now = 0L
        var actual: List<String>? = null
        val expected = mutableListOf<String>()
        var lastEvent: String? = null

        fun check() {
            val got = actual ?: return
            assertEquals("after '> $lastEvent'", expected.toList(), got)
            expected.clear()
            actual = null
        }

        for ((no, line) in Vectors.scenarios.getValue(name)) {
            when {
                line.startsWith("settings") -> engine = Engine(Vectors.parseSettings(line.substring(8)))
                line.startsWith("< ") -> expected += line.substring(2)
                line.startsWith("> ") -> {
                    check()
                    val ev = line.substring(2)
                    if (ev.startsWith("wait ")) {
                        now += ev.substring(5).toLong()
                    } else {
                        val e = engine ?: Engine(Settings(Role.PHONE)).also { engine = it }
                        lastEvent = ev
                        actual = e.handle(Vectors.parseEvent(ev), now).map(Describe::of)
                    }
                }
                else -> error("line $no: $line")
            }
        }
        check()
    }
}
